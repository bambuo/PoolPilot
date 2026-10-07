using PoolPilot.Application.Dtos;
using PoolPilot.Application.Mapping;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 读实盘跟单的流水。
///
/// 与纸面台账的读取（<see cref="GetCopyLedgerUseCase"/>）有个根本差别：
/// 纸面要**重放全部流水**才能算出持仓与权益，所以读的是"账"；
/// 实盘的真实持仓与盈亏以**交易所的账户状态**为准（那是唯一权威的一份），
/// 这里读的是"我发过什么、结果如何"的日志，按时间倒序取最近若干条即可。
///
/// 因此这份流水不提供"权益曲线"：它算不出来，也不该由我方算第二份。
/// </summary>
public sealed class GetLiveOrdersUseCase(
    IFollowerRepository followers,
    ILiveOrderRepository orders,
    IExchangeConfigRepository exchanges)
{
    public async Task<IReadOnlyList<LiveOrderDto>> ExecuteAsync(
        long followerId,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        Follower follower = await followers.GetAsync(followerId, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"跟单员 {followerId} 不存在。", nameof(followerId));

        IReadOnlyList<LiveOrder> rows = await orders
            .ListAsync(followerId, limit, cancellationToken)
            .ConfigureAwait(false);

        // 配置名是界面上的可读标签（"币安 主账号"）。取不到也不影响流水本身，
        // 所以失败时留空而不是让整个读取失败
        string? exchangeName = null;

        if (follower.ExchangeConfigId is { } configId)
        {
            ExchangeConfig? config = await exchanges.GetAsync(configId, cancellationToken).ConfigureAwait(false);
            exchangeName = config?.DisplayName;
        }

        return [.. rows.Select(row => LiveOrderMapper.ToDto(row, exchangeName))];
    }
}
