namespace PoolPilot.Application.Abstractions;

/// <summary>
/// 当前请求的登录用户。
///
/// 它把"是谁"这件事从 HTTP 上下文里抽出来：应用层与基础设施都能问"当下的主体是谁"，
/// 而不必认识 <c>HttpContext</c>。实现放在表现层（Api），因为只有它知道会话 Cookie 长什么样。
/// </summary>
public interface ICurrentUser
{
    /// <summary>当前登录用户的主键；未登录时为 <c>null</c>。</summary>
    long? UserId { get; }
}
