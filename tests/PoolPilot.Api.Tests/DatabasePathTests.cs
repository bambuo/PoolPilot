using PoolPilot.Infrastructure.Persistence;

namespace PoolPilot.Api.Tests;

/// <summary>
/// 数据目录的解析口径。打包后的服务把内容根放在 `/opt/poolpilot/api`，
/// 如果这里仍按默认的 `../../data` 落库，数据就会落到安装目录旁边；
/// `PoolPilot__DataDirectory` 是把它改到 `/var/lib/poolpilot` 的唯一入口。
/// </summary>
[TestClass]
public sealed class DatabasePathTests
{
    [TestMethod]
    public void ResolveDataDirectory_WithoutConfiguration_KeepsTheHistoricalRelativePath()
    {
        string contentRoot = Path.Combine(Path.GetTempPath(), "poolpilot-content-root");

        string actual = DatabasePath.ResolveDataDirectory(contentRoot, configuredDirectory: null);

        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(contentRoot, "..", "..", "data")),
            actual,
            "未配置时必须保持本地开发的既有落库位置，不能悄悄换目录");
    }

    [TestMethod]
    public void ResolveDataDirectory_WithConfiguration_UsesTheConfiguredAbsoluteDirectory()
    {
        string contentRoot = Path.Combine(Path.GetTempPath(), "poolpilot-content-root");
        string configured = Path.Combine(Path.GetTempPath(), "poolpilot-data");

        string actual = DatabasePath.ResolveDataDirectory(contentRoot, configured);

        Assert.AreEqual(Path.GetFullPath(configured), actual);
        Assert.IsTrue(Path.IsPathFullyQualified(actual), "数据目录必须是绝对路径，启动时才能直接建目录");
    }

    [TestMethod]
    public void ResolveDataDirectory_WithWhitespaceConfiguration_FallsBackToDefault()
    {
        string contentRoot = Path.Combine(Path.GetTempPath(), "poolpilot-content-root");

        string actual = DatabasePath.ResolveDataDirectory(contentRoot, "   ");

        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(contentRoot, "..", "..", "data")),
            actual,
            "空白配置按未配置处理，避免启动时把库落到当前目录");
    }

    [TestMethod]
    public void ResolveDatabaseFile_UsesTheConfiguredDirectoryAndFileName()
    {
        string configured = Path.Combine(Path.GetTempPath(), "poolpilot-data");

        string actual = DatabasePath.ResolveDatabaseFile("/srv/poolpilot/api", configured);

        Assert.AreEqual(Path.Combine(Path.GetFullPath(configured), DatabasePath.FileName), actual);
    }
}
