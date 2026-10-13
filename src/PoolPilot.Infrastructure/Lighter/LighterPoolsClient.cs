using Lighter.Net.Interfaces.Clients;

using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Infrastructure.Lighter;

/// <summary>
/// Lighter 公开数据客户端——**对接走 CryptoClients.Net**，本类只是一层薄壳：
///
/// <list type="number">
///   <item>库覆盖到的三条（系统配置游标、账户快照、合约规格）走**库客户端上的扩展成员**
///     （<see cref="LighterClientExtensions"/>）——它们都由库的公开方法组合而成；</item>
///   <item>库没覆盖的五条（池枚举、净值序列、池的成交与挂单、盘口）走
///     <see cref="LighterPoolDataExtension"/>——那些端点进不了库的请求管线，
///     理由与证据写在那里。</item>
/// </list>
/// </summary>
public sealed class LighterPoolsClient(
    ILighterRestClient client,
    LighterPoolDataExtension extension) : ILighterPoolsClient
{
    public Task<LighterSystemConfig> FetchSystemConfigAsync(CancellationToken cancellationToken = default) =>
        client.GetPoolCursorsAsync(cancellationToken);

    public Task<IReadOnlyList<Pool>> FetchPoolsAsync(
        PoolFilter filter,
        CancellationToken cancellationToken = default) =>
        extension.FetchPoolsAsync(filter, cancellationToken);

    public Task<PoolSnapshot> FetchSnapshotAsync(
        AccountIndex account,
        CancellationToken cancellationToken = default) =>
        client.GetPoolSnapshotAsync(account, cancellationToken);

    public Task<PoolInfo> FetchPoolInfoAsync(
        AccountIndex account,
        CancellationToken cancellationToken = default) =>
        extension.FetchPoolInfoAsync(account, cancellationToken);

    public Task<IReadOnlyList<PoolTrade>> FetchRecentTradesAsync(
        AccountIndex account,
        int limit = 50,
        CancellationToken cancellationToken = default) =>
        extension.FetchRecentTradesAsync(account, limit, cancellationToken);

    public Task<IReadOnlyList<PoolOrder>> FetchOrdersAsync(
        AccountIndex account,
        CancellationToken cancellationToken = default) =>
        extension.FetchOrdersAsync(account, cancellationToken);

    public Task<IReadOnlyList<InstrumentSpec>> FetchInstrumentsAsync(
        CancellationToken cancellationToken = default) =>
        client.GetInstrumentSpecsAsync(cancellationToken);

    public Task<OrderBookSnapshot> FetchOrderBookAsync(
        int marketId,
        CancellationToken cancellationToken = default) =>
        extension.FetchOrderBookAsync(marketId, cancellationToken);
}
