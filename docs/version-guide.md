# ValleyServer 版本指南（2.x vs 3.x）

ValleyServer 存在 **2.x** 与 **3.x** 两条主线，二者在实现原理与代码位置上完全不同。本页只做选型对比；各主线的完整文档见 [2.x 文档](v2/README.md) 与 [3.x 文档](v3/README.md)。

## 对比一览

| 维度 | 2.x（MOD + Docker） | 3.x（反编译 / mock 协议端） |
| :--- | :--- | :--- |
| 原理 | 真实客户端 + `SMAPI` + 自动化 MOD | 加载反编译程序集 + 反射 / mock，无 GUI |
| 代码位置 | `Mods/`、`oneclick-script/` | `src/ValleyServer/` |
| 是否依赖游戏客户端 | 是 | 否（仅依赖反编译 DLL 与 `Content`） |
| 是否有 GUI | 是（可后台 + VNC） | 无 GUI，纯无头 |
| 稳定性 | 高，但已冻结（仅修复性维护） | 低（实验性） |
| 版本号 | `v2.x` | `v3.x` |
| 推荐给新用户 | 否 | 仅限尝鲜 / 研究 |
| 完整文档 | [2.x 文档](v2/README.md) | [3.x 文档](v3/README.md) |

## 我该用哪个？

- **已经有一台在跑的 2.x 服务器** → 保持现状，参考 [2.x 文档](v2/README.md) 与 [Docker 部署手册](v2/cookbook.md)；遇到崩溃类问题仍可提交 `issue`。
- **想尝鲜无头方案，或对协议 / 反编译感兴趣** → 进入 [3.x 文档](v3/README.md)，并加入 QQ 群 / 提交 `issue`（3.x 仍是实验性的，请勿用于生产）。
- **需要长期稳定的生产环境** → 目前两条主线都不是理想答案：2.x 已冻结、3.x 未成熟。可以先评估生态位相近的 [JunimoServer](https://github.com/stardew-valley-dedicated-server/server)，或关注 3.x 的[当前进度](v3/README.md#当前进度)。
