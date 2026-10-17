using System.Globalization;

using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 公开页的池名候选（G4）：在公共池里按名字或账户索引找一个池。
///
/// 数据来自 <see cref="IPoolCatalog"/>（带 60 秒缓存的读口），不在这里打上游：
/// 候选框是"每敲一个字问一次"的交互，匿名流量每次都穿透的话，
/// 上游那点按权重算的额度会先被输入框吃光，工作台自己的取数排在后面等。
///
/// **冻结池不列**：运营者已经收摊的池没有观察价值，工作台的创建弹窗也是这么筛的。
///
/// 搜索与工作台对齐：池名是大小写不敏感的子串；账户索引要求**精确相等**
/// （纯数字查询才按索引解释）。索引不做子串匹配——"1"会命中一大片索引，
/// 那不是一个能用的筛选。
///
/// <paramref name="limit"/> 越界不报错而是夹回区间：这是"多给几条少给几条"，
/// 不是数据正确性问题，为它回 400 只会让前端多写一条没有信息量的分支。
/// </summary>
public sealed class PublicPoolOptionsUseCase(IPoolCatalog catalog)
{
    public async Task<IReadOnlyList<PublicPoolOptionDto>> ExecuteAsync(
        string? query,
        int? limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        IReadOnlyList<Pool> pools = await catalog.ListAllAsync(cancellationToken).ConfigureAwait(false);

        int take = limit switch
        {
            null or <= 0 => PublicPageLimits.PoolOptionLimitDefault,
            int requested => Math.Min(requested, PublicPageLimits.PoolOptionLimitMax),
        };

        string needle = query?.Trim() ?? string.Empty;

        // 冻结池没有观察价值；工作台那张弹窗同样把 isFrozen 排除在外
        IEnumerable<Pool> open = pools.Where(p => !p.IsFrozen);

        // 空查询给"最新上市的若干池"。排序键用创建时间而不是账户索引：
        // 索引是上游的分配结果，历史上并不单调，拿它当"新旧"会把老池子顶到新池子前面
        IEnumerable<Pool> matched;

        if (needle.Length == 0)
        {
            matched = open.OrderByDescending(p => p.CreatedAt.Utc);
        }
        else
        {
            bool indexQuery = long.TryParse(
                needle,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long accountIndex);

            matched = open.Where(p =>
                p.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || (indexQuery && p.AccountIndex.Value == accountIndex));
        }

        return [.. matched
            .Take(take)
            .Select(p => new PublicPoolOptionDto
            {
                AccountIndex = p.AccountIndex.Value,
                Name = p.Name,
                TotalAssetValue = p.TotalAssetValue.Value,
            })];
    }
}
