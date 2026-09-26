<div align="center">

<img src="icon-round.png" width="180" alt="ValleyServer" />

# ValleyServer

**A Multiplayer Unattended Server Solution for Stardew Valley**

[简体中文](README.md) | [English](README_en.md)

[![Release](https://img.shields.io/github/v/release/Lixeer/ValleyServer)](https://github.com/Lixeer/ValleyServer/releases) [![License](https://img.shields.io/github/license/Lixeer/ValleyServer)](LICENSE) [![Stars](https://img.shields.io/github/stars/Lixeer/ValleyServer)](https://github.com/Lixeer/ValleyServer/stargazers)

</div>

---

## What is this

ValleyServer keeps a Stardew Valley farm running with no human at the keyboard, and provides a multiplayer server your friends can join directly. The repository contains two independent mainlines:

- **2.x — MOD + Docker**: automated MODs (auto sleep, skip cutscenes, close dialogs) run on top of a real game client, deployed with Docker. **Maintenance mode.**
- **3.x — reverse-engineered / mock protocol server**: no game client involved; decompiled game assemblies are loaded directly to build a native, GUI-less headless server. **Experimental.**

> [!NOTE]
> Neither mainline requires a Steam account or the Steam client. The project is free and open source, and the author earns nothing from it — if you enjoy the game, please buy it.

## Which version should I use

| | 2.x (MOD + Docker) | 3.x (reverse-engineered / mock protocol server) |
| :--- | :--- | :--- |
| Approach | Real game + `SMAPI` + automation MODs | Decompiled assemblies driven by reflection / mocking |
| Status | Maintenance mode: bug fixes only | Experimental: not recommended for production |
| Best for | Existing servers already in use | Trying it out, studying the protocol, contributing |
| Docs | [docs/v2](docs/v2/README.md) | [docs/v3](docs/v3/README.md) |

**Recommendation**

- **Long-term stability**: 2.x still works but is frozen; [JunimoServer](https://github.com/stardew-valley-dedicated-server/server) is a comparable alternative.
- **Trying 3.x or contributing to it**: start from the [3.x documentation](docs/v3/README.md).
- Full comparison and decision path: [version guide](docs/version-guide.md) (Chinese).

> [!IMPORTANT]
> 2.x is in maintenance mode: feature requests, container-management proposals and similar pull requests are no longer accepted — only crash and compatibility fixes. Existing deployments keep working, and the image plus the [deployment cookbook](docs/v2/cookbook.md) stay available.

## Quick start

### 3.x (experimental)

Download `ValleyServer-<platform>-x64.zip` from [Releases](https://github.com/Lixeer/ValleyServer/releases) and run the executable — the .NET runtime is bundled. A `config.json` is written on first run, the server listens on `24642/udp` by default, and `help` on the console lists the available commands.

Build steps, configuration and known limitations: [3.x documentation](docs/v3/README.md).

### 2.x (maintenance mode)

```sh
cd oneclick-script
bash ./run.sh
```

Then open `http://<server-ip>:5800`, switch to the VNC page and enter the password (`041041` by default). The MOD directory is empty on first start — add `ALOS` and the others yourself.

Ports, volume mounts and a walkthrough of the compose file: [Docker cookbook](docs/v2/cookbook.md).

## Documentation

| Document | Contents |
| :--- | :--- |
| [Version guide](docs/version-guide.md) | Choosing between 2.x and 3.x (Chinese) |
| [2.x documentation](docs/v2/README.md) | MOD + Docker: how it works, quick start, MOD list, FAQ (Chinese) |
| [Docker cookbook](docs/v2/cookbook.md) | Container deployment and compose file walkthrough (Chinese) |
| [3.x documentation](docs/v3/README.md) | Headless protocol server: build, configuration, progress, limits (Chinese) |
| `Mods/*/README.md` | Features and configuration options of each MOD |

## Community

The project is maintained and updated by the community — `issues` and `PRs` are welcome.

> [!IMPORTANT]
> The `issue` tracker only accepts MOD-related feature requests — "please support panel X / deployment style Y" is out of scope and community-driven. If you already have a mature deployment setup, feel free to open a `PR` adding your repository to the docs.

| QQ Group (#1–#3 full) | [![QQ Group#4](https://img.shields.io/badge/QQ%20Group%234-Join-blue)](https://qm.qq.com/q/XUzyb67T6C) | [![QQ Group#3](https://img.shields.io/badge/QQ%20Group%233-Join-blue)](https://qm.qq.com/q/vfn1YWMCRM) | [![QQ Group#2](https://img.shields.io/badge/QQ%20Group%232-Join-blue)](https://qm.qq.com/q/KhXvEqsw8g) | [![QQ Group#1](https://img.shields.io/badge/QQ%20Group%231-Join-blue)](https://qm.qq.com/q/Q8QaovnQWG) |
| :-: | :-: | :-: | :-: | :-: |

| QQ Channel (release announcements) | [![QQ Channel](https://img.shields.io/badge/QQ%20Channel-Join-blue)](https://pd.qq.com/s/7gut1do04?b=5) |
| :-: | :-: |

## Credits and related projects

- [SMAPI](https://github.com/Pathoschild/StardewModdingAPI): game injection and modding framework
- [Stardew Valley](https://www.stardewvalley.net): official game site
- [stardew-multiplayer-docker](https://github.com/printfuck/stardew-multiplayer-docker): Docker deployment for Stardew Valley multiplayer
- [JunimoServer](https://github.com/stardew-valley-dedicated-server/server): a comparable open-source unattended server project

## License

This repository is distributed under the [Apache License 2.0](LICENSE).

Some MODs under `Mods/` are derivative works and follow their original licenses — for example [`ALOS`](Mods/ALOS/README.md) is based on [perkmi/Always-On-Server-for-Multiplayer](https://github.com/perkmi/Always-On-Server-for-Multiplayer) and is distributed under MIT plus additional terms; attribute the original repository when using it commercially. MODs by other authors bundled in releases remain the property of their authors.

## Star History

[![Star History Chart](https://star-history.dera.page/svg?repos=Lixeer/ValleyServer&type=Date)](https://star-history.dera.page/#Lixeer/ValleyServer&Date)

## Contributors

<a href="https://github.com/Lixeer/ValleyServer/graphs/contributors">
  <img src="https://contrib.rocks/image?repo=Lixeer/ValleyServer" alt="Contributors" />
</a>

## Donate

If you like this project, you can support development here:

<img src="docs/img/vx_pay.jpg" width="240" alt="WeChat donation QR code" />
