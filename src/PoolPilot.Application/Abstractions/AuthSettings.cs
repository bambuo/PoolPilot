namespace PoolPilot.Application.Abstractions;

/// <summary>
/// 登录与注册的总开关（配置节 <c>Auth</c>）。
///
/// 关闭后只影响**新的登录/注册流程**：登录、注册、MFA 与首次初始化端点回 404；
/// 公开页和已有的会话不受影响。它是为“只对外提供公开页”的部署准备的，
/// 不是权限系统的一部分——匿名访客本来就不在这套权限模型里。
/// </summary>
public sealed class AuthSettings
{
    /// <summary>
    /// 是否允许登录与注册。默认 <c>true</c>；公开-only 部署设为 <c>false</c>
    /// （环境变量 <c>Auth__AccountsEnabled</c>）。
    /// </summary>
    public bool AccountsEnabled { get; set; } = true;
}
