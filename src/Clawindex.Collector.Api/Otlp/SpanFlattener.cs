using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;

namespace Clawindex.Collector.Api.Otlp;

public sealed class SpanFlattener
{
    public IReadOnlyList<FlatSpan> Flatten(ExportTraceServiceRequest request)
    {
        var result = new List<FlatSpan>();

        foreach (var resourceSpans in request.ResourceSpans)
        {
            var resourceAttrs = BuildAttributeDict(resourceSpans.Resource?.Attributes ?? []);

            foreach (var scopeSpans in resourceSpans.ScopeSpans)
            {
                foreach (var span in scopeSpans.Spans)
                {
                    var merged = new Dictionary<string, string>(resourceAttrs, StringComparer.Ordinal);
                    foreach (var kv in BuildAttributeDict(span.Attributes))
                    {
                        merged[kv.Key] = kv.Value;
                    }
                    result.Add(new FlatSpan(span, merged));
                }
            }
        }

        return result;
    }

    private static Dictionary<string, string> BuildAttributeDict(
        IEnumerable<KeyValue> attributes)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in attributes)
        {
            dict[kv.Key] = AnyValueToString(kv.Value);
        }
        return dict;
    }

    private static string AnyValueToString(AnyValue value) => value.ValueCase switch
    {
        AnyValue.ValueOneofCase.StringValue => value.StringValue,
        AnyValue.ValueOneofCase.IntValue => value.IntValue.ToString(),
        AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue.ToString(),
        AnyValue.ValueOneofCase.BoolValue => value.BoolValue ? "true" : "false",
        AnyValue.ValueOneofCase.BytesValue => Convert.ToBase64String(value.BytesValue.ToByteArray()),
        _ => string.Empty
    };
}

public sealed record FlatSpan(
    OpenTelemetry.Proto.Trace.V1.Span Span,
    IReadOnlyDictionary<string, string> Attributes
);

