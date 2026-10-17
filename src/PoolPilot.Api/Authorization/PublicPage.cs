namespace PoolPilot.Api.Authorization;

/// <summary>
/// 公开页的两片面。它们各自有开关（配置节 <c>PublicPages</c>），因为放开一片的成本不同：
/// 带单页只是把人往库里加一行观察者，跟单页还会加一份纸面账并跟着那条观察者产出流水。
/// </summary>
public enum PublicPage
{
    /// <summary>公开带单页（<c>/api/public/lead-traders</c>）。</summary>
    Leads,

    /// <summary>公开跟单页（<c>/api/public/followers</c>）。</summary>
    Followers,
}
