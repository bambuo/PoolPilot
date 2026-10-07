# CODING_STANDARDS.md — PoolPilot 代码规范（C# / .NET 10）

> 本文件是 `AGENTS.md` 的落地细则。所有代码必须遵守本规范。
> 冲突时：本规范优先于个人习惯；`AGENTS.md` 优先于本规范。

---

## 1. 架构规范（DDD + 整洁架构）

### 1.1 分层与依赖规则

```
Domain（领域） ← Application（应用） ← Infrastructure（基础设施） ← Api（表现层）
```

- 依赖箭头指向内层：外层可引用内层，**内层禁止引用外层**（依赖倒置）。
- `PoolPilot.Domain` 零外部依赖（纯 C#，不引用 EF Core / ASP.NET Core / 任何 NuGet 包）。
- `PoolPilot.Application` 只引用 Domain；`PoolPilot.Infrastructure` 引用 Domain + Application；`PoolPilot.Api` 引用 Application + Infrastructure（组合根）。
- 检查方式：csproj 引用关系 + code review 抽查 using；违反即返工。禁止循环依赖。

### 1.2 各项目职责

| 项目 | 职责 | 禁止 |
|---|---|---|
| **PoolPilot.Domain** | 实体（`Pool`/`LeadTrader`/`Follower`/`PoolEvent`/`PaperFill`…）、值对象（`AccountIndex`/`MarginFraction`/`LighterTimestamp`/`UsdAmount`…）、领域服务（`NavAnalyzer`/`PositionDiffer`/`PaperLedger`/`CopySlippageCalculator`…）、跟单契约（`LeadSignal`/`IFollower`）、仓储接口、协议常量 | 引用任何外部包；EF/ASP.NET Core 类型；HTTP/JSON 解析 |
| **PoolPilot.Application** | 用例（`GetPoolList`/`GetPoolDetail`/`EnsureLeadTrader`/`ListLeadTraders`/`ConfigureLeadTrader`/`CreateFollower`/`GetFollower`/`ListFollowers`/`UpdateFollower`/`DeleteFollower`/`GetCopyLedger`/`RunBacktest`/`EvaluateSizeFeasibility`）、DTO、映射、对外部能力的端口（`ILighterPoolsClient`/`IMarkPrices`/`ILeadWatchState`/`ILeadTraderSync`） | 业务规则散落；直接操作 SQL；依赖 EF；依赖 HttpClient |
| **PoolPilot.Infrastructure** | Lighter REST 客户端、Lighter WebSocket 客户端、EF Core DbContext 与仓储实现、带单员运行时（`LeadTraderRegistry`/`LeadTraderRuntime`/`PaperFollower`）、常驻 HostedService | 业务决策；领域逻辑；对外响应契约 |
| **PoolPilot.Api** | MVC Controllers（薄壳）、SignalR Hub、异常中间件、DI 组合根（`Program.cs`） | 业务逻辑；直接查询数据库 |

### 1.3 目录规范

- 每层一个项目（`src/PoolPilot.*`），按职责建子目录：`Entities/`、`ValueObjects/`、`DomainServices/`、`Repositories/`、`Constants/`（Domain）；`UseCases/`、`Dtos/`、`Mapping/`、`Abstractions/`（Application）；`Lighter/`、`Persistence/`、`Monitoring/`（Infrastructure）；`Controllers/`、`Hubs/`、`Middleware/`（Api）。
- 文件名与类名一致；一个文件一个公开类型（小 DTO 可同文件多个 record）。

---

## 2. 设计模式约定

- **Repository（仓储）**：接口定义在 Domain，实现在 Infrastructure（EF Core）。
- **Dependency Injection**：构造器注入（primary constructor）；组合根在 Api 层 `Program.cs`。
- **Domain Service（领域服务）**：跨实体、无状态的纯计算逻辑。
- **Strategy（策略）**：跟单信号解析、撮合定价等多个可替换实现。
- **MVC**：Controllers 薄 + DTO（不暴露实体）；错误统一由 `ExceptionHandlingMiddleware` 映射（404/400/500）。

---

## 3. C# 语言规范（C# 14 / net10.0）

- `Nullable` 启用、`ImplicitUsings` 启用；`TreatWarningsAsErrors` 开启。
- 类型选择：DTO 用 `record`（不可变 + 位置参数）；实体用 `sealed class`；无继承意图一律 `sealed`。
- 命名：类型/方法 PascalCase、局部变量 camelCase、私有字段 `_camelCase`、异步方法 `Async` 后缀、接口 `I` 前缀。
- 语法偏好：
  - primary constructors（类/record 直接声明依赖）
  - collection expressions：`[]`、`[a, b]`
  - raw string literals：多行 JSON / SQL 用 `"""..."""`
  - `required` 成员（实体必填属性）
- 禁止：`var` 滥用（仅类型明显时）、`dynamic`、吞异常、字符串拼接、魔法数字。

### 3.1 索引与数值类型（本项目的硬约束）

- **账户索引一律 `long`**：池索引形如 `281474976552443`，**远超 int32 上限**，用 `int` 会静默溢出成负数。
- 金额、价格一经从 Lighter 解析即转为 `decimal`，不用 `double` 做货币运算。
- 涉及比例（保证金率、仓位占比）用 `decimal`；统计类计算（夏普、波动率）可用 `double`。

---

## 4. 测试规范（MSTest，测试兜底）

- 四个测试项目对应四层（`Domain.Tests` / `Application.Tests` / `Api.Tests` / `Lighter.Tests`）。
- 命名：`Method_Scenario_ExpectedBehavior`（如 `Parse_WithMicrosecondTimestamp_ReturnsUtcInstant`）。
- 结构：AAA（Arrange-Act-Assert）；只断言行为，不断言实现细节（序列化格式等脆弱断言用 `JsonDocument` 解析验证）。
- 断言陷阱：`Assert.IsGreaterThan(lowerBound, value)`——**第一个参数是下界**，断言 `value > lowerBound`。
- 单元测试用 fake 仓储/假客户端（实现接口，不用 mock 框架）；API 集成测试用 `WebApplicationFactory` + 临时 SQLite 文件 + 种子数据。
- 真实接口契约测试（`PoolPilot.Lighter.Tests`）标记 `[TestCategory("Live")]`，日常门禁排除：
  ```
  dotnet test -m:1 --filter "TestCategory!=Live"
  ```

### 4.1 字段陷阱必须逐条断言

**领域层最核心的一类测试**：`docs/LIGHTER-API-NOTES.md` 的「字段陷阱清单」每一条，都必须在 `PoolPilot.Domain.Tests` 里有对应断言。这不是形式主义——这些陷阱全部是实测踩出来的，单位写错一位就会让全部计算失效。

---

## 5. 数据访问规范（EF Core 10 + SQLite）

- Code-First，建库用 `EnsureCreated`（不引入迁移工具链，见 `AGENTS.md` 铁律 8）。
- 仓储不向领域层泄露 `IQueryable`；领域层只看到 `Task<T>` / `IReadOnlyList<T>`。
- 大写入批量事务（5000 条/批）；查询避免 N+1（批量 `GetByIdsAsync`）。
- **JSON 列**（如 `PoolOrder` 的原始载荷快照）：实体属性用强类型，EF 侧用 `ValueConverter` 序列化为 JSON 文本，避免为上游的每个可选字段建列。
- 时间戳统一以 `LighterTimestamp` 值对象入库为 UTC；**不做本地时区转换**（比较逻辑全在 UTC 下进行）。

---

## 6. 错误处理与日志

- 领域异常（`PoolNotFoundException` 等）→ 中间件映射：404（未找到）、400（参数错误）、500（未预期）。
- 异常不吞：中间件记录完整堆栈（`fail:` 级别），对外只返回 `{"error": "..."}`。
- **上游接口失败必须显式暴露**：Lighter 的 HTTP 200 与业务成功不等价（见 `LIGHTER-API-NOTES.md`），响应信封的 `code` 非 200 时按失败处理，不得静默返回空结果。
- WebSocket 断连、订阅失败、限速（429/405）都要留可观测的日志，并区分「机制故障」与「上游无数据」——这两者在本项目里是完全不同的结论。

---

## 7. 前端规范（Vue 3 + Arco Design Vue）

### 7.1 语义色必须写成 `rgb(var(--danger-6))`

Arco 的颜色变量分两类，名字只差一个前缀，混用会**静默失效**——元素照常渲染，
只是颜色变成"没有"，不报错、不警告：

| 类别 | 变量名式样 | 值的形式 | 用法 |
|---|---|---|---|
| 中性色 | `--color-text-1`、`--color-border-1`、`--color-fill-2`、`--color-white` | 完整颜色 `rgb(29,33,41)` | `var(--color-text-1)` |
| 语义色 | `--primary-6`、`--success-6`、`--danger-6`、`--warning-6` | **裸 RGB 三元组** `22,93,255` | `rgb(var(--primary-6))` |

即：**语义色没有 `color-` 前缀，且必须套一层 `rgb()`**。
写 `var(--color-danger-6)` 不会报错，只会得到空值——曾因此在整站范围内让涨跌色、
图表折线、阶梯条形全部不着色（折线尤其隐蔽：DOM 里路径存在，屏幕上什么都没有）。
判定方式：`getComputedStyle(el).stroke` 返回 `none` 即变量没解析。

### 7.2 数值格式化只允许一份

所有 K/M 缩写、百分数、基点、时间格式化都在 `src/utils/format.ts`，组件不得自己 `toFixed`。
理由：同一个数在两个组件里用不同精度显示，会让人以为是两个不同的量。

### 7.3 图表用实测尺寸，不用固定 viewBox

手写 SVG 图表时，`viewBox` 固定尺寸 + `width:100%` 会按**宽高比**缩放，
`height` 参数随即失效（要 280px 会渲染成 430px），字号也被一起放大。
正确做法是用 `ResizeObserver` 实测容器宽度，让 1 个 viewBox 单位 = 1 个 CSS 像素。
纵轴刻度是右对齐的，左侧留白必须按**最长标签的实际宽度**推算，固定值会把 8 位数字
的开头裁掉（刻度 `0.0016647` 显示成 `.0016647`）。

### 7.4 页面必须能区分"没有数据"与"数据通道断了"

依赖推送的界面不能只渲染事件流。实测池的活动率只有市场成交的约 0.24%，
长时间无事件是正常的；只显示事件流会让使用者把正常运行误判成故障。
状态必须显式呈现。带单员页因此把**配置状态**与**此刻是否在收推送**分成两列：
`isEnabled=true, isWatching=false` 是"已启用但连接没到位"，与"我自己暂停的"长得完全不同，
合成一个"状态"列会把这个区别抹掉。

配套的后端约定：状态只在**变化**时广播，因此 Hub 必须在客户端连上时补发一次当前状态
（`MonitorHub.OnConnectedAsync`），否则晚于所有变化才打开的页面永远停在"未知"。
另外状态的 `LastMessageAt` 必须填"最近一次收到上游消息"的时刻，而不是"状态生成时间"——
填后者会让静默计时器每次广播都清零，恰好掩盖它本该暴露的问题。

### 7.5 不依赖 Arco 组件的"默认外观"，它可能根本没有默认值

Arco 有两处组件**只给骨架不给外观**，照直觉用会得到一个看起来"没写"的界面：

| 组件 | 实际提供的 CSS | 后果 |
|---|---|---|
| `a-layout-header` | 只有 `flex: 0 0 auto; box-sizing; margin`，**没有高度也没有背景** | 塌成内容高度（约 20px）、背景透明；配上浅色文字就是一条**看不见的导航**——比没有导航更糟 |
| `a-link` | 渲染 `<a>` 但**不生成 `href`** | 不能键盘聚焦、不能新标签页打开、对辅助技术不可见；按 `role=link` 也定位不到 |

规则：**导航/顶栏这类骨架元素，高度与配色一律显式写出**，不依赖框架默认值。
**站内跳转一律用 `RouterLink`**（生成真 `href`），只在纯装饰性文本时才用 `a-link`。

判定方法：`getComputedStyle(el).height` 是否符合预期、`el.getAttribute('href')` 是否为空。

补充：**小图标一律用 `@arco-design/web-vue/es/icon` 的官方图标**（如 `IconQuestionCircle`），
不要用字符加 CSS 手造图形——官方图标与组件库的字号、颜色体系（currentColor）天然一致。

### 7.6 菜单高亮不要依赖 `router-link-active`

`router-link-active` 的包含式匹配取决于**路由表的嵌套结构**。平级的兄弟路由
（如 `/pools` 与 `/pools/:accountIndex`）之间匹配不到，会出现"进了详情页菜单反而全灭"的现象。

正确做法是在外壳组件里显式判定，把规则写在能读到的位置：

```ts
function isActive(item: NavItem): boolean {
  return item.hasChildren
    ? route.path === item.to || route.path.startsWith(`${item.to}/`)
    : route.path === item.to
}
```

同理，**边线类的 1~2px 溢出**是真实缺陷（导航条 56px 而链接 58px 撑出去 2px），
`height: 100%` 配 `border-bottom` 时必须带 `box-sizing: border-box`。

### 7.7 原始协议标识不直接面向用户

账户索引（15 位数字）、频道串（`account_all_positions/281…`）、状态码（`first_connect`）
这类标识是**给机器对账用的**，直接渲染成界面文本等于把调试信息当成了产品文案。
用户读到的是"这串数字和我有什么关系"。

规则：展示层必须建立「标识 → 人话」的映射（池名、频道用途、状态含义），
原始标识降级为次要信息——缩小弱化、或放进悬浮提示保留精确性。

本项目的落点：`MonitorView.vue` 的 `poolNameOf` / `channelLabel` / `backendStateLabel`。
池名由前端从池列表解析（历史事件 DTO 里池名为空串，只有实时推送带名字，
前端映射让两条路径显示一致），后端不为显示再做一次全量查询。

配套规则：**新增 DTO 字段时检查它在所有产出路径上是否都有值**。
`PoolEventDto.PoolName` 曾只在 SignalR 推送路径填值、REST 历史路径恒为空串——
同一个字段两条路径两种质量，使用者会把空串当成"没有名字"。

### 7.8 Arco Tag 的 `color` 只认预设色名，传语义名会静默失效

`a-tag` 的 `color` 与 `a-alert`/`a-button` 的 `type` **不是同一套取值**：

| 组件 | 可用值 |
|---|---|
| `a-tag` `color` | 预设色名：`red` `orangered` `orange` `gold` `lime` `green` `cyan` `blue` `arcoblue` `purple` `pinkpurple` `magenta` `gray`（或自定义色值） |
| `a-alert` / `a-button` `type` | 语义名：`success` `warning` `danger` `error` `info` |

给 Tag 传语义名的后果是**静默失效**：Arco 当成"自定义色"，套上 `arco-tag-custom-color`，
最终渲染成 **白字 + 近白底**（实测文字 `rgb(255,255,255)` / 背景 `rgb(242,243,245)`），
在亮色主题下几乎不可读，而控制台不报任何错。

规则：**凡是驱动 `a-tag` 颜色的变量/函数，其取值域一律写"预设色名"那一套**。
字段名也别叫 `type`——那会诱导后人填语义名；叫 `color`。
判定方法：`getComputedStyle(el).color === 'rgb(255, 255, 255)'` 且背景近白，即是踩了这条。
