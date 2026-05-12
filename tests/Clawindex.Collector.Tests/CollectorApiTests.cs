using System.Net;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Clawindex.Collector.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Clawindex.Collector.Tests;

public sealed class CollectorApiTests
{
    [Fact]
    public async Task Health_ReturnsOk()
    {
        using var fixture = new CollectorFixture();
        var client = fixture.CreateClient();

        using var response = await client.GetAsync("/v1/health");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("clawindex-collector", body.RootElement.GetProperty("service").GetString());
        Assert.Equal("0.1.0", body.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public async Task Schema_ReturnsEnvelopeMetadata()
    {
        using var fixture = new CollectorFixture();
        var client = fixture.CreateClient();

        using var response = await client.GetAsync("/v1/schema");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0.1.0", body.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal("clawindex.event.envelope", body.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Events_AcceptsValidEvent_GeneratesEventId_AndStoresRawPayload()
    {
        using var fixture = new CollectorFixture();
        var client = fixture.CreateClient();
        var request = """
            {
              "schema_version": "0.1.0",
              "event_type": "policy.evaluated",
              "occurred_at": "2026-05-11T22:15:00Z",
              "source": {
                "system": "bouncer-md",
                "component": "resolver",
                "version": "1.0.0"
              },
              "correlation": {
                "trace_id": "trace_abc",
                "task_id": "task_456",
                "agent_id": "agent_soil_report"
              },
              "payload": {
                "decision": "deny",
                "policy_id": "soil-data-scope"
              }
            }
            """;

        using var response = await client.PostAsync("/v1/events", JsonContent.Create(JsonDocument.Parse(request).RootElement));
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("accepted", body.RootElement.GetProperty("status").GetString());

        var eventId = body.RootElement.GetProperty("event_id").GetString();
        Assert.StartsWith("evt_", eventId);
        Assert.Equal("0.1.0", body.RootElement.GetProperty("schema_version").GetString());
        Assert.True(body.RootElement.TryGetProperty("received_at", out _));

        var stored = await fixture.Repository.GetByIdAsync(eventId!);
        Assert.NotNull(stored);
        Assert.Equal("trace_abc", stored.TraceId);
        Assert.Equal("task_456", stored.TaskId);
        Assert.Equal("agent_soil_report", stored.AgentId);
        Assert.Contains("\"event_type\":\"policy.evaluated\"", CompactJson(stored.RawJson));
        Assert.Equal("{\"decision\":\"deny\",\"policy_id\":\"soil-data-scope\"}", CompactJson(stored.PayloadJson));
    }

    [Fact]
    public async Task Events_RejectsMissingEventType()
    {
        using var fixture = new CollectorFixture();
        var client = fixture.CreateClient();
        var request = new
        {
            schema_version = "0.1.0",
            occurred_at = "2026-05-11T22:15:00Z",
            source = new { system = "bouncer-md" },
            payload = new { decision = "deny" }
        };

        using var response = await client.PostAsJsonAsync("/v1/events", request);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("rejected", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("validation_failed", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Missing required field: event_type", body.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(0, await fixture.Repository.CountAsync());
    }

    [Fact]
    public async Task Events_RejectsInvalidOccurredAt()
    {
        using var fixture = new CollectorFixture();
        var client = fixture.CreateClient();
        var request = ValidEvent(eventType: "policy.evaluated") with { OccurredAt = "not-a-date" };

        using var response = await client.PostAsJsonAsync("/v1/events", request);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("occurred_at", body.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Events_RejectsMalformedJson()
    {
        using var fixture = new CollectorFixture();
        var client = fixture.CreateClient();
        using var content = new StringContent("{", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/v1/events", content);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("rejected", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("validation_failed", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Request body must be valid JSON", body.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(0, await fixture.Repository.CountAsync());
    }

    [Fact]
    public async Task Events_RejectsMissingSourceSystem()
    {
        using var fixture = new CollectorFixture();
        var client = fixture.CreateClient();
        var request = new
        {
            schema_version = "0.1.0",
            event_type = "policy.evaluated",
            occurred_at = "2026-05-11T22:15:00Z",
            source = new { component = "resolver" },
            payload = new { decision = "deny" }
        };

        using var response = await client.PostAsJsonAsync("/v1/events", request);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Missing required field: source.system", body.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(0, await fixture.Repository.CountAsync());
    }

    [Fact]
    public async Task Events_RejectsMissingPayload()
    {
        using var fixture = new CollectorFixture();
        var client = fixture.CreateClient();
        var request = new
        {
            schema_version = "0.1.0",
            event_type = "policy.evaluated",
            occurred_at = "2026-05-11T22:15:00Z",
            source = new { system = "bouncer-md" }
        };

        using var response = await client.PostAsJsonAsync("/v1/events", request);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Missing required field: payload", body.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(0, await fixture.Repository.CountAsync());
    }

    [Fact]
    public async Task Events_RejectsMalformedPayload()
    {
        using var fixture = new CollectorFixture();
        var client = fixture.CreateClient();
        var request = """
            {
              "schema_version": "0.1.0",
              "event_type": "policy.evaluated",
              "occurred_at": "2026-05-11T22:15:00Z",
              "source": { "system": "bouncer-md" },
              "payload": []
            }
            """;

        using var response = await client.PostAsync("/v1/events", JsonContent.Create(JsonDocument.Parse(request).RootElement));
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Missing required field: payload", body.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(0, await fixture.Repository.CountAsync());
    }

    [Fact]
    public async Task Events_AcceptsUnknownEventType()
    {
        using var fixture = new CollectorFixture();
        var client = fixture.CreateClient();

        using var response = await client.PostAsJsonAsync("/v1/events", ValidEvent(eventType: "vendor.custom.event"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, await fixture.Repository.CountAsync());
    }

    [Fact]
    public async Task EventsBatch_PartialSuccess_PersistsOnlyValidEvents()
    {
        using var fixture = new CollectorFixture();
        var client = fixture.CreateClient();
        var request = new
        {
            events = new object[]
            {
                ValidEvent(eventType: "agent.task.started"),
                new
                {
                    schema_version = "0.1.0",
                    occurred_at = "2026-05-11T22:15:00Z",
                    source = new { system = "test-agent" },
                    payload = new { task_name = "Generate soil report" }
                }
            }
        };

        using var response = await client.PostAsJsonAsync("/v1/events/batch", request);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("partial", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("accepted_count").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("rejected_count").GetInt32());
        Assert.Equal("accepted", body.RootElement.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.StartsWith("evt_", body.RootElement.GetProperty("results")[0].GetProperty("event_id").GetString());
        Assert.Equal("rejected", body.RootElement.GetProperty("results")[1].GetProperty("status").GetString());
        Assert.Equal("validation_failed", body.RootElement.GetProperty("results")[1].GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Missing required field: event_type", body.RootElement.GetProperty("results")[1].GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(1, await fixture.Repository.CountAsync());
    }

    private static EventRequest ValidEvent(string eventType) => new(
        "0.1.0",
        eventType,
        "2026-05-11T22:15:00Z",
        new SourceRequest("bouncer-md"),
        new { decision = "deny" });

    private static string CompactJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement);
    }

    private sealed record EventRequest(
        [property: JsonPropertyName("schema_version")]
        string SchemaVersion,
        [property: JsonPropertyName("event_type")]
        string EventType,
        [property: JsonPropertyName("occurred_at")]
        string OccurredAt,
        [property: JsonPropertyName("source")]
        SourceRequest Source,
        [property: JsonPropertyName("payload")]
        object Payload);

    private sealed record SourceRequest([property: JsonPropertyName("system")] string System);
}

public sealed class CollectorFixture : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public CollectorFixture()
    {
        DatabasePath = Path.Combine(Path.GetTempPath(), $"clawindex-tests-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Clawindex:DatabasePath"] = DatabasePath
                    });
                });
            });
    }

    public string DatabasePath { get; }

    public EventRepository Repository => _factory.Services.GetRequiredService<EventRepository>();

    public HttpClient CreateClient() => _factory.CreateClient();

    public void Dispose()
    {
        _factory.Dispose();
        if (File.Exists(DatabasePath))
        {
            File.Delete(DatabasePath);
        }
    }
}
