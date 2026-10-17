using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 公开页新增带单员之前的两道数量闸。
///
/// 单独成一个类（而不是塞进用例里）的理由：这两条闸要读库、要按时区口径数"今天"，
/// 而 <see cref="AddPublicLeadTraderUseCase"/> 本身只做"存在就返回、不存在就建、再装配 DTO"。
/// 上限只有一个入口要守（公开带单页的「观察这个池」）——「开始跟单」不是入口：
/// 跟单员订阅的是带单员，池上没有观察者时那条请求直接被拒绝。
///
/// 只在「确实会新增一行」时被调用：已存在的带单员不占新增名额（见
/// <see cref="AddPublicLeadTraderUseCase"/> 的判断）。
/// </summary>
public sealed class PublicLeadTraderQuota(
    ILeadTraderRepository leads,
    TimeProvider clock)
{
    public async Task EnsureCanAddAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LeadTrader> all = await leads.ListAsync(cancellationToken).ConfigureAwait(false);

        if (all.Count >= PublicPageLimits.TotalLeads)
        {
            throw new ArgumentException(
                $"全站带单员已达上限 {PublicPageLimits.TotalLeads} 个，公开页暂时不能新增观察者。"
                + "请到「带单员」页停掉不再需要的那几个。",
                nameof(cancellationToken));
        }

        DateTimeOffset todayUtc = clock.GetUtcNow().UtcDateTime;
        int addedToday = all.Count(lead => lead.CreatedAt.Utc.UtcDateTime.Date == todayUtc.Date);

        if (addedToday >= PublicPageLimits.LeadsPerDay)
        {
            throw new ArgumentException(
                $"今天（UTC 日）公开页新增的带单员已达上限 {PublicPageLimits.LeadsPerDay} 个，请明天再来。",
                nameof(cancellationToken));
        }
    }
}
