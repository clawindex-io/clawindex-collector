namespace Clawindex.Collector.Api.Economics;

public sealed record AgentTokenAggregate(
    string  AgentId,
    string? Provider,
    string? Model,
    long    InputTokens,
    long    OutputTokens,
    long    SpanCount,
    long    TokenBearingSpanCount,
    long    ErrorTraceInputTokens,
    long    ErrorTraceOutputTokens,
    long    ErrorTraceTokenBearingSpanCount);
