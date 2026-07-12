namespace Clawindex.Collector.Api.Economics;

public sealed record AgentCostEstimate(
    decimal?  EstimatedCostUsd,
    decimal?  EstimatedErrorTraceCostUsd,
    long      CostedSpanCount,
    long      UncostedSpanCount,
    double    CostCoverage,
    DateOnly? PricedAsOf,
    bool      PricingStale)
{
    public static AgentCostEstimate Empty(long totalSpanCount) => new(
        EstimatedCostUsd:             null,
        EstimatedErrorTraceCostUsd:   null,
        CostedSpanCount:              0,
        UncostedSpanCount:            totalSpanCount,
        CostCoverage:                 0.0,
        PricedAsOf:                   null,
        PricingStale:                 false);
}
