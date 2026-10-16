using System.Data.Common;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace PoolPilot.Infrastructure.Persistence;

/// <summary>
/// 建表与补表。**只负责"让库达到当前模型"，不负责"从某个旧版本升上来"**。
///
/// **为什么不用迁移工具链**：`dotnet-ef` 在本机需要额外的 net8 runtime（见 AGENTS.md 铁律 8），
/// 不值得为此引入。
///
/// **为什么不能只用 `EnsureCreated`**：它对**已存在**的数据库什么都不做。于是只要给模型加了
/// 新表（纸面跟单、权限那五张表都是后加的），那个库就永远没有新表，接口报 `no such table`，
/// 而错误信息完全不提"你需要重建数据库"。这个坑已经踩过一次。
///
/// 这里的做法：先 `EnsureCreated`（新库一次建全），再让 EF **由模型生成建表脚本**，
/// 只执行库里还没有的那几条。脚本来自模型本身，因此不存在"手写 DDL 与模型漂移"的问题，
/// 也不需要维护版本号——补的是"缺什么建什么"。
///
/// ⚠️ 它**不做数据迁移**：表结构变了（改列、改索引）不在这里修，历史数据也不在这里搬。
/// 这里曾经有过一批一次性迁移（纸面账户改跟单员、标的映射改平台口径、实盘流水补列等），
/// 都已经在开发机上执行完毕、按"代码只保留最新状态"移除。若拿一个旧库来跑，
/// 缺什么表补什么表；表存在但结构对不上时，做法是**删掉库文件重建**（台账数据不可重算的那部分
/// 只能先手工搬）。
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

    /// <summary>匹配 <c>CREATE TABLE "名字"</c> 或 <c>CREATE INDEX "名字"</c>。</summary>
    [GeneratedRegex("^CREATE\\s+(?:TABLE|(?:UNIQUE\\s+)?INDEX)\\s+(?:IF\\s+NOT\\s+EXISTS\\s+)?\"([^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ObjectNamePattern();
}
