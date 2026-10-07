using PoolPilot.Domain.Enums;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Entities;

/// <summary>
/// 公共资金池的元数据（来自 <c>publicPoolsMetadata</c>）。
///
/// 本类型只承载上游在**这个接口上确实提供**的字段——实测字段清单为：
/// <c>code, account_index, created_at, master_account_index, account_type, name,
/// l1_address, annual_percentage_yield, sharpe_ratio, status, operator_fee,
/// total_asset_value, total_spot_value, total_perps_value, total_shares, assets</c>。
///
/// 注意这里**没有** <c>operator_shares</c> 与 <c>min_operator_share_rate</c>：
/// 它们只存在于 WS 频道 <c>pool_info</c>，见 <see cref="PoolInfo"/>。
/// 不在本类型上放读不到的字段，是为了让"读不到"与"真的是 0"无法被混淆。
///
/// 本类型承载的指标全部是上游给的**点估计**。真实指标（最大回撤、真实夏普）
/// 必须由 <c>NavAnalyzer</c> 从净值序列实算——实测见过点估计同时低估收益与风险的例子。
/// </summary>
public sealed record Pool
{
    /// <summary>
    /// Lighter 官方运营方的钱包地址。
    ///
    /// **这个地址不在任何 API 文档里**——它是从官方前端的生产产物（<c>dist-*.js</c>）里
    /// 逆向出来的判定条件，官方前端用它对池子打 "LIGHTER" 徽标。该地址运营着
    /// 195 个杠杆代币池（"ETH 3x long"、"BTC 3x short" 之类），那是官方的产品线而非第三方。
    ///
    /// ⚠️ 官方若更换运营钱包，这里必须同步更新，否则官方池会被误标为社区。
    /// 与官方实现有一处**有意差异**：官方用严格相等比较，这里忽略大小写。
    /// EVM 地址本身不区分大小写（大写只是校验和显示约定），忽略大小写不可能误判，
    /// 还能容忍上游某天返回不同大小写——而官方那套严格比较会因此失效。
    /// </summary>
    public const string LighterOperatorAddress = "0xb01CD2bf96559C3Ca27C844B88E36Ea47465DBa0";

    public required AccountIndex AccountIndex { get; init; }

    public required string Name { get; init; }

    public required string L1Address { get; init; }

    /// <summary>
    /// 上游的账户类型码（原始值，含义见 <see cref="Enums.PoolAccountType"/>）。
    /// 保存原始整数而不是枚举：未知取值不该让解析失败，只该让归属判断退化为"社区"。
    /// </summary>
    public required int AccountTypeCode { get; init; }

    /// <summary>
    /// 运营方归属。判定与官方前端**同源**：
    /// <c>account_type == InsuranceFund(3)</c> 或 <c>l1_address == 官方运营钱包</c>。
    ///
    /// 未知类型码一律归为社区——判不出"这是官方"时就不该标官方；
    /// 反过来（把官方池标成社区）只是少一条信息，不会误导。
    /// </summary>
    public PoolOrigin Origin =>
        AccountTypeCode == (int)PoolAccountType.InsuranceFund
        || string.Equals(L1Address, LighterOperatorAddress, StringComparison.OrdinalIgnoreCase)
            ? PoolOrigin.Lighter
            : PoolOrigin.Community;

    public required UsdAmount TotalAssetValue { get; init; }

    /// <summary>
    /// 上游报的年化收益率（百分数，点估计）。
    ///
    /// **实测该字段可以荒谬到 6726 万%**——短命池把微小波动年化后就是这个量级。
    /// 因此界面上必须与净值序列实算值并排展示，不能单独采信。
    /// </summary>
    public required PercentValue ReportedAnnualPercentageYield { get; init; }

    /// <summary>上游报的夏普比率（点估计）。样本期短或波动被低估时会虚高。</summary>
    public required decimal ReportedSharpe { get; init; }

    /// <summary>运营费（利润分成比例）。实测取值区间为 1% ~ 30%。</summary>
    public required PercentValue OperatorFee { get; init; }

    public required long TotalShares { get; init; }

    public required LighterTimestamp CreatedAt { get; init; }

    /// <summary>
    /// 上游的状态码（含义见 <see cref="PoolLifecycle"/>）。原始值保留供诊断。
    /// </summary>
    public required int StateCode { get; init; }

    /// <summary>
    /// 是否已冻结（停止接受新入金）。
    ///
    /// 判定取 <c>status == 1</c>。**未知状态码一律视为未冻结**——理由与归属判定一致：
    /// 判不出"它被冻结"时不该把它藏起来（藏起来会让池子凭空消失），
    /// 而误标成正常最多只是少一层提示。这是 imf=0 那类事故的教训：
    /// 对取值范围未知的字段，保守方向要选"不隐藏数据"。
    /// </summary>
    public bool IsFrozen => StateCode == (int)PoolLifecycle.Frozen;
}
