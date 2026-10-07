# 交易所标识图

这些是各交易所**官方发布的标识图**，一次性取回后随包走——不在运行时外链加载。

理由：各家的 CDN 会拦也会变（实测 Gate 的 `apple-touch-icon` 直接 403、
官网 favicon 允许但走的是另一条路径），把界面上的图标交给别人的可用性不可接受。

| 文件 | 来源 | 原始尺寸 |
|---|---|---|
| `lighter.png` | `https://app.lighter.xyz/apple-touch-icon.png` | 180×180 |
| `binance.png` | `https://bin.bnbstatic.com/static/images/favicon.ico`（官方 CDN，转 PNG） | 32×32 |
| `okx.png` | CoinGecko 收录的 OKX 标识（官方 `favicon.ico` 只有 24×24） | 50×50 |
| `gate.png` | `https://gate.io/favicon.ico`（转 PNG） | 32×32 |

用途是**指认平台**（在配置卡片上标出"这是哪一家"），不用于任何其他场景；
更新时替换同名文件即可，尺寸不必一致——界面上统一按 20×20 的圆角方块显示。
