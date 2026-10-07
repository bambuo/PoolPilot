namespace PoolPilot.Domain.Constants;

/// <summary>
/// Lighter 协议的固定常量。
///
/// 全部来自 2026-10-05 的实测（详见 docs/LIGHTER-API-NOTES.md）。
/// 刻意不散落魔法数字：凡是在领域逻辑里要用的协议数值，都必须在这里有名字。
/// </summary>
public static class LighterConstants
{
    /// <summary>
    /// 公共池账户索引的起始段。
    ///
    /// 池索引形如 281474976552443，**远超 int32 上限**，因此全项目的账户索引
    /// 一律使用 <see cref="long"/>。用 int 会在解析时静默溢出成负数。
    /// </summary>
    public const long PublicPoolIndexFloor = 281_474_976_000_000L;

    /// <summary>主网 REST 基地址（注意是 <c>elliot.ai</c> 域名，不是 <c>lighter.xyz</c>）。</summary>
    public const string MainnetRestBaseUrl = "https://mainnet.zklighter.elliot.ai/api/v1";

    /// <summary>主网 WebSocket 地址。</summary>
    public const string MainnetWebSocketUrl = "wss://mainnet.zklighter.elliot.ai/stream";

    /// <summary>
    /// WebSocket 保活间隔（秒）。
    ///
    /// 服务端要求客户端每 2 分钟至少主动发一帧（**收消息不算**），否则以
    /// <c>i/o timeout</c> 关闭连接——实测 120.573 秒被关。取 60 秒留一倍余量。
    /// </summary>
    public const int WebSocketKeepAliveSeconds = 60;

    /// <summary>
    /// 池活动率参考值：某观察窗口内市场 412 笔成交中池仅占 1 笔。
    /// 用于在界面上区分"没有信号"是机制问题还是机会问题。
    /// </summary>
    public const double TypicalPoolActivityShare = 0.0024;

    /// <summary>
    /// 跟单漏损的有效测量窗口（毫秒）。
    ///
    /// **这是一条方法论约束，不是性能参数。** 滑点只能在成交发生的当下测：
    /// 拿当前盘口去比几小时前的成交价，算出来的是价格变动，不是摩擦成本。
    /// 实测教训：池的成交发生在 2 小时前（0.25518），用当前盘口（0.25257）去比，
    /// 会得出 −107 bps 的"负滑点"——即"我方价格比池子好"，这在逻辑上不可能。
    ///
    /// 超过该窗口的样本必须排除在聚合之外，并显式告知使用者排除了多少。
    /// </summary>
    public const int SlippageValidityWindowMs = 60_000;

    /// <summary>ENA 市场的标识，用作规模门槛计算的默认示例。</summary>
    public const int EnaMarketId = 29;
}
