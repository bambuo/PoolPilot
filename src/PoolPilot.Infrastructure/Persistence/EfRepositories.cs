using Microsoft.EntityFrameworkCore;

using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Infrastructure.Persistence;

/// <summary>事件存档（EF Core + SQLite）。</summary>
public sealed class PoolEventRepository(PoolPilotDbContext db) : IPoolEventRepository
{
    /// <summary>批量写入的分片大小。合规于项目的批量事务约定（5000 条/批）。</summary>
    private const int BatchSize = 5000;

    public async Task AppendAsync(
        IReadOnlyList<PoolEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        if (events.Count == 0)
        {
            return;
        }

        foreach (PoolEvent[] chunk in events.Chunk(BatchSize))
        {
            db.PoolEvents.AddRange(chunk.Select(PoolEventRecord.FromDomain));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<PoolEvent>> GetRecentAsync(
        AccountIndex account,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        List<PoolEventRecord> rows = await db.PoolEvents
            .AsNoTracking()
            .Where(e => e.AccountIndex == account.Value)
            .OrderByDescending(e => e.DetectedAtUtcTicks)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task<IReadOnlyList<PoolEvent>> GetRecentAcrossPoolsAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        List<PoolEventRecord> rows = await db.PoolEvents
            .AsNoTracking()
            .OrderByDescending(e => e.DetectedAtUtcTicks)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task<IReadOnlyList<PoolSignalStats>> GetSignalStatsAsync(
        CancellationToken cancellationToken = default)
    {
        // 分组聚合在数据库侧完成：只回传"池 + 条数 + 最近时刻"三列，
        // 不把事件行拉回内存再算。
        var rows = await db.PoolEvents
            .AsNoTracking()
            .GroupBy(e => e.AccountIndex)
            .Select(g => new
            {
                Pool = g.Key,
                Count = g.Count(),
                LastTicks = g.Max(e => e.DetectedAtUtcTicks),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. rows.Select(r => new PoolSignalStats(
                AccountIndex.From(r.Pool),
                r.Count,
                LighterTimestamp.FromMilliseconds(
                    new DateTimeOffset(r.LastTicks, TimeSpan.Zero).ToUnixTimeMilliseconds()))),
        ];
    }
}

/// <summary>跟单对照存档（EF Core + SQLite）。</summary>
public sealed class CopyFillRepository(PoolPilotDbContext db) : ICopyFillRepository
{
    private const int BatchSize = 5000;

    public async Task AppendAsync(
        IReadOnlyList<CopyFill> fills,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fills);

        if (fills.Count == 0)
        {
            return;
        }

        foreach (CopyFill[] chunk in fills.Chunk(BatchSize))
        {
            db.CopyFills.AddRange(chunk.Select(CopyFillRecord.FromDomain));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<CopyFill>> GetRecentAsync(
        AccountIndex account,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        List<CopyFillRecord> rows = await db.CopyFills
            .AsNoTracking()
            .Where(f => f.AccountIndex == account.Value)
            .OrderByDescending(f => f.ObservedAtUtcTicks)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task<IReadOnlySet<long>> GetRecordedTradeIdsAsync(
        AccountIndex account,
        IReadOnlyList<long> tradeIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(tradeIds);

        if (tradeIds.Count == 0)
        {
            return new HashSet<long>();
        }

        List<long> found = await db.CopyFills
            .AsNoTracking()
            .Where(f => f.AccountIndex == account.Value && tradeIds.Contains(f.SourceTradeId))
            .Select(f => f.SourceTradeId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new HashSet<long>(found);
    }
}

/// <summary>带单员存档（EF Core + SQLite）。</summary>
public sealed class LeadTraderRepository(PoolPilotDbContext db) : ILeadTraderRepository
{
    public async Task<LeadTrader?> GetAsync(
        AccountIndex account,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        LeadTraderRecord? row = await db.LeadTraders
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.AccountIndex == account.Value, cancellationToken)
            .ConfigureAwait(false);

        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<LeadTrader>> ListAsync(CancellationToken cancellationToken = default)
    {
        List<LeadTraderRecord> rows = await db.LeadTraders
            .AsNoTracking()
            .OrderBy(l => l.AccountIndex)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    /// <summary>写入或覆盖。名称与创建时间以**首次创建**为准：改名不该改写历史。</summary>
    public async Task UpsertAsync(LeadTrader lead, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lead);

        LeadTraderRecord? existing = await db.LeadTraders
            .FirstOrDefaultAsync(l => l.AccountIndex == lead.AccountIndex.Value, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            db.LeadTraders.Add(LeadTraderRecord.FromDomain(lead));
        }
        else
        {
            existing.IsEnabled = lead.IsEnabled;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(AccountIndex account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        await db.LeadTraders
            .Where(l => l.AccountIndex == account.Value)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>跟单员存档（EF Core + SQLite）。</summary>
public sealed class FollowerRepository(PoolPilotDbContext db) : IFollowerRepository
{
    public async Task<Follower?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        FollowerRecord? row = await db.Followers
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<Follower>> ListAsync(CancellationToken cancellationToken = default)
    {
        List<FollowerRecord> rows = await db.Followers
            .AsNoTracking()
            .OrderBy(f => f.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task<IReadOnlyList<Follower>> ListByLeadAsync(
        AccountIndex leadAccountIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(leadAccountIndex);

        List<FollowerRecord> rows = await db.Followers
            .AsNoTracking()
            .Where(f => f.LeadAccountIndex == leadAccountIndex.Value)
            .OrderBy(f => f.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    /// <summary>新建。主键由数据库分配，返回的对象带着分配好的 Id。</summary>
    public async Task<Follower> CreateAsync(Follower follower, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(follower);

        FollowerRecord row = FollowerRecord.FromDomain(follower with { Id = 0L });
        db.Followers.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return row.ToDomain();
    }

    public async Task UpdateAsync(Follower follower, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(follower);

        FollowerRecord? existing = await db.Followers
            .FirstOrDefaultAsync(f => f.Id == follower.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"跟单员 {follower.Id} 不存在");

        existing.InitialCapital = follower.InitialCapital.Value;
        existing.IsActive = follower.IsActive;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await db.Followers
            .Where(f => f.Id == id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>跟单员流水与权益点存档（EF Core + SQLite）。</summary>
public sealed class FollowerLedgerRepository(PoolPilotDbContext db) : IFollowerLedgerRepository
{
    private const int BatchSize = 5000;

    public async Task AppendFillsAsync(
        IReadOnlyList<PaperFill> fills,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fills);

        if (fills.Count == 0)
        {
            return;
        }

        foreach (PaperFill[] chunk in fills.Chunk(BatchSize))
        {
            db.FollowerFills.AddRange(chunk.Select(FollowerFillRecord.FromDomain));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 按时间**升序**返回流水。
    ///
    /// 取"最近 limit 条"用降序拿完再反转：直接升序取前 limit 条会拿到最旧的记录，
    /// 那样重放出来的仓位是历史某一刻的状态，与当前盘口对不上。
    /// </summary>
    public async Task<IReadOnlyList<PaperFill>> GetFillsAsync(
        long followerId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        List<FollowerFillRecord> rows = await db.FollowerFills
            .AsNoTracking()
            .Where(f => f.FollowerId == followerId)
            .OrderByDescending(f => f.ObservedAtUtcTicks)
            .ThenByDescending(f => f.Id)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        rows.Reverse();

        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task AppendEquityPointAsync(
        PaperEquityPoint point,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(point);

        db.FollowerEquityPoints.Add(FollowerEquityPointRecord.FromDomain(point));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PaperEquityPoint>> GetEquityCurveAsync(
        long followerId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        List<FollowerEquityPointRecord> rows = await db.FollowerEquityPoints
            .AsNoTracking()
            .Where(p => p.FollowerId == followerId)
            .OrderByDescending(p => p.TakenAtUtcTicks)
            .ThenByDescending(p => p.Id)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        rows.Reverse();

        return [.. rows.Select(r => r.ToDomain())];
    }

    /// <summary>清空该跟单员的流水与权益点。改本金时必须调用，理由见用例说明。</summary>
    public async Task ClearHistoryAsync(long followerId, CancellationToken cancellationToken = default)
    {
        await db.FollowerFills
            .Where(f => f.FollowerId == followerId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        await db.FollowerEquityPoints
            .Where(p => p.FollowerId == followerId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>交易所配置存档（EF Core + SQLite）。凭据按原样存取（明文，见 ExchangeConfig 的说明）。</summary>
public sealed class ExchangeConfigRepository(PoolPilotDbContext db) : IExchangeConfigRepository
{
    public async Task<ExchangeConfig?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        ExchangeConfigRecord? row = await db.ExchangeConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<ExchangeConfig>> ListAsync(CancellationToken cancellationToken = default)
    {
        List<ExchangeConfigRecord> rows = await db.ExchangeConfigs
            .AsNoTracking()
            .OrderBy(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task<ExchangeConfig> CreateAsync(
        ExchangeConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        ExchangeConfigRecord row = ExchangeConfigRecord.FromDomain(config with { Id = 0L });
        db.ExchangeConfigs.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return row.ToDomain();
    }

    public async Task UpdateAsync(ExchangeConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        ExchangeConfigRecord? existing = await db.ExchangeConfigs
            .FirstOrDefaultAsync(e => e.Id == config.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"交易所配置 {config.Id} 不存在");

        // 不换实体、逐字段覆盖：换实体容易漏字段，而漏掉凭据列会把已存的密钥清空
        existing.DisplayName = config.DisplayName;
        existing.Kind = ExchangeEnumText.ToText(config.Kind);
        existing.IsTestnet = config.IsTestnet;
        existing.RestBaseUrl = config.RestBaseUrl;
        existing.WsUrl = config.WsUrl;
        existing.MakerFeePercent = config.MakerFeePercent;
        existing.TakerFeePercent = config.TakerFeePercent;
        existing.IsEnabled = config.IsEnabled;
        existing.Notes = config.Notes;
        existing.ApiKey = config.ApiKey;
        existing.ApiSecret = config.ApiSecret;
        existing.Passphrase = config.Passphrase;
        existing.ApiKeyHint = config.ApiKeyHint;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await db.ExchangeConfigs
            .Where(e => e.Id == id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>标的映射存档（EF Core + SQLite）。</summary>
public sealed class SymbolMappingRepository(PoolPilotDbContext db) : ISymbolMappingRepository
{
    public async Task<IReadOnlyList<SymbolMapping>> ListAsync(CancellationToken cancellationToken = default)
    {
        List<SymbolMappingRecord> rows = await db.SymbolMappings
            .AsNoTracking()
            .OrderBy(m => m.TargetPlatform)
            .ThenBy(m => m.SourceBase)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task<SymbolMapping?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        SymbolMappingRecord? row = await db.SymbolMappings
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return row?.ToDomain();
    }

    public async Task<SymbolMapping?> FindAsync(
        string targetPlatform,
        string sourceBase,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPlatform);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBase);

        SymbolMappingRecord? row = await db.SymbolMappings
            .AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.TargetPlatform == targetPlatform && m.SourceBase == sourceBase,
                cancellationToken)
            .ConfigureAwait(false);

        return row?.ToDomain();
    }

    public async Task<SymbolMapping> CreateAsync(
        SymbolMapping mapping,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        SymbolMappingRecord row = SymbolMappingRecord.FromDomain(mapping with { Id = 0L });
        db.SymbolMappings.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return row.ToDomain();
    }

    public async Task UpdateAsync(SymbolMapping mapping, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        SymbolMappingRecord? existing = await db.SymbolMappings
            .FirstOrDefaultAsync(m => m.Id == mapping.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"标的映射 {mapping.Id} 不存在");

        existing.TargetBase = mapping.TargetBase;
        existing.TargetQuote = mapping.TargetQuote;
        existing.TargetContractType = mapping.TargetContractType;
        existing.TargetIsTradFi = mapping.TargetIsTradFi;
        existing.Origin = mapping.Origin.ToString();
        existing.IsEnabled = mapping.IsEnabled;
        existing.SourcePrice = mapping.SourcePrice;
        existing.TargetPrice = mapping.TargetPrice;
        existing.VerifiedAtUtcTicks = mapping.VerifiedAt?.Utc.UtcTicks;
        existing.Note = mapping.Note;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await db.SymbolMappings
            .Where(m => m.Id == id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>实盘流水（只追加，不修改——它是日志，不是账）。</summary>
public sealed class LiveOrderRepository(PoolPilotDbContext db) : ILiveOrderRepository
{
    public async Task AddAsync(LiveOrder order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);

        db.LiveOrders.Add(LiveOrderRecord.FromDomain(order));

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LiveOrder>> ListAsync(
        long followerId,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        List<LiveOrderRecord> rows = await db.LiveOrders
            .AsNoTracking()
            .Where(o => o.FollowerId == followerId)
            .OrderByDescending(o => o.Id)
            .Take(Math.Max(1, limit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task<(int Placed, int NotFollowed)> CountAsync(
        long followerId,
        CancellationToken cancellationToken = default)
    {
        // 一次分组查询取齐，不为了两个数打两次库
        List<(string Status, int Count)> counts = await db.LiveOrders
            .AsNoTracking()
            .Where(o => o.FollowerId == followerId)
            .GroupBy(o => o.Status)
            .Select(g => new ValueTuple<string, int>(g.Key, g.Count()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        int placed = counts.FirstOrDefault(c => c.Status == LiveOrderStatus.Placed).Count;
        int skipped = counts.FirstOrDefault(c => c.Status == LiveOrderStatus.Skipped).Count;
        int failed = counts.FirstOrDefault(c => c.Status == LiveOrderStatus.Failed).Count;

        return (placed, skipped + failed);
    }

    public async Task<IReadOnlyList<string>> SourcesForAsync(
        long followerId,
        string targetBase,
        string targetQuote,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetBase);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetQuote);

        List<string> sources = await db.LiveOrders
            .AsNoTracking()
            .Where(o => o.FollowerId == followerId
                && o.TargetBase == targetBase
                && o.TargetQuote == targetQuote)
            .OrderByDescending(o => o.Id)
            .Take(Math.Max(1, limit))
            .Select(o => o.SourceSymbol)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return sources;
    }
}

/// <summary>登录账号的存档（EF Core + SQLite）。</summary>
public sealed class UserRepository(PoolPilotDbContext db) : IUserRepository
{
    public async Task<User?> FindByEmailAsync(EmailAddress email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);

        UserRecord? row = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == email.Value, cancellationToken)
            .ConfigureAwait(false);

        return row?.ToDomain();
    }

    public async Task<User?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        UserRecord? row = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return row?.ToDomain();
    }

    public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        db.Users.CountAsync(cancellationToken);

    public async Task<User> CreateAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        UserRecord row = UserRecord.FromDomain(user with { Id = 0L });
        db.Users.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return row.ToDomain();
    }

    public async Task UpdateAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        UserRecord row = await db.Users
            .FirstOrDefaultAsync(u => u.Id == user.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ArgumentException($"账号 {user.Id} 不存在，无法更新", nameof(user));

        // 只回写可变的那两列：邮箱与口令哈希没有"改"这个动作（改口令要走另一条流程，
        // 那时它会有自己的用例与审计，而不是顺手在登录路径上更新）
        row.TotpSecret = user.TotpSecret?.ToBase32();
        row.TotpLastStep = user.TotpLastStep;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>通知通道的存档（EF Core + SQLite）。</summary>
public sealed class NotificationProviderRepository(PoolPilotDbContext db) : INotificationProviderRepository
{
    public async Task<IReadOnlyList<NotificationProvider>> ListAsync(CancellationToken cancellationToken = default)
    {
        List<NotificationProviderRecord> rows = await db.NotificationProviders
            .AsNoTracking()
            .OrderBy(p => p.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task<NotificationProvider?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        NotificationProviderRecord? row = await db.NotificationProviders
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return row?.ToDomain();
    }

    public async Task<NotificationProvider> CreateAsync(
        NotificationProvider provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        NotificationProviderRecord row = NotificationProviderRecord.FromDomain(provider with { Id = 0L });
        db.NotificationProviders.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return row.ToDomain();
    }

    public async Task UpdateAsync(NotificationProvider provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        NotificationProviderRecord row = await db.NotificationProviders
            .FirstOrDefaultAsync(p => p.Id == provider.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ArgumentException($"通知通道 {provider.Id} 不存在，无法更新", nameof(provider));

        row.Kind = provider.Kind;
        row.DisplayName = provider.DisplayName;
        row.IsEnabled = provider.IsEnabled;
        row.Host = provider.Host;
        row.Port = provider.Port;
        row.UseStartTls = provider.UseStartTls;
        row.UserName = provider.UserName;
        row.Password = provider.Password;
        row.FromAddress = provider.FromAddress;
        row.FromName = provider.FromName;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await db.NotificationProviders
            .Where(p => p.Id == id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<int> CountTemplatesUsingAsync(long id, CancellationToken cancellationToken = default) =>
        db.NotificationTemplates.CountAsync(t => t.ProviderId == id, cancellationToken);
}

/// <summary>通知样板的存档（EF Core + SQLite）。</summary>
public sealed class NotificationTemplateRepository(PoolPilotDbContext db) : INotificationTemplateRepository
{
    public async Task<IReadOnlyList<NotificationTemplate>> ListAsync(CancellationToken cancellationToken = default)
    {
        List<NotificationTemplateRecord> rows = await db.NotificationTemplates
            .AsNoTracking()
            .OrderBy(t => t.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task<NotificationTemplate?> FindByPurposeAsync(
        string purpose,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        NotificationTemplateRecord? row = await db.NotificationTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Purpose == purpose, cancellationToken)
            .ConfigureAwait(false);

        return row?.ToDomain();
    }

    public async Task<NotificationTemplate> CreateAsync(
        NotificationTemplate notificationTemplate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notificationTemplate);

        NotificationTemplateRecord row = NotificationTemplateRecord.FromDomain(notificationTemplate with { Id = 0L });
        db.NotificationTemplates.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return row.ToDomain();
    }

    public async Task UpdateAsync(
        NotificationTemplate notificationTemplate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notificationTemplate);

        NotificationTemplateRecord row = await db.NotificationTemplates
            .FirstOrDefaultAsync(t => t.Id == notificationTemplate.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ArgumentException($"通知样板 {notificationTemplate.Id} 不存在，无法更新", nameof(notificationTemplate));

        row.Subject = notificationTemplate.Subject;
        row.Body = notificationTemplate.Body;
        row.ProviderId = notificationTemplate.ProviderId;
        row.UpdatedAtUtc = notificationTemplate.UpdatedAt.Utc;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>通知投递记录的存档（EF Core + SQLite）。只追加与读取。</summary>
public sealed class NotificationLogRepository(PoolPilotDbContext db) : INotificationLogRepository
{
    public async Task AppendAsync(NotificationLog entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        db.NotificationLogs.Add(NotificationLogRecord.FromDomain(entry with { Id = 0L }));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NotificationLog>> ListAsync(
        int limit,
        string? purpose = null,
        CancellationToken cancellationToken = default)
    {
        IQueryable<NotificationLogRecord> query = db.NotificationLogs.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(purpose))
        {
            query = query.Where(l => l.Purpose == purpose);
        }

        List<NotificationLogRecord> rows = await query
            .OrderByDescending(l => l.Id)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }
}
