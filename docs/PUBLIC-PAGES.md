# 公开页（匿名可达的带单与跟单留言板）

两条不需要账号就能看的页面：**公开带单页**（`/public/leads`）与**公开跟单页**（`/public/follows`）。
它们的存在是为了让"这套系统在看哪些池、跟得怎么样"这件事可以被陌生人观察，
而陌生人不必为了看一眼就注册；顺带允许他不留身份地建一份**纸面**跟单来自己试。

**源码默认全关，deb 包默认打开**。开关在 `appsettings.json` 的 `PublicPages` 节与包的
`/etc/default/poolpilot`（`PublicPages__Leads` / `PublicPages__Followers` 各自一刀）。
代码里的默认值是 false；deb 包为了公开部署把它显式覆盖成 true。这两页匿名可写，
打开之前要确认这台部署确实想对外。

## 一、两页各自能做什么

| | 公开带单页 | 公开跟单页 |
|---|---|---|
| 读 | 正在被观察的带单员：池名、是否在收推送、订阅数、信号条数、最近信号时刻 | 公开纸面跟单的卡片（本金、权益、收益率、跳过数、持仓摘要）与详情（持仓含保证金/ROI/清算价、权益曲线、最近 50 条流水） |
| 写 | **新增**带单员（选一个池，让系统开始观察它） | **新增**纸面跟单：挑一个**带单员** + 本金 + 可选名称 |
| 不能写 | 暂停、删除 | 改本金、启停、实盘任何操作；删除只对创建者开放 |
| 身份 | 不需要（带单员没有创建者概念） | 需要——见第三节 |

带单页的"不能暂停"不是靠界面藏起按钮：**新建走的是 `Ensure` 语义**
（该池已有带单员时原样返回现状，绝不改动 `IsEnabled`），所以公开端点根本不存在能改开关的路径。
暂停是交易决策，只有工作台里的人能做。

**跟单页挑的是带单员，不是池。** 带单员与池 1:1（它的身份就是池索引），作用是替系统盯着那个池
产出信号；跟单员订阅的是**带单员的信号**。所以下拉里的候选来自 G1（已经在观察的那些），
而不是全量池列表——池上没有观察者时服务端直接回 400，**不会"顺手补一个"**：
建观察者意味着替全站决定去长期订阅一个池，那是带单页（W1）的一道独立动作，
有自己的名额与限流。工作台里的「开始跟单」从来就是这条规则（`CreateFollowerUseCase`
在没有带单员时抛错），公开侧与它一致。

`GET /api/public/pool-options` 因此只服务带单页的选池框，跟单弹窗不用它。

## 二、公开面不在 Casbin 里

`/api/public/*` 不挂权限点，也不进菜单表。原因不是漏了，是这类判定在这里换不来保护：
Casbin 的主体是"用户 / 角色"，而匿名访客不是主体——给他一个 `menu:public-leads` 的键，
要么所有人都有（等于没有），要么按访客身份发（而访客没有身份）。

替权限挡在那里的是三道闸：

1. **功能开关**（`PublicPages`，源码默认关、deb 包默认开）。关掉时每个公开端点回 **404 而不是 403**——
   一个没开放的功能不该让人探测得出它存在。
2. **按来源地址的限流**（`Microsoft.AspNetCore.RateLimiting`，框架自带，未引入任何包）：
   读 60 次/分钟、写 10 次/小时，超限回 429 + `Retry-After`。
   状态读口（`/api/public/status`）只回三个布尔，不占读额度。
3. **数量上限**（`src/PoolPilot.Domain/Constants/PublicPageLimits.cs`，**代码常量**，
   回退方式是改常量重新发布）：

   | 常量 | 值 | 防的是 |
   |---|---|---|
   | `TotalLeads` | 100 | 全站带单员总数（一个带单员 = 一条长期上游订阅 + 持续落库的信号） |
   | `LeadsPerDay` | 20 | 一天被灌进几十个池子观察 |
   | `TotalPublicFollowers` | 300 | 每条公开跟单都进复制管线并写两张账本表，是持续的 CPU 与磁盘 |
   | `FollowersPerVisitor` | 10 | 单个创建者占满留言板 |
   | `MinPublicCapital` / `MaxPublicCapital` | 10 / 10 000 | 低于下算不出任何仓位只剩摩擦成本；高于上不该由匿名页面产生 |
   | `PoolOptionLimitDefault` / `PoolOptionLimitMax` | 20 / 50 | 池名候选的返回面 |
   | `PoolCatalogCacheSeconds` | 60 | 候选框每敲一个字问一次上游；不缓存会把工作台的取数挤到限速之后 |
   | `FillsShownOnDetail` | 50 | 整条账不对外——那是点进工作台才有的东西 |
   | `LabelMaxLength` | 24 | 名称长度 |
   | `StreamIntervalSeconds` | 15 | 无数据帧时的保活 ping 间隔（数据推帧由事件驱动） |
   | `MaxStreamConnections` | 50 | 匿名 SSE 三类读路合计上限 |
   | `NavPointsOnLeadDetail` | 180 | 带单员详情只给最近这段净值曲线 |
   | `PositionsOnLeadDetail` | 6 | 带单员详情只列前几大持仓（按名义额） |

端点集合、状态码与请求形状由 `tests/PoolPilot.Api.Tests/PublicPagesApiTests.cs` 端到端钉住。
它们同时逐条登记在覆盖门禁的白名单里（`AuthorizationCoverageTests.OpenEndpoints`，
**9 个 REST + 1 条 SSE，共 10 条**，各附理由）——
白名单里多出"业务数据端点"是本仓库一个有意识的例外，例外成立的条件写在那张表的注释里。

## 三、创建者是谁：一枚访客 Cookie，库里只存派生哈希

公开跟单"只有创建者能删"。匿名访客没有账号，所以这里的"创建者"是一种**能力**，不是身份。

- 浏览器侧：`poolpilot_visitor` Cookie，值是 32 字节随机数的 64 位小写十六进制。
  HttpOnly、SameSite=Lax、`path=/api/public`、有效期 180 天。
  **只在第一次成功建跟单时签发**（纯浏览的人拿不到 Cookie），之后由服务端在响应里续期。
- 库侧（`followers` 表四条可空列）：

  | 建这行的人 | `IsPublic` | `OwnerUserId` | `OwnerCredentialHash` |
  |---|---|---|---|
  | 工作台里的账号 | `false` | null | null |
  | 公开页 · 未登录 | `true` | null | 派生值 |
  | 公开页 · 已登录 | `true` | 该账号 | null |

  派生值 = `lowerhex(SHA256(HMACSHA256(key, $"{visitorSecret}:{followerId}")))`。
  `key` 是常量 `poolpilot.follower-owner.v1`，它只做**域分隔**（防止 A 行的能力被搬到 B 行），
  不承担机密性——这个仓库是公开的。真正的机密只有访客密钥，它只存在于 Cookie：
  **不进库、不进任何响应体、不出现在 URL**。因此"源码泄露 + 拖库"仍然删不掉别人的行，
  库里那个哈希反推不出访客密钥。

- 删除判据（`src/PoolPilot.Domain/Services/PublicFollowerOwnership.cs`）：
  `OwnerUserId` 非空 → 比会话用户；否则 `OwnerCredentialHash` 非空 → 用本次请求的访客密钥现派生、
  与存库值**定长时间比较**（`CryptographicOperations.FixedTimeEquals`）；两者都空 → 不是公开页的行，404。

**为什么不给"删除口令/删除链接"**：令牌进过 URL 就进过浏览器历史、访问日志和 Referer；
可复制、可口头转述的东西不是一个能力。Cookie 由浏览器自动携带，前端拿不到它
（HttpOnly），也就不用把它存在任何地方。`SameSite=Lax` 顺带让别站的跨域 fetch 带不上它，
匿名删除因此不被 CSRF 白送。

**这条设计唯一真正用户可见的代价**：换浏览器、清 Cookie、隐私模式之后，
那条匿名跟单**自己再也删不掉**，只能由管理员在工作台处理。界面上写明了这件事。
不做"口令找回"的第二通道——那等于把高熵能力降级成一个可猜的口令。
访客后来注册也不会自动认领旧行（`OwnerCredentialHash` 与账号是两套判据，认领需要一个"证明你持有那枚 Cookie"
的流程，本批不做）。

## 四、公开面的数据裁剪

公开响应用**独立的窄 DTO**（`PublicPageDtos` + `PublicPageMapper`），而不是复用工作台的
`FollowerSummaryDto`。裁剪掉的正是不能给陌生人看的那几项：交易所配置名与 Id、实盘可用余额、
实盘持仓、任何 `Mode == Live` 的行。账的算法只有一份——公开用例调用的是工作台的同一套装配，
输出前才裁剪，所以"公开页的数字"与"工作台的数字"不会有第二套口径。

两条对外规则：

- 私有行与实盘行经公开端点一律 **404**（不是 403）。列表本来就是公开的，
  但"这条存在"这件事只对公开行成立。
- `GET /api/public/lead-traders` 里的订阅数与信号数是**全站口径**（含工作台里别人建的私有跟单）。
  留言板需要的是"这个池有多少人在跟"，而不是"谁在跟"。
- `GET /api/public/pool-options` 只列**未冻结**的池；按池名子串或**精确**账户索引搜索，
  返回 `accountIndex` / `name` / `totalAssetValue`，最多 20 条（`limit` 可到 50）。
  候选上会标出已有带单员与跟随数；选中已有的池只返回现状，不会打开或关闭它的开关。

## 五、实时：一条匿名 SSE，零轮询

公开页**不接 SignalR**。`/hubs/monitor` 要求登录且要求 `menu:graph` 的 `view` 才准入
（见 [PERMISSIONS.md](PERMISSIONS.md) 第八节），为一个留言板放开匿名订阅通道，
等于把全量广播送给没有身份的连接。公开页改为一条匿名 SSE：

```
GET /api/public/stream               → event: snapshot
  data: { leads, followers, takenAt }  建连即发；之后由池事件/落账/行情事件推帧；
                                        公开写成功后立即插一帧；无帧时每 15 秒 `: ping` 保活
GET /api/public/stream?detail=<id>   → event: detail（G3 的 DTO，逐键一致）
  行被删除/转私有 → event: gone { id }，客户端收到后关闭这条连接
GET /api/public/stream?lead=<index>  → event: lead（从池详情精选的公开投影）
  池不再是带单员 → event: gone { id }，客户端收到后关闭这条连接
```

- **并发上限 50**（`PublicPageLimits.MaxStreamConnections`，三条读路合计），超出在推流前回 503；
  长连接不挂 `public-read` 限流（会被一分钟 60 次的窗口误杀）。
- **在线人数与上游调用脱钩**：快照由一个进程级单例在事件到达时装配，所有订阅者共享；
  没有订阅者时不装配。这是"不能按每个访客各打一遍上游"的落点。
- **落账即时推帧**：纸面复制管线写完流水/权益点后，通过 `IFollowerLedgerActivity`
  通知 feed 立即重装配该行详情并刷新列表。
- **池事件即时推帧**：`LeadTraderRegistry` 每轮观察后通过 `ILeadActivity` 通知 feed；
  在线列表快照与打开着的 `?lead=` 详情一起重装配。池的净值/敞口来自上游 REST 快照，
  因此**不**由 `market_stats` 逐帧重算（与跟单员纸面账的盯市补丁是两条数据源）。
- **行情事件即时打补丁**：公开详情与列表卡片的持仓市场会订阅 Lighter `market_stats`；
  每条帧到达就按新中间价重算标记价/名义价值/未实现盈亏/权益并推帧，不读库、不重打上游。
  `IMarkPrices` 的缓存也被这些帧刷新，后续完整重装配直接拿最新价。
- **详情首帧不吃缓存**：快照是事件驱动的 live 状态；详情按 id 每次建连重新装配，
  不能拿旧账当实时首帧——否则用户会看到"建仓前"的 0 笔。
- **两片开关各自生效**：流是一条共享连接，但快照按 `PublicPages` 裁剪——关掉跟单页时
  `followers` 是空数组，`?detail=` 也回 404；关掉带单页时 `leads` 是空数组，
  `?lead=` 同样回 404（这一路自己看 `Leads`，不靠流聚合门）。
- **带单员详情是池详情的精选投影**：`?lead=` 给收益/风险（上报与实算并列）、
  规模与费用、敞口、前 6 大持仓（含开仓价、标记价、清算价、已实现/未实现盈亏与投资回报率）、
  这些持仓市场的挂单（用于持仓行展开的买卖阶梯）、近 180 个净值点。逐笔成交与运营者地址
  **刻意不取**——匿名观察需要的是"值不值得跟"，不是整本可被用来抢先交易的账。
- **归属不进共享帧**：snapshot/detail 是给所有订阅者的，装不下按 Cookie 裁剪的
  `isOwnedByCaller`。前端在页面打开时调用一次
  `GET /api/public/followers/ownership`（只回当前调用者的公开行 Id，不装配估值、不打上游），
  建/删后本地增删。**它不是轮询**：卡片与详情的数据仍然只来自 SSE。
- **状态读口**：`GET /api/public/status` 只回 `{ leads, followers, accountsEnabled }` 三个布尔，匿名、不挂开关、
  两片都关时也回 200。页面先问它一句，明确关掉时显示"未启用"，而不是让 EventSource
  永远停在"连接中"（EventSource 看不到 404 的响应体）。
- **零轮询**：前端没有 `usePolling`/`setInterval`，断线只由 `EventSource` 原生重连；
  数据只可能来自推送帧。

## 六、怎么打开看

```jsonc
// src/PoolPilot.Api/appsettings.json
"PublicPages": { "Leads": true, "Followers": true }
```

然后重启后端，浏览器进 `/public/leads` 与 `/public/follows`（前端 vite 已在 `/api` 上反代）。
**站点根路径 `/` 按身份分流**：未登录的人落在公开带单页（陌生人输入站点地址不该先看到登录墙），
已登录的人仍然落在工作台首页 `/graph`。判据在 `web/src/router/index.ts` 的
`PUBLIC_HOME_PATH` 与守卫的 `entry` 分支——`/` 刻意不用 `redirect`，因为 redirect 在守卫之前
就解析掉了，那一刻还没问过"是谁"。
系统还没初始化时这两页显示"系统尚未就绪"，不发请求。

本地开发**不改** `appsettings.json` 的默认值：`appsettings.Development.json` 把两片顶成 true
（只在 Development 生效）。生产默认两片都关时，匿名 `/` 仍落公开带单页，但页面会先读状态读口、
显示"公开带单页未启用"，不会永远停在"实时数据连接中…"。

## 七、对外开放之前

现在这套代码的匿名可写面，与仓库原有的"这是一台只监听本机的工作台"的前提**是冲突的**
（[AUTH.md](AUTH.md) 第六、七节）。真要把它放到公网，下面几项必须先处理，不是可选项：

- **TLS 与 Cookie 的 `Secure`**：`Cookie.SecurePolicy` 目前是 `None`（本机 http）。
  公网 http 下会话 Cookie 与访客 Cookie 都是明文过网。
- **库里的明文内容**：交易所 API 凭据、SMTP 口令、MFA 密钥都是明文落库
  （加密那套已按"密钥与密文同库等于没防"移除）。匿名可写的站点后面挂着这些东西，
  是这台工作台从"本机工具"变成"对外服务"时最需要先解的一件事。
- **单进程假设**：注册验证码、登录挑战与失败计数都在内存缓存里，多实例部署不成立。
- **限流的分区键**：按 `Connection.RemoteIpAddress`。反向代理下那是代理自己的地址——
  到时需要 `ForwardedHeaders` + 真实客户端 IP，否则限流会把所有陌生人当成同一个人。
- **注册面**：注册本来就开放，公开页并不改变它；但公开页上线后"陌生人能建账号"
  与"陌生人能建纸面跟单"同时成立，账号侧的角色分配规则要复核。
- **SSE 的连接额度是全局的**：50 条上限不按来源分；一个来源可以把额度占满。
  真要对公网，至少再加一条按来源的连接数上限，或把上限调到与预期并发匹配。

## 八、已知边界（刻意不做）

- 匿名行不做口令找回、不做注册后认领（理由见第三节）。
- 公开侧不能改名、不能改本金、不能启停；留言板只有"建"和"自己的那条能删"。
- 带单页的池名候选走公开端点，工作台侧的池列表/详情/回测/规模门槛一律不放开
  （一次回测是一串真实上游请求，匿名可触发等于把外部成本挂在陌生人身上）。
- 公开行仍然进的是同一张 `followers` 表、同一套纸面账管线：管理员在工作台看得到它们，
  也能暂停或删除它们——"只有创建者能删"是公开页的规则，不是"这行连系统主人都管不了"。
