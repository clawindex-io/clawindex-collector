using System.Text.Json.Serialization;

namespace Clawindex.Collector.Api.Persistence;

public sealed record AgentRollup(
    [property: JsonPropertyName("agent_id")]         string         AgentId,
    [property: JsonPropertyName("span_count")]        long           SpanCount,
    [property: JsonPropertyName("trace_count")]       long           TraceCount,
    [property: JsonPropertyName("error_count")]       long           ErrorCount,
    [property: JsonPropertyName("error_rate")]        double         ErrorRate,
    [property: JsonPropertyName("last_seen")]         DateTimeOffset LastSeen,
    [property: JsonPropertyName("conformance_ratio")] double         ConformanceRatio)
{
    // Economics — populated at read time in the handler; never written to span_state.
    // All figures are estimates. estimated_cost_usd is null when no span in the window
    // carried token data with a resolvable price — never reported as zero in that case.

    [JsonPropertyName("estimated_cost_usd")]
    public decimal? EstimatedCostUsd { get; init; }

    [JsonPropertyName("estimated_error_trace_cost_usd")]
    public decimal? EstimatedErrorTraceCostUsd { get; init; }

    [JsonPropertyName("costed_span_count")]
    public long CostedSpanCount { get; init; }

    [JsonPropertyName("uncosted_span_count")]
    public long UncostedSpanCount { get; init; }

    [JsonPropertyName("cost_coverage")]
    public double CostCoverage { get; init; }

    [JsonPropertyName("priced_as_of")]
    public DateOnly? PricedAsOf { get; init; }

    [JsonPropertyName("pricing_stale")]
    public bool PricingStale { get; init; }
}
