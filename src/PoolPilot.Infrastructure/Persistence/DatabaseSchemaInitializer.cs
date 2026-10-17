using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;

namespace PoolPilot.Infrastructure.Persistence;

/// <summary>
/// 让已有库达到**当前模型**：建缺失的表与索引，补已有表里缺的列。
///
/// **为什么不用迁移工具链**：`dotnet-ef` 在本机需要额外的 net8 runtime（见 AGENTS.md 铁律 8），
/// 不值得为此引入。
///
/// **为什么不能只用 `EnsureCreated`**：它对**已存在**的数据库什么都不做。于是只要给模型加了
/// 新表（纸面跟单、权限那五张表都是后加的），那个库就永远没有新表，接口报 `no such table`，
/// 而错误信息完全不提"你需要重建数据库"。这个坑已经踩过一次。
///
/// 做法是三步，全都以模型为唯一事实源（因此不存在"手写 DDL 与模型漂移"，也不需要版本号）：
/// <list type="number">
///   <item>`EnsureCreated`：新库一次建全。</item>
///   <item>让 EF 由模型生成建表脚本，只执行库里还没有的那几条（缺什么表建什么表，
///       连带它自己的索引）。</item>
///   <item>**补列**：逐个实体读 `PRAGMA table_info`，把模型里有、库里没有的列
///       `ALTER TABLE ... ADD COLUMN` 上去。</item>
/// </list>
///
/// ## 补列算不算"迁移"？
///
/// 不算。这一趟做的是**"让已有库达到当前模型"**——和补表同一件事，只是粒度细到列：
/// 模型说这一列该在，库里没有，就让它在那儿。它不解释"这个库以前是哪个版本"，
/// 不搬历史数据，也不改已有列的类型、含义或约束。
///
/// 只有两类列会被补上，这个限制来自 SQLite 本身而不是偏好：
/// <list type="bullet">
///   <item>**可空列**：已有行取 NULL。</item>
///   <item>**有默认值的列**：已有行取那个默认值。</item>
/// </list>
/// NOT NULL 且没有默认值的缺失列**不猜**（SQLite 在这里也会直接失败：给已有行加一个
/// 无值可填的 NOT NULL 列本来就不被允许），只记一条 Warning 说明它需要删库重建——
/// "该给老行填什么"是一个业务决定，让补列代码替人做这个决定等于把一条未经审核的数据
/// 写进每个人的库。
///
/// ## 推翻入口
///
/// **删掉库文件让它按当前模型重建**，是这一类改动的正规出路：
/// 改列类型、改列语义、改索引、给已有行补 NOT NULL 列、以及任何"老数据在新规则下说不通"的改动，
/// 都不在这里做。台账数据不可重算的那部分要先手工搬。
/// 补列存在的意义只是"加一列可选信息不该让你重建库"，不是"从此不需要重建库"。
/// </summary>
public static partial class DatabaseSchemaInitializer
{
    /// <summary>
    /// 补建缺失的表、索引与列，返回补建的对象名。
    /// 列用 <c>表名.列名</c> 的形式（与表名放在同一个清单里，日志与测试都按这一条读）。
    /// </summary>
    public static async Task<IReadOnlyList<string>> EnsureSchemaAsync(
        PoolPilotDbContext db,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(logger);

        await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        List<string> created = [];

        string script = db.Database.GenerateCreateScript();

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

        // 顺序不能倒：表刚建出来时列本来就是齐的，先补表再补列才不会把新建的表也过一遍
        created.AddRange(await AddMissingColumnsAsync(db, logger, cancellationToken).ConfigureAwait(false));

        if (created.Count > 0)
        {
            logger.LogWarning(
                "已按当前模型补齐 {Count} 个数据库对象：{Objects}。"
                + "补建只加库里缺的东西（表、索引、可空列或有默认值的列），不改任何已有列的语义；"
                + "NOT NULL 且无默认值的缺列不在此列——那种改动需要删除数据库文件重建（见类说明）。",
                created.Count,
                string.Join(", ", created));
        }

        return created;
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

    /// <summary>
    /// 逐个实体比对"模型里的列"与"库里的列"，缺的补上。
    ///
    /// 一条连接跑完整趟：每张表一次 PRAGMA，实体一共十几张，没有理由为此开十几个连接。
    /// </summary>
    private static async Task<IReadOnlyList<string>> AddMissingColumnsAsync(
        PoolPilotDbContext db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        List<string> added = [];

        await using DbConnection connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (ITable table in db.Model.GetRelationalModel().Tables)
        {
            HashSet<string> present = await ReadColumnNamesAsync(connection, table.Name, cancellationToken)
                .ConfigureAwait(false);

            // 库里根本没有这张表：它在上一趟刚被建出来（列本来就是齐的），或者模型里没有它
            if (present.Count == 0)
            {
                continue;
            }

            foreach (IColumn column in table.Columns)
            {
                if (present.Contains(column.Name))
                {
                    continue;
                }

                string? definition = DescribeColumnForAdd(table.Name, column, logger);

                if (definition is null)
                {
                    continue;
                }

                await using DbCommand command = connection.CreateCommand();
                command.CommandText = $"ALTER TABLE {QuoteIdentifier(table.Name)} ADD COLUMN {definition}";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                string name = $"{table.Name}.{column.Name}";

                logger.LogWarning("表 {Table} 缺少列 {Column}，已按当前模型补上：{Definition}",
                    table.Name, column.Name, definition);

                added.Add(name);
            }
        }

        return added;
    }

    /// <summary>
    /// 这一列该怎么补。返回 <c>null</c> 表示**不补**（并已记下原因）：
    /// 不补的列一律是"该给老行填什么值"这个业务决定还没人做，那要删库重建。
    /// </summary>
    private static string? DescribeColumnForAdd(string tableName, IColumn column, ILogger logger)
    {
        if (column.StoreType is not { Length: > 0 } storeType)
        {
            logger.LogWarning(
                "表 {Table} 的列 {Column} 在模型里没有存储类型，无法补列——需要删除数据库文件重建",
                tableName,
                column.Name);

            return null;
        }

        if (column.IsNullable)
        {
            return $"{QuoteIdentifier(column.Name)} {storeType} NULL";
        }

        string? defaultValue = column.DefaultValueSql ?? RenderConstant(column.DefaultValue);

        if (defaultValue is null)
        {
            logger.LogWarning(
                "表 {Table} 缺少 NOT NULL 且没有默认值的列 {Column}：不猜该给已有行填什么值"
                + "（那是一个业务决定）——这一类改动请删除数据库文件重建",
                tableName,
                column.Name);

            return null;
        }

        return $"{QuoteIdentifier(column.Name)} {storeType} NOT NULL DEFAULT {defaultValue}";
    }

    /// <summary>
    /// 把 CLR 默认值渲染成 SQL 字面量。
    ///
    /// 只覆盖"字面量本身就是那个值"的几类；认不出来的一律返回 null（= 不补），
    /// 因为这里宁可漏补一列让人看到 Warning，也不能把一个猜出来的默认值写进每个人的库。
    /// 日期时间类刻意不算在内：它们的文本口径由列映射决定，用 <c>ToString</c> 猜一次就是赌。
    /// </summary>
    private static string? RenderConstant(object? value) => value switch
    {
        null => null,
        bool flag => flag ? "1" : "0",
        string text => $"'{text.Replace("'", "''", StringComparison.Ordinal)}'",
        DateTime or DateTimeOffset or DateOnly or TimeOnly => null,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),

        // 认不出来的类型（Guid、枚举盒装…）一律不补：这里宁可漏补一列让人看到 Warning，
        // 也不能把一个猜出来的默认值写进每个人的库
        _ => null,
    };

    /// <summary>读库里已有的列名（PRAGMA 对不存在的表返回零行，正好当作"这张表还没有"）。</summary>
    private static async Task<HashSet<string>> ReadColumnNamesAsync(
        DbConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        HashSet<string> names = new(StringComparer.Ordinal);

        await using DbCommand command = connection.CreateCommand();

        // PRAGMA 不参与参数绑定（SQLite 的语句形状里就没有占位符），标识符只能拼进文本；
        // 表名来自模型而不是用户输入，QuoteIdentifier 仍把双引号转义掉，不给自己留口子
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)})";

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        int nameOrdinal = reader.GetOrdinal("name");

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            names.Add(reader.GetString(nameOrdinal));
        }

        return names;
    }

    private static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    /// <summary>匹配 <c>CREATE TABLE "名字"</c> 或 <c>CREATE INDEX "名字"</c>。</summary>
    [GeneratedRegex("^CREATE\\s+(?:TABLE|(?:UNIQUE\\s+)?INDEX)\\s+(?:IF\\s+NOT\\s+EXISTS\\s+)?\"([^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ObjectNamePattern();
}
