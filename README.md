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

The projection worker polls unprojected rows from SQLite, maps task/tool events to durable span lifecycle state, maps policy and human-review events to span events, marks rows as projected, and exports completed traces through OTLP.

Durable span state lives in SQLite rather than process memory. On startup the collector initializes the lifecycle tables, resets any `in_progress` projection rows back to `pending`, logs the number of open traces and spans recovered from SQLite, and then continues projecting new events against that persisted state. Open or incomplete spans remain queryable after a restart.

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

## Durable Span State

Phase 1.2 persists trace correlation and span lifecycle state in these SQLite tables:

- `events`: validated event envelopes plus projection status (`pending`, `in_progress`, `failed`, `projected`).
- `trace_state`: one durable row per trace with `trace_id`, `root_span_id`, task/agent correlation, lifecycle status, and start/end timestamps.
- `span_state`: durable root and child span rows with parent linkage, source start/end event IDs, lifecycle status, timestamps, and projected attributes JSON.
- `event_span_map`: idempotent event-to-span relationships for source start/end events, span events, duplicate lifecycle events, and instant events.

Lifecycle rules:

- `agent.task.started` opens or updates `trace_state` and creates the root `agent.task` span.
- `agent.task.completed` closes the root span and marks the trace `completed`.
- `agent.task.failed` closes the root span and marks the trace and root span `error`.
- `tool.call.started` creates an open child span under the recovered root/task span when possible.
- `tool.call.completed` closes the matching child span as `completed`.
- `tool.call.failed` closes the matching child span as `error`.
- `policy.*` and `human.review.*` attach to the best open span and preserve the relationship in `event_span_map`.
- Orphan lifecycle or policy events create deterministic placeholder state, log a structured warning, and keep the event mapped instead of dropping it.

Duplicate event projection is guarded by `event_span_map.event_id`. Duplicate completion events map as duplicate lifecycle events and do not reopen or corrupt already closed spans.

## Inspecting SQLite

Use the same path passed to `CLAWINDEX_DB_PATH`.

```bash
sqlite3 ./data/clawindex-collector.db '.schema events'
sqlite3 ./data/clawindex-collector.db '.schema trace_state'
sqlite3 ./data/clawindex-collector.db '.schema span_state'
sqlite3 ./data/clawindex-collector.db '.schema event_span_map'
sqlite3 ./data/clawindex-collector.db 'select count(*) from events;'
sqlite3 ./data/clawindex-collector.db 'select event_id, event_type, source_system, trace_id, task_id, received_at, projection_status, projection_attempts, projected_at, projection_errors from events order by received_at;'
```

Inspect durable traces:

```bash
sqlite3 ./data/clawindex-collector.db \
  'select trace_id, root_span_id, task_id, agent_id, status, started_at, ended_at, updated_at from trace_state order by updated_at;'
```

Inspect durable spans:

```bash
sqlite3 ./data/clawindex-collector.db \
  'select span_id, trace_id, parent_span_id, span_kind, status, source_start_event_id, source_end_event_id, started_at, ended_at from span_state order by started_at;'
```

Inspect event-to-span relationships:

```bash
sqlite3 ./data/clawindex-collector.db \
  'select event_id, trace_id, span_id, relationship_type, created_at from event_span_map order by created_at;'
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

Simulate restart recovery:

```bash
CLAWINDEX_DB_PATH=./data/clawindex-collector.db \
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 \
OTEL_EXPORTER_OTLP_PROTOCOL=grpc \
dotnet run --project src/Clawindex.Collector.Api --urls http://localhost:5000
```

Post only a task start event, stop the collector, then start it again with the same `CLAWINDEX_DB_PATH`. The startup log reports recovered open traces/spans. Post the matching tool and task completion events after restart, then inspect `trace_state`, `span_state`, and Aspire to confirm the new events attached to the recovered state.

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
- Out-of-order lifecycle events: the collector creates placeholder durable state when needed, then maps the event so the evidence remains inspectable.
- Duplicate completion events: duplicates are marked projected but do not emit duplicate task/tool spans.
- Aspire login prompt: the README and Compose commands set `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true` for local development.

Known limitations:

- Completed traces are exported when the root task span closes; there is no replay engine for manually re-exporting historical traces.
- Placeholder spans are deterministic and inspectable, but Phase 1.2 does not attempt advanced repair beyond later events attaching to the same durable IDs.
- SQLite access is suitable for the local spike. Production queueing, distributed locking, and multi-tenant authorization are intentionally out of scope.
- Analytics, risk scoring, and Clawindex UI views are not implemented in this phase.

## Test

```bash
dotnet test Clawindex.Collector.slnx
```
