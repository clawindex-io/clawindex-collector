namespace Clawindex.Collector.Api.Otlp;

public interface IValidatedSpanSink
{
    Task AcceptAsync(IReadOnlyList<ValidatedSpan> spans, CancellationToken cancellationToken = default);
}
