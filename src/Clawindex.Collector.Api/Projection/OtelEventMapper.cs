using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Clawindex.Collector.Api.Persistence;

namespace Clawindex.Collector.Api.Projection;

public sealed class OtelEventMapper(ILogger<OtelEventMapper> logger)
{
    private readonly Dictionary<string, Activity> _activeTasks = [];
    private readonly Dictionary<string, Activity> _activeTools = [];
    private readonly Dictionary<string, Activity> _activeToolByTask = [];
    private readonly HashSet<string> _completedTasks = [];
    private readonly HashSet<string> _completedTools = [];

    public ProjectionResult Project(AcceptedEvent acceptedEvent)
    {
        switch (acceptedEvent.EventType)
        {
            case "agent.task.started":
                return StartTaskSpan(acceptedEvent);
            case "agent.task.completed":
                return StopTaskSpan(acceptedEvent, ActivityStatusCode.Ok);
            case "agent.task.failed":
                return StopTaskSpan(acceptedEvent, ActivityStatusCode.Error);
            case "tool.call.started":
                return StartToolSpan(acceptedEvent);
            case "tool.call.completed":
                return StopToolSpan(acceptedEvent, ActivityStatusCode.Ok);
            case "tool.call.failed":
                return StopToolSpan(acceptedEvent, ActivityStatusCode.Error);
            case "policy.evaluated":
            case "policy.denied":
            case "policy.escalated":
            case "human.review.requested":
            case "human.review.completed":
                AddSpanEvent(acceptedEvent);
                return ProjectionResult.Success();
            default:
                EmitInstantSpan(acceptedEvent);
                return ProjectionResult.Success();
        }
    }

    private ProjectionResult StartTaskSpan(AcceptedEvent acceptedEvent)
    {
        var key = TaskKey(acceptedEvent);
        if (_activeTasks.ContainsKey(key))
        {
            logger.LogWarning(
                "Duplicate active task start skipped for event {EventId}, task {TaskId}, trace {TraceId}",
                acceptedEvent.EventId,
                acceptedEvent.TaskId,
                acceptedEvent.TraceId);
            return ProjectionResult.Success();
        }

        if (_completedTasks.Contains(key))
        {
            logger.LogWarning(
                "Task start skipped because task is already completed for event {EventId}, task {TaskId}, trace {TraceId}",
                acceptedEvent.EventId,
                acceptedEvent.TaskId,
                acceptedEvent.TraceId);
            return ProjectionResult.Success();
        }

        var activity = ClawindexTelemetry.ActivitySource.StartActivity(
            $"agent.task {PayloadString(acceptedEvent, "task_name") ?? acceptedEvent.EventId}",
            ActivityKind.Internal,
            CreateParentContext(acceptedEvent),
            CreateTags(acceptedEvent, "agent.task"),
            startTime: acceptedEvent.OccurredAt);

        if (activity is null)
        {
            return ProjectionResult.Failure("ActivitySource did not create task activity.");
        }

        _activeTasks[key] = activity;
        return ProjectionResult.Success();
    }

    private ProjectionResult StopTaskSpan(AcceptedEvent acceptedEvent, ActivityStatusCode statusCode)
    {
        var key = TaskKey(acceptedEvent);
        if (_completedTasks.Contains(key))
        {
            logger.LogWarning(
                "Duplicate task completion skipped for event {EventId}, task {TaskId}, trace {TraceId}",
                acceptedEvent.EventId,
                acceptedEvent.TaskId,
                acceptedEvent.TraceId);
            return ProjectionResult.Success();
        }

        if (!_activeTasks.Remove(key, out var activity))
        {
            logger.LogWarning(
                "Task completion has no active task span for event {EventId}, task {TaskId}, trace {TraceId}",
                acceptedEvent.EventId,
                acceptedEvent.TaskId,
                acceptedEvent.TraceId);
            return ProjectionResult.Failure("Task completion has no active task span.");
        }

        activity.SetStatus(statusCode);
        AddCompletionTags(activity, acceptedEvent);
        activity.SetEndTime(acceptedEvent.OccurredAt.UtcDateTime);
        activity.Stop();
        _completedTasks.Add(key);
        return ProjectionResult.Success();
    }

    private ProjectionResult StartToolSpan(AcceptedEvent acceptedEvent)
    {
        var key = ToolKey(acceptedEvent);
        if (_activeTools.ContainsKey(key))
        {
            logger.LogWarning(
                "Duplicate active tool start skipped for event {EventId}, span {SpanId}, task {TaskId}, trace {TraceId}",
                acceptedEvent.EventId,
                acceptedEvent.SpanId,
                acceptedEvent.TaskId,
                acceptedEvent.TraceId);
            return ProjectionResult.Success();
        }

        if (_completedTools.Contains(key))
        {
            logger.LogWarning(
                "Tool start skipped because tool is already completed for event {EventId}, span {SpanId}, task {TaskId}, trace {TraceId}",
                acceptedEvent.EventId,
                acceptedEvent.SpanId,
                acceptedEvent.TaskId,
                acceptedEvent.TraceId);
            return ProjectionResult.Success();
        }

        if (!_activeTasks.TryGetValue(TaskKey(acceptedEvent), out var taskActivity))
        {
            logger.LogWarning(
                "Tool start has no active parent task span for event {EventId}, span {SpanId}, task {TaskId}, trace {TraceId}",
                acceptedEvent.EventId,
                acceptedEvent.SpanId,
                acceptedEvent.TaskId,
                acceptedEvent.TraceId);
            return ProjectionResult.Failure("Tool start has no active parent task span.");
        }

        var activity = ClawindexTelemetry.ActivitySource.StartActivity(
            $"tool.call {PayloadString(acceptedEvent, "tool_name") ?? acceptedEvent.EventId}",
            ActivityKind.Internal,
            taskActivity.Context,
            CreateTags(acceptedEvent, "tool.call"),
            startTime: acceptedEvent.OccurredAt);

        if (activity is null)
        {
            return ProjectionResult.Failure("ActivitySource did not create tool activity.");
        }

        _activeTools[key] = activity;
        if (!string.IsNullOrWhiteSpace(acceptedEvent.TaskId))
        {
            _activeToolByTask[TaskKey(acceptedEvent)] = activity;
        }

        return ProjectionResult.Success();
    }

    private ProjectionResult StopToolSpan(AcceptedEvent acceptedEvent, ActivityStatusCode statusCode)
    {
        var key = ToolKey(acceptedEvent);
        if (_completedTools.Contains(key))
        {
            logger.LogWarning(
                "Duplicate tool completion skipped for event {EventId}, span {SpanId}, task {TaskId}, trace {TraceId}",
                acceptedEvent.EventId,
                acceptedEvent.SpanId,
                acceptedEvent.TaskId,
                acceptedEvent.TraceId);
            return ProjectionResult.Success();
        }

        if (!_activeTools.Remove(key, out var activity))
        {
            logger.LogWarning(
                "Tool completion has no active tool span for event {EventId}, span {SpanId}, task {TaskId}, trace {TraceId}",
                acceptedEvent.EventId,
                acceptedEvent.SpanId,
                acceptedEvent.TaskId,
                acceptedEvent.TraceId);
            return ProjectionResult.Failure("Tool completion has no active tool span.");
        }

        if (!string.IsNullOrWhiteSpace(acceptedEvent.TaskId))
        {
            _activeToolByTask.Remove(TaskKey(acceptedEvent));
        }

        activity.SetStatus(statusCode);
        AddCompletionTags(activity, acceptedEvent);
        activity.SetEndTime(acceptedEvent.OccurredAt.UtcDateTime);
        activity.Stop();
        _completedTools.Add(key);
        return ProjectionResult.Success();
    }

    private void AddSpanEvent(AcceptedEvent acceptedEvent)
    {
        var target = FindActiveSpan(acceptedEvent);
        var tags = CreateTags(acceptedEvent, "span.event");

        if (acceptedEvent.EventType == "policy.denied")
        {
            tags.Add(new("otel.status_code", "WARN"));
        }

        if (target is not null)
        {
            target.AddEvent(new ActivityEvent(acceptedEvent.EventType, acceptedEvent.OccurredAt, ToActivityTags(tags)));
            return;
        }

        logger.LogWarning(
            "Span event has no active parent span for event {EventId}, event type {EventType}, task {TaskId}, trace {TraceId}",
            acceptedEvent.EventId,
            acceptedEvent.EventType,
            acceptedEvent.TaskId,
            acceptedEvent.TraceId);

        using var activity = StartFallbackActivity(acceptedEvent, "clawindex.event", ActivityStatusCode.Ok);
        activity?.AddEvent(new ActivityEvent(acceptedEvent.EventType, acceptedEvent.OccurredAt, ToActivityTags(tags)));
    }

    private void EmitInstantSpan(
        AcceptedEvent acceptedEvent,
        string operationName = "clawindex.event",
        ActivityStatusCode statusCode = ActivityStatusCode.Ok)
    {
        using var activity = StartFallbackActivity(acceptedEvent, operationName, statusCode);
    }

    private Activity? StartFallbackActivity(AcceptedEvent acceptedEvent, string operationName, ActivityStatusCode statusCode)
    {
        var activity = ClawindexTelemetry.ActivitySource.StartActivity(
            $"{operationName} {acceptedEvent.EventType}",
            ActivityKind.Internal,
            CreateParentContext(acceptedEvent),
            CreateTags(acceptedEvent, operationName),
            startTime: acceptedEvent.OccurredAt);

        activity?.SetStatus(statusCode);
        activity?.SetEndTime(acceptedEvent.OccurredAt.UtcDateTime);
        return activity;
    }

    private Activity? FindActiveSpan(AcceptedEvent acceptedEvent)
    {
        if (_activeTools.TryGetValue(ToolKey(acceptedEvent), out var toolActivity))
        {
            return toolActivity;
        }

        if (_activeToolByTask.TryGetValue(TaskKey(acceptedEvent), out var taskToolActivity))
        {
            return taskToolActivity;
        }

        return _activeTasks.GetValueOrDefault(TaskKey(acceptedEvent));
    }

    private static ActivityContext CreateParentContext(AcceptedEvent acceptedEvent)
    {
        var traceId = ToActivityTraceId(acceptedEvent.TraceId);
        if (traceId == default)
        {
            return default;
        }

        var spanId = ToActivitySpanId(acceptedEvent.SpanId ?? acceptedEvent.TaskId ?? acceptedEvent.SessionId ?? "clawindex-root");
        return new ActivityContext(traceId, spanId, ActivityTraceFlags.Recorded, isRemote: true);
    }

    private static ActivityTraceId ToActivityTraceId(string? rawTraceId)
    {
        if (string.IsNullOrWhiteSpace(rawTraceId))
        {
            return default;
        }

        var normalized = rawTraceId.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        if (normalized.Length == 32 && normalized.All(Uri.IsHexDigit))
        {
            return ActivityTraceId.CreateFromString(normalized);
        }

        return ActivityTraceId.CreateFromBytes(Hash(rawTraceId, 16));
    }

    private static ActivitySpanId ToActivitySpanId(string rawSpanId)
    {
        var normalized = rawSpanId.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        if (normalized.Length == 16 && normalized.All(Uri.IsHexDigit))
        {
            return ActivitySpanId.CreateFromString(normalized);
        }

        return ActivitySpanId.CreateFromBytes(Hash(rawSpanId, 8));
    }

    private static byte[] Hash(string value, int byteCount)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return hash[..byteCount];
    }

    private static List<KeyValuePair<string, object?>> CreateTags(AcceptedEvent acceptedEvent, string operationName)
    {
        var tags = new List<KeyValuePair<string, object?>>
        {
            new("gen_ai.operation.name", operationName),
            new("clawindex.schema.version", acceptedEvent.SchemaVersion),
            new("clawindex.event.id", acceptedEvent.EventId),
            new("clawindex.event.type", acceptedEvent.EventType),
            new("clawindex.received_at", acceptedEvent.ReceivedAt.ToString("O", CultureInfo.InvariantCulture)),
            new("clawindex.source.system", acceptedEvent.SourceSystem),
            new("clawindex.source.component", acceptedEvent.SourceComponent),
            new("clawindex.source.version", acceptedEvent.SourceVersion),
            new("clawindex.trace_id", acceptedEvent.TraceId),
            new("clawindex.span_id", acceptedEvent.SpanId),
            new("clawindex.task_id", acceptedEvent.TaskId),
            new("clawindex.agent_id", acceptedEvent.AgentId),
            new("clawindex.session_id", acceptedEvent.SessionId),
            new("gen_ai.agent.name", acceptedEvent.AgentId)
        };

        AddPayloadTag(tags, acceptedEvent, "tool_name", "gen_ai.tool.name");
        AddPayloadTag(tags, acceptedEvent, "side_effect_type", "clawindex.side_effect.type");
        AddPayloadTag(tags, acceptedEvent, "decision", "clawindex.policy.decision");
        AddPayloadTag(tags, acceptedEvent, "policy_id", "clawindex.policy.id");
        AddPayloadTag(tags, acceptedEvent, "model", "gen_ai.request.model");
        AddPayloadTag(tags, acceptedEvent, "input_tokens", "gen_ai.usage.input_tokens");
        AddPayloadTag(tags, acceptedEvent, "output_tokens", "gen_ai.usage.output_tokens");

        return tags.Where(tag => tag.Value is not null).ToList();
    }

    private static ActivityTagsCollection ToActivityTags(IEnumerable<KeyValuePair<string, object?>> tags)
    {
        var collection = new ActivityTagsCollection();
        foreach (var tag in tags)
        {
            collection.Add(tag);
        }

        return collection;
    }

    private static void AddCompletionTags(Activity activity, AcceptedEvent acceptedEvent)
    {
        foreach (var tag in CreateTags(acceptedEvent, "completion"))
        {
            activity.SetTag(tag.Key, tag.Value);
        }
    }

    private static void AddPayloadTag(
        List<KeyValuePair<string, object?>> tags,
        AcceptedEvent acceptedEvent,
        string payloadProperty,
        string tagName)
    {
        var value = PayloadValue(acceptedEvent, payloadProperty);
        if (value is not null)
        {
            tags.Add(new(tagName, value));
        }
    }

    private static string TaskKey(AcceptedEvent acceptedEvent) =>
        $"{acceptedEvent.TraceId ?? acceptedEvent.SessionId ?? "local"}:{acceptedEvent.TaskId ?? acceptedEvent.EventId}";

    private static string ToolKey(AcceptedEvent acceptedEvent) =>
        $"{TaskKey(acceptedEvent)}:{acceptedEvent.SpanId ?? PayloadString(acceptedEvent, "tool_name") ?? acceptedEvent.EventId}";

    private static string? PayloadString(AcceptedEvent acceptedEvent, string propertyName) =>
        PayloadValue(acceptedEvent, propertyName)?.ToString();

    private static object? PayloadValue(AcceptedEvent acceptedEvent, string propertyName)
    {
        using var document = JsonDocument.Parse(acceptedEvent.PayloadJson);
        if (!document.RootElement.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number when property.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when property.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => property.GetRawText()
        };
    }
}
