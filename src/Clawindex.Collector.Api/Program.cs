using System.Text.Json;
using Clawindex.Collector.Api;
using Clawindex.Collector.Api.Economics;
using Clawindex.Collector.Api.Fanout;
using Clawindex.Collector.Api.Otlp;
using Clawindex.Collector.Api.Persistence;
using Clawindex.Collector.Api.Projection;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<EventEnvelopeValidator>();
builder.Services.AddSingleton<EventRepository>();
builder.Services.AddSingleton(_ =>
{
    var assembly = typeof(PricingTable).Assembly;
    using var stream = assembly.GetManifestResourceStream("Clawindex.Collector.Api.Economics.pricing.json")
        ?? throw new InvalidOperationException("Embedded resource pricing.json not found.");
    return new PricingTable(stream);
});
builder.Services.AddSingleton<CostEstimator>();
builder.Services.AddSingleton<SpanFlattener>();
builder.Services.AddSingleton<SemConvConformanceValidator>();
builder.Services.AddSingleton<InMemorySpanSink>();
builder.Services.AddSingleton<DurableSpanSink>();
builder.Services.AddSingleton<IValidatedSpanSink>(sp => sp.GetRequiredService<DurableSpanSink>());

// Fan-out destinations — read from config; no image rebuild required to change them.
// Header values go into DefaultRequestHeaders and are never logged.
var destConfigs = builder.Configuration
    .GetSection("Clawindex:Destinations")
    .Get<List<DestinationConfig>>() ?? [];

// Always register IHttpClientFactory even when no named clients are configured.
builder.Services.AddHttpClient();

foreach (var cfg in destConfigs)
{
    builder.Services.AddHttpClient($"dest-{cfg.Name}", client =>
    {
        foreach (var (k, v) in cfg.Headers)
            client.DefaultRequestHeaders.TryAddWithoutValidation(k, v);
    });
}

builder.Services.AddSingleton<IReadOnlyList<ITelemetryDestination>>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return destConfigs.Select<DestinationConfig, ITelemetryDestination>(cfg => cfg.Type switch
    {
        "otlp-http" => new OtlpHttpDestination(cfg, factory.CreateClient($"dest-{cfg.Name}")),
        _ => throw new InvalidOperationException($"Unknown destination type '{cfg.Type}'")
    }).ToList();
});

builder.Services.AddSingleton<ForwardQueue>();
builder.Services.AddSingleton<ForwardDispatcher>();

if (builder.Configuration.GetValue("Clawindex:Forwarding:Enabled", true)
    && destConfigs.Any(d => d.Enabled))
{
    builder.Services.AddHostedService<ForwardWorker>();
}

builder.Services.AddOpenApi();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(ClawindexTelemetry.ServiceName, serviceVersion: "0.1.0"))
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(ClawindexTelemetry.ActivitySourceName)
            .AddOtlpExporter();
    });

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var repository = scope.ServiceProvider.GetRequiredService<EventRepository>();
    await repository.InitializeAsync();
    var openTraces = await repository.GetOpenTraceStatesAsync();
    var openSpans = await repository.GetOpenSpanStatesAsync();
    app.Logger.LogInformation(
        "Recovered durable span state with {OpenTraceCount} open traces and {OpenSpanCount} open spans.",
        openTraces.Count,
        openSpans.Count);
}

app.MapGet("/v1/health", () => Results.Ok(new
{
    status = "ok",
    service = "clawindex-collector",
    version = "0.1.0"
}));

app.MapGet("/v1/agents", async (
    string? since,
    string? until,
    string? sort,
    EventRepository repository,
    CostEstimator costEstimator,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    var (windowError, sinceValue, untilValue) = ParseWindow(since, until, timeProvider.GetUtcNow());
    if (windowError is not null) return windowError;

    var rollups         = await repository.GetAgentRollupsAsync(sinceValue, untilValue, cancellationToken);
    var tokenAggregates = await repository.GetAgentTokenAggregatesAsync(sinceValue, untilValue, cancellationToken);

    var costByAgent = tokenAggregates
        .GroupBy(a => a.AgentId)
        .ToDictionary(g => g.Key, g => g.ToList());

    var enriched = rollups.Select(r =>
    {
        var groups = costByAgent.TryGetValue(r.AgentId, out var g) ? g : [];
        var cost = costEstimator.EstimateAgent(groups, r.SpanCount, untilValue);
        return r with
        {
            EstimatedCostUsd           = cost.EstimatedCostUsd,
            EstimatedErrorTraceCostUsd = cost.EstimatedErrorTraceCostUsd,
            CostedSpanCount            = cost.CostedSpanCount,
            UncostedSpanCount          = cost.UncostedSpanCount,
            CostCoverage               = cost.CostCoverage,
            PricedAsOf                 = cost.PricedAsOf,
            PricingStale               = cost.PricingStale
        };
    });

    var ordered = sort?.ToLowerInvariant() == "estimated_cost_desc"
        ? enriched.OrderByDescending(r => r.EstimatedCostUsd ?? decimal.MinValue)
        : enriched;

    return Results.Ok(ordered);
});

app.MapGet("/v1/agents/{id}", async (
    string id,
    string? since,
    string? until,
    string? limit,
    EventRepository repository,
    CostEstimator costEstimator,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(id, out _))
        return Results.BadRequest(Rejected("'id' must be a valid GUID"));

    var (windowError, sinceValue, untilValue) = ParseWindow(since, until, timeProvider.GetUtcNow());
    if (windowError is not null) return windowError;

    int limitValue;
    if (limit is not null)
    {
        if (!int.TryParse(limit, out limitValue))
            return Results.BadRequest(Rejected("Invalid 'limit' parameter: expected a positive integer"));
        if (limitValue <= 0)
            return Results.BadRequest(Rejected("Invalid 'limit' parameter: must be greater than 0"));
        if (limitValue > 200)
            limitValue = 200;
    }
    else
    {
        limitValue = 50;
    }

    var rollup              = await repository.GetAgentRollupAsync(id, sinceValue, untilValue, cancellationToken);
    var agentTokenAggregates = await repository.GetSingleAgentTokenAggregatesAsync(id, sinceValue, untilValue, cancellationToken);
    var agentCost           = costEstimator.EstimateAgent(agentTokenAggregates, rollup.SpanCount, untilValue);

    var enrichedRollup = rollup with
    {
        EstimatedCostUsd           = agentCost.EstimatedCostUsd,
        EstimatedErrorTraceCostUsd = agentCost.EstimatedErrorTraceCostUsd,
        CostedSpanCount            = agentCost.CostedSpanCount,
        UncostedSpanCount          = agentCost.UncostedSpanCount,
        CostCoverage               = agentCost.CostCoverage,
        PricedAsOf                 = agentCost.PricedAsOf,
        PricingStale               = agentCost.PricingStale
    };

    var recentTraces        = await repository.GetAgentRecentTracesAsync(id, sinceValue, untilValue, limitValue, cancellationToken);
    var traceTokenAggregates = await repository.GetAgentTraceTokenAggregatesAsync(id, sinceValue, untilValue, cancellationToken);

    var traceAggregatesByTraceId = traceTokenAggregates
        .GroupBy(a => a.TraceId)
        .ToDictionary(g => g.Key, g => g.ToList());

    var enrichedTraces = recentTraces.Select(t =>
    {
        var groups = traceAggregatesByTraceId.TryGetValue(t.TraceId, out var g) ? g : [];
        var cost = costEstimator.EstimateTrace(groups, t.AgentSpanCount, untilValue);
        return t with { EstimatedCostUsd = cost.EstimatedCostUsd, CostCoverage = cost.CostCoverage };
    }).ToList();

    return Results.Ok(new
    {
        agent_id = id,
        rollup = enrichedRollup,
        recent_traces = enrichedTraces
    });
});

app.MapGet("/v1/schema", () => Results.Ok(new
{
    schema_version = "0.1.0",
    name = "clawindex.event.envelope"
}));

app.MapOpenApi();

app.MapPost("/v1/traces", async (
    HttpRequest request,
    SpanFlattener flattener,
    SemConvConformanceValidator validator,
    IValidatedSpanSink sink,
    ForwardQueue forwardQueue,
    CancellationToken cancellationToken) =>
{
    var rawBody     = await ReadBinaryBodyAsync(request);
    var contentType = request.ContentType ?? "application/x-protobuf";

    if (rawBody.Length == 0)
    {
        return Results.BadRequest(Rejected("Request body must not be empty"));
    }

    ExportTraceServiceRequest otlpRequest;
    try
    {
        otlpRequest = ExportTraceServiceRequest.Parser.ParseFrom(rawBody);
    }
    catch (InvalidProtocolBufferException)
    {
        return Results.BadRequest(Rejected("Request body is not a valid ExportTraceServiceRequest protobuf"));
    }

    var flatSpans = flattener.Flatten(otlpRequest);
    var validatedSpans = new List<ValidatedSpan>(flatSpans.Count);
    var rejectedCount = 0;

    foreach (var flat in flatSpans)
    {
        var result = validator.Validate(flat);
        if (!result.IsEnvelopeValid)
        {
            rejectedCount++;
        }
        else
        {
            validatedSpans.Add(result.Span!);
        }
    }

    if (validatedSpans.Count > 0)
    {
        await sink.AcceptAsync(validatedSpans, cancellationToken);
    }

    // Enqueue for forwarding AFTER the durable write — persist-first is enforced by code order.
    // TryEnqueueAsync never throws; no-op if no destinations are configured.
    await forwardQueue.TryEnqueueAsync(rawBody, contentType, cancellationToken);

    var response = new ExportTraceServiceResponse();
    if (rejectedCount > 0)
    {
        response.PartialSuccess = new ExportTracePartialSuccess
        {
            RejectedSpans = rejectedCount,
            ErrorMessage = $"{rejectedCount} span(s) rejected: missing required identity fields (trace_id, span_id, name, or start_time)."
        };
    }

    return Results.Bytes(response.ToByteArray(), "application/x-protobuf");
});

app.MapPost("/v1/events", async (
    HttpRequest request,
    EventEnvelopeValidator validator,
    EventRepository repository,
    TimeProvider timeProvider) =>
{
    var rawBody = await ReadBodyAsync(request);
    if (!TryParseObject(rawBody, out var document, out var parseError))
    {
        return Results.BadRequest(Rejected(parseError));
    }

    using (document)
    {
        var validation = validator.Validate(document.RootElement);
        if (!validation.IsValid)
        {
            return Results.BadRequest(Rejected(validation.ErrorMessage));
        }

        var accepted = AcceptedEvent.From(validation.Envelope!, rawBody, timeProvider.GetUtcNow());
        await repository.InsertAsync(accepted);

        return Results.Accepted("/v1/events", new
        {
            status = "accepted",
            event_id = accepted.EventId,
            received_at = accepted.ReceivedAt,
            schema_version = accepted.SchemaVersion
        });
    }
});

app.MapPost("/v1/events/batch", async (
    HttpRequest request,
    EventEnvelopeValidator validator,
    EventRepository repository,
    TimeProvider timeProvider) =>
{
    var rawBody = await ReadBodyAsync(request);
    if (!TryParseObject(rawBody, out var document, out var parseError))
    {
        return Results.BadRequest(Rejected(parseError));
    }

    using (document)
    {
        var root = document.RootElement;
        if (!root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
        {
            return Results.BadRequest(Rejected("Missing required field: events"));
        }

        var acceptedEvents = new List<AcceptedEvent>();
        var results = new List<BatchEventResult>();
        var receivedAt = timeProvider.GetUtcNow();
        var index = 0;

        foreach (var item in events.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                results.Add(BatchEventResult.Rejected(index, "Event must be a JSON object"));
                index++;
                continue;
            }

            var validation = validator.Validate(item);
            if (!validation.IsValid)
            {
                results.Add(BatchEventResult.Rejected(index, validation.ErrorMessage));
                index++;
                continue;
            }

            var accepted = AcceptedEvent.From(validation.Envelope!, item.GetRawText(), receivedAt);
            acceptedEvents.Add(accepted);
            results.Add(BatchEventResult.Accepted(index, accepted.EventId));
            index++;
        }

        if (acceptedEvents.Count > 0)
        {
            await repository.InsertBatchAsync(acceptedEvents);
        }

        var rejectedCount = results.Count - acceptedEvents.Count;
        var status = acceptedEvents.Count == 0 ? "rejected" : rejectedCount == 0 ? "accepted" : "partial";
        var response = new
        {
            status,
            accepted_count = acceptedEvents.Count,
            rejected_count = rejectedCount,
            results
        };

        return status switch
        {
            "accepted" => Results.Accepted("/v1/events/batch", response),
            "partial" => Results.Ok(response),
            _ => Results.BadRequest(response)
        };
    }
});

app.Run();

static async Task<string> ReadBodyAsync(HttpRequest request)
{
    using var reader = new StreamReader(request.Body);
    return await reader.ReadToEndAsync();
}

static async Task<byte[]> ReadBinaryBodyAsync(HttpRequest request)
{
    using var ms = new MemoryStream();
    await request.Body.CopyToAsync(ms);
    return ms.ToArray();
}

static bool TryParseObject(string rawBody, out JsonDocument document, out string error)
{
    document = null!;

    if (string.IsNullOrWhiteSpace(rawBody))
    {
        error = "Request body must be a JSON object";
        return false;
    }

    try
    {
        document = JsonDocument.Parse(rawBody);
    }
    catch (JsonException)
    {
        error = "Request body must be valid JSON";
        return false;
    }

    if (document.RootElement.ValueKind == JsonValueKind.Object)
    {
        error = string.Empty;
        return true;
    }

    document.Dispose();
    error = "Request body must be a JSON object";
    return false;
}

static object Rejected(string message) => new
{
    status = "rejected",
    error = new
    {
        code = "validation_failed",
        message
    }
};

static (IResult? Error, DateTimeOffset Since, DateTimeOffset Until) ParseWindow(
    string? since,
    string? until,
    DateTimeOffset now)
{
    DateTimeOffset sinceValue;
    if (since is not null)
    {
        if (!DateTimeOffset.TryParse(since, out var parsed))
            return (Results.BadRequest(Rejected("Invalid 'since' parameter: expected ISO-8601 timestamp")), default, default);
        sinceValue = parsed.ToUniversalTime();
    }
    else
    {
        sinceValue = now.AddDays(-30);
    }

    DateTimeOffset untilValue;
    if (until is not null)
    {
        if (!DateTimeOffset.TryParse(until, out var parsed))
            return (Results.BadRequest(Rejected("Invalid 'until' parameter: expected ISO-8601 timestamp")), default, default);
        untilValue = parsed.ToUniversalTime();
    }
    else
    {
        untilValue = now;
    }

    if (sinceValue >= untilValue)
        return (Results.BadRequest(Rejected("'since' must be before 'until'")), default, default);

    return (null, sinceValue, untilValue);
}

public partial class Program;
