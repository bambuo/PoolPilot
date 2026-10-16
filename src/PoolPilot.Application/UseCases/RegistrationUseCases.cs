using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Exceptions;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 发送注册验证码。
///
/// 顺序是有讲究的：先挡"已注册"和"还在冷却"，再生成、落缓存、发信。
/// 反过来的话，每敲一次"发送"都会生成一枚新码把上一枚顶掉——
/// 使用者连着点两次，手里那枚就永远对不上了。
///
/// 发信走**通知配置**：用哪条通道、说什么话，都由「通知 → 样板」里那一行决定；
/// 库里没配过就用代码里的默认样板（见 <c>NotificationDefaults</c>），
/// 于是"刚装好、什么都没配"也是一个能跑的状态。
/// </summary>
public sealed class RequestRegistrationCodeUseCase(
    IUserRepository users,
    IRegistrationCodeStore codes,
    INotificationProviderRepository providers,
    INotificationTemplateRepository templates,
    INotificationMailer mailer,
    INotificationLogRepository logs,
    TimeProvider clock)
{
    public async Task<NotificationDeliveryDto> ExecuteAsync(
        string rawEmail,
        CancellationToken cancellationToken = default)
    {
        EmailAddress address = EmailAddress.Parse(rawEmail);

        // 已注册的邮箱不给发码：注册那一步也会拒，早说一句省一轮等待。
        // 这确实回答了"某邮箱是否已注册"，但注册本来就是开放的——谁都能试着注册一次来问同一个问题，
        // 把它藏到发码之后只是让使用者多等一轮（而对方一点也没多知道什么）。
        if (await users.FindByEmailAsync(address, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new ArgumentException("该邮箱已经注册过了，直接登录即可", nameof(rawEmail));
        }

        TimeSpan remaining = await codes.RemainingCooldownAsync(address, cancellationToken).ConfigureAwait(false);

        if (remaining > TimeSpan.Zero)
        {
            throw new AuthRateLimitedException(
                $"验证码刚发过，请 {Math.Ceiling(remaining.TotalSeconds):F0} 秒后再试");
        }

        string code = VerificationCode.Generate();

        await codes.StoreAsync(
            address,
            code,
            AuthPolicy.CodeLifetime,
            AuthPolicy.CodeResendCooldown,
            cancellationToken).ConfigureAwait(false);

        // 样板：库里的那一行优先，没有就用默认的（默认文本自带 {code} 占位符）
        NotificationDefaults.Definition definition = NotificationDefaults.For(NotificationPurposes.RegistrationCode);
        NotificationTemplate effective = NotificationDefaults.Effective(
            definition,
            await templates.FindByPurposeAsync(definition.Purpose, cancellationToken).ConfigureAwait(false));

        // 通道：样板指向哪条就是哪条；指向的那条不存在或不可用，投递器会降级成写日志并如实回答
        NotificationProvider? provider = effective.ProviderId is { } providerId
            ? await providers.GetAsync(providerId, cancellationToken).ConfigureAwait(false)
            : null;

        string subject = NotificationRenderer.RenderSubject(effective.Subject, code);
        string body = NotificationRenderer.Render(
            effective.Body,
            code,
            address.Value,
            (int)AuthPolicy.CodeLifetime.TotalMinutes);

        NotificationDelivery delivery = await mailer
            .SendAsync(provider, address, subject, body, cancellationToken)
            .ConfigureAwait(false);

        // 每一次投递都留一条记录，成功与否都留：验证码只在邮件里出现过一次，
        // 事后要回答"到底发出去没有、发的是什么"，只能靠这张表
        await NotificationLogWriter
            .AppendAsync(logs, provider, definition.Purpose, address.Value, subject, body, delivery, clock, cancellationToken)
            .ConfigureAwait(false);

        return NotificationReplies.ToDto(delivery, $"验证码已发往 {address.Value}");
    }
}

/// <summary>
/// 校验验证码并建账号。
///
/// **不在这里签发会话**：注册完仍要去登录，登录必然经过 MFA 绑定。
/// 少一条"注册即登录"的快捷路径，就少一个可以绕过第二个因子的入口。
/// </summary>
public sealed class RegisterUserUseCase(
    IUserRepository users,
    IUserRoleRepository userRoles,
    IRegistrationCodeStore codes,
    IPolicyWriter policies)
{
    public async Task ExecuteAsync(RegisterRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        EmailAddress address = EmailAddress.Parse(request.Email);

        if (!string.Equals(request.Password, request.ConfirmPassword, StringComparison.Ordinal))
        {
            throw new ArgumentException("两次输入的密码不一致", nameof(request));
        }

        // 口令规则先挡：一次注定失败的注册不该把验证码消耗掉（那会逼使用者再等 60 秒重发）
        PasswordHash.ValidatePolicy(request.Password);

        if (await users.FindByEmailAsync(address, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new ArgumentException("该邮箱已经注册过了，直接登录即可", nameof(request));
        }

        string code = request.Code?.Trim() ?? string.Empty;

        if (!await codes.TryConsumeAsync(address, code, AuthPolicy.MaxCodeAttempts, cancellationToken).ConfigureAwait(false))
        {
            throw new ArgumentException("验证码不正确或已过期，请重新获取", nameof(request));
        }

        User created = await users.CreateAsync(
            new User
            {
                Email = address,
                PasswordHash = PasswordHash.FromPassword(request.Password),

                // 不绑密钥 = 首次登录必须走绑定流程（MFA 是强制的，这里没有"开关"
                // 可以落在"不用 MFA"那一档上）
                TotpSecret = null,
                TotpLastStep = null,
                CreatedAt = LighterTimestamp.FromMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            },
            cancellationToken).ConfigureAwait(false);

        // 首个账号自动成为超级管理员（判据与启动播种共用 SuperadminBootstrap，只有一处）。
        // 之后的账号默认无角色——注册保持开放，但也因此不能顺带拿到任何权限。
        bool bound = await SuperadminBootstrap
            .BindNewAccountAsync(created.Id, users, userRoles, cancellationToken)
            .ConfigureAwait(false);

        // 绑定确实发生时才重建：那是唯一一次"注册动了授权事实源"的时刻。
        // 少了它，第一个账号要等到下一次写操作才真正拿到超管权限——在界面上表现为
        // "刚注册完、什么都没改，却进不去菜单管理"，且刷新也不会好。
        if (bound)
        {
            await policies.RebuildAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
