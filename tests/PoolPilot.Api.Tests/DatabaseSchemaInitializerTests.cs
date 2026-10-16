using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using PoolPilot.Infrastructure.Persistence;

namespace PoolPilot.Api.Tests;

/// <summary>
/// 建库与补表测试。
///
/// 锁住一个**已经踩过**的坑：`EnsureCreated` 对已存在的数据库什么都不做，
/// 所以给模型加了新表之后，老库永远缺那张表，接口只报 `no such table`，
/// 错误信息完全没提"你需要重建数据库"。补建逻辑因此必须存在，且必须
/// **只加缺失的对象、不碰已有的表**（重建会丢数据）。
///
/// 这里曾经还有两条"旧纸面账户迁成跟单员"的用例，随那次一次性迁移的移除一并删掉——
/// 代码只保留最新状态，不再维护"从旧版本升上来"的路径。
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
