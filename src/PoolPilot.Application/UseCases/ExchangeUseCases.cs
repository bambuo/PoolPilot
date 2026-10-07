using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.Mapping;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.UseCases;

/// <summary>列出全部交易所配置。凭据只以尾号出现在结果里，明文与密文都不出口。</summary>
public sealed class ListExchangesUseCase(
    IExchangeConfigRepository exchanges,
    IExchangeSymbolCatalogFactory catalogs)
{
    public async Task<IReadOnlyList<ExchangeConfigDto>> ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ExchangeConfig> rows = await exchanges
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);

        // 顺带把"这个平台有没有合约目录"问一遍：界面靠它决定按钮可不可点。
        // 问工厂而不是猜平台名——将来给别的平台加了目录，这里自动跟着变
        return
        [
            .. rows
                .Select(ExchangeMapper.ToDto)
                .Zip(rows, (dto, row) => dto with { SupportsSymbolCatalog = catalogs.Create(row.Code) is not null }),
        ];
    }
}

/// <summary>
/// 新建交易所配置。
///
/// 凭据按原样落库（明文，见 <see cref="ExchangeConfig"/> 的说明），
/// 但**读取路径只回尾号**：那是减少屏幕泄露，与存储方式无关。
/// </summary>
public sealed class CreateExchangeUseCase(
    IExchangeConfigRepository exchanges,
    IExchangeSymbolCatalogFactory catalogs)
{
    public async Task<ExchangeConfigDto> ExecuteAsync(
        CreateExchangeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string name = (request.DisplayName ?? string.Empty).Trim();

        if (name.Length == 0)
        {
            throw new ArgumentException("名称不能为空——同一个交易所可以配多条，名字是区分它们的唯一依据。", nameof(request));
        }

        ExchangeCredentials.EnsurePair(request.ApiKey, request.ApiSecret);

        ExchangeConfig created = await exchanges.CreateAsync(
            new ExchangeConfig
            {
                Code = ExchangeEnumText.ParseCode(request.Code),
                Kind = ExchangeEnumText.ParseKind(request.Kind),
                DisplayName = name,
                IsTestnet = request.IsTestnet,
                RestBaseUrl = Blank(request.RestBaseUrl),
                WsUrl = Blank(request.WsUrl),
                MakerFeePercent = request.MakerFeePercent,
                TakerFeePercent = request.TakerFeePercent,
                IsEnabled = true,
                Notes = Blank(request.Notes),
                CreatedAt = LighterTimestamp.FromMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                ApiKey = ExchangeCredentials.Apply(request.ApiKey, null),
                ApiSecret = ExchangeCredentials.Apply(request.ApiSecret, null),
                Passphrase = ExchangeCredentials.Apply(request.Passphrase, null),
                ApiKeyHint = ExchangeConfig.HintOf(request.ApiKey),
            },
            cancellationToken).ConfigureAwait(false);

        return ExchangeMapper.ToDto(created)
            with { SupportsSymbolCatalog = catalogs.Create(created.Code) is not null };
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// 修改交易所配置：名称、环境、接口地址、费率、启停、备注，以及凭据的替换或清除。
///
/// 凭据字段是**三态**（见 <see cref="UpdateExchangeRequest"/>）：没动 / 清除 / 换成新的。
/// </summary>
public sealed class UpdateExchangeUseCase(
    IExchangeConfigRepository exchanges,
    IExchangeSymbolCatalogFactory catalogs)
{
    public async Task<ExchangeConfigDto> ExecuteAsync(
        long id,
        UpdateExchangeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ExchangeConfig existing = await exchanges.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"交易所配置 {id} 不存在。", nameof(id));

        string name = (request.DisplayName ?? string.Empty).Trim();

        if (name.Length == 0)
        {
            throw new ArgumentException("名称不能为空——同一个交易所可以配多条，名字是区分它们的唯一依据。", nameof(request));
        }

        // 只在新值确实要给的时候校验成对性：保持原样（null）时原值本来就是成对的
        if (request.ApiKey is not null || request.ApiSecret is not null)
        {
            ExchangeCredentials.EnsurePair(request.ApiKey, request.ApiSecret);
        }

        string? apiKey = ExchangeCredentials.Apply(request.ApiKey, existing.ApiKey);

        ExchangeConfig updated = existing with
        {
            DisplayName = name,
            IsTestnet = request.IsTestnet,

            // 地址的两态与凭据的三态不同：**不传就保持原值，没有"清除"**。
            // 理由是它不面向使用者——界面上不显示也不许改，值来自平台预设；
            // 而读取接口不返回它，是"不要把它发到前端"的必然结果，
            // 于是更新时也拿不回来，只能由这里保证不被覆盖掉。
            RestBaseUrl = request.RestBaseUrl is null ? existing.RestBaseUrl : Blank(request.RestBaseUrl),
            WsUrl = request.WsUrl is null ? existing.WsUrl : Blank(request.WsUrl),
            MakerFeePercent = request.MakerFeePercent,
            TakerFeePercent = request.TakerFeePercent,
            IsEnabled = request.IsEnabled,
            Notes = Blank(request.Notes),
            ApiKey = apiKey,
            ApiSecret = ExchangeCredentials.Apply(request.ApiSecret, existing.ApiSecret),
            Passphrase = ExchangeCredentials.Apply(request.Passphrase, existing.Passphrase),

            // 尾号跟着 key 走：清了 key 就不该再显示上一次的尾号
            ApiKeyHint = request.ApiKey is null
                ? existing.ApiKeyHint
                : ExchangeConfig.HintOf(string.IsNullOrWhiteSpace(request.ApiKey) ? null : request.ApiKey),
        };

        await exchanges.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);

        return ExchangeMapper.ToDto(updated)
            with { SupportsSymbolCatalog = catalogs.Create(updated.Code) is not null };
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>删除交易所配置（连同它的凭据密文）。</summary>
public sealed class DeleteExchangeUseCase(IExchangeConfigRepository exchanges)
{
    public async Task ExecuteAsync(long id, CancellationToken cancellationToken = default)
    {
        await exchanges.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
    }
}
