# 2.x — 基于 MOD 的无人值守 + Docker

在真实的 `Stardew Valley` + `SMAPI` 客户端之上运行自动化 MOD，让「农场主人」自动运行，并用 Docker 提供开箱即用的容器化部署。

> [!IMPORTANT]
> 2.x 处于**维护模式**：不再新增功能，不再接收功能增强与容器管理方案一类的合并请求，只修复崩溃与兼容性问题。已有部署可以继续使用，镜像、[部署手册](cookbook.md)与本文档继续保留；**新部署不建议从 2.x 开始**，请先确认 [3.x](../v3/README.md) 是否满足需求。

## 原理

2.x 本质上是一个「虚拟显示器 + `SMAPI` + 游戏本体」的容器：

- 真实游戏客户端 + `SMAPI`，可以像正常游戏一样加载其他 MOD
- `ALOS` 等 MOD 接管农场主人：自动睡觉、自动跳过剧情、自动关闭弹窗
- 无人时自动暂停，并修复游戏原生的暂停 bug
- 通过 `ChatCommand` / `ServerCMD` / `CommandWebUI` 在无头环境下执行控制指令
- Docker 容器化部署，附带 WebUI / WebVNC，可在浏览器里看到游戏画面

## 适用与不适用

| | 说明 |
| :--- | :--- |
| 适合 | 已经在运行的存量服务器；明确需要「真实客户端 + `SMAPI` MOD」能力的场景 |
| 不适合 | 新项目；需要持续的功能演进；不想同时维护容器与游戏客户端 |

由于 2.x 已冻结，如果需要生态位相近、仍在活跃维护的方案，可以评估 [JunimoServer](https://github.com/stardew-valley-dedicated-server/server)。

## 快速开始

```sh
cd oneclick-script
bash ./run.sh
```

启动后访问 `http://<服务器IP>:5800` 进入 Web 界面，打开其中的 VNC 页面并输入连接密码（默认 `041041`），即可看到游戏主界面。首次启动时 MOD 目录是空的，需要自行放入 `ALOS` 等 MOD 才能实现无人值守。

完整的端口、目录挂载与编排文件解读见 [Docker 部署手册](cookbook.md)。

## 本仓库维护的 MOD

以下 MOD 用于 2.x 主线，源码位于 `Mods/`，也可以单独使用：

| MOD | 作用 | 文档 |
| :--- | :--- | :--- |
| `ALOS`（Always On Server） | 无人值守运行游戏：自动睡觉、跳过剧情、自动操作 | [文档](../../Mods/ALOS/README.md) |
| `ServerCMD` | 在无头服务器环境下执行控制指令 | [文档](../../Mods/ServerCMD/README.md) |
| `ChatCommand` | 在游戏聊天框中执行控制台指令 | [文档](../../Mods/ChatCommand/README.md) |
| `CommandWebUI` | 在浏览器中使用 `SMAPI` 控制台 | [文档](../../Mods/CommandWebUI/README.md) |
| `ChangeServerPort` | 修改服务器端口 | [文档](../../Mods/ChangeServerPort/README.md) |

> [!NOTE]
> `release` 页中也会一并打包其他作者的 MOD（与本项目搭配使用效果更好），版权归各自作者所有。你可以在对应的 `manifest.json` 中找到仓库地址，并为他们提供支持。

## 端口

| 端口 | 用途 |
| :--- | :--- |
| `24642/udp` | 游戏端口 |
| `5800` | WebVNC，浏览器里操作游戏 |
| `5900` | VNC，可用 TigerVNC 等客户端直连，性能更好 |
| `29103` | 预留 |

## 常见问题

**2.x 还会更新吗？**
不会新增功能。只接受崩溃与兼容性修复；功能增强、容器管理方案一类的 PR 不再接收。

**可以加载其他 SMAPI MOD 吗？**
可以，2.x 本身就是真实客户端加 `SMAPI`。但部分 MOD 的联机兼容性需要自行测试。

**首次启动看到的是空白 / 没有游戏画面？**
按顺序检查：`5800` 端口是否可访问 → VNC 密码是否为默认的 `041041` → MOD 目录是否为空（首次启动时为空是正常的，需要自行放入 `ALOS` 等 MOD）。

**镜像在哪里，还能拉到吗？**
镜像与部署方式保持不变，见 [Docker 部署手册](cookbook.md)。如果拉取缓慢，手册中说明了加速方式。

## 相关文档

- [版本指南](../version-guide.md)：2.x 与 3.x 的选型对比
- [Docker 部署手册](cookbook.md)：容器化部署与编排文件解读
- [3.x 文档](../v3/README.md)：反编译 / mock 协议服务端
