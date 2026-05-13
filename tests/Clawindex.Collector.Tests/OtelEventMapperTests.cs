using System.Diagnostics;
using Clawindex.Collector.Api.Persistence;
using Clawindex.Collector.Api.Projection;
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
    public void Project_CreatesToolSpanUnderTaskSpan_WithPolicySpanEvent()
    {
        var mapper = CreateMapper();

        ProjectAll(mapper, HappyPath());

        var toolSpan = Assert.Single(_stoppedActivities, activity => activity.DisplayName.StartsWith("tool.call", StringComparison.Ordinal));
        var taskSpan = Assert.Single(_stoppedActivities, activity => activity.DisplayName.StartsWith("agent.task", StringComparison.Ordinal));

        Assert.Equal(taskSpan.TraceId, toolSpan.TraceId);
        Assert.Equal(taskSpan.SpanId, toolSpan.ParentSpanId);
        Assert.Contains(toolSpan.Events, activityEvent => activityEvent.Name == "policy.evaluated");
        Assert.Equal("trace_abc", toolSpan.GetTagItem("clawindex.trace_id"));
        Assert.Equal("calculate_recommendation", toolSpan.GetTagItem("gen_ai.tool.name"));
    }

    [Fact]
    public void Project_PreservesValidIncomingTraceIdAsActivityTraceId()
    {
        var mapper = CreateMapper();
        const string traceId = "4bf92f3577b34da6a3ce929d0e0e4736";

        mapper.Project(Event("evt_task_start", "agent.task.started", traceId: traceId, taskId: "task_456"));
        mapper.Project(Event("evt_task_done", "agent.task.completed", traceId: traceId, taskId: "task_456"));

        var taskSpan = Assert.Single(_stoppedActivities);
        Assert.Equal(traceId, taskSpan.TraceId.ToString());
        Assert.Equal(traceId, taskSpan.GetTagItem("clawindex.trace_id"));
    }

    [Fact]
    public void Project_FailurePath_MarksToolAndTaskSpansAsError()
    {
        var mapper = CreateMapper();

        ProjectAll(mapper, FailurePath());

        var toolSpan = Assert.Single(_stoppedActivities, activity => activity.DisplayName.StartsWith("tool.call", StringComparison.Ordinal));
        var taskSpan = Assert.Single(_stoppedActivities, activity => activity.DisplayName.StartsWith("agent.task", StringComparison.Ordinal));

        Assert.Equal(ActivityStatusCode.Error, toolSpan.Status);
        Assert.Equal(ActivityStatusCode.Error, taskSpan.Status);
        Assert.Equal(taskSpan.TraceId, toolSpan.TraceId);
        Assert.Equal(taskSpan.SpanId, toolSpan.ParentSpanId);
    }

    [Fact]
    public void Project_EscalationPath_AttachesPolicyAndHumanReviewEventsToTaskSpan()
    {
        var mapper = CreateMapper();

        ProjectAll(mapper, EscalationPath());

        var taskSpan = Assert.Single(_stoppedActivities, activity => activity.DisplayName.StartsWith("agent.task", StringComparison.Ordinal));
        Assert.Contains(taskSpan.Events, activityEvent => activityEvent.Name == "policy.escalated");
        Assert.Contains(taskSpan.Events, activityEvent => activityEvent.Name == "human.review.requested");
        Assert.Contains(taskSpan.Events, activityEvent => activityEvent.Name == "human.review.completed");
    }

    [Fact]
    public void Project_DuplicateCompletion_DoesNotCreateDuplicateSpan()
    {
        var mapper = CreateMapper();
        var events = HappyPath().ToList();
        events.Add(Event("evt_tool_done_duplicate", "tool.call.completed", taskId: "task_456", spanId: "span_tool_001"));
        events.Add(Event("evt_task_done_duplicate", "agent.task.completed", taskId: "task_456"));

        var results = events.Select(mapper.Project).ToList();

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Single(_stoppedActivities, activity => activity.DisplayName.StartsWith("tool.call", StringComparison.Ordinal));
        Assert.Single(_stoppedActivities, activity => activity.DisplayName.StartsWith("agent.task", StringComparison.Ordinal));
    }

    [Fact]
    public void Project_OutOfOrderToolStart_ReturnsFailureInsteadOfOrphanSpan()
    {
        var mapper = CreateMapper();

        var result = mapper.Project(Event("evt_tool_start", "tool.call.started", taskId: "task_456", spanId: "span_tool_001"));

        Assert.False(result.Succeeded);
        Assert.Empty(_stoppedActivities);
    }

    public void Dispose()
    {
        _listener.Dispose();
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

    private static OtelEventMapper CreateMapper() => new(NullLogger<OtelEventMapper>.Instance);

    private static void ProjectAll(OtelEventMapper mapper, IEnumerable<AcceptedEvent> events)
    {
        foreach (var acceptedEvent in events)
        {
            var result = mapper.Project(acceptedEvent);
            Assert.True(result.Succeeded, result.Error);
        }
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

    private static IEnumerable<AcceptedEvent> EscalationPath()
    {
        yield return Event("evt_task_start", "agent.task.started", taskId: "task_456");
        yield return Event("evt_policy_escalated", "policy.escalated", taskId: "task_456");
        yield return Event("evt_review_requested", "human.review.requested", taskId: "task_456");
        yield return Event("evt_review_completed", "human.review.completed", taskId: "task_456");
        yield return Event("evt_task_done", "agent.task.completed", taskId: "task_456");
    }
}
