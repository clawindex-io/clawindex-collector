using System.Text.Json.Serialization;

namespace Clawindex.Collector.Api.Persistence;

public sealed record RecentTrace(
    [property: JsonPropertyName("trace_id")]         string TraceId,
    [property: JsonPropertyName("status")]           string Status,
    [property: JsonPropertyName("started_at")]       DateTimeOffset? StartedAt,
    [property: JsonPropertyName("duration_ms")]      long? DurationMs,
    [property: JsonPropertyName("agent_span_count")] long AgentSpanCount,
    [property: JsonPropertyName("error_count")]      long ErrorCount);
