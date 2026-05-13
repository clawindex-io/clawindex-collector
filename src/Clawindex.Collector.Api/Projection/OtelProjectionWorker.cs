using Clawindex.Collector.Api.Persistence;
using Microsoft.Extensions.Options;

namespace Clawindex.Collector.Api.Projection;

public sealed class OtelProjectionWorker(
    EventRepository repository,
    OtelEventMapper mapper,
    IOptions<OtelProjectionOptions> options,
    ILogger<OtelProjectionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("OTel projection worker is disabled.");
            return;
        }

        logger.LogInformation("OTel projection worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var events = await repository.GetUnprojectedAsync(
                    options.Value.BatchSize,
                    options.Value.MaxAttempts,
                    stoppingToken);
                foreach (var acceptedEvent in events)
                {
                    await repository.MarkProjectionAttemptAsync(acceptedEvent.EventId, stoppingToken);
                    var result = mapper.Project(acceptedEvent);
                    if (result.Succeeded)
                    {
                        await repository.MarkProjectedAsync(acceptedEvent.EventId, stoppingToken);
                        logger.LogDebug(
                            "Projected event {EventId} of type {EventType} for trace {TraceId}",
                            acceptedEvent.EventId,
                            acceptedEvent.EventType,
                            acceptedEvent.TraceId);
                        continue;
                    }

                    await repository.MarkProjectionFailedAsync(
                        acceptedEvent.EventId,
                        result.Error ?? "Projection failed.",
                        stoppingToken);
                    logger.LogWarning(
                        "Projection failed for event {EventId} of type {EventType} for trace {TraceId}: {ProjectionError}",
                        acceptedEvent.EventId,
                        acceptedEvent.EventType,
                        acceptedEvent.TraceId,
                        result.Error);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "OTel projection worker failed while processing persisted events.");
            }

            await Task.Delay(options.Value.PollIntervalMilliseconds, stoppingToken);
        }
    }
}
