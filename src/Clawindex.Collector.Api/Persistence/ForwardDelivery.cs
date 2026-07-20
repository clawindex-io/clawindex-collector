namespace Clawindex.Collector.Api.Persistence;

public sealed record ForwardDelivery(
    string QueueItemId,
    string DestinationName,
    byte[] Payload,
    string ContentType);
