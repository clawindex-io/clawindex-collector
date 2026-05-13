using System.Diagnostics;
using Clawindex.Collector.Api.Persistence;
using Clawindex.Collector.Api.Projection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clawindex.Collector.Tests;

public sealed class OtelEventMapperTests : IDisposable
{
    private readonly List<Activity> _stoppedActivities = [];
    private readonly ActivityListener _listener;

    public OtelEventMapperTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ClawindexTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _stoppedActivities.Add(activity)
        };

        ActivitySource.AddActivityListener(_listener);
    }

    [Fact]
    public async Task Project_CreatesToolSpanUnderTaskSpan_WithPolicySpanEvent()
    {
        using var context = await TestMapperContext.CreateAsync();

        await ProjectAllAsync(context, HappyPath());

        var toolSpan = Assert.Single(_stoppedActivities, activity => activity.DisplayName.StartsWith("tool.call", StringComparison.Ordinal));
        var taskSpan = Assert.Single(_stoppedActivities, activity => activity.DisplayName.StartsWith("agent.task", StringComparison.Ordinal));

        Assert.Equal(taskSpan.TraceId, toolSpan.TraceId);
        Assert.Equal(taskSpan.SpanId, toolSpan.ParentSpanId);
        Assert.Contains(toolSpan.Events, activityEvent => activityEvent.Name == "policy.evaluated");
        Assert.Equal("trace_abc", toolSpan.GetTagItem("clawindex.trace_id"));
        Assert.Equal("calculate_recommendation", toolSpan.GetTagItem("gen_ai.tool.name"));
    }

    [Fact]
    public async Task Project_PreservesValidIncomingTraceIdAsActivityTraceId()
    {
        using var context = await TestMapperContext.CreateAsync();
        const string traceId = "4bf92f3577b34da6a3ce929d0e0e4736";

        await ProjectAllAsync(context,
        [
            Event("evt_task_start", "agent.task.started", traceId: traceId, taskId: "task_456"),
            Event("evt_task_done", "agent.task.completed", traceId: traceId, taskId: "task_456")
        ]);

        var taskSpan = Assert.Single(_stoppedActivities);
        Assert.Equal(traceId, taskSpan.TraceId.ToString());
        Assert.Equal(traceId, taskSpan.GetTagItem("clawindex.trace_id"));
    }

    [Fact]
    public async Task Project_FailurePath_MarksToolAndTaskSpansAsError()
    {
        using var context = await TestMapperContext.CreateAsync();

        await ProjectAllAsync(context, FailurePath());

        var toolSpan = Assert.Single(_stoppedActivities, activity => activity.DisplayName.StartsWith("tool.call", StringComparison.Ordinal));
        var taskSpan = Assert.Single(_stoppedActivities, activity => activity.DisplayName.StartsWith("agent.task", StringComparison.Ordinal));

        Assert.Equal(ActivityStatusCode.Error, toolSpan.Status);
        Assert.Equal(ActivityStatusCode.Error, taskSpan.Status);
        Assert.Equal(taskSpan.TraceId, toolSpan.TraceId);
        Assert.Equal(taskSpan.SpanId, toolSpan.ParentSpanId);
    }

    [Fact]
    public async Task RootSpan_Persists()
    {
        using var context = await TestMapperContext.CreateAsync();
        var started = Event("evt_task_start", "agent.task.started", taskId: "task_456");

        await context.InsertAndProjectAsync(started);

        var trace = await context.Repository.GetTraceStateAsync("trace_abc");
        Assert.NotNull(trace);
        Assert.Equal("open", trace.Status);

        var root = await context.Repository.GetSpanStateAsync(trace.RootSpanId);
        Assert.NotNull(root);
        Assert.Equal("agent.task", root.SpanKind);
        Assert.Equal("open", root.Status);
        Assert.Equal("evt_task_start", root.SourceStartEventId);
    }

    [Fact]
    public async Task ChildSpan_Persists()
    {
        using var context = await TestMapperContext.CreateAsync();

        await ProjectAllAsync(context,
        [
            Event("evt_task_start", "agent.task.started", taskId: "task_456"),
            Event("evt_tool_start", "tool.call.started", taskId: "task_456", spanId: "span_tool_001")
        ]);

        var spans = await context.Repository.GetTraceSpansAsync("trace_abc");
        var root = Assert.Single(spans, span => span.SpanKind == "agent.task");
        var child = Assert.Single(spans, span => span.SpanKind == "tool.call");

        Assert.Equal(root.SpanId, child.ParentSpanId);
        Assert.Equal("open", child.Status);
        Assert.Equal("evt_tool_start", child.SourceStartEventId);
    }

    [Fact]
    public async Task TaskCompletion_ClosesPersistedRootSpan()
    {
        using var context = await TestMapperContext.CreateAsync();

        await ProjectAllAsync(context,
        [
            Event("evt_task_start", "agent.task.started", taskId: "task_456"),
            Event("evt_task_done", "agent.task.completed", taskId: "task_456")
        ]);

        var trace = await context.Repository.GetTraceStateAsync("trace_abc");
        Assert.NotNull(trace);
        Assert.Equal("completed", trace.Status);
        Assert.NotNull(trace.EndedAt);

        var root = await context.Repository.GetSpanStateAsync(trace.RootSpanId);
        Assert.NotNull(root);
        Assert.Equal("completed", root.Status);
        Assert.Equal("evt_task_done", root.SourceEndEventId);
        Assert.NotNull(root.EndedAt);
    }

    [Fact]
    public async Task ToolFailure_MarksPersistedSpanError()
    {
        using var context = await TestMapperContext.CreateAsync();

        await ProjectAllAsync(context,
        [
            Event("evt_task_start", "agent.task.started", taskId: "task_456"),
            Event("evt_tool_start", "tool.call.started", taskId: "task_456", spanId: "span_tool_001"),
            Event("evt_tool_failed", "tool.call.failed", taskId: "task_456", spanId: "span_tool_001")
        ]);

        var spans = await context.Repository.GetTraceSpansAsync("trace_abc");
        var child = Assert.Single(spans, span => span.SpanKind == "tool.call");
        Assert.Equal("error", child.Status);
        Assert.Equal("evt_tool_failed", child.SourceEndEventId);
    }

    [Fact]
    public async Task RestartRecovery_ReloadsOpenSpans_AndAttachesNewEvents()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"clawindex-recovery-{Guid.NewGuid():N}.db");
        try
        {
            using (var first = await TestMapperContext.CreateAsync(databasePath, deleteOnDispose: false))
            {
                await first.InsertAndProjectAsync(Event("evt_task_start", "agent.task.started", taskId: "task_456"));
            }

            using var recovered = await TestMapperContext.CreateAsync(databasePath, deleteOnDispose: false);
            var openTraces = await recovered.Repository.GetOpenTraceStatesAsync();
            var openSpans = await recovered.Repository.GetOpenSpanStatesAsync();

            var trace = Assert.Single(openTraces);
            var root = Assert.Single(openSpans);
            Assert.Equal(trace.RootSpanId, root.SpanId);

            await recovered.InsertAndProjectAsync(Event("evt_tool_start", "tool.call.started", taskId: "task_456", spanId: "span_tool_001"));

            var spans = await recovered.Repository.GetTraceSpansAsync("trace_abc");
            var child = Assert.Single(spans, span => span.SpanKind == "tool.call");
            Assert.Equal(root.SpanId, child.ParentSpanId);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task DuplicateEventId_DoesNotDuplicateSpanState()
    {
        using var context = await TestMapperContext.CreateAsync();
        var started = Event("evt_task_start", "agent.task.started", taskId: "task_456");

        await context.InsertAndProjectAsync(started);
        var retry = await context.Mapper.ProjectAsync(started);

        Assert.True(retry.Succeeded, retry.Error);
        var spans = await context.Repository.GetTraceSpansAsync("trace_abc");
        Assert.Single(spans);
        var mapping = await context.Repository.GetEventSpanMapAsync("evt_task_start");
        Assert.NotNull(mapping);
        Assert.Equal("source_start", mapping.RelationshipType);
    }

    [Fact]
    public async Task DuplicateCompletion_DoesNotCorruptClosedSpan()
    {
        using var context = await TestMapperContext.CreateAsync();

        await ProjectAllAsync(context,
        [
            Event("evt_task_start", "agent.task.started", taskId: "task_456"),
            Event("evt_task_done", "agent.task.completed", taskId: "task_456"),
            Event("evt_task_done_duplicate", "agent.task.completed", taskId: "task_456")
        ]);

        var trace = await context.Repository.GetTraceStateAsync("trace_abc");
        Assert.NotNull(trace);
        Assert.Equal("completed", trace.Status);

        var root = await context.Repository.GetSpanStateAsync(trace.RootSpanId);
        Assert.NotNull(root);
        Assert.Equal("completed", root.Status);
        Assert.Equal("evt_task_done", root.SourceEndEventId);

        var duplicateMapping = await context.Repository.GetEventSpanMapAsync("evt_task_done_duplicate");
        Assert.NotNull(duplicateMapping);
        Assert.Equal("duplicate_end", duplicateMapping.RelationshipType);
    }

    [Fact]
    public async Task OutOfOrderCompletion_CreatesClosedPlaceholderSafely()
    {
        using var context = await TestMapperContext.CreateAsync();

        await context.InsertAndProjectAsync(Event("evt_tool_done", "tool.call.completed", taskId: "task_456", spanId: "span_tool_001"));

        var spans = await context.Repository.GetTraceSpansAsync("trace_abc");
        var root = Assert.Single(spans, span => span.SpanKind == "agent.task");
        var child = Assert.Single(spans, span => span.SpanKind == "tool.call");

        Assert.Equal("open", root.Status);
        Assert.Equal(root.SpanId, child.ParentSpanId);
        Assert.Equal("completed", child.Status);
        Assert.Equal("evt_tool_done", child.SourceEndEventId);
    }

    [Fact]
    public async Task OrphanPolicyEvent_IsPreservedOnPlaceholderSpan()
    {
        using var context = await TestMapperContext.CreateAsync();

        await context.InsertAndProjectAsync(Event("evt_policy", "policy.evaluated", taskId: "task_456"));

        var trace = await context.Repository.GetTraceStateAsync("trace_abc");
        Assert.NotNull(trace);

        var root = await context.Repository.GetSpanStateAsync(trace.RootSpanId);
        Assert.NotNull(root);
        Assert.Equal("open", root.Status);

        var mapping = await context.Repository.GetEventSpanMapAsync("evt_policy");
        Assert.NotNull(mapping);
        Assert.Equal(root.SpanId, mapping.SpanId);
        Assert.Equal("span_event", mapping.RelationshipType);
    }

    public void Dispose()
    {
        _listener.Dispose();
    }

    private static async Task ProjectAllAsync(TestMapperContext context, IEnumerable<AcceptedEvent> events)
    {
        foreach (var acceptedEvent in events)
        {
            await context.Repository.InsertAsync(acceptedEvent);
        }

        foreach (var acceptedEvent in events)
        {
            var result = await context.Mapper.ProjectAsync(acceptedEvent);
            Assert.True(result.Succeeded, result.Error);
        }
    }

    private static AcceptedEvent Event(
        string eventId,
        string eventType,
        string traceId = "trace_abc",
        string? taskId = null,
        string? spanId = null)
    {
        var payload = eventType.StartsWith("tool.call", StringComparison.Ordinal)
            ? """{"tool_name":"calculate_recommendation","side_effect_type":"none"}"""
            : """{"task_name":"Generate soil report","decision":"deny","policy_id":"soil-data-scope"}""";

        return new AcceptedEvent(
            eventId,
            "0.1.0",
            eventType,
            DateTimeOffset.Parse("2026-05-11T22:15:00Z"),
            DateTimeOffset.Parse("2026-05-11T22:15:01Z"),
            "test-agent",
            "test-component",
            "0.1.0",
            traceId,
            spanId,
            taskId,
            "agent_soil_report",
            "session_789",
            "{}",
            payload);
    }

    private static IEnumerable<AcceptedEvent> HappyPath()
    {
        yield return Event("evt_task_start", "agent.task.started", taskId: "task_456");
        yield return Event("evt_tool_start", "tool.call.started", taskId: "task_456", spanId: "span_tool_001");
        yield return Event("evt_policy", "policy.evaluated", taskId: "task_456");
        yield return Event("evt_tool_done", "tool.call.completed", taskId: "task_456", spanId: "span_tool_001");
        yield return Event("evt_task_done", "agent.task.completed", taskId: "task_456");
    }

    private static IEnumerable<AcceptedEvent> FailurePath()
    {
        yield return Event("evt_task_start", "agent.task.started", taskId: "task_456");
        yield return Event("evt_tool_start", "tool.call.started", taskId: "task_456", spanId: "span_tool_001");
        yield return Event("evt_tool_failed", "tool.call.failed", taskId: "task_456", spanId: "span_tool_001");
        yield return Event("evt_task_failed", "agent.task.failed", taskId: "task_456");
    }

    private static void DeleteDatabase(string databasePath)
    {
        foreach (var path in new[] { databasePath, $"{databasePath}-shm", $"{databasePath}-wal" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class TestMapperContext : IDisposable
    {
        private readonly bool _deleteOnDispose;

        private TestMapperContext(string databasePath, EventRepository repository, OtelEventMapper mapper, bool deleteOnDispose)
        {
            DatabasePath = databasePath;
            Repository = repository;
            Mapper = mapper;
            _deleteOnDispose = deleteOnDispose;
        }

        public string DatabasePath { get; }

        public EventRepository Repository { get; }

        public OtelEventMapper Mapper { get; }

        public static async Task<TestMapperContext> CreateAsync(string? databasePath = null, bool deleteOnDispose = true)
        {
            databasePath ??= Path.Combine(Path.GetTempPath(), $"clawindex-mapper-tests-{Guid.NewGuid():N}.db");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Clawindex:DatabasePath"] = databasePath
                })
                .Build();
            var repository = new EventRepository(configuration);
            await repository.InitializeAsync();
            var mapper = new OtelEventMapper(repository, NullLogger<OtelEventMapper>.Instance);

            return new TestMapperContext(databasePath, repository, mapper, deleteOnDispose);
        }

        public async Task InsertAndProjectAsync(AcceptedEvent acceptedEvent)
        {
            await Repository.InsertAsync(acceptedEvent);
            var result = await Mapper.ProjectAsync(acceptedEvent);
            Assert.True(result.Succeeded, result.Error);
        }

        public void Dispose()
        {
            if (_deleteOnDispose)
            {
                DeleteDatabase(DatabasePath);
            }
        }
    }
}
