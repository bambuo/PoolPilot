namespace PoolPilot.Domain.Services;

/// <summary>
/// 通知样板的占位符与渲染。
///
/// 占位符写成 <c>{名字}</c> 而不是 <c>{{name}}</c> 或 <c>%s</c>：这是给人在界面上手改的文本，
/// 越像"这里会被替换掉"越好。未知的占位符**原样保留**——它多半是使用者打错或故意留着，
/// 悄悄吃掉会让人以为写对了。
/// </summary>
public static class NotificationRenderer
{
    /// <summary>验证码。注册验证码类样板的必填占位符。</summary>
    public const string CodePlaceholder = "{code}";

    /// <summary>收件人邮箱（有时正文里想写"你正在用 xxx@yyy 注册"）。</summary>
    public const string EmailPlaceholder = "{email}";

    /// <summary>有效分钟数。</summary>
    public const string MinutesPlaceholder = "{minutes}";

    /// <summary>渲染正文。</summary>
    public static string Render(string body, string code, string email, int validMinutes)
    {
        ArgumentNullException.ThrowIfNull(body);

        return body
            .Replace(CodePlaceholder, code ?? string.Empty, StringComparison.Ordinal)
            .Replace(EmailPlaceholder, email ?? string.Empty, StringComparison.Ordinal)
            .Replace(
                MinutesPlaceholder,
                validMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
    }

    /// <summary>渲染主题。主题里也常带验证码——那样在通知栏里不用打开就能看到。</summary>
    public static string RenderSubject(string subject, string code) =>
        (subject ?? string.Empty).Replace(CodePlaceholder, code ?? string.Empty, StringComparison.Ordinal);
}
