namespace Clawindex.Collector.Api.Projection;

public sealed record ProjectionResult(bool Succeeded, string? Error = null)
{
    public static ProjectionResult Success() => new(true);

    public static ProjectionResult Failure(string error) => new(false, error);
}
