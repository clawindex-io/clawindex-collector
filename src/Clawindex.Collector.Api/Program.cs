using System.Text.Json;
using Clawindex.Collector.Api;
using Clawindex.Collector.Api.Persistence;
using Clawindex.Collector.Api.Projection;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<EventEnvelopeValidator>();
builder.Services.AddSingleton<EventRepository>();
builder.Services.AddSingleton<OtelEventMapper>();
builder.Services.Configure<OtelProjectionOptions>(builder.Configuration.GetSection("Clawindex:Projection"));
builder.Services.AddHostedService<OtelProjectionWorker>();
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

app.MapGet("/v1/schema", () => Results.Ok(new
{
    schema_version = "0.1.0",
    name = "clawindex.event.envelope"
}));

app.MapOpenApi();

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

public partial class Program;
