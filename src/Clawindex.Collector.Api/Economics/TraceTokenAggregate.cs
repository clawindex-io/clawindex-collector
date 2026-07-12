namespace Clawindex.Collector.Api.Economics;

public sealed record TraceTokenAggregate(
    string  TraceId,
    string? Provider,
    string? Model,
    long    InputTokens,
    long    OutputTokens,
    long    TokenBearingSpanCount);
