namespace Clawindex.Collector.Api.Persistence;

public sealed record SpanState(
    string SpanId,
    string TraceId,
    string? ParentSpanId,
    string? AgentId,
    string SpanName,
    string SpanKind,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string? Operation,
    string? Provider,
    string? Model,
    long? InputTokens,
    long? OutputTokens,
    bool IsConformant,
    string AttributesJson,
    DateTimeOffset UpdatedAt);
