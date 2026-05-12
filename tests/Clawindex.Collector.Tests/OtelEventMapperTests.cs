using System.Diagnostics;
using Clawindex.Collector.Api.Persistence;
using Clawindex.Collector.Api.Projection;

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
        var mapper = new OtelEventMapper();

        mapper.Project(Event("evt_task_start", "agent.task.started", taskId: "task_456"));
        mapper.Project(Event("evt_tool_start", "tool.call.started", taskId: "task_456", spanId: "span_tool_001"));
        mapper.Project(Event("evt_policy", "policy.evaluated", taskId: "task_456"));
        mapper.Project(Event("evt_tool_done", "tool.call.completed", taskId: "task_456", spanId: "span_tool_001"));
        mapper.Project(Event("evt_task_done", "agent.task.completed", taskId: "task_456"));

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
        var mapper = new OtelEventMapper();
        const string traceId = "4bf92f3577b34da6a3ce929d0e0e4736";

        mapper.Project(Event("evt_task_start", "agent.task.started", traceId: traceId, taskId: "task_456"));
        mapper.Project(Event("evt_task_done", "agent.task.completed", traceId: traceId, taskId: "task_456"));

        var taskSpan = Assert.Single(_stoppedActivities);
        Assert.Equal(traceId, taskSpan.TraceId.ToString());
        Assert.Equal(traceId, taskSpan.GetTagItem("clawindex.trace_id"));
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
}
