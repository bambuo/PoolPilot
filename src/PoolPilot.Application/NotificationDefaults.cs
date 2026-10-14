using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application;

/// <summary>
/// 通知的默认样板与默认显示名。
///
/// **默认样板住在代码里、也可以被界面覆盖**：库里没有对应那一行时用这里的文本，
/// 保存过一次之后以库里的为准。这样"从没配置过"也是一个能跑通的状态，
/// 而不是"必须先配好一封邮件模板，否则注册不了"。
/// </summary>
public static class NotificationDefaults
{
    /// <summary>一类通知的元信息（用途、界面名、默认文案）。</summary>
    public sealed record Definition(string Purpose, string DisplayName, string Subject, string Body);

    private const string RegistrationCodeSubject = $"PoolPilot 注册验证码：{NotificationRenderer.CodePlaceholder}";

    private static readonly string RegistrationCodeBody =
        $"""
        你的注册验证码是：{NotificationRenderer.CodePlaceholder}

        {NotificationRenderer.MinutesPlaceholder} 分钟内有效，请勿转发给任何人。
        如果这不是你本人的操作，忽略这封邮件即可。
        """;

    /// <summary>全部通知类型。加一类通知 = 在这里加一条；界面与校验都读它。</summary>
    public static readonly IReadOnlyList<Definition> All =
    [
        new(
            NotificationPurposes.RegistrationCode,
            "注册验证码",
            RegistrationCodeSubject,
            RegistrationCodeBody),
    ];

    /// <summary>没有样板的用途的界面名（见 <see cref="NotificationPurposes.ProviderTest"/>）。</summary>
    private static readonly Dictionary<string, string> NonTemplatePurposeNames = new(StringComparer.Ordinal)
    {
        [NotificationPurposes.ProviderTest] = "通道测试",
    };

    /// <summary>用途的界面名。查不到就回退成用途本身——总比显示空白好排查。</summary>
    public static string DisplayNameOf(string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        return All.FirstOrDefault(d => d.Purpose == purpose)?.DisplayName
            ?? NonTemplatePurposeNames.GetValueOrDefault(purpose)
            ?? purpose;
    }

    /// <summary>这类通知的正文里必须保留的占位符（从默认正文推出来，避免另立一份清单忘记同步）。</summary>
    public static IReadOnlyList<string> RequiredPlaceholders(Definition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return definition.Body.Contains(NotificationRenderer.CodePlaceholder, StringComparison.Ordinal)
            ? [NotificationRenderer.CodePlaceholder]
            : [];
    }

    public static Definition For(string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        return All.FirstOrDefault(d => d.Purpose == purpose)
            ?? throw new ArgumentException($"未知的通知用途「{purpose}」", nameof(purpose));
    }

    /// <summary>把库里的样板与代码默认值合成"实际会发出去的那一份"。</summary>
    public static NotificationTemplate Effective(Definition definition, NotificationTemplate? stored)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return stored ?? new NotificationTemplate
        {
            Purpose = definition.Purpose,
            DisplayName = definition.DisplayName,
            Subject = definition.Subject,
            Body = definition.Body,
            ProviderId = null,
            UpdatedAt = LighterTimestamp.FromMilliseconds(0),
        };
    }
}
