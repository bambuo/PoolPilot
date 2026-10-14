using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PoolPilot.Domain.ValueObjects;

/// <summary>
/// 口令哈希（PBKDF2-HMAC-SHA256）。
///
/// 用 PBKDF2 而不是"加盐 SHA256"：后者的计算太快，拿到库就能按字典猛试；
/// PBKDF2 的迭代次数是给攻击者加的时间成本。用 BCL 的 <see cref="Rfc2898DeriveBytes"/>，
/// 不引第三方库——口令哈希这种"必须写对"的东西，宁可少一层外部依赖。
///
/// 编码成单列文本 <c>pbkdf2-sha256$迭代次数$盐$哈希</c>：
/// 迭代次数随哈希一起存，将来调大它时老口令仍然验得过（重新登录时再按新参数重算）。
/// 盐每口令独立随机，因此两个人用同一个口令，库里的两行也不相同。
/// </summary>
public sealed record PasswordHash
{
    /// <summary>口令长度下限。8 位是"比 6 位强、又不至于把人逼去写便签"的折中。</summary>
    public const int MinLength = 8;

    /// <summary>长度上限：PBKDF2 的成本与输入长度几乎无关，这里挡的是"往库里灌大字符串"。</summary>
    public const int MaxLength = 200;

    private const string AlgorithmLabel = "pbkdf2-sha256";
    private const int SaltBytes = 16;
    private const int KeyBytes = 32;

    /// <summary>迭代次数。210k 是 OWASP 对 PBKDF2-HMAC-SHA256 的建议量级。</summary>
    private const int Iterations = 210_000;

    private PasswordHash(string encoded) => Encoded = encoded;

    /// <summary>完整编码（含算法标签、迭代次数与盐）。落库的就是它。</summary>
    public string Encoded { get; }

    public static PasswordHash FromPassword(string rawPassword)
    {
        ValidatePolicy(rawPassword);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(
            rawPassword,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);

        return new PasswordHash(
            $"{AlgorithmLabel}${Iterations.ToString(CultureInfo.InvariantCulture)}"
            + $"${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}");
    }

    /// <summary>读回库里的编码。格式不对即拒绝，而不是留一个"永远验不过"的账号。</summary>
    public static PasswordHash Parse(string encoded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoded);

        string[] parts = encoded.Split('$');

        if (parts.Length != 4 || parts[0] != AlgorithmLabel
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int iterations)
            || iterations <= 0)
        {
            throw new ArgumentException("口令哈希的存储格式不对（应为 pbkdf2-sha256$迭代次数$盐$哈希）", nameof(encoded));
        }

        return new PasswordHash(encoded);
    }

    /// <summary>
    /// 校验口令。**固定时间比较**：逐字节提前返回会把"前几位对不对"泄成时间差。
    /// </summary>
    public bool Verify(string rawPassword)
    {
        ArgumentNullException.ThrowIfNull(rawPassword);

        string[] parts = Encoded.Split('$');
        int iterations = int.Parse(parts[1], CultureInfo.InvariantCulture);

        byte[] salt = Convert.FromBase64String(parts[2]);
        byte[] expected = Convert.FromBase64String(parts[3]);

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
            rawPassword,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// 口令规则。放在这里而不是"注册用例"里：规则与哈希是一件事的两面，
    /// 分成两处就会出现"某条路径忘了校验"的口子。
    /// </summary>
    public static void ValidatePolicy(string rawPassword)
    {
        ArgumentNullException.ThrowIfNull(rawPassword);

        if (rawPassword.Length < MinLength)
        {
            throw new ArgumentException($"口令至少 {MinLength} 位", nameof(rawPassword));
        }

        if (rawPassword.Length > MaxLength)
        {
            throw new ArgumentException($"口令最多 {MaxLength} 位", nameof(rawPassword));
        }

        if (string.IsNullOrWhiteSpace(rawPassword))
        {
            throw new ArgumentException("口令不能只有空白字符", nameof(rawPassword));
        }
    }

    /// <summary>不把哈希打进日志：它虽然是单向的，但没有出现在任何日志里的理由。</summary>
    public override string ToString() => $"{AlgorithmLabel}$（已隐藏）";
}
