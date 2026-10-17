using System.Text;

using PoolPilot.Domain.Constants;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 公开页建行前的字段口径：模式、交易所配置、本金区间、名称。
///
/// 收在一处的理由和 <c>LiveCapitalLimit</c> 一样：这些是**对陌生人的规则**，
/// 散在用例的各个分支里就没人能一眼看出"公开页到底接受什么"。
/// 越界一律抛 <see cref="ArgumentException"/>（中间件映射成 400，消息就是给界面读的那句话）。
/// </summary>
public static class PublicFollowerInputRules
{
    /// <summary>公开面允许的模式（纸面）。空串与不传同义。</summary>
    private const string PaperMode = "paper";

    /// <summary>公开面明确拒绝的模式：实盘要真实下单，而陌生人既没有凭据也不该有。</summary>
    private const string LiveMode = "live";

    /// <summary>控制字符不出现在名称里：它们会打断界面上一行的排版，也会被复制进日志。</summary>
    private const char ZeroWidthSpace = '\u200b';

    /// <summary>
    /// 解析模式。**"live" 是明确拒绝，不是静默降级成纸面**：
    /// 使用者以为自己在开实盘、实际拿到的是一份模拟账，比建不出来危险得多。
    /// </summary>
    public static void EnsurePaperMode(string? mode)
    {
        switch (mode?.Trim().ToLowerInvariant())
        {
            case null or "" or PaperMode:
                return;
            case LiveMode:
                throw new ArgumentException(
                    "公开页只能建纸面跟单：实盘跟单要落到你自己的交易所账号上，"
                    + "而这件事只在工作台里做（登录后在「跟单」页新建）。",
                    nameof(mode));
            default:
                throw new ArgumentException($"未知的跟单模式：{mode}。公开页可用值：paper。", nameof(mode));
        }
    }

    /// <summary>交易所配置：公开面没有实盘，所以这个字段只可能是 null。</summary>
    public static void EnsureNoExchangeConfig(long? exchangeConfigId)
    {
        if (exchangeConfigId is not null)
        {
            throw new ArgumentException(
                "公开页不能指定交易所账号：这里只会建纸面跟单，不存在「往哪个账号下单」这一步。",
                nameof(exchangeConfigId));
        }
    }

    /// <summary>本金区间。上限防的是"把一个亿填进一个匿名页面"，下限防的是跟不出任何仓位的最小额。</summary>
    public static void EnsureCapital(decimal initialCapital)
    {
        if (initialCapital < PublicPageLimits.MinPublicCapital
            || initialCapital > PublicPageLimits.MaxPublicCapital)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialCapital),
                initialCapital,
                $"公开页的本金要在 {PublicPageLimits.MinPublicCapital} ~ "
                + $"{PublicPageLimits.MaxPublicCapital} 之间（当前值：{initialCapital}）。"
                + "要投更多，请到工作台登录后建跟单员。");
        }
    }

    /// <summary>
    /// 名称的规范化：剥控制字符 → trim → 空串按 null → 超长拒绝。
    /// </summary>
    /// <returns>可以落库的名称，或 <c>null</c>（没起名）。</returns>
    public static string? NormalizeLabel(string? label)
    {
        if (label is null)
        {
            return null;
        }

        StringBuilder kept = new(label.Length);

        foreach (char character in label)
        {
            // char.IsControl 管不住零宽空格这类"看不见但会改变文本"的字符，一并剥掉
            if (!char.IsControl(character) && character != ZeroWidthSpace)
            {
                kept.Append(character);
            }
        }

        string normalized = kept.ToString().Trim();

        if (normalized.Length == 0)
        {
            return null;
        }

        if (normalized.Length > PublicPageLimits.LabelMaxLength)
        {
            throw new ArgumentException(
                $"名称最多 {PublicPageLimits.LabelMaxLength} 个字符（当前 {normalized.Length} 个）。",
                nameof(label));
        }

        return normalized;
    }

    /// <summary>
    /// 这一发能不能建立起"创建者"这件事。
    ///
    /// 匿名建行必须带着一枚访客密钥（响应会据此签发 Cookie）；否则这一行建出来
    /// 两个 owner 列都是空，谁也都删不掉——**宁可现在拒建**，也不要往库里留一条没人负责的记录。
    /// </summary>
    public static void EnsureCallerCanOwnFollower(long? userId, string? visitorSecret)
    {
        if (userId is null && string.IsNullOrWhiteSpace(visitorSecret))
        {
            throw new ArgumentException(
                "这个请求既没有登录态也没有访客标识，建出来的跟单员将没有任何人能删除它。"
                + "请先允许本站设置 Cookie（path=/api/public），再重试。",
                nameof(visitorSecret));
        }
    }
}
