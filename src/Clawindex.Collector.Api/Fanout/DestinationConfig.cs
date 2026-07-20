namespace Clawindex.Collector.Api.Fanout;

public sealed class DestinationConfig
{
    public string Name     { get; set; } = string.Empty;
    public string Type     { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public bool   Enabled  { get; set; } = true;
    // Header values are auth secrets — never log them.
    public Dictionary<string, string> Headers { get; set; } = [];
}
