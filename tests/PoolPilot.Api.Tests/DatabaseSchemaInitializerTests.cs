using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using PoolPilot.Infrastructure.Persistence;

namespace PoolPilot.Api.Tests;

/// <summary>
/// 建库、补表与一次性数据迁移测试。
///
/// 锁住两个**已经踩过或必然踩到**的坑：
/// <list type="number">
///   <item>`EnsureCreated` 对已存在的数据库什么都不做，所以给模型加了新表之后，
///     老库永远缺那张表，接口只报 `no such table`，错误信息完全没提"你需要重建数据库"。</item>
///   <item>把"纸面账户"改成"跟单员"要改主键（池索引 → 自增 Id）。改主键没法就地做，
///     只能建新表搬数据。搬家搬错等于把已经量出来的盈亏清零——流水无法重新采集，
///     当时的盘口已经过去了。所以这里的断言直接盯着"行数与归属有没有搬对"。</item>
/// </list>
/// </summary>
[TestClass]
public sealed class DatabaseSchemaInitializerTests
{
    private static PoolPilotDbContext Open(string path)
    {
        DbContextOptions<PoolPilotDbContext> options = new DbContextOptionsBuilder<PoolPilotDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        return new PoolPilotDbContext(options);
    }

    [TestMethod]
    public void EnsureSchema_OnFreshDatabase_CreatesEveryTable()
    {
        string path = Temp();

        try
        {
            using PoolPilotDbContext db = Open(path);
            DatabaseSchemaInitializer.EnsureSchemaAsync(db, NullLogger.Instance).GetAwaiter().GetResult();

            foreach (string table in ExpectedTables)
            {
                Assert.IsTrue(TableExists(path, table), $"新库应含表 {table}");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void EnsureSchema_WhenModelGainedNewTables_AddsOnlyTheMissingOnes()
    {
        string path = Temp();

        try
        {
            using (PoolPilotDbContext db = Open(path))
            {
                DatabaseSchemaInitializer.EnsureSchemaAsync(db, NullLogger.Instance).GetAwaiter().GetResult();
            }

            // 模拟"老库"：把后加的表删掉，就像它们引入之前的数据库那样
            foreach (string table in RegrownTables)
            {
                DropTable(path, table);
            }

            // 库里还留着先前的数据，不能被清掉
            Execute(
                path,
                "INSERT INTO pool_events (AccountIndex, Kind, MarketId, Symbol, Side, SizeBefore, SizeAfter, "
                + "NotionalDelta, ReferencePrice, DetectedAtUtc, DetectedAtUtcTicks) "
                + "VALUES (42, 'Open', 29, 'ENA', 'Long', 0, 1, 1, 1, '2026-01-01 00:00:00+00:00', 638000000000000000)");

            using (PoolPilotDbContext db = Open(path))
            {
                IReadOnlyList<string> created = DatabaseSchemaInitializer
                    .EnsureSchemaAsync(db, NullLogger.Instance).GetAwaiter().GetResult();

                // 删表会连带删掉该表的索引，所以补建的是"表 + 索引"，数量不止这几个；
                // 这里不断言具体条数，只断言两件真正重要的事：该补的都补了、不该动的一个没动。
                foreach (string table in RegrownTables)
                {
                    CollectionAssert.Contains(created.ToList(), table, $"应补建表 {table}");
                }

                foreach (string untouched in new[] { "pool_events", "copy_fills" })
                {
                    Assert.IsFalse(
                        created.Contains(untouched),
                        $"已存在的表 {untouched} 不应被重建——重建会丢数据");
                }
            }

            foreach (string table in ExpectedTables)
            {
                Assert.IsTrue(TableExists(path, table), $"补表后应含表 {table}");
            }

            Assert.AreEqual(1, CountRows(path, "pool_events"), "补表不得清空既有数据");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void EnsureSchema_WhenRunTwice_IsIdempotent()
    {
        string path = Temp();

        try
        {
            using PoolPilotDbContext db = Open(path);
            DatabaseSchemaInitializer.EnsureSchemaAsync(db, NullLogger.Instance).GetAwaiter().GetResult();

            IReadOnlyList<string> second = DatabaseSchemaInitializer
                .EnsureSchemaAsync(db, NullLogger.Instance).GetAwaiter().GetResult();

            Assert.IsEmpty(second, "第二次运行不应再建任何对象");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void EnsureSchema_WithLegacyPaperTables_MigratesAccountsAndHistoryToFollowers()
    {
        string path = Temp();

        try
        {
            CreateLegacySchema(path);

            using (PoolPilotDbContext db = Open(path))
            {
                DatabaseSchemaInitializer.EnsureSchemaAsync(db, NullLogger.Instance).GetAwaiter().GetResult();
            }

            // 账户搬过来了，且"当初跟的是哪个池"被保存在 lead_account_index 上
            Assert.AreEqual(1, CountRows(path, "followers"), "旧纸面账户应成为一条跟单员");
            Assert.AreEqual(
                281474976552443L,
                Scalar(path, "SELECT LeadAccountIndex FROM followers"),
                "跟单员应记得自己订阅的是哪个池");
            Assert.AreEqual(
                1L,
                Scalar(
                    path,
                    // 金额列存的是 TEXT（EF 的 decimal → TEXT 映射），比较必须显式转成数值——
                    // 直接写 = 5000 是拿文本跟整数比，永远不相等
                    "SELECT COUNT(*) FROM followers WHERE CAST(InitialCapital AS REAL) = 5000 AND Mode = 'Paper' AND IsActive = 1"),
                "本金、模式与启用状态都要一起搬过来");
            Assert.AreEqual(
                1L,
                Scalar(path, "SELECT COUNT(*) FROM followers WHERE CreatedAtUtcTicks = 638000000000000000"),
                "创建时间要保留，否则'运营时长'这类派生值会变成迁移那一刻");

            // 历史流水与权益点也跟着账户改键——它们无法重新采集
            Assert.AreEqual(2, CountRows(path, "follower_fills"), "两条流水都要搬过来");
            Assert.AreEqual(
                2L,
                Scalar(path, "SELECT COUNT(*) FROM follower_fills f JOIN followers o ON o.Id = f.FollowerId"),
                "每一行流水都要指向新建的跟单员");
            Assert.AreEqual(
                1L,
                Scalar(path, "SELECT COUNT(DISTINCT FollowerId) FROM follower_fills"),
                "同一个账户下的流水必须全部改键到同一个跟单员，而不是各自散开");
            Assert.AreEqual(
                "ENA",
                Scalar(path, "SELECT Symbol FROM follower_fills LIMIT 1"),
                "流水内容要原样保留");

            Assert.AreEqual(1, CountRows(path, "follower_equity_points"), "权益点要搬过来");
            Assert.AreEqual(
                1.25m,
                Convert.ToDecimal(Scalar(path, "SELECT Equity FROM follower_equity_points"), System.Globalization.CultureInfo.InvariantCulture),
                "权益值必须原样保留");

            // 旧表退场：留着它们会让"哪张表是真相"成为每次排查都要先回答的问题
            foreach (string legacy in LegacyTables)
            {
                Assert.IsFalse(TableExists(path, legacy), $"旧表 {legacy} 应已删除");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void EnsureSchema_WithLegacyPaperTablesTwice_DoesNotDuplicateFills()
    {
        // 迁移必须可重复执行：服务重启、迁移中途失败后重来，都不该把流水复制成两份。
        // 复制会让重放出来的仓位与盈亏凭空翻倍，而界面上看不出任何异常。
        string path = Temp();

        try
        {
            CreateLegacySchema(path);

            using PoolPilotDbContext db = Open(path);
            DatabaseSchemaInitializer.EnsureSchemaAsync(db, NullLogger.Instance).GetAwaiter().GetResult();
            DatabaseSchemaInitializer.EnsureSchemaAsync(db, NullLogger.Instance).GetAwaiter().GetResult();

            Assert.AreEqual(1, CountRows(path, "followers"), "跟单员不应被搬出第二份");
            Assert.AreEqual(2, CountRows(path, "follower_fills"), "流水不应被复制");
            Assert.AreEqual(1, CountRows(path, "follower_equity_points"), "权益点不应被复制");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static readonly string[] LegacyTables =
        ["paper_accounts", "paper_fills", "paper_equity_points"];

    private static readonly string[] LeadFollowerTables =
        ["lead_traders", "followers", "follower_fills", "follower_equity_points"];

    /// <summary>后加的交易所配置表——与带单员那批一样要能补建出来。
    /// （曾经还有一张 app_settings 存加密主密钥，密钥那套移除后它也随之消失。）</summary>
    private static readonly string[] ExchangeTables =
        ["exchange_configs"];

    /// <summary>授权那批后加的表：菜单 / 动作 / 角色 / 两张绑定。
    /// 它们与别的后加表一样，靠"按模型补建"从老库里长出来。</summary>
    private static readonly string[] PermissionTables =
        ["menus", "menu_actions", "roles", "user_roles", "menu_roles"];

    private static readonly string[] ExpectedTables =
        ["pool_events", "copy_fills", .. LeadFollowerTables, .. ExchangeTables, .. PermissionTables];

    /// <summary>老库里会被按模型补建出来的那批表（用于"补表"这条用例）。</summary>
    private static readonly string[] RegrownTables =
        [.. LeadFollowerTables, .. ExchangeTables, .. PermissionTables];

    /// <summary>造一个"带单员之前的库"：主键为池索引的纸面账户 + 两条流水 + 一个权益点。</summary>
    private static void CreateLegacySchema(string path)
    {
        Execute(
            path,
            """
            CREATE TABLE "paper_accounts" (
                "AccountIndex" INTEGER NOT NULL CONSTRAINT "PK_paper_accounts" PRIMARY KEY,
                "InitialCapital" TEXT NOT NULL,
                "IsActive" INTEGER NOT NULL,
                "CreatedAtUtcTicks" INTEGER NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL);

            CREATE TABLE "paper_fills" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_paper_fills" PRIMARY KEY AUTOINCREMENT,
                "AccountIndex" INTEGER NOT NULL,
                "MarketId" INTEGER NOT NULL,
                "Symbol" TEXT NOT NULL,
                "Side" TEXT NULL,
                "PoolPositionAfter" TEXT NOT NULL,
                "PoolEquity" TEXT NOT NULL,
                "TargetPosition" TEXT NOT NULL,
                "PositionBefore" TEXT NOT NULL,
                "PositionAfter" TEXT NOT NULL,
                "Size" TEXT NOT NULL,
                "Price" TEXT NOT NULL,
                "Notional" TEXT NOT NULL,
                "DepthSufficient" INTEGER NOT NULL,
                "Fee" TEXT NOT NULL,
                "RealizedPnl" TEXT NOT NULL,
                "SkipReason" TEXT NULL,
                "ObservedAtUtcTicks" INTEGER NOT NULL,
                "ObservedAtUtc" TEXT NOT NULL);

            CREATE TABLE "paper_equity_points" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_paper_equity_points" PRIMARY KEY AUTOINCREMENT,
                "AccountIndex" INTEGER NOT NULL,
                "Equity" TEXT NOT NULL,
                "RealizedPnl" TEXT NOT NULL,
                "UnrealizedPnl" TEXT NOT NULL,
                "Fees" TEXT NOT NULL,
                "GrossNotional" TEXT NOT NULL,
                "TakenAtUtcTicks" INTEGER NOT NULL,
                "TakenAtUtc" TEXT NOT NULL);

            INSERT INTO "paper_accounts"
                ("AccountIndex", "InitialCapital", "IsActive", "CreatedAtUtcTicks", "CreatedAtUtc")
            VALUES (281474976552443, 5000, 1, 638000000000000000, '2026-01-01 00:00:00+00:00');

            INSERT INTO "paper_fills"
                ("AccountIndex", "MarketId", "Symbol", "Side", "PoolPositionAfter", "PoolEquity",
                 "TargetPosition", "PositionBefore", "PositionAfter", "Size", "Price", "Notional",
                 "DepthSufficient", "Fee", "RealizedPnl", "SkipReason", "ObservedAtUtcTicks", "ObservedAtUtc")
            VALUES
                (281474976552443, 29, 'ENA', 'Buy', 7931.8, 72100, 144.7, 0, 144.7, 144.7, 0.2447, 35.4, 1, 0, 0, NULL, 638000000010000000, '2026-01-01 00:00:00+00:00'),
                (281474976552443, 29, 'ENA', 'Sell', 0, 72100, 0, 144.7, 0, 144.7, 0.2501, 36.2, 1, 0, 0.78, NULL, 638000000020000000, '2026-01-01 00:00:00+00:00');

            INSERT INTO "paper_equity_points"
                ("AccountIndex", "Equity", "RealizedPnl", "UnrealizedPnl", "Fees", "GrossNotional",
                 "TakenAtUtcTicks", "TakenAtUtc")
            VALUES (281474976552443, 1.25, 0.78, 0.47, 0, 0, 638000000030000000, '2026-01-01 00:00:00+00:00');
            """);
    }

    private static string Temp() =>
        Path.Combine(Path.GetTempPath(), $"poolpilot-{Guid.NewGuid():N}.db");

    private static bool TableExists(string path, string table) =>
        CountRows(path, "sqlite_master", $"type = 'table' AND name = '{table}'") > 0;

    private static long CountRows(string path, string table, string? where = null)
    {
        using SqliteConnection connection = new($"Data Source={path}");
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}"
            + (where is null ? string.Empty : $" WHERE {where}");

        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static object Scalar(string path, string sql)
    {
        using SqliteConnection connection = new($"Data Source={path}");
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;

        return command.ExecuteScalar() ?? throw new InvalidOperationException($"查询没有返回行：{sql}");
    }

    private static void DropTable(string path, string table) =>
        Execute(path, $"DROP TABLE \"{table}\"");

    private static void Execute(string path, string sql)
    {
        using SqliteConnection connection = new($"Data Source={path}");
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
