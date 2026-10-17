using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using PoolPilot.Domain.Entities;

namespace PoolPilot.Domain.Services;

/// <summary>
/// 公开跟单页的「创建者」判据——纯计算，不碰库也不认识 HTTP。
///
/// 要解决的问题：公开页匿名也能建跟单员，而"谁能删掉它"不能靠登录态回答
/// （很多人根本没有账号）。这里的答法是把创建者能力拆成两列，各自一条判据：
/// <list type="bullet">
///   <item><see cref="Follower.OwnerUserId"/> 非空 → 只认会话用户 Id 相等。</item>
///   <item><see cref="Follower.OwnerCredentialHash"/> 非空 → 用**本次请求携带的访客密钥**
///     现派生哈希，与存库值定长时间比较。</item>
///   <item>两列都为空 → 谁都不能从公开面删它（这种行只可能来自异常写入，宁可不给口子）。</item>
/// </list>
/// 非公开行（<c>IsPublic == false</c>）在这里一律判否：公开端点对它回 404，
/// 而不是"存在但看不到"——后者等于一个"这个 Id 有没有被用过"的探针。
///
/// ⚠️ 派生值里那个 <see cref="DomainSeparator"/> **不承担机密性**：这个仓库是公开的，
/// 常量谁都能读到。它的唯一作用是域分隔——把这一条派生与同密钥的其他 HMAC 用途隔开，
/// 免得将来别处复用了同一套哈希构造时互相能伪造。真正的机密性只来自访客密钥本身：
/// 它是 32 字节随机数，只存在于那条 Cookie 里，永不进库、永不进响应体、永不出现在 URL。
/// </summary>
public static class PublicFollowerOwnership
{
    /// <summary>域分隔常量（见类注释：它不是秘密）。改它会让已发出的匿名行全部无法被删除——
    /// 那是"公开页历史数据作废"级别的改动，必须连同库里的哈希一起重算。</summary>
    public const string DomainSeparator = "poolpilot.follower-owner.v1";

    /// <summary>访客密钥的形状：SHA-256 输出的十六进制长度，也就是 32 字节随机数的 hex。</summary>
    public const int VisitorSecretHexLength = 64;

    /// <summary>Cookie 里读到的值是否是一枚合法访客密钥。形状不对就当作"没有 Cookie"，不做补救。</summary>
    public static bool IsVisitorSecret(string? value) =>
        value is { Length: VisitorSecretHexLength } && value.All(char.IsAsciiHexDigitLower);

    /// <summary>
    /// 派生某一行匿名创建者的凭证：<c>lowerhex(SHA256(HMACSHA256(key=域分隔, msg=访客密钥:跟单员 Id)))</c>。
    ///
    /// 带行 Id 是刻意的：同一枚访客密钥在不同行上派生出不同值，于是"能删 A"绝不等于"能删 B"，
    /// 拿到一行也不会得到整个库里其他匿名行的删除能力。
    /// </summary>
    public static string DeriveCredential(string visitorSecret, long followerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(visitorSecret);

        return Convert.ToHexStringLower(DeriveBytes(visitorSecret, followerId));
    }

    /// <summary>
    /// 这一行的创建者是不是当前调用者。四个判据见类注释。
    ///
    /// 两样都可能没有（第一次访问公开页的匿名请求连 Cookie 都还没有）：那时
    /// <paramref name="visitorSecret"/> 为 <c>null</c>，匿名行一律判否——**不是**
    /// "先让你建一条，Cookie 之后再补"那种宽容，因为那样建出来的行没人认得出创建者。
    /// </summary>
    public static bool IsCreator(Follower follower, long? userId, string? visitorSecret)
    {
        ArgumentNullException.ThrowIfNull(follower);

        if (!follower.IsPublic)
        {
            return false;
        }

        // 登录建的行：只认会话用户。这时访客密钥不参与（两种身份各走各的判据，不交叉）
        if (follower.OwnerUserId is { } ownerUserId)
        {
            return userId == ownerUserId;
        }

        if (follower.OwnerCredentialHash is { Length: > 0 } storedHash)
        {
            return visitorSecret is { Length: > 0 } secret && CredentialMatches(secret, follower.Id, storedHash);
        }

        return false;
    }

    /// <summary>
    /// 调用者能删的判据里唯一涉及密码学的一步：现派生 + **定长时间**比较。
    ///
    /// 用 <see cref="CryptographicOperations.FixedTimeEquals"/> 而不是 <c>string ==</c>：
    /// 逐字节的短路比较会让响应时间随"猜对了几个字节"变化，那是可测的旁路——
    /// 一个能反复请求的匿名端点正是最便宜的攻击面。
    /// </summary>
    private static bool CredentialMatches(string visitorSecret, long followerId, string storedHash)
    {
        byte[] expected = DeriveBytes(visitorSecret, followerId);

        byte[] actual;

        try
        {
            actual = Convert.FromHexString(storedHash);
        }
        catch (FormatException)
        {
            // 库里存的不是十六进制（手改过或来自旧版本）：判不通过，不因此抛异常
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static byte[] DeriveBytes(string visitorSecret, long followerId)
    {
        string message = visitorSecret + ":" + followerId.ToString(CultureInfo.InvariantCulture);

        byte[] mac = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(DomainSeparator),
            Encoding.UTF8.GetBytes(message));

        return SHA256.HashData(mac);
    }
}
