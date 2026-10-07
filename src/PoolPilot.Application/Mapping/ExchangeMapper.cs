using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Entities;

namespace PoolPilot.Application.Mapping;

/// <summary>交易所配置的 DTO 映射。凭据字段一律不进 DTO——只带"配没配、配的是哪一把"。</summary>
public static class ExchangeMapper
{
    public static ExchangeConfigDto ToDto(ExchangeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new ExchangeConfigDto
        {
            Id = config.Id,
            Code = ExchangeEnumText.ToText(config.Code),
            Kind = ExchangeEnumText.ToText(config.Kind),
            DisplayName = config.DisplayName,
            IsTestnet = config.IsTestnet,
            MakerFeePercent = config.MakerFeePercent,
            TakerFeePercent = config.TakerFeePercent,
            IsEnabled = config.IsEnabled,
            Notes = config.Notes,
            CreatedAt = config.CreatedAt.Utc,
            HasCredentials = config.HasCredentials,
            ApiKeyHint = config.ApiKeyHint,
            HasPassphrase = config.Passphrase is not null,

            // 默认 false，由用例拿工厂问一遍再覆盖：映射器不认识工厂，
            // 也不该为了让一个字段有值而把"平台能力"的判断塞进映射层
            SupportsSymbolCatalog = false,
        };
    }
}
