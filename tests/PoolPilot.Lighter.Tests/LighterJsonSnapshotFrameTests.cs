using System.Text.Json;

using PoolPilot.Infrastructure.Lighter;

namespace PoolPilot.Lighter.Tests;

/// <summary>
/// 订阅确认帧的识别测试。
///
/// 锁住的是 <c>CollectAsync</c> 的停止条件不能用"含某个数据字段就停"这个事实：
/// 增量帧同样含那些字段，一旦增量先于确认到达，我们就会把增量当成完整快照，
/// 返回残缺的挂单列表 / 盘口 / 成交。实测池子连续挂单时挂单每几秒一变，
/// 这个竞态不是理论风险。
/// </summary>
[TestClass]
public sealed class LighterJsonSnapshotFrameTests
{
    private static JsonElement Frame(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [TestMethod]
    public void IsSubscriptionSnapshot_ForChannelAck_IsTrue()
    {
        // 实测四个频道的确认帧 type 统一为 subscribed/<名字>，且携带完整快照
        foreach (string channel in new[] { "pool_info", "account_all_trades", "account_all_orders", "order_book" })
        {
            // 用普通字符串拼接：原始内插字符串里连续的右大括号需要额外的 $ 前缀，读起来更绕
            JsonElement frame = Frame(
                "{\"type\":\"subscribed/" + channel + "\",\"" + channel + "\":{}}");

            Assert.IsTrue(LighterJson.IsSubscriptionSnapshot(frame), $"{channel} 的确认帧应被识别");
        }
    }

    [TestMethod]
    public void IsSubscriptionSnapshot_ForIncrementFrameWithDataField_IsFalse()
    {
        // 关键：增量帧**也含** orders 字段，若按字段判断就会误把增量当完整快照
        JsonElement increment = Frame("""{"type":"update/account_all_orders","orders":{"45":[]}}""");

        Assert.IsFalse(
            LighterJson.IsSubscriptionSnapshot(increment),
            "含 orders 字段的增量帧不是完整快照，不能据此停止收集");
    }

    [TestMethod]
    public void IsSubscriptionSnapshot_ForLifecycleMarkers_IsFalse()
    {
        Assert.IsFalse(LighterJson.IsSubscriptionSnapshot(Frame("""{"type":"connected"}""")));
        Assert.IsFalse(LighterJson.IsSubscriptionSnapshot(Frame("""{"type":"pong"}""")));
        Assert.IsFalse(LighterJson.IsSubscriptionSnapshot(Frame("""{"type":"first_connect"}""")));
    }

    [TestMethod]
    public void IsSubscriptionSnapshot_WithoutTypeField_IsFalse()
    {
        Assert.IsFalse(LighterJson.IsSubscriptionSnapshot(Frame("""{"orders":{}}""")));
    }
}
