using System.Globalization;
using System.Text.Json;

using PoolPilot.Domain.Exceptions;

namespace PoolPilot.Infrastructure.Lighter;

/// <summary>
/// 上游 JSON 的读取辅助。
///
/// 存在理由：Lighter 的字段类型混用（金额是字符串、统计量是数字、时间戳有四种刻度），
/// 直接反序列化到强类型会在类型不符时静默得到默认值。这里全部走显式读取，
/// 缺失或格式不符时抛 <see cref="InvalidLighterFieldException"/>——
/// 静默降级成 0 会让错误一路传播到跟单收益计算里。
/// </summary>
internal static class LighterJson
{
    /// <summary>校验响应信封。<c>code</c> 非 200 时视为失败——HTTP 200 与业务成功不等价。</summary>
    public static void EnsureSuccess(JsonElement root, string endpoint)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidLighterFieldException(endpoint, root.ValueKind.ToString(), "响应应为 JSON 对象");
        }

        if (!root.TryGetProperty("code", out JsonElement codeElement))
        {
            return;
        }

        int code = codeElement.ValueKind == JsonValueKind.Number
            ? codeElement.GetInt32()
            : int.TryParse(codeElement.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : 0;

        if (code is not (0 or 200))
        {
            string message = root.TryGetProperty("message", out JsonElement messageElement)
                ? messageElement.GetString() ?? string.Empty
                : string.Empty;

            throw new InvalidLighterFieldException(endpoint, code.ToString(CultureInfo.InvariantCulture), $"上游返回业务错误：{message}");
        }
    }

    public static JsonElement Require(JsonElement parent, string name, string owner)
    {
        if (!parent.TryGetProperty(name, out JsonElement value))
        {
            throw new InvalidLighterFieldException($"{owner}.{name}", "(缺失)", "该字段为必需");
        }

        return value;
    }

    public static JsonElement? Optional(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    /// <summary>
    /// 该帧是否为**订阅确认**——即携带完整初始快照的那一帧。
    ///
    /// 用它作为 <c>CollectAsync</c> 的停止条件，而**不要**用"含某个数据字段就停"。
    /// 理由：增量帧同样含那些字段。一旦增量先于确认到达，我们就会把增量当成完整快照，
    /// 返回残缺的挂单列表 / 盘口 / 成交——实测挂单在池子连续挂单时每几秒一变，
    /// 这种竞态并非理论风险。而残缺盘口会直接污染漏损与报价计算。
    ///
    /// 实测四个频道（<c>pool_info</c>、<c>account_all_trades</c>、<c>account_all_orders</c>、
    /// <c>order_book</c>）的确认帧类型统一为 <c>subscribed/&lt;名字&gt;</c>。
    /// </summary>
    public static bool IsSubscriptionSnapshot(JsonElement message)
    {
        if (!message.TryGetProperty("type", out JsonElement type)
            || type.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return type.GetString() is { } text
            && text.StartsWith("subscribed/", StringComparison.Ordinal);
    }

    public static string RequireString(JsonElement parent, string name, string owner) =>
        Require(parent, name, owner).GetString()
        ?? throw new InvalidLighterFieldException($"{owner}.{name}", "(null)", "该字段不应为 null");

    public static string OptionalString(JsonElement parent, string name, string fallback = "") =>
        Optional(parent, name)?.GetString() ?? fallback;

    /// <summary>读取字符串形式的十进制数（上游的金额字段形态）。</summary>
    public static decimal RequireDecimalText(JsonElement parent, string name, string owner)
    {
        JsonElement element = Require(parent, name, owner);

        return element.ValueKind switch
        {
            JsonValueKind.String => LighterNumericText.Parse($"{owner}.{name}", element.GetString()!),
            JsonValueKind.Number => element.GetDecimal(),
            _ => throw new InvalidLighterFieldException($"{owner}.{name}", element.ValueKind.ToString(), "应为十进制数或数字字符串"),
        };
    }

    public static decimal OptionalDecimalText(JsonElement parent, string name, decimal fallback = 0m)
    {
        JsonElement? element = Optional(parent, name);

        if (element is null)
        {
            return fallback;
        }

        return element.Value.ValueKind switch
        {
            JsonValueKind.String => LighterNumericText.Parse(name, element.Value.GetString()!),
            JsonValueKind.Number => element.Value.GetDecimal(),
            _ => fallback,
        };
    }

    /// <summary>读取数字（上游的统计类字段形态）。字符串数字也能容错读取。</summary>
    public static decimal RequireNumber(JsonElement parent, string name, string owner)
    {
        JsonElement element = Require(parent, name, owner);

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDecimal(),
            JsonValueKind.String => LighterNumericText.Parse($"{owner}.{name}", element.GetString()!),
            _ => throw new InvalidLighterFieldException($"{owner}.{name}", element.ValueKind.ToString(), "应为数字"),
        };
    }

    public static decimal OptionalNumber(JsonElement parent, string name, decimal fallback = 0m)
    {
        JsonElement? element = Optional(parent, name);

        if (element is null)
        {
            return fallback;
        }

        return element.Value.ValueKind switch
        {
            JsonValueKind.Number => element.Value.GetDecimal(),
            JsonValueKind.String => LighterNumericText.Parse(name, element.Value.GetString()!),
            _ => fallback,
        };
    }

    public static long RequireLong(JsonElement parent, string name, string owner)
    {
        JsonElement element = Require(parent, name, owner);

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.GetInt64(),
            JsonValueKind.String => long.Parse(element.GetString()!, NumberStyles.Integer, CultureInfo.InvariantCulture),
            _ => throw new InvalidLighterFieldException($"{owner}.{name}", element.ValueKind.ToString(), "应为整数"),
        };
    }

    public static long OptionalLong(JsonElement parent, string name, long fallback = 0L)
    {
        JsonElement? element = Optional(parent, name);

        if (element is null)
        {
            return fallback;
        }

        return element.Value.ValueKind switch
        {
            JsonValueKind.Number => element.Value.GetInt64(),
            JsonValueKind.String => long.TryParse(element.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : fallback,
            _ => fallback,
        };
    }

    public static int RequireInt(JsonElement parent, string name, string owner) => (int)RequireLong(parent, name, owner);

    public static int OptionalInt(JsonElement parent, string name, int fallback = 0) => (int)OptionalLong(parent, name, fallback);

    public static bool OptionalBool(JsonElement parent, string name, bool fallback = false)
    {
        JsonElement? element = Optional(parent, name);

        return element?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    public static IReadOnlyList<JsonElement> RequireArray(JsonElement parent, string name, string owner)
    {
        JsonElement element = Require(parent, name, owner);

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidLighterFieldException($"{owner}.{name}", element.ValueKind.ToString(), "应为数组");
        }

        return [.. element.EnumerateArray()];
    }

    public static IReadOnlyList<JsonElement> OptionalArray(JsonElement parent, string name)
    {
        JsonElement? element = Optional(parent, name);

        return element?.ValueKind == JsonValueKind.Array
            ? [.. element.Value.EnumerateArray()]
            : [];
    }
}

/// <summary>
/// 字符串数值的解析入口。
///
/// 与 <see cref="PoolPilot.Domain.ValueObjects.LighterNumeric"/> 的差别：
/// 那个面向领域层、返回 decimal；这个面向基础设施层、带字段名以便报错定位。
/// 两者共用同一套"负零归一化"规则。
/// </summary>
internal static class LighterNumericText
{
    public static decimal Parse(string fieldName, string raw)
    {
        if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value))
        {
            throw new InvalidLighterFieldException(fieldName, raw, "应为十进制数字符串");
        }

        // 上游的零仓 position_value 会返回 "-0.000000"（负零），归一化为 0
        return value == 0m ? 0m : value;
    }
}
