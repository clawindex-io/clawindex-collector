namespace Clawindex.Collector.Api.Projection;

public sealed class OtelProjectionOptions
{
    public bool Enabled { get; set; } = true;

    public int PollIntervalMilliseconds { get; set; } = 1000;

    public int BatchSize { get; set; } = 100;

    public int MaxAttempts { get; set; } = 3;
}
