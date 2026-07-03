using System.Text.Json.Serialization;

namespace Clawindex.Collector.Api.Persistence;

public sealed record AgentRollup(
    [property: JsonPropertyName("agent_id")]         string AgentId,
    [property: JsonPropertyName("span_count")]        long SpanCount,
    [property: JsonPropertyName("trace_count")]       long TraceCount,
    [property: JsonPropertyName("error_count")]       long ErrorCount,
    [property: JsonPropertyName("error_rate")]        double ErrorRate,
    [property: JsonPropertyName("last_seen")]         DateTimeOffset LastSeen,
    [property: JsonPropertyName("conformance_ratio")] double ConformanceRatio);
