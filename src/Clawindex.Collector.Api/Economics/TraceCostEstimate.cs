namespace Clawindex.Collector.Api.Economics;

public sealed record TraceCostEstimate(
    decimal? EstimatedCostUsd,
    long     CostedSpanCount,
    long     UncostedSpanCount,
    double   CostCoverage)
{
    public static TraceCostEstimate Empty(long totalSpanCount) => new(
        EstimatedCostUsd: null,
        CostedSpanCount:  0,
        UncostedSpanCount: totalSpanCount,
        CostCoverage:     0.0);
}
