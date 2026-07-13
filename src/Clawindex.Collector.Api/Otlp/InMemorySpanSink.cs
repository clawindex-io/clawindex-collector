using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Clawindex.Collector.Api.Otlp;

public sealed class InMemorySpanSink : IValidatedSpanSink
{
    private readonly ConcurrentQueue<ValidatedSpan> _received = new();
    private readonly ILogger<InMemorySpanSink> _logger;

    public InMemorySpanSink(ILogger<InMemorySpanSink> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<ValidatedSpan> Received => _received.ToArray();

    public void Clear() => _received.Clear();

    public Task AcceptAsync(IReadOnlyList<ValidatedSpan> spans, CancellationToken cancellationToken = default)
    {
        foreach (var span in spans)
        {
            _received.Enqueue(span);
        }

        _logger.LogDebug(
            "InMemorySpanSink accepted {Count} span(s) — no persistence until #17b lands.",
            spans.Count);

        return Task.CompletedTask;
    }
}
