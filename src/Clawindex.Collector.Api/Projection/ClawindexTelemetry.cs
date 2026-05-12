using System.Diagnostics;

namespace Clawindex.Collector.Api.Projection;

public static class ClawindexTelemetry
{
    public const string ActivitySourceName = "Clawindex.Collector.Projection";
    public const string ServiceName = "clawindex-collector";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "0.1.0");
}
