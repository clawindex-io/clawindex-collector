using Clawindex.Collector.Api.Persistence;

namespace Clawindex.Collector.Api.Fanout;

// Drain logic — injected separately from ForwardWorker so tests can call DrainAsync directly.
public sealed class ForwardDispatcher(
    EventRepository repository,
    IReadOnlyList<ITelemetryDestination> destinations,
    ILogger<ForwardDispatcher> logger)
{
    public async Task DrainAsync(CancellationToken ct = default)
    {
        var pending = await repository.GetPendingForwardDeliveriesAsync(limit: 100, ct);
        foreach (var item in pending)
        {
            var dest = destinations.FirstOrDefault(d => d.Name == item.DestinationName && d.Enabled);
            if (dest is null) continue;

            await repository.MarkForwardAttemptAsync(item.QueueItemId, item.DestinationName, ct);
            var ok = await dest.TryDeliverAsync(item.Payload, item.ContentType, ct);
            if (ok)
            {
                await repository.MarkForwardDeliveredAsync(item.QueueItemId, item.DestinationName, ct);
            }
            else
            {
                logger.LogWarning("Delivery failed for queue item {QueueItemId} to {Destination}",
                    item.QueueItemId, item.DestinationName);
                await repository.MarkForwardFailedAsync(item.QueueItemId, item.DestinationName, "Delivery failed", ct);
            }
        }
    }
}
