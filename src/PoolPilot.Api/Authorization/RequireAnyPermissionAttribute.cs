using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

using PoolPilot.Application.Abstractions;

namespace PoolPilot.Api.Authorization;

/// <summary>
/// "或"语义的权限点：<c>[RequireAnyPermission("menu:system-menus", "view", "menu:system-roles", "view")]</c>。
///
/// 参数是**「权限键, 动作」交替排列**的一串（因此长度必须为偶数），命中任意一对即放行；
/// 一对都没命中才 <see cref="ForbidResult"/>——形状与 <see cref="RequirePermissionAttribute"/> 一致，
/// 403 也走同一段 <c>OnRedirectToAccessDenied</c>。
///
/// **为什么需要它**：有些端点服务的是"两个页面都要读的同一份数据"，而授权粒度是"菜单 + 动作"。
/// 目前只有一处：<c>GET /api/admin/menus</c> 既服务「菜单管理」页（<c>menu:system-menus</c>），
/// 也服务「角色管理」页的"配置菜单"弹窗（<c>menu:system-roles</c>）。若只挂前者，
/// 只持有角色管理权限的账号点得开页面、却打不开那个弹窗；只挂后者则反过来。
/// 与其给这个端点造一个"查看菜单管理"的复合权限点（那会多出一个人人都要维护的中间概念），
/// 不如就地表达"这两者之一即可"。
///
/// **刻意不做成"任意动作任意键"**：动作必须逐个与键配对写出，判定的粒度才和
/// <see cref="RequirePermissionAttribute"/> 完全同源（都是"某键上的某动作"），
/// 不会出现"给了 view 却顺带放行 edit"这种隐藏放大。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequireAnyPermissionAttribute : Attribute, IAuthorizationFilter
{
    private readonly (string PermissionKey, string Action)[] _requirements;

    public RequireAnyPermissionAttribute(params string[] permissionKeyActionPairs)
    {
        ArgumentNullException.ThrowIfNull(permissionKeyActionPairs);

        if (permissionKeyActionPairs.Length == 0 || permissionKeyActionPairs.Length % 2 != 0)
        {
            throw new ArgumentException(
                "参数必须是成对的「权限键, 动作」，且至少一对。",
                nameof(permissionKeyActionPairs));
        }

        _requirements = new (string, string)[permissionKeyActionPairs.Length / 2];

        for (int index = 0; index < _requirements.Length; index++)
        {
            string permissionKey = permissionKeyActionPairs[index * 2];
            string action = permissionKeyActionPairs[(index * 2) + 1];

            ArgumentException.ThrowIfNullOrWhiteSpace(permissionKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(action);

            _requirements[index] = (permissionKey, action);
        }
    }

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IPermissionEvaluator evaluator = context.HttpContext.RequestServices
            .GetRequiredService<IPermissionEvaluator>();

        foreach ((string permissionKey, string action) in _requirements)
        {
            if (evaluator.Has(permissionKey, action))
            {
                return;
            }
        }

        context.Result = new ForbidResult(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
