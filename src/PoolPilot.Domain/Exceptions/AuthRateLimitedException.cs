namespace PoolPilot.Domain.Exceptions;

/// <summary>
/// 触发了本地限流：验证码重发冷却、登录失败锁定、挑战尝试次数用尽。
///
/// 单独成类而不是复用 <see cref="ArgumentException"/>：参数本身没错，是"现在不行、等一会儿可以"。
/// 中间件据此回 429 而不是 400——这两种答复在界面上要说的话、用户该做的事完全不同
/// （一个是"改改再提交"，一个是"等 N 秒再来"）。
/// </summary>
public sealed class AuthRateLimitedException(string message) : Exception(message);
