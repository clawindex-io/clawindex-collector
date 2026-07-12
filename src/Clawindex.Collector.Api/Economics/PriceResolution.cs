namespace Clawindex.Collector.Api.Economics;

public sealed record PriceResolution(
    decimal  InputPricePerToken,
    decimal  OutputPricePerToken,
    DateOnly PricedAsOf,
    bool     IsStale);
