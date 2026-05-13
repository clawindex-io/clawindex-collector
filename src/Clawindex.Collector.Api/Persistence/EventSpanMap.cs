namespace Clawindex.Collector.Api.Persistence;

public sealed record EventSpanMap(
    string EventId,
    string TraceId,
    string SpanId,
    string RelationshipType,
    DateTimeOffset CreatedAt);
