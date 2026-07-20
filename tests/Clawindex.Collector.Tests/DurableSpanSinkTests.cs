using Clawindex.Collector.Api.Otlp;
using Clawindex.Collector.Api.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clawindex.Collector.Tests;

public sealed class DurableSpanSinkTests
{
    private static ValidatedSpan MakeSpan(
        string spanId,
        string traceId,
        string? parentSpanId = null,
        string name = "test.span",
        bool isConformant = true,
        bool isComplete = true,
        string otlpStatus = "unset",
        string? operation = "chat",
        string? provider = "openai",
        string? model = "gpt-4o",
        long? inputTokens = 100,
        long? outputTokens = 50,
        string? agentId = null,
        DateTimeOffset? startTime = null,
        DateTimeOffset? endTime = null) =>
        new(
            TraceId: traceId,
            SpanId: spanId,
            ParentSpanId: parentSpanId,
            Name: name,
            Kind: 1,
            StartTime: startTime ?? DateTimeOffset.UtcNow.AddSeconds(-1),
            EndTime: endTime ?? DateTimeOffset.UtcNow,
            Operation: isConformant ? operation : null,
            Provider: isConformant ? provider : null,
            Model: isConformant ? model : null,
            InputTokens: isConformant ? inputTokens : null,
            OutputTokens: isConformant ? outputTokens : null,
            AgentId: isConformant ? (agentId ?? "svc-default-agent") : null,
            IsConformant: isConformant,
            IsComplete: isComplete,
            OtlpStatus: otlpStatus,
            RawAttributes: [
                new("gen_ai.operation.name", operation ?? ""),
                new("gen_ai.provider.name", provider ?? ""),
                new("gen_ai.request.model", model ?? "")
            ]);

    private sealed class DurableSinkFixture : IDisposable
    {
        private readonly string _dbPath;
        public EventRepository Repository { get; }
        public DurableSpanSink Sink { get; }

        public DurableSinkFixture()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"clawindex-test-{Guid.NewGuid():N}.db");
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Clawindex:DatabasePath"] = _dbPath })
                .Build();
            Repository = new EventRepository(config);
            Repository.InitializeAsync().GetAwaiter().GetResult();
            Sink = new DurableSpanSink(Repository, NullLogger<DurableSpanSink>.Instance);
        }

        public void Dispose()
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task ConformantSpan_ProjectsCorrectly()
    {
        using var fixture = new DurableSinkFixture();
        var agentId = "svc-chat-prod-v1";
        var span = MakeSpan("aaaa0000000000aa", "bbbb0000000000000000000000000000", agentId: agentId);

        await fixture.Sink.AcceptAsync([span]);

        var row = await fixture.Repository.GetSpanStateAsync("aaaa0000000000aa");
        Assert.NotNull(row);
        Assert.Equal("aaaa0000000000aa", row.SpanId);
        Assert.Equal("bbbb0000000000000000000000000000", row.TraceId);
        Assert.Equal("chat", row.Operation);
        Assert.Equal("openai", row.Provider);
        Assert.Equal("gpt-4o", row.Model);
        Assert.Equal(100, row.InputTokens);
        Assert.Equal(50, row.OutputTokens);
        Assert.Equal(agentId, row.AgentId);
        Assert.True(row.IsConformant);
        Assert.Equal("unset", row.Status);

        var trace = await fixture.Repository.GetTraceStateAsync("bbbb0000000000000000000000000000");
        Assert.NotNull(trace);
        Assert.Equal("bbbb0000000000000000000000000000", trace.TraceId);
    }

    [Fact]
    public async Task SameSpanId_Twice_IsIdempotent()
    {
        using var fixture = new DurableSinkFixture();
        var span = MakeSpan("aaaa0000000000aa", "bbbb0000000000000000000000000000");

        await fixture.Sink.AcceptAsync([span]);
        await fixture.Sink.AcceptAsync([span]);

        var spans = await fixture.Repository.GetTraceSpansAsync("bbbb0000000000000000000000000000");
        Assert.Single(spans, s => s.Status != "placeholder");
    }

    [Fact]
    public async Task ChildBeforeParent_PlaceholderCreated()
    {
        using var fixture = new DurableSinkFixture();
        var child = MakeSpan("cccc0000000000cc", "bbbb0000000000000000000000000000",
            parentSpanId: "pppp0000000000pp");

        await fixture.Sink.AcceptAsync([child]);

        var spans = await fixture.Repository.GetTraceSpansAsync("bbbb0000000000000000000000000000");
        Assert.Equal(2, spans.Count);

        var placeholder = spans.FirstOrDefault(s => s.SpanId == "pppp0000000000pp");
        Assert.NotNull(placeholder);
        Assert.Equal("placeholder", placeholder.Status);

        var realChild = spans.FirstOrDefault(s => s.SpanId == "cccc0000000000cc");
        Assert.NotNull(realChild);
        Assert.NotEqual("placeholder", realChild.Status);
    }

    [Fact]
    public async Task ChildBeforeParent_RealParentArrives_PlaceholderReplaced()
    {
        using var fixture = new DurableSinkFixture();
        var child = MakeSpan("cccc0000000000cc", "bbbb0000000000000000000000000000",
            parentSpanId: "pppp0000000000pp");
        await fixture.Sink.AcceptAsync([child]);

        var parent = MakeSpan("pppp0000000000pp", "bbbb0000000000000000000000000000",
            operation: "chat", provider: "anthropic", model: "claude-3-5-sonnet");
        await fixture.Sink.AcceptAsync([parent]);

        var spans = await fixture.Repository.GetTraceSpansAsync("bbbb0000000000000000000000000000");
        Assert.Equal(2, spans.Count);

        var parentRow = spans.First(s => s.SpanId == "pppp0000000000pp");
        Assert.NotEqual("placeholder", parentRow.Status);
        Assert.Equal("anthropic", parentRow.Provider);
        Assert.Equal("claude-3-5-sonnet", parentRow.Model);

        var childRow = spans.First(s => s.SpanId == "cccc0000000000cc");
        Assert.Equal("pppp0000000000pp", childRow.ParentSpanId);
    }

    [Fact]
    public async Task ParentBeforeChild_NoPlaceholder()
    {
        using var fixture = new DurableSinkFixture();
        var root = MakeSpan("rrrr0000000000rr", "bbbb0000000000000000000000000000");
        var child = MakeSpan("cccc0000000000cc", "bbbb0000000000000000000000000000",
            parentSpanId: "rrrr0000000000rr");

        await fixture.Sink.AcceptAsync([root]);
        await fixture.Sink.AcceptAsync([child]);

        var spans = await fixture.Repository.GetTraceSpansAsync("bbbb0000000000000000000000000000");
        Assert.Equal(2, spans.Count);
        Assert.DoesNotContain(spans, s => s.Status == "placeholder");
    }

    [Fact]
    public async Task RootSpan_FinalizesTrace()
    {
        using var fixture = new DurableSinkFixture();
        var endTime = DateTimeOffset.UtcNow;
        var root = MakeSpan("rrrr0000000000rr", "bbbb0000000000000000000000000000",
            endTime: endTime);

        await fixture.Sink.AcceptAsync([root]);

        var trace = await fixture.Repository.GetTraceStateAsync("bbbb0000000000000000000000000000");
        Assert.NotNull(trace);
        Assert.Equal("finalized", trace.Status);
        Assert.Equal("rrrr0000000000rr", trace.RootSpanId);
        Assert.NotNull(trace.EndedAt);
        Assert.InRange(
            trace.EndedAt.Value.ToUniversalTime().Ticks,
            endTime.ToUniversalTime().Ticks - TimeSpan.TicksPerMillisecond,
            endTime.ToUniversalTime().Ticks + TimeSpan.TicksPerMillisecond);
    }

    [Fact]
    public async Task NonRootSpan_TraceRemainsOpen()
    {
        using var fixture = new DurableSinkFixture();
        var child = MakeSpan("cccc0000000000cc", "bbbb0000000000000000000000000000",
            parentSpanId: "pppp0000000000pp");

        await fixture.Sink.AcceptAsync([child]);

        var trace = await fixture.Repository.GetTraceStateAsync("bbbb0000000000000000000000000000");
        Assert.NotNull(trace);
        Assert.Equal("open", trace.Status);
        Assert.Null(trace.EndedAt);
        Assert.Null(trace.RootSpanId);
    }

    [Fact]
    public async Task NonConformantSpan_StoredWithFlag()
    {
        using var fixture = new DurableSinkFixture();
        var span = MakeSpan("aaaa0000000000aa", "bbbb0000000000000000000000000000",
            isConformant: false);

        await fixture.Sink.AcceptAsync([span]);

        var row = await fixture.Repository.GetSpanStateAsync("aaaa0000000000aa");
        Assert.NotNull(row);
        Assert.False(row.IsConformant);
        Assert.Null(row.Operation);
        Assert.Null(row.Provider);
        Assert.Null(row.AgentId);
    }

    [Fact]
    public async Task MultiSpanTrace_TreeConnected_FinalizedOnRoot()
    {
        using var fixture = new DurableSinkFixture();
        var traceId = "tttt0000000000000000000000000000";
        var root = MakeSpan("rrrr0000000000rr", traceId);
        var child1 = MakeSpan("c1c10000000000c1", traceId, parentSpanId: "rrrr0000000000rr");
        var child2 = MakeSpan("c2c20000000000c2", traceId, parentSpanId: "rrrr0000000000rr");

        await fixture.Sink.AcceptAsync([child1, child2, root]);

        var spans = await fixture.Repository.GetTraceSpansAsync(traceId);
        Assert.Equal(3, spans.Count);
        Assert.DoesNotContain(spans, s => s.Status == "placeholder");

        var rootRow = spans.First(s => s.SpanId == "rrrr0000000000rr");
        Assert.Null(rootRow.ParentSpanId);

        var trace = await fixture.Repository.GetTraceStateAsync(traceId);
        Assert.NotNull(trace);
        Assert.Equal("finalized", trace.Status);
        Assert.Equal("rrrr0000000000rr", trace.RootSpanId);
    }

    [Fact]
    public async Task IncompleteSpan_Dropped()
    {
        using var fixture = new DurableSinkFixture();
        var span = MakeSpan("aaaa0000000000aa", "bbbb0000000000000000000000000000",
            isComplete: false);

        await fixture.Sink.AcceptAsync([span]);

        var row = await fixture.Repository.GetSpanStateAsync("aaaa0000000000aa");
        Assert.Null(row);
    }

    [Fact]
    public async Task PlaceholderNeverOverwritesRealSpan()
    {
        using var fixture = new DurableSinkFixture();
        var parent = MakeSpan("pppp0000000000pp", "bbbb0000000000000000000000000000",
            operation: "chat", provider: "openai", model: "gpt-4o");
        await fixture.Sink.AcceptAsync([parent]);

        var child = MakeSpan("cccc0000000000cc", "bbbb0000000000000000000000000000",
            parentSpanId: "pppp0000000000pp");
        await fixture.Sink.AcceptAsync([child]);

        var parentRow = await fixture.Repository.GetSpanStateAsync("pppp0000000000pp");
        Assert.NotNull(parentRow);
        Assert.NotEqual("placeholder", parentRow.Status);
        Assert.Equal("openai", parentRow.Provider);
        Assert.Equal("gpt-4o", parentRow.Model);
    }

    [Fact]
    public async Task OtlpStatus_MappedToSpanState()
    {
        using var fixture = new DurableSinkFixture();
        var span = MakeSpan("aaaa0000000000aa", "bbbb0000000000000000000000000000",
            otlpStatus: "error");

        await fixture.Sink.AcceptAsync([span]);

        var row = await fixture.Repository.GetSpanStateAsync("aaaa0000000000aa");
        Assert.NotNull(row);
        Assert.Equal("error", row.Status);
    }

    [Fact]
    public async Task RootFirst_ThenLateChild_TraceStaysFinalized()
    {
        using var fixture = new DurableSinkFixture();
        var traceId = "ffff0000000000000000000000000000";
        var root = MakeSpan("rrrr0000000000rr", traceId);
        await fixture.Sink.AcceptAsync([root]);

        var lateChild = MakeSpan("cccc0000000000cc", traceId, parentSpanId: "rrrr0000000000rr");
        await fixture.Sink.AcceptAsync([lateChild]);

        var trace = await fixture.Repository.GetTraceStateAsync(traceId);
        Assert.NotNull(trace);
        Assert.Equal("finalized", trace.Status);
        Assert.Equal("rrrr0000000000rr", trace.RootSpanId);
        Assert.NotNull(trace.EndedAt);
    }

    [Fact]
    public async Task RealSpan_Redelivered_FirstWins_IsImmutable()
    {
        using var fixture = new DurableSinkFixture();
        var traceId = "eeee0000000000000000000000000000";

        var first = MakeSpan("aaaa0000000000aa", traceId, otlpStatus: "unset", model: "gpt-4o");
        await fixture.Sink.AcceptAsync([first]);

        var redelivered = MakeSpan("aaaa0000000000aa", traceId, otlpStatus: "error", model: "claude-3-5-sonnet");
        await fixture.Sink.AcceptAsync([redelivered]);

        var row = await fixture.Repository.GetSpanStateAsync("aaaa0000000000aa");
        Assert.NotNull(row);
        Assert.Equal("unset", row.Status);
        Assert.Equal("gpt-4o", row.Model);
    }

    // Regression test for issue #55: span_state and trace_state were dropped on every
    // call to InitializeAsync, destroying all persisted telemetry on restart.
    [Fact]
    public async Task SpansAndTraces_SurviveRepositoryRestart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"clawindex-restart-test-{Guid.NewGuid():N}.db");
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Clawindex:DatabasePath"] = dbPath })
                .Build();

            // First lifetime — write spans
            var repo1 = new EventRepository(config);
            await repo1.InitializeAsync();
            var sink1 = new DurableSpanSink(repo1, NullLogger<DurableSpanSink>.Instance);

            var span = MakeSpan("aaaa0000000000aa", "bbbb0000000000000000000000000000",
                agentId: "svc-durability-check");
            await sink1.AcceptAsync([span]);

            var beforeRestart = await repo1.GetSpanStateAsync("aaaa0000000000aa");
            Assert.NotNull(beforeRestart);

            // Second lifetime — simulate restart by calling InitializeAsync on the same DB
            var repo2 = new EventRepository(config);
            await repo2.InitializeAsync();

            var afterRestart = await repo2.GetSpanStateAsync("aaaa0000000000aa");
            Assert.NotNull(afterRestart);
            Assert.Equal("aaaa0000000000aa", afterRestart.SpanId);
            Assert.Equal("svc-durability-check", afterRestart.AgentId);

            var trace = await repo2.GetTraceStateAsync("bbbb0000000000000000000000000000");
            Assert.NotNull(trace);
            Assert.Equal("finalized", trace.Status);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
