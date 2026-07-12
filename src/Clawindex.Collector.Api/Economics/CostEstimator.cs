namespace Clawindex.Collector.Api.Economics;

public sealed class CostEstimator(PricingTable pricingTable)
{
    public AgentCostEstimate EstimateAgent(
        IEnumerable<AgentTokenAggregate> groups,
        long totalSpanCount,
        DateTimeOffset windowTimestamp)
    {
        decimal totalCost          = 0m;
        decimal errorTraceCost     = 0m;
        long    costedSpanCount    = 0;
        long    errorTraceCosted   = 0;
        DateOnly? pricedAsOf       = null;
        bool    isStale            = false;

        foreach (var group in groups)
        {
            var resolution = pricingTable.TryResolvePrice(group.Provider, group.Model, windowTimestamp);
            if (resolution is null)
                continue;

            var groupCost = group.InputTokens * resolution.InputPricePerToken
                          + group.OutputTokens * resolution.OutputPricePerToken;

            var errorGroupCost = group.ErrorTraceInputTokens  * resolution.InputPricePerToken
                               + group.ErrorTraceOutputTokens * resolution.OutputPricePerToken;

            totalCost       += groupCost;
            errorTraceCost  += errorGroupCost;
            costedSpanCount += group.TokenBearingSpanCount;
            errorTraceCosted += group.ErrorTraceTokenBearingSpanCount;

            pricedAsOf = pricedAsOf is null
                ? resolution.PricedAsOf
                : (resolution.PricedAsOf < pricedAsOf ? resolution.PricedAsOf : pricedAsOf);

            isStale |= resolution.IsStale;
        }

        var uncostedSpanCount = totalSpanCount - costedSpanCount;
        var coverage = totalSpanCount > 0 ? costedSpanCount / (double)totalSpanCount : 0.0;

        return new AgentCostEstimate(
            EstimatedCostUsd:           costedSpanCount > 0 ? totalCost : null,
            EstimatedErrorTraceCostUsd: errorTraceCosted > 0 ? errorTraceCost : null,
            CostedSpanCount:            costedSpanCount,
            UncostedSpanCount:          uncostedSpanCount,
            CostCoverage:               coverage,
            PricedAsOf:                 pricedAsOf,
            PricingStale:               isStale);
    }

    public TraceCostEstimate EstimateTrace(
        IEnumerable<TraceTokenAggregate> groups,
        long traceSpanCount,
        DateTimeOffset windowTimestamp)
    {
        decimal totalCost       = 0m;
        long    costedSpanCount = 0;

        foreach (var group in groups)
        {
            var resolution = pricingTable.TryResolvePrice(group.Provider, group.Model, windowTimestamp);
            if (resolution is null)
                continue;

            totalCost       += group.InputTokens  * resolution.InputPricePerToken
                             +  group.OutputTokens * resolution.OutputPricePerToken;
            costedSpanCount += group.TokenBearingSpanCount;
        }

        var uncostedSpanCount = traceSpanCount - costedSpanCount;
        var coverage = traceSpanCount > 0 ? costedSpanCount / (double)traceSpanCount : 0.0;

        return new TraceCostEstimate(
            EstimatedCostUsd:  costedSpanCount > 0 ? totalCost : null,
            CostedSpanCount:   costedSpanCount,
            UncostedSpanCount: uncostedSpanCount,
            CostCoverage:      coverage);
    }
}
