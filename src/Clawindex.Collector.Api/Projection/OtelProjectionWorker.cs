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
                var events = await repository.GetUnprojectedAsync(options.Value.BatchSize, stoppingToken);
                foreach (var acceptedEvent in events)
                {
                    mapper.Project(acceptedEvent);
                    await repository.MarkProjectedAsync(acceptedEvent.EventId, stoppingToken);
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
