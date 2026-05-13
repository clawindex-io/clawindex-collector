using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Clawindex.Collector.Api.Persistence;

namespace Clawindex.Collector.Api.Projection;

public sealed class OtelEventMapper(EventRepository repository, ILogger<OtelEventMapper> logger)
{
    public async Task<ProjectionResult> ProjectAsync(AcceptedEvent acceptedEvent, CancellationToken cancellationToken = default)
    {
        if (await repository.GetEventSpanMapAsync(acceptedEvent.EventId, cancellationToken) is not null)
        {
            logger.LogDebug("Skipping already mapped event {EventId}", acceptedEvent.EventId);
            return ProjectionResult.Success();
        }

        return acceptedEvent.EventType switch
        {
            "agent.task.started" => await StartTaskSpanAsync(acceptedEvent, cancellationToken),
            "agent.task.completed" => await StopTaskSpanAsync(acceptedEvent, "completed", cancellationToken),
            "agent.task.failed" => await StopTaskSpanAsync(acceptedEvent, "error", cancellationToken),
            "tool.call.started" => await StartToolSpanAsync(acceptedEvent, cancellationToken),
            "tool.call.completed" => await StopToolSpanAsync(acceptedEvent, "completed", cancellationToken),
            "tool.call.failed" => await StopToolSpanAsync(acceptedEvent, "error", cancellationToken),
            "policy.evaluated" or "policy.denied" or "policy.escalated" or
                "human.review.requested" or "human.review.completed" =>
                    await MapSpanEventAsync(acceptedEvent, cancellationToken),
            _ => await EmitInstantSpanAsync(acceptedEvent, cancellationToken)
        };
    }

    private async Task<ProjectionResult> StartTaskSpanAsync(AcceptedEvent acceptedEvent, CancellationToken cancellationToken)
    {
        var traceId = TraceIdFor(acceptedEvent);
        var rootSpanId = RootSpanIdFor(acceptedEvent);
        var now = DateTimeOffset.UtcNow;

        await repository.UpsertTraceStateAsync(new TraceState(
            traceId,
            rootSpanId,
            acceptedEvent.TaskId,
            acceptedEvent.AgentId,
            "open",
            acceptedEvent.OccurredAt,
            null,
            now), cancellationToken);

        await repository.UpsertSpanStateAsync(new SpanState(
            rootSpanId,
            traceId,
            null,
            acceptedEvent.TaskId,
            acceptedEvent.AgentId,
            $"agent.task {PayloadString(acceptedEvent, "task_name") ?? acceptedEvent.EventId}",
            "agent.task",
            "open",
            acceptedEvent.OccurredAt,
            null,
            acceptedEvent.EventId,
            null,
            AttributesJson(acceptedEvent, "agent.task"),
            now), cancellationToken);

        await repository.MapEventToSpanAsync(acceptedEvent.EventId, traceId, rootSpanId, "source_start", cancellationToken);
        return ProjectionResult.Success();
    }

    private async Task<ProjectionResult> StopTaskSpanAsync(
        AcceptedEvent acceptedEvent,
        string status,
        CancellationToken cancellationToken)
    {
        var traceId = TraceIdFor(acceptedEvent);
        var rootSpanId = RootSpanIdFor(acceptedEvent);
        var existing = await repository.GetSpanStateAsync(rootSpanId, cancellationToken);
        if (existing is null)
        {
            logger.LogWarning(
                "Task completion created placeholder root span for event {EventId}, task {TaskId}, trace {TraceId}",
                acceptedEvent.EventId,
                acceptedEvent.TaskId,
                acceptedEvent.TraceId);
            await EnsurePlaceholderRootAsync(acceptedEvent, cancellationToken);
        }
        else if (existing.Status is "completed" or "error")
        {
            await repository.MapEventToSpanAsync(acceptedEvent.EventId, traceId, rootSpanId, "duplicate_end", cancellationToken);
            return ProjectionResult.Success();
        }

        await repository.CloseSpanStateAsync(
            rootSpanId,
            acceptedEvent.EventId,
            status,
            acceptedEvent.OccurredAt,
            AttributesJson(acceptedEvent, "agent.task"),
            cancellationToken);

        await repository.UpsertTraceStateAsync(new TraceState(
            traceId,
            rootSpanId,
            acceptedEvent.TaskId,
            acceptedEvent.AgentId,
            status,
            existing?.StartedAt ?? acceptedEvent.OccurredAt,
            acceptedEvent.OccurredAt,
            DateTimeOffset.UtcNow), cancellationToken);

        await repository.MapEventToSpanAsync(acceptedEvent.EventId, traceId, rootSpanId, "source_end", cancellationToken);
        await EmitTraceAsync(traceId, cancellationToken);
        return ProjectionResult.Success();
    }

    private async Task<ProjectionResult> StartToolSpanAsync(AcceptedEvent acceptedEvent, CancellationToken cancellationToken)
    {
        var traceId = TraceIdFor(acceptedEvent);
        var rootSpanId = await EnsureRootForChildAsync(acceptedEvent, cancellationToken);
        var spanId = ToolSpanIdFor(acceptedEvent);
        var now = DateTimeOffset.UtcNow;

        var existing = await repository.GetSpanStateAsync(spanId, cancellationToken);
        if (existing?.Status is "completed" or "error")
        {
            await repository.MapEventToSpanAsync(acceptedEvent.EventId, traceId, spanId, "duplicate_start", cancellationToken);
            return ProjectionResult.Success();
        }

        await repository.UpsertSpanStateAsync(new SpanState(
            spanId,
            traceId,
            rootSpanId,
            acceptedEvent.TaskId,
            acceptedEvent.AgentId,
            $"tool.call {PayloadString(acceptedEvent, "tool_name") ?? acceptedEvent.EventId}",
            "tool.call",
            "open",
            acceptedEvent.OccurredAt,
            null,
            acceptedEvent.EventId,
            null,
            AttributesJson(acceptedEvent, "tool.call"),
            now), cancellationToken);

        await repository.MapEventToSpanAsync(acceptedEvent.EventId, traceId, spanId, "source_start", cancellationToken);
        return ProjectionResult.Success();
    }

    private async Task<ProjectionResult> StopToolSpanAsync(
        AcceptedEvent acceptedEvent,
        string status,
        CancellationToken cancellationToken)
    {
        var traceId = TraceIdFor(acceptedEvent);
        var spanId = ToolSpanIdFor(acceptedEvent);
        var existing = await repository.GetSpanStateAsync(spanId, cancellationToken);
        if (existing is null)
        {
            logger.LogWarning(
                "Tool completion created placeholder child span for event {EventId}, span {SpanId}, task {TaskId}, trace {TraceId}",
                acceptedEvent.EventId,
                acceptedEvent.SpanId,
                acceptedEvent.TaskId,
                acceptedEvent.TraceId);
            var rootSpanId = await EnsureRootForChildAsync(acceptedEvent, cancellationToken);
            await repository.UpsertSpanStateAsync(new SpanState(
                spanId,
                traceId,
                rootSpanId,
                acceptedEvent.TaskId,
                acceptedEvent.AgentId,
                $"tool.call {PayloadString(acceptedEvent, "tool_name") ?? acceptedEvent.EventId}",
                "tool.call",
                "open",
                acceptedEvent.OccurredAt,
                null,
                acceptedEvent.EventId,
                null,
                AttributesJson(acceptedEvent, "tool.call"),
                DateTimeOffset.UtcNow), cancellationToken);
        }
        else if (existing.Status is "completed" or "error")
        {
            await repository.MapEventToSpanAsync(acceptedEvent.EventId, traceId, spanId, "duplicate_end", cancellationToken);
            return ProjectionResult.Success();
        }

        await repository.CloseSpanStateAsync(
            spanId,
            acceptedEvent.EventId,
            status,
            acceptedEvent.OccurredAt,
            AttributesJson(acceptedEvent, "tool.call"),
            cancellationToken);

        await repository.MapEventToSpanAsync(acceptedEvent.EventId, traceId, spanId, "source_end", cancellationToken);
        return ProjectionResult.Success();
    }

    private async Task<ProjectionResult> MapSpanEventAsync(AcceptedEvent acceptedEvent, CancellationToken cancellationToken)
    {
        var traceId = TraceIdFor(acceptedEvent);
        var target = await repository.FindBestOpenSpanAsync(traceId, acceptedEvent.TaskId, cancellationToken);
        if (target is null)
        {
            logger.LogWarning(
                "Span event created placeholder root span for event {EventId}, event type {EventType}, task {TaskId}, trace {TraceId}",
                acceptedEvent.EventId,
                acceptedEvent.EventType,
                acceptedEvent.TaskId,
                acceptedEvent.TraceId);
            target = await EnsurePlaceholderRootAsync(acceptedEvent, cancellationToken);
        }

        await repository.MapEventToSpanAsync(acceptedEvent.EventId, traceId, target.SpanId, "span_event", cancellationToken);
        return ProjectionResult.Success();
    }

    private async Task<ProjectionResult> EmitInstantSpanAsync(AcceptedEvent acceptedEvent, CancellationToken cancellationToken)
    {
        using var activity = ClawindexTelemetry.ActivitySource.StartActivity(
            $"clawindex.event {acceptedEvent.EventType}",
            ActivityKind.Internal,
            CreateParentContext(TraceIdFor(acceptedEvent), "clawindex-instant"),
            TagsFromJson(AttributesJson(acceptedEvent, "clawindex.event")),
            startTime: acceptedEvent.OccurredAt);

        activity?.SetEndTime(acceptedEvent.OccurredAt.UtcDateTime);
        await repository.MapEventToSpanAsync(
            acceptedEvent.EventId,
            TraceIdFor(acceptedEvent),
            ToSpanId($"{acceptedEvent.EventId}:instant"),
            "instant",
            cancellationToken);
        return ProjectionResult.Success();
    }

    private async Task<string> EnsureRootForChildAsync(AcceptedEvent acceptedEvent, CancellationToken cancellationToken)
    {
        var rootSpanId = RootSpanIdFor(acceptedEvent);
        if (await repository.GetSpanStateAsync(rootSpanId, cancellationToken) is not null)
        {
            return rootSpanId;
        }

        logger.LogWarning(
            "Creating placeholder root span for event {EventId}, task {TaskId}, trace {TraceId}",
            acceptedEvent.EventId,
            acceptedEvent.TaskId,
            acceptedEvent.TraceId);
        await EnsurePlaceholderRootAsync(acceptedEvent, cancellationToken);
        return rootSpanId;
    }

    private async Task<SpanState> EnsurePlaceholderRootAsync(AcceptedEvent acceptedEvent, CancellationToken cancellationToken)
    {
        var traceId = TraceIdFor(acceptedEvent);
        var rootSpanId = RootSpanIdFor(acceptedEvent);
        var now = DateTimeOffset.UtcNow;
        var root = new SpanState(
            rootSpanId,
            traceId,
            null,
            acceptedEvent.TaskId,
            acceptedEvent.AgentId,
            $"agent.task {acceptedEvent.TaskId ?? "placeholder"}",
            "agent.task",
            "open",
            acceptedEvent.OccurredAt,
            null,
            acceptedEvent.EventId,
            null,
            AttributesJson(acceptedEvent, "agent.task.placeholder"),
            now);

        await repository.UpsertTraceStateAsync(new TraceState(
            traceId,
            rootSpanId,
            acceptedEvent.TaskId,
            acceptedEvent.AgentId,
            "open",
            acceptedEvent.OccurredAt,
            null,
            now), cancellationToken);
        await repository.UpsertSpanStateAsync(root, cancellationToken);
        return root;
    }

    private async Task EmitTraceAsync(string traceId, CancellationToken cancellationToken)
    {
        var spans = await repository.GetTraceSpansAsync(traceId, cancellationToken);
        var root = spans.FirstOrDefault(span => span.ParentSpanId is null && span.SpanKind == "agent.task");
        if (root is null || root.EndedAt is null)
        {
            return;
        }

        using var rootActivity = StartActivity(root, null);
        if (rootActivity is null)
        {
            return;
        }

        await AddMappedEventsAsync(rootActivity, root.SpanId, cancellationToken);
        foreach (var child in spans.Where(span => span.ParentSpanId == root.SpanId && span.EndedAt is not null))
        {
            using var childActivity = StartActivity(child, rootActivity.Context);
            if (childActivity is null)
            {
                continue;
            }

            await AddMappedEventsAsync(childActivity, child.SpanId, cancellationToken);
        }
    }

    private static Activity? StartActivity(SpanState span, ActivityContext? parentContext)
    {
        var activity = ClawindexTelemetry.ActivitySource.StartActivity(
            span.SpanName,
            ActivityKind.Internal,
            parentContext ?? CreateParentContext(span.TraceId, "external-parent"),
            TagsFromJson(span.AttributesJson),
            startTime: span.StartedAt);

        activity?.SetStatus(span.Status == "error" ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
        activity?.SetEndTime((span.EndedAt ?? span.StartedAt).UtcDateTime);
        return activity;
    }

    private async Task AddMappedEventsAsync(Activity activity, string spanId, CancellationToken cancellationToken)
    {
        var events = await repository.GetMappedSpanEventsAsync(spanId, cancellationToken);
        foreach (var acceptedEvent in events)
        {
            activity.AddEvent(new ActivityEvent(
                acceptedEvent.EventType,
                acceptedEvent.OccurredAt,
                ToActivityTags(CreateTags(acceptedEvent, "span.event"))));
        }
    }

    private static ActivityContext CreateParentContext(string traceId, string parentSpanSeed)
    {
        return new ActivityContext(
            ToActivityTraceId(traceId),
            ActivitySpanId.CreateFromString(ToSpanId(parentSpanSeed)),
            ActivityTraceFlags.Recorded,
            isRemote: true);
    }

    private static string TraceIdFor(AcceptedEvent acceptedEvent) =>
        !string.IsNullOrWhiteSpace(acceptedEvent.TraceId)
            ? acceptedEvent.TraceId
            : ToTraceId(acceptedEvent.SessionId ?? acceptedEvent.TaskId ?? acceptedEvent.EventId);

    private static string RootSpanIdFor(AcceptedEvent acceptedEvent) =>
        ToSpanId($"{TraceIdFor(acceptedEvent)}:{acceptedEvent.TaskId ?? acceptedEvent.SessionId ?? "root"}:root");

    private static string ToolSpanIdFor(AcceptedEvent acceptedEvent) =>
        IsValidSpanId(acceptedEvent.SpanId)
            ? acceptedEvent.SpanId!.ToLowerInvariant()
            : ToSpanId($"{TraceIdFor(acceptedEvent)}:{acceptedEvent.TaskId}:{acceptedEvent.SpanId ?? PayloadString(acceptedEvent, "tool_name") ?? acceptedEvent.EventId}:tool");

    private static string ToTraceId(string value)
    {
        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return normalized.Length == 32 && normalized.All(Uri.IsHexDigit)
            ? normalized
            : Convert.ToHexString(Hash(value, 16)).ToLowerInvariant();
    }

    private static string ToSpanId(string value)
    {
        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return normalized.Length == 16 && normalized.All(Uri.IsHexDigit)
            ? normalized
            : Convert.ToHexString(Hash(value, 8)).ToLowerInvariant();
    }

    private static bool IsValidSpanId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length == 16 &&
        value.All(Uri.IsHexDigit);

    private static ActivityTraceId ToActivityTraceId(string rawTraceId) =>
        ActivityTraceId.CreateFromString(ToTraceId(rawTraceId));

    private static byte[] Hash(string value, int byteCount)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return hash[..byteCount];
    }

    private static string AttributesJson(AcceptedEvent acceptedEvent, string operationName)
    {
        var attributes = CreateTags(acceptedEvent, operationName)
            .Where(tag => tag.Value is not null)
            .ToDictionary(tag => tag.Key, tag => tag.Value);

        return JsonSerializer.Serialize(attributes);
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

    private static IEnumerable<KeyValuePair<string, object?>> TagsFromJson(string attributesJson)
    {
        using var document = JsonDocument.Parse(attributesJson);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            yield return new KeyValuePair<string, object?>(property.Name, JsonValue(property.Value));
        }
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

    private static string? PayloadString(AcceptedEvent acceptedEvent, string propertyName) =>
        PayloadValue(acceptedEvent, propertyName)?.ToString();

    private static object? PayloadValue(AcceptedEvent acceptedEvent, string propertyName)
    {
        using var document = JsonDocument.Parse(acceptedEvent.PayloadJson);
        if (!document.RootElement.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return JsonValue(property);
    }

    private static object? JsonValue(JsonElement property)
    {
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
