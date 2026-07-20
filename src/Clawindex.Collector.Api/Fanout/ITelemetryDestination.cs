namespace Clawindex.Collector.Api.Fanout;

public interface ITelemetryDestination
{
    string Name    { get; }
    bool   Enabled { get; }
    // Returns true on 2xx. Never throws — catches all exceptions internally.
    Task<bool> TryDeliverAsync(byte[] payload, string contentType, CancellationToken ct);
}
