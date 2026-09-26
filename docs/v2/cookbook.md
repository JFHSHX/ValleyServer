# 2.x Docker 部署手册

本页是 [2.x 主线](README.md)的容器化部署手册，配合仓库中的 `oneclick-script/` 目录使用。

## 1. 安装 Docker 与 Docker Compose

需要先安装 `docker` 与 `docker compose`（Compose v2）。

`docker` 会把应用及其依赖的系统库、配置、运行时一起打包成可移植的容器镜像，因此在任何支持 Docker 的环境中行为一致，宿主机不需要另外安装游戏本体和运行依赖。`docker compose` 用于编排容器，在本项目中只是把配置结构化。

## 2. 准备脚本目录

把仓库中的 `oneclick-script/` 目录下载（或克隆）到服务器上，后续命令都在该目录内执行。

## 3. 一键拉取并启动

```sh
cd oneclick-script
bash ./run.sh
```

> [!NOTE]
> 首次拉取镜像受网络影响可能较慢（取决于与 Docker Hub 的连接质量，不需要从 Steam 下载游戏本体）。可以自行配置镜像加速或代理；为了方便使用，也可以在 QQ 群中联系作者获取加速流量，单日象征性收取一元。

## 4. 验证是否启动成功

访问 `http://<服务器IP>:5800`，会看到一个 Web 界面。进入其中带 `vnc` 字样的页面，输入连接密码（默认 `041041`），即可看到游戏主界面，此时只需要在游戏里主持（Host）多人游戏。

> [!NOTE]
> 此时 MOD 目录是空的，这是正常的。要让服务器无人值守运行，还需要把 `ALOS` 等 MOD 放进去。

## 5. 编排文件解读

以下为 [`docker-compose.yml`](../../oneclick-script/docker/docker-compose.yml) 的内容：

```yml
version: "3.8"

services:
  stardew:
    image: lixeer/valley-server:v26.5
    container_name: stardew_server
    restart: unless-stopped

    volumes:
      - ./Mods:/content/Stardew Valley/Mods     # 容器内的 MOD 目录挂载到宿主机：容器重建或删除都不会影响该目录，MOD 放入这里才会生效
      - ./Saves:/root/.config/StardewValley/Saves # 同上，这里是存档目录

    ports:
      - "24642:24642/udp"  # 游戏端口
      - "5800:5800"        # WebVNC 端口
      - "5900:5900"        # VNC 端口，可用 TigerVNC 等软件连接，性能优于 WebVNC
      - "29103:29103"      # 预留端口
```

| 挂载 / 端口 | 说明 |
| :--- | :--- |
| `./Mods` | MOD 目录，映射到宿主机后容器重建也不会丢；MOD 必须放入这里才会加载 |
| `./Saves` | 存档目录 |
| `24642/udp` | 游戏端口 |
| `5800` | WebVNC |
| `5900` | VNC，可用 TigerVNC 等客户端直连 |
| `29103` | 预留 |

## 6. 遇到问题

可在 QQ 群或 `issue` 中提出，反馈时请附上容器日志（`docker compose logs`）。相关文档：[2.x 文档](README.md) · [版本指南](../version-guide.md)。
