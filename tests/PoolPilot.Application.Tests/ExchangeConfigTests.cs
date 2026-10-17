using System.Text.Json;

using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.Tests;

/// <summary>
/// 交易所配置的用例测试。
///
/// 两条底线在这里被钉住：
/// <list type="number">
///   <item>**明文只存在于请求的内存里**：落库的是密文，接口回的是尾号——
///     连"序列化整个 DTO 再搜明文"这种最笨也最可靠的检查都要过。</item>
///   <item>**半份凭据必须被拒绝**：只填 Key 不填 Secret 会被存成"看起来已配置"，
///     而它调不通任何接口——这种"看起来成功"的失败最难查。</item>
/// </list>
/// </summary>
[TestClass]
public sealed class ExchangeConfigTests
{
    private const string ApiKey = "BINANCE-API-KEY-0001";
    private const string ApiSecret = "binance-secret-9999";

    [TestMethod]
    public async Task Create_WithoutCredentials_IsAllowedAndReportsNothingConfigured()
    {
        // 先登记平台、稍后再贴凭据是正常流程：接口地址与费率本身就有价值
        FakeRepository repo = new();
        CreateExchangeUseCase useCase = new(repo, new FakeCatalogFactory(), Scope());

        ExchangeConfigDto dto = await useCase.ExecuteAsync(new CreateExchangeRequest
        {
            Code = "binance",
            Kind = "cex",
            DisplayName = "币安主账号",
            MakerFeePercent = 0.02m,
            TakerFeePercent = 0.05m,
            RestBaseUrl = "https://fapi.binance.com",
        }, CancellationToken.None);

        Assert.IsFalse(dto.HasCredentials);
        Assert.IsNull(dto.ApiKeyHint);
        Assert.AreEqual("binance", dto.Code);
        Assert.AreEqual(1, repo.Rows.Count);
        Assert.AreEqual(1L, repo.Rows.Single().OwnerUserId, "新配置归创建者所有");
    }

    [TestMethod]
    public async Task List_OnlyReturnsCurrentUsersConfigs_AndAdminSeesAll()
    {
        FakeRepository repo = new();
        repo.Rows.Add(new ExchangeConfig
        {
            Id = 1,
            OwnerUserId = 1,
            Code = ExchangeCode.Binance,
            Kind = ExchangeKind.Cex,
            DisplayName = "我的",
            IsTestnet = false,
            MakerFeePercent = 0m,
            TakerFeePercent = 0m,
            IsEnabled = true,
            CreatedAt = LighterTimestamp.FromMilliseconds(0),
        });
        repo.Rows.Add(new ExchangeConfig
        {
            Id = 2,
            OwnerUserId = 2,
            Code = ExchangeCode.Okx,
            Kind = ExchangeKind.Cex,
            DisplayName = "别人的",
            IsTestnet = false,
            MakerFeePercent = 0m,
            TakerFeePercent = 0m,
            IsEnabled = true,
            CreatedAt = LighterTimestamp.FromMilliseconds(0),
        });

        IReadOnlyList<ExchangeConfigDto> mine = await new ListExchangesUseCase(
            repo, new FakeCatalogFactory(), Scope()).ExecuteAsync(CancellationToken.None);

        Assert.HasCount(1, mine);
        Assert.AreEqual("我的", mine[0].DisplayName);

        IReadOnlyList<ExchangeConfigDto> all = await new ListExchangesUseCase(
            repo, new FakeCatalogFactory(), new FakeUserDataScope { UserId = 1, CanSeeAll = true })
            .ExecuteAsync(CancellationToken.None);

        Assert.HasCount(2, all, "superadmin 保留全局视图");
    }

    [TestMethod]
    public async Task Create_WithCredentials_StoresThemAsGivenAndExposesOnlyTheHint()
    {
        FakeRepository repo = new();
        CreateExchangeUseCase useCase = new(repo, new FakeCatalogFactory(), Scope());

        ExchangeConfigDto dto = await useCase.ExecuteAsync(new CreateExchangeRequest
        {
            Code = "binance",
            Kind = "cex",
            DisplayName = "币安主账号",
            ApiKey = ApiKey,
            ApiSecret = ApiSecret,
        }, CancellationToken.None);

        ExchangeConfig stored = repo.Rows.Single();

        // 断言的不是"安全"，而是"没有被悄悄改过"：存进去的取出来必须一模一样——
        // 将来接实盘时若拿到的是被加工过的值，签名会失败而原因极难定位
        Assert.AreEqual(ApiKey, stored.ApiKey);
        Assert.AreEqual(ApiSecret, stored.ApiSecret);
        Assert.IsTrue(dto.HasCredentials);
        Assert.AreEqual("0001", dto.ApiKeyHint, "存储是明文，但屏幕上只摆尾号");
    }

    [TestMethod]
    public async Task Create_WithCredentials_NeverLeaksPlaintextThroughTheDto()
    {
        // 最笨也最可靠的检查：把 DTO 整个序列化，明文不该出现在里面。
        // 将来谁给 DTO 加了个字段把凭据带出来，这条会立刻失败。
        FakeRepository repo = new();
        CreateExchangeUseCase useCase = new(repo, new FakeCatalogFactory(), Scope());

        ExchangeConfigDto dto = await useCase.ExecuteAsync(new CreateExchangeRequest
        {
            Code = "okx",
            Kind = "cex",
            DisplayName = "OKX",
            ApiKey = ApiKey,
            ApiSecret = ApiSecret,
            Passphrase = "okx-passphrase",
        }, CancellationToken.None);

        string json = JsonSerializer.Serialize(dto);

        Assert.DoesNotContain(ApiKey, json);
        Assert.DoesNotContain(ApiSecret, json);
        Assert.DoesNotContain("okx-passphrase", json);
        Assert.IsTrue(dto.HasPassphrase, "但要如实告诉界面：passphrase 配了");
    }

    [TestMethod]
    public async Task Create_WithOnlyKeyOrOnlySecret_IsRejected()
    {
        FakeRepository repo = new();
        CreateExchangeUseCase useCase = new(repo, new FakeCatalogFactory(), Scope());

        foreach (CreateExchangeRequest request in new[]
        {
            new CreateExchangeRequest { Code = "binance", Kind = "cex", DisplayName = "只给 Key", ApiKey = ApiKey },
            new CreateExchangeRequest { Code = "binance", Kind = "cex", DisplayName = "只给 Secret", ApiSecret = ApiSecret },
        })
        {
            ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
                useCase.ExecuteAsync(request, CancellationToken.None));

            Assert.Contains("一起给", error.Message);
        }

        Assert.IsEmpty(repo.Rows, "被拒绝的请求不该留下任何行");
    }

    [TestMethod]
    public async Task Create_WithBlankCredentials_LeavesTheFieldsNull()
    {
        // 空白（或空串）不该留下一串空字符串：空字符串会让"没配"这件事在库里看不出来
        FakeRepository repo = new();
        CreateExchangeUseCase useCase = new(repo, new FakeCatalogFactory(), Scope());

        await useCase.ExecuteAsync(new CreateExchangeRequest
        {
            Code = "binance",
            Kind = "cex",
            DisplayName = "币安",
            ApiKey = "   ",
            ApiSecret = "",
        }, CancellationToken.None);

        ExchangeConfig stored = repo.Rows.Single();

        Assert.IsNull(stored.ApiKey);
        Assert.IsNull(stored.ApiSecret);
        Assert.IsNull(stored.Passphrase);
    }

    [TestMethod]
    public async Task Update_CredentialFieldsAreTriState()
    {
        // 三态是本用例的核心语义：不传 = 保持、空串 = 清除、有值 = 换新。
        // 少了"清除"这一态，贴错的 key 就永远删不掉。
        FakeRepository repo = new();
        CreateExchangeUseCase create = new(repo, new FakeCatalogFactory(), Scope());
        UpdateExchangeUseCase update = new(repo, new FakeCatalogFactory());

        ExchangeConfigDto created = await create.ExecuteAsync(new CreateExchangeRequest
        {
            Code = "binance",
            Kind = "cex",
            DisplayName = "币安",
            ApiKey = ApiKey,
            ApiSecret = ApiSecret,
        }, CancellationToken.None);

        // ① 不传凭据 → 原样保留
        ExchangeConfigDto untouched = await update.ExecuteAsync(created.Id, new UpdateExchangeRequest
        {
            DisplayName = "币安（改名）",
            IsEnabled = true,
        }, CancellationToken.None);

        Assert.IsTrue(untouched.HasCredentials);
        Assert.AreEqual("0001", untouched.ApiKeyHint);

        // ② 空串 → 清除，且尾号要跟着消失（留着尾号等于宣称还配着旧 key）
        ExchangeConfigDto cleared = await update.ExecuteAsync(created.Id, new UpdateExchangeRequest
        {
            DisplayName = "币安（改名）",
            IsEnabled = true,
            ApiKey = "",
            ApiSecret = "",
        }, CancellationToken.None);

        Assert.IsFalse(cleared.HasCredentials);
        Assert.IsNull(cleared.ApiKeyHint);

        // ③ 给新值 → 换成新的
        ExchangeConfigDto replaced = await update.ExecuteAsync(created.Id, new UpdateExchangeRequest
        {
            DisplayName = "币安（改名）",
            IsEnabled = true,
            ApiKey = "NEW-KEY-7777",
            ApiSecret = "new-secret",
        }, CancellationToken.None);

        Assert.IsTrue(replaced.HasCredentials);
        Assert.AreEqual("7777", replaced.ApiKeyHint);
    }

    [TestMethod]
    public async Task Dto_ReportsWhetherThePlatformHasASymbolCatalog()
    {
        // 界面靠这个标记决定"扫描/登记"按钮可不可点，而不是让人点一下再吃错误。
        // 判据来自工厂而不是猜平台名——将来给别的平台加了目录，这里自动跟着变
        FakeRepository repo = new();
        CreateExchangeUseCase create = new(repo, new FakeCatalogFactory(), Scope());

        ExchangeConfigDto binance = await create.ExecuteAsync(
            new CreateExchangeRequest { Code = "binance", Kind = "cex", DisplayName = "币安" },
            CancellationToken.None);

        ExchangeConfigDto okx = await create.ExecuteAsync(
            new CreateExchangeRequest { Code = "okx", Kind = "cex", DisplayName = "OKX" },
            CancellationToken.None);

        Assert.IsTrue(binance.SupportsSymbolCatalog);
        Assert.IsFalse(okx.SupportsSymbolCatalog, "OKX 还没有目录实现，界面该提前禁用按钮");
    }

    [TestMethod]
    public async Task Update_WithoutEndpoints_PreservesTheStoredOnes()
    {
        // 读取接口**不返回**接口地址（它由平台预设决定、不面向界面），所以更新时前端
        // 也拿不回来。若"不传"被当成"清空"，保存一次改动就会把已存的地址抹掉——
        // 这种数据丢失在界面上完全看不出来，只有将来接实盘时才会以"地址为空"暴露。
        FakeRepository repo = new();
        CreateExchangeUseCase create = new(repo, new FakeCatalogFactory(), Scope());
        UpdateExchangeUseCase update = new(repo, new FakeCatalogFactory());

        ExchangeConfigDto created = await create.ExecuteAsync(new CreateExchangeRequest
        {
            Code = "gate",
            Kind = "cex",
            DisplayName = "Gate",
            RestBaseUrl = "https://api.gateio.ws/api/v4",
            WsUrl = "wss://fx-ws.gateio.ws/v4/ws/usdt",
        }, CancellationToken.None);

        await update.ExecuteAsync(created.Id, new UpdateExchangeRequest
        {
            DisplayName = "Gate（改名）",
            IsEnabled = true,
            MakerFeePercent = 0.015m,
            TakerFeePercent = 0.045m,
        }, CancellationToken.None);

        ExchangeConfig stored = repo.Rows.Single();

        Assert.AreEqual("https://api.gateio.ws/api/v4", stored.RestBaseUrl, "不传地址时必须保持原值");
        Assert.AreEqual("wss://fx-ws.gateio.ws/v4/ws/usdt", stored.WsUrl);
        Assert.AreEqual("Gate（改名）", stored.DisplayName);
        Assert.AreEqual(0.015m, stored.MakerFeePercent, "其余字段照常更新");
    }

    [TestMethod]
    public async Task ReadDto_NeverCarriesEndpoints()
    {
        // 接口地址不该发到前端：界面不显示也不许改，前端拿不到，也就不会有人想着去改它。
        // 把它从 DTO 里整个去掉，比"前端拿到了但不渲染"更彻底。
        FakeRepository repo = new();
        CreateExchangeUseCase create = new(repo, new FakeCatalogFactory(), Scope());

        ExchangeConfigDto dto = await create.ExecuteAsync(new CreateExchangeRequest
        {
            Code = "binance",
            Kind = "cex",
            DisplayName = "币安",
            RestBaseUrl = "https://fapi.binance.com",
            WsUrl = "wss://fstream.binance.com/ws",
        }, CancellationToken.None);

        string json = System.Text.Json.JsonSerializer.Serialize(dto);

        Assert.DoesNotContain("fapi.binance.com", json);
        Assert.DoesNotContain("fstream", json);
        Assert.IsFalse(json.Contains("restBaseUrl", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task Update_WithOnlyOneCredentialField_IsRejected()
    {
        FakeRepository repo = new();
        CreateExchangeUseCase create = new(repo, new FakeCatalogFactory(), Scope());
        UpdateExchangeUseCase update = new(repo, new FakeCatalogFactory());

        ExchangeConfigDto created = await create.ExecuteAsync(new CreateExchangeRequest
        {
            Code = "binance",
            Kind = "cex",
            DisplayName = "币安",
        }, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() => update.ExecuteAsync(created.Id, new UpdateExchangeRequest
        {
            DisplayName = "币安",
            IsEnabled = true,
            ApiKey = "ONLY-KEY-1234",
        }, CancellationToken.None));
    }

    [TestMethod]
    public async Task Create_WithBlankName_IsRejected()
    {
        // 同一个交易所可以配多条，名字是区分它们的唯一依据
        FakeRepository repo = new();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new CreateExchangeUseCase(repo, new FakeCatalogFactory(), Scope()).ExecuteAsync(
                new CreateExchangeRequest { Code = "binance", Kind = "cex", DisplayName = "   " },
                CancellationToken.None));
    }

    [TestMethod]
    public async Task Create_WithUnknownCode_IsRejectedButKnownOnesMapToTheirCode()
    {
        FakeRepository repo = new();
        CreateExchangeUseCase useCase = new(repo, new FakeCatalogFactory(), Scope());

        await Assert.ThrowsAsync<ArgumentException>(() => useCase.ExecuteAsync(
            new CreateExchangeRequest { Code = "不存在的所", Kind = "cex", DisplayName = "X" },
            CancellationToken.None));

        ExchangeConfigDto okx = await useCase.ExecuteAsync(
            new CreateExchangeRequest { Code = "OKX", Kind = "dex", DisplayName = "OKX" },
            CancellationToken.None);

        Assert.AreEqual("okx", okx.Code, "大小写不敏感");
        Assert.AreEqual("dex", okx.Kind);
    }

    // ── fakes ─────────────────────────────────────────────

    private static FakeUserDataScope Scope() => new() { UserId = 1L };

    private sealed class FakeRepository : IExchangeConfigRepository
    {
        public List<ExchangeConfig> Rows { get; } = [];

        public Task<ExchangeConfig?> GetAsync(long id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Rows.FirstOrDefault(r => r.Id == id));

        public Task<IReadOnlyList<ExchangeConfig>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExchangeConfig>>([.. Rows]);

        public Task<ExchangeConfig> CreateAsync(ExchangeConfig config, CancellationToken cancellationToken = default)
        {
            ExchangeConfig stored = config with { Id = Rows.Count + 1 };
            Rows.Add(stored);

            return Task.FromResult(stored);
        }

        public Task UpdateAsync(ExchangeConfig config, CancellationToken cancellationToken = default)
        {
            Rows.RemoveAll(r => r.Id == config.Id);
            Rows.Add(config);

            return Task.CompletedTask;
        }

        public Task DeleteAsync(long id, CancellationToken cancellationToken = default)
        {
            Rows.RemoveAll(r => r.Id == id);

            return Task.CompletedTask;
        }
    }

}

/// <summary>只认币安的目录工厂替身，行为与生产实现一致：别的平台返回 null。</summary>
internal sealed class FakeCatalogFactory : IExchangeSymbolCatalogFactory
{
    public IReadOnlyList<Domain.Enums.ExchangeCode> SupportedPlatforms { get; } =
        [Domain.Enums.ExchangeCode.Binance];

    public IExchangeSymbolCatalog? Create(Domain.Enums.ExchangeCode platform) =>
        platform == Domain.Enums.ExchangeCode.Binance ? new FakeCatalog() : null;

    private sealed class FakeCatalog : IExchangeSymbolCatalog
    {
        public string Code => "binance";

        public string Coverage => "加密永续与传统金融永续（股票、商品、指数）";

        public Task<IReadOnlyList<ExchangeSymbolInfo>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExchangeSymbolInfo>>(
            [
                new ExchangeSymbolInfo("ENAUSDT", "ENA", "USDT", "PERPETUAL", IsTradFi: false, 5m, 1m, 0),
                new ExchangeSymbolInfo("CLUSDT", "CL", "USDT", "TRADIFI_PERPETUAL", IsTradFi: true, 5m, 0.01m, 2),
            ]);

        public async Task<IReadOnlyDictionary<ExchangeSymbolInfo, decimal>> PricesAsync(
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ExchangeSymbolInfo> rows = await ListAsync(cancellationToken).ConfigureAwait(false);

            return rows.ToDictionary(
                row => row,
                row => row.Symbol == "ENAUSDT" ? 0.2447m : 87.9m);
        }
    }
}
