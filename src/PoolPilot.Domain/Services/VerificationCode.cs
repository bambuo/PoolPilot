using System.Globalization;
using System.Security.Cryptography;

namespace PoolPilot.Domain.Services;

/// <summary>
/// 邮箱验证码：六位随机数字。
///
/// 单独成一个类型，是为了让"六位数字"这条规则只有一个出处——生成、校验、界面提示
/// 若各写一遍，改一处就会漏两处。校验侧用固定长度与全数字两道判断，
/// 因此不需要在这里比对明文（比对发生在用例里，且失败次数有上限兜底）。
/// </summary>
public static class VerificationCode
{
    /// <summary>位数。与主流"邮箱验证码"一致，也是六位输入框的长度。</summary>
    public const int Digits = 6;

    private const int Range = 1_000_000;

    /// <summary>生成一枚码（左侧补零，永远是 <see cref="Digits"/> 位）。</summary>
    public static string Generate() =>
        RandomNumberGenerator.GetInt32(Range).ToString("D" + Digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>形状是否合法（六位、全数字）。</summary>
    public static bool IsWellFormed(string? candidate) =>
        candidate is { Length: Digits } && candidate.All(char.IsAsciiDigit);
}
