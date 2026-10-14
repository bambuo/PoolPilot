using System.Globalization;
using System.Security.Cryptography;

using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Services;

/// <summary>
/// 一次性口令校验的结果。
///
/// <see cref="MatchedStep"/> 是命中时用的时间步，调用方必须把它记回账号——
/// 那是防重放的依据（见 <see cref="TotpGenerator.Verify"/>）。
/// </summary>
public readonly record struct TotpVerification(bool IsValid, long MatchedStep);

/// <summary>
/// TOTP 生成与校验（RFC 6238，建立在 RFC 4226 的 HOTP 之上）。
///
/// **为什么是 SHA-1**：RFC 6238 也定义了 SHA-256/512 变体，但验证器 App 默认注册的凭证
/// 等价于 SHA-1 模式（`otpauth://` 里的 `algorithm` 缺省即 SHA1）。换成 SHA-256
/// 会有相当一部分 App 算出来的码永远对不上，而这个功能是**给 App 扫的**——互操作优先。
///
/// 六位码的取法照 RFC 4226 §5.3：对 HMAC 结果做动态截断取 31 位整数，再取模 10^位数、左侧补零。
/// </summary>
public static class TotpGenerator
{
    /// <summary>位数。6 位是所有验证器 App 的默认。</summary>
    public const int Digits = 6;

    /// <summary>时间步长（秒）。30 秒是 RFC 6238 §4 的建议值，也是 App 的默认。</summary>
    public const int StepSeconds = 30;

    /// <summary>
    /// 容忍的时间步窗口（±1 步 = ±30 秒）。取 1 而不是 0：客户端与服务端的时钟很少完全一致，
    /// 窗口为 0 会把"码刚跳变时按下的那一下"判成错误。
    /// </summary>
    public const int Window = 1;

    /// <summary>时间步序号：自 Unix 纪元起每 <see cref="StepSeconds"/> 秒一步。</summary>
    public static long StepAt(DateTimeOffset atUtc) => atUtc.ToUnixTimeSeconds() / StepSeconds;

    public static string Generate(TotpSecret secret, DateTimeOffset atUtc) =>
        Generate(secret, StepAt(atUtc), Digits);

    /// <summary>
    /// 算某个时间步的码。
    /// </summary>
    /// <param name="digits">
    /// 6 位供界面与 App 使用；8 位只为一件事存在——RFC 6238 附录 B 公布的官方测试向量是 8 位的，
    /// 拿它断言才能证明这套实现与标准一致，而不是"自己跟自己对答案"。
    /// </param>
    public static string Generate(TotpSecret secret, long step, int digits)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentOutOfRangeException.ThrowIfNegative(step);

        int modulus = digits switch
        {
            6 => 1_000_000,
            7 => 10_000_000,
            8 => 100_000_000,
            _ => throw new ArgumentOutOfRangeException(nameof(digits), digits, "只支持 6-8 位"),
        };

        // 计数器是**大端** 8 字节（RFC 4226 §5.2）
        byte[] counter = BitConverter.GetBytes(step);

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counter);
        }

        // CA5350（"使用了弱加密算法"）在这一处**刻意抑制**：
        // RFC 6238 的默认模式就是 HMAC-SHA-1，验证器 App 注册的凭证也等价于它。
        // 换 SHA-256 会让相当一部分 App 算出的码永远对不上——这个码是给人扫进 App 的，
        // 互操作优先。它做的是"一次性口令"，不是签名，SHA-1 的碰撞弱点在这里不构成攻击面。
#pragma warning disable CA5350
        byte[] mac = HMACSHA1.HashData(secret.Bytes, counter);
#pragma warning restore CA5350

        int offset = mac[^1] & 0x0F;
        int binary =
            ((mac[offset] & 0x7F) << 24)
            | (mac[offset + 1] << 16)
            | (mac[offset + 2] << 8)
            | mac[offset + 3];

        return (binary % modulus).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>
    /// 校验一个码。
    ///
    /// 两条规则值得单独说明：
    /// · **窗口 ±1 步**，且只在窗口内找一次匹配（找不到就失败，不做"最接近"之类的模糊判定）。
    /// · **不接受比上次更旧的时间步**：验证器 App 在窗口内一直显示同一个码，
    ///   如果连"上一个窗口的码"都还收，那它就成了一张可以反复使用的凭据。
    ///   但**同一个时间步可以再用**——这是踩过的一次坑：绑定时用掉的正是当前这一步，
    ///   紧接着重新登录会拿着同一个码进来，按"必须严格更新"判就会在注册后立刻告诉使用者
    ///   "验证码不正确"，而他能做的只有干等下一个 30 秒。
    ///   代价是一个码在它自己的窗口内（≤90 秒）可被重复提交——对一个只监听本机、
    ///   且失败次数有上限的系统，这个代价换掉那半分钟的困惑是值得的。
    /// </summary>
    public static TotpVerification Verify(
        TotpSecret secret,
        string candidateCode,
        DateTimeOffset nowUtc,
        long? lastAcceptedStep)
    {
        ArgumentNullException.ThrowIfNull(secret);

        string candidate = candidateCode?.Trim() ?? string.Empty;

        if (candidate.Length != Digits || !candidate.All(char.IsAsciiDigit))
        {
            return new TotpVerification(false, -1);
        }

        long currentStep = StepAt(nowUtc);

        for (long offset = -Window; offset <= Window; offset++)
        {
            long step = currentStep + offset;

            if (step < 0 || (lastAcceptedStep is { } last && step < last))
            {
                continue;
            }

            if (string.Equals(Generate(secret, step, Digits), candidate, StringComparison.Ordinal))
            {
                return new TotpVerification(true, step);
            }
        }

        return new TotpVerification(false, -1);
    }

    /// <summary>
    /// 拼 <c>otpauth://</c> URI（给前端画二维码用）。
    ///
    /// **这不是 RFC**：它是 Google Authenticator 起头、各家 App 共同遵守的事实约定
    /// （参数名 <c>secret</c>/<c>issuer</c>/<c>algorithm</c>/<c>digits</c>/<c>period</c>）。
    /// label 里的 <c>issuer:account</c> 与查询串都要转义，否则邮箱里的 <c>+</c> 之类会把 URI 拆坏。
    /// </summary>
    public static string BuildOtpauthUri(TotpSecret secret, string issuer, string accountLabel)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountLabel);

        string label = Uri.EscapeDataString($"{issuer}:{accountLabel}");

        return $"otpauth://totp/{label}?secret={secret.ToBase32()}"
            + $"&issuer={Uri.EscapeDataString(issuer)}"
            + $"&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }
}
