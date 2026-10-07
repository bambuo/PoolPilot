namespace PoolPilot.Domain.Enums;

/// <summary>
/// 池的生命周期状态（上游 <c>status</c> 字段）。
///
/// 枚举值取自 Lighter 官方 SDK 包（<c>vendor-zklighter</c>）：<c>{ Active: 0, Frozen: 1 }</c>，
/// 并由官方前端的用法**交叉验证**——它把该字段与 <c>Frozen</c> 比较来显示冻结标识，
/// 官方原文说明是 "This public pool is frozen, deposits are not allowed anymore."。
///
/// ⚠️ 实测**全量 337 个池里有 145 个（43%）处于冻结态**，其中 52 个仍有资产。
/// 冻结不影响既有仓位与净值，只停止接受新入金；但对本项目的意义是：
/// 一个已冻结的池，「直接入池」这个选项事实上不存在，且冻结本身是运营者收摊的信号。
/// </summary>
public enum PoolLifecycle
{
    /// <summary>正常，接受入金。</summary>
    Active = 0,

    /// <summary>已冻结，不再接受新入金（既有仓位与净值继续）。</summary>
    Frozen = 1,
}
