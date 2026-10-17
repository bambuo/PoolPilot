using PoolPilot.Domain.Enums;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Entities;

/// <summary>
/// 跟单员：带单员信号的订阅者。
///
/// 身份是**独立的**（自增 <see cref="Id"/>），不借用池索引——一个池可以挂多个跟单员，
/// 各自一份本金、各记一套账。它不认识 WebSocket、也不认识池子的连接，只消费信号，
/// 所以跟单员来去都不影响池子被观察这件事。
///
/// 与带单员是**一对一订阅**：一个跟单员只跟一个带单员。
/// 多源合成需要一套冲突消解规则（两个带单员给出相反信号时听谁的），不在当前范围内。
///
/// <see cref="InitialCapital"/> 不是一个"好看的数字"，而是整套计算的基准：
/// 规模比例 = 本金 / 池子权益。没有它就算不出我方该持多少，也就无从谈"跟单能不能赚"。
/// 改本金等于换一个完全不同的实验，所以改本金会清空该跟单员的流水重新开始。
/// </summary>
public sealed record Follower
{
    /// <summary>持久化主键；未入库时为 0（由数据库分配）。</summary>
    public long Id { get; init; }

    /// <summary>订阅的带单员——也就是被观察的那个池。</summary>
    public required AccountIndex LeadAccountIndex { get; init; }

    /// <summary>投入的本金（USDT）。</summary>
    public required UsdAmount InitialCapital { get; init; }

    /// <summary>执行模式。纸面与实盘可以同时挂在同一个带单员下，各自独立记账。</summary>
    public required CopyMode Mode { get; init; }

    /// <summary>
    /// 实盘用的交易所配置（凭据与接入地址都在它里面）。纸面模式为 null。
    ///
    /// 为什么这里有配置 Id、而标的映射刻意按**平台**存：映射是"哪个资产对应哪个合约"的知识，
    /// 与你开了几个账号无关；但下单必须落到**某一个账号**上，那是账户事实。
    /// 两者混在一起会让同一份映射被迫存两遍，或让两个账号被迫用同一套凭据。
    /// </summary>
    public long? ExchangeConfigId { get; init; }

    /// <summary>是否在跟随。关掉后带单员仍会发信号，只是这个跟单员不再响应。</summary>
    public required bool IsActive { get; init; }

    public required LighterTimestamp CreatedAt { get; init; }

    // ── 公开面（/api/public/*）的四列 ─────────────────────────
    // 工作台建的行这四列一律保持默认值（false / null / null / null）：
    // 现状与既有行为零变化。只有公开页建的纸面跟单会把 IsPublic 置真，并按登录与否
    // 写两个 owner 列中的**恰好一个**（见 PublicFollowerOwnership 的判据）。

    /// <summary>
    /// 这一行是否出现在公开跟单页上。
    ///
    /// 判据只有这一个布尔，不看两个 owner 列——"看得到"与"谁能删"是两件事：
    /// 公开行对所有人可读，删除权才由 owner 列决定。
    /// </summary>
    public bool IsPublic { get; init; }

    /// <summary>
    /// 创建者（会话用户主键）。公开页 + **已登录**时写这一列，<see cref="OwnerCredentialHash"/> 留 null。
    ///
    /// 存 Id 而不是哈希：登录态本身可信，"是不是同一个人"由会话直接回答，不需要再派生凭证。
    /// </summary>
    public long? OwnerUserId { get; init; }

    /// <summary>
    /// 创建者凭证哈希（由访客密钥与**本行 Id** 派生）。公开页 + **匿名**时写这一列。
    ///
    /// 派生而不是直接存访客密钥：密钥一旦落库就等于"库被读到就能删掉任何人的跟单"，
    /// 而行级派生值既认不出人也不能跨行复用。访客密钥只存在于 Cookie，永不进库。
    /// </summary>
    public string? OwnerCredentialHash { get; init; }

    /// <summary>
    /// 公开页上的自定义名称（trim 后 ≤ <c>PublicPageLimits.LabelMaxLength</c>，可为 null）。
    /// 纯展示用途，不参与任何判据。
    /// </summary>
    public string? Label { get; init; }

    /// <summary>按池子权益算出的规模比例。<see cref="UsdAmount.Zero"/> 或负值时返回 0。</summary>
    public decimal ScaleAgainst(PoolSnapshot pool) =>
        pool.TotalAssetValue.Value <= 0m
            ? 0m
            : InitialCapital.Value / pool.TotalAssetValue.Value;
}
