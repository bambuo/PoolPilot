# 部署到 Linux（amd64 deb）

目标：在 Debian 12+ / Ubuntu 22.04+ 上安装一个 **自包含** 的 `poolpilot` 服务，Kestrel 同时提供 API 与前端，SQLite 数据放在 `/var/lib/poolpilot`，由 systemd 负责自动启动与重启。

## 一、构建

构建机只需要 Docker（macOS 上用 Docker Desktop 即可），不需要安装 .NET、Node 或 `dpkg`。

```bash
cd /path/to/PoolPilot
./packaging/linux/build-deb.sh --version 0.1.0
```

产物：

```text
packaging/out/poolpilot_0.1.0_amd64.deb
```

构建过程在容器内完成三件事：

1. `node:22-bookworm-slim` 里执行 `npm ci && npm run build`；
2. `mcr.microsoft.com/dotnet/sdk:10.0` 里执行 `dotnet publish -r linux-x64 --self-contained true`；
3. `debian:bookworm-slim` 里用 `dpkg-deb` 组装包。

默认构建 `linux/amd64`。Apple Silicon 上 Docker 会使用 amd64 模拟，构建会比原生慢；如果构建机本身是 amd64 Linux，则没有模拟开销。

公开页头部的「登录 / 注册」入口由 `VITE_SHOW_AUTH_LINKS` 控制，默认显示。打包时隐藏：

```bash
SHOW_AUTH_LINKS=false ./packaging/linux/build-deb.sh --version 0.1.0
```

## 二、安装

```bash
sudo apt install ./packaging/out/poolpilot_0.1.0_amd64.deb
```

安装后：

- 服务单元：`/lib/systemd/system/poolpilot.service`
- 程序与前端：`/opt/poolpilot/api`（含 `wwwroot`）
- 配置：`/etc/default/poolpilot`（dpkg conffile，升级不会覆盖）
- 数据：`/var/lib/poolpilot`（0700，属 `poolpilot:poolpilot`）
- 服务用户：`poolpilot`，无登录 shell

检查：

```bash
systemctl status poolpilot
journalctl -u poolpilot -f
curl http://127.0.0.1:5274/api/health
```

浏览器打开 `http://<服务器IP>:5274/`。首次安装且库里没有账号时会进入 `/setup` 初始化页；第一个注册账号自动成为超级管理员。

## 三、配置

编辑：

```bash
sudo nano /etc/default/poolpilot
sudo systemctl restart poolpilot
```

默认内容：

```bash
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://0.0.0.0:5274
PoolPilot__DataDirectory=/var/lib/poolpilot
PublicPages__Leads=true
PublicPages__Followers=true
Auth__AccountsEnabled=true
```

常见改动：

- 换端口：改 `ASPNETCORE_URLS`，然后确保防火墙同步放行。
- 关闭公开页：把 `PublicPages__Leads` / `PublicPages__Followers` 改成 `false` 并重启。**deb 包默认打开**；源码里的 `appsettings.json` 仍保持 `false`，直接跑 API 不会意外公开。
- 禁用登录/注册：把 `Auth__AccountsEnabled` 改成 `false` 并重启。登录、注册、MFA、首次初始化端点都会回 404；公开页头部会根据状态接口自动隐藏登录/注册入口，直接访问 `/login`、`/register`、`/mfa`、`/setup` 会被前端送回公开首页。**已有会话 Cookie 不会因此被吊销**，仍可使用到过期；要彻底锁死工作台，需同时清理会话或加网络层限制。
- 放到反向代理后面：把监听改成 `http://127.0.0.1:5274`，由 nginx/caddy 终止 TLS 并反代 `/`、`/api`、`/hubs`（SignalR 需要 WebSocket）。

### ⚠️ 公网安全前提

包默认按“直接访问”配置成 `0.0.0.0:5274`。**服务本身不终止 TLS，会话 Cookie 目前也没有 `Secure` 标记**；直接把 5274 暴露到公网，会让登录密码、MFA 码和会话 Cookie 以明文经过网络。

公网部署请至少做到其中一项：

- 只放在防火墙/VPN/内网之后；
- 或改成 `127.0.0.1:5274`，用 nginx/caddy 配证书后反代。

## 四、迁移现有数据

如果要把本机开发库搬到服务器：

```bash
sudo systemctl stop poolpilot
sudo install -d -o poolpilot -g poolpilot -m 0700 /var/lib/poolpilot
sudo install -o poolpilot -g poolpilot -m 0600 \
  data/poolpilot.db data/poolpilot.db-wal data/poolpilot.db-shm \
  /var/lib/poolpilot/
sudo systemctl start poolpilot
```

如果只想搬主库，先在本机正常停掉 API 让 WAL 落盘，再只复制 `poolpilot.db`；不要带着正在写入的 WAL 单独搬主库。

升级/重装包不会覆盖 `/var/lib/poolpilot`，也不会覆盖 `/etc/default/poolpilot` 的本地修改。

> **从"全局共享"版本升级到按用户隔离的版本**：已有的跟单员与交易所配置
> `OwnerUserId` 为空，只对 superadmin 可见。确认归属后，在停服前或升级后用 SQL 回填
> （库在 `/var/lib/poolpilot/poolpilot.db`）：
> `UPDATE followers SET OwnerUserId=<用户 Id> WHERE COALESCE(IsPublic,0)=0 AND OwnerUserId IS NULL;`
> `UPDATE exchange_configs SET OwnerUserId=<用户 Id> WHERE OwnerUserId IS NULL;`
> 公开页的行（`IsPublic=1`）不要回填成私有归属。

## 五、升级与卸载

```bash
sudo apt install ./poolpilot_0.2.0_amd64.deb   # 升级
sudo apt remove poolpilot                       # 卸载服务，保留数据
sudo apt purge poolpilot                        # 卸载；数据目录仍然保留
```

`postrm` 刻意不删除 `/var/lib/poolpilot`：里面有账本、交易所凭据和 MFA 种子，是否删除必须由管理员明确决定。确认不再需要时手工：

```bash
sudo rm -rf /var/lib/poolpilot
```

## 六、排障

- 服务起不来：`journalctl -u poolpilot -n 200 --no-pager`。
- 端口被占：`ss -ltnp | grep 5274`，改 `/etc/default/poolpilot` 的 `ASPNETCORE_URLS`。
- 前端空白/404：确认 `/opt/poolpilot/api/wwwroot/index.html` 存在；包内前端由 Kestrel 托管。
- 权限错误：确认 `/var/lib/poolpilot` 属 `poolpilot:poolpilot` 且为 0700。
- 包安装依赖失败：目标系统需要 Debian 12+/Ubuntu 22.04+（`libssl3`）。
