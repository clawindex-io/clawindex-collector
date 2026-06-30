using System.Text.Json;
using Clawindex.Collector.Api.Persistence;

namespace Clawindex.Collector.Api.Otlp;

public sealed class DurableSpanSink(EventRepository repository, ILogger<DurableSpanSink> logger) : IValidatedSpanSink
{
    private static readonly string[] SpanKindNames = ["unset", "internal", "server", "client", "producer", "consumer"];

    public async Task AcceptAsync(IReadOnlyList<ValidatedSpan> spans, CancellationToken cancellationToken = default)
    {
        foreach (var span in spans)
        {
            if (!span.IsComplete)
            {
                logger.LogWarning("Dropping incomplete span {SpanId} in trace {TraceId}", span.SpanId, span.TraceId);
                continue;
            }

            await ProjectSpanAsync(span, cancellationToken);
        }
    }

    private async Task ProjectSpanAsync(ValidatedSpan span, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var spanState = ToSpanState(span, now);
        await repository.UpsertSpanStateAsync(spanState, cancellationToken);

        if (span.ParentSpanId is not null)
        {
            var parent = await repository.GetSpanStateAsync(span.ParentSpanId, cancellationToken);
            if (parent is null)
            {
                var placeholder = MakePlaceholder(span.ParentSpanId, span.TraceId, now);
                await repository.InsertPlaceholderSpanIfAbsentAsync(placeholder, cancellationToken);
            }
        }

        await ProjectTraceAsync(span, now, cancellationToken);
    }

    private async Task ProjectTraceAsync(ValidatedSpan span, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var isRoot = span.ParentSpanId is null;
        var traceState = new TraceState(
            TraceId: span.TraceId,
            RootSpanId: isRoot ? span.SpanId : null,
            AgentId: span.AgentId?.ToString(),
            Status: isRoot ? "finalized" : "open",
            StartedAt: span.StartTime,
            EndedAt: isRoot ? span.EndTime : null,
            UpdatedAt: now);

        await repository.UpsertTraceStateAsync(traceState, cancellationToken);
    }

    private static SpanState ToSpanState(ValidatedSpan span, DateTimeOffset now) => new(
        SpanId: span.SpanId,
        TraceId: span.TraceId,
        ParentSpanId: span.ParentSpanId,
        AgentId: span.AgentId?.ToString(),
        SpanName: span.Name,
        SpanKind: KindToString(span.Kind),
        Status: span.OtlpStatus,
        StartedAt: span.StartTime,
        EndedAt: span.EndTime,
        Operation: span.Operation,
        Provider: span.Provider,
        Model: span.Model,
        InputTokens: span.InputTokens,
        OutputTokens: span.OutputTokens,
        IsConformant: span.IsConformant,
        AttributesJson: BuildAttributesJson(span.RawAttributes),
        UpdatedAt: now);

    private static SpanState MakePlaceholder(string spanId, string traceId, DateTimeOffset now) => new(
        SpanId: spanId,
        TraceId: traceId,
        ParentSpanId: null,
        AgentId: null,
        SpanName: string.Empty,
        SpanKind: "unknown",
        Status: "placeholder",
        StartedAt: now,
        EndedAt: now,
        Operation: null,
        Provider: null,
        Model: null,
        InputTokens: null,
        OutputTokens: null,
        IsConformant: false,
        AttributesJson: "{}",
        UpdatedAt: now);

    private static string KindToString(int kind) =>
        kind >= 0 && kind < SpanKindNames.Length ? SpanKindNames[kind] : "unset";

    private static string BuildAttributesJson(IReadOnlyList<KeyValuePair<string, string>> attributes)
    {
        var dict = new Dictionary<string, string>(attributes.Count, StringComparer.Ordinal);
        foreach (var kv in attributes)
        {
            dict[kv.Key] = kv.Value;
        }

        return JsonSerializer.Serialize(dict);
    }
}
