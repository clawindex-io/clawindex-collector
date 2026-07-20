using System.Net.Http.Headers;

namespace Clawindex.Collector.Api.Fanout;

public sealed class OtlpHttpDestination : ITelemetryDestination
{
    private readonly HttpClient _client;
    private readonly string _endpoint;

    public string Name    { get; }
    public bool   Enabled { get; }

    public OtlpHttpDestination(DestinationConfig config, HttpClient client)
    {
        Name      = config.Name;
        Enabled   = config.Enabled;
        _endpoint = config.Endpoint;
        _client   = client;
    }

    public async Task<bool> TryDeliverAsync(byte[] payload, string contentType, CancellationToken ct)
    {
        try
        {
            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            using var response = await _client.PostAsync(_endpoint, content, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
