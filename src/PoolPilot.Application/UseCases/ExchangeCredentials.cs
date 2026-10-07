namespace PoolPilot.Application.UseCases;

/// <summary>
/// 凭据字段的处理规则，新建与修改共用一处。
///
/// 散成两份的话，"只填了 Key 没填 Secret"这种半配置状态迟早会在其中一条路径上漏过去——
/// 而它的后果是卡片显示"已配置"、实际调不通，排查时最费时间。
/// </summary>
internal static class ExchangeCredentials
{
    /// <summary>
    /// 校验成对性：key 与 secret 要么都给，要么都不给。
    ///
    /// 只给一半必须拒绝：半份凭据无法调用任何接口，但界面会显示"已配置"，
    /// 于是使用者以为自己配好了——这种"看起来成功"的失败最难查。
    /// </summary>
    public static void EnsurePair(string? apiKey, string? apiSecret)
    {
        bool hasKey = !string.IsNullOrWhiteSpace(apiKey);
        bool hasSecret = !string.IsNullOrWhiteSpace(apiSecret);

        if (hasKey != hasSecret)
        {
            throw new ArgumentException(
                "API Key 与 Secret 要一起给（或都不给）。只填一半的话凭据是不完整的，"
                + "但卡片上会显示「已配置」——那种状态比没配更麻烦。",
                nameof(apiKey));
        }
    }

    /// <summary>
    /// 计算一个凭据字段的新值，三种入参含义不同：
    /// <list type="bullet">
    ///   <item><c>null</c> —— 界面没动它，保持原值。</item>
    ///   <item>空白串 —— 显式清除（少了这一态，贴错的 key 就永远删不掉）。</item>
    ///   <item>有值 —— 换成新的。</item>
    /// </list>
    ///
    /// 一律 trim：前后空白最常见的原因是复制粘贴带进来的换行，
    /// 而"带着换行的凭据调不通"是极难查的一类问题。
    /// </summary>
    public static string? Apply(string? incoming, string? existing)
    {
        if (incoming is null)
        {
            return existing;
        }

        return string.IsNullOrWhiteSpace(incoming) ? null : incoming.Trim();
    }
}
