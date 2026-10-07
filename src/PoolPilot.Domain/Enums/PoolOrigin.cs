namespace PoolPilot.Domain.Enums;

/// <summary>
/// 池的运营方归属。对应 Lighter 官方前端在操作员列上打的徽标。
/// </summary>
public enum PoolOrigin
{
    /// <summary>第三方社区运营者。官方前端显示为「社区」。</summary>
    Community,

    /// <summary>Lighter 官方运营。官方前端显示为「LIGHTER」。</summary>
    Lighter,
}
