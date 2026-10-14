namespace PoolPilot.Domain.Constants;

/// <summary>
/// 通知通道的类型。目前只有邮箱一条。
///
/// 用常量而不是单成员枚举：枚举的好处是"编译器保证取值闭合"，但这里取值随时会加第二种，
/// 而库里的列是文本、界面上的下拉也是文本——多一层枚举转换只会多一处要同步的地方。
/// </summary>
public static class NotificationKinds
{
    /// <summary>邮箱（SMTP）。</summary>
    public const string Email = "email";
}

/// <summary>
/// 通知的用途（一类通知一条样板）。
/// </summary>
public static class NotificationPurposes
{
    /// <summary>注册验证码。发信方在注册流程里，收信方是尚未通过验证的邮箱。</summary>
    public const string RegistrationCode = "registration_code";

    /// <summary>
    /// 通道测试（在"通知 → 提供商"里点"发送测试邮件"）。
    ///
    /// 它**不是一类通知**——没有样板可配、也没有收件人可等：发到通道自己的发件人地址，
    /// 目的是当场回答"这条通道到底通不通"。所以它只出现在投递记录里。
    /// </summary>
    public const string ProviderTest = "provider_test";
}
