# 权限（菜单、角色与 Casbin）

本文说明这套工作台的授权模型：权限点是什么、策略怎么从表里算出来、什么时候重算、
以及超级管理员的权限从哪来。实现落在 `PoolPilot.Infrastructure/Authorization`，
判定抽象是 `PoolPilot.Application.Abstractions.IPermissionEvaluator`。

## 一、权限点的粒度与命名

- **粒度 = 菜单 + 动作**。动作只有三个：`view` / `edit` / `delete`。能看、能改、能删分开判，
  界面因此可以只藏"删"这一个按钮，而不是整页藏掉。
- **菜单是数据，不是代码常量**。菜单存在 `menus` 表里，加一个页面、挪一次层级都是写一行。
- **但内置菜单的 `code` 是常量**：改不了、删不掉（守卫在 `UpdateMenuUseCase` / `DeleteMenuUseCase`，
  判据来自 `SeedAuthorizationUseCase.BuiltInMenuCodes`）。因为权限键由 `code` 生成、前端视图注册表
  也按 `code` 匹配——改名或删除会让那一页对除超管外的所有人失效，且重启不会自愈。
  其余字段（标题 / 图标 / 排序 / 层级 / 可见性 / 动作）照旧可配；要"停用"一个内置页面，
  把「进导航」关掉即可（路由与权限键留在库里）。
- **权限键**（Casbin 里的对象）：`menu:{code}`，例如 `menu:system-menus`。
  它由服务端按菜单 `code` 生成（`menus.permission_key` 列），**不接受客户端传入**——
  让客户端传就会出现"code 与权限键对不上"的状态，而那种错位不会被任何断言发现。
- **角色主体**：`role:{code}`（如 `role:superadmin`）。
- **用户主体**：`user:{id}`，`id` 是 `users` 表主键。

## 二、Casbin 模型

```
[request_definition]
r = sub, obj, act
[policy_definition]
p = sub, obj, act
[role_definition]
g = _, _
[policy_effect]
e = some(where (p.eft == allow))
[matchers]
m = g(r.sub, "role:superadmin") || (g(r.sub, p.sub) && r.obj == p.obj && r.act == p.act)
```

三条刻意的约束：

1. **matcher 只有两段**：超管短路（`g(r.sub, "role:superadmin")`）+ 精确匹配的普通判定。
   除此之外没有通配符、没有属性表达式——**除超管外**，"谁能做什么"永远等价于
   "策略表里有没有这一行"，一句 `SELECT` 就能回答；超管那一段在第四节单独说明。
2. **`g` 是唯一的一跳角色继承**：`user:{id} → role:{code}`。
   角色不下挂角色（不做角色层级），需要"多个角色的并集"时给用户绑多个角色即可。
3. **`p` 的行是"角色 → 菜单 → 动作"三元组**，逐条显式，且**只为真实授权出行**
   （超管的行不落表）。

## 三、事实源与策略投影

**没有 `casbin_rule` 表**。策略的唯一事实源是四张业务表，Enforcer 由它们**算出来**：

| 表 | 决定 |
|---|---|
| `menus` + `menu_actions` | 有哪些菜单、每个菜单暴露哪些动作 |
| `menu_roles` | 每个菜单被授予了哪些角色（`p` 行的来源） |
| `user_roles` | 每个用户拥有哪些角色（`g` 行的来源） |
| `roles` | 角色标识与显示名 |

投影规则（`PoolPilot.Domain.Services.PermissionPolicyProjection`，纯计算、不碰库也不碰 Casbin）：

```
g: 每个 (user, role) 绑定          → user:{id}  →  role:{code}
p: 菜单绑定的每个角色 × 该菜单的每个动作 → role:{code} → menu:{code} → 动作
   （没有绑定就没有行；superadmin 永远不落行）
```

## 四、超级管理员的授权来源

超级管理员的特权**不在策略集里**，而是模型里的一小段 matcher 短路：

```
m = g(r.sub, "role:superadmin") || (g(r.sub, p.sub) && r.obj == p.obj && r.act == p.act)
```

四个要点：

- **主语必须走 `g()`**，不能写 `r.sub == "role:superadmin"`：请求里的 `sub` 是 `user:{id}`，
  超管身份存在 `g` 行里，直接比字符串对真实用户永远为假（这是实测踩过的坑）。
- **策略集里因此一行超管行都没有**：`p` 只装真实授权（角色↔菜单），
  一句 `SELECT` 就能看出"谁被真的授过权"，不必从一大片"每人都有"的行里挑。
- **超管的清单照样可读**：`GET /api/auth/permissions` 与 `/api/menus/tree` 不穷举策略行，
  而是拿"全部菜单 × 各自动作"当候选**逐项问 matcher**（见 `CasbinPermissionEvaluator`），
  短路口径因此被如实展开——列出什么，就是判得过什么。
- **解绑即失效**：把超管的 `user_roles` 行删掉，短路立即不再命中（它依赖那条 `g` 行）。

`superadmin` 是唯一的系统角色（`roles.is_system = 1`），不可删。
它**不落在 `menu_roles` 里**：`GET/PUT /api/admin/menus/{id}/roles` 里它常驻显示（排在最前），
但写入时会被忽略、投影也不为它出行——"取消超管的访问权"这件事在结构上做不到。

## 五、重建时机与并发

- **单一写入口**：`IPolicyWriter.RebuildAsync`。菜单、角色、两张绑定表发生任何变化之后，
  用例都必须经过它把 Enforcer 从表**整体重建**一次，而不是增量改几条策略。
  增量维护要在十几处写点上各自记得改对，漏一处就留下一条没人看得见的过期策略。
- **整体重建意味着永远与表一致**：读的是表本身，不是"这次改动影响了哪几条"。
- **先建后换，读者看不到半加载**：每次重建都是**全新构造**一个 Enforcer，构造完成后
  在锁内一次引用交换。读者要么拿到旧的完整实例、要么拿到新的完整实例。
- **判定缓存按请求**：`IPermissionEvaluator` 在一次请求内缓存该用户的全部授权
  （菜单树过滤与按钮判定会反复问同一件事）。跨请求不缓存，所以写操作后的下一次请求
  自然读到重建过的策略——改一次角色绑定立刻生效，不需要重新登录。

## 六、首个账号与防锁死

- **首个账号自动成为超级管理员**：注册成功时若它是全库唯一的账号，就绑上 `superadmin`。
  判据只有一处（`SuperadminBootstrap`），注册路径与启动播种共用。
- **之后的账号绑定「注册默认角色」**（如果角色管理里指定了）：默认角色只影响之后注册的账号，
  不改已有绑定；全库最多一个，换一个默认会自动清掉旧的。没有指定时注册账号仍然无角色——
  注册保持开放，但开放不等于送权限。
- **系统角色不能设为注册默认**（尤其 `superadmin`：那等于让每个注册者直接拿到全部权限）；
  注册默认角色也不能直接删除，先取消默认。默认角色被绑定后同样**立刻重建策略**，
  否则新账号登录后看得到角色名却没有任何权限。
- **启动播种**：`roles` 里没有 `superadmin` 就建；`menus` 为空就播种内置菜单；
  库里恰有一个账号且它没有任何角色时，把它绑成 `superadmin` 并打 `LogWarning`——
  否则那个账号连「菜单管理」都打不开，系统再也回不到可配置状态。
- 第一个账号绑上超管后会**立刻重建**一次策略；少了它，刚注册完的账号要等下一次写操作
  才真正拿到权限（表现为"什么都没改却进不去菜单管理"）。

## 七、数据隔离：权限之外的第二道边界

权限决定"能进哪个页面"，数据归属决定"能看哪些行"。工作台目前把两类资源按用户隔离：

| 资源 | 归属规则 |
|---|---|
| 跟单员（含流水、权益曲线、实盘流水） | 创建者私有；普通账号只列/读/改/删自己的，superadmin 可跨用户 |
| 交易所配置（含凭据） | 创建者私有；普通账号只列/读/改/删/探测自己的，superadmin 可跨用户 |

具体口径：

- `followers.owner_user_id` 同时承载**工作台私有归属**与公开页登录创建者的归属；
  工作台只列 `is_public = false` 的行，公开行即使创建者是当前用户也不混进来。
- 按 Id 的私有端点先过归属守卫；不属于自己的行与不存在回同一个 404，不把 Id 变成探针。
- 迁移前没有 owner 的老行只对 superadmin 可见——没有归属证据就不该默认分给某个人。
  升级后确认归属再用 `UPDATE followers SET OwnerUserId=<用户 Id> WHERE ...` /
  `UPDATE exchange_configs SET OwnerUserId=<用户 Id> WHERE ...` 回填；不替使用者猜。
- **共享数据不隔离**：带单观察、池与行情、标的映射、通知配置、角色/用户/菜单。
  带单列表上的跟随数：工作台按当前用户自己的跟单计数，公开页沿用全量计数。
- 这不是加密，也不是数据库行级安全：能直接读 SQLite 文件的人仍然看得到全部数据；
  隔离挡的是应用层不同登录账号之间的越权。

## 八、已经接入强制的接口

**后端强制现在是全量的**：除下面「白名单」一节列出的匿名与"登录即可"端点外，
每个 MVC action 都挂了权限门。这条不靠人盯——有一条**覆盖门禁测试**兜底（见后文）。

### 「系统」三个管理页

- 「菜单管理」（`menu:system-menus`）：`GET /api/admin/menus`（`view`，**例外，见下**）、
  `POST/PUT /api/admin/menus*`（`edit`）、`DELETE /api/admin/menus/{id}`（`delete`）、
  `GET /api/admin/menus/{id}/roles`（`view`）、`PUT /api/admin/menus/{id}/roles`（`edit`）。
- 「用户管理」（`menu:system-users`）：`GET /api/admin/users`（`view`）、
  `PUT /api/admin/users/{id}/roles`（`edit`）。
- 「角色管理」（`menu:system-roles`）：`GET /api/admin/roles`（`view`）、
  `PUT /api/admin/roles/{code}`（`edit`）、`DELETE /api/admin/roles/{code}`（`delete`）、
  `PUT/DELETE /api/admin/roles/{code}/registration-default`（`edit`——指定/取消注册默认角色）、
  `GET /api/admin/roles/{code}/menus`（`view`）、`PUT /api/admin/roles/{code}/menus`（`edit`）、
  `POST /api/roles`（`edit`——角色是谁建的这件事属于「角色管理」，不挂在菜单管理上）。

### 「或」权限：`GET /api/admin/menus`

`GET /api/admin/menus` 既服务「菜单管理」页（`menu:system-menus`），也服务「角色管理」页的
"配置菜单"弹窗（`menu:system-roles`）——两个页面的读写面各自独立，却要读同一份菜单清单。
只挂其中任意一个都会让另一个页面开不了这个弹窗，所以用 `RequireAnyPermissionAttribute`
（参数按「键, 动作」交替传入，命中任一即放行）明确表达"**菜单管理 view 或 角色管理 view** 皆可"。

这是全站唯一的"或"端点；`POST /api/roles` 归「角色管理」不变（持有 `menu:system-menus`
的账号在菜单管理页里点"新建角色"仍会拿到 403——角色创建归属「角色管理」是刻意的，
不是遗漏）。超管由 matcher 短路，不受影响。

### 业务端点（本批 42 个）

- **「总览」**（`menu:graph`）：`GET /api/monitor/events`、`GET /api/monitor/watch-state`（`view`）。
- **「池列表」**（`menu:pools`）：`GET /api/pools`、`GET /api/pools/{accountIndex}`（均 `view`）。
- **「带单管理」**（`menu:leads`）：`GET /api/lead-traders`、`GET /api/lead-traders/{accountIndex}`（`view`）；
  `PUT /api/lead-traders/{accountIndex}`（`edit`）；`DELETE /api/lead-traders/{accountIndex}`（`delete`）。
  **暂停/恢复/停止是共享观察对象的管理动作，只许 superadmin**：拥有 edit/delete 的普通角色
  新建观察仍然可以，但改开关与删除会被用例拒绝（菜单权限之外的第二道判定）。
- **「跟单管理」**（`menu:copy-list`）：`GET /api/followers`、`GET /api/followers/{id}`、
  `GET /api/followers/{id}/live-account`、`GET /api/followers/{id}/live-orders`（`view`）；
  `POST /api/followers`、`PUT /api/followers/{id}`、`POST /api/followers/{id}/live-positions/close`
  （`edit`，最后一处是**真实平仓**）；`DELETE /api/followers/{id}`（`delete`）。
  另有 `GET /api/monitor/ledger/{accountIndex}` 也挂这里（`view`，已按前端调用点核实只被跟单详情页用）。
- **「交易所」**（`menu:exchanges`）：`GET /api/exchanges`、`GET /api/exchanges/{id}/symbol-map`（`view`）；
  `POST /api/exchanges`、`PUT /api/exchanges/{id}`、
  `POST /api/exchanges/live-order/plan`（`edit`——下单规划是**对外请求**）；
  `DELETE /api/exchanges/{id}`（`delete`——全站统一的"删=delete"）。
  `POST /api/exchanges/{id}/probe` 例外：它是「跟单管理」页新建实盘时**自动**调用去读可用保证金的，
  用手工探测当门槛会连带把跟单流程掐掉——因此它要 `menu:exchanges` **或** `menu:copy-list` 的 `edit`
  （`RequireAnyPermissionAttribute`，见下）。
- **「标的映射」**（`menu:symbol-mappings`）：`GET platforms`、`GET source-symbols`、`GET symbols`、
  `GET /api/symbol-mappings`（`view`）；`POST scan`、`POST /api/symbol-mappings`（`edit`）；
  `DELETE /api/symbol-mappings/{id}`（`delete`）。
- **「规模门槛」**（`menu:feasibility`）：`GET /api/feasibility/{accountIndex}`、
  `GET /api/markets/{marketId}/quote`（`view`）。
- **「回测面板」**（`menu:backtest`）：`GET /api/backtest/{accountIndex}`（`edit`——跑一次回测是一次对外取数，
  口径已定，故不是 `view`）。
- **「通知」**（`menu:notifications`）：`GET providers`、`GET templates`、`GET logs`（`view`）；
  `POST providers`、`PUT providers/{id}`、`POST providers/{id}/test`、`PUT templates/{purpose}`（`edit`）；
  `DELETE providers/{id}`（`delete`）。

**详情接口一律挂父菜单**（已定口径）：`/api/pools/{index}` → 「池列表」、`/api/followers/{id}*` → 「跟单管理」，
不做"只有详情权限"的组合。

### 白名单（匿名 / 登录即可）

只有两类端点不加权限门，且逐条有理由（覆盖测试里以「控制器.方法名」显式列出）：

- **匿名**：注册 / 登录 / MFA 三步 / 登出（`AuthController` 里标了 `AllowAnonymous` 的那几个），
  以及首次初始化 `api/setup/*` 三个（初始化前系统里没有任何账号，不开口子就永远进不来；
  初始化完成后写接口即回 400 失效）。健康检查 `GET /api/health` 是 minimal API、不在控制器扫描范围内，
  也刻意匿名（登录页要能判断后端起没起来）。
- **登录即可**：自省类端点——`GET /api/auth/me`、`GET /api/auth/permissions`、`GET /api/menus/tree`、
  `GET /api/roles`。它们回答"我是谁 / 我能做什么 / 我看到哪些菜单"，不承载业务数据；
  没有角色的账号拿到的正是空树、空清单。
- **匿名公开面**：`/api/public/*` 的 10 条端点（9 个 REST + 1 条 SSE）。它们是**业务数据端点**，
  出现在这张表里是本仓库一个有意识的例外，理由与代价写在
  [PUBLIC-PAGES.md](PUBLIC-PAGES.md) 与覆盖测试里那张表的注释中。

- 未登录 → **401**（Cookie 处理器的 `OnRedirectToLogin`，JSON `{ error }`）；
  登录了但没权限 → **403**（权限属性返回 `ForbidResult`，走 `OnRedirectToAccessDenied`，
  形状与前者一致，文案带一句"联系管理员在「用户管理」里分配菜单权限"的引导）。

`/api/menus/tree` 只返回当前用户拥有 `view` 的节点——只在前端藏起来不够，地址栏照样进得去，
所以过滤发生在服务端。节点的 `actions` 是"我在这个菜单上能做什么"，界面据此决定按钮是否可点。

### 公开面为什么不在这套模型里

`/api/public/*`（公开带单页与公开跟单页）**不挂权限点、不进菜单表**。
这套模型回答的是"工作台里的这个人能不能做这件事"，而匿名访客不是一个主体：
给他一个键，要么所有人都有（等于没有），要么先给他一个身份（他就不是匿名访客了）。

顶在那里的三道闸是功能开关（`PublicPages`，源码默认关、deb 包默认开，关掉时回 404）、数量上限
（`PoolPilot.Domain.Constants.PublicPageLimits`，代码常量）与按来源地址的限流。
"创建者能删自己那条公开跟单"用的也不是角色，而是一枚访客 Cookie 派生出的能力
（库里只存哈希）。整套设计与对外部署的前提见 [PUBLIC-PAGES.md](PUBLIC-PAGES.md)。

### 覆盖门禁测试（新端点漏网会红）

`tests/PoolPilot.Api.Tests/AuthorizationCoverageTests.cs` 反射扫 `PoolPilot.Api` 程序集里的**全部**
MVC action，逐个要求"挂了 `RequirePermission` / `RequireAnyPermission`（方法级或类级），
或在白名单里"。白名单写在测试代码里、每条附一句"为什么它可以敞开"；另有反向断言
"白名单条目必须对得上真实 action"（防止端点了删改后白名单烂掉）。
**新加端点忘记加门 = 这条测试变红**，这是"全量强制"这句话的可执行形式。

端到端的三态（401 / 403 / 200）另有三组用例：
`BusinessEndpointAuthorizationTests`（零角色账号对本批 42 个端点逐条 403 + 超管抽查 200）、
`AuthApiTests`（匿名 401）、`MenuApiTests` / `AdminUserRoleApiTests`（管理面 403/200 与策略重建）。

### SignalR hub 准入

`/hubs/monitor` 不再只是"登录即可连"。监控推送的内容是**全量广播**（`Clients.All`，不按收件人裁剪），
所以在 `MonitorHub.OnConnectedAsync` 里加一道 `IPermissionEvaluator.Has("menu:graph", "view")` 判定：
不通过就 `Context.Abort()`（并打一条日志说明被谁拒的）。**拒绝连接是唯一能真正挡住内容的做法**——
逐条过滤在这里不成立，因为推送本身就不认识收件人。分支由
`MonitorHubAuthorizationTests` 覆盖（通过者照常补发状态、被拒者收不到任何推送）。

`/hubs/followers`（工作台跟单模块）同理，门是 `menu:copy-list` 的 `view`：
账本变化帧仍发全体，行情帧按市场分组，但准入漏掉的后果不是"多收几个价格"，
而是任何登录用户都能收到账本变化信号并知道有哪些跟单员在动。`FollowerHubTests`
覆盖准入、市场入组与"入组时必须同时确保上游 `market_stats` 订阅"。
