using System.Text.Json.Serialization;

namespace Clawindex.Collector.Api.Economics;

internal sealed record PricingEntry(
    [property: JsonPropertyName("provider")]                 string  Provider,
    [property: JsonPropertyName("model")]                    string  Model,
    [property: JsonPropertyName("token_type")]               string  TokenType,
    [property: JsonPropertyName("price_per_million_tokens")] decimal PricePerMillionTokens,
    [property: JsonPropertyName("effective_from")]           DateOnly EffectiveFrom,
    [property: JsonPropertyName("last_confirmed")]           DateOnly LastConfirmed)
{
    public decimal PricePerToken => PricePerMillionTokens / 1_000_000m;
}
