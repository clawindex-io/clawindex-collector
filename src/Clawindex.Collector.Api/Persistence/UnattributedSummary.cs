using System.Text.Json.Serialization;

namespace Clawindex.Collector.Api.Persistence;

public sealed record UnattributedSummary(
    [property: JsonPropertyName("count")]         long            Count,
    [property: JsonPropertyName("service_names")] string[]        ServiceNames,
    [property: JsonPropertyName("models")]        string[]        Models,
    [property: JsonPropertyName("earliest_seen")] DateTimeOffset? EarliestSeen,
    [property: JsonPropertyName("latest_seen")]   DateTimeOffset? LatestSeen
);
