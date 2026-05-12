using System.Text.Json.Serialization;

namespace Clawindex.Collector.Api;

public sealed record BatchEventResult(
    [property: JsonPropertyName("index")]
    int Index,
    [property: JsonPropertyName("status")]
    string Status,
    [property: JsonPropertyName("event_id")]
    string? EventId,
    [property: JsonPropertyName("error")]
    ValidationError? Error)
{
    public static BatchEventResult Accepted(int index, string eventId) =>
        new(index, "accepted", eventId, null);

    public static BatchEventResult Rejected(int index, string message) =>
        new(index, "rejected", null, new ValidationError("validation_failed", message));
}

public sealed record ValidationError(
    [property: JsonPropertyName("code")]
    string Code,
    [property: JsonPropertyName("message")]
    string Message);
