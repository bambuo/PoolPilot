# PoolPilot

Lighter（zk-rollup 永续 DEX）公共资金池的**观察、分析、纸面跟单与回测工作台**。

回答的问题：某个资金池值不值得跟、跟单的摩擦成本有多大、本金至少多少才跟得动。

界面按「总览 → 观察 → 跟单 → 配置」组织：首页是**系统关系图**（池 → 带单员 → 跟单员三层），
随后是池列表、带单员、跟单、交易所、规模门槛、回测面板。

## 不做什么

**本系统不下单。** Lighter 的下单必须用 API key 签名，而 .NET 生态没有可用签名器
（官方只提供 Go 编译的 WASM、C 动态库、Python SDK）。未来若接入，只实现 `IBroker` 抽象，
不改动领域逻辑。

## 技术栈

| 层 | 技术 |
|---|---|
| 后端 | C# 14 / .NET 10 · ASP.NET Core · EF Core 10 + SQLite · SignalR |
| 前端 | Vue 3.5 · TypeScript 6 · Vite 8 · Arco Design Vue 2.58 |
| 测试 | MSTest 4（四层 + 真实接口契约测试） |

架构为整洁架构四层：`Domain ← Application ← Infrastructure ← Api`。
领域层零外部依赖——上游的字段口径全部在这一层固化为类型约束。

## 目录

```
PoolPilot.slnx
src/
  PoolPilot.Domain/          领域模型与领域服务（零外部依赖）
  PoolPilot.Application/     用例与 DTO
  PoolPilot.Infrastructure/  Lighter 客户端、持久化、常驻监控
  PoolPilot.Api/             Controllers、SignalR Hub、组合根
tests/
  PoolPilot.Domain.Tests/    字段陷阱断言（最重要的一层）
  PoolPilot.Application.Tests/
  PoolPilot.Api.Tests/
  PoolPilot.Lighter.Tests/   真实接口契约测试（[TestCategory("Live")]）
web/                         Vue 工作台
docs/                        编码规范、接口实测笔记、角色关系、凭据存储
```

## 环境要求与命令

dotnet 位于 `~/.dotnet`，SDK 需 .NET 10。

```bash
# 构建与测试（一律加 -m:1，本机需要防 Jetsam）
dotnet build -m:1
dotnet test  -m:1

# 日常门禁：排除需要联网的契约测试
dotnet test -m:1 --filter "TestCategory!=Live"

# 后端（默认 http://localhost:5274）
dotnet run --project src/PoolPilot.Api

# 前端（默认 http://localhost:5273，已配置 /api 与 /hubs 反代到 5274）
cd web && npm install && npm run dev
```

> ⚠ **不要安装任何 .NET runtime**：安装 runtime 会覆盖 muxer，导致全线进程以 137 退出
> （Jetsam `bug_type 309`）。本机已有 SDK 10.0.302 与 ASP.NET Core 10.0.10 runtime。

## 文档

| 文档 | 内容 |
|---|---|
| [AGENTS.md](AGENTS.md) | AI 角色画像与八条工作铁律 |
| [docs/CODING_STANDARDS.md](docs/CODING_STANDARDS.md) | 分层、C# 规范、测试规范、数据访问规范 |
| [docs/LIGHTER-API-NOTES.md](docs/LIGHTER-API-NOTES.md) | **Lighter 接口实测笔记**——领域模型的唯一依据 |
| [docs/LEAD-FOLLOWERS.md](docs/LEAD-FOLLOWERS.md) | 带单员与跟单员的职责边界、信号、触发模型与已知限制 |
| [docs/EXCHANGES.md](docs/EXCHANGES.md) | 交易所配置与凭据：为什么**不加密**、凭据三态与最小权限建议 |
| [docs/PAPER-TRADING.md](docs/PAPER-TRADING.md) | 纸面跟单的会计口径（比例复制、跳过记录、权益点） |

第三份是最该先读的：它记录了上游的字段陷阱（四种时间刻度、两种保证金率刻度、
一串"本身就是百分数"的字段、三个恒为 0 的死字段），以及官方文档与实测相矛盾的四处。
领域层之所以长成现在这样，全部是为了守住这些口径。
