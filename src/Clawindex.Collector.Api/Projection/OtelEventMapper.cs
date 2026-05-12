using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Clawindex.Collector.Api.Persistence;

namespace Clawindex.Collector.Api.Projection;

public sealed class OtelEventMapper
{
    private readonly Dictionary<string, Activity> _activeTasks = [];
    private readonly Dictionary<string, Activity> _activeTools = [];
    private readonly Dictionary<string, Activity> _activeToolByTask = [];

    public void Project(AcceptedEvent acceptedEvent)
    {
        switch (acceptedEvent.EventType)
        {
            case "agent.task.started":
                StartTaskSpan(acceptedEvent);
                break;
            case "agent.task.completed":
                StopTaskSpan(acceptedEvent, ActivityStatusCode.Ok);
                break;
            case "agent.task.failed":
                StopTaskSpan(acceptedEvent, ActivityStatusCode.Error);
                break;
            case "tool.call.started":
                StartToolSpan(acceptedEvent);
                break;
            case "tool.call.completed":
                StopToolSpan(acceptedEvent, ActivityStatusCode.Ok);
                break;
            case "tool.call.failed":
                StopToolSpan(acceptedEvent, ActivityStatusCode.Error);
                break;
            case "policy.evaluated":
            case "policy.denied":
            case "policy.escalated":
            case "human.review.requested":
            case "human.review.completed":
                AddSpanEvent(acceptedEvent);
                break;
            default:
                EmitInstantSpan(acceptedEvent);
                break;
        }
    }

    private void StartTaskSpan(AcceptedEvent acceptedEvent)
    {
        var key = TaskKey(acceptedEvent);
        if (_activeTasks.ContainsKey(key))
        {
            return;
        }

        var activity = ClawindexTelemetry.ActivitySource.StartActivity(
            $"agent.task {PayloadString(acceptedEvent, "task_name") ?? acceptedEvent.EventId}",
            ActivityKind.Internal,
            CreateParentContext(acceptedEvent),
            CreateTags(acceptedEvent, "agent.task"),
            startTime: acceptedEvent.OccurredAt);

        if (activity is null)
        {
            return;
        }

        _activeTasks[key] = activity;
    }

    private void StopTaskSpan(AcceptedEvent acceptedEvent, ActivityStatusCode statusCode)
    {
        var key = TaskKey(acceptedEvent);
        if (!_activeTasks.Remove(key, out var activity))
        {
            EmitInstantSpan(acceptedEvent, "agent.task", statusCode);
            return;
        }

        activity.SetStatus(statusCode);
        AddCompletionTags(activity, acceptedEvent);
        activity.SetEndTime(acceptedEvent.OccurredAt.UtcDateTime);
        activity.Stop();
    }

    private void StartToolSpan(AcceptedEvent acceptedEvent)
    {
        var key = ToolKey(acceptedEvent);
        if (_activeTools.ContainsKey(key))
        {
            return;
        }

        var parentContext = _activeTasks.TryGetValue(TaskKey(acceptedEvent), out var taskActivity)
            ? taskActivity.Context
            : CreateParentContext(acceptedEvent);

        var activity = ClawindexTelemetry.ActivitySource.StartActivity(
            $"tool.call {PayloadString(acceptedEvent, "tool_name") ?? acceptedEvent.EventId}",
            ActivityKind.Internal,
            parentContext,
            CreateTags(acceptedEvent, "tool.call"),
            startTime: acceptedEvent.OccurredAt);

        if (activity is null)
        {
            return;
        }

        _activeTools[key] = activity;
        if (!string.IsNullOrWhiteSpace(acceptedEvent.TaskId))
        {
            _activeToolByTask[TaskKey(acceptedEvent)] = activity;
        }
    }

    private void StopToolSpan(AcceptedEvent acceptedEvent, ActivityStatusCode statusCode)
    {
        var key = ToolKey(acceptedEvent);
        if (!_activeTools.Remove(key, out var activity))
        {
            EmitInstantSpan(acceptedEvent, "tool.call", statusCode);
            return;
        }

        if (!string.IsNullOrWhiteSpace(acceptedEvent.TaskId))
        {
            _activeToolByTask.Remove(TaskKey(acceptedEvent));
        }

        activity.SetStatus(statusCode);
        AddCompletionTags(activity, acceptedEvent);
        activity.SetEndTime(acceptedEvent.OccurredAt.UtcDateTime);
        activity.Stop();
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
