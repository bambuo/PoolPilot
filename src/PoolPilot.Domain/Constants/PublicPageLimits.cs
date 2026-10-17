namespace PoolPilot.Domain.Constants;

/// <summary>
/// 公开页（<c>/api/public/*</c>）的数量上限与区间。
///
/// **这些是代码常量，不是配置项**（用户口径：回退的方式是改常量重新发布）。
/// 之所以写成具名常量而不是散在判断里的数字：它们每一条都是一条对外承诺
/// ——「公开侧最多长这么大」，改哪一个都应该是一次有意识的改动。
///
/// 与 <c>LiveTradingLimits</c> 的分工别混淆：那个走配置（实盘开关要能不重发就关掉），
/// 这里只有「一页面向陌生人打开，最多允许产生多少行」。
/// </summary>
public static class PublicPageLimits
{
    /// <summary>全站带单员总数上限（公开页与工作台共用这张表，所以是全站口径）。</summary>
    public const int TotalLeads = 100;

    /// <summary>带单员的单日（UTC 日）新增上限。防的是"一天被灌进几十个池子观察"。</summary>
    public const int LeadsPerDay = 20;

    /// <summary>全站公开跟单员总数上限。</summary>
    public const int TotalPublicFollowers = 300;

    /// <summary>单个创建者（一个会话用户或一枚访客密钥）能拥有的公开跟单条数上限。</summary>
    public const int FollowersPerVisitor = 10;

    /// <summary>公开页允许的本金下限（低于这个数跟不出任何仓位，只剩摩擦成本）。</summary>
    public const decimal MinPublicCapital = 10m;

    /// <summary>公开页允许的本金上限。</summary>
    public const decimal MaxPublicCapital = 10_000m;

    /// <summary>池名候选的默认返回条数。</summary>
    public const int PoolOptionLimitDefault = 20;

    /// <summary>池名候选的最大返回条数（查询参数再大也截到这里）。</summary>
    public const int PoolOptionLimitMax = 50;

    /// <summary>公开详情页展示的流水条数上限。整条账不对外——那是点进工作台才有的东西。</summary>
    public const int FillsShownOnDetail = 50;

    /// <summary>公开带单员详情展示的净值点上限（只给最近一段，不把整条历史摊开）。</summary>
    public const int NavPointsOnLeadDetail = 180;

    /// <summary>公开带单员详情展示的前几大持仓数。</summary>
    public const int PositionsOnLeadDetail = 6;

    /// <summary>公开跟单名称的最大长度（trim 之后）。</summary>
    public const int LabelMaxLength = 24;

    /// <summary>
    /// 池名候选的服务端缓存存活秒数。
    ///
    /// 上游按**权重**限速，而候选框是"每敲一个字问一次"的交互：不缓存的话，
    /// 匿名流量能把全量池枚举打穿，把工作台自己的取数挤到限速之后。
    /// </summary>
    public const int PoolCatalogCacheSeconds = 60;

    /// <summary>
    /// 公开页 SSE 无帧时的保活 ping 间隔（秒）。**数据推帧由事件驱动，不靠这个间隔**：
    /// 它只保证长时间没有数据时链路不被代理判定为空闲。
    /// </summary>
    public const int StreamIntervalSeconds = 15;

    /// <summary>
    /// 公开页 SSE 两类连接（列表 + 详情）合计上限。
    ///
    /// 流不挂 <c>public-read</c> 限流（长连接会被一分钟 60 次的窗口误杀），
    /// 这个上限就是它的额度：全站点阅者能同时挂住的连接数。超出的连接在推流前回 503。
    /// </summary>
    public const int MaxStreamConnections = 50;
}
