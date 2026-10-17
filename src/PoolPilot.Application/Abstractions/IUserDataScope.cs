namespace PoolPilot.Application.Abstractions;

/// <summary>
/// 当前请求的数据作用域。
///
/// 权限回答"能进哪个页面、能点哪个按钮"，这里回答"能看哪些行"。两者是两件事：
/// 同一个「跟单管理」view 权限下，普通账号只该看到自己创建的跟单与交易所配置，
/// 超级管理员则保留全局视图用于排障。
///
/// 只有工作台私有数据用这个作用域；带单观察、池与行情、标的映射、通知配置是共享数据，
/// 不经过它。公开页的归属另有一套判据（见 <c>PublicFollowerOwnership</c>），
/// 那些行不进入工作台私有列表。
/// </summary>
public interface IUserDataScope
{
    /// <summary>当前登录用户主键。工作台私有端点要求已登录，未登录时取出即失败。</summary>
    long UserId { get; }

    /// <summary>当前用户是否拥有跨用户查看的全量视图（目前只有 superadmin）。</summary>
    Task<bool> CanSeeAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 这一行的归属是否对当前用户可见。owner 为 <c>null</c> 的行（迁移前遗留）
    /// 只对全量视图开放——没有 owner 就没有人能证明它是自己的。
    /// </summary>
    Task<bool> CanAccessOwnerAsync(long? ownerUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 列表查询的归属过滤：<c>null</c> = 不过滤（全量视图），否则只取该用户的行。
    /// </summary>
    Task<long?> OwnerFilterAsync(CancellationToken cancellationToken = default);
}
