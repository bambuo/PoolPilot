using System.Security.Cryptography;

namespace PoolPilot.Domain.ValueObjects;

/// <summary>
/// TOTP 共享密钥。
///
/// 20 字节（160 bit）不是随手取的：RFC 4226 §4 对 HMAC-SHA-1 的建议就是它，
/// 也是所有验证器 App 的默认长度。长度不对时 App 那边的表现是"扫进去但码永远不对"，
/// 所以 <see cref="Parse"/> 严格按这个长度收口。
///
/// 对外只有两种形态：Base32 文本（写进 otpauth:// 与界面上"手动输入"那条）与原始字节（算码用）。
/// <see cref="ToString"/> 刻意不返回密钥——它很容易被顺手打进日志。
/// </summary>
public sealed record TotpSecret
{
    /// <summary>密钥长度（字节）。见类型说明。</summary>
    public const int ByteLength = 20;

    private readonly byte[] _bytes;

    private TotpSecret(byte[] bytes) => _bytes = bytes;

    public ReadOnlySpan<byte> Bytes => _bytes;

    public static TotpSecret Generate() => new(RandomNumberGenerator.GetBytes(ByteLength));

    /// <summary>从 Base32 文本解析（用户从别处复制来的那份可能带空格或小写，<see cref="Base32"/> 会宽容处理）。</summary>
    public static TotpSecret Parse(string base32)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base32);

        byte[] bytes = Base32.Decode(base32);

        if (bytes.Length != ByteLength)
        {
            throw new ArgumentException(
                $"TOTP 密钥应当是 {ByteLength} 字节（Base32 共 {Base32.Encode(new byte[ByteLength]).Length} 个字符），"
                + $"解出来是 {bytes.Length} 字节",
                nameof(base32));
        }

        return new TotpSecret(bytes);
    }

    public string ToBase32() => Base32.Encode(_bytes);

    /// <summary>密钥不进日志、不进异常消息。</summary>
    public override string ToString() => "（已隐藏）";
}
