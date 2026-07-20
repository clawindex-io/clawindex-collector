using Clawindex.Collector.Api.Persistence;

namespace Clawindex.Collector.Api.Fanout;

// Thin enqueue service — the only call site is the /v1/traces handler.
// Never throws. Fast path: if no enabled destinations, returns without touching SQLite.
public sealed class ForwardQueue(
    EventRepository repository,
    IReadOnlyList<ITelemetryDestination> destinations,
    ILogger<ForwardQueue> logger)
{
    private readonly IReadOnlyList<string> _names =
        destinations.Where(d => d.Enabled).Select(d => d.Name).ToList();

    public async Task TryEnqueueAsync(byte[] payload, string contentType, CancellationToken ct)
    {
        if (_names.Count == 0) return;
        try
        {
            await repository.EnqueueForwardAsync(payload, contentType, _names, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Forward enqueue failed; payload lost for this request");
        }
    }
}
