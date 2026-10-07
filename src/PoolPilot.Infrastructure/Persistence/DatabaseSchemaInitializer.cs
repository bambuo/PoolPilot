using System.Data.Common;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace PoolPilot.Infrastructure.Persistence;

/// <summary>
/// 建表与补表。
///
/// **为什么不用迁移**：`dotnet-ef` 在本机需要额外的 net8 runtime（见 AGENTS.md 铁律 8），
/// 而本项目数据可重新采集，不值得为此引入迁移工具链。
///
/// **为什么不能只用 `EnsureCreated`**：它对**已存在**的数据库什么都不做。
/// 于是只要给模型加了新表（纸面跟单就是后加的），那个库就永远没有新表，
/// 接口报 `no such table`，而错误信息完全不提"你需要重建数据库"。这个坑已经踩过一次。
///
/// 这里的做法：先 `EnsureCreated`（新库一次建全），再让 EF **由模型生成建表脚本**，
/// 只执行库里还没有的那几条。脚本来自模型本身，因此不存在"手写 DDL 与模型漂移"的问题，
/// 也不需要维护版本号——补的是"缺什么建什么"，而不是"从版本 N 升到 N+1"。
/// </summary>
public static partial class DatabaseSchemaInitializer
{
    /// <summary>补建缺失的表与索引，返回补建的对象名。</summary>
    public static async Task<IReadOnlyList<string>> EnsureSchemaAsync(
        PoolPilotDbContext db,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(logger);

        await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        // 必须**在补建之前**：旧表还在时，补建会拿着新索引去撞它（实测启动直接失败：
        // 'no such column: ...'）。这里迁移而不是丢表：映射里可能有人工作的判断
        await MigrateSymbolMappingsToPlatformAsync(db, logger, cancellationToken).ConfigureAwait(false);

        // 已有表加列也是"补建"覆盖不到的：补建只补缺失的对象，不动已存在的表结构
        await AddFollowerExchangeConfigAsync(db, logger, cancellationToken).ConfigureAwait(false);
        await SplitLiveOrderNotionalsAsync(db, logger, cancellationToken).ConfigureAwait(false);
        await AddLiveOrderTriggerAsync(db, logger, cancellationToken).ConfigureAwait(false);
        await AddLiveOrderPricesAsync(db, logger, cancellationToken).ConfigureAwait(false);

        string script = db.Database.GenerateCreateScript();
        List<string> created = [];

        foreach (string statement in SplitStatements(script))
        {
            string? objectName = ExtractObjectName(statement);

            if (objectName is null || await ExistsAsync(db, objectName, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            logger.LogWarning("数据库缺少对象 {Object}，按当前模型补建", objectName);
            await db.Database.ExecuteSqlRawAsync(statement, cancellationToken).ConfigureAwait(false);
            created.Add(objectName);
        }

        if (created.Count > 0)
        {
            logger.LogWarning(
                "已补建 {Count} 个数据库对象：{Objects}。补建只加新对象，不修改已存在的表结构——" +
                "若某张表的列变了，需要删除数据库文件重建。",
                created.Count,
                string.Join(", ", created));
        }

        await MigratePaperAccountsToFollowersAsync(db, logger, cancellationToken).ConfigureAwait(false);
        await DropRetiredSecretStorageAsync(db, logger, cancellationToken).ConfigureAwait(false);

        return created;
    }

    /// <summary>
    /// 清掉"加密主密钥"那一套留下的东西。
    ///
    /// 两件事：把凭据列从 <c>*Cipher</c> 改回本名（列名里带 Cipher 而内容其实是明文，
    /// 那是库里的一句假话，读库的人会被它误导），以及删掉只为存放主密钥而建的 <c>app_settings</c> 表。
    ///
    /// 为什么删表而不是留着空表：密钥材料不该以任何形式留在库里，哪怕只是曾经放过它的容器；
    /// 而且那张表除了主密钥没有别的用途。表里若还存着密钥行，一并随之消失。
    /// 整个过程幂等：列已改名或表已不在时什么都不做。
    /// </summary>
    private static async Task DropRetiredSecretStorageAsync(
        PoolPilotDbContext db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (await ExistsAsync(db, "exchange_configs", cancellationToken).ConfigureAwait(false))
        {
            // 三条改名语句全部写死。这里没有来自外部的输入，也就不该出现任何拼出来的 SQL
            // （拼 SQL 的写法会被分析器拦下，而且拦得对：一旦哪天名字来自输入，那就是注入）。
            foreach ((string from, string to, string sql) in new[]
            {
                ("ApiKeyCipher", "ApiKey", "ALTER TABLE \"exchange_configs\" RENAME COLUMN \"ApiKeyCipher\" TO \"ApiKey\""),
                ("ApiSecretCipher", "ApiSecret", "ALTER TABLE \"exchange_configs\" RENAME COLUMN \"ApiSecretCipher\" TO \"ApiSecret\""),
                ("PassphraseCipher", "Passphrase", "ALTER TABLE \"exchange_configs\" RENAME COLUMN \"PassphraseCipher\" TO \"Passphrase\""),
            })
            {
                // 只有旧列名还在、新列名还没出现时才改名。SQLite 的 RENAME COLUMN 不会自动跳过，
                // 重复执行会报错，所以判断必须显式做
                if (await ColumnExistsAsync(db, ColumnProbeSql, from, cancellationToken).ConfigureAwait(false)
                    && !await ColumnExistsAsync(db, ColumnProbeSql, to, cancellationToken).ConfigureAwait(false))
                {
                    await db.Database.ExecuteSqlRawAsync(sql, cancellationToken).ConfigureAwait(false);

                    logger.LogWarning("凭据列 {From} 已改名为 {To}（内容本就是明文，列名不该再叫 Cipher）", from, to);
                }
            }
        }

        if (await ExistsAsync(db, "app_settings", cancellationToken).ConfigureAwait(false))
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"app_settings\"", cancellationToken).ConfigureAwait(false);
            logger.LogWarning("已删除 app_settings 表：它只为加密主密钥而存在，而主密钥整套已移除");
        }
    }

    /// <summary>
    /// 迁移标的映射表：把"目标 = 交易所配置 Id"改成"目标 = 平台符号"。
    ///
    /// 这次做**真迁移**而不是丢表重建：映射里可能有人工作的判断（把某个标的指到哪一行是人的决定），
    /// 重建会把它们一起丢掉。所以是加列 → 从配置表取平台代码填进去 → 删旧索引 → 删旧列。
    /// 旧行的平台代码由 <c>exchange_configs.Code</c> 反查得到，不丢信息。
    ///
    /// ⚠️ 判据是"旧列还在且新列还没有"，因此可反复执行。
    /// </summary>
    /// <summary>
    /// 给跟单员表补上"实盘用哪个交易所配置"这一列。
    ///
    /// 不能靠补建：那个逻辑只补缺失的表与索引，不会给已存在的表加列
    /// （它的日志里写着这一点）。空值表示纸面模式——纸面不需要账号。
    /// </summary>
    private static async Task AddFollowerExchangeConfigAsync(
        PoolPilotDbContext db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!await ExistsAsync(db, "followers", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        const string probe = "PRAGMA table_info(\"followers\")";

        if (await ColumnExistsAsync(db, probe, "ExchangeConfigId", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"followers\" ADD COLUMN \"ExchangeConfigId\" INTEGER NULL",
            cancellationToken).ConfigureAwait(false);

        logger.LogWarning("跟单员表补上交易所配置列（实盘模式要落到某一个账号上）");
    }

    /// <summary>
    /// 把实盘流水里的"池子名义"与"目标名义"分开。
    ///
    /// 起因是一个字段被当成两个意思用：目标名义（池子名义 × 规模比例）被写进了
    /// <c>PoolNotional</c>，于是流水上"池子名义 19.07"读起来像池子只有 19 USDT，
    /// 而它实际是 2078 的池仓按 50/5441 比例复制后的目标。这种错位不会报错，只会误导读的人。
    ///
    /// 已有行的回填**是精确反推、不是猜**：目标名义 = 池仓名义 × (本金 ÷ 池子权益) 这条式子
    /// 两边都记在流水里（本金与池子权益同一条记录），所以池仓名义 = 目标名义 ÷ 比例，误差只来自浮点。
    /// 跳过行没有目标名义，那一侧填 0。
    /// </summary>
    private static async Task SplitLiveOrderNotionalsAsync(
        PoolPilotDbContext db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!await ExistsAsync(db, "live_orders", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        const string probe = "PRAGMA table_info(\"live_orders\")";

        if (await ColumnExistsAsync(db, probe, "TargetNotionalUsd", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"live_orders\" ADD COLUMN \"TargetNotionalUsd\" REAL NOT NULL DEFAULT 0",
            cancellationToken).ConfigureAwait(false);

        // 已下单的行：把那个错位的值搬到目标名义，再把池仓名义按比例反推回来
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"live_orders\" SET \"TargetNotionalUsd\" = \"PoolNotional\" WHERE \"Status\" = 'placed'",
            cancellationToken).ConfigureAwait(false);

        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"live_orders\" SET \"PoolNotional\" = \"TargetNotionalUsd\" / (\"Capital\" / \"PoolEquity\") "
            + "WHERE \"Status\" = 'placed' AND \"Capital\" > 0 AND \"PoolEquity\" > 0 AND \"TargetNotionalUsd\" <> 0",
            cancellationToken).ConfigureAwait(false);

        logger.LogWarning("实盘流水补上「目标名义」列，并把已下单行的两个名义额分开");
    }

    /// <summary>
    /// 给实盘流水补上"这一单是谁的决定"。已有行都是信号驱动的（手动平仓是后来才有的功能），
    /// 所以默认值 <c>signal</c> 对它们是准的。
    /// </summary>
    private static async Task AddLiveOrderTriggerAsync(
        PoolPilotDbContext db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!await ExistsAsync(db, "live_orders", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        const string probe = "PRAGMA table_info(\"live_orders\")";

        if (await ColumnExistsAsync(db, probe, "Trigger", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"live_orders\" ADD COLUMN \"Trigger\" TEXT NOT NULL DEFAULT 'signal'",
            cancellationToken).ConfigureAwait(false);

        logger.LogWarning("实盘流水补上触发者列（区分信号驱动的单与人手动下的单）");
    }

    /// <summary>
    /// 给实盘流水补上"开仓价 / 平仓价"两列。
    ///
    /// 回填只**近似**：早于"记录回执均价"功能的行，只有下单时的参考价可用，
    /// 市价单的成交价与参考价相差通常不到一个基点——列的语义是"这趟回合的入场/出场价"，
    /// 给一个差一个基点的近似数比全部留空更接近看表人的目的（近似在注释里说清，不装作精确）。
    /// </summary>
    private static async Task AddLiveOrderPricesAsync(
        PoolPilotDbContext db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!await ExistsAsync(db, "live_orders", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        const string probe = "PRAGMA table_info(\"live_orders\")";

        bool hasEntry = await ColumnExistsAsync(db, probe, "EntryPrice", cancellationToken).ConfigureAwait(false);
        bool hasClose = await ColumnExistsAsync(db, probe, "ClosePrice", cancellationToken).ConfigureAwait(false);

        if (hasEntry && hasClose)
        {
            return;
        }

        if (!hasEntry)
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"live_orders\" ADD COLUMN \"EntryPrice\" REAL NOT NULL DEFAULT 0",
                cancellationToken).ConfigureAwait(false);
        }

        if (!hasClose)
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"live_orders\" ADD COLUMN \"ClosePrice\" REAL NULL",
                cancellationToken).ConfigureAwait(false);
        }

        // 已下单的行：开仓单的"开仓价"与平仓单的"平仓价"都用参考价近似（市价单误差 < 1bp）
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"live_orders\" SET \"EntryPrice\" = \"Price\" "
            + "WHERE \"Status\" = 'placed' AND \"Intent\" IN ('open_long','open_short') AND \"EntryPrice\" = 0",
            cancellationToken).ConfigureAwait(false);

        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"live_orders\" SET \"ClosePrice\" = \"Price\" "
            + "WHERE \"Status\" = 'placed' AND \"Intent\" IN ('close_long','close_short') AND \"ClosePrice\" IS NULL",
            cancellationToken).ConfigureAwait(false);

        logger.LogWarning("实盘流水补上开仓价/平仓价两列（旧行用下单参考价近似回填）");
    }

    private static async Task MigrateSymbolMappingsToPlatformAsync(
        PoolPilotDbContext db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!await ExistsAsync(db, "symbol_mappings", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        const string probe = "PRAGMA table_info(\"symbol_mappings\")";

        // ① 拆掉"平台合约代码"那一列。它曾经把一家的写法（BTCUSDT / BTC-USDT-SWAP）
        //    当成系统的标的表示存了进来——那既不是本系统的口径，也让别处开始依赖它。
        //    标的一律用资产币 + 交易币两段表示，平台写法由对接实现在下单时自己转换。
        if (await ColumnExistsAsync(db, probe, "TargetSymbol", cancellationToken).ConfigureAwait(false))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"symbol_mappings\" DROP COLUMN \"TargetSymbol\"",
                cancellationToken).ConfigureAwait(false);

            logger.LogWarning("标的映射表拆掉平台合约代码列（标的表示统一为资产币 + 交易币）");
        }

        // ② 补上"是不是传统金融"这一列。判据各平台不同（币安看命名空间、OKX 看 instCategory、
        //    Gate 看 contract_type），所以结论要存下来，不能留一个隐式的"币安口径"当全局规则。
        //    已有行全是币安的扫描结果，那条字符串判据对它们是准的
        if (!await ColumnExistsAsync(db, probe, "TargetIsTradFi", cancellationToken).ConfigureAwait(false))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"symbol_mappings\" ADD COLUMN \"TargetIsTradFi\" INTEGER NOT NULL DEFAULT 0",
                cancellationToken).ConfigureAwait(false);

            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"symbol_mappings\" SET \"TargetIsTradFi\" = 1 "
                + "WHERE \"TargetContractType\" = 'TRADIFI_PERPETUAL'",
                cancellationToken).ConfigureAwait(false);

            logger.LogWarning("标的映射表补上「传统金融」标记列");
        }

        bool hasConfigId = await ColumnExistsAsync(db, probe, "ExchangeConfigId", cancellationToken)
            .ConfigureAwait(false);
        bool hasPlatform = await ColumnExistsAsync(db, probe, "TargetPlatform", cancellationToken)
            .ConfigureAwait(false);

        if (!hasConfigId || hasPlatform)
        {
            return;
        }

        logger.LogWarning("标的映射表改为平台口径：开始把「目标配置」迁移成「目标平台」");

        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"symbol_mappings\" ADD COLUMN \"TargetPlatform\" TEXT NOT NULL DEFAULT ''",
            cancellationToken).ConfigureAwait(false);

        // 从配置表把平台代码取回来：mapping.ExchangeConfigId → config.Code
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "symbol_mappings"
            SET "TargetPlatform" = COALESCE(
                (SELECT LOWER("Code") FROM "exchange_configs" WHERE "exchange_configs"."Id" = "symbol_mappings"."ExchangeConfigId"),
                'unknown')
            """,
            cancellationToken).ConfigureAwait(false);

        // 先删旧唯一索引再删旧列：SQLite 不允许删掉被索引引用的列
        await db.Database.ExecuteSqlRawAsync(
            "DROP INDEX IF EXISTS \"IX_symbol_mappings_ExchangeConfigId_SourceBase_SourceQuote\"",
            cancellationToken).ConfigureAwait(false);

        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"symbol_mappings\" DROP COLUMN \"ExchangeConfigId\"",
            cancellationToken).ConfigureAwait(false);

        // 新的唯一索引由下面的"按模型补建"负责建（不手写，避免与模型漂移）
        logger.LogWarning("标的映射表迁移完成：目标由配置 Id 改为平台符号，行数与人工判断都保留");
    }

    /// <summary>查表结构的语句（写死，见上面的理由）。</summary>
    private const string ColumnProbeSql = "PRAGMA table_info(\"exchange_configs\")";

    /// <summary>查 PRAGMA 判断某张表里有没有这一列。</summary>
    private static async Task<bool> ColumnExistsAsync(
        PoolPilotDbContext db,
        string pragmaSql,
        string column,
        CancellationToken cancellationToken)
    {
        await using DbConnection connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();
        command.CommandText = pragmaSql;

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 一次性数据迁移：旧的"纸面账户"（主键 = 池索引，一个池最多一个）迁成"跟单员"（主键自增，一个池可多个）。
    ///
    /// 为什么要迁而不是重建：流水是**无法重新采集**的东西——当时的盘口已经过去了，
    /// 重建等于把已经量出来的盈亏与摩擦清零。所以库里的账必须搬过去。
    ///
    /// 做法是"新表由模型建、数据用 SQL 搬"：列名与索引都来自 EF 生成的脚本，
    /// 这里只负责把行按 <c>池索引 → 跟单员 Id</c> 的映射搬过去，不手写任何 DDL。
    /// 整个过程在一个事务里，中途失败即回滚，下次启动重来，因此可以反复执行。
    /// </summary>
    private static async Task MigratePaperAccountsToFollowersAsync(
        PoolPilotDbContext db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!await ExistsAsync(db, "paper_accounts", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        logger.LogWarning("检测到旧的纸面账户表，开始迁移为跟单员结构");

        // 要删哪些旧表**在事务外**探明：探测用的是独立的连接与查询，
        // 而事务内的命令必须绑定到同一个事务对象，两者不能混。
        bool hasFills = await ExistsAsync(db, "paper_fills", cancellationToken).ConfigureAwait(false);
        bool hasCurve = await ExistsAsync(db, "paper_equity_points", cancellationToken).ConfigureAwait(false);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // 1) 账户 → 跟单员。lead_account_index 保存"当初跟的是哪个池"，
        //    这也是后面把流水改键到新 Id 的桥。
        //
        //    每一行都搬两个时间列（*Ticks 与 DateTimeOffset 各一份）：EF 把这两个属性
        //    都映射成了列，且时间列是 NOT NULL——只搬 ticks 会撞约束，而且两个列表示的是
        //    同一个时刻，从旧表里原样复制才不会引入时区/精度差异。
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "followers"
                ("LeadAccountIndex", "InitialCapital", "Mode", "IsActive", "CreatedAtUtcTicks", "CreatedAtUtc")
            SELECT "AccountIndex", "InitialCapital", 'Paper', "IsActive", "CreatedAtUtcTicks", "CreatedAtUtc"
            FROM "paper_accounts"
            """,
            cancellationToken).ConfigureAwait(false);

        // 2) 流水与权益点跟着账户走
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "follower_fills"
                ("FollowerId", "MarketId", "Symbol", "Side", "PoolPositionAfter", "PoolEquity",
                 "TargetPosition", "PositionBefore", "PositionAfter", "Size", "Price", "Notional",
                 "DepthSufficient", "Fee", "RealizedPnl", "SkipReason",
                 "ObservedAtUtcTicks", "ObservedAtUtc")
            SELECT f."Id", p."MarketId", p."Symbol", p."Side", p."PoolPositionAfter", p."PoolEquity",
                   p."TargetPosition", p."PositionBefore", p."PositionAfter", p."Size", p."Price", p."Notional",
                   p."DepthSufficient", p."Fee", p."RealizedPnl", p."SkipReason",
                   p."ObservedAtUtcTicks", p."ObservedAtUtc"
            FROM "paper_fills" p
            JOIN "followers" f ON f."LeadAccountIndex" = p."AccountIndex"
            """,
            cancellationToken).ConfigureAwait(false);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "follower_equity_points"
                ("FollowerId", "Equity", "RealizedPnl", "UnrealizedPnl", "Fees", "GrossNotional",
                 "TakenAtUtcTicks", "TakenAtUtc")
            SELECT f."Id", p."Equity", p."RealizedPnl", p."UnrealizedPnl", p."Fees", p."GrossNotional",
                   p."TakenAtUtcTicks", p."TakenAtUtc"
            FROM "paper_equity_points" p
            JOIN "followers" f ON f."LeadAccountIndex" = p."AccountIndex"
            """,
            cancellationToken).ConfigureAwait(false);

        // 3) 旧表退场。留着它们会让"到底哪张表是真相"变成每次排查都要先回答的问题。
        //    表名全部写死在这里，不做任何拼接——迁移里没有来自外部的输入，
        //    也就不该出现任何拼接出来的 SQL。
        if (hasFills)
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"paper_fills\"", cancellationToken).ConfigureAwait(false);
        }

        if (hasCurve)
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"paper_equity_points\"", cancellationToken).ConfigureAwait(false);
        }

        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"paper_accounts\"", cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        logger.LogWarning("迁移完成：纸面账户已成为跟单员，历史流水与权益曲线保持不变");
    }

    /// <summary>把建表脚本切成单条语句。按分号切即可：EF 生成的脚本里字符串常量不含裸分号。</summary>
    private static IEnumerable<string> SplitStatements(string script) =>
        script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase));

    /// <summary>取语句创建的对象名（被双引号包住的第一个标识符）。非建表/建索引语句返回 null。</summary>
    private static string? ExtractObjectName(string statement)
    {
        Match match = ObjectNamePattern().Match(statement);

        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>查 sqlite_master 判断对象是否已存在。</summary>
    private static async Task<bool> ExistsAsync(
        PoolPilotDbContext db,
        string objectName,
        CancellationToken cancellationToken)
    {
        await using DbConnection connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = $name";
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = objectName;
        command.Parameters.Add(parameter);

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture) > 0L;
    }

    /// <summary>匹配 <c>CREATE TABLE "名字"</c> 或 <c>CREATE INDEX "名字"</c>。</summary>
    [GeneratedRegex("^CREATE\\s+(?:TABLE|(?:UNIQUE\\s+)?INDEX)\\s+(?:IF\\s+NOT\\s+EXISTS\\s+)?\"([^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ObjectNamePattern();
}
