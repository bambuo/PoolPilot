using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Entities;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 实盘"规模基准"的上限：**不得超过交易所账户的可用保证金**（使用者定的口径）。
///
/// 为什么这条规则必须存在：实盘的仓位是"目标名义额 = 池仓名义 × (本金 ÷ 池子权益)"算出来的，
/// 本金在这里是**分母上那个决定跟多大仓的数**。本金填得比账户里能用的钱多，系统就会按一个
/// 不存在的余额去下"标准跟单"的仓，结果是交易所一句"保证金不足"（-2019）把单退回来——
/// 而使用者看到的是"跟单没跟上"，不是"我的本金填错了"。
///
/// 上限取**可用保证金**而不是钱包余额：钱包余额是账户里"名义上有多少钱"，
/// 可用保证金才是"这一刻能开仓的钱"。跟单要花的正是后者。
///
/// ⚠️ 可用保证金会随持仓与挂单浮动（持仓占用的保证金越多，可用越少）。所以这条检查的时机是
/// **设置本金的那一刻**：那一刻的数就是使用者看到的数，不会出现"填的时候合规、持仓之后才发现超标"。
/// 这也意味着**不要**拿它去卡"暂停/继续"这类不涉及本金的操作。
/// </summary>
public static class LiveCapitalLimit
{
    /// <summary>不满足时报错（<see cref="ArgumentException"/>，接口层会把它当 400 带出原文）。</summary>
    public static async Task EnsureWithinAvailableAsync(
        IExchangeBrokerFactory brokers,
        ExchangeConfig config,
        decimal capital,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(brokers);
        ArgumentNullException.ThrowIfNull(config);

        if (capital <= 0m)
        {
            // 本金为正由调用方已经校验过；这里只是不把"非正"当成"没超上限"
            return;
        }

        IExchangeBroker? broker = brokers.Create(config);

        if (broker is null)
        {
            // 没有执行侧实现的平台在调用方那里已经被拒；这里不重复报同一件事
            return;
        }

        decimal available;

        try
        {
            BrokerAccount account = await broker.GetAccountAsync(cancellationToken).ConfigureAwait(false);
            available = account.AvailableBalance;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 读不到账户就不放行：这条规则的目的是"别按不存在的余额下仓"，
            // 无法确认时放行等于把规则交给运气
            throw new ArgumentException(
                $"读不到 {config.DisplayName} 的账户，无法确认本金不超过可用保证金"
                + $"（{ex.Message}）。等交易所可访问时再改，或先把跟单员暂停。",
                nameof(capital));
        }

        if (capital > available)
        {
            throw new ArgumentException(
                $"本金的规模基准 {capital:F2} 超过 {config.DisplayName} 的可用保证金 {available:F2}"
                + "（持仓占用的保证金会算在外）。实盘按这个本金算出来的仓位，账户里的钱开不出来——"
                + "要么把本金改到可用范围以内，要么先往账户里补钱（或者先减小持仓占用）。",
                nameof(capital));
        }
    }
}
