using Microsoft.EntityFrameworkCore;

using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.ValueObjects;

// 实体属性名 AccountIndex 会遮蔽同名类型，用别名消歧
using AccountIdx = PoolPilot.Domain.ValueObjects.AccountIndex;

// 同理：UserRecord 的属性名与这两个值对象同名
using PasswordHashValue = PoolPilot.Domain.ValueObjects.PasswordHash;
using TotpSecretValue = PoolPilot.Domain.ValueObjects.TotpSecret;

namespace PoolPilot.Infrastructure.Persistence;

/// <summary>
/// 数据库上下文。
///
/// 建库用 <c>EnsureCreated</c> 而不是迁移：本机 dotnet-ef 需要额外的 net8 runtime 才能工作
/// （见 AGENTS.md 铁律 8），而本项目的表结构简单且数据可重新采集，不值得为此引入迁移工具链。
///
/// 持久化的范围刻意最小化：只存**无法重新获取**的东西——监控到的事件流与跟单对照记录。
/// 池元数据、净值序列、快照都能随时从上游重拉，不必落库。
/// </summary>
public sealed class PoolPilotDbContext(DbContextOptions<PoolPilotDbContext> options) : DbContext(options)
{
    public DbSet<PoolEventRecord> PoolEvents => Set<PoolEventRecord>();

    public DbSet<CopyFillRecord> CopyFills => Set<CopyFillRecord>();

    public DbSet<LeadTraderRecord> LeadTraders => Set<LeadTraderRecord>();

    public DbSet<FollowerRecord> Followers => Set<FollowerRecord>();

    public DbSet<FollowerFillRecord> FollowerFills => Set<FollowerFillRecord>();

    public DbSet<FollowerEquityPointRecord> FollowerEquityPoints => Set<FollowerEquityPointRecord>();

    public DbSet<ExchangeConfigRecord> ExchangeConfigs => Set<ExchangeConfigRecord>();

    public DbSet<SymbolMappingRecord> SymbolMappings => Set<SymbolMappingRecord>();

    public DbSet<LiveOrderRecord> LiveOrders => Set<LiveOrderRecord>();

    /// <summary>登录账号。与上面那些业务表没有关系：那些说的是"交易所里发生了什么"。</summary>
    public DbSet<UserRecord> Users => Set<UserRecord>();

    /// <summary>通知通道、样板与投递记录（见 notification 三张表的说明）。</summary>
    public DbSet<NotificationProviderRecord> NotificationProviders => Set<NotificationProviderRecord>();

    public DbSet<NotificationTemplateRecord> NotificationTemplates => Set<NotificationTemplateRecord>();

    public DbSet<NotificationLogRecord> NotificationLogs => Set<NotificationLogRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<PoolEventRecord>(entity =>
        {
            entity.ToTable("pool_events");
            entity.HasKey(e => e.Id);

            // 账户索引远超 int32，SQLite 的 INTEGER 是 64 位，可直接承载
            entity.Property(e => e.AccountIndex).IsRequired();
            entity.HasIndex(e => new { e.AccountIndex, e.DetectedAtUtc });
            entity.HasIndex(e => e.DetectedAtUtc);
        });

        modelBuilder.Entity<CopyFillRecord>(entity =>
        {
            entity.ToTable("copy_fills");
            entity.HasKey(e => e.Id);

            // 同一笔池成交只允许记录一次——重连回填会重复投递
            entity.HasIndex(e => new { e.AccountIndex, e.SourceTradeId }).IsUnique();
            entity.HasIndex(e => new { e.AccountIndex, e.ObservedAtUtc });
        });

        // ── 带单员 ──────────────────────────────────────────────
        // 池索引即主键：一个池最多一个带单员，"同一个池建出两个观察者"在结构上被杜绝。
        modelBuilder.Entity<LeadTraderRecord>(entity =>
        {
            entity.ToTable("lead_traders");
            entity.HasKey(e => e.AccountIndex);
        });

        // ── 跟单员 ──────────────────────────────────────────────
        // 自增主键：跟单员的身份**不借用池索引**——一个池可以挂多个跟单员，
        // 各投一份本金、各记一套账。用池索引当主键就写不出这种结构。
        modelBuilder.Entity<FollowerRecord>(entity =>
        {
            entity.ToTable("followers");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => e.LeadAccountIndex);
        });

        modelBuilder.Entity<FollowerFillRecord>(entity =>
        {
            entity.ToTable("follower_fills");
            entity.HasKey(e => e.Id);

            // 重放仓位账要求按时间升序读取，故时间列建索引
            entity.HasIndex(e => new { e.FollowerId, e.ObservedAtUtcTicks });
        });

        modelBuilder.Entity<FollowerEquityPointRecord>(entity =>
        {
            entity.ToTable("follower_equity_points");
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => new { e.FollowerId, e.TakenAtUtcTicks });
        });

        // ── 标的映射 ────────────────────────────────────────────
        // 唯一键是 (目标平台, 源资产币, 源交易币)：映射是平台级的知识，与你有几个账号无关
        modelBuilder.Entity<SymbolMappingRecord>(entity =>
        {
            entity.ToTable("symbol_mappings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => new { e.TargetPlatform, e.SourceBase, e.SourceQuote }).IsUnique();
        });

        // ── 交易所配置 ──────────────────────────────────────────
        // 自增主键而不是"交易所标识"：同一个交易所可以配多条（两个币安账号是常态）。
        // 凭据列是**明文**（曾经加密过，见 ExchangeConfig 的说明：密钥与密文同库等于没防）。
        modelBuilder.Entity<ExchangeConfigRecord>(entity =>
        {
            entity.ToTable("exchange_configs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
        });

        // ── 实盘流水 ────────────────────────────────────────────
        // 与纸面台账（follower_fills）分开两张表，不合并：一张是"账"（要重放才能算持仓与权益），
        // 一张是"日志"（持仓与权益以交易所为准）。合成一张会让每一次读取都要先分清这行属于哪种。
        modelBuilder.Entity<LiveOrderRecord>(entity =>
        {
            entity.ToTable("live_orders");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();

            // 按跟单员取"最近若干条"是唯一的读法，索引跟着它建
            entity.HasIndex(e => new { e.FollowerId, e.Id });
        });

        // ── 通知：通道 / 样板 / 投递记录 ────────────────────────
        // 三张表而不是一张：通道参数是"我有什么账号"（换一次 SMTP 账号所有通知一起换），
        // 样板是"每类通知说什么"，记录是"当时实际发生了什么"（只追加，永不改写）。
        // 合成一张的话，加第二类通知就要把 SMTP 参数抄第二遍，而历史记录也会被后续编辑污染。
        modelBuilder.Entity<NotificationProviderRecord>(entity =>
        {
            entity.ToTable("notification_providers");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
        });

        // 一类通知一条：用途唯一，否则"发注册码用哪条样板"就得靠排序去猜
        modelBuilder.Entity<NotificationTemplateRecord>(entity =>
        {
            entity.ToTable("notification_templates");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => e.Purpose).IsUnique();
        });

        // 只追加。按时间倒序取最近若干条是唯一的读法，索引跟着它建
        modelBuilder.Entity<NotificationLogRecord>(entity =>
        {
            entity.ToTable("notification_logs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => e.SentAtUtcTicks);
        });

        // ── 登录账号 ────────────────────────────────────────────
        // 邮箱唯一：它同时是登录名，允许两条就是允许两个"同名账号"，登录时连该验谁的密码都说不清。
        // 归一化（去空白 + 转小写）在 EmailAddress 里做，所以这里直接比字符串即可。
        modelBuilder.Entity<UserRecord>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => e.Email).IsUnique();
        });
    }
}

/// <summary>
/// 实盘流水表行。
///
/// 目标交易对按**系统标准两段**存（<c>TargetBase</c> + <c>TargetQuote</c>），
/// 不是平台自己的合约代码——那张表已经在标的表示上踩过一次坑，这里不再重复。
/// </summary>
public sealed class LiveOrderRecord
{
    public long Id { get; set; }

    public long FollowerId { get; set; }

    public long ExchangeConfigId { get; set; }

    public string Platform { get; set; } = string.Empty;

    public string TargetBase { get; set; } = string.Empty;

    public string TargetQuote { get; set; } = string.Empty;

    public string SourceSymbol { get; set; } = string.Empty;

    public string Intent { get; set; } = string.Empty;

    public string Side { get; set; } = string.Empty;

    public string? PositionSide { get; set; }

    public decimal Quantity { get; set; }

    public decimal Price { get; set; }

    /// <summary>这一单所涉仓位的开仓均价（开仓单=自己的成交均价；平仓单=被平仓位的成本线）。0 = 未知（旧行）。</summary>
    public decimal EntryPrice { get; set; }

    /// <summary>平仓成交价；只有平仓单有。</summary>
    public decimal? ClosePrice { get; set; }

    public decimal NotionalUsd { get; set; }

    public decimal PoolNotional { get; set; }

    public decimal TargetNotionalUsd { get; set; }

    public decimal PoolEquity { get; set; }

    public decimal Capital { get; set; }

    /// <summary><c>signal</c> / <c>manual</c>。</summary>
    public string Trigger { get; set; } = LiveOrderTrigger.Signal;

    /// <summary><c>placed</c> / <c>skipped</c> / <c>failed</c>。</summary>
    public string Status { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public string? VenueOrderId { get; set; }

    public string? ClientOrderId { get; set; }

    public long CreatedAtUtcTicks { get; set; }

    public DateTimeOffset CreatedAtUtc
    {
        get => new(CreatedAtUtcTicks, TimeSpan.Zero);
        set => CreatedAtUtcTicks = value.UtcTicks;
    }

    public static LiveOrderRecord FromDomain(LiveOrder source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new LiveOrderRecord
        {
            Id = source.Id,
            FollowerId = source.FollowerId,
            ExchangeConfigId = source.ExchangeConfigId,
            Platform = source.Platform,
            TargetBase = source.TargetBase,
            TargetQuote = source.TargetQuote,
            SourceSymbol = source.SourceSymbol,
            Intent = source.Intent,
            Side = source.Side,
            PositionSide = source.PositionSide,
            Quantity = source.Quantity,
            Price = source.Price,
            EntryPrice = source.EntryPrice,
            ClosePrice = source.ClosePrice,
            NotionalUsd = source.NotionalUsd,
            PoolNotional = source.PoolNotional,
            TargetNotionalUsd = source.TargetNotionalUsd,
            PoolEquity = source.PoolEquity,
            Capital = source.Capital,
            Trigger = source.Trigger,
            Status = source.Status,
            Reason = source.Reason,
            VenueOrderId = source.VenueOrderId,
            ClientOrderId = source.ClientOrderId,
            CreatedAtUtc = source.CreatedAt.Utc,
        };
    }

    public LiveOrder ToDomain() => new()
    {
        Id = Id,
        FollowerId = FollowerId,
        ExchangeConfigId = ExchangeConfigId,
        Platform = Platform,
        TargetBase = TargetBase,
        TargetQuote = TargetQuote,
        SourceSymbol = SourceSymbol,
        Intent = Intent,
        Side = Side,
        PositionSide = PositionSide,
        Quantity = Quantity,
        Price = Price,
        EntryPrice = EntryPrice,
        ClosePrice = ClosePrice,
        NotionalUsd = NotionalUsd,
        PoolNotional = PoolNotional,
        TargetNotionalUsd = TargetNotionalUsd,
        PoolEquity = PoolEquity,
        Capital = Capital,
        Trigger = Trigger,
        Status = Status,
        Reason = Reason,
        VenueOrderId = VenueOrderId,
        ClientOrderId = ClientOrderId,
        CreatedAt = LighterTimestamp.FromMilliseconds(CreatedAtUtc.ToUnixTimeMilliseconds()),
    };
}

/// <summary>事件表行。</summary>
public sealed class PoolEventRecord
{
    public long Id { get; set; }

    public long AccountIndex { get; set; }

    public string Kind { get; set; } = string.Empty;

    public int MarketId { get; set; }

    public string Symbol { get; set; } = string.Empty;

    public string Side { get; set; } = string.Empty;

    public decimal SizeBefore { get; set; }

    public decimal SizeAfter { get; set; }

    public decimal NotionalDelta { get; set; }

    public decimal ReferencePrice { get; set; }

    public long DetectedAtUtcTicks { get; set; }

    public DateTimeOffset DetectedAtUtc
    {
        get => new(DetectedAtUtcTicks, TimeSpan.Zero);
        set => DetectedAtUtcTicks = value.UtcTicks;
    }

    public static PoolEventRecord FromDomain(PoolEvent source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new PoolEventRecord
        {
            AccountIndex = source.AccountIndex.Value,
            Kind = source.Kind.ToString(),
            MarketId = source.MarketId,
            Symbol = source.Symbol,
            Side = source.Side.ToString(),
            SizeBefore = source.SizeBefore,
            SizeAfter = source.SizeAfter,
            NotionalDelta = source.NotionalDelta.Value,
            ReferencePrice = source.ReferencePrice.Value,
            DetectedAtUtc = source.DetectedAt.Utc,
        };
    }

    public PoolEvent ToDomain() => new()
    {
        AccountIndex = AccountIdx.From(AccountIndex),
        Kind = Enum.TryParse(Kind, out PoolEventKind kind) ? kind : PoolEventKind.Open,
        MarketId = MarketId,
        Symbol = Symbol,
        Side = Enum.TryParse(Side, out PositionSide side) ? side : PositionSide.Long,
        SizeBefore = SizeBefore,
        SizeAfter = SizeAfter,
        DetectedAt = LighterTimestamp.FromMilliseconds(DetectedAtUtc.ToUnixTimeMilliseconds()),
        ReferencePrice = PriceAmount.From(ReferencePrice),
    };
}

/// <summary>跟单对照表行。</summary>
public sealed class CopyFillRecord
{
    public long Id { get; set; }

    public long AccountIndex { get; set; }

    public long SourceTradeId { get; set; }

    public int MarketId { get; set; }

    public string Symbol { get; set; } = string.Empty;

    public string Side { get; set; } = string.Empty;

    public decimal Size { get; set; }

    public decimal PoolPrice { get; set; }

    public decimal OurVwap { get; set; }

    public decimal SlippageBps { get; set; }

    public bool DepthSufficient { get; set; }

    public double DetectionLatencyMs { get; set; }

    public long ObservedAtUtcTicks { get; set; }

    public DateTimeOffset ObservedAtUtc
    {
        get => new(ObservedAtUtcTicks, TimeSpan.Zero);
        set => ObservedAtUtcTicks = value.UtcTicks;
    }

    public static CopyFillRecord FromDomain(CopyFill source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new CopyFillRecord
        {
            Id = source.Id,
            AccountIndex = source.AccountIndex.Value,
            SourceTradeId = source.SourceTradeId,
            MarketId = source.MarketId,
            Symbol = source.Symbol,
            Side = source.Side.ToString(),
            Size = source.Size,
            PoolPrice = source.PoolPrice.Value,
            OurVwap = source.OurVwap.Value,
            SlippageBps = source.SlippageBps,
            DepthSufficient = source.DepthSufficient,
            DetectionLatencyMs = source.DetectionLatencyMs,
            ObservedAtUtc = source.ObservedAt.Utc,
        };
    }

    public CopyFill ToDomain() => new()
    {
        Id = Id,
        AccountIndex = AccountIdx.From(AccountIndex),
        SourceTradeId = SourceTradeId,
        MarketId = MarketId,
        Symbol = Symbol,
        Side = Enum.TryParse(Side, out OrderSide side) ? side : OrderSide.Buy,
        Size = Size,
        PoolPrice = PriceAmount.From(PoolPrice),
        OurVwap = PriceAmount.From(OurVwap),
        SlippageBps = SlippageBps,
        DepthSufficient = DepthSufficient,
        DetectionLatencyMs = DetectionLatencyMs,
        ObservedAt = LighterTimestamp.FromMilliseconds(ObservedAtUtc.ToUnixTimeMilliseconds()),
    };
}

/// <summary>带单员表行。存在即被观察。</summary>
public sealed class LeadTraderRecord
{
    /// <summary>主键：被观察的池的账户索引。</summary>
    public long AccountIndex { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }

    public long CreatedAtUtcTicks { get; set; }

    public DateTimeOffset CreatedAtUtc
    {
        get => new(CreatedAtUtcTicks, TimeSpan.Zero);
        set => CreatedAtUtcTicks = value.UtcTicks;
    }

    public static LeadTraderRecord FromDomain(LeadTrader source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new LeadTraderRecord
        {
            AccountIndex = source.AccountIndex.Value,
            Name = source.Name,
            IsEnabled = source.IsEnabled,
            CreatedAtUtc = source.CreatedAt.Utc,
        };
    }

    public LeadTrader ToDomain() => new()
    {
        AccountIndex = AccountIdx.From(AccountIndex),
        Name = Name,
        IsEnabled = IsEnabled,
        CreatedAt = LighterTimestamp.FromMilliseconds(CreatedAtUtc.ToUnixTimeMilliseconds()),
    };
}

/// <summary>跟单员表行。主键自增，与池索引无关。</summary>
public sealed class FollowerRecord
{
    public long Id { get; set; }

    /// <summary>订阅的带单员（池账户索引）。</summary>
    public long LeadAccountIndex { get; set; }

    public decimal InitialCapital { get; set; }

    /// <summary>执行模式，存枚举名（见 <see cref="CopyMode"/>）。</summary>
    public string Mode { get; set; } = nameof(CopyMode.Paper);

    /// <summary>实盘用的交易所配置；纸面模式为 null。</summary>
    public long? ExchangeConfigId { get; set; }

    public bool IsActive { get; set; }

    public long CreatedAtUtcTicks { get; set; }

    public DateTimeOffset CreatedAtUtc
    {
        get => new(CreatedAtUtcTicks, TimeSpan.Zero);
        set => CreatedAtUtcTicks = value.UtcTicks;
    }

    public static FollowerRecord FromDomain(Follower source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new FollowerRecord
        {
            Id = source.Id,
            LeadAccountIndex = source.LeadAccountIndex.Value,
            InitialCapital = source.InitialCapital.Value,
            Mode = source.Mode.ToString(),
            ExchangeConfigId = source.ExchangeConfigId,
            IsActive = source.IsActive,
            CreatedAtUtc = source.CreatedAt.Utc,
        };
    }

    public Follower ToDomain() => new()
    {
        Id = Id,
        LeadAccountIndex = AccountIdx.From(LeadAccountIndex),
        InitialCapital = UsdAmount.From(InitialCapital),

        // 认不出的模式退回纸面：它是唯一能安全执行的一侧。
        // 反过来默认实盘会在读一行坏数据时把"模拟"升级成"真下单"，那是不可逆的。
        Mode = Enum.TryParse(Mode, out CopyMode mode) ? mode : CopyMode.Paper,
        ExchangeConfigId = ExchangeConfigId,
        IsActive = IsActive,
        CreatedAt = LighterTimestamp.FromMilliseconds(CreatedAtUtc.ToUnixTimeMilliseconds()),
    };
}

/// <summary>
/// 跟单员的动作流水表行（含被跳过的信号）。
///
/// 表名用 follower_fills 而不是 paper_fills：账属于**跟单员**（<c>follower_id</c>），
/// 池索引只是行里记录的一个池子侧事实。这张表存的是纸面模式的模拟成交；
/// 实盘跟单记的是真实成交，口径不同，将来另起一张表，两者不混。
/// </summary>
public sealed class FollowerFillRecord
{
    public long Id { get; set; }

    public long FollowerId { get; set; }

    public int MarketId { get; set; }

    public string Symbol { get; set; } = string.Empty;

    /// <summary>被跳过时为 null。</summary>
    public string? Side { get; set; }

    public decimal PoolPositionAfter { get; set; }

    public decimal PoolEquity { get; set; }

    public decimal TargetPosition { get; set; }

    public decimal PositionBefore { get; set; }

    public decimal PositionAfter { get; set; }

    public decimal Size { get; set; }

    public decimal Price { get; set; }

    public decimal Notional { get; set; }

    public bool DepthSufficient { get; set; }

    public decimal Fee { get; set; }

    public decimal RealizedPnl { get; set; }

    public string? SkipReason { get; set; }

    public long ObservedAtUtcTicks { get; set; }

    public DateTimeOffset ObservedAtUtc
    {
        get => new(ObservedAtUtcTicks, TimeSpan.Zero);
        set => ObservedAtUtcTicks = value.UtcTicks;
    }

    public static FollowerFillRecord FromDomain(PaperFill source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new FollowerFillRecord
        {
            Id = source.Id,
            FollowerId = source.FollowerId,
            MarketId = source.MarketId,
            Symbol = source.Symbol,
            Side = source.Side?.ToString(),
            PoolPositionAfter = source.PoolPositionAfter.Value,
            PoolEquity = source.PoolEquity.Value,
            TargetPosition = source.TargetPosition.Value,
            PositionBefore = source.PositionBefore.Value,
            PositionAfter = source.PositionAfter.Value,
            Size = source.Size,
            Price = source.Price.Value,
            Notional = source.Notional.Value,
            DepthSufficient = source.DepthSufficient,
            Fee = source.Fee.Value,
            RealizedPnl = source.RealizedPnl.Value,
            SkipReason = source.SkipReason?.ToString(),
            ObservedAtUtc = source.ObservedAt.Utc,
        };
    }

    public PaperFill ToDomain() => new()
    {
        Id = Id,
        FollowerId = FollowerId,
        MarketId = MarketId,
        Symbol = Symbol,
        Side = Enum.TryParse(Side, out OrderSide side) ? side : null,
        PoolPositionAfter = SignedSize.From(PoolPositionAfter),
        PoolEquity = UsdAmount.From(PoolEquity),
        TargetPosition = SignedSize.From(TargetPosition),
        PositionBefore = SignedSize.From(PositionBefore),
        PositionAfter = SignedSize.From(PositionAfter),
        Size = Size,
        Price = PriceAmount.From(Price),
        Notional = UsdAmount.From(Notional),
        DepthSufficient = DepthSufficient,
        Fee = UsdAmount.From(Fee),
        RealizedPnl = UsdAmount.From(RealizedPnl),
        SkipReason = Enum.TryParse(SkipReason, out PaperSkipReason reason) ? reason : null,
        ObservedAt = LighterTimestamp.FromMilliseconds(ObservedAtUtc.ToUnixTimeMilliseconds()),
    };
}

/// <summary>跟单员的盯市估值点表行（权益曲线）。</summary>
public sealed class FollowerEquityPointRecord
{
    public long Id { get; set; }

    public long FollowerId { get; set; }

    public decimal Equity { get; set; }

    public decimal RealizedPnl { get; set; }

    public decimal UnrealizedPnl { get; set; }

    public decimal Fees { get; set; }

    public decimal GrossNotional { get; set; }

    public long TakenAtUtcTicks { get; set; }

    public DateTimeOffset TakenAtUtc
    {
        get => new(TakenAtUtcTicks, TimeSpan.Zero);
        set => TakenAtUtcTicks = value.UtcTicks;
    }

    public static FollowerEquityPointRecord FromDomain(PaperEquityPoint source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new FollowerEquityPointRecord
        {
            Id = source.Id,
            FollowerId = source.FollowerId,
            Equity = source.Equity.Value,
            RealizedPnl = source.RealizedPnl.Value,
            UnrealizedPnl = source.UnrealizedPnl.Value,
            Fees = source.Fees.Value,
            GrossNotional = source.GrossNotional.Value,
            TakenAtUtc = source.TakenAt.Utc,
        };
    }

    public PaperEquityPoint ToDomain() => new()
    {
        Id = Id,
        FollowerId = FollowerId,
        Equity = UsdAmount.From(Equity),
        RealizedPnl = UsdAmount.From(RealizedPnl),
        UnrealizedPnl = UsdAmount.From(UnrealizedPnl),
        Fees = UsdAmount.From(Fees),
        GrossNotional = UsdAmount.From(GrossNotional),
        TakenAt = LighterTimestamp.FromMilliseconds(TakenAtUtc.ToUnixTimeMilliseconds()),
    };
}

/// <summary>交易所配置表行。凭据三列是密文，不是明文。</summary>
public sealed class ExchangeConfigRecord
{
    public long Id { get; set; }

    /// <summary><c>lighter</c> / <c>binance</c> / <c>okx</c> / <c>gate</c> / <c>custom</c>。</summary>
    public string Code { get; set; } = "custom";

    /// <summary><c>cex</c> / <c>dex</c>。</summary>
    public string Kind { get; set; } = "cex";

    public string DisplayName { get; set; } = string.Empty;

    public bool IsTestnet { get; set; }

    public string? RestBaseUrl { get; set; }

    public string? WsUrl { get; set; }

    public decimal MakerFeePercent { get; set; }

    public decimal TakerFeePercent { get; set; }

    public bool IsEnabled { get; set; }

    public string? Notes { get; set; }

    public long CreatedAtUtcTicks { get; set; }

    public DateTimeOffset CreatedAtUtc
    {
        get => new(CreatedAtUtcTicks, TimeSpan.Zero);
        set => CreatedAtUtcTicks = value.UtcTicks;
    }

    // ── 凭据（明文；见 ExchangeConfig 的说明）──────────────

    public string? ApiKey { get; set; }

    public string? ApiSecret { get; set; }

    public string? Passphrase { get; set; }

    /// <summary>API Key 尾号（明文，仅用于界面确认）。</summary>
    public string? ApiKeyHint { get; set; }

    public static ExchangeConfigRecord FromDomain(ExchangeConfig source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new ExchangeConfigRecord
        {
            Id = source.Id,
            Code = ExchangeEnumText.ToText(source.Code),
            Kind = ExchangeEnumText.ToText(source.Kind),
            DisplayName = source.DisplayName,
            IsTestnet = source.IsTestnet,
            RestBaseUrl = source.RestBaseUrl,
            WsUrl = source.WsUrl,
            MakerFeePercent = source.MakerFeePercent,
            TakerFeePercent = source.TakerFeePercent,
            IsEnabled = source.IsEnabled,
            Notes = source.Notes,
            CreatedAtUtc = source.CreatedAt.Utc,
            ApiKey = source.ApiKey,
            ApiSecret = source.ApiSecret,
            Passphrase = source.Passphrase,
            ApiKeyHint = source.ApiKeyHint,
        };
    }

    public ExchangeConfig ToDomain() => new()
    {
        Id = Id,
        Code = ExchangeEnumText.ReadCode(Code),
        Kind = ExchangeEnumText.ReadKind(Kind),
        DisplayName = DisplayName,
        IsTestnet = IsTestnet,
        RestBaseUrl = RestBaseUrl,
        WsUrl = WsUrl,
        MakerFeePercent = MakerFeePercent,
        TakerFeePercent = TakerFeePercent,
        IsEnabled = IsEnabled,
        Notes = Notes,
        CreatedAt = LighterTimestamp.FromMilliseconds(CreatedAtUtc.ToUnixTimeMilliseconds()),
        ApiKey = ApiKey,
        ApiSecret = ApiSecret,
        Passphrase = Passphrase,
        ApiKeyHint = ApiKeyHint,
    };
}

/// <summary>标的映射表行。</summary>
public sealed class SymbolMappingRecord
{
    public long Id { get; set; }

    public string SourcePlatform { get; set; } = "lighter";

    public string SourceBase { get; set; } = string.Empty;

    public string SourceQuote { get; set; } = "USDC";

    /// <summary>目标平台符号（binance 等）。</summary>
    public string TargetPlatform { get; set; } = string.Empty;

    public string TargetBase { get; set; } = string.Empty;

    public string TargetQuote { get; set; } = "USDT";

    /// <summary>
    /// 目标合约是不是传统金融（股票/商品/指数）。
    ///
    /// 存它而不是"用合约类型等于某个字符串去推"：各平台的口径不一样（币安看
    /// <c>TRADIFI_PERPETUAL</c>、OKX 看 instCategory、Gate 看 contract_type），
    /// 判定发生在对接实现里，结论记在这里。
    /// </summary>
    public bool TargetIsTradFi { get; set; }

    public string TargetContractType { get; set; } = "PERPETUAL";

    /// <summary>存枚举名。<c>Manual</c> 的行扫描时永不覆盖。</summary>
    public string Origin { get; set; } = nameof(SymbolMappingOrigin.Scanned);

    public bool IsEnabled { get; set; } = true;

    public decimal? SourcePrice { get; set; }

    public decimal? TargetPrice { get; set; }

    public long? VerifiedAtUtcTicks { get; set; }

    public string? Note { get; set; }

    public long CreatedAtUtcTicks { get; set; }

    public static SymbolMappingRecord FromDomain(SymbolMapping source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new SymbolMappingRecord
        {
            Id = source.Id,
            SourcePlatform = source.SourcePlatform,
            SourceBase = source.SourceBase,
            SourceQuote = source.SourceQuote,
            TargetPlatform = source.TargetPlatform,
            TargetBase = source.TargetBase,
            TargetQuote = source.TargetQuote,
            TargetContractType = source.TargetContractType,
            TargetIsTradFi = source.TargetIsTradFi,
            Origin = source.Origin.ToString(),
            IsEnabled = source.IsEnabled,
            SourcePrice = source.SourcePrice,
            TargetPrice = source.TargetPrice,
            VerifiedAtUtcTicks = source.VerifiedAt?.Utc.UtcTicks,
            Note = source.Note,
            CreatedAtUtcTicks = source.CreatedAt.Utc.UtcTicks,
        };
    }

    public SymbolMapping ToDomain() => new()
    {
        Id = Id,
        SourcePlatform = SourcePlatform,
        SourceBase = SourceBase,
        SourceQuote = SourceQuote,
        TargetPlatform = TargetPlatform,
        TargetBase = TargetBase,
        TargetQuote = TargetQuote,
        TargetContractType = TargetContractType,
        TargetIsTradFi = TargetIsTradFi,

        // 认不出的来源退回人工：把一条来历不明的行当作"扫描出来的"会让扫描覆盖掉它，
        // 而覆盖掉的可能是某个人的判断。退回人工则是"扫描不动它"，更安全
        Origin = Enum.TryParse(Origin, out SymbolMappingOrigin origin) ? origin : SymbolMappingOrigin.Manual,
        IsEnabled = IsEnabled,
        SourcePrice = SourcePrice,
        TargetPrice = TargetPrice,
        VerifiedAt = VerifiedAtUtcTicks is { } ticks
            ? LighterTimestamp.FromMilliseconds(new DateTimeOffset(ticks, TimeSpan.Zero).ToUnixTimeMilliseconds())
            : null,
        Note = Note,
        CreatedAt = LighterTimestamp.FromMilliseconds(
            new DateTimeOffset(CreatedAtUtcTicks, TimeSpan.Zero).ToUnixTimeMilliseconds()),
    };
}

/// <summary>
/// 登录账号表行。
///
/// 两处列名与领域语义的对应值得写下来：
/// · <c>PasswordHash</c> 存的是**带算法标签与迭代次数的完整编码**（见 PasswordHash），
///   不是一个裸哈希——将来调大迭代次数时，老口令仍然验得过。
/// · <c>TotpSecret</c> 为 NULL 表示"还没绑定 MFA"，不是"这个账号不用 MFA"。
///   MFA 是强制的，这个字段没有"关掉"的取值。
/// </summary>
public sealed class UserRecord
{
    public long Id { get; set; }

    /// <summary>已归一化（小写、去空白）的登录邮箱。</summary>
    public string Email { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Base32 的 TOTP 密钥；NULL = 未绑定。</summary>
    public string? TotpSecret { get; set; }

    /// <summary>最近一次成功校验使用的时间步（防重放）。</summary>
    public long? TotpLastStep { get; set; }

    public long CreatedAtUtcTicks { get; set; }

    public DateTimeOffset CreatedAtUtc
    {
        get => new(CreatedAtUtcTicks, TimeSpan.Zero);
        set => CreatedAtUtcTicks = value.UtcTicks;
    }

    public static UserRecord FromDomain(User source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new UserRecord
        {
            Id = source.Id,
            Email = source.Email.Value,
            PasswordHash = source.PasswordHash.Encoded,
            TotpSecret = source.TotpSecret?.ToBase32(),
            TotpLastStep = source.TotpLastStep,
            CreatedAtUtc = source.CreatedAt.Utc,
        };
    }

    public User ToDomain() => new()
    {
        Id = Id,
        Email = EmailAddress.Parse(Email),
        PasswordHash = PasswordHashValue.Parse(PasswordHash),
        TotpSecret = TotpSecret is null ? null : TotpSecretValue.Parse(TotpSecret),
        TotpLastStep = TotpLastStep,
        CreatedAt = LighterTimestamp.FromMilliseconds(CreatedAtUtc.ToUnixTimeMilliseconds()),
    };
}

/// <summary>
/// 通知通道表行。邮箱（kind=email）用到的列目前就这些——
/// 与 ExchangeConfig 同一种做法：一张表承载一种类型，用 Kind 区分，用不到的列留空。
/// </summary>
public sealed class NotificationProviderRecord
{
    public long Id { get; set; }

    /// <summary><c>email</c>。</summary>
    public string Kind { get; set; } = "email";

    public string DisplayName { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }

    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public bool UseStartTls { get; set; } = true;

    public string? UserName { get; set; }

    /// <summary>SMTP 口令（授权码）。**明文**，与交易所凭据同一条约定。</summary>
    public string? Password { get; set; }

    public string? FromAddress { get; set; }

    public string FromName { get; set; } = "PoolPilot";

    public long CreatedAtUtcTicks { get; set; }

    public DateTimeOffset CreatedAtUtc
    {
        get => new(CreatedAtUtcTicks, TimeSpan.Zero);
        set => CreatedAtUtcTicks = value.UtcTicks;
    }

    public static NotificationProviderRecord FromDomain(NotificationProvider source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new NotificationProviderRecord
        {
            Id = source.Id,
            Kind = source.Kind,
            DisplayName = source.DisplayName,
            IsEnabled = source.IsEnabled,
            Host = source.Host,
            Port = source.Port,
            UseStartTls = source.UseStartTls,
            UserName = source.UserName,
            Password = source.Password,
            FromAddress = source.FromAddress,
            FromName = source.FromName,
            CreatedAtUtc = source.CreatedAt.Utc,
        };
    }

    public NotificationProvider ToDomain() => new()
    {
        Id = Id,
        Kind = Kind,
        DisplayName = DisplayName,
        IsEnabled = IsEnabled,
        Host = Host,
        Port = Port,
        UseStartTls = UseStartTls,
        UserName = UserName,
        Password = Password,
        FromAddress = FromAddress,
        FromName = FromName,
        CreatedAt = LighterTimestamp.FromMilliseconds(CreatedAtUtc.ToUnixTimeMilliseconds()),
    };
}

/// <summary>通知样板表行。一类通知一条（Purpose 唯一）。</summary>
public sealed class NotificationTemplateRecord
{
    public long Id { get; set; }

    /// <summary><c>registration_code</c>。</summary>
    public string Purpose { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    /// <summary>用哪条通道发；NULL = 还没选。</summary>
    public long? ProviderId { get; set; }

    public long UpdatedAtUtcTicks { get; set; }

    public DateTimeOffset UpdatedAtUtc
    {
        get => new(UpdatedAtUtcTicks, TimeSpan.Zero);
        set => UpdatedAtUtcTicks = value.UtcTicks;
    }

    public static NotificationTemplateRecord FromDomain(NotificationTemplate source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new NotificationTemplateRecord
        {
            Id = source.Id,
            Purpose = source.Purpose,
            DisplayName = source.DisplayName,
            Subject = source.Subject,
            Body = source.Body,
            ProviderId = source.ProviderId,
            UpdatedAtUtc = source.UpdatedAt.Utc,
        };
    }

    public NotificationTemplate ToDomain() => new()
    {
        Id = Id,
        Purpose = Purpose,
        DisplayName = DisplayName,
        Subject = Subject,
        Body = Body,
        ProviderId = ProviderId,
        UpdatedAt = LighterTimestamp.FromMilliseconds(UpdatedAtUtc.ToUnixTimeMilliseconds()),
    };
}

/// <summary>
/// 通知投递记录表行。
///
/// 通道信息存的是**快照**（显示名、类型），不是靠外键现查：通道改名或删除之后，
/// 这条记录仍要能说清"当时用的是谁"。<c>ProviderId</c> 只作参考，可能指向已删除的行。
/// </summary>
public sealed class NotificationLogRecord
{
    public long Id { get; set; }

    public string Purpose { get; set; } = string.Empty;

    public long? ProviderId { get; set; }

    public string ProviderName { get; set; } = string.Empty;

    public string Kind { get; set; } = "email";

    public string Recipient { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public bool Delivered { get; set; }

    public bool LogFallback { get; set; }

    public string? Error { get; set; }

    public long SentAtUtcTicks { get; set; }

    public DateTimeOffset SentAtUtc
    {
        get => new(SentAtUtcTicks, TimeSpan.Zero);
        set => SentAtUtcTicks = value.UtcTicks;
    }

    public static NotificationLogRecord FromDomain(NotificationLog source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new NotificationLogRecord
        {
            Id = source.Id,
            Purpose = source.Purpose,
            ProviderId = source.ProviderId,
            ProviderName = source.ProviderName,
            Kind = source.Kind,
            Recipient = source.Recipient,
            Subject = source.Subject,
            Body = source.Body,
            Delivered = source.Delivered,
            LogFallback = source.LogFallback,
            Error = source.Error,
            SentAtUtc = source.SentAt.Utc,
        };
    }

    public NotificationLog ToDomain() => new()
    {
        Id = Id,
        Purpose = Purpose,
        ProviderId = ProviderId,
        ProviderName = ProviderName,
        Kind = Kind,
        Recipient = Recipient,
        Subject = Subject,
        Body = Body,
        Delivered = Delivered,
        LogFallback = LogFallback,
        Error = Error,
        SentAt = LighterTimestamp.FromMilliseconds(SentAtUtc.ToUnixTimeMilliseconds()),
    };
}
