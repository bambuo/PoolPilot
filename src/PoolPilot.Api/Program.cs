using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

using PoolPilot.Api.Authorization;
using PoolPilot.Api.Hubs;
using PoolPilot.Api.Middleware;
using PoolPilot.Api.RateLimiting;
using PoolPilot.Api.Streaming;
using PoolPilot.Application.Abstractions;
using PoolPilot.Application.UseCases;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Repositories;
using Lighter.Net.Interfaces.Clients;

using PoolPilot.Infrastructure.Authorization;
using PoolPilot.Infrastructure.Lighter;
using PoolPilot.Infrastructure.Auth;
using PoolPilot.Infrastructure.Brokers;
using PoolPilot.Infrastructure.Monitoring;
using PoolPilot.Infrastructure.Persistence;

namespace PoolPilot.Api;

/// <summary>
/// 组合根。分层依赖只在这里汇合：Api 引用 Application + Infrastructure。
/// 业务逻辑一律不写在本题——Api 层是薄壳（docs/CODING_STANDARDS.md 1.2）。
/// </summary>
public sealed class Program
{
    private Program()
    {
    }

    public static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        builder.Services.AddControllers();
        builder.Services.AddOpenApi();
        builder.Services.AddSignalR();

        // ── 登录与 MFA ──────────────────────────────────────────
        // Cookie 会话（框架自带，零新增包）：HttpOnly 让前端脚本拿不到它，
        // 前端因此没有"把令牌存在哪儿"这个决定要做。
        builder.Services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "poolpilot_session";
                options.Cookie.HttpOnly = true;

                // Lax 而不是 Strict：同源前端的所有请求都带得上它，
                // 而从别处点进来的顶层跳转也仍然可用——Strict 会让后者丢会话。
                options.Cookie.SameSite = SameSiteMode.Lax;

                // 本机 http://localhost，没有 TLS 可谈。改成 Always 会让 Cookie 根本发不出去。
                options.Cookie.SecurePolicy = CookieSecurePolicy.None;

                options.ExpireTimeSpan = TimeSpan.FromDays(7);
                options.SlidingExpiration = true;

                // API 不该把 401 变成 302 跳某个登录页：前端需要的是能判断的状态码，
                // 而不是一个"看起来请求成功了"的重定向后的登录页 HTML。
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return context.Response.WriteAsJsonAsync(new { error = "未登录或会话已过期" });
                };

                options.Events.OnRedirectToAccessDenied = context =>
                {
                    // 带上引导语：403 的绝大多数场景是"这个人还没被分配对应菜单权限"，
                    // 而使用者看到一句"没有权限"通常不知道下一步该找谁。形状仍是 { error }。
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return context.Response.WriteAsJsonAsync(new
                    {
                        error = "没有权限执行该操作；如果这是你的工作所需，"
                            + "请联系管理员在「用户管理」里为你分配对应的菜单权限",
                    });
                };
            });

        // 验证码、登录挑战、失败计数都是"几分钟内有效的临时状态"：
        // 内存缓存够用（单进程、重启即失效反而是想要的），不落库。
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton(TimeProvider.System);

        // ── 交易所对接 ──────────────────────────────────────────
        // 一律走 CryptoClients.Net（AGENTS.md 里「第三方依赖最小化」的唯一例外）。
        // EnabledExchanges 只管**运行时**初始化与聚合路由，依赖树是全的（元包把 30 家带进来）；
        // 这里只开本项目用到的四家——其余 26 家没有理由在本进程里建客户端。
        //
        // 凭据**不在这里配**：同一个平台可以配多条（两个币安账号是常态），
        // 所以客户端按账号取（IBinanceUserClientProvider 等），见 BinanceClients 的说明。
        builder.Services.AddCryptoClients(options => options.EnabledExchanges =
        [
            CryptoClients.Net.Enums.Exchange.Binance,
            CryptoClients.Net.Enums.Exchange.OKX,
            CryptoClients.Net.Enums.Exchange.GateIo,
            CryptoClients.Net.Enums.Exchange.Lighter,
        ]);

        // ── 持久化 ──────────────────────────────────────────────
        // 只存无法重新获取的东西：监控到的事件流与跟单对照记录。
        // 池元数据、净值、快照都能随时从上游重拉，不落库。
        //
        // 数据目录可由 PoolPilot:DataDirectory（环境变量 PoolPilot__DataDirectory）指定；
        // 未配置时保持历史口径 ContentRootPath/../../data。deb 包把它指向 /var/lib/poolpilot，
        // 让服务账号只对数据目录有写权限，安装目录保持只读。
        string dataDirectory = DatabasePath.ResolveDataDirectory(
            builder.Environment.ContentRootPath,
            builder.Configuration[DatabasePath.DataDirectoryConfigurationKey]);
        string dbPath = Path.Combine(dataDirectory, DatabasePath.FileName);

        Directory.CreateDirectory(dataDirectory);

        builder.Services.AddDbContext<PoolPilotDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        builder.Services.AddScoped<IPoolEventRepository, PoolEventRepository>();
        builder.Services.AddScoped<ICopyFillRepository, CopyFillRepository>();
        builder.Services.AddScoped<ILeadTraderRepository, LeadTraderRepository>();
        builder.Services.AddScoped<IExchangeConfigRepository, ExchangeConfigRepository>();
        builder.Services.AddScoped<ISymbolMappingRepository, SymbolMappingRepository>();
        builder.Services.AddScoped<IFollowerRepository, FollowerRepository>();
        builder.Services.AddScoped<IFollowerLedgerRepository, FollowerLedgerRepository>();
        builder.Services.AddScoped<ILiveOrderRepository, LiveOrderRepository>();
        builder.Services.AddScoped<IUserRepository, UserRepository>();

        // 授权的事实源（菜单 / 角色 / 两张绑定表）。它们是 Casbin 策略投影的输入，
        // 不落单独的 casbin_rule 表（见 docs/PERMISSIONS.md）。
        builder.Services.AddScoped<IMenuRepository, MenuRepository>();
        builder.Services.AddScoped<IRoleRepository, RoleRepository>();
        builder.Services.AddScoped<IUserRoleRepository, UserRoleRepository>();
        builder.Services.AddScoped<IMenuRoleRepository, MenuRoleRepository>();

        // ── 授权：Casbin（只出现在 Infrastructure）─────────────
        // Enforcer 是单例（表达"全库此刻的授权状态"，每请求造一个是浪费且会看到不同快照）；
        // 判定与写侧都走应用层的抽象，Controller 不认识 Casbin。
        builder.Services.AddSingleton<CasbinEnforcer>();
        builder.Services.AddScoped<IPolicyWriter, CasbinPolicyWriter>();
        builder.Services.AddScoped<IPermissionEvaluator, CasbinPermissionEvaluator>();

        // "当前是谁"的实现放在表现层（它认识 Cookie 声明），应用层只认 ICurrentUser
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

        // 通知：通道 / 样板 / 记录。口令与交易所凭据同一条约定（明文，见 EXCHANGES.md）
        builder.Services.AddScoped<INotificationProviderRepository, NotificationProviderRepository>();
        builder.Services.AddScoped<INotificationTemplateRepository, NotificationTemplateRepository>();
        builder.Services.AddScoped<INotificationLogRepository, NotificationLogRepository>();

        // ── Lighter 客户端 ───────────────────────────────────────
        // 分两半（依据见 LighterPoolDataExtension 的类说明）：
        //   库覆盖到的（系统配置/账户快照/合约规格）走 CryptoClients.Net；
        //   池枚举、净值序列、池的成交与挂单、盘口这五条库没有，走自研的扩展
        builder.Services.AddSingleton<LighterWebSocketClient>();
        builder.Services.AddSingleton<IMarketStatsSubscription, MarketStatsSubscription>();

        builder.Services.AddHttpClient<LighterPoolDataExtension>(http =>
        {
            http.BaseAddress = new Uri($"{LighterConstants.MainnetRestBaseUrl}/");
            http.Timeout = TimeSpan.FromSeconds(60);
        });

        builder.Services.AddScoped<ILighterPoolsClient>(sp => new LighterPoolsClient(
            sp.GetRequiredService<ILighterUserClientProvider>()
                .GetRestClient("public-data", null, Lighter.Net.LighterEnvironment.Live),
            sp.GetRequiredService<LighterPoolDataExtension>()));

        // ── 用例 ────────────────────────────────────────────────
        builder.Services.AddScoped<GetPoolListUseCase>();
        builder.Services.AddScoped<GetPoolDetailUseCase>();
        builder.Services.AddScoped<EvaluateSizeFeasibilityUseCase>();
        builder.Services.AddScoped<RunBacktestUseCase>();
        builder.Services.AddScoped<GetCopyLedgerUseCase>();

        // 带单员（被观察的池）与跟单员（订阅者）是两组用例：
        // 前者的生命周期与有没有人跟无关，后者来去都不影响前者。
        builder.Services.AddScoped<EnsureLeadTraderUseCase>();
        builder.Services.AddScoped<ListLeadTradersUseCase>();
        builder.Services.AddScoped<ConfigureLeadTraderUseCase>();
        builder.Services.AddScoped<CreateFollowerUseCase>();
        builder.Services.AddScoped<GetFollowerUseCase>();
        builder.Services.AddScoped<ListFollowersUseCase>();
        builder.Services.AddScoped<UpdateFollowerUseCase>();
        builder.Services.AddScoped<DeleteFollowerUseCase>();

        // ── 公开面（/api/public/*）──────────────────────────────
        // 九个 REST 端点 + 一条 SSE 流。装配全部复用上面那几条私有路径（账的算法只有一份），
        // 这里加的是"筛行 + 裁字段 + 归属判据 + 数量上限"这几层公开面自己的规则。
        // 这些端点没有权限门：它们不进菜单表、不挂 Casbin 主体（docs/PERMISSIONS.md）。
        builder.Services.AddScoped<PublicLeadTraderQuota>();
        builder.Services.AddScoped<ListPublicLeadTradersUseCase>();
        builder.Services.AddScoped<GetPublicLeadTraderDetailUseCase>();
        builder.Services.AddScoped<AddPublicLeadTraderUseCase>();
        builder.Services.AddScoped<PublicPoolOptionsUseCase>();
        builder.Services.AddScoped<ListPublicFollowersUseCase>();
        builder.Services.AddScoped<GetPublicFollowerUseCase>();
        builder.Services.AddScoped<ListOwnedPublicFollowerIdsUseCase>();
        builder.Services.AddScoped<CreatePublicFollowerUseCase>();
        builder.Services.AddScoped<DeletePublicFollowerUseCase>();

        // 池名候选的读上游要带缓存（上游按权重限速，匿名流量不能每次穿透；理由见 PoolCatalog 的类说明）
        builder.Services.AddSingleton<IPoolCatalog, PoolCatalog>();

        // "这两页开没开"。与 LiveTradingLimits 同一种注册法：绑成实例再注一次，
        // 应用层因此不依赖任何配置包；代码里的默认值是两条 false（默认不公开）
        builder.Services.AddSingleton(
            builder.Configuration.GetSection("PublicPages").Get<PublicPageSettings>() ?? new PublicPageSettings());

        // 登录/注册总开关：公开-only 部署可以关掉新登录、新注册、MFA 与首次初始化。
        builder.Services.AddSingleton(
            builder.Configuration.GetSection("Auth").Get<AuthSettings>() ?? new AuthSettings());

        // "公开页的调用者是谁"——会话用户 + 访客 Cookie。只有表现层读得懂这两样
        builder.Services.AddScoped<PublicCallerAccessor>();

        // 匿名面上的限流：这里既没有登录失败锁定也没有 Casbin 主体可问（形状见该类说明）
        builder.Services.AddPublicPageRateLimiting();

        // 公开写成功后的后台运行期同步：不挡响应，但也不能完全不通知（已删行会继续落账）
        builder.Services.AddScoped<PublicWriteSync>();

        // ── 公开页实时流（/api/public/stream）────────────────────
        // 单例：订阅者表、在线详情与连接额度都是进程级的。装配器每次开新作用域，
        // 因此一条挂几分钟的长连接不会把某个请求的 DbContext 一直拽住。
        // 连接上限绑配置节 PublicPages:Stream（缺省仍是代码常量），
        // 只为让测试能降到 2 验证 503 分支
        builder.Services.AddSingleton(
            builder.Configuration.GetSection("PublicPages:Stream").Get<PublicStreamSettings>()
                ?? new PublicStreamSettings());
        builder.Services.AddSingleton<IPublicPageFeedAssembler, PublicPageFeedAssembler>();
        builder.Services.AddSingleton<PublicPageFeed>();

        // 纸面复制管线落账后，把同一条 feed 当变更通知器用：公开页立即推帧，不靠定时刷新。
        // 这是同进程内的一条单向通知，不是新的事件总线。
        builder.Services.AddSingleton<IFollowerLedgerActivity>(
            sp => sp.GetRequiredService<PublicPageFeed>());
        builder.Services.AddSingleton<ILeadActivity>(
            sp => sp.GetRequiredService<PublicPageFeed>());
        builder.Services.AddSingleton<IMarketPriceActivity>(
            sp => sp.GetRequiredService<PublicPageFeed>());

        // 交易所配置与凭据。凭据按原样保存（明文）——加密那一套已移除，
        // 理由见 ExchangeConfig：密钥与密文同库等于没防，而"看起来加密了"比明文更误导
        builder.Services.AddScoped<ListExchangesUseCase>();
        builder.Services.AddScoped<CreateExchangeUseCase>();
        builder.Services.AddScoped<UpdateExchangeUseCase>();
        builder.Services.AddScoped<DeleteExchangeUseCase>();

        // ── 注册、登录与 MFA ────────────────────────────────────
        // 三个"临时状态"的存放处都是单例（背后是同一个 IMemoryCache），
        // 因为缓存本身是进程级的：注册成 Scoped 只会让每个请求多包一层壳。
        builder.Services.AddSingleton<IRegistrationCodeStore, MemoryRegistrationCodeStore>();
        builder.Services.AddSingleton<IMfaChallengeStore, MemoryMfaChallengeStore>();
        builder.Services.AddSingleton<ILoginThrottle, MemoryLoginThrottle>();

        // 投递器：用哪条通道由「通知 → 样板」决定（读库），这里的注册与配置无关
        builder.Services.AddSingleton<INotificationMailer, NotificationMailer>();

        builder.Services.AddScoped<RequestRegistrationCodeUseCase>();
        builder.Services.AddScoped<RegisterUserUseCase>();
        builder.Services.AddScoped<LoginUseCase>();
        builder.Services.AddScoped<VerifyMfaLoginUseCase>();
        builder.Services.AddScoped<BindMfaOnLoginUseCase>();
        builder.Services.AddScoped<GetSessionUserUseCase>();

        // ── 菜单与角色（「系统 → 菜单管理」）────────────────────
        builder.Services.AddScoped<GetMenuTreeUseCase>();
        builder.Services.AddScoped<GetAdminMenuTreeUseCase>();
        builder.Services.AddScoped<CreateMenuUseCase>();
        builder.Services.AddScoped<UpdateMenuUseCase>();
        builder.Services.AddScoped<DeleteMenuUseCase>();
        builder.Services.AddScoped<GetMenuRolesUseCase>();
        builder.Services.AddScoped<SetMenuRolesUseCase>();
        builder.Services.AddScoped<ListRolesUseCase>();
        builder.Services.AddScoped<CreateRoleUseCase>();
        builder.Services.AddScoped<GetPermissionsUseCase>();

        // ── 用户管理与角色管理（「系统 → 用户管理 / 角色管理」）──
        builder.Services.AddScoped<ListAdminUsersUseCase>();
        builder.Services.AddScoped<SetUserRolesUseCase>();
        builder.Services.AddScoped<ListAdminRolesUseCase>();
        builder.Services.AddScoped<RenameRoleUseCase>();
        builder.Services.AddScoped<DeleteRoleUseCase>();
        builder.Services.AddScoped<GetRoleMenusUseCase>();
        builder.Services.AddScoped<SetRoleMenusUseCase>();

        // 启动播种：确保超级管理员角色存在、空库播种菜单、补齐老库缺的后加内置菜单、
        // 库里唯一的账号绑上超管（防锁死）
        builder.Services.AddScoped<SeedAuthorizationUseCase>();

        builder.Services.AddScoped<ListNotificationProvidersUseCase>();
        builder.Services.AddScoped<CreateNotificationProviderUseCase>();
        builder.Services.AddScoped<UpdateNotificationProviderUseCase>();
        builder.Services.AddScoped<DeleteNotificationProviderUseCase>();
        builder.Services.AddScoped<SendTestNotificationUseCase>();
        builder.Services.AddScoped<ListNotificationTemplatesUseCase>();
        builder.Services.AddScoped<UpdateNotificationTemplateUseCase>();
        builder.Services.AddScoped<ListNotificationLogsUseCase>();

        // 首次初始化（匿名但只在未初始化时生效，见 SetupController 的说明）
        builder.Services.AddScoped<GetSetupStatusUseCase>();
        builder.Services.AddScoped<InitializeNotificationsUseCase>();
        builder.Services.AddScoped<SendSetupTestNotificationUseCase>();

        // ── 实盘执行侧 ──────────────────────────────────────────
        // 目前只有币安 U 本位合约。上限由配置给出（LiveTrading 节），
        // 直接注册实例而不是 IOptions：应用层零包依赖，绑定的活留给组合根
        // 券商的客户端由库按账号造；这里注册的命名 HttpClient 只服务**唯一那处自研调用**
        // （币安 U 本位合约的 /order/test，库没封装，见 BinanceOrderTestExtension 的说明）
        builder.Services.AddHttpClient(nameof(BinanceOrderTestExtension));
        builder.Services.AddSingleton<IExchangeBrokerFactory, ExchangeBrokerFactory>();
        builder.Services.AddSingleton(
            builder.Configuration.GetSection("LiveTrading").Get<LiveTradingLimits>() ?? new LiveTradingLimits());
        builder.Services.AddScoped<ProbeExchangeUseCase>();
        builder.Services.AddScoped<PlanLiveOrderUseCase>();
        builder.Services.AddScoped<MapPoolMarketsUseCase>();
        builder.Services.AddScoped<LiveTargetResolver>();
        builder.Services.AddScoped<GetLiveOrdersUseCase>();
        builder.Services.AddScoped<GetLiveAccountUseCase>();
        builder.Services.AddScoped<CloseLivePositionUseCase>();

        // 实盘账号的用户数据流：交易所主动推"仓位变了"，界面据此实时刷新。
        // 单例（连接是长生命周期的），按**账号**一条——listenKey 挂在 API key 上，
        // 同一账号下的多个跟单员共用一条流
        builder.Services.AddSingleton<AccountStreamRegistry>();

        // 标的映射表：扫描整理 + 热路径上的本地查询
        builder.Services.AddScoped<ListSymbolMappingsUseCase>();
        builder.Services.AddScoped<ScanSymbolMappingsUseCase>();
        builder.Services.AddScoped<SaveSymbolMappingUseCase>();
        builder.Services.AddScoped<DeleteSymbolMappingUseCase>();

        // 合约目录**按配置造**（工厂），不注册成单例：同一平台的主网与测试网是两套清单，
        // 注入单例会让"选中 OKX/测试网配置"时拿到币安主网的合约
        builder.Services.AddSingleton<IExchangeSymbolCatalogFactory, ExchangeSymbolCatalogFactory>();

        // ── 监控 ────────────────────────────────────────────────
        // 被观察的池由**带单员表**决定（见 LeadTraderRegistry）；配置里的名单只在首次运行时播种，
        // 之后库里说了算。跟单员的存在与否不参与"订阅哪些池"这个决定。
        builder.Services.Configure<LeadTraderOptions>(builder.Configuration.GetSection("LeadTraders"));

        builder.Services.AddSingleton<IMonitorBroadcaster, SignalRMonitorBroadcaster>();
        builder.Services.AddSingleton<MarketDataCache>();
        builder.Services.AddSingleton<MarkPriceProvider>();
        builder.Services.AddSingleton<IMarkPrices>(sp => sp.GetRequiredService<MarkPriceProvider>());
        builder.Services.AddSingleton<IMarkPriceSink>(sp => sp.GetRequiredService<MarkPriceProvider>());
        builder.Services.AddSingleton<LeadTraderRegistry>();
        builder.Services.AddSingleton<ILeadWatchState>(sp => sp.GetRequiredService<LeadTraderRegistry>());

        // 配置写完立刻同步运行期（否则页面会有一段"已启用但未连接"的空窗）
        builder.Services.AddSingleton<ILeadTraderSync>(sp => sp.GetRequiredService<LeadTraderRegistry>());
        builder.Services.AddSingleton<CopySlippageRecorder>();
        builder.Services.AddHostedService<LeadTraderHostedService>();

        WebApplication app = builder.Build();

        // 建库/补表：见 DatabaseSchemaInitializer 的说明。刻意不用迁移工具链
        // （本机 dotnet-ef 需额外的 net8 runtime，见 AGENTS.md 铁律 8），
        // 但也不能只靠 EnsureCreated——它对已存在的库什么都不做，模型新增表永远建不出来。
        using (IServiceScope scope = app.Services.CreateScope())
        {
            PoolPilotDbContext db = scope.ServiceProvider.GetRequiredService<PoolPilotDbContext>();
            ILogger<Program> startupLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            DatabaseSchemaInitializer.EnsureSchemaAsync(db, startupLogger).GetAwaiter().GetResult();

            // 授权播种必须在建表之后：它要写 roles / menus，并最后重建一次 Enforcer。
            SeedAuthorizationUseCase seed = scope.ServiceProvider.GetRequiredService<SeedAuthorizationUseCase>();
            AuthorizationSeedResult seeded = seed.ExecuteAsync().GetAwaiter().GetResult();

            if (seeded.SuperadminRoleCreated)
            {
                startupLogger.LogWarning("已建立系统角色 superadmin（超级管理员）");
            }

            if (seeded.AddedMenuCodes.Count > 0)
            {
                // 内置菜单按 code 缺一补一：第一次建库会全量出现在这里，
                // 之后只有"随新版本加入、这个库还没见过"的那几条（内置菜单不可删，
                // 所以"缺"只可能是库比代码老）。
                startupLogger.LogWarning(
                    "内置菜单缺少 {Count} 条，已补建：{Codes}",
                    seeded.AddedMenuCodes.Count,
                    string.Join("/", seeded.AddedMenuCodes));
            }

            if (seeded.SuperadminBoundToSoleAccount)
            {
                // 防锁死：库里只剩一个账号却没有任何角色时，把它绑成超管，
                // 否则那个账号连「菜单管理」都打不开，系统再也回不到可配置状态
                startupLogger.LogWarning(
                    "库里唯一的账号还没有任何角色，已自动绑定 superadmin（否则无人能进入菜单管理）");
            }
        }

        app.UseMiddleware<ExceptionHandlingMiddleware>();

        // deb 包里 web/dist 会放在 wwwroot：生产环境由 Kestrel 直接托管前端。
        // 静态资源与 API 的授权无关，放在认证之前；开发期没有 wwwroot 时不挂这两个中间件，
        // 避免 Kestrel 每次启动都打一条 "WebRootPath was not found" 的警告。
        if (app.Environment.WebRootPath is { } webRootPath && Directory.Exists(webRootPath))
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
        }

        // 限流排在认证之前：公开页的匿名流量根本不该先去解一遍 Cookie。
        // 顺序对正确性没有影响（拒绝由 OnRejected 自己写响应），只是少做无用功
        app.UseRateLimiter();

        // 顺序不能反：先认出"是谁"（认证），再判断"能不能"（授权）
        app.UseAuthentication();
        app.UseAuthorization();

        if (app.Environment.IsDevelopment())
        {
            // 必须鉴权：这份文档列出了全部接口与它们的形状，匿名可读等于把整套接口清单送出去
            app.MapOpenApi().RequireAuthorization();
        }

        // **一处收口**：控制器默认全部要登录，只有显式标了 AllowAnonymous 的端点例外
        // （目前只有 AuthController 与下面的健康检查）。这样以后新增的控制器是"默认受保护"，
        // 而不是"默认敞开、要靠人记得加属性"。
        app.MapControllers().RequireAuthorization();

        // Hub 也先要登录；"连进来之后能不能收内容"另在 MonitorHub.OnConnectedAsync 里判
        // （menu:graph 的 view，全量广播只能靠拒绝连接挡，见 docs/PERMISSIONS.md 第七节）
        app.MapHub<MonitorHub>("/hubs/monitor").RequireAuthorization();

        // 健康检查：供前端确认后端已就绪。它必须在**登录之前**可访问——
        // 前端的登录页也要能判断"后端起来了没有"。
        app.MapGet("/api/health", () => Results.Ok(new
        {
            status = "ok",
            service = "PoolPilot",
            upstream = LighterConstants.MainnetRestBaseUrl,
        })).AllowAnonymous();

        // SPA 回退：只接客户端路由（/login、/public/leads 等刷新直达）。
        // **不能**用无差别 MapFallbackToFile：那会把拼错的 /api/... 也回成 200 HTML，
        // 让调用方把"接口不存在"误判成"服务正常"。API 与 Hub 的未匹配路径保持 JSON 404。
        app.MapFallback(async context =>
        {
            string? webRoot = app.Environment.WebRootPath;
            string indexPath = Path.Combine(webRoot ?? string.Empty, "index.html");

            // 只有 GET/HEAD 的客户端路由才该拿 index；POST 到不存在的路径仍然是 404。
            bool isClientRouteMethod =
                HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);

            if (!isClientRouteMethod
                || context.Request.Path.StartsWithSegments("/api")
                || context.Request.Path.StartsWithSegments("/hubs")
                || webRoot is null
                || !File.Exists(indexPath))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(new { error = "Not Found" }).ConfigureAwait(false);
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(indexPath).ConfigureAwait(false);
        });

        app.Run();
    }
}
