using System.Text.Json.Serialization;

namespace Clawindex.Collector.Api.Persistence;

public sealed record AgentsResponse(
    [property: JsonPropertyName("agents")]       IEnumerable<AgentRollup> Agents,
    [property: JsonPropertyName("unattributed")] UnattributedSummary      Unattributed
);
