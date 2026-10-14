namespace PoolPilot.Application.Dtos;

/// <summary>
/// 系统是否还没完成首次初始化。
///
/// 判据是**库里还没有任何账号**——"这套系统还没被用过"就是这个意思，
/// 而初始化页今天要做的事（配通知通道）恰好是"用起来之前必须解决的那一件"。
///
/// 为什么不用"通道配好了没有"当判据：口令填错时，通道静态看是"配齐"的，
/// 系统会立刻算作已初始化、初始化页随之关闭——而注册码其实还是发不出去，
/// 那个"注册要验证码 → 验证码要通道 → 通道配置在登录之后"的环就又闭上了。
/// 以"有没有账号"为窗口，填错了可以反复改到通为止；第一个账号一旦建出来，这个口子就关上了。
/// </summary>
public sealed record SetupStatusDto
{
    public required bool NeedsSetup { get; init; }

    /// <summary>没配好的原因（界面直接显示这一句）；<see cref="NeedsSetup"/> 为 false 时为 null。</summary>
    public required string? Reason { get; init; }
}
