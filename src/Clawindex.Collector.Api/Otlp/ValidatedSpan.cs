namespace Clawindex.Collector.Api.Otlp;

public sealed record ValidatedSpan(
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    string Name,
    int Kind,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    string? Operation,
    string? Provider,
    string? Model,
    long? InputTokens,
    long? OutputTokens,
    Guid? AgentId,
    bool IsConformant,
    bool IsComplete,
    string OtlpStatus,
    IReadOnlyList<KeyValuePair<string, string>> RawAttributes
);
