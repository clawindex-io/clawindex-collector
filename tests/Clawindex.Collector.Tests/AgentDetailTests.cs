using System.Net;
using System.Text.Json;
using Clawindex.Collector.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Clawindex.Collector.Tests;

public sealed class AgentDetailTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

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

    private static TraceState MakeTraceState(
        string traceId,
        string status,
        DateTimeOffset startedAt,
        DateTimeOffset? endedAt = null,
        string? agentId = null) =>
        new(traceId, null, agentId, status, startedAt, endedAt, DateTimeOffset.UtcNow);

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

    // -------------------------------------------------------------------------
    // Repository — GetAgentRollupAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetAgentRollup_MultipleTraces_CorrectCounts()
    {
        using var fixture = new RepositoryFixture();
        var agent = Guid.NewGuid().ToString();
        var inside = new DateTimeOffset(2025, 3, 15, 0, 0, 0, TimeSpan.Zero);

        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("s1", "trace-1", agent, endedAt: inside));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("s2", "trace-1", agent, endedAt: inside));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("s3", "trace-2", agent, endedAt: inside));

        var rollup = await fixture.Repository.GetAgentRollupAsync(
            agent,
            new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 4, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(3, rollup.SpanCount);
        Assert.Equal(2, rollup.TraceCount);
        Assert.NotNull(rollup.LastSeen);
    }

    [Fact]
    public async Task GetAgentRollup_ErrorSpans_CorrectErrorCountAndRate()
    {
        using var fixture = new RepositoryFixture();
        var agent = Guid.NewGuid().ToString();
        var inside = new DateTimeOffset(2025, 4, 15, 0, 0, 0, TimeSpan.Zero);

        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("e1", "t1", agent, status: "error", endedAt: inside));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("e2", "t1", agent, status: "error", endedAt: inside));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("e3", "t1", agent, status: "unset", endedAt: inside));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("e4", "t1", agent, status: "unset", endedAt: inside));

        var rollup = await fixture.Repository.GetAgentRollupAsync(
            agent,
            new DateTimeOffset(2025, 4, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 5, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(4, rollup.SpanCount);
        Assert.Equal(2, rollup.ErrorCount);
        Assert.Equal(0.5, rollup.ErrorRate, precision: 5);
    }

    [Fact]
    public async Task GetAgentRollup_ConformanceRatio_Correct()
    {
        using var fixture = new RepositoryFixture();
        var agent = Guid.NewGuid().ToString();
        var inside = new DateTimeOffset(2025, 5, 15, 0, 0, 0, TimeSpan.Zero);

        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("c1", "t1", agent, isConformant: true,  endedAt: inside));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("c2", "t1", agent, isConformant: false, endedAt: inside));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("c3", "t1", agent, isConformant: false, endedAt: inside));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("c4", "t1", agent, isConformant: false, endedAt: inside));

        var rollup = await fixture.Repository.GetAgentRollupAsync(
            agent,
            new DateTimeOffset(2025, 5, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(0.25, rollup.ConformanceRatio, precision: 5);
    }

    [Fact]
    public async Task GetAgentRollup_NoSpansInWindow_ReturnsZeroedRollup()
    {
        using var fixture = new RepositoryFixture();
        var agent = Guid.NewGuid().ToString();

        await fixture.Repository.UpsertSpanStateAsync(
            MakeSpanState("s1", "t1", agent, endedAt: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        var rollup = await fixture.Repository.GetAgentRollupAsync(
            agent,
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(0, rollup.SpanCount);
        Assert.Equal(0, rollup.TraceCount);
        Assert.Equal(0, rollup.ErrorCount);
        Assert.Equal(0.0, rollup.ErrorRate);
        Assert.Null(rollup.LastSeen);
        Assert.Equal(0.0, rollup.ConformanceRatio);
    }

    [Fact]
    public async Task GetAgentRollup_PlaceholderSpans_NotIncluded()
    {
        using var fixture = new RepositoryFixture();
        var agent = Guid.NewGuid().ToString();
        var inside = new DateTimeOffset(2025, 6, 15, 0, 0, 0, TimeSpan.Zero);

        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("p1", "t1", agentId: null, endedAt: inside));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("r1", "t2", agent, endedAt: inside));

        var rollup = await fixture.Repository.GetAgentRollupAsync(
            agent,
            new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 7, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(1, rollup.SpanCount);
    }

    [Fact]
    public async Task GetAgentRollup_WindowBounds_InclusiveSinceExclusiveUntil()
    {
        using var fixture = new RepositoryFixture();
        var agent = Guid.NewGuid().ToString();
        var since = new DateTimeOffset(2025, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var until = new DateTimeOffset(2025, 8, 1, 0, 0, 0, TimeSpan.Zero);

        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("before",   "t1", agent, endedAt: since.AddSeconds(-1)));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("at-since", "t2", agent, endedAt: since));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("inside",   "t3", agent, endedAt: since.AddDays(10)));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("at-until", "t4", agent, endedAt: until));

        var rollup = await fixture.Repository.GetAgentRollupAsync(agent, since, until);

        Assert.Equal(2, rollup.SpanCount);
    }

    // -------------------------------------------------------------------------
    // Repository — GetAgentRecentTracesAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetAgentRecentTraces_Ordering_MostRecentFirst()
    {
        using var fixture = new RepositoryFixture();
        var agent = Guid.NewGuid().ToString();
        var window = new DateTimeOffset(2025, 8, 15, 0, 0, 0, TimeSpan.Zero);

        var t1Start = new DateTimeOffset(2025, 8, 10, 0, 0, 0, TimeSpan.Zero);
        var t2Start = new DateTimeOffset(2025, 8, 12, 0, 0, 0, TimeSpan.Zero);
        var t3Start = new DateTimeOffset(2025, 8, 14, 0, 0, 0, TimeSpan.Zero);

        await fixture.Repository.UpsertTraceStateAsync(MakeTraceState("trace-a", "finalized", t1Start, t1Start.AddMinutes(5)));
        await fixture.Repository.UpsertTraceStateAsync(MakeTraceState("trace-b", "finalized", t2Start, t2Start.AddMinutes(5)));
        await fixture.Repository.UpsertTraceStateAsync(MakeTraceState("trace-c", "finalized", t3Start, t3Start.AddMinutes(5)));

        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("s1", "trace-a", agent, endedAt: window));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("s2", "trace-b", agent, endedAt: window));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("s3", "trace-c", agent, endedAt: window));

        var traces = await fixture.Repository.GetAgentRecentTracesAsync(
            agent,
            new DateTimeOffset(2025, 8, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 9, 1, 0, 0, 0, TimeSpan.Zero),
            limit: 10);

        Assert.Equal(3, traces.Count);
        Assert.Equal("trace-c", traces[0].TraceId);
        Assert.Equal("trace-b", traces[1].TraceId);
        Assert.Equal("trace-a", traces[2].TraceId);
    }

    [Fact]
    public async Task GetAgentRecentTraces_PerTraceErrorSignal_Correct()
    {
        using var fixture = new RepositoryFixture();
        var agent = Guid.NewGuid().ToString();
        var window = new DateTimeOffset(2025, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var traceStart = new DateTimeOffset(2025, 9, 14, 0, 0, 0, TimeSpan.Zero);

        await fixture.Repository.UpsertTraceStateAsync(MakeTraceState("clean-trace", "finalized", traceStart, traceStart.AddMinutes(1)));
        await fixture.Repository.UpsertTraceStateAsync(MakeTraceState("error-trace", "finalized", traceStart.AddHours(1), traceStart.AddHours(1).AddMinutes(1)));

        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("c1", "clean-trace", agent, status: "unset", endedAt: window));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("c2", "clean-trace", agent, status: "unset", endedAt: window));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("e1", "error-trace", agent, status: "error", endedAt: window));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("e2", "error-trace", agent, status: "unset", endedAt: window));

        var traces = await fixture.Repository.GetAgentRecentTracesAsync(
            agent,
            new DateTimeOffset(2025, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero),
            limit: 10);

        var clean = traces.Single(t => t.TraceId == "clean-trace");
        var errored = traces.Single(t => t.TraceId == "error-trace");

        Assert.Equal(0, clean.ErrorCount);
        Assert.Equal(2, clean.SpanCount);
        Assert.Equal(1, errored.ErrorCount);
        Assert.Equal(2, errored.SpanCount);
    }

    [Fact]
    public async Task GetAgentRecentTraces_FinalizedTrace_HasDurationMs()
    {
        using var fixture = new RepositoryFixture();
        var agent = Guid.NewGuid().ToString();
        var window = new DateTimeOffset(2025, 10, 15, 0, 0, 0, TimeSpan.Zero);
        var traceStart = new DateTimeOffset(2025, 10, 14, 12, 0, 0, TimeSpan.Zero);
        var traceEnd = traceStart.AddMilliseconds(3750);

        await fixture.Repository.UpsertTraceStateAsync(MakeTraceState("t1", "finalized", traceStart, traceEnd));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("s1", "t1", agent, endedAt: window));

        var traces = await fixture.Repository.GetAgentRecentTracesAsync(
            agent,
            new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 11, 1, 0, 0, 0, TimeSpan.Zero),
            limit: 10);

        var trace = Assert.Single(traces);
        Assert.Equal(3750L, trace.DurationMs);
    }

    [Fact]
    public async Task GetAgentRecentTraces_OpenTrace_DurationMsIsNull()
    {
        using var fixture = new RepositoryFixture();
        var agent = Guid.NewGuid().ToString();
        var window = new DateTimeOffset(2025, 11, 15, 0, 0, 0, TimeSpan.Zero);
        var traceStart = new DateTimeOffset(2025, 11, 14, 0, 0, 0, TimeSpan.Zero);

        await fixture.Repository.UpsertTraceStateAsync(MakeTraceState("open-t", "open", traceStart, endedAt: null));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("s1", "open-t", agent, endedAt: window));

        var traces = await fixture.Repository.GetAgentRecentTracesAsync(
            agent,
            new DateTimeOffset(2025, 11, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero),
            limit: 10);

        var trace = Assert.Single(traces);
        Assert.Equal("open", trace.Status);
        Assert.Null(trace.DurationMs);
    }

    [Fact]
    public async Task GetAgentRecentTraces_StatusFromTraceState()
    {
        using var fixture = new RepositoryFixture();
        var agent = Guid.NewGuid().ToString();
        var window = new DateTimeOffset(2025, 12, 15, 0, 0, 0, TimeSpan.Zero);
        var tStart = new DateTimeOffset(2025, 12, 14, 0, 0, 0, TimeSpan.Zero);

        await fixture.Repository.UpsertTraceStateAsync(MakeTraceState("open-t",      "open",      tStart));
        await fixture.Repository.UpsertTraceStateAsync(MakeTraceState("finalized-t", "finalized", tStart.AddHours(1), tStart.AddHours(1).AddMinutes(1)));

        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("s1", "open-t",      agent, endedAt: window));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("s2", "finalized-t", agent, endedAt: window));

        var traces = await fixture.Repository.GetAgentRecentTracesAsync(
            agent,
            new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            limit: 10);

        Assert.Equal("open",      traces.Single(t => t.TraceId == "open-t").Status);
        Assert.Equal("finalized", traces.Single(t => t.TraceId == "finalized-t").Status);
    }

    [Fact]
    public async Task GetAgentRecentTraces_DurationIntegrity_ClippedWindowUsesAuthoritativeDates()
    {
        // Trace has started_at / ended_at in trace_state from before the query window.
        // Only one span falls inside the window. Duration must come from trace_state,
        // not from the windowed span subset.
        using var fixture = new RepositoryFixture();
        var agent = Guid.NewGuid().ToString();

        var traceStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var traceEnd   = traceStart.AddMinutes(10);
        await fixture.Repository.UpsertTraceStateAsync(MakeTraceState("t1", "finalized", traceStart, traceEnd));

        // span before window — excluded by query
        await fixture.Repository.UpsertSpanStateAsync(
            MakeSpanState("early", "t1", agent, endedAt: traceStart.AddMinutes(1)));

        // span inside window — the only one the aggregation sees
        var windowSince = traceStart.AddMinutes(5);
        await fixture.Repository.UpsertSpanStateAsync(
            MakeSpanState("late", "t1", agent, endedAt: traceStart.AddMinutes(7)));

        var traces = await fixture.Repository.GetAgentRecentTracesAsync(
            agent,
            windowSince,
            traceStart.AddMinutes(20),
            limit: 10);

        var trace = Assert.Single(traces);
        Assert.Equal(10 * 60 * 1000L, trace.DurationMs);   // full 10-min duration from trace_state
        Assert.Equal(traceStart, trace.StartedAt);
    }

    [Fact]
    public async Task GetAgentRecentTraces_Limit_EnforcedInSql()
    {
        using var fixture = new RepositoryFixture();
        var agent = Guid.NewGuid().ToString();
        var window = new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 5; i++)
        {
            var tStart = new DateTimeOffset(2026, 2, i + 1, 0, 0, 0, TimeSpan.Zero);
            await fixture.Repository.UpsertTraceStateAsync(
                MakeTraceState($"t{i}", "finalized", tStart, tStart.AddMinutes(1)));
            await fixture.Repository.UpsertSpanStateAsync(
                MakeSpanState($"s{i}", $"t{i}", agent, endedAt: window));
        }

        var traces = await fixture.Repository.GetAgentRecentTracesAsync(
            agent,
            new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            limit: 3);

        Assert.Equal(3, traces.Count);
    }

    [Fact]
    public async Task GetAgentRecentTraces_WindowExcludesOutOfWindowTraces()
    {
        using var fixture = new RepositoryFixture();
        var agent = Guid.NewGuid().ToString();
        var windowSince = new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero);
        var windowUntil = new DateTimeOffset(2026, 3, 20, 0, 0, 0, TimeSpan.Zero);

        var insideStart = new DateTimeOffset(2026, 3, 12, 0, 0, 0, TimeSpan.Zero);
        await fixture.Repository.UpsertTraceStateAsync(MakeTraceState("inside",  "finalized", insideStart, insideStart.AddMinutes(1)));
        await fixture.Repository.UpsertSpanStateAsync(
            MakeSpanState("s-in",  "inside",  agent, endedAt: new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero)));

        var outsideStart = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        await fixture.Repository.UpsertTraceStateAsync(MakeTraceState("outside", "finalized", outsideStart, outsideStart.AddMinutes(1)));
        await fixture.Repository.UpsertSpanStateAsync(
            MakeSpanState("s-out", "outside", agent, endedAt: new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero)));

        var traces = await fixture.Repository.GetAgentRecentTracesAsync(agent, windowSince, windowUntil, limit: 10);

        var t = Assert.Single(traces);
        Assert.Equal("inside", t.TraceId);
    }

    [Fact]
    public async Task GetAgentRecentTraces_PlaceholderSpans_NotIncluded()
    {
        using var fixture = new RepositoryFixture();
        var agent = Guid.NewGuid().ToString();
        var window = new DateTimeOffset(2026, 4, 15, 0, 0, 0, TimeSpan.Zero);
        var tStart = new DateTimeOffset(2026, 4, 14, 0, 0, 0, TimeSpan.Zero);

        await fixture.Repository.UpsertTraceStateAsync(MakeTraceState("real-trace",        "finalized", tStart,             tStart.AddMinutes(1), agent));
        await fixture.Repository.UpsertTraceStateAsync(MakeTraceState("placeholder-trace", "finalized", tStart.AddHours(1), tStart.AddHours(1).AddMinutes(1)));

        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("r1", "real-trace",        agent,        endedAt: window));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("p1", "placeholder-trace", agentId: null, endedAt: window));

        var traces = await fixture.Repository.GetAgentRecentTracesAsync(
            agent,
            new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            limit: 10);

        var t = Assert.Single(traces);
        Assert.Equal("real-trace", t.TraceId);
    }

    // -------------------------------------------------------------------------
    // Endpoint — GET /v1/agents/{id}
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetAgentDetail_NonGuidId_Returns400()
    {
        using var fixture = new CollectorFixture();
        using var response = await fixture.CreateClient().GetAsync(
            "/v1/agents/not-a-guid?since=2025-01-01T00:00:00Z&until=2025-02-01T00:00:00Z");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetAgentDetail_UnparseableSince_Returns400()
    {
        using var fixture = new CollectorFixture();
        var id = Guid.NewGuid();
        using var response = await fixture.CreateClient().GetAsync($"/v1/agents/{id}?since=not-a-date");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("since", body.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task GetAgentDetail_UnparseableUntil_Returns400()
    {
        using var fixture = new CollectorFixture();
        var id = Guid.NewGuid();
        using var response = await fixture.CreateClient().GetAsync($"/v1/agents/{id}?until=not-a-date");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("until", body.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task GetAgentDetail_SinceEqualsUntil_Returns400()
    {
        using var fixture = new CollectorFixture();
        var id = Guid.NewGuid();
        using var response = await fixture.CreateClient().GetAsync(
            $"/v1/agents/{id}?since=2025-01-01T00:00:00Z&until=2025-01-01T00:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetAgentDetail_SinceAfterUntil_Returns400()
    {
        using var fixture = new CollectorFixture();
        var id = Guid.NewGuid();
        using var response = await fixture.CreateClient().GetAsync(
            $"/v1/agents/{id}?since=2025-02-01T00:00:00Z&until=2025-01-01T00:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetAgentDetail_LimitNonNumeric_Returns400()
    {
        using var fixture = new CollectorFixture();
        var id = Guid.NewGuid();
        using var response = await fixture.CreateClient().GetAsync(
            $"/v1/agents/{id}?since=2025-01-01T00:00:00Z&until=2025-02-01T00:00:00Z&limit=abc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task GetAgentDetail_LimitZeroOrNegative_Returns400(int badLimit)
    {
        using var fixture = new CollectorFixture();
        var id = Guid.NewGuid();
        using var response = await fixture.CreateClient().GetAsync(
            $"/v1/agents/{id}?since=2025-01-01T00:00:00Z&until=2025-02-01T00:00:00Z&limit={badLimit}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetAgentDetail_UnknownAgent_Returns200WithZeroedRollupAndEmptyTraces()
    {
        using var fixture = new CollectorFixture();
        var unknownId = Guid.NewGuid();
        using var response = await fixture.CreateClient().GetAsync(
            $"/v1/agents/{unknownId}?since=2020-01-01T00:00:00Z&until=2020-02-01T00:00:00Z");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(unknownId.ToString(), body.RootElement.GetProperty("agent_id").GetString());

        var rollup = body.RootElement.GetProperty("rollup");
        Assert.Equal(0, rollup.GetProperty("span_count").GetInt64());
        Assert.Equal(0, rollup.GetProperty("trace_count").GetInt64());
        Assert.Equal(0, rollup.GetProperty("error_count").GetInt64());
        Assert.Equal(0.0, rollup.GetProperty("error_rate").GetDouble());
        Assert.Equal(JsonValueKind.Null, rollup.GetProperty("last_seen").ValueKind);
        Assert.Equal(0.0, rollup.GetProperty("conformance_ratio").GetDouble());
        Assert.Equal(0, body.RootElement.GetProperty("recent_traces").GetArrayLength());
    }

    [Fact]
    public async Task GetAgentDetail_LimitGreaterThan200_ClampedTo200NotAnError()
    {
        using var fixture = new CollectorFixture();
        var id = Guid.NewGuid();
        using var response = await fixture.CreateClient().GetAsync(
            $"/v1/agents/{id}?since=2020-01-01T00:00:00Z&until=2020-02-01T00:00:00Z&limit=999");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Array, body.RootElement.GetProperty("recent_traces").ValueKind);
    }

    [Fact]
    public async Task GetAgentDetail_ExplicitLimit_CapsRecentTraces()
    {
        using var fixture = new CollectorFixture();
        var agent = Guid.NewGuid();
        var window = new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 4; i++)
        {
            var tStart = new DateTimeOffset(2026, 5, i + 1, 0, 0, 0, TimeSpan.Zero);
            await fixture.Repository.UpsertTraceStateAsync(
                MakeTraceState($"t{i}", "finalized", tStart, tStart.AddMinutes(1)));
            await fixture.Repository.UpsertSpanStateAsync(
                MakeSpanState($"s{i}", $"t{i}", agent.ToString(), endedAt: window));
        }

        using var response = await fixture.CreateClient().GetAsync(
            $"/v1/agents/{agent}?since=2026-05-01T00:00:00Z&until=2026-06-01T00:00:00Z&limit=2");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, body.RootElement.GetProperty("recent_traces").GetArrayLength());
    }

    [Fact]
    public async Task GetAgentDetail_DefaultLimit50_CapsRecentTraces()
    {
        using var fixture = new CollectorFixture();
        var agent = Guid.NewGuid();
        var window = new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 51; i++)
        {
            var tStart = new DateTimeOffset(2026, 8, 1, 0, i, 0, TimeSpan.Zero);
            await fixture.Repository.UpsertTraceStateAsync(
                MakeTraceState($"trace-{i}", "finalized", tStart, tStart.AddMinutes(1)));
            await fixture.Repository.UpsertSpanStateAsync(
                MakeSpanState($"span-{i}", $"trace-{i}", agent.ToString(), endedAt: window));
        }

        using var response = await fixture.CreateClient().GetAsync(
            $"/v1/agents/{agent}?since=2026-08-01T00:00:00Z&until=2026-09-01T00:00:00Z");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(50, body.RootElement.GetProperty("recent_traces").GetArrayLength());
    }

    [Fact]
    public async Task GetAgentDetail_DefaultWindow_Trailing30Days_DeterministicWithFakeTime()
    {
        var fixedNow = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
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
            var repo = factory.Services.GetRequiredService<EventRepository>();
            var agent = Guid.NewGuid();

            await repo.UpsertSpanStateAsync(
                MakeSpanState("in",  "t1", agent.ToString(), endedAt: fixedNow.AddDays(-1)));   // inside
            await repo.UpsertSpanStateAsync(
                MakeSpanState("out", "t2", agent.ToString(), endedAt: fixedNow.AddDays(-31)));  // outside

            using var response = await factory.CreateClient().GetAsync($"/v1/agents/{agent}");
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, body.RootElement.GetProperty("rollup").GetProperty("span_count").GetInt64());
        }

        if (File.Exists(dbPath)) File.Delete(dbPath);
    }

    [Fact]
    public async Task GetAgentDetail_HappyPath_RollupAndRecentTracesAssembled()
    {
        using var fixture = new CollectorFixture();
        var agent = Guid.NewGuid();
        var window = new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);
        var t1Start = new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);
        var t2Start = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

        await fixture.Repository.UpsertTraceStateAsync(MakeTraceState("t1", "finalized", t1Start, t1Start.AddMinutes(2)));
        await fixture.Repository.UpsertTraceStateAsync(MakeTraceState("t2", "finalized", t2Start, t2Start.AddMinutes(3)));

        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("s1", "t1", agent.ToString(), status: "error", endedAt: window));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("s2", "t1", agent.ToString(), status: "unset", endedAt: window));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState("s3", "t2", agent.ToString(), status: "unset", endedAt: window));

        using var response = await fixture.CreateClient().GetAsync(
            $"/v1/agents/{agent}?since=2026-07-01T00:00:00Z&until=2026-08-01T00:00:00Z");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(agent.ToString(), body.RootElement.GetProperty("agent_id").GetString());

        var rollup = body.RootElement.GetProperty("rollup");
        Assert.Equal(3, rollup.GetProperty("span_count").GetInt64());
        Assert.Equal(2, rollup.GetProperty("trace_count").GetInt64());
        Assert.Equal(1, rollup.GetProperty("error_count").GetInt64());

        var recent = body.RootElement.GetProperty("recent_traces");
        Assert.Equal(2, recent.GetArrayLength());
        Assert.Equal("t2", recent[0].GetProperty("trace_id").GetString());   // most recent first
        Assert.Equal("t1", recent[1].GetProperty("trace_id").GetString());
        Assert.Equal(1, recent[1].GetProperty("error_count").GetInt64());
        Assert.Equal(2 * 60 * 1000L, recent[1].GetProperty("duration_ms").GetInt64());
    }
}
