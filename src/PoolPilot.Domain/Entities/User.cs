using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Entities;

/// <summary>
/// 登录账号。
///
/// 与业务实体（池、带单员、跟单员）没有任何关系：那些说的是"交易所里发生了什么"，
/// 这个说的是"谁能打开这个工作台"。放在同一层只是因为共用那一张库。
///
/// **MFA 是账号的必备部分，不是一个可选开关**：<see cref="TotpSecret"/> 为 <c>null</c>
/// 只表示"还没绑"（首次登录时必须绑上），不表示"这人不用 MFA"——所以这里没有
/// <c>MfaEnabled</c> 之类的字段：有它就会有人写出绕过它的分支，而那正是这类系统最常见的洞。
/// </summary>
public sealed record User
{
    /// <summary>持久化主键；未入库时为 0（由数据库分配）。</summary>
    public long Id { get; init; }

    /// <summary>登录名。已归一化（见 <see cref="EmailAddress"/>）。</summary>
    public required EmailAddress Email { get; init; }

    public required PasswordHash PasswordHash { get; init; }

    /// <summary>绑定的 TOTP 密钥；<c>null</c> = 还没绑定（首次登录必须绑）。</summary>
    public TotpSecret? TotpSecret { get; init; }

    /// <summary>
    /// 最近一次成功校验所使用的时间步。防重放用——验证器 App 在 30 秒窗口内显示的是同一个码，
    /// 不记住它，同一个码就能被用第二次。
    /// </summary>
    public long? TotpLastStep { get; init; }

    /// <summary>
    /// 创建时间。本机产生的墙钟时间，**不是**上游时间——用 <see cref="LighterTimestamp"/>
    /// 只是沿用仓库里既有的时间类型（跟单员、交易所配置也是这么存的），它并没有刻度歧义。
    /// </summary>
    public required LighterTimestamp CreatedAt { get; init; }

    /// <summary>是否已绑定 MFA。未绑定 = 首次登录必须走绑定流程。</summary>
    public bool HasMfa => TotpSecret is not null;

    /// <summary>绑定密钥并记下本次使用的时间步。</summary>
    public User BindMfa(TotpSecret secret, long matchedStep) =>
        this with { TotpSecret = secret, TotpLastStep = matchedStep };

    /// <summary>记下本次校验使用的时间步（已绑定账号每次登录成功后调用）。</summary>
    public User RecordMfaStep(long matchedStep) => this with { TotpLastStep = matchedStep };
}
