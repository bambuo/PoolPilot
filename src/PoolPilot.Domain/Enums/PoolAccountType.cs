namespace PoolPilot.Domain.Enums;

/// <summary>
/// 上游的账户类型码（<c>account_type</c>）。
///
/// 枚举值取自 Lighter 官方 SDK 包（<c>vendor-zklighter</c>）里的定义：
/// <c>Master=0, Sub=1, PublicPool=2, InsuranceFund=3</c>。
/// <see cref="StakePool"/> 不在该枚举里，是<b>从数据观测得出</b>的
/// （实测质押池的 <c>account_type</c> 为 4）。
///
/// ⚠️ 这不是"全部取值"的清单。上游可能引入新类型码，因此领域模型保存的是
/// 原始整数（见 <c>Pool.AccountTypeCode</c>），本枚举只用于可读性判断，
/// **遇到未知取值不会抛异常**——这是 <c>initial_margin_fraction = "0.00"</c>
/// 那次故障的教训：对"取值范围未知"的字段抛异常，会让上游的一次新增类型
/// 打挂整个列表接口。
/// </summary>
public enum PoolAccountType
{
    /// <summary>主账户。</summary>
    Master = 0,

    /// <summary>子账户。</summary>
    Sub = 1,

    /// <summary>公共资金池。用户与官方运营的池子都是这个类型。</summary>
    PublicPool = 2,

    /// <summary>保险基金 / 协议流动性提供者（实测为 XLP 池）。</summary>
    InsuranceFund = 3,

    /// <summary>质押池（实测 LIT 质押池为此类型）。</summary>
    StakePool = 4,
}
