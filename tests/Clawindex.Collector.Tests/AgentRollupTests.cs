using System.Net;
using System.Text.Json;
using Clawindex.Collector.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Clawindex.Collector.Tests;

public sealed class AgentRollupTests
{
    private static SpanState MakeSpanState(
        string spanId,
        string traceId,
        string? agentId,
        string status = "unset",
        bool isConformant = true,
        DateTimeOffset? endedAt = null,
        DateTimeOffset? startedAt = null)
    {
        var ended = endedAt ?? DateTimeOffset.UtcNow;
        return new SpanState(
            SpanId: spanId,
            TraceId: traceId,
            ParentSpanId: null,
            AgentId: agentId,
            SpanName: "test.span",
            SpanKind: "client",
            Status: status,
            StartedAt: startedAt ?? ended.AddSeconds(-1),
            EndedAt: ended,
            Operation: null,
            Provider: null,
            Model: null,
            InputTokens: null,
            OutputTokens: null,
            IsConformant: isConformant,
            AttributesJson: "{}",
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private sealed class RepositoryFixture : IDisposable
    {
        private readonly string _dbPath;
        public EventRepository Repository { get; }

        public RepositoryFixture()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"clawindex-test-{Guid.NewGuid():N}.db");
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Clawindex:DatabasePath"] = _dbPath })
                .Build();
            Repository = new EventRepository(config);
            Repository.InitializeAsync().GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
    }

    // TC-1: Two agents, multi-trace COUNT DISTINCT
    [Fact]
    public async Task GetAgentRollups_TwoAgents_CorrectSpanAndTraceCount()
    {
        using var fixture = new RepositoryFixture();
        var agentA = Guid.NewGuid().ToString();
        var agentB = Guid.NewGuid().ToString();
        var inside = new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero);

        // Agent A: 3 spans across 2 distinct trace IDs
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("a1", "trace-1", agentA, endedAt: inside));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("a2", "trace-1", agentA, endedAt: inside));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("a3", "trace-2", agentA, endedAt: inside));
        // Agent B: 2 spans in 1 trace ID
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("b1", "trace-3", agentB, endedAt: inside));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("b2", "trace-3", agentB, endedAt: inside));

        var rollups = await fixture.Repository.GetAgentRollupsAsync(
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(2, rollups.Count);

        var a = rollups.Single(r => r.AgentId == agentA);
        Assert.Equal(3, a.SpanCount);
        Assert.Equal(2, a.TraceCount);

        var b = rollups.Single(r => r.AgentId == agentB);
        Assert.Equal(2, b.SpanCount);
        Assert.Equal(1, b.TraceCount);
    }

    // TC-2: Error rate — mixed 'error' and non-error spans
    [Fact]
    public async Task GetAgentRollups_MixedStatus_CorrectErrorCountAndRate()
    {
        using var fixture = new RepositoryFixture();
        var agent = Guid.NewGuid().ToString();
        var inside = new DateTimeOffset(2025, 3, 15, 0, 0, 0, TimeSpan.Zero);

        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("e1", "t1", agent, status: "error", endedAt: inside));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("e2", "t1", agent, status: "error", endedAt: inside));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("e3", "t1", agent, status: "unset", endedAt: inside));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("e4", "t1", agent, status: "unset", endedAt: inside));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("e5", "t1", agent, status: "unset", endedAt: inside));

        var rollups = await fixture.Repository.GetAgentRollupsAsync(
            new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 4, 1, 0, 0, 0, TimeSpan.Zero));

        var row = Assert.Single(rollups);
        Assert.Equal(5, row.SpanCount);
        Assert.Equal(2, row.ErrorCount);
        Assert.Equal(0.4, row.ErrorRate, precision: 5);
    }

    // TC-3: Conformance ratio — non-conformant-but-real spans drag ratio down.
    // Seeded via UpsertSpanStateAsync directly with non-null agent_id and is_conformant=false,
    // because MakeSpan(isConformant:false) in DurableSpanSinkTests nulls the agent_id,
    // which would exclude those spans from the rollup entirely.
    [Fact]
    public async Task GetAgentRollups_MixedConformance_CorrectRatio()
    {
        using var fixture = new RepositoryFixture();
        var agent = Guid.NewGuid().ToString();
        var inside = new DateTimeOffset(2025, 4, 15, 0, 0, 0, TimeSpan.Zero);

        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("c1", "t1", agent, isConformant: true,  endedAt: inside));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("c2", "t1", agent, isConformant: true,  endedAt: inside));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("c3", "t1", agent, isConformant: false, endedAt: inside));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("c4", "t1", agent, isConformant: false, endedAt: inside));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("c5", "t1", agent, isConformant: false, endedAt: inside));

        var rollups = await fixture.Repository.GetAgentRollupsAsync(
            new DateTimeOffset(2025, 4, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 5, 1, 0, 0, 0, TimeSpan.Zero));

        var row = Assert.Single(rollups);
        Assert.Equal(5, row.SpanCount);
        Assert.Equal(0.4, row.ConformanceRatio, precision: 5);
    }

    // TC-4: Placeholder spans (null agent_id) are excluded from rollups
    [Fact]
    public async Task GetAgentRollups_PlaceholderSpans_NotIncludedInRollup()
    {
        using var fixture = new RepositoryFixture();
        var realAgent = Guid.NewGuid().ToString();
        var inside = new DateTimeOffset(2025, 5, 15, 0, 0, 0, TimeSpan.Zero);

        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("p1", "t1", agentId: null, endedAt: inside));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("p2", "t1", agentId: null, endedAt: inside));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("r1", "t2", realAgent,     endedAt: inside));

        var rollups = await fixture.Repository.GetAgentRollupsAsync(
            new DateTimeOffset(2025, 5, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero));

        var row = Assert.Single(rollups);
        Assert.Equal(realAgent, row.AgentId);
        Assert.Equal(1, row.SpanCount);
    }

    // TC-5: Window is [since, until) — inclusive lower bound, exclusive upper bound
    [Fact]
    public async Task GetAgentRollups_WindowBounds_InclusiveSinceExclusiveUntil()
    {
        using var fixture = new RepositoryFixture();
        var agent = Guid.NewGuid().ToString();
        var since = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var until = new DateTimeOffset(2025, 7, 1, 0, 0, 0, TimeSpan.Zero);

        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("before",   "t1", agent, endedAt: since.AddSeconds(-1)));  // excluded
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("at-since", "t1", agent, endedAt: since));                 // included (>=)
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("inside",   "t1", agent, endedAt: since.AddDays(15)));     // included
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("at-until", "t1", agent, endedAt: until));                 // excluded (<)

        var rollups = await fixture.Repository.GetAgentRollupsAsync(since, until);

        var row = Assert.Single(rollups);
        Assert.Equal(2, row.SpanCount);
    }

    // TC-9: No spans in window → empty list, no error
    [Fact]
    public async Task GetAgentRollups_NoSpansInWindow_ReturnsEmpty()
    {
        using var fixture = new RepositoryFixture();
        var agent = Guid.NewGuid().ToString();

        await fixture.Repository.UpsertSpanStateAsync(
            MakeSpanState("s1", "t1", agent, endedAt: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        var rollups = await fixture.Repository.GetAgentRollupsAsync(
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero));

        Assert.Empty(rollups);
    }

    // TC-6: Default window is trailing 30 days from injected TimeProvider.GetUtcNow()
    [Fact]
    public async Task GetAgents_DefaultWindow_Trailing30Days_DeterministicWithFakeTime()
    {
        var fixedNow = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(fixedNow);
        var dbPath = Path.Combine(Path.GetTempPath(), $"clawindex-test-{Guid.NewGuid():N}.db");

        await using (var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Clawindex:DatabasePath"] = dbPath,
                        ["Clawindex:Projection:Enabled"] = "false"
                    }));
                b.ConfigureServices(services =>
                {
                    var existing = services.SingleOrDefault(d => d.ServiceType == typeof(TimeProvider));
                    if (existing is not null) services.Remove(existing);
                    services.AddSingleton<TimeProvider>(fakeTime);
                });
            }))
        {
            var client = factory.CreateClient();
            var repository = factory.Services.GetRequiredService<EventRepository>();
            var agent = Guid.NewGuid().ToString();

            // inside: 1 day ago — within trailing 30 days
            await repository.UpsertSpanStateAsync(
                MakeSpanState("in-window", "t1", agent, endedAt: fixedNow.AddDays(-1)));
            // outside: 31 days ago — before trailing 30 days
            await repository.UpsertSpanStateAsync(
                MakeSpanState("out-window", "t2", agent, endedAt: fixedNow.AddDays(-31)));

            using var response = await client.GetAsync("/v1/agents");
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var rows = body.RootElement.EnumerateArray().ToList();
            Assert.Single(rows);
            Assert.Equal(1, rows[0].GetProperty("span_count").GetInt64());
        }

        if (File.Exists(dbPath)) File.Delete(dbPath);
    }

    // TC-7a: since == until → 400
    [Fact]
    public async Task GetAgents_SinceEqualsUntil_Returns400()
    {
        using var fixture = new CollectorFixture();
        var client = fixture.CreateClient();

        using var response = await client.GetAsync("/v1/agents?since=2025-01-01T00:00:00Z&until=2025-01-01T00:00:00Z");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("rejected", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("validation_failed", body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // TC-7b: since > until → 400
    [Fact]
    public async Task GetAgents_SinceAfterUntil_Returns400()
    {
        using var fixture = new CollectorFixture();
        var client = fixture.CreateClient();

        using var response = await client.GetAsync("/v1/agents?since=2025-01-02T00:00:00Z&until=2025-01-01T00:00:00Z");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // TC-8a: unparseable since → 400, message mentions 'since'
    [Fact]
    public async Task GetAgents_UnparseableSince_Returns400()
    {
        using var fixture = new CollectorFixture();
        var client = fixture.CreateClient();

        using var response = await client.GetAsync("/v1/agents?since=not-a-date");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("since", body.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    // TC-8b: unparseable until → 400, message mentions 'until'
    [Fact]
    public async Task GetAgents_UnparseableUntil_Returns400()
    {
        using var fixture = new CollectorFixture();
        var client = fixture.CreateClient();

        using var response = await client.GetAsync("/v1/agents?until=not-a-date");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("until", body.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    // Endpoint smoke test: 200 with empty JSON array when no spans exist in window
    [Fact]
    public async Task GetAgents_EmptyWindow_Returns200EmptyArray()
    {
        using var fixture = new CollectorFixture();
        var client = fixture.CreateClient();

        using var response = await client.GetAsync("/v1/agents?since=2020-01-01T00:00:00Z&until=2020-01-02T00:00:00Z");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);
        Assert.Equal(0, body.RootElement.GetArrayLength());
    }
}
