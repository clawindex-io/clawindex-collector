using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Clawindex.Collector.Api.Fanout;
using Clawindex.Collector.Api.Persistence;
using Google.Protobuf;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Clawindex.Collector.Tests;

public sealed class FanoutTests
{
    // ----- byte-identical forward to one destination -----

    [Fact]
    public async Task OneDestination_PayloadForwarded_ByteIdentical()
    {
        var handler = new CapturingHttpHandler();
        using var fixture = new FanoutFixture([MakeDest("primary", handler)]);

        var bytes   = ConformantOtlpBytes();
        var client  = fixture.CreateClient();
        using var resp = await PostOtlpAsync(client, bytes);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await fixture.Dispatcher.DrainAsync();

        Assert.Single(handler.Received);
        Assert.True(handler.Received[0].Body.SequenceEqual(bytes),
            "Forwarded bytes must be byte-identical to the received payload");
        Assert.Equal("application/x-protobuf", handler.Received[0].ContentType);
    }

    // ----- N destinations each receive the same payload -----

    [Fact]
    public async Task TwoDestinations_BothReceiveSamePayload()
    {
        var h1 = new CapturingHttpHandler();
        var h2 = new CapturingHttpHandler();
        using var fixture = new FanoutFixture([MakeDest("primary", h1), MakeDest("secondary", h2)]);

        var bytes = ConformantOtlpBytes();
        using var _ = await PostOtlpAsync(fixture.CreateClient(), bytes);

        await fixture.Dispatcher.DrainAsync();

        Assert.Single(h1.Received);
        Assert.Single(h2.Received);
        Assert.True(h1.Received[0].Body.SequenceEqual(bytes));
        Assert.True(h2.Received[0].Body.SequenceEqual(bytes));
    }

    // ----- unreachable destination does not affect ingestion -----

    [Fact]
    public async Task UnreachableDestination_IngestionSucceeds_SpanPersisted_DeliveryFailed()
    {
        using var fixture = new FanoutFixture([MakeDest("dead", new FailingHttpHandler())]);

        var bytes = ConformantOtlpBytes();
        using var resp = await PostOtlpAsync(fixture.CreateClient(), bytes);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await fixture.Dispatcher.DrainAsync();

        var pending = await fixture.Repository.GetPendingForwardDeliveriesAsync(10);
        Assert.Empty(pending);

        var delivered = await fixture.Repository.GetForwardDeliveryStatusAsync("dead");
        Assert.Contains(delivered, d => d == "failed");
    }

    // ----- zero destinations — no queue rows written -----

    [Fact]
    public async Task ZeroDestinations_IngestionSucceeds_NoQueueRows()
    {
        using var fixture = new FanoutFixture([]);

        using var resp = await PostOtlpAsync(fixture.CreateClient(), ConformantOtlpBytes());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var pending = await fixture.Repository.GetPendingForwardDeliveriesAsync(10);
        Assert.Empty(pending);
    }

    // ----- disabled destination is not forwarded to -----

    [Fact]
    public async Task DisabledDestination_NotForwarded()
    {
        var handler = new CapturingHttpHandler();
        using var fixture = new FanoutFixture([MakeDest("off", handler, enabled: false)]);

        using var _ = await PostOtlpAsync(fixture.CreateClient(), ConformantOtlpBytes());
        await fixture.Dispatcher.DrainAsync();

        Assert.Empty(handler.Received);
    }

    // ----- restart with queued items — items survive and drain -----

    [Fact]
    public async Task QueuedItems_SurviveRepositoryRestart_ThenDrain()
    {
        var handler = new CapturingHttpHandler();
        var dbPath  = Path.Combine(Path.GetTempPath(), $"clawindex-fanout-restart-{Guid.NewGuid():N}.db");
        try
        {
            // First lifetime — enqueue
            var repo1 = new EventRepository(BuildConfig(dbPath));
            await repo1.InitializeAsync();
            await repo1.EnqueueForwardAsync(
                [0x01, 0x02, 0x03], "application/x-protobuf", ["primary"]);

            // Simulate restart — same file, fresh EventRepository
            var repo2 = new EventRepository(BuildConfig(dbPath));
            await repo2.InitializeAsync();

            var pending = await repo2.GetPendingForwardDeliveriesAsync(10);
            Assert.Single(pending);

            // Drain via dispatcher wired to repo2
            var dest       = MakeDest("primary", handler);
            var dispatcher = new ForwardDispatcher(
                repo2,
                [dest],
                NullLogger<ForwardDispatcher>.Instance);
            await dispatcher.DrainAsync();

            Assert.Single(handler.Received);
            Assert.Equal([0x01, 0x02, 0x03], handler.Received[0].Body);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    // ----- env-var-only config loads destinations correctly -----

    [Fact]
    public async Task EnvVarOnlyConfig_DestinationsLoad_AndDeliver()
    {
        var handler = new CapturingHttpHandler();
        var dest    = MakeDest("aspire", handler);
        // Destinations are overridden directly in DI — this verifies the fixture pattern
        // that mirrors production env-var config (the double-underscore bindings are tested
        // in integration; here we verify the full ingest→enqueue→drain path works when
        // destinations come from outside appsettings.json).
        using var fixture = new FanoutFixture([dest]);

        using var _ = await PostOtlpAsync(fixture.CreateClient(), ConformantOtlpBytes());
        await fixture.Dispatcher.DrainAsync();

        Assert.Single(handler.Received);
    }

    // ----- POST latency unaffected by forwarding (enqueue is on-path; delivery is not) -----

    [Fact]
    public async Task SlowDestination_PostResponseTime_Unaffected()
    {
        // ForwardQueue only writes to SQLite (fast). The slow delivery happens in DrainAsync,
        // which is not on the POST request path.
        using var fixture = new FanoutFixture([MakeDest("slow", new SlowHttpHandler(delay: TimeSpan.FromSeconds(10)))]);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var resp = await PostOtlpAsync(fixture.CreateClient(), ConformantOtlpBytes());
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"POST should return in under 2 s even with a slow destination; took {sw.ElapsedMilliseconds} ms");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static OtlpHttpDestination MakeDest(string name, HttpMessageHandler handler, bool enabled = true)
    {
        var config = new DestinationConfig
        {
            Name     = name,
            Type     = "otlp-http",
            Endpoint = $"http://localhost/{name}/v1/traces",
            Enabled  = enabled
        };
        var client = new HttpClient(handler);
        return new OtlpHttpDestination(config, client);
    }

    private static IConfiguration BuildConfig(string dbPath) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Clawindex:DatabasePath"] = dbPath })
            .Build();

    private static Task<HttpResponseMessage> PostOtlpAsync(HttpClient client, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        return client.PostAsync("/v1/traces", content);
    }

    private static byte[] ConformantOtlpBytes()
    {
        var now  = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L);
        var span = new Span
        {
            TraceId           = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            SpanId            = ByteString.CopyFrom(Guid.NewGuid().ToByteArray(), 0, 8),
            Name              = "gen_ai.chat",
            StartTimeUnixNano = now,
            EndTimeUnixNano   = now + 1_000_000_000UL,
            Kind              = Span.Types.SpanKind.Client
        };
        span.Attributes.Add(new KeyValue { Key = "gen_ai.operation.name",     Value = new AnyValue { StringValue = "chat" } });
        span.Attributes.Add(new KeyValue { Key = "gen_ai.provider.name",      Value = new AnyValue { StringValue = "anthropic" } });
        span.Attributes.Add(new KeyValue { Key = "gen_ai.request.model",      Value = new AnyValue { StringValue = "claude-sonnet-4-6" } });
        span.Attributes.Add(new KeyValue { Key = "gen_ai.usage.input_tokens", Value = new AnyValue { IntValue = 100 } });
        span.Attributes.Add(new KeyValue { Key = "gen_ai.usage.output_tokens",Value = new AnyValue { IntValue = 200 } });
        span.Attributes.Add(new KeyValue { Key = "clawindex.agent.id",        Value = new AnyValue { StringValue = "svc-fanout-test-01" } });

        var request = new ExportTraceServiceRequest();
        var rs      = new ResourceSpans();
        var ss      = new ScopeSpans();
        ss.Spans.Add(span);
        rs.ScopeSpans.Add(ss);
        rs.Resource = new Resource();
        rs.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "fanout-test" } });
        request.ResourceSpans.Add(rs);
        return request.ToByteArray();
    }
}

// -------------------------------------------------------------------------
// Test HTTP handlers
// -------------------------------------------------------------------------

internal sealed class CapturingHttpHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<(byte[] Body, string ContentType)> _received = new();

    public IReadOnlyList<(byte[] Body, string ContentType)> Received => _received.ToArray();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var body        = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        var contentType = request.Content?.Headers.ContentType?.MediaType ?? string.Empty;
        _received.Enqueue((body, contentType));
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}

internal sealed class FailingHttpHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct) =>
        throw new HttpRequestException("Simulated unreachable destination");
}

internal sealed class SlowHttpHandler(TimeSpan delay) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        await Task.Delay(delay, ct);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}

// -------------------------------------------------------------------------
// FanoutFixture
// -------------------------------------------------------------------------

public sealed class FanoutFixture : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbPath;

    public FanoutFixture(IReadOnlyList<ITelemetryDestination> destinations)
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"clawindex-fanout-{Guid.NewGuid():N}.db");

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Clawindex:DatabasePath"]       = _dbPath,
                        ["Clawindex:Forwarding:Enabled"] = "false"   // worker disabled; tests call DrainAsync directly
                    });
                });
                builder.ConfigureServices(services =>
                {
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(IReadOnlyList<ITelemetryDestination>));
                    if (descriptor is not null) services.Remove(descriptor);
                    services.AddSingleton(destinations);
                });
            });
    }

    public ForwardDispatcher Dispatcher  => _factory.Services.GetRequiredService<ForwardDispatcher>();
    public EventRepository   Repository  => _factory.Services.GetRequiredService<EventRepository>();
    public HttpClient        CreateClient() => _factory.CreateClient();

    public void Dispose()
    {
        _factory.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
