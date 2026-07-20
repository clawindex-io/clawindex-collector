namespace Clawindex.Collector.Api.Fanout;

public sealed class ForwardWorker(
    ForwardDispatcher dispatcher,
    IConfiguration configuration,
    ILogger<ForwardWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollSeconds = configuration.GetValue("Clawindex:Forwarding:PollIntervalSeconds", 1);
        var interval = TimeSpan.FromSeconds(Math.Max(1, pollSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await dispatcher.DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Forward worker drain failed; will retry after {Interval}", interval);
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
