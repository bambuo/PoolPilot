namespace PoolPilot.Application;

/// <summary>
/// 登录与 MFA 的策略常量。
///
/// 全部集中在这一处：它们互相牵制（有效期越长越方便，越短越安全；尝试上限越高越好用，
/// 越低越难爆破），散在五个用例里就没法一眼看出这套参数整体是什么样。
/// </summary>
public static class AuthPolicy
{
    /// <summary>
    /// 写进 <c>otpauth://</c> 的服务名（验证器 App 里显示的那一行前缀）。
    /// 用应用名而不是域名：这是本机工具，没有域名可写。
    /// </summary>
    public const string Issuer = "PoolPilot";

    // ── 注册验证码 ──────────────────────────────────────────

    /// <summary>验证码有效时长。</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    /// <summary>重发冷却。够短不至于烦人，够长能挡住"把发送接口当短信轰炸机用"。</summary>
    public static readonly TimeSpan CodeResendCooldown = TimeSpan.FromSeconds(60);

    /// <summary>同一枚验证码允许错几次。</summary>
    public const int MaxCodeAttempts = 5;

    // ── 登录挑战 ────────────────────────────────────────────

    /// <summary>挑战有效时长（从密码验过到码验完）。比验证码短：这中间人是坐在屏幕前的。</summary>
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    /// <summary>同一个挑战允许错几次（含 TOTP 与绑定两条路径）。</summary>
    public const int MaxChallengeAttempts = 5;

    // ── 登录防爆破 ──────────────────────────────────────────

    /// <summary>连续失败多少次开始锁定。</summary>
    public const int MaxLoginFailures = 5;

    /// <summary>锁定时长。</summary>
    public static readonly TimeSpan LoginLockout = TimeSpan.FromMinutes(15);
}
