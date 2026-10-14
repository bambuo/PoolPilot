namespace PoolPilot.Domain.Services;

/// <summary>
/// 秘密的"尾号提示"。
///
/// 界面上的凭据永远不回显明文，但又必须让人分得清"配的是哪一把"——不然贴错账号只能靠重贴一遍
/// 来发现。<see cref="Of"/> 就是这条折中：只给最后 4 位。
///
/// **不足 8 位时一个字符都不给**：短秘密的尾号可能占其大半，那种"提示"不是提示而是泄露。
/// </summary>
public static class SecretHint
{
    private const int MinimumLength = 8;
    private const int HintLength = 4;

    public static string? Of(string? plaintext) =>
        plaintext is { Length: >= MinimumLength } value ? value[^HintLength..] : null;
}
