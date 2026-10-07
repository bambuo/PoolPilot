namespace PoolPilot.Domain.Exceptions;

/// <summary>
/// 上游字段不符合实测口径时抛出。
///
/// 存在理由：本项目的全部领域结论都建立在对上游字段的**实测**之上，
/// 一旦上游改了类型或单位，必须立刻失败，绝不允许静默降级成 0 或猜测值——
/// 静默兜底会让错误一路传播到跟单收益计算里，比直接报错危险得多。
/// </summary>
public sealed class InvalidLighterFieldException : Exception
{
    public InvalidLighterFieldException(string fieldName, string rawValue, string expectation)
        : base($"字段 {fieldName} 的取值 \"{rawValue}\" 不符合实测口径：{expectation}")
    {
        FieldName = fieldName;
        RawValue = rawValue;
    }

    public string FieldName { get; }

    public string RawValue { get; }
}
