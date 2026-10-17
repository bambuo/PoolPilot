using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using PoolPilot.Domain.Entities;
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
/// 2026-10 起这一趟还多了一层：**补列**（followers 的公开面四列就是这么长在老库上的）。
/// 于是这里多了三条针对列的钉子——可空列要补得上且不许改写老行，
/// NOT NULL 无默认值的缺列只许记 Warning 不许猜。
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

    // ── 补列（公开面四列 + 那条「不许猜」的边界）──────────────

    /// <summary>followers 的公开面四列——全可空，正是补列这一趟允许的形态。</summary>
    private static readonly string[] PublicFollowerColumns =
        ["IsPublic", "OwnerUserId", "OwnerCredentialHash", "Label"];

    [TestMethod]
    public void EnsureSchema_WhenFollowersMissingPublicColumns_BackfillsThemAndKeepsOldRowsIntact()
    {
        string path = Temp();

        try
        {
            long oldRowId;

            using (PoolPilotDbContext db = Open(path))
            {
                DatabaseSchemaInitializer.EnsureSchemaAsync(db, NullLogger.Instance).GetAwaiter().GetResult();

                FollowerRecord row = new()
                {
                    LeadAccountIndex = 42L,
                    InitialCapital = 500m,
                    Mode = "Paper",
                    IsActive = true,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                };

                db.Followers.Add(row);
                db.SaveChangesAsync().GetAwaiter().GetResult();
                oldRowId = row.Id;
            }

            // 假装这是公开面引入之前的老库：把那四列删掉（都可空、无索引，SQLite 允许逐列 DROP）
            foreach (string column in PublicFollowerColumns)
            {
                Execute(path, $"ALTER TABLE \"followers\" DROP COLUMN \"{column}\"");
            }

            using (PoolPilotDbContext db = Open(path))
            {
                IReadOnlyList<string> created = DatabaseSchemaInitializer
                    .EnsureSchemaAsync(db, NullLogger.Instance).GetAwaiter().GetResult();

                foreach (string column in PublicFollowerColumns)
                {
                    CollectionAssert.Contains(
                        created.ToList(),
                        $"followers.{column}",
                        $"缺列 {column} 应当被按当前模型补上，并出现在返回的清单里");

                    Assert.IsTrue(ColumnExists(path, "followers", column), $"补列后库里应含 {column}");
                }
            }

            // 老行行为零变化：补出来的 NULL 在领域侧读成「工作台建的行」，
            // 而且私有端点的读路径（FollowerRepository）依然读得到它
            using (PoolPilotDbContext db = Open(path))
            {
                Follower? stored = new FollowerRepository(db)
                    .GetAsync(oldRowId).GetAwaiter().GetResult();

                Assert.IsNotNull(stored, "补列不该动既有行——私有列表读的就是这一行");
                Assert.IsFalse(stored.IsPublic, "NULL 收口成 false：老行永远不是公开行");
                Assert.IsNull(stored.OwnerUserId);
                Assert.IsNull(stored.OwnerCredentialHash);
                Assert.IsNull(stored.Label);

                // 库里存的确实是 NULL 而不是被顺手改写成了 false/空串：改写老行不在这趟的权力范围内
                Assert.AreEqual(
                    1L,
                    CountRows(path, "followers", "IsPublic IS NULL AND OwnerUserId IS NULL AND Label IS NULL"),
                    "补列只能给老行落 NULL，不许替它们编一个值");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// roles 的注册默认标记：非空、有默认值（false），所以老库可以原地补上，
    /// 不需要删库重建。补出来的值必须是 false——"老角色默认接管新注册"是个业务决定，
    /// 不能由补列代码替人做。
    /// </summary>
    [TestMethod]
    public void EnsureSchema_WhenRolesMissingRegistrationDefault_BackfillsFalse()
    {
        string path = Temp();

        try
        {
            using (PoolPilotDbContext db = Open(path))
            {
                DatabaseSchemaInitializer.EnsureSchemaAsync(db, NullLogger.Instance).GetAwaiter().GetResult();

                db.Roles.Add(new RoleRecord { Code = "member", Title = "普通成员", IsSystem = false });
                db.SaveChangesAsync().GetAwaiter().GetResult();
            }

            Execute(path, "ALTER TABLE \"roles\" DROP COLUMN \"IsRegistrationDefault\"");

            using (PoolPilotDbContext db = Open(path))
            {
                IReadOnlyList<string> created = DatabaseSchemaInitializer
                    .EnsureSchemaAsync(db, NullLogger.Instance).GetAwaiter().GetResult();

                CollectionAssert.Contains(
                    created.ToList(),
                    "roles.IsRegistrationDefault",
                    "有默认值的非空列应当被原地补上");
            }

            using (PoolPilotDbContext db = Open(path))
            {
                RoleRecord stored = db.Roles.Single();

                Assert.AreEqual("member", stored.Code);
                Assert.IsFalse(stored.IsRegistrationDefault, "老角色不能被自动设成注册默认");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// exchange_configs 的归属列：可空，老库补列后为 NULL。NULL 在作用域里读成
    /// "只有 superadmin 可见"，不能顺手分给第一个账号。
    /// </summary>
    [TestMethod]
    public void EnsureSchema_WhenExchangeConfigsMissingOwner_BackfillsNull()
    {
        string path = Temp();

        try
        {
            using (PoolPilotDbContext db = Open(path))
            {
                DatabaseSchemaInitializer.EnsureSchemaAsync(db, NullLogger.Instance).GetAwaiter().GetResult();

                db.ExchangeConfigs.Add(new ExchangeConfigRecord
                {
                    Code = "binance",
                    Kind = "cex",
                    DisplayName = "老配置",
                    MakerFeePercent = 0.02m,
                    TakerFeePercent = 0.05m,
                    IsEnabled = true,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                });

                db.SaveChangesAsync().GetAwaiter().GetResult();
            }

            Execute(path, "ALTER TABLE \"exchange_configs\" DROP COLUMN \"OwnerUserId\"");

            using (PoolPilotDbContext db = Open(path))
            {
                IReadOnlyList<string> created = DatabaseSchemaInitializer
                    .EnsureSchemaAsync(db, NullLogger.Instance).GetAwaiter().GetResult();

                CollectionAssert.Contains(
                    created.ToList(),
                    "exchange_configs.OwnerUserId",
                    "可空归属列应当被原地补上");
            }

            using (PoolPilotDbContext db = Open(path))
            {
                Assert.IsNull(db.ExchangeConfigs.Single().OwnerUserId, "老配置不能被自动分配归属");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void EnsureSchema_WhenMissingNotNullColumnWithoutDefault_WarnsAndDoesNotGuess()
    {
        string path = Temp();

        try
        {
            using (PoolPilotDbContext db = Open(path))
            {
                DatabaseSchemaInitializer.EnsureSchemaAsync(db, NullLogger.Instance).GetAwaiter().GetResult();

                db.Followers.Add(new FollowerRecord
                {
                    LeadAccountIndex = 42L,
                    InitialCapital = 500m,
                    Mode = "Paper",
                    IsActive = true,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                });

                db.SaveChangesAsync().GetAwaiter().GetResult();
            }

            // IsActive：NOT NULL、无默认值。「该给老行填什么」是一个业务决定，补列这一趟不许替人做
            Execute(path, "ALTER TABLE \"followers\" DROP COLUMN \"IsActive\"");

            List<string> warnings = [];

            using (PoolPilotDbContext db = Open(path))
            {
                IReadOnlyList<string> created = DatabaseSchemaInitializer
                    .EnsureSchemaAsync(db, new CollectingLogger(warnings)).GetAwaiter().GetResult();

                Assert.IsFalse(
                    created.Contains("followers.IsActive"),
                    "NOT NULL 且无默认值的缺列不许被补上——补它等于把猜出来的值写进每个人的库");

                Assert.IsTrue(
                    warnings.Any(w => w.Contains("IsActive", StringComparison.Ordinal)),
                    "必须留下一条点名这一列的 Warning，否则使用者只会看到查询报错");
            }

            Assert.IsFalse(ColumnExists(path, "followers", "IsActive"), "不补就是真没补，Warning 之后库里状态不变");
            Assert.AreEqual(1L, CountRows(path, "followers"), "这一趟一个老行都不该动");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static bool ColumnExists(string path, string table, string column)
    {
        using SqliteConnection connection = new($"Data Source={path}");
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM PRAGMA_TABLE_INFO('{table}') WHERE name = '{column}'";

        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>只收 Warning 的极简 logger：补列这一趟的「不许猜」是靠一条日志兑现的，
    /// 没有它就只能靠读代码的人相信那行 if 存在。</summary>
    private sealed class CollectingLogger(List<string> warnings) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                warnings.Add(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
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
