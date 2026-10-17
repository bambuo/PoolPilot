namespace PoolPilot.Infrastructure.Persistence;

/// <summary>
/// SQLite 数据库的位置。默认口径与历史行为一致：`ContentRootPath/../../data`。
///
/// 打包成 Linux 服务后，内容根在 `/opt/poolpilot/api`，按默认口径会落到
/// `/opt/poolpilot/data`。那不符合 Linux 的 `/var/lib` 约定，也会让服务账号
/// 对安装目录有写权限。因此允许用配置（环境变量 `PoolPilot__DataDirectory`）
/// 显式指定数据目录；未配置时仍按原路径计算，本地开发行为零变化。
/// </summary>
public static class DatabasePath
{
    /// <summary>配置键；环境变量形式为 <c>PoolPilot__DataDirectory</c>。</summary>
    public const string DataDirectoryConfigurationKey = "PoolPilot:DataDirectory";

    /// <summary>数据库文件名。目录与文件名分开，便于迁移时只搬目录。</summary>
    public const string FileName = "poolpilot.db";

    /// <summary>
    /// 解析数据目录。空白配置按“未配置”处理；返回值一定是绝对路径。
    /// </summary>
    public static string ResolveDataDirectory(string contentRootPath, string? configuredDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        string directory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(contentRootPath, "..", "..", "data")
            : configuredDirectory;

        return Path.GetFullPath(directory);
    }

    /// <summary>解析数据库文件的绝对路径。</summary>
    public static string ResolveDatabaseFile(string contentRootPath, string? configuredDirectory) =>
        Path.Combine(ResolveDataDirectory(contentRootPath, configuredDirectory), FileName);
}
