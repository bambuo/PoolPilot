using System.Text;

namespace PoolPilot.Domain.ValueObjects;

/// <summary>
/// Base32 编解码（RFC 4648 §6：大写字母表、无填充）。
///
/// 只在 TOTP 密钥上用到：<c>otpauth://</c> 规定密钥以 Base32 出现，验证器 App 也只认这个编码。
///
/// 解码侧刻意宽容：大小写、空格、连字符、<c>=</c> 填充都接受。密钥最常见的来源是
/// "从某个页面复制粘贴"，那份文本里常带分隔空格或小写——为这个把人挡回去没有任何意义。
/// </summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>每 5 bit 出一个字符。</summary>
    private const int BitsPerChar = 5;

    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        StringBuilder text = new(((bytes.Length * 8) + BitsPerChar - 1) / BitsPerChar);
        int buffer = 0;
        int bitsInBuffer = 0;

        foreach (byte value in bytes)
        {
            buffer = (buffer << 8) | value;
            bitsInBuffer += 8;

            while (bitsInBuffer >= BitsPerChar)
            {
                text.Append(Alphabet[(buffer >> (bitsInBuffer - BitsPerChar)) & 0x1F]);
                bitsInBuffer -= BitsPerChar;
            }

            // 只留下还没用掉的那几位：不掩掉的话 buffer 会一路左移涨到溢出
            buffer &= (1 << bitsInBuffer) - 1;
        }

        // 收尾：不足 5 bit 的余量左移补零（RFC 4648 的填充位，但**不输出 '='**）
        if (bitsInBuffer > 0)
        {
            text.Append(Alphabet[(buffer << (BitsPerChar - bitsInBuffer)) & 0x1F]);
        }

        return text.ToString();
    }

    public static byte[] Decode(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        List<byte> bytes = [];
        int buffer = 0;
        int bitsInBuffer = 0;

        foreach (char raw in text)
        {
            if (raw == '=')
            {
                break;
            }

            if (raw is ' ' or '-' or '\t' or '\r' or '\n')
            {
                continue;
            }

            int value = Alphabet.IndexOf(char.ToUpperInvariant(raw), StringComparison.Ordinal);

            if (value < 0)
            {
                throw new ArgumentException($"Base32 文本里有非法字符 '{raw}'", nameof(text));
            }

            buffer = (buffer << BitsPerChar) | value;
            bitsInBuffer += BitsPerChar;

            if (bitsInBuffer >= 8)
            {
                bytes.Add((byte)((buffer >> (bitsInBuffer - 8)) & 0xFF));
                bitsInBuffer -= 8;
            }

            // 同上：只留没用到的那几位
            buffer &= (1 << bitsInBuffer) - 1;
        }

        // 尾部若还有非零位，说明末尾那个字符是多余的（编码器不该产出这种文本）
        if (bitsInBuffer > 0 && (buffer & ((1 << bitsInBuffer) - 1)) != 0)
        {
            throw new ArgumentException("Base32 文本尾部有非零填充位，长度或最后一个字符不对", nameof(text));
        }

        return [.. bytes];
    }
}
