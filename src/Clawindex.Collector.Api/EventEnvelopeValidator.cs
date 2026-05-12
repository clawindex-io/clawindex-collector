using System.Globalization;
using System.Text.Json;

namespace Clawindex.Collector.Api;

public sealed class EventEnvelopeValidator
{
    public EnvelopeValidationResult Validate(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return EnvelopeValidationResult.Invalid("Event must be a JSON object");
        }

        if (!TryGetRequiredString(element, "schema_version", out var schemaVersion))
        {
            return EnvelopeValidationResult.Invalid("Missing required field: schema_version");
        }

        var eventId = TryGetString(element, "event_id", out var suppliedEventId)
            ? suppliedEventId
            : null;

        if (!TryGetRequiredString(element, "event_type", out var eventType))
        {
            return EnvelopeValidationResult.Invalid("Missing required field: event_type");
        }

        if (!TryGetRequiredString(element, "occurred_at", out var occurredAtRaw))
        {
            return EnvelopeValidationResult.Invalid("Missing required field: occurred_at");
        }

        if (!DateTimeOffset.TryParse(
                occurredAtRaw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var occurredAt))
        {
            return EnvelopeValidationResult.Invalid("Invalid field: occurred_at must be an ISO-8601 timestamp");
        }

        if (!element.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object)
        {
            return EnvelopeValidationResult.Invalid("Missing required field: source");
        }

        if (!TryGetRequiredString(source, "system", out var sourceSystem))
        {
            return EnvelopeValidationResult.Invalid("Missing required field: source.system");
        }

        if (!element.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return EnvelopeValidationResult.Invalid("Missing required field: payload");
        }

        var envelope = new EventEnvelope(
            string.IsNullOrWhiteSpace(eventId) ? GenerateEventId() : eventId,
            schemaVersion,
            eventType,
            occurredAt,
            sourceSystem,
            TryGetString(source, "component", out var sourceComponent) ? sourceComponent : null,
            TryGetString(source, "version", out var sourceVersion) ? sourceVersion : null,
            GetCorrelationValue(element, "trace_id"),
            GetCorrelationValue(element, "span_id"),
            GetCorrelationValue(element, "task_id"),
            GetCorrelationValue(element, "agent_id"),
            GetCorrelationValue(element, "session_id"),
            payload.GetRawText());

        return EnvelopeValidationResult.Valid(envelope);
    }

    private static string GenerateEventId() => $"evt_{Guid.NewGuid():N}";

    private static bool TryGetRequiredString(JsonElement element, string propertyName, out string value)
    {
        return TryGetString(element, propertyName, out value) && !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;

        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static string? GetCorrelationValue(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty("correlation", out var correlation) || correlation.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return TryGetString(correlation, propertyName, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }
}

public sealed record EnvelopeValidationResult(bool IsValid, EventEnvelope? Envelope, string ErrorMessage)
{
    public static EnvelopeValidationResult Valid(EventEnvelope envelope) => new(true, envelope, string.Empty);

    public static EnvelopeValidationResult Invalid(string errorMessage) => new(false, null, errorMessage);
}

public sealed record EventEnvelope(
    string EventId,
    string SchemaVersion,
    string EventType,
    DateTimeOffset OccurredAt,
    string SourceSystem,
    string? SourceComponent,
    string? SourceVersion,
    string? TraceId,
    string? SpanId,
    string? TaskId,
    string? AgentId,
    string? SessionId,
    string PayloadJson);
