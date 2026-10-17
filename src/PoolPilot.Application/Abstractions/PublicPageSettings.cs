namespace PoolPilot.Application.Abstractions;

/// <summary>
/// 公开页（<c>/api/public/*</c>）的总开关。
///
/// **两个页面各自一刀**，默认全关：带单页只是给人看池子（外加"为某个池建立观察"这一写），
/// 跟单页则会往库里写一行纸面账并跟着产出流水——它不会替人建观察者，那属于带单页。
/// 想只放开前者，就把 <see cref="Followers"/> 留着关。
///
/// 注册方式与 <see cref="LiveTradingLimits"/> 一样：由组合根从配置节
/// <c>"PublicPages"</c> 绑好再注成实例——应用层因此不依赖任何配置包。
/// 关掉时对应端点回 404（不是 403）：对一个没有开放的功能，"存在但拒绝你"和
/// "不存在"对外应当无法区分。
/// </summary>
public sealed class PublicPageSettings
{
    /// <summary>是否放开公开带单页（读 + 匿名新增带单员）。</summary>
    public bool Leads { get; set; }

    /// <summary>是否放开公开跟单页（读 + 匿名建纸面跟单 + 创建者删除）。</summary>
    public bool Followers { get; set; }
}
