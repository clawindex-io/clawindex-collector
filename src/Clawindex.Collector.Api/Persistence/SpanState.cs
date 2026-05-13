namespace Clawindex.Collector.Api.Persistence;

public sealed record SpanState(
    string SpanId,
    string TraceId,
    string? ParentSpanId,
    string? TaskId,
    string? AgentId,
    string SpanName,
    string SpanKind,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string SourceStartEventId,
    string? SourceEndEventId,
    string AttributesJson,
    DateTimeOffset UpdatedAt);
