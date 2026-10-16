using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.Tests;

/// <summary>
/// 登录 / 通知这一组用例共用的假实现。
///
/// 放在单独一个文件而不是各测试文件底部：这一组用例要装配的是同一套依赖
/// （账号、验证码、挑战、限流、通道、样板、投递、记录），在两个文件里各写一遍
/// 就是八份复制的假类型——而它们的行为必须完全一致，否则两条测试会开始"各测各的"。
/// </summary>
internal sealed class FakeClock(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan by) => UtcNow += by;
}

internal sealed class FakeUserRepository : IUserRepository
{
    private readonly List<User> _users = [];
    private long _nextId = 1;

    public int CreateCount { get; private set; }

    public int UpdateCount { get; private set; }

    public IReadOnlyList<User> Users => _users;

    public Task<User?> FindByEmailAsync(EmailAddress email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);

        return Task.FromResult(_users.FirstOrDefault(u => u.Email == email));
    }

    public Task<User?> GetAsync(long id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_users.FirstOrDefault(u => u.Id == id));

    public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_users.Count);

    public Task<IReadOnlyList<long>> ListIdsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<long>>([.. _users.Select(u => u.Id)]);

    public Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<User>>([.. _users.OrderBy(u => u.Id)]);

    public Task<User> CreateAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        CreateCount++;
        User stored = user with { Id = _nextId++ };
        _users.Add(stored);

        return Task.FromResult(stored);
    }

    public Task UpdateAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        UpdateCount++;
        int index = _users.FindIndex(u => u.Id == user.Id);

        if (index < 0)
        {
            throw new ArgumentException($"账号 {user.Id} 不存在", nameof(user));
        }

        _users[index] = user;

        return Task.CompletedTask;
    }

    /// <summary>测试里直接摆一个账号（省去走一遍注册）。</summary>
    public User Seed(string email, string password, TotpSecret? secret = null)
    {
        User user = new()
        {
            Id = _nextId++,
            Email = EmailAddress.Parse(email),
            PasswordHash = PasswordHash.FromPassword(password),
            TotpSecret = secret,
            TotpLastStep = null,
            CreatedAt = LighterTimestamp.FromMilliseconds(0),
        };

        _users.Add(user);

        return user;
    }
}

internal sealed class FakeRegistrationCodeStore : IRegistrationCodeStore
{
    private readonly Dictionary<string, (string Code, int Failures, DateTimeOffset ExpiresAt)> _codes = [];
    private readonly Dictionary<string, DateTimeOffset> _cooldowns = [];
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    public void SetNow(DateTimeOffset now) => _now = now;

    public Task<TimeSpan> RemainingCooldownAsync(EmailAddress email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);

        if (!_cooldowns.TryGetValue(email.Value, out DateTimeOffset until))
        {
            return Task.FromResult(TimeSpan.Zero);
        }

        TimeSpan remaining = until - _now;

        return Task.FromResult(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
    }

    public Task StoreAsync(
        EmailAddress email,
        string code,
        TimeSpan lifetime,
        TimeSpan resendCooldown,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);

        _codes[email.Value] = (code, 0, _now + lifetime);
        _cooldowns[email.Value] = _now + resendCooldown;

        return Task.CompletedTask;
    }

    public Task<bool> TryConsumeAsync(
        EmailAddress email,
        string code,
        int maxAttempts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);

        if (!_codes.TryGetValue(email.Value, out (string Code, int Failures, DateTimeOffset ExpiresAt) entry)
            || entry.ExpiresAt <= _now)
        {
            return Task.FromResult(false);
        }

        if (string.Equals(entry.Code, code, StringComparison.Ordinal))
        {
            _codes.Remove(email.Value);
            _cooldowns.Remove(email.Value);

            return Task.FromResult(true);
        }

        entry.Failures++;

        if (entry.Failures >= maxAttempts)
        {
            _codes.Remove(email.Value);
        }
        else
        {
            _codes[email.Value] = entry;
        }

        return Task.FromResult(false);
    }

    /// <summary>测试里直接读出码（真实实现里它只在邮件/日志里出现）。</summary>
    public string? Peek(EmailAddress email) =>
        _codes.TryGetValue(email.Value, out (string Code, int Failures, DateTimeOffset ExpiresAt) entry)
            ? entry.Code
            : null;
}

internal sealed class FakeMfaChallengeStore : IMfaChallengeStore
{
    private readonly Dictionary<string, (MfaChallenge Challenge, int Failures, DateTimeOffset ExpiresAt)> _challenges = [];
    private long _nextId = 1;

    public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;

    public Task<string> CreateAsync(
        long userId,
        TotpSecret? pendingSecret,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        string id = $"challenge-{_nextId++}";
        _challenges[id] = (new MfaChallenge(userId, pendingSecret), 0, Now + lifetime);

        return Task.FromResult(id);
    }

    public Task<MfaChallenge?> GetAsync(string challengeId, CancellationToken cancellationToken = default)
    {
        if (!_challenges.TryGetValue(challengeId, out (MfaChallenge Challenge, int Failures, DateTimeOffset ExpiresAt) entry)
            || entry.ExpiresAt <= Now)
        {
            return Task.FromResult<MfaChallenge?>(null);
        }

        return Task.FromResult<MfaChallenge?>(entry.Challenge);
    }

    public Task<bool> RegisterFailureAsync(
        string challengeId,
        int maxAttempts,
        CancellationToken cancellationToken = default)
    {
        if (!_challenges.TryGetValue(challengeId, out (MfaChallenge Challenge, int Failures, DateTimeOffset ExpiresAt) entry))
        {
            return Task.FromResult(false);
        }

        entry.Failures++;
        _challenges[challengeId] = entry;

        bool alive = entry.Failures < maxAttempts;

        if (!alive)
        {
            _challenges.Remove(challengeId);
        }

        return Task.FromResult(alive);
    }

    public Task RemoveAsync(string challengeId, CancellationToken cancellationToken = default)
    {
        _challenges.Remove(challengeId);

        return Task.CompletedTask;
    }

    public bool Contains(string challengeId) => _challenges.ContainsKey(challengeId);

    /// <summary>摆一个挑战（用于直接测"第二步已过期/类型不对"这类路径）。</summary>
    public string Seed(long userId, TotpSecret? pendingSecret = null)
    {
        string id = $"challenge-{_nextId++}";
        _challenges[id] = (new MfaChallenge(userId, pendingSecret), 0, Now.AddHours(1));

        return id;
    }
}

internal sealed class FakeLoginThrottle : ILoginThrottle
{
    private readonly Dictionary<string, int> _failures = [];
    private readonly Dictionary<string, DateTimeOffset> _lockedUntil = [];

    public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;

    public Task<TimeSpan> RemainingLockoutAsync(EmailAddress email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);

        if (!_lockedUntil.TryGetValue(email.Value, out DateTimeOffset until))
        {
            return Task.FromResult(TimeSpan.Zero);
        }

        TimeSpan remaining = until - Now;

        return Task.FromResult(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
    }

    public Task RecordFailureAsync(
        EmailAddress email,
        int maxFailures,
        TimeSpan lockout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);

        int failures = _failures.GetValueOrDefault(email.Value) + 1;

        if (failures >= maxFailures)
        {
            _lockedUntil[email.Value] = Now + lockout;
            _failures.Remove(email.Value);
        }
        else
        {
            _failures[email.Value] = failures;
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync(EmailAddress email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);

        _failures.Remove(email.Value);
        _lockedUntil.Remove(email.Value);

        return Task.CompletedTask;
    }
}

internal sealed class FakeNotificationProviderRepository : INotificationProviderRepository
{
    private readonly List<NotificationProvider> _providers = [];
    private long _nextId = 1;

    /// <summary>摆进去的通道（测试直接读它断言）。</summary>
    public IReadOnlyList<NotificationProvider> Providers => _providers;

    public Task<IReadOnlyList<NotificationProvider>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NotificationProvider>>(_providers);

    public Task<NotificationProvider?> GetAsync(long id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_providers.FirstOrDefault(p => p.Id == id));

    public Task<NotificationProvider> CreateAsync(
        NotificationProvider provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        NotificationProvider stored = provider with { Id = _nextId++ };
        _providers.Add(stored);

        return Task.FromResult(stored);
    }

    public Task UpdateAsync(NotificationProvider provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        int index = _providers.FindIndex(p => p.Id == provider.Id);
        _providers[index] = provider;

        return Task.CompletedTask;
    }

    public Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        _providers.RemoveAll(p => p.Id == id);

        return Task.CompletedTask;
    }

    public Task<int> CountTemplatesUsingAsync(long id, CancellationToken cancellationToken = default) =>
        Task.FromResult(TemplatesUsing.GetValueOrDefault(id));

    /// <summary>摆一个"有几条样板在用它"的答案（删除前的检查）。</summary>
    public Dictionary<long, int> TemplatesUsing { get; } = [];

    public NotificationProvider Seed(
        string displayName = "测试通道",
        bool isEnabled = true,
        string? host = "smtp.example.com")
    {
        NotificationProvider provider = new()
        {
            Id = _nextId++,
            Kind = Domain.Constants.NotificationKinds.Email,
            DisplayName = displayName,
            IsEnabled = isEnabled,
            Host = host,
            Port = 587,
            UseStartTls = true,
            UserName = "user@example.com",
            Password = "app-password",
            FromAddress = "user@example.com",
            FromName = "PoolPilot",
            CreatedAt = LighterTimestamp.FromMilliseconds(0),
        };

        _providers.Add(provider);

        return provider;
    }
}

internal sealed class FakeNotificationTemplateRepository : INotificationTemplateRepository
{
    private readonly List<NotificationTemplate> _templates = [];
    private long _nextId = 1;

    /// <summary>摆进去的样板（测试直接读它断言）。</summary>
    public IReadOnlyList<NotificationTemplate> Templates => _templates;

    public Task<IReadOnlyList<NotificationTemplate>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NotificationTemplate>>(_templates);

    public Task<NotificationTemplate?> FindByPurposeAsync(
        string purpose,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_templates.FirstOrDefault(t => t.Purpose == purpose));

    public Task<NotificationTemplate> CreateAsync(
        NotificationTemplate notificationTemplate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notificationTemplate);

        NotificationTemplate stored = notificationTemplate with { Id = _nextId++ };
        _templates.Add(stored);

        return Task.FromResult(stored);
    }

    public Task UpdateAsync(
        NotificationTemplate notificationTemplate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notificationTemplate);

        int index = _templates.FindIndex(t => t.Id == notificationTemplate.Id);
        _templates[index] = notificationTemplate;

        return Task.CompletedTask;
    }

    public void Seed(NotificationTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        _templates.Add(template with { Id = _nextId++ });
    }
}

internal sealed class FakeNotificationMailer : INotificationMailer
{
    public List<(NotificationProvider? Provider, string Recipient, string Subject, string Body)> Sent { get; } = [];

    /// <summary>下一次投递返回什么。默认"真发出去了"。</summary>
    public Func<NotificationDelivery> NextDelivery { get; set; } = NotificationDelivery.Sent;

    public Task<NotificationDelivery> SendAsync(
        NotificationProvider? provider,
        EmailAddress recipient,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipient);

        Sent.Add((provider, recipient.Value, subject, body));

        return Task.FromResult(NextDelivery());
    }
}

internal sealed class FakeNotificationLogRepository : INotificationLogRepository
{
    private readonly List<NotificationLog> _entries = [];
    private long _nextId = 1;

    public IReadOnlyList<NotificationLog> Entries => _entries;

    public Task AppendAsync(NotificationLog entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _entries.Add(entry with { Id = _nextId++ });

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NotificationLog>> ListAsync(
        int limit,
        string? purpose = null,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<NotificationLog> query = _entries.OrderByDescending(e => e.Id);

        if (!string.IsNullOrWhiteSpace(purpose))
        {
            query = query.Where(e => e.Purpose == purpose);
        }

        return Task.FromResult<IReadOnlyList<NotificationLog>>([.. query.Take(limit)]);
    }
}
