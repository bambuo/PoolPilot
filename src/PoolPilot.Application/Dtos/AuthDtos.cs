namespace PoolPilot.Application.Dtos;

/// <summary>请求发送注册验证码。</summary>
public sealed record RegistrationCodeRequestDto
{
    public required string Email { get; init; }
}

/// <summary>
/// 注册请求。四个字段是这组接口里最少的自洽集合：
/// 邮箱（同时也是登录名）、验证码（证明邮箱是本人的）、口令（两次，防手误）。
/// </summary>
public sealed record RegisterRequestDto
{
    public required string Email { get; init; }

    public required string Code { get; init; }

    public required string Password { get; init; }

    public required string ConfirmPassword { get; init; }
}

/// <summary>登录第一步：邮箱 + 口令。</summary>
public sealed record LoginRequestDto
{
    public required string Email { get; init; }

    public required string Password { get; init; }
}

/// <summary>登录第二/第三步：拿挑战 Id 与六位码完成。</summary>
public sealed record MfaCodeRequestDto
{
    public required string ChallengeId { get; init; }

    public required string Code { get; init; }
}

/// <summary>
/// 登录第一步的答复：**下一步该做什么**，而不是"登录成功"。
///
/// 本系统的 MFA 是强制的，所以这一步永远不会直接签发会话——要么让前端显示六位码输入框
/// （<c>mfaRequired</c>），要么让它显示二维码去绑定（<c>mfaBindingRequired</c>）。
/// </summary>
public sealed record LoginResultDto
{
    /// <summary><c>mfaRequired</c>=已绑令牌，直接输码；<c>mfaBindingRequired</c>=还没绑，先扫码。</summary>
    public required string Status { get; init; }

    /// <summary>完成这一步要带回来的凭据（一次性、5 分钟有效）。</summary>
    public required string ChallengeId { get; init; }

    /// <summary>待绑定的密钥（Base32），仅 <c>mfaBindingRequired</c> 时有值。</summary>
    public required string? Secret { get; init; }

    /// <summary>前端据此画二维码，仅 <c>mfaBindingRequired</c> 时有值。</summary>
    public required string? OtpauthUri { get; init; }
}

/// <summary>当前会话（<c>GET /api/auth/me</c>）。</summary>
public sealed record SessionDto
{
    /// <summary>账号主键。会话 Cookie 里存的就是它（<c>NameIdentifier</c>）。</summary>
    public required long UserId { get; init; }

    public required string Email { get; init; }
}
