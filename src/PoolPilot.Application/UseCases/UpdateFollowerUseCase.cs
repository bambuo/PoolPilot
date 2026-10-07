using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.Mapping;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 修改跟单员的本金与跟随状态。
///
/// 一条关键规则：**改动本金会清空该跟单员的历史**。
///
/// 理由不是省事，而是会计上的必然：本金是规模比例的分母（比例 = 本金 / 池子权益），
/// 所有历史流水里的仓位都是按当时那个本金算出来的。把 200 USDC 时期和 5000 USDC 时期的
/// 流水混在同一条权益曲线里，得到的曲线不对应任何一个真实实验，却会被读成"跟单业绩"。
/// 所以改本金等于换一个实验，必须重来。
///
/// 池子权益同样在变，但不影响历史的一致性——流水里记下了每笔动作当时的池子权益与目标仓位，
/// 历史记录自身是可解释的。会破坏一致性的只有本金。
/// </summary>
public sealed class UpdateFollowerUseCase(
    IFollowerRepository followers,
    IFollowerLedgerRepository ledger,
    ILeadTraderRepository leads,
    IExchangeConfigRepository exchanges,
    IExchangeBrokerFactory brokers,
    ILeadTraderSync sync)
{
    public async Task<FollowerDto> ExecuteAsync(
        long followerId,
        UpdateFollowerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.InitialCapital <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.InitialCapital,
                "本金必须为正数——它是规模比例的分母，为 0 或负数算不出任何仓位。");
        }

        Follower existing = await followers.GetAsync(followerId, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"跟单员 {followerId} 不存在。", nameof(followerId));

        bool capitalChanged = existing.InitialCapital.Value != request.InitialCapital;

        // 实盘：重设本金（也就是"重设本金并继续"那个动作）时，规模基准不得超过账号的**可用保证金**。
        //
        // 只在**真的改了本金**时查：暂停与继续本身不涉及本金，不该被一次对交易所的网络请求挡住——
        // 那会让"我想先暂停一下"变成"先把本金调到合规"。
        if (capitalChanged && existing is { Mode: CopyMode.Live, ExchangeConfigId: { } configId })
        {
            ExchangeConfig config = await exchanges.GetAsync(configId, cancellationToken).ConfigureAwait(false)
                ?? throw new ArgumentException(
                    $"跟单员 {followerId} 绑定的交易所配置 {configId} 已不存在，改本金前请先重新指定账号。",
                    nameof(request));

            // 先查上限再清历史：不满足时什么都不该发生（清历史是不可逆的）
            await LiveCapitalLimit
                .EnsureWithinAvailableAsync(brokers, config, request.InitialCapital, cancellationToken)
                .ConfigureAwait(false);
        }

        if (capitalChanged)
        {
            await ledger.ClearHistoryAsync(followerId, cancellationToken).ConfigureAwait(false);
        }

        Follower updated = existing with
        {
            InitialCapital = UsdAmount.From(request.InitialCapital),
            IsActive = request.IsActive,
        };

        await followers.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);

        // 立刻生效：暂停要停得下来，改完本金要马上按新规模对齐
        await sync.RequestSyncAsync(cancellationToken).ConfigureAwait(false);

        LeadTrader? lead = await leads
            .GetAsync(updated.LeadAccountIndex, cancellationToken)
            .ConfigureAwait(false);

        // 只回读"刚写进去了什么"。完整账户状态由读取接口给。
        return FollowerMapper.Snapshot(updated, lead);
    }
}
