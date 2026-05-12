namespace Clawindex.Collector.Api;

public sealed record BatchEventResult(
    int Index,
    string Status,
    string? EventId,
    ValidationError? Error)
{
    public static BatchEventResult Accepted(int index, string eventId) =>
        new(index, "accepted", eventId, null);

    public static BatchEventResult Rejected(int index, string message) =>
        new(index, "rejected", null, new ValidationError("validation_failed", message));
}

public sealed record ValidationError(string Code, string Message);
