using System.Globalization;
using System.Security.Claims;

using PoolPilot.Application.Abstractions;

namespace PoolPilot.Api.Authorization;

/// <summary>
/// 从会话 Cookie 的声明里读出"当前是谁"。
///
/// 放在表现层而不是基础设施：只有这一层认识 <c>HttpContext</c> 与 Cookie 声明。
/// 应用层与基础设施只通过 <see cref="ICurrentUser"/> 问"主体是谁"，不知道它是怎么来的。
/// </summary>
public sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public long? UserId
    {
        get
        {
            string? raw = accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);

            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) ? id : null;
        }
    }
}
