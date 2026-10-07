# ValleyServer

从 [Lixeer/ValleyServer](https://github.com/Lixeer/ValleyServer) fork 出来的仓库。main 分支目前领先上游 10 个提交（合计 12 个文件、+2690/-87），内容不是新功能，而是 10 月 6-7 日把 3.x 无头核心在 Windows 上架起来、用真实 1.6.15 客户端连进去玩了两天之后，实打实撞出来的一批问题的修复。

这份 README 就是写给上游的问题汇报，尽量完整：已修的给根因和修法，没修的也如实列出。上游 docs/v3/README.md 里提到过"正在编写用于分析星露谷源码、整理协议文档的 agent"——这一轮就是它的第一份产出：逐文件代码审计、反编译 Stardew Valley.dll（1.6.15.24356）对照原版逻辑，再用真实客户端连服实测验证。除标注"未修/待复测"的条目，所有修复都重新部署、重启、真机验证过。

文中源码行号以审计基线为准（上游 main：Program.cs 1234 行，MD5 6008F83F202BF2A59ADA2782B8BB16B3），反编译行号对应 1.6.15.24356。

## 一、世界进度不落盘

原版 3.x 的世界只存在于内存里：农手 XML（saved_farmhands）有落盘，但地形、作物、建筑、钱包、日期统统不写盘。stop 或重启，世界直接回 day 1，玩家一整天的进度蒸发。

修复（b547207 + 542997a）：

- 日结走 vanilla 的 SaveGame 保存枚举器，把世界写进真实存档槽；重启时按 farm name 找最新槽恢复，恢复前列出全部候选槽（防止同名新档静默遮蔽旧档，这类事故一旦发生很难察觉）；
- 关服（stop 命令 / Ctrl+C / 关窗口）先存世界再关 socket；
- 新增 `save` 控制台命令，不过夜也能存；
- 保存失败不再向客户端报成功，日志会明确打出失败。

配套的坏档防护是吃过亏才加上的：

- 槽位存在但读不出来：默认拒绝启动（exit 1），磁盘上一个字节都不动。配置 `World.StartFreshWhenSaveUnreadable=true` 才会放弃旧档，且放弃前先把槽位改名为 `<slot>_unloadable_<时间戳>`（改名，绝不删除）；
- 载入管线中途失败：即使开了上面的 opt-in 也拒绝启动。半应用的世界如果继续跑，日结会带着旧档的 farm name 和 unique id 把真档覆盖掉——开发期间真的发生过一次，一份 87 个 location 的存档被写成了两倍大的坏档。

另外给日结加了看门狗（542997a）：vanilla 的 NetSynchronizer barrier 没有超时，任何一个无应答农手就能把日结连同当天的保存一起永久卡死，而 `shouldAbort()` 只看 `Game1.client.timedOut`，在服务器上它永远是 null。现在主循环 15 秒告警、120 秒把无应答农手标记进 `disconnectingFarmers`——不敢直接碰 `otherFarmers`，那是日结 worker 正在遍历的集合。

## 二、真实客户端在线时的日结竞态

单人自测一切正常，客户端一连上问题就全来了：

- **B4**：主循环随时从 `Game1.otherFarmers` 增删农手，日结 worker 正在 barrier 里遍历同一个集合，"Collection was modified" 直接打断日结，当天的保存一起丢。修法：断线只标记 `disconnectingFarmers`，新农手注册进 pending 队列，日结结束后再放行。
- **B3/bB1**：`clientConnections` 是普通 Dictionary，主循环和 worker 并发读写；worker 查不到映射时，客户端会在屏障上永久等待。改 `ConcurrentDictionary`。
- **D1**（这条查得最久）：`NetDictionary` 的待发变更排在私有 `outgoingChanges` 列表里，`Write` 先枚举它、再走一个对同一列表的惰性 `.Where(Removal)` 枚举，唯一清空它的是 `CleanImpl()`（NetDictionary.cs:693-697）。写入中途一旦有重入的 Clean，枚举就炸 "Collection was modified"（NetDictionary.cs:669），而且每次强制世界状态写入都会复现。修法：启动期用反射沿 BaseType 链找到这个私有字段整批丢弃（`Type.GetField` 不返回基类声明的私有字段，这个坑值得记）。只在启动期做是安全的——那时还没有任何客户端收到过这些条目。
- **D2**：`NetEvent` 同款问题换了字段（如 `removeTemporarySpritesWithIDEvent`），有客户端在场时 `broadcastLocationDelta` 撞上另一线程的 `Fire()`，同样的炸法（NetEvent.cs:108-121 的 foreach）。修法：日结前排空各地点的事件待发队列——`WriteFull` 本来就是空的，丢掉的只有瞬态动画事件。
- **D3**：日结期间农手完成捏脸会立刻在主线程写盘并发种子，和 worker 的序列化竞争。改挂 pending，日结结束后统一补。
- **B8**：日结期间延迟的 14/31 号应答，日结结束后无人排空，第 N 天的应答可能漏进第 N+1 天的同步。现在日结结束后整批丢弃并计数告警。
- **E1**：聊天 15 号消息在服务器端走 `ChatBox.receiveChatMessage`，无 UI 的 headless 进程直接 NRE。改为纯转发，服务器不渲染聊天。
- **E2**：vanilla 自带的 `DedicatedServer.HostSleepInBed()`（DedicatedServer.cs:503）会构造 `ReadyCheckDialog`，headless 下必 NRE，而外层 catch 会把这一拍的全部同步吞掉。单独包 try/catch 加一次性告警，睡眠路径不依赖这个对话框。

其中 B3/B4/D1/D3/B8 随审计修复入 542997a，D2/E1/E2 入 2ff515b。修完之后真实客户端连服自然睡觉，一口气从 day 3 连续滚到 day 12，再没出现日结中断。

## 三、睡觉卡"等待其他玩家 1/1"

用户实测：睡觉时显示"请等待其他玩家…1/1"，过不了夜，重睡一次才能到下一天。根因有三层，对应睡觉三修（de621da）：

- **F1**：host 补票后下一拍就直接 `Game1.NewDay()`，不等 `netReady.IsReady("sleep")` 的全局确认，客户端可能还挂在 ReadyCheckDialog 就收到了日结开始。加 ready 门。
- **F2**：mock 的 SpriteFont 是未初始化对象，`ReadyCheckDialog` 一画就崩。换成惰性但真正可用的字体对象（`MeasureString` 可用）。
- **F3**：F2 修好之后 vanilla 会把 `ReadyCheckDialog` 挂在 `activeClickableMenu` 上，headless 没人跑菜单 update，对话框就永远挂着。在日结触发块里模仿 vanilla 的 closeDialog 清掉。

顺带说明：客户端看到 "1/1" 而服务器日志是 ready=2/2，是 vanilla 的设计——`ServerReadyCheck` 特意对客户端隐藏隐形主机（发 UpdateAmounts 时减一），不是 bug，不用去"修"它。

## 四、节日（Luau 实测）

现象：夏 11 Luau 当天，要么卡在"等待所有玩家 1/1"，要么到点了 Beach 上什么都没有——没有节日场地、没有 NPC，主机也没被送进去。

根因是一整串，每一环都够它死一次：

1. headless 没有十分钟时钟，`whereIsTodaysFest` 只在 `performTenMinuteClockUpdate` 里学习，永远是 null，而 vanilla 的节日窗口检查全靠它；
2. 就算知道了，主机 warp 用的 `Game1.screenFade` 是我们自己的惰性实现，`onFadeToBlackComplete` 回调被吞——warp 实际从未完成，主机一直站在原地；
3. 就算 warp 开始走了，`FarmHouse.checkForEvents` 里的 `Farmer.hasPet()`（Farmer.cs:5744）会 `RequireLocation(homeLocation)`，而农手存档的 homeLocation 指向一个早已不存在的小屋 GUID（小屋 uniqueName 带一次性 GUID，成因没细究），KeyNotFoundException 把 warp 半路打断，主机滞留 FarmHouse。

修复（e40572c + 7649539）：

- `EnsureHeadlessFestivalKnowledge`：weatherIcon==1 或强排日时学习 whereIsTodaysFest，日历兜底（存档重载后 weatherIcon 不一定可靠）；
- `PumpHeadlessFestival`：窗口没开 hold、窗口开始送主机进场、窗口关了就不再出手（每天只告警一次）；
- `ScreenFade` 换成会把 fade 回调转发回 vanilla 的实现，warp 真正走完；
- `RepairFarmerHomeLocations`：世界载入后和农手连入注册后各跑一遍，把悬空 homeLocation 指回 farmhandReference 对得上的小屋。vanilla 载入管线其实会自己修好世界档里的这部分，但 saved_farmhands 里的农手 XML 还是旧值，断线重连时客户端会带回来，所以连入路径必须修；
- 新增 `homes` 诊断命令：列出全部 FarmHouse/Cabin 和各来源农手的 homeLocation 解析结果，这个问题就是靠它定位的。

还差的：真实节日日的端到端复测（set-up 事件是否建立、节日流程能否走完）。另外 **B14 还没修**：vanilla `GameServer` 处理 warp 消息（case 5）时，在可选的 `warpFarmer(...)` 之后会**无条件**调 `Game1.dedicatedServer.HandleFarmerWarp(...)`（反编译 :735-736），我们缺这一步——warp 触发的事件（比如进城触发节日开场）在服务端永远不会发生。

## 五、NPC 与小屋

- **dd8082e**：睡醒后 NPC 全体冻结——vanilla 的 schedule 引擎在 headless 下没人驱动。修完顺带把 wake 后状态解冻。
- **0ade3ae**：`WarpPathfindingCache` 不填充，NPC 跨图日程全部失败。
- **c26d070 / 7213acf**：小屋里没有家具——starter furniture 路径依赖 host farmhouse 的状态，headless 下走不通。改用 `Furniture.GetFurnitureInstance` 生成正确子类（原实现用基类，sourceRect 不对），再兜底补床/壁炉/地毯/桌/椅，并加了 `housediag` 诊断命令。

## 六、连接与农手管理

- **B5**：客户端自报的 farmhand id 完全不校验——冒充主机 id、顶掉别人的 id，两条连接同时给一个农手写 delta。修：`IsClientFarmhandIdAcceptable` 拒绝 0 / 主机 id / 已被其它 Connected 连接占用的 id，允许接管陈旧映射。
- **B1/B2**：启动自测（debris self-test）会把农场真实掉落物 `Clear()` 掉，还会把测试农手漏进 `netWorldState.farmhandData` 这个持久目录（幽灵农手每次重启重现）。修：先快照再恢复真实 debris；farmhandData 清理，读档路径顺带 purge 老版本留下的幽灵。
- **B10/B11**：消息队列守卫内的 Clear 让无映射农手的队列无限增长；异常消息里对 null location 解引用。均修。

## 七、部署与运维

- **存档目录跟随服务器**（de621da）：vanilla 的 `Program.GetSavesFolder()` 是纯计算、没有任何覆写点，`SaveGame` 内部四处调用全走它，env 重定向也罩不住（Windows 下 `GetFolderPath` 不读 APPDATA 变量），只能 Harmony prefix detour。新增配置 `Paths.WorldSavesFolder`：默认 `saves/`（落在服务器根目录，Linux 云机部署的刚需），空串保持完全 vanilla 行为。配了防遮蔽守卫：重定向目录里没有本 farm 的候选档、而游戏默认目录里有——拒绝启动并列出修复路径，否则会静默开一个新世界，把玩家进度晾在原目录里。
- **命令文件卫生**（server-commands.txt）：原版的注释声称一次性消费，实现却从不截断，残留的 `roll` 每次重启都会重放——开发早期真实滚过一天并覆盖了存档。修：消费即截断；启动时忽略上次运行残留的内容（日志 `[Commands] Ignoring command file content left over from a previous run`）。再送部署者一条运维教训：构建输出目录里残留的命令文件会跟着部署被拷走，曾连续两次启动把世界各滚了一天，Spring 28 就这么丢的。

## 八、还没修的（如实列出）

- **F4 掉落物拾取（HIGH，定位中）**：垃圾桶食物、锄地产出捡不起来。机制已定位：vanilla `Debris.updateChunks` 只有 master 会指派 `player`（反编译 :752-755），客户端 `shouldControlThis=false` 永远不自指派、只能等服务器；服务器指派要三件事同时成立——debris 同步到服务器（type-6 delta，已证 OK）、地点里有农手（已证 OK）、农手 Position 在磁吸半径内。实测 BusStop 有 debris、有农手，十几秒内 assigned 恒 0：主嫌是 farmer.Position 的 type-0 delta 太稀疏/陈旧，磁吸检查一直拿旧位置。另外 Town 垃圾桶的 debris 压根没出现在服务器上（整场会话 world debris total 恒为 1），垃圾桶 action 的产生路径还在查。
- **门不显示**（第一次进 NPC 家）：`interiorDoors` 在服务端只有两个来源——`hostSetup()`（GameLocation.cs:6849，进入 isStructure 地点时 reset）和 SaveGame 载入（SaveGame.cs:1314，只填主机所在地点）。headless 下两个都不发生，字典是空的，客户端建不出门。根因已定位，修复未动手。
- **家具乱码**：自家小屋家具齐全（床/桌/椅/电视都在）但渲染成乱码。尚未定位，怀疑 furniture 的贴图/sourceRect 同步数据。
- **B6（语义待定，请作者定夺）**：`availableList.Count < MaxFarmhands` 只数"当前可选角色"，4 人在线且 MaxFarmhands=4 时仍会追加新槽位，农手数实际无上限（只受 MaxConnections 限制）。是改成 `clientConnections.Count + availableList.Count < MaxFarmhands`，还是维持语义改文档？
- **B12**：连接映射先于握手建立，catch 只记日志不回滚——握手失败的连接仍算在线、农手滞留集合。
- **B9**：warp 消息的 facing/forced-event 解码只进日志，从未应用到 farmer。
- 小项：bB3 `Protocol.cs:24` ForceKick=25 应为 23；bB4 `--port` 只认两段式且坏值直接 return（`--port abc --port 3000` 永远不生效）；bB6 ServerCommand 别名冲突不查重；bB7 Usage 未判空；bB2/bB5/bB8 死代码；C1 诊断命令死分支、C2 ProbeNpc 在主线程同步跑 20000 tick、C3 无连接时不刷农手 XML、C4 死状态。
- 观察项：`getUserName` 对所有农手返回同一个 "Player"；一次断线保存打印了成功日志但目标文件 mtime 未变（SaveFarmhand 走 temp+Move，未复现，先挂着）。

## 提交索引

| 提交 | 内容 |
|---|---|
| dd8082e | 睡醒 NPC 解冻 + 驱动 vanilla schedule 引擎 |
| 0ade3ae | 填充 WarpPathfindingCache，跨图日程可用 |
| 7213acf | 小屋家具兜底（床/壁炉/地毯/桌/椅）+ housediag |
| c26d070 | 家具改用 Furniture.GetFurnitureInstance（正确子类） |
| e40572c | 农手连入重指派悬空 homeLocation + 节日 host 自动 ready |
| b547207 | 日结写真实存档 + 重启恢复 + 候选槽列表 + 坏档拒绝启动 + SDL2.dll |
| 542997a | 审计修复：关服保存/save 命令/看门狗、日结竞态（延迟注册、ConcurrentDictionary、NetDictionary 排空）、自测污染、id 校验等 |
| 2ff515b | 真实客户端日结竞态（NetEvent 排空）、聊天 NRE、DedicatedServer.Tick NRE |
| de621da | 存档目录跟随服务器（Harmony + 防遮蔽守卫）、睡觉三修 |
| 7649539 | 节日链路（学习/窗口/真 fade 回调）、homeLocation 修复、homes 诊断 |

所有修复尽量走 vanilla 已有的机制（SaveGame 枚举器、disconnectingFarmers、netReady、schedule 引擎），不另起炉灶重写游戏逻辑——服务器核心的正确性，最终得以客户端看到的行为为准。上游若愿意合入，每个提交都独立成立、可单独挑拣。
