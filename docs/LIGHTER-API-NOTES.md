# LIGHTER-API-NOTES.md — Lighter 公开接口实测笔记

> **本文件是本项目领域模型的唯一依据。**
> 全部内容来自对 Lighter 主网真实接口的实测（2026-10-05），不是官方文档转述。
> 官方文档与实测冲突时，以本文件为准，并在「六、文档与实测的矛盾」留痕。
>
> 铁律：任何新用到的字段，先在此登记其**类型、单位、刻度**，再写进领域模型；
> 登记过的每一条都必须在 `PoolPilot.Domain.Tests` 里有对应断言。

---

## 一、端点与主机名

```
REST base     https://mainnet.zklighter.elliot.ai/api/v1
WebSocket     wss://mainnet.zklighter.elliot.ai/stream
只读 WS        wss://mainnet.zklighter.elliot.ai/stream?readonly=true   （受限地区用）
签名 chainId  304（主网） / 300（测试网）
L1 合约        0x3B4D794a66304F130a4Db8F2551B0070dfCf5ca7
```

**主机名陷阱**：以下主机**没有 DNS 解析，不要使用**（实测 `dig` 全部为空）：

```
api.lighter.xyz              ❌
mainnet.zklighter.xyz        ❌
explorer.zklighter.xyz       ❌
```

正确域名是 **`mainnet.zklighter.elliot.ai`**（`elliot.ai`，不是 `lighter.xyz`）。

---

## 二、免鉴权接口清单

以下 8 个接口实测**无需任何鉴权**即可调用。响应统一包一层信封（`code` 字段），
**HTTP 200 与业务成功不一定同时成立，必须判 `code`**。

| 方法 | 路径 | 用途 | 信封容器字段 |
|---|---|---|---|
| GET | `/systemConfig` | 系统配置、各类特殊账户索引 | 字段直接平铺 |
| GET | `/publicPoolsMetadata` | 公共资金池元数据（可分页枚举） | `public_pools[]` |
| GET | `/account` | 单账户余额/抵押/持仓/池份额 | `total`, `accounts[]` |
| GET | `/accountsByL1Address` | 一个 L1 地址名下的全部账户 | `l1_address`, `sub_accounts[]` |
| GET | `/pnlLeaderboard` | 收益排行榜（支持按地址检索） | `entries[]`, `total`, `updated_at` |
| GET | `/exchangeStats` | 全市场 24h 行情汇总 | `total`, `order_book_stats[]` |
| GET | `/orderBookDetails` | 全市场合约规格（费率/最小下单量/精度/保证金） | `order_book_details[]`, `spot_order_book_details[]` |
| GET | `/assetDetails` | 资产精度与转账下限 | `asset_details[]` |

**需要鉴权（本项目不使用）**：`accountMetadata`（HTTP 400，`"auth query param and Authorization header are both empty"`）、
`accountActiveOrders` / `accountOrders` / `accountInactiveOrders`（同上）、`transfer_history`（HTTP 403）。

**关键推论**：REST 侧看不到挂单——但 **WebSocket 的 `account_all_orders` 免鉴权可得完整挂单**（见第四节）。

---

## 三、字段陷阱清单（领域层必须逐条断言）

### 3.1 时间戳有四种口径（同一套 API 内）

| 字段 | 位置 | 单位 | 实测值示例 |
|---|---|---|---|
| `transaction_time` | `account.accounts[]` | **微秒** | `1791163963009550` |
| `updated_at` | `pnlLeaderboard` | **秒** | `1791190800` |
| `created_at` | `orderBookDetails.order_book_details[]` | **毫秒，且是字符串** | `"1737098461127"` |
| `created_at` | `publicPoolsMetadata.public_pools[]` | **秒** | `1737101233` |

把 `transaction_time` 当毫秒用会得到公元 58733 年。

### 3.2 数值类型：字符串与数字混用

| 数据 | 接口 | 类型 |
|---|---|---|
| 账户余额、抵押、总资产 | `account` | **字符串** `"4510553.190349"` |
| 池的 `total_asset_value` / `operator_fee` | `publicPoolsMetadata` | **字符串** `"1633519.9729449996"` / `"20.0000"` |
| 池的 `annual_percentage_yield` / `sharpe_ratio` | `publicPoolsMetadata` | **数字** |
| 排行榜的 `account_value` | `pnlLeaderboard` | **数字** |
| 订单簿的费率、最小下单量 | `orderBookDetails` | **字符串** `"0.055"` |
| 订单簿的 `last_trade_price` | `orderBookDetails` | **数字** |

空值形态：零仓的 `position_value` 可能是字符串 `"-0.000000"`（**负零**），解析后为 `0`，不可当负数处理。

### 3.3 两个保证金率字段的刻度不同

| 字段 | 来源 | 刻度 | 换算 |
|---|---|---|---|
| `default_initial_margin_fraction` / `min_...` / `maintenance_...` / `closeout_...` | `orderBookDetails` | 整数 | **÷ 10000** → 比例（`1000` → 0.10 → 10%） |
| `initial_margin_fraction` | `account.positions[]` | 百分数字符串 | **÷ 100** → 比例（`"25.00"` → 0.25） |

**杠杆倍数只能从后者算**：`杠杆 = 100 ÷ positions[].initial_margin_fraction`。
实测校验：`持仓名义 872.38 × 25.00% = 218.09` = 账户报的 `cross_initial_margin_requirement` `218.076915`，吻合。

ENA 市场实测：初始 10.00% / 维持 **6.00%** / 平仓线 4.00%，满足官方不等式 `C < M < I`。

### 3.4 三个"本身就是百分数"的字段（**不要再乘 100**）

| 字段 | 实测值 | 含义 |
|---|---|---|
| `annual_percentage_yield` | `7.372113` | **7.37%** |
| `operator_fee` | `"20.0000"` | **20%** |
| `daily_price_change` | `5.3257` | **+5.33%** |

`daily_price_change` 的单位曾用 24h 区间反推验证：按百分数解释全部落在区间内，
按小数解释会得出**负价格**（如 ROBO −0.0038），物理上不可能。

### 3.5 死字段：实测恒为 0，不建模

| 字段 | 实测 |
|---|---|
| `positions[].realized_pnl` | 112/112 全为 `0`。持仓层面不回填已实现盈亏 |
| `positions[].margin_mode` | 恒为 `0` |
| `positions[].allocated_margin` | 恒为 `"0.000000"` |

在 `PoolPilot.Lighter.Tests` 里断言其仍为 0；一旦上游开始回填，测试会先红。

### 3.6 池的核心字段语义

- **`positions[]` 含全部市场**（实测 57 项），大部分是零仓，必须按 `position != 0` 过滤。
- **方向不在 `position` 里**：`position` 只有绝对值；方向看 **`sign`（`1` 多 / `-1` 空）**。
- **`liquidation_price` 与方向强相关**（实测 112 条非零持仓）：空头 **36/36** 有值，
  多头 **70/76 为 `0`**。`0` 应读作「全仓模式下该仓位不会被单独强平」而非「数据缺失」——此为推断，非实测结论。
- **净值必须用 `pool_info.share_prices`，不能用 `total_asset_value`**：后者会被申购/赎回影响，
  直接看它的变化率会把"有人存入"误判成"赚了"。`share_price` 是每份资产价值，对申赎免疫。
- **`operator_shares / total_shares`** 读作运营者利益绑定程度。实测某池为 6.28%，
  而 `min_operator_share_rate` 为 5.00% —— 刚够门槛。
- **API 的 `annual_percentage_yield` 与 `sharpe_ratio` 是点估计**，不可单独采信。
  实测某池 API 报 APY 7.37%，而从 `share_prices` 算出的真实年化为 34.21%、
  **真实最大回撤 −26.32%**——点估计同时低估了收益与风险。

### 3.7 接口之间的字段差异（实测，极易踩）

**同一份语义的字段，不同接口提供的不一样。** 硬读不存在的字段会永远得到 0/空，
让"上游没给"与"值真的是 0"无法区分。

| 字段 | `publicPoolsMetadata` | WS `pool_info` |
|---|---|---|
| `operator_shares` | **没有** | 有 |
| `min_operator_share_rate` | **没有** | 有 |
| `total_shares` | 有 | 有 |
| `annual_percentage_yield` / `sharpe_ratio` | 有 | 有 |

实测 `publicPoolsMetadata` 的完整字段清单（**共 16 个**）：
```
code, account_index, created_at, master_account_index, account_type, name,
l1_address, annual_percentage_yield, sharpe_ratio, status, operator_fee,
total_asset_value, total_spot_value, total_perps_value, total_shares, assets
```

**推论**：要算"运营者利益绑定有多紧"（`operator_shares / total_shares` 对比
`min_operator_share_rate`），必须走 WS `pool_info`，池清单接口给不了。

### 3.8 `annual_percentage_yield` 可以荒谬到 6700 万%

实测 336 个池里，|APY| 最大的几个：

| 池名 | `annual_percentage_yield` |
|---|---|
| ARB 2x long | **67,264,730%** |
| MONAD 2x long | 22,326,655% |
| MONAD 2x short | 22,118,457% |
| BERA 2x short | 20,755,624% |

成因：短命池把极小的净值波动按年化放大。**这不是解析错误，是上游字段的真实行为**——
它恰好证明了"点估计不可信"：界面上必须把上游值与净值序列实算值并排展示。

**因此不要把该字段的任何具体上界写成断言**（除非刻意用来探测字段语义变化）。

### 3.9 `order_book` 的载荷是嵌套的，不是顶层

```
{
  "channel": "order_book:29",
  "order_book": {          ← 盘口在这里
    "asks": [{ "price": "...", "size": "..." }],
    "bids": [...],
    "last_updated_at": 微秒
  },
  "type": "update/order_book"
}
```

顶层只有 `channel` / `offset` / `type` / `last_updated_at`。**按顶层 `asks` 读会一条都匹配不到**，
且会静默走进"无快照"分支。价格与数量都是字符串。

该频道订阅时推一次完整快照，之后只推增减量。

### 3.10 `account_all_trades` 的快照窗口比想象中短

同一个池，两次相隔约两小时的快照：

| 时刻 | 快照成交笔数 | 挂单成交占比 |
|---|---|---|
| 10:30Z | **50 笔** | 96% |
| 12:40Z | **2 笔** | 0% |

**不能假设它返回"最近 50 笔"**——窗口由服务端决定，会随池子的动作节奏变化。
所以：不要基于该快照断言"池子是挂单型"这类总体统计，只应断言**分类规则与上游标志位的一致性**。

### 3.7b 账户与市场的其他要点

- `account.accounts[].created_at` 实测为 `0`（未回填），**不能用它判断账户年龄**。
- `account` 的 `index` 与 `account_index` 同值，任取其一。
- `account_all_orders` 返回的挂单含 `base_size`（= 数量 × 10）与 `base_price`（= 价格 × 1e5）
  两个缩放整数；`remaining_base_amount × price` 与 `base_size × base_price / 1e6` 互为校验，
  实测两者差异为 `0.000000`。
- `orderBookOrders` 的每个挂单都返回 `owner_account_index` —— **挂单是可公开归属的**。

---


### 3.5.1 `initial_margin_fraction` 会短暂返回 `"0.00"`（仓位变动瞬间，2026-10-06 实测）

**现象**：账户正在活跃调仓时（实测某池同时持有 44 个市场、频繁调仓），
某一行持仓的 `initial_margin_fraction` 会短暂变成 `"0.00"`。

**为什么不能当非法值抛异常**：0% 保证金率 = 无穷杠杆，显然不是任何账户的真实设定——
它表达的是"这一行正在更新中"。曾因把它当非法值抛 `ArgumentOutOfRangeException`，
一个池子就让整个账户列表接口 400（44 个仓位，撞上 0 的概率远高于 2 个仓位的池，
所以"单池详情一直正常、列表突然全挂"，极具迷惑性）。

**建模**：`PoolPosition.Margin` 为 `MarginFraction?`，`null` = 此刻无有效值；
工厂 `FromPositionPercent` 仍拒绝 0（那是工厂的契约），解析层负责把 `"0.00"` 翻译成 null。
杠杆显示为「未设定」而不是 0 倍。

**教训**：对"数值必须为正"的字段，抛异常前先问一句——上游会不会用 0 表达"暂无"？
判据：这个值有没有一个**合法的解读**。0% 费率是合法的（实测 Standard 档就是 0），
0% 保证金率不是。

## 四、WebSocket 行为（实测）

### 4.1 保活是硬要求，且是客户端义务

官方要求客户端**每 2 分钟至少主动发一帧**（可以是 `{"type":"ping"}`，会收到 `{"type":"pong"}`）。

**A/B 实测**：

```
不发任何帧:    +120573ms 连接被关闭 (code=1000, reason="read tcp …: i/o timeout")
每 60s 发 ping: 存活满 200 秒 ✔
```

关闭原因里的 `i/o timeout` 是**服务端的读超时**——因为客户端一个帧都没发过。
**收消息不算，必须是客户端主动发帧**——只读订阅也必须显式保活。

**正确做法**：每 30~60 秒发一次 ping。不要用"每 120 秒重连 + 回填去重"绕过，那样会留下周期性盲区。

### 4.2 频道清单与推送行为

| 频道 | 文档标注 | 实测 | 推送内容 |
|---|---|---|---|
| `account_all` | 免鉴权 | ✅ **会推增量** | `positions` + `assets` + `shares` + `trades` + `funding_histories` + 各期成交量 |
| `account_all_positions` | 免鉴权 | ✅ **会推增量** | `positions` + `shares` |
| `account_all_trades` | 免鉴权 | ✅ **会推增量** | 成交明细 |
| `account_all_orders` | 称需鉴权 | ✅ **免鉴权可得且有数据** | 完整挂单（含价格/方向/剩余量） |
| `pool_info` | 称需鉴权 | ✅ 免鉴权可得 | **净值曲线**：`share_prices[]` + `daily_returns[]` |
| `pool_data` | 称需鉴权 | ✅ 免鉴权可得 | 仅推 1 次，内容是**历史流水快照**，非实时增量 |
| `trade/{market_id}` | 免鉴权 | ✅ 实时高频 | 全市场成交，含 `ask_account_id` / `bid_account_id` |
| `order_book/{market_id}` | 免鉴权 | ✅ 实时 | 盘口（漏损计算用） |
| `market_stats/{market_id}` | 免鉴权 | ✅ 实时 | 市场统计 |

**账户频道会推增量的实测依据**：对普通账户订阅 72 秒，`account_all_trades` 收 59 条、
`account_all_positions` 收 261 条、`account_all` 收 319 条；对真实公共池
（`account_type=2`）订阅 300 秒，账户频道推了 4 条。

> ⚠ 曾一度得出"账户频道只给快照、不推增量"的错误结论。原因是**测试窗口内被测对象没有活动**——
> 「没有活动所以没推送」与「只给快照不推送」是两种完全不同的原因。判断推送行为时，
> 必须用**有活动**的对象测。

### 4.3 实时捕获实测数据

一起完整的跟单标的平仓事件（池作为 maker 卖出）：

| 项目 | 实测值 |
|---|---|
| 流内 `transaction_time` | `10:48:34.969793Z` |
| 客户端到达 | `10:48:35.043Z` |
| **端到端延迟** | **73.2ms** |
| 延迟分布（24 笔样本） | min 71 / p50 74 / p90 103 / max 158 ms |
| 捕获方式 | 两路独立连接同时实时命中，**非回填**，不在任何重连缺口内 |

`account_all_trades` 与 `trade/{market}` 两条通道都能捕获该笔成交。
但**账户频道更可靠**：实测某池交易的是 AMD/SNDK/MU 等市场，同期「全市场 `trade/{market}` + 账户过滤」
一条都没捕获到（那些市场不在订阅列表里），而它的账户频道推了 4 条。
**账户频道不需要预先知道池交易哪些市场。**

### 4.4 形态差异：REST 是数组，WS 是映射

| | REST `account.positions` | WS `account_all_positions.positions` |
|---|---|---|
| 形态 | **数组** `[{market_id, symbol, ...}]` | **映射** `{"0": {market_id, ...}}`，值是**单对象不是数组** |

适配层必须统一这两种形态。

### 4.5 池的活动率（用于区分"机制故障"与"机会未现"）

实测窗口内 `trade/{market}` 共 412 笔成交，其中目标池仅 **1 笔（0.24%）**。
另一窗口 1122 笔成交涉及 383 个账户，公共池命中 **0 笔**。

**所以"长时间收不到池的成交"是正常的，不是通道故障。**

---

## 五、费率与限速

### 5.1 费率：Standard 档实测为 0

`orderBookDetails` 全市场的 `taker_fee` / `maker_fee` 实测均为 `"0.0000"`，
且 `is_taker_fee_enabled` / `is_maker_fee_enabled` 为 `true` —— **不是占位符**。

档位费率：

| 档位 | Maker | Taker | Taker 延迟 |
|---|---|---|---|
| **Standard**（默认） | **0%** | **0%** | **300 ms** |
| Plus | 0.005% | 0.005% | 300 ms |
| Premium（零质押） | 0.0040% | 0.0280% | 140 ms |

- **不存在 maker 返佣**（无负费率机制）。
- 唯一降费路径是质押 LIT 上 Premium（最高 30% off）。
- **Standard 档吃单有 300ms 延迟（speed bump）** —— 这个机制对"跟单"是结构性不利：
  检测到成交只要 73ms，但下单执行还要再等 300ms，实际滞后 373ms+。

### 5.2 限速

| 档位 | 只读加权请求/滚动分钟 |
|---|---|
| Standard | **60** |
| Plus / Premium | 24,000 |

关键端点权重：`trades` = **200**，`recentTrades` = 600，`accountActiveOrders` = 100，`apikeys` = 150，`search` = 3，`accounts/*` = 2，默认 300。

**推论**：Standard 档下 `trades` 端点权重 200 而总配额 60 —— **连每分钟轮询一次都跑不满**。
所以跟单信号**必须走 WebSocket**，不能靠 REST 轮询。

超限返回 HTTP **429 或 405**。API server 冷却 = `weightOfEndpoint / (totalWeight/60)`。

WS 限制：连接数 255/IP，订阅 500/连接、5000/IP，唯一账户 500/连接，客户端消息 200/分。

### 5.3 分页（`publicPoolsMetadata`）

- `index` 是**上界游标**，返回 `account_index` 严格小于该值的池，按索引降序，单页 ≤ 100。
- 起始游标取 `systemConfig.liquidity_pool_index`（实测 `281474976710654`）。
- **下一页游标取「本页最小 `account_index`」本身，不是 `min - 1`，然后靠 `account_index` 去重。**

**用 `min - 1` 会在页边界漏池**。症状有诊断价值：`filter=user` 与 `filter=all` 的差集出现
**双向不对称**（各自都有对方没有的池），这在集合关系上不可能成立。改成重叠复用边界后
`user ⊂ all` 成立（336 ⊂ 337）。

`filter` 取值：`user`（336 个）/ `protocol`（1 个）/ `stake`（1 个）/ `all`（= user + protocol，337 个）。

---

## 六、文档与实测的矛盾

| 官方文档说 | 实测结果 | 采用 |
|---|---|---|
| `transfer_history` 对公共池免鉴权 | HTTP **403**（三种参数写法均失败） | 以实测为准；挂单/流水走 WS |
| `pool_data` / `pool_info` / `account_all_orders` 需 `auth` | 不带 auth 也能订阅并收到真实数据 | 以实测为准，**但把"订阅失败"当正常分支处理**，不假定永久开放 |
| `IMF = min(用户设定, 市场最小)` | 按 `min()` 应得 10x，实测账户用的是 25%（4x） | 文档的 `min` 应为 **`max`**，否则"用户降低杠杆"无法生效 |
| `accountMetadata` 的 `cursor` 暗示可分页 | HTTP 400 需鉴权，未能验证分页行为 | 标注为未验证 |
| 未见文档说明 `order_book` 的嵌套结构 | 实测盘口嵌在 `order_book` 对象内，顶层无 `asks` | 以实测为准 |

---

## 七、明确不做的事

### 7.1 下单需要签名——签名器已经有了，但本项目仍不下单

- 所有交易必须用 **API key 签名**后经 `POST /api/v1/sendTx` 提交，签名方案是 Lighter 自研的（非 EIP-712）。
- API key 的注册需要一次 **L1 钱包签名**（`ChangePubKey`）；注册后下单不再需要 L1 私钥。
- ⚠️ **订正（2026-10-13）**：原先这里写着"没有纯 .NET 实现，所以 .NET 生态没有可用签名器"。
  现在它不成立了——`Lighter.Net`（CryptoExchange.Net 家族，`LighterCredentials` 用 `EthKey`）
  把官方那个 Go 编译的 C 动态库**按平台封装好**（实测包里带 `osx-arm64` / `linux-x64` / `win-x64`
  的 `lighter_signer`，P/Invoke 调用），下单接口齐备（下单/改单/撤单/查单）。
- **但本项目仍不启用 Lighter 执行侧**：这已经是**产品决定**，不再是能力限制。
  真要启用，先办两件事：
  1. **实盘实测一次签名**（下最小单再撤掉）——README 与包内容都不算数，铁律 7 以实测为准；
  2. **显式设 `IntegratorFeePercentage = 0`**：该包默认走 Integrator Code，每笔单会在交易所费率
     之外再收 1bps 给库作者（README 原文）。
- 执行侧仍在币安 U 本位（见 docs/EXCHANGES.md）；Lighter 侧的对接也统一走 CryptoClients.Net。

### 7.2 历史成交流水需要付费解锁

`export/historicalTrades` 提供全市场历史成交流水的 parquet（覆盖到主网创世 2025-01-17，
每日 8PM UTC 更新，返回预签名 S3 URL），但需要**一次性向指定地址做 100 LIT 的 `L2Transfer`**。

本系统不依赖它：纸面跟单运行时会自产验证数据（用真实盘口算我方成交价，与池子成交价对照）。

### 7.3 没有官方跟单机制

API key 的 scope 是"一个账户索引"，必须由该账户的 L1 持有者签名注册。
**任何人都无法在不拿到 L1 私钥的情况下代你交易，你也不能交易别人的账户。**
跟单只能用自己的账户复刻信号。

### 7.4 对接库的覆盖边界（2026-10-13 读库源码 + 实测）

对接走 CryptoClients.Net 的 `JKorf.Lighter.Net`（元包钉的 1.7.0）。它的覆盖**不是全部**，
本项目因此一分为二（两个类都写明了依据）：

| 用途 | 上游入口 | 走哪条 |
|---|---|---|
| 系统配置 | `/api/v1/systemConfig` | 库 |
| 账户（池）快照 | `/api/v1/account?by=index` | 库 |
| 合约规格 | `/api/v1/orderBookDetails` | 库 |
| 池枚举 | `/api/v1/publicPoolsMetadata` | 自研（库没有） |
| 净值序列 | WS `pool_info` | 自研（库没有） |
| 池的成交 | WS `account_all_trades` | 自研（库没有） |
| 池的挂单 | WS `account_all_orders` | 自研（库没有；REST 免鉴权实测 403） |
| 盘口 | WS `order_book/<market>` | 自研（库有 REST 版，但按 market_id 订阅更省） |

两条支撑判据：
- **库没有**：它的 socket 只注册了 `account_all` / `account_all_assets` / `user_stats` 三个频道
  （读源码得到，不是猜的）；REST 路径里也没有 `publicPoolsMetadata`。
- **`/api/v1/account` 不能替代池枚举**：实测带 `index/limit` 返回
  `{"code":20001,"message":"invalid param"}`——游标枚举只有 `publicPoolsMetadata` 有。
