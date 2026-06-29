using System.Net;
using System.Net.Http.Headers;
using Clawindex.Collector.Api.Otlp;
using Google.Protobuf;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Clawindex.Collector.Tests;

public sealed class OtlpIngestionTests
{
    [Fact]
    public async Task OtlpIngestion_ConformantSpan_Returns200_AndIsConformantInSink()
    {
        using var fixture = new OtlpFixture();
        var agentId = Guid.NewGuid();

        using var response = await fixture.PostOtlpAsync(BuildConformantRequest(agentId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = fixture.SpanSink.Received;
        Assert.Single(received);
        Assert.True(received[0].IsConformant);
        Assert.Equal(agentId, received[0].AgentId);
        Assert.Equal("chat", received[0].Operation);
        Assert.Equal("anthropic", received[0].Provider);
        Assert.Equal("claude-sonnet-4-6", received[0].Model);
        Assert.Equal(100L, received[0].InputTokens);
        Assert.Equal(200L, received[0].OutputTokens);
    }

    [Fact]
    public async Task OtlpIngestion_MissingTokenFields_Accepted_MarkedNonConformant()
    {
        using var fixture = new OtlpFixture();

        var request = BuildConformantRequest(Guid.NewGuid());
        // Remove token attributes from the span
        var span = request.ResourceSpans[0].ScopeSpans[0].Spans[0];
        var attrs = span.Attributes.Where(a =>
            a.Key != "gen_ai.usage.input_tokens" && a.Key != "gen_ai.usage.output_tokens").ToList();
        span.Attributes.Clear();
        span.Attributes.AddRange(attrs);

        using var response = await fixture.PostOtlpAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = fixture.SpanSink.Received;
        Assert.Single(received);
        Assert.False(received[0].IsConformant);
        Assert.Null(received[0].InputTokens);
        Assert.Null(received[0].OutputTokens);
    }

    [Fact]
    public async Task OtlpIngestion_NilGuidAgentId_MarkedNonConformant()
    {
        using var fixture = new OtlpFixture();

        var request = BuildConformantRequest(Guid.Empty);

        using var response = await fixture.PostOtlpAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = fixture.SpanSink.Received;
        Assert.Single(received);
        Assert.False(received[0].IsConformant);
        Assert.Null(received[0].AgentId);
        // Raw attribute string must still be present (amendment 3)
        Assert.Contains(received[0].RawAttributes,
            kv => kv.Key == "gen_ai.agent.id" && kv.Value == Guid.Empty.ToString());
    }

    [Fact]
    public async Task OtlpIngestion_SentinelGuidAgentId_MarkedNonConformant()
    {
        using var fixture = new OtlpFixture();
        var sentinel = new Guid("00000000-0000-0000-0000-000000000001");

        var request = BuildConformantRequest(sentinel);

        using var response = await fixture.PostOtlpAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = fixture.SpanSink.Received;
        Assert.Single(received);
        Assert.False(received[0].IsConformant);
        Assert.Null(received[0].AgentId);
        Assert.Contains(received[0].RawAttributes,
            kv => kv.Key == "gen_ai.agent.id" && kv.Value == sentinel.ToString());
    }

    [Fact]
    public async Task OtlpIngestion_MalformedAgentId_MarkedNonConformant()
    {
        using var fixture = new OtlpFixture();

        var request = BuildConformantRequest(Guid.NewGuid());
        var span = request.ResourceSpans[0].ScopeSpans[0].Spans[0];
        var agentAttr = span.Attributes.First(a => a.Key == "gen_ai.agent.id");
        agentAttr.Value = new AnyValue { StringValue = "not-a-guid" };

        using var response = await fixture.PostOtlpAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = fixture.SpanSink.Received;
        Assert.Single(received);
        Assert.False(received[0].IsConformant);
        Assert.Null(received[0].AgentId);
        // Raw bad value must survive (amendment 3)
        Assert.Contains(received[0].RawAttributes,
            kv => kv.Key == "gen_ai.agent.id" && kv.Value == "not-a-guid");
    }

    [Fact]
    public async Task OtlpIngestion_EmptyBody_ReturnsBadRequest_NoSinkEntries()
    {
        using var fixture = new OtlpFixture();
        var client = fixture.CreateClient();

        var content = new ByteArrayContent([]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        using var response = await client.PostAsync("/v1/traces", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fixture.SpanSink.Received);
    }

    [Fact]
    public async Task OtlpIngestion_MalformedBody_ReturnsBadRequest_NoSinkEntries()
    {
        using var fixture = new OtlpFixture();
        var client = fixture.CreateClient();

        var content = new ByteArrayContent([0xFF, 0xFE, 0x00, 0x01]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        using var response = await client.PostAsync("/v1/traces", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fixture.SpanSink.Received);
    }

    [Fact]
    public async Task OtlpIngestion_MultipleSpans_MixedConformance_AllAccepted()
    {
        using var fixture = new OtlpFixture();
        var goodAgent = Guid.NewGuid();

        var request = new ExportTraceServiceRequest();
        var resource = new Resource();
        var resourceSpans = new ResourceSpans { Resource = resource };
        var scopeSpans = new ScopeSpans();
        resourceSpans.ScopeSpans.Add(scopeSpans);
        request.ResourceSpans.Add(resourceSpans);

        // Span 1: conformant
        scopeSpans.Spans.Add(MakeSpan(goodAgent.ToString(), includeTokens: true));
        // Span 2: missing agent id → non-conformant
        scopeSpans.Spans.Add(MakeSpan(agentId: null, includeTokens: true));
        // Span 3: missing tokens → non-conformant
        scopeSpans.Spans.Add(MakeSpan(goodAgent.ToString(), includeTokens: false));

        using var response = await fixture.PostOtlpAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = fixture.SpanSink.Received;
        Assert.Equal(3, received.Count);
        Assert.Equal(1, received.Count(s => s.IsConformant));
        Assert.Equal(2, received.Count(s => !s.IsConformant));
    }

    [Fact]
    public async Task OtlpIngestion_ProviderViaGenAiSystem_IsConformant()
    {
        using var fixture = new OtlpFixture();
        var agentId = Guid.NewGuid();

        // Use gen_ai.system (prior key) instead of gen_ai.provider.name (current key)
        var request = BuildConformantRequest(agentId, providerKey: "gen_ai.system");

        using var response = await fixture.PostOtlpAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = fixture.SpanSink.Received;
        Assert.Single(received);
        Assert.True(received[0].IsConformant);
        Assert.Equal("anthropic", received[0].Provider);
    }

    [Fact]
    public async Task OtlpIngestion_StringEncodedTokens_IsConformant()
    {
        using var fixture = new OtlpFixture();
        var agentId = Guid.NewGuid();

        // Tokens as string-encoded integers (tolerance from SemConv amendment)
        var request = BuildConformantRequest(agentId, tokensAsString: true);

        using var response = await fixture.PostOtlpAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = fixture.SpanSink.Received;
        Assert.Single(received);
        Assert.True(received[0].IsConformant);
        Assert.Equal(100L, received[0].InputTokens);
        Assert.Equal(200L, received[0].OutputTokens);
    }

    // --- Helpers ---

    private static ExportTraceServiceRequest BuildConformantRequest(
        Guid agentId,
        string providerKey = "gen_ai.provider.name",
        bool tokensAsString = false)
    {
        var request = new ExportTraceServiceRequest();
        var resource = new Resource();
        var resourceSpans = new ResourceSpans { Resource = resource };
        var scopeSpans = new ScopeSpans();
        scopeSpans.Spans.Add(MakeSpan(agentId.ToString(), includeTokens: true, providerKey: providerKey, tokensAsString: tokensAsString));
        resourceSpans.ScopeSpans.Add(scopeSpans);
        request.ResourceSpans.Add(resourceSpans);
        return request;
    }

    private static Span MakeSpan(
        string? agentId,
        bool includeTokens,
        string providerKey = "gen_ai.provider.name",
        bool tokensAsString = false)
    {
        var now = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L);
        var span = new Span
        {
            TraceId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            SpanId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray(), 0, 8),
            Name = "gen_ai.chat",
            StartTimeUnixNano = now,
            EndTimeUnixNano = now + 1_000_000_000UL,
            Kind = Span.Types.SpanKind.Client
        };

        span.Attributes.Add(new KeyValue { Key = "gen_ai.operation.name", Value = new AnyValue { StringValue = "chat" } });
        span.Attributes.Add(new KeyValue { Key = providerKey, Value = new AnyValue { StringValue = "anthropic" } });
        span.Attributes.Add(new KeyValue { Key = "gen_ai.request.model", Value = new AnyValue { StringValue = "claude-sonnet-4-6" } });

        if (includeTokens)
        {
            if (tokensAsString)
            {
                span.Attributes.Add(new KeyValue { Key = "gen_ai.usage.input_tokens", Value = new AnyValue { StringValue = "100" } });
                span.Attributes.Add(new KeyValue { Key = "gen_ai.usage.output_tokens", Value = new AnyValue { StringValue = "200" } });
            }
            else
            {
                span.Attributes.Add(new KeyValue { Key = "gen_ai.usage.input_tokens", Value = new AnyValue { IntValue = 100 } });
                span.Attributes.Add(new KeyValue { Key = "gen_ai.usage.output_tokens", Value = new AnyValue { IntValue = 200 } });
            }
        }

        if (agentId != null)
        {
            span.Attributes.Add(new KeyValue { Key = "gen_ai.agent.id", Value = new AnyValue { StringValue = agentId } });
        }

        return span;
    }
}

public sealed class OtlpFixture : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public OtlpFixture()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Clawindex:DatabasePath"] = Path.Combine(Path.GetTempPath(), $"clawindex-otlp-tests-{Guid.NewGuid():N}.db"),
                        ["Clawindex:Projection:Enabled"] = "false"
                    });
                });
            });
    }

    public InMemorySpanSink SpanSink => _factory.Services.GetRequiredService<InMemorySpanSink>();

    public HttpClient CreateClient() => _factory.CreateClient();

    public async Task<HttpResponseMessage> PostOtlpAsync(ExportTraceServiceRequest request)
    {
        var client = _factory.CreateClient();
        var bytes = request.ToByteArray();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        return await client.PostAsync("/v1/traces", content);
    }

    public void Dispose() => _factory.Dispose();
}
