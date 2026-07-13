namespace Clawindex.Collector.Api.Otlp;

public sealed class SemConvConformanceValidator
{
    private static readonly HashSet<string> AgentIdDenylist = new(StringComparer.OrdinalIgnoreCase)
    {
        "agent", "test", "null", "none", "unknown", "id", "default",
        "00000000-0000-0000-0000-000000000000",
        "00000000-0000-0000-0000-000000000001"
    };

    public ValidationResult Validate(FlatSpan flatSpan)
    {
        var span = flatSpan.Span;
        var attrs = flatSpan.Attributes;

        // Tier 1 — envelope-valid
        if (span.TraceId.IsEmpty || span.TraceId.Length != 16 ||
            span.SpanId.IsEmpty || span.SpanId.Length != 8 ||
            string.IsNullOrEmpty(span.Name) ||
            span.StartTimeUnixNano == 0)
        {
            return ValidationResult.EnvelopeInvalid(
                "Span is missing required identity fields (trace_id, span_id, name, or start_time).");
        }

        var rawAttributes = attrs
            .Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value))
            .ToList();

        // Tier 2 — conformance-complete
        // Provider: tolerant — gen_ai.provider.name (current, preferred) or gen_ai.system (prior)
        var provider = GetNonEmpty(attrs, "gen_ai.provider.name") ?? GetNonEmpty(attrs, "gen_ai.system");
        var operation = GetNonEmpty(attrs, "gen_ai.operation.name");
        var model = GetNonEmpty(attrs, "gen_ai.request.model");

        // Tokens: tolerant — integer attribute or string-encoded integer (both converted to string by flattener)
        var inputTokens = ParseTokens(attrs, "gen_ai.usage.input_tokens");
        var outputTokens = ParseTokens(attrs, "gen_ai.usage.output_tokens");

        // Agent ID: clawindex.agent.id is a ClawIndex-owned key for stable logical agent identity.
        // Value rules: non-empty after trim, length >= 8, not in the placeholder denylist.
        // A GUID is a valid value; GUID format is not required.
        string? agentId = null;
        var agentIdRaw = GetNonEmpty(attrs, "clawindex.agent.id");
        if (agentIdRaw != null)
        {
            var trimmed = agentIdRaw.Trim();
            if (trimmed.Length >= 8 && !AgentIdDenylist.Contains(trimmed))
                agentId = trimmed;
        }

        var isConformant = provider != null
            && operation != null
            && model != null
            && inputTokens.HasValue
            && outputTokens.HasValue
            && agentId != null;

        var startTime = NanosToOffset(span.StartTimeUnixNano);
        var isComplete = span.EndTimeUnixNano > 0;
        var endTime = isComplete ? NanosToOffset(span.EndTimeUnixNano) : startTime;
        var otlpStatus = span.Status?.Code switch
        {
            OpenTelemetry.Proto.Trace.V1.Status.Types.StatusCode.Ok => "ok",
            OpenTelemetry.Proto.Trace.V1.Status.Types.StatusCode.Error => "error",
            _ => "unset"
        };

        var validated = new ValidatedSpan(
            TraceId: Convert.ToHexString(span.TraceId.ToByteArray()).ToLowerInvariant(),
            SpanId: Convert.ToHexString(span.SpanId.ToByteArray()).ToLowerInvariant(),
            ParentSpanId: span.ParentSpanId.IsEmpty
                ? null
                : Convert.ToHexString(span.ParentSpanId.ToByteArray()).ToLowerInvariant(),
            Name: span.Name,
            Kind: (int)span.Kind,
            StartTime: startTime,
            EndTime: endTime,
            Operation: operation,
            Provider: provider,
            Model: model,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            AgentId: agentId,
            IsConformant: isConformant,
            IsComplete: isComplete,
            OtlpStatus: otlpStatus,
            RawAttributes: rawAttributes
        );

        return ValidationResult.Valid(validated);
    }

    private static string? GetNonEmpty(IReadOnlyDictionary<string, string> attrs, string key) =>
        attrs.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    private static long? ParseTokens(IReadOnlyDictionary<string, string> attrs, string key)
    {
        if (!attrs.TryGetValue(key, out var str) || string.IsNullOrEmpty(str))
            return null;

        return long.TryParse(str, out var n) && n >= 0 ? n : null;
    }

    private static DateTimeOffset NanosToOffset(ulong nanos) =>
        DateTimeOffset.UnixEpoch.AddTicks((long)(nanos / 100));
}

public sealed record ValidationResult
{
    public bool IsEnvelopeValid { get; private init; }
    public string? EnvelopeError { get; private init; }
    public ValidatedSpan? Span { get; private init; }

    public static ValidationResult Valid(ValidatedSpan span) =>
        new() { IsEnvelopeValid = true, Span = span };

    public static ValidationResult EnvelopeInvalid(string error) =>
        new() { IsEnvelopeValid = false, EnvelopeError = error };
}
