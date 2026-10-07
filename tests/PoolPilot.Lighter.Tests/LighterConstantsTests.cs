using PoolPilot.Domain.Constants;

namespace PoolPilot.Lighter.Tests;

/// <summary>
/// Lighter 端点与协议常量的自洽性检查。
///
/// 这一层不联网，属于"常量写错就会立刻暴露"的低成本防线；
/// 真正的联网契约测试（[TestCategory("Live")]）在接入 REST 客户端后补入。
/// </summary>
[TestClass]
public sealed class LighterConstantsTests
{
    [TestMethod]
    public void RestBaseUrl_UsesElliotAiDomain_NotLighterXyz()
    {
        // Arrange / Act：实测 api.lighter.xyz 等主机没有 DNS 解析，正确域名是 elliot.ai
        // Assert
        StringAssert.Contains(LighterConstants.MainnetRestBaseUrl, "zklighter.elliot.ai");
        Assert.IsFalse(LighterConstants.MainnetRestBaseUrl.Contains("lighter.xyz", StringComparison.Ordinal));
        StringAssert.EndsWith(LighterConstants.MainnetRestBaseUrl, "/api/v1");
    }

    [TestMethod]
    public void WebSocketUrl_IsSecureAndPointsToStream()
    {
        // Arrange / Act / Assert
        StringAssert.StartsWith(LighterConstants.MainnetWebSocketUrl, "wss://");
        StringAssert.EndsWith(LighterConstants.MainnetWebSocketUrl, "/stream");
    }

    [TestMethod]
    public void PublicPoolIndexFloor_ExceedsInt32Range()
    {
        // Arrange：池索引远超 int32，域模型必须用 long 承接
        // Act / Assert
        Assert.IsGreaterThan((long)int.MaxValue, LighterConstants.PublicPoolIndexFloor);
    }

    [TestMethod]
    public void WebSocketKeepAlive_IsComfortablyBelowDocumentedTimeout()
    {
        // Arrange：服务端要求 2 分钟内至少一帧，实测 120.573 秒被关闭
        const int documentedTimeoutSeconds = 120;

        // Act / Assert：取一半作为保活间隔，留一倍余量
        Assert.IsLessThan(documentedTimeoutSeconds, LighterConstants.WebSocketKeepAliveSeconds);
        Assert.IsGreaterThan(0, LighterConstants.WebSocketKeepAliveSeconds);
    }
}
