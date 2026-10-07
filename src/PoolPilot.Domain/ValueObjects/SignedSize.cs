namespace PoolPilot.Domain.ValueObjects;

/// <summary>
/// 带方向的数量。正为多、负为空、零为平。
///
/// 存在的理由是一个已实测的陷阱：上游的 <see cref="Entities.PoolPosition.Size"/> 永远是**绝对值**，
/// 方向放在单独的 <c>Side</c> 字段里；而跟单要算的"目标仓位"必须是**带符号**的，
/// 否则多空转换、减仓、反手全部算不对。两种表示混在一起用，出错时不会报错——
/// 只会得到一个方向相反的仓位。所以用这个类型把两者隔开：
/// 从上游数据进来时必须显式选 <see cref="FromSide"/>，参与运算的一律是带符号值。
/// </summary>
public sealed record SignedSize
{
    public static readonly SignedSize Zero = new(0m);

    private SignedSize(decimal value) => Value = value;

    /// <summary>带符号数量。</summary>
    public decimal Value { get; }

    public static SignedSize From(decimal signedValue) => new(signedValue);

    /// <summary>由"绝对值 + 方向"构造，对应上游的表示法。</summary>
    public static SignedSize FromSide(decimal absoluteSize, Enums.PositionSide side) => new(
        absoluteSize == 0m
            ? 0m
            : side == Enums.PositionSide.Long ? Math.Abs(absoluteSize) : -Math.Abs(absoluteSize));

    /// <summary>绝对值（名义数量）。</summary>
    public decimal Absolute => Math.Abs(Value);

    /// <summary>方向；平仓时为 <c>null</c>。</summary>
    public Enums.PositionSide? Side => Value switch
    {
        > 0m => Enums.PositionSide.Long,
        < 0m => Enums.PositionSide.Short,
        _ => null,
    };

    public bool IsFlat => Value == 0m;

    /// <summary>方向是否一致（含"从平仓开出"）。用于判断一笔变动是加仓还是减仓。</summary>
    public bool HasSameDirectionAs(SignedSize other) =>
        Value != 0m && other.Value != 0m && Math.Sign(Value) == Math.Sign(other.Value);

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
