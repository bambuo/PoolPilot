namespace PoolPilot.Application.Abstractions;

/// <summary>
/// 公开页的"调用者是谁"。
///
/// 为什么不是一个布尔或一个 Id，而是两个都可能为 <c>null</c> 的零件：
/// 公开页同时接受匿名与已登录的访问，而两条归属路各自独立——
/// 登录建的行按 <see cref="UserId"/> 认人，匿名建的行按访客密钥派生的哈希认人。
/// 用例需要这两样才能判"这一行是不是你建的"，但也只有这两样。
///
/// <see cref="VisitorSecret"/> 是 Api 层从 <c>poolpilot_visitor</c> Cookie 里读出来的值
/// （没有 Cookie 时是刚生成、准备签发的候选值）。Application 层因此不认识 HttpContext，
/// 而这个值也**永远不会**出现在任何响应体或 URL 里——它只进哈希。
/// </summary>
/// <param name="UserId">会话用户的 Id；未登录时为 <c>null</c>。</param>
/// <param name="VisitorSecret">64 位小写十六进制的访客密钥。读路径上没有 Cookie 就是 <c>null</c>；
/// 写路径（新建跟单）上 Api 层会在缺 Cookie 时先给一枚**候选值**——它是否真的签发出去，
/// 由"这次建成了"决定。</param>
public sealed record PublicCaller(long? UserId, string? VisitorSecret);
