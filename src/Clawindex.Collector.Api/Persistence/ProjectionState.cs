namespace Clawindex.Collector.Api.Persistence;

public sealed record ProjectionState(
    string EventId,
    string ProjectionStatus,
    DateTimeOffset? ProjectedAt,
    DateTimeOffset? ExportedAt,
    int ProjectionAttempts,
    string? ProjectionErrors);
