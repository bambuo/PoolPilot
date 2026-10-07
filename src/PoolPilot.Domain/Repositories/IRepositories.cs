using PoolPilot.Domain.Entities;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Repositories;

/// <summary>某个池的信号统计（带单员列表用：累计条数与最近一条的时刻）。</summary>
public sealed record PoolSignalStats(AccountIndex Pool, int Count, LighterTimestamp? LastSignalAt);

/// <summary>持仓变化事件的存档。</summary>
public interface IPoolEventRepository
{
    Task AppendAsync(IReadOnlyList<PoolEvent> events, CancellationToken cancellationToken = default);

    /// <summary>读取指定池的最近事件（按时间降序）。</summary>
    Task<IReadOnlyList<PoolEvent>> GetRecentAsync(
        AccountIndex account,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>读取全部池的最近事件（监控台用）。</summary>
    Task<IReadOnlyList<PoolEvent>> GetRecentAcrossPoolsAsync(
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 每个池的信号条数与最近一条的时刻。
    ///
    /// 一次聚合查询而不是"每个池查一次最近一条"：后者在带单员数量增长时是 N+1 次查询，
    /// 而列表页只需要两个数字。
    /// </summary>
    Task<IReadOnlyList<PoolSignalStats>> GetSignalStatsAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>跟单对照记录的存档。</summary>
public interface ICopyFillRepository
{
    Task AppendAsync(IReadOnlyList<CopyFill> fills, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CopyFill>> GetRecentAsync(
        AccountIndex account,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>判断某笔池成交是否已被记录，用于幂等去重（重连回填会重复投递）。</summary>
    Task<IReadOnlySet<long>> GetRecordedTradeIdsAsync(
        AccountIndex account,
        IReadOnlyList<long> tradeIds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 跨交易所标的映射的存档。
///
/// 按 (目标平台, 源资产币, 源交易币) 唯一：同一个源标的对**不同的平台**当然可以有不同的映射
/// （币安叫 CL、另一家可能叫 WTI），但同一个平台只需一份——映射与你有几个账号无关。
/// </summary>
public interface ISymbolMappingRepository
{
    Task<IReadOnlyList<SymbolMapping>> ListAsync(CancellationToken cancellationToken = default);

    Task<SymbolMapping?> GetAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>按 (目标平台, 源资产币) 取唯一一条。跟单路径查的就是它。</summary>
    Task<SymbolMapping?> FindAsync(
        string targetPlatform,
        string sourceBase,
        CancellationToken cancellationToken = default);

    Task<SymbolMapping> CreateAsync(SymbolMapping mapping, CancellationToken cancellationToken = default);

    Task UpdateAsync(SymbolMapping mapping, CancellationToken cancellationToken = default);

    Task DeleteAsync(long id, CancellationToken cancellationToken = default);
}

/// <summary>交易所配置的存档。</summary>
public interface IExchangeConfigRepository
{
    Task<ExchangeConfig?> GetAsync(long id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExchangeConfig>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>新建并返回带库分配主键的配置。</summary>
    Task<ExchangeConfig> CreateAsync(ExchangeConfig config, CancellationToken cancellationToken = default);

    Task UpdateAsync(ExchangeConfig config, CancellationToken cancellationToken = default);

    Task DeleteAsync(long id, CancellationToken cancellationToken = default);
}

/// <summary>带单员的存档。存在即被观察——与有没有跟单员订阅无关。</summary>
public interface ILeadTraderRepository
{
    /// <summary>读取一个带单员；该池没有带单员时返回 <c>null</c>。</summary>
    Task<LeadTrader?> GetAsync(AccountIndex account, CancellationToken cancellationToken = default);

    /// <summary>列出全部带单员（监控服务据此决定订阅哪些池）。</summary>
    Task<IReadOnlyList<LeadTrader>> ListAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(LeadTrader lead, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除带单员。调用方必须先确认没有跟单员订阅它——删掉观察者而让订阅者留在原地，
    /// 会造出"账户看着在跟随、其实永远收不到信号"的状态。
    /// </summary>
    Task DeleteAsync(AccountIndex account, CancellationToken cancellationToken = default);
}

/// <summary>跟单员的存档。</summary>
public interface IFollowerRepository
{
    /// <summary>按主键读取；不存在时返回 <c>null</c>。</summary>
    Task<Follower?> GetAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>列出全部跟单员（运行期据此维护订阅关系）。</summary>
    Task<IReadOnlyList<Follower>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>列出订阅了某个带单员的跟单员（删除带单员前要检查）。</summary>
    Task<IReadOnlyList<Follower>> ListByLeadAsync(
        AccountIndex leadAccountIndex,
        CancellationToken cancellationToken = default);

    /// <summary>新建并返回带库分配主键的跟单员。</summary>
    Task<Follower> CreateAsync(Follower follower, CancellationToken cancellationToken = default);

    /// <summary>更新本金/启用状态等可变字段（按主键）。</summary>
    Task UpdateAsync(Follower follower, CancellationToken cancellationToken = default);

    Task DeleteAsync(long id, CancellationToken cancellationToken = default);
}

/// <summary>跟单员的成交流水与权益点。</summary>
/// <summary>
/// 实盘流水的读写。
///
/// 与纸面台账（<see cref="IFollowerLedgerRepository"/>）分开：纸面流水是**账**（要重放才能算出持仓与权益），
/// 实盘流水是**日志**（持仓与权益以交易所为准，这里只记我方发过什么、结果如何）。
/// 两者的读法完全不同——账要全量重放，日志只看最近若干条。
/// </summary>
public interface ILiveOrderRepository
{
    Task AddAsync(Domain.Entities.LiveOrder order, CancellationToken cancellationToken = default);

    /// <summary>按时间倒序取最近若干条。</summary>
    Task<IReadOnlyList<Domain.Entities.LiveOrder>> ListAsync(
        long followerId,
        int limit = 200,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 实盘流水的两档计数：**发出去了多少**、**有多少没跟上**。
    ///
    /// "没跟上"把 <c>skipped</c>（我方主动没发：低于最小下单量、没映射上）与
    /// <c>failed</c>（发出去了被交易所拒）算在一起。两者在流水里必须分开记
    /// （排查方向不同），但在卡片这一格上合并：被拒的单如果只进"未列出"的角落，
    /// 一个连续被拒的跟单员在列表上看起来会一切正常。
    /// </summary>
    Task<(int Placed, int NotFollowed)> CountAsync(
        long followerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 某个目标合约最近一笔是从哪个源标的来的。
    ///
    /// 用途很具体：池子平掉某仓位后，我方也要平掉——而我方持仓是按**目标交易对**记的，
    /// 那时池子里已经没有这个标的可以反查了。这张日志是唯一还记得对应关系的地方。
    /// </summary>
    Task<IReadOnlyList<string>> SourcesForAsync(
        long followerId,
        string targetBase,
        string targetQuote,
        int limit = 20,
        CancellationToken cancellationToken = default);
}

public interface IFollowerLedgerRepository
{
    Task AppendFillsAsync(IReadOnlyList<PaperFill> fills, CancellationToken cancellationToken = default);

    /// <summary>按时间升序读取流水（重放仓位账要求顺序）。</summary>
    Task<IReadOnlyList<PaperFill>> GetFillsAsync(
        long followerId,
        int limit,
        CancellationToken cancellationToken = default);

    Task AppendEquityPointAsync(PaperEquityPoint point, CancellationToken cancellationToken = default);

    /// <summary>按时间升序读取权益点，用于画曲线。</summary>
    Task<IReadOnlyList<PaperEquityPoint>> GetEquityCurveAsync(
        long followerId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 清空该跟单员的全部流水与估值点。
    ///
    /// 改本金时必须调用：本金是规模比例的分母，同一个跟单员跨越两种本金的历史
    /// 在会计上是两笔不同的实验，混在一条权益曲线里不对应任何真实情况。
    /// </summary>
    Task ClearHistoryAsync(long followerId, CancellationToken cancellationToken = default);
}
