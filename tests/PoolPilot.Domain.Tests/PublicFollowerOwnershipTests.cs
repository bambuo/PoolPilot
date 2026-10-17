using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Tests;

/// <summary>
/// 公开跟单页的创建者判据。
///
/// 这一组用例锁的是**一整套安全性质**，不是实现细节：匿名访客手里那枚 Cookie 是公开面上
/// 唯一能删东西的凭证，判据错一处就是"别人的跟单能被你删"或"自己的删不掉"。
/// 前者的代价是可指认的破坏，后者的代价是使用者不再相信这个页面。
///
/// 固定向量（<see cref="PinnedVisitorSecret"/> 与 <see cref="PinnedCredentialForRowOne"/>）
/// 是按口径独立算出来写死的：改派生公式（域分隔常量、拼接顺序、哈希层数）都会让它变红。
/// 那类改动会让**库里已存的匿名行全部无法被删除**，所以它必须是一次看得见红线的决定。
/// </summary>
[TestClass]
public sealed class PublicFollowerOwnershipTests
{
    private static readonly AccountIndex Pool = AccountIndex.From(281474976552443L);

    /// <summary>一枚形状合法的访客密钥（64 位小写十六进制）。</summary>
    private const string PinnedVisitorSecret =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary><c>lowerhex(SHA256(HMACSHA256(key=域分隔, msg="密钥:1")))</c>，独立算出的固定值。</summary>
    private const string PinnedCredentialForRowOne =
        "595a2b26123f450e979d24dabf7819c52fdd3b911f7118a4192defdd7c05414b";

    private const string OtherVisitorSecret =
        "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    // ── 派生：决定性、形状、以及"密钥不落库" ─────────────────

    [TestMethod]
    public void DeriveCredential_IsDeterministicAndMatchesThePinnedVector()
    {
        string first = PublicFollowerOwnership.DeriveCredential(PinnedVisitorSecret, 1L);
        string again = PublicFollowerOwnership.DeriveCredential(PinnedVisitorSecret, 1L);

        Assert.AreEqual(first, again, "同一枚密钥加同一行必须得出同一个值——否则刷新一次就认不出创建者了");
        Assert.AreEqual(PinnedCredentialForRowOne, first, "派生公式（域分隔、拼接顺序、哈希层数）与口径不一致");
        Assert.AreEqual(PublicFollowerOwnership.VisitorSecretHexLength, first.Length);
        Assert.AreEqual(first.ToLowerInvariant(), first, "存库与比较都用小写十六进制，大小写不一致会比较不过");

        // 域分隔常量本身是公开知识（这仓库是公开的），机密性全部来自那 32 字节随机数。
        // 想把它改成"从配置读"之前先想清楚：库里已派生的匿名行会因此全部失效。
        // 这条前提写在 <see cref="PublicFollowerOwnership.DomainSeparator"/> 的注释里，
        // 不在这里断言——两个常量相等不是一条会红的用例。
    }

    [TestMethod]
    public void DeriveCredential_IsNotTheVisitorSecretItself()
    {
        string credential = PublicFollowerOwnership.DeriveCredential(PinnedVisitorSecret, 1L);

        // 这条断言看着多余，但它是"访客密钥永不落库"这句话的可执行形式：
        // 存的是派生值，于是读到库（甚至读到备份）的人也拿不到那枚 Cookie
        Assert.AreNotEqual(PinnedVisitorSecret, credential);
        Assert.IsFalse(credential.Contains(PinnedVisitorSecret, StringComparison.Ordinal));
    }

    [TestMethod]
    public void DeriveCredential_ChangesWithTheRowAndWithTheVisitor()
    {
        string rowOne = PublicFollowerOwnership.DeriveCredential(PinnedVisitorSecret, 1L);
        string rowTwo = PublicFollowerOwnership.DeriveCredential(PinnedVisitorSecret, 2L);
        string otherVisitorRowOne = PublicFollowerOwnership.DeriveCredential(OtherVisitorSecret, 1L);

        Assert.AreNotEqual(rowOne, rowTwo, "带上行 Id 正是为了：一枚密钥不该能开所有行");
        Assert.AreNotEqual(rowOne, otherVisitorRowOne);
    }

    // ── 匿名行 ────────────────────────────────────────────

    [TestMethod]
    public void IsCreator_AnonRow_AcceptsItsOwnVisitorSecret()
    {
        Assert.IsTrue(
            PublicFollowerOwnership.IsCreator(AnonRow(1L), userId: null, PinnedVisitorSecret),
            "带着当初那枚 Cookie 的人应当认得出自己的行");
    }

    [TestMethod]
    public void IsCreator_CredentialDerivedForRowOne_DoesNotOpenRowTwo()
    {
        // "跨行不可复用"的全部含义：B 行存的是按 B 的 Id 派生的值，
        // 拿按 A 派生的凭证（同一枚密钥）打不开它
        Follower rowTwoHoldingRowOnesCredential = AnonRow(id: 2L, credentialDerivedFromRowId: 1L);

        Assert.IsFalse(
            PublicFollowerOwnership.IsCreator(rowTwoHoldingRowOnesCredential, userId: null, PinnedVisitorSecret),
            "同一枚密钥在不同行上派生出的值不同——它因此不能跨行使用");

        Assert.IsTrue(PublicFollowerOwnership.IsCreator(AnonRow(2L), userId: null, PinnedVisitorSecret));
    }

    [TestMethod]
    public void IsCreator_AnonRow_RejectsAnotherVisitorsSecret()
    {
        Assert.IsFalse(
            PublicFollowerOwnership.IsCreator(AnonRow(1L), userId: null, OtherVisitorSecret),
            "别的浏览器不该能删这一行");
    }

    [TestMethod]
    public void IsCreator_AnonRow_RejectsMissingVisitorSecret()
    {
        Follower row = AnonRow(1L);

        Assert.IsFalse(
            PublicFollowerOwnership.IsCreator(row, userId: null, visitorSecret: null),
            "没有 Cookie 就没有创建者能力");
        Assert.IsFalse(PublicFollowerOwnership.IsCreator(row, userId: null, string.Empty));
    }

    [TestMethod]
    public void IsCreator_AnonRow_IsJudgedByTheSecretEvenWhenTheCallerIsSignedIn()
    {
        // 两条判据按**行上哪一列有值**分叉，不看调用者登录了没有：
        // 先在匿名状态下建了几条、后来才登录的人，那几条仍然归那枚 Cookie 管
        Assert.IsTrue(
            PublicFollowerOwnership.IsCreator(AnonRow(1L), userId: 7L, PinnedVisitorSecret),
            "换设备之前建的匿名行，靠的还是原来那枚 Cookie");

        Assert.IsFalse(
            PublicFollowerOwnership.IsCreator(AnonRow(1L), userId: 7L, visitorSecret: null),
            "而 userId 对匿名行不起作用：否则任何一个账号都能删掉别人匿名建的跟单");
    }

    // ── 登录行 ────────────────────────────────────────────

    [TestMethod]
    public void IsCreator_UserRow_AcceptsOnlyTheSameUserId()
    {
        Follower row = UserRow(3L, ownerUserId: 7L);

        Assert.IsTrue(PublicFollowerOwnership.IsCreator(row, userId: 7L, visitorSecret: null));
        Assert.IsFalse(PublicFollowerOwnership.IsCreator(row, userId: 8L, PinnedVisitorSecret));
        Assert.IsFalse(PublicFollowerOwnership.IsCreator(row, userId: null, PinnedVisitorSecret));
    }

    [TestMethod]
    public void IsCreator_UserRow_IsNotOpenedByAnyVisitorSecret()
    {
        // user 行没有哈希列，于是"猜中一枚访客密钥"对登录建的行一点用都没有
        Assert.IsFalse(PublicFollowerOwnership.IsCreator(UserRow(3L, 7L), userId: null, OtherVisitorSecret));
        Assert.IsFalse(PublicFollowerOwnership.IsCreator(UserRow(3L, 7L), userId: null, PinnedVisitorSecret));
    }

    // ── 其余三种"不该给口子"的形状 ─────────────────────────

    [TestMethod]
    public void IsCreator_WorkbenchRow_IsAlwaysFalse()
    {
        // 工作台的行不是公开行：判据在这里就否掉，公开端点连"它存在"都不承认（回 404）。
        // 这一行故意把两个 owner 列都填上，证明挡住它的是 IsPublic 而不是 owner
        Follower privateRow = new()
        {
            Id = 9L,
            LeadAccountIndex = Pool,
            InitialCapital = UsdAmount.From(500m),
            Mode = CopyMode.Paper,
            IsActive = true,
            CreatedAt = LighterTimestamp.FromSeconds(1_800_000_000),
            IsPublic = false,
            OwnerUserId = 7L,
            OwnerCredentialHash = PublicFollowerOwnership.DeriveCredential(PinnedVisitorSecret, 9L),
        };

        Assert.IsFalse(PublicFollowerOwnership.IsCreator(privateRow, userId: 7L, PinnedVisitorSecret));
        Assert.IsFalse(PublicFollowerOwnership.IsCreator(privateRow, userId: null, PinnedVisitorSecret));
    }

    [TestMethod]
    public void IsCreator_PublicRowWithoutOwner_IsFalse()
    {
        // 建行与回填凭证之间崩溃留下的形状：两列都空。判据是 fail-closed 的——
        // 这种行只能由管理员用工作台的删除接口清理（见 IFollowerRepository 的说明）
        Follower orphan = new()
        {
            Id = 11L,
            LeadAccountIndex = Pool,
            InitialCapital = UsdAmount.From(500m),
            Mode = CopyMode.Paper,
            IsActive = true,
            CreatedAt = LighterTimestamp.FromSeconds(1_800_000_000),
            IsPublic = true,
        };

        Assert.IsFalse(PublicFollowerOwnership.IsCreator(orphan, userId: 7L, PinnedVisitorSecret));
    }

    [TestMethod]
    public void IsCreator_CorruptedStoredHash_IsFalseInsteadOfThrowing()
    {
        // 库里存的不是十六进制（被手改过）：判不通过，而不是抛一个 500 给界面
        Follower corrupted = new()
        {
            Id = 12L,
            LeadAccountIndex = Pool,
            InitialCapital = UsdAmount.From(500m),
            Mode = CopyMode.Paper,
            IsActive = true,
            CreatedAt = LighterTimestamp.FromSeconds(1_800_000_000),
            IsPublic = true,
            OwnerCredentialHash = "not-hex-at-all",
        };

        Assert.IsFalse(PublicFollowerOwnership.IsCreator(corrupted, userId: null, PinnedVisitorSecret));
    }

    // ── Cookie 里的形状 ───────────────────────────────────

    [TestMethod]
    public void IsVisitorSecret_AcceptsOnlySixtyFourLowercaseHexCharacters()
    {
        Assert.IsTrue(PublicFollowerOwnership.IsVisitorSecret(PinnedVisitorSecret));
        Assert.IsFalse(PublicFollowerOwnership.IsVisitorSecret(null));
        Assert.IsFalse(PublicFollowerOwnership.IsVisitorSecret(string.Empty));
        Assert.IsFalse(PublicFollowerOwnership.IsVisitorSecret(PinnedVisitorSecret[..63]), "短一位就不是密钥，别猜也别补救");
        Assert.IsFalse(PublicFollowerOwnership.IsVisitorSecret(PinnedVisitorSecret.ToUpperInvariant()));
        Assert.IsFalse(PublicFollowerOwnership.IsVisitorSecret(new string('g', 64)));
    }

    /// <summary>
    /// 匿名公开行：存的哈希按**自己的 Id** 派生。
    /// <paramref name="credentialDerivedFromRowId"/> 用来造"第 1 行的凭证挂在第 2 行上"这种错位形状。
    /// </summary>
    private static Follower AnonRow(long id, long? credentialDerivedFromRowId = null) => new()
    {
        Id = id,
        LeadAccountIndex = Pool,
        InitialCapital = UsdAmount.From(500m),
        Mode = CopyMode.Paper,
        IsActive = true,
        CreatedAt = LighterTimestamp.FromSeconds(1_800_000_000),
        IsPublic = true,
        OwnerCredentialHash = PublicFollowerOwnership.DeriveCredential(
            PinnedVisitorSecret,
            credentialDerivedFromRowId ?? id),
    };

    private static Follower UserRow(long id, long ownerUserId) => new()
    {
        Id = id,
        LeadAccountIndex = Pool,
        InitialCapital = UsdAmount.From(500m),
        Mode = CopyMode.Paper,
        IsActive = true,
        CreatedAt = LighterTimestamp.FromSeconds(1_800_000_000),
        IsPublic = true,
        OwnerUserId = ownerUserId,
    };
}
