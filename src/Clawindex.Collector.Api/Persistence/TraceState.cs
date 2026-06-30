namespace Clawindex.Collector.Api.Persistence;

public sealed record TraceState(
    string TraceId,
    string? RootSpanId,
    string? AgentId,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset UpdatedAt);
