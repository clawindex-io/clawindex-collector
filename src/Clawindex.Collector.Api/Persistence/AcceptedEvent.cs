namespace Clawindex.Collector.Api.Persistence;

public sealed record AcceptedEvent(
    string EventId,
    string SchemaVersion,
    string EventType,
    DateTimeOffset OccurredAt,
    DateTimeOffset ReceivedAt,
    string SourceSystem,
    string? SourceComponent,
    string? SourceVersion,
    string? TraceId,
    string? SpanId,
    string? TaskId,
    string? AgentId,
    string? SessionId,
    string RawJson,
    string PayloadJson)
{
    public static AcceptedEvent From(EventEnvelope envelope, string rawJson, DateTimeOffset receivedAt) => new(
        envelope.EventId,
        envelope.SchemaVersion,
        envelope.EventType,
        envelope.OccurredAt,
        receivedAt.ToUniversalTime(),
        envelope.SourceSystem,
        envelope.SourceComponent,
        envelope.SourceVersion,
        envelope.TraceId,
        envelope.SpanId,
        envelope.TaskId,
        envelope.AgentId,
        envelope.SessionId,
        rawJson,
        envelope.PayloadJson);
}
