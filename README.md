# Clawindex Collector v0.1

Clawindex Collector is a narrow intake service for governed agent telemetry. It validates event envelopes, generates missing event IDs, stamps `received_at`, preserves raw inbound JSON, and persists accepted events to SQLite.

## Prerequisites

- .NET SDK 10.0 or newer
- Optional: Docker

## Run Locally

```bash
dotnet run --project src/Clawindex.Collector.Api --urls http://localhost:5000
```

By default the SQLite database is created under the app output directory at `data/clawindex-collector.db`.

Override the database path:

```bash
CLAWINDEX_DB_PATH=./data/clawindex-collector.db dotnet run --project src/Clawindex.Collector.Api --urls http://localhost:5000
```

## Endpoints

- `GET /v1/health`
- `GET /v1/schema`
- `POST /v1/events`
- `POST /v1/events/batch`
- `GET /openapi/v1.json`

## Curl Examples

Health:

```bash
curl http://localhost:5000/v1/health
```

Schema:

```bash
curl http://localhost:5000/v1/schema
```

Single event:

```bash
curl -X POST http://localhost:5000/v1/events \
  -H 'Content-Type: application/json' \
  -d '{
    "schema_version": "0.1.0",
    "event_type": "policy.evaluated",
    "occurred_at": "2026-05-11T22:15:00Z",
    "source": {
      "system": "bouncer-md"
    },
    "payload": {
      "decision": "deny"
    }
  }'
```

Invalid event:

```bash
curl -X POST http://localhost:5000/v1/events \
  -H 'Content-Type: application/json' \
  -d '{
    "schema_version": "0.1.0",
    "occurred_at": "2026-05-11T22:15:00Z",
    "source": {
      "system": "bouncer-md"
    },
    "payload": {
      "decision": "deny"
    }
  }'
```

Batch event:

```bash
curl -X POST http://localhost:5000/v1/events/batch \
  -H 'Content-Type: application/json' \
  -d '{
    "events": [
      {
        "schema_version": "0.1.0",
        "event_type": "agent.task.started",
        "occurred_at": "2026-05-11T22:15:00Z",
        "source": {
          "system": "test-agent"
        },
        "payload": {
          "task_name": "Generate soil report"
        }
      },
      {
        "schema_version": "0.1.0",
        "occurred_at": "2026-05-11T22:15:00Z",
        "source": {
          "system": "test-agent"
        },
        "payload": {
          "task_name": "Missing event type"
        }
      }
    ]
  }'
```

## Docker

```bash
docker compose up --build
```

The service listens on `http://localhost:8080` in Docker and stores SQLite data in the `clawindex-data` volume.

## Test

```bash
dotnet test Clawindex.Collector.slnx
```
