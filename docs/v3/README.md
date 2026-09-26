# 3.x — 反编译 / mock 协议服务端（实验性）

不启动真实游戏客户端，直接加载反编译得到的游戏程序集，通过反射与 mock 构造一个无 GUI 的原生无头服务器，并用 `Lidgren.Network` 直接实现星露谷的多人消息层。

> [!WARNING]
> 3.x 处于实验阶段，**不建议用于生产环境**。它依赖游戏内部实现，游戏更新后极易失效，并且缺少完整的游戏逻辑（农业、季节、NPC、任务、节日等）。选型对比见 [版本指南](../version-guide.md)。

## 原理

- 通过 `HeadlessContentManager` mock 掉 `Texture2D` / `SpriteFont` 等图形资源
- 用 `FormatterServices.GetUninitializedObject` 绕过构造函数与 XNA 图形上下文检查
- 通过反射注入 `Game1` 静态字段、`Multiplayer`、`NullSDKHelper` 等
- 直接基于 `Lidgren.Network` 实现星露谷的信息层协议（握手、传送、广播等）
- 服务端本身没有 GUI，可以在 1 核 / 512 MB 级别的机器上运行

## 快速开始

从 [Releases](https://github.com/Lixeer/ValleyServer/releases) 下载对应平台的压缩包并解压，直接运行可执行文件即可——自带 .NET 运行时，目标机器无需安装：

| 平台 | 压缩包 |
| :--- | :--- |
| Windows x64 | `ValleyServer-win-x64.zip` |
| Linux x64 | `ValleyServer-linux-x64.zip` |

- 首次启动会在可执行文件旁生成 `config.json`，可按需修改；游戏资源目录可用 `VALLEY_CONTENT_PATH` 指定。
- 默认监听 `24642/udp`，可用 `--port` 覆盖。
- 控制台输入 `help` 查看指令，`stop`（别名 `shutdown`、`quit`）会保存所有 farmhand、断开客户端并优雅退出。

## 构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。资源目录不在仓库里，先下载对应游戏版本（当前为 `1.6.15`）的 `Content`：

```sh
mkdir -p src/ValleyServer/Content
curl -L -o content.zip https://github.com/Lixeer/ValleyContent/releases/download/G1.6.15/Content.zip
unzip -q content.zip -d src/ValleyServer/Content
rm content.zip
```

发布独立运行版本（自带 .NET 运行时，目标机器无需安装）：

```sh
# Windows x64
dotnet publish src/ValleyServer/ValleyServer.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64

# Linux x64
dotnet publish src/ValleyServer/ValleyServer.csproj -c Release -r linux-x64 --self-contained true -o publish/linux-x64
```

`csproj` 会把 `deps/` 下的原生依赖一并复制到输出目录，因此发布产物不依赖本机的游戏安装路径。CI 中相同的流程见 [`.github/workflows/build-server.yml`](../../.github/workflows/build-server.yml)。

## 源码结构

代码位于 [`src/ValleyServer`](../../src/ValleyServer)：

| 文件 | 作用 |
| :--- | :--- |
| `Program.cs` | 主程序：反射注入 `Game1` 状态、初始化网络、消息主循环 |
| `Program.Helpers.cs` | 消息处理、广播、昼夜切换与存档逻辑 |
| `Program.Commands.cs` | 内置控制台指令的注册与执行 |
| `Program.Shutdown.cs` | 优雅退出流程 |
| `Protocol.cs` | 多人消息类型常量与消息描述辅助函数 |
| `ServerConfig.cs`、`ConfigLoader.cs` | 分层配置模型与 `config.json` 加载 / 生成 |
| `ConsoleCommandReader.cs`、`ServerCommand.cs` | 控制台指令框架 |
| `HeadlessContentManager.cs` | mock `Texture2D` / `SpriteFont` 等图形资源 |
| `HeadlessDisplayDevice.cs` | 无 GUI 的显示设备实现 |
| `HeadlessGameServer.cs` | 无头服务器实例与推迟的过夜消息泵 |
| `MockLidgrenMessageUtils.cs` | 消息构造辅助 |
| `deps/` | 反编译得到的程序集（`Stardew Valley.dll`、`MonoGame.Framework.dll`、`Lidgren.Network.dll`、`xTile.dll`、`liblwjgl_lz4` 等），随仓库分发 |
| `Content/` | 游戏资源，**不在版本库中**，需要按上方步骤下载 |

## 配置项

`config.json` 的缺省值即当前的内置默认行为，只填写需要改动的字段也可以。完整默认值见 [`ServerConfig.cs`](../../src/ValleyServer/ServerConfig.cs)：

| 配置段 | 关键项 | 默认值 |
| :--- | :--- | :--- |
| `Network` | `Port` / `MaxConnections` / `ConnectionTimeoutSeconds` | `24642` / `16` / `30` |
| `World` | `FarmType` / `FarmName` / `HostName` / `StartingCabins` / `MaxFarmhands` | `0` / `HeadlessFarm` / `Host` / `4` / `4` |
| `Simulation` | `MillisecondsPerTenMinutes`：真实毫秒数与游戏内 10 分钟的换算 | `1000` |
| `Paths` | `ContentPath`：游戏资源目录，留空则自动探测；`SaveDirectory`：farmhand 存档目录 | 留空 / `saved_farmhands` |

命令行与环境变量：

- `--port <1024-65535>`：覆盖监听端口，非法值会被忽略并保留配置值。
- `VALLEY_CONTENT_PATH`：指定 `Content` 资源目录，优先级高于配置文件。
- `VALLEY_GAME_PATH`：指定本机星露谷安装目录，作为资源探测的兜底路径。

## 当前进度

已经跑通的部分包括客户端发现 / 连接 / 握手、farmhand 列表与新建、玩家之间的介绍（`ServerIntroduction` / `PlayerIntroduction` / `LocationIntroduction`）、传送与位置根同步、部分消息的广播转发、昼夜切换的 `NewDaySync` / `NetReady` 同步，以及 farmhand 存档的落盘。

尚未完成的部分：完整的游戏逻辑（农业、季节推进、NPC、任务、节日等）。最新进展以代码为准，欢迎通过 QQ 群或 `issue` 参与——我们正在编写一个用于分析星露谷源码、整理协议文档的 `agent`。

## 已知限制

- 仍依赖从 [Lixeer/ValleyContent](https://github.com/Lixeer/ValleyContent) 下载的 `Content` 资源。
- 缺少完整的游戏逻辑支撑，很多玩法相关的行为尚不成立。
- 大量依赖反射与 mock 绕过构造函数与图形上下文检查，**游戏版本升级后极易失效**。
- 代码中保留了硬编码的兜底路径（例如 `Program.cs` 中的 `D:\app\steam\...`），在其他机器上请用 `VALLEY_CONTENT_PATH` / `VALLEY_GAME_PATH` 覆盖。

## 常见问题

**为什么 3.x 缺少农业、季节、NPC、节日这些内容？**
3.x 目前只 mock 了运行服务器所必需的最小游戏状态，完整的游戏逻辑尚未实现。当前进度见上方「当前进度」。

**3.x 为什么还要下载 `Content` 资源？**
本仓库不包含游戏的贴图与数据资源，因此运行时（以及构建时）需要从 [Lixeer/ValleyContent](https://github.com/Lixeer/ValleyContent) 获取对应游戏版本的 `Content.zip`。

**需要 Steam 账号或正版游戏吗？**
都不需要，3.x 的构建与运行都不依赖游戏账号，也不需要 Steam 客户端。

**服务器资源占用高吗？**
即使全量加载地图坐标状态也不会超过 1 GB 内存，实测在 1 核 N150 + 500 MB 内存的环境下可以流畅运行。

**可以加载 SMAPI MOD 吗？**
架构上可以把 `SMAPI` 当作修改入口，但完全拥抱 SMAPI 生态并不现实——好在很多 MOD 属于 `Only Client`。如果需要完整的 MOD 能力，请使用 [2.x](../v2/README.md)。

## 相关文档

- [版本指南](../version-guide.md)：2.x 与 3.x 的选型对比
- [2.x 文档](../v2/README.md)：基于 MOD 的无人值守 + Docker（维护模式）
