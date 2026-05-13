# Clawindex Collector

Clawindex Collector is the intake and projection layer for governed agent telemetry. It accepts validated Clawindex event envelopes, stores them in SQLite, and projects accepted events into OpenTelemetry-compatible traces for local visualization in Aspire Dashboard.

Phase 1 is a spike: it proves Clawindex IR can flow into standard OTel tooling. It does not add Clawindex UI, replay, analytics, risk scoring, multi-tenancy, SIEM integrations, or production orchestration.

## Architecture

```text
Agent / bouncer-md / adapter
        |
        v
Clawindex Collector intake API
        |
        v
SQLite validated event store
        |
        v
OTel projection worker
        |
        v
OTLP export
        |
        v
Aspire Dashboard
```

The projection worker polls unprojected rows from SQLite, maps task/tool events to spans, maps policy and human-review events to span events, marks rows as projected, and exports through OTLP.

## Prerequisites

- .NET SDK 10.0 or newer
- Docker, for Aspire Dashboard and Docker Compose flows
- `sqlite3`, for local database inspection

## Running Locally

Start Aspire Dashboard in a separate terminal:

```bash
docker run --rm -it \
  -p 18888:18888 \
  -p 4317:18889 \
  -p 4318:18890 \
  -e ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true \
  --name aspire-dashboard \
  mcr.microsoft.com/dotnet/aspire-dashboard:latest
```

Run the collector and point OTLP at Aspire:

```bash
CLAWINDEX_DB_PATH=./data/clawindex-collector.db \
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 \
OTEL_EXPORTER_OTLP_PROTOCOL=grpc \
dotnet run --project src/Clawindex.Collector.Api --urls http://localhost:5000
```

The service initializes the SQLite schema automatically. The default local database path in these examples is `./data/clawindex-collector.db`.

## Running With Docker

```bash
docker compose up --build
```

Docker Compose starts:

- Collector API: `http://localhost:8080`
- Aspire Dashboard: `http://localhost:18888`
- Aspire OTLP/gRPC: `http://localhost:4317`
- Aspire OTLP/HTTP: `http://localhost:4318`

The collector stores SQLite data in the `clawindex-data` Docker volume and exports OTLP to the Aspire Dashboard container.

## Endpoints

- `GET /v1/health`
- `GET /v1/schema`
- `POST /v1/events`
- `POST /v1/events/batch`
- `GET /openapi/v1.json`

## Posting Sample Events

Health:

```bash
curl http://localhost:5000/v1/health
```

Schema:

```bash
curl http://localhost:5000/v1/schema
```

Single policy event:

```bash
curl -X POST http://localhost:5000/v1/events \
  -H 'Content-Type: application/json' \
  --data-binary @examples/policy-evaluated.json
```

Single tool event:

```bash
curl -X POST http://localhost:5000/v1/events \
  -H 'Content-Type: application/json' \
  --data-binary @examples/tool-call.json
```

Batch intake event:

```bash
curl -X POST http://localhost:5000/v1/events/batch \
  -H 'Content-Type: application/json' \
  --data-binary @examples/batch-events.json
```

Complete correlated trace for Aspire:

```bash
curl -X POST http://localhost:5000/v1/events/batch \
  -H 'Content-Type: application/json' \
  --data-binary @examples/phase1-correlated-trace.json
```

Golden trace fixtures:

```bash
curl -X POST http://localhost:5000/v1/events/batch \
  -H 'Content-Type: application/json' \
  --data-binary @examples/golden-happy-path.json

curl -X POST http://localhost:5000/v1/events/batch \
  -H 'Content-Type: application/json' \
  --data-binary @examples/golden-failure-path.json

curl -X POST http://localhost:5000/v1/events/batch \
  -H 'Content-Type: application/json' \
  --data-binary @examples/golden-escalation-path.json
```

For Docker Compose, replace `localhost:5000` with `localhost:8080`.

## Viewing Traces In Aspire

1. Open `http://localhost:18888`.
2. Go to the Traces page.
3. Post `examples/phase1-correlated-trace.json`.
4. Look for service `clawindex-collector`.
5. Open the trace with ID `4bf92f3577b34da6a3ce929d0e0e4736`.

Expected shape:

- `agent.task Generate soil report` is the root span.
- `tool.call calculate_recommendation` appears beneath the task span.
- `policy.evaluated` appears as a span event on the active tool span.
- Clawindex correlation appears as attributes such as `clawindex.trace_id`, `clawindex.task_id`, and `clawindex.agent_id`.

Golden topology checks:

- Happy path: one root task span, one nested tool span, one policy event on the tool span.
- Failure path: task and tool spans both end with error status.
- Escalation path: policy escalation and human review events appear on the task span.

Clawindex IDs that are already valid W3C trace/span IDs are used directly. Non-W3C IDs, such as `trace_abc`, are preserved as `clawindex.trace_id` and mapped to stable valid OTel IDs for export.

## Inspecting SQLite

Use the same path passed to `CLAWINDEX_DB_PATH`.

```bash
sqlite3 ./data/clawindex-collector.db '.schema events'
sqlite3 ./data/clawindex-collector.db 'select count(*) from events;'
sqlite3 ./data/clawindex-collector.db 'select event_id, event_type, source_system, trace_id, task_id, received_at, projection_status, projection_attempts, projected_at, projection_errors from events order by received_at;'
```

Check raw JSON preservation:

```bash
sqlite3 ./data/clawindex-collector.db 'select event_type, length(raw_json), length(payload_json) from events;'
```

Inspect the Docker Compose SQLite volume:

```bash
docker run --rm -v pulse-collector_clawindex-data:/data alpine:latest \
  sh -c "apk add --no-cache sqlite >/dev/null && sqlite3 /data/clawindex-collector.db 'select event_type, trace_id, task_id, projection_status, projection_attempts, projected_at from events order by received_at;'"
```

Reset local SQLite:

```bash
rm -f ./data/clawindex-collector.db ./data/clawindex-collector.db-shm ./data/clawindex-collector.db-wal
```

Reset Docker SQLite volume:

```bash
docker compose down -v
```

## OTLP Configuration

The collector uses the standard OpenTelemetry environment variables:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317
OTEL_EXPORTER_OTLP_PROTOCOL=grpc
```

For Docker Compose, the collector uses:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://aspire-dashboard:18889
OTEL_EXPORTER_OTLP_PROTOCOL=grpc
```

Projection worker settings live under `Clawindex:Projection`:

```json
{
  "Clawindex": {
    "Projection": {
      "Enabled": true,
      "PollIntervalMilliseconds": 1000,
      "BatchSize": 100,
      "MaxAttempts": 3
    }
  }
}
```

## Troubleshooting

- No traces in Aspire: make sure Aspire is running before posting events and `OTEL_EXPORTER_OTLP_ENDPOINT` points to `http://localhost:4317` locally.
- Started-only events do not appear as completed spans: post `examples/phase1-correlated-trace.json`, which includes completion events.
- Docker traces missing: run `docker compose logs clawindex-collector` and confirm the collector is using `http://aspire-dashboard:18889`.
- SQLite has rows but no projection: inspect `projection_status`, `projection_attempts`, `projected_at`, and `projection_errors`.
- Missing parent spans: check collector logs for structured warnings containing `EventId`, `TraceId`, `TaskId`, and `SpanId`.
- Out-of-order lifecycle events: projection retries failed rows up to `Clawindex:Projection:MaxAttempts`; rows that still cannot be correlated stay `failed` with `projection_errors`.
- Duplicate completion events: duplicates are marked projected but do not emit duplicate task/tool spans.
- Aspire login prompt: the README and Compose commands set `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true` for local development.

## Test

```bash
dotnet test Clawindex.Collector.slnx
```
