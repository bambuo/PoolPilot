using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;

namespace PoolPilot.Application.Services;

/// <summary>
/// 交易所配置的归属守卫。配置里放着凭据，跨用户读写必须在用例之前就挡住。
/// </summary>
public sealed class ExchangeAccessGuard(IExchangeConfigRepository exchanges, IUserDataScope scope)
{
    public async Task<ExchangeConfig?> FindAsync(long id, CancellationToken cancellationToken = default)
    {
        ExchangeConfig? config = await exchanges.GetAsync(id, cancellationToken).ConfigureAwait(false);

        return config is not null
            && await scope.CanAccessOwnerAsync(config.OwnerUserId, cancellationToken).ConfigureAwait(false)
                ? config
                : null;
    }
}
