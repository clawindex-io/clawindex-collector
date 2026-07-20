# clawindex-collector

Durable, vendor-neutral, OpenTelemetry-first telemetry collector for AI agent fleets. Part of the ClawIndex open core (AGPL-3.0).

**Status: early.** Under active development. Interfaces and schema may change.

## What this is

ClawIndex is the durable system-of-record for agent telemetry — the thing that retains and makes sense of your agent fleet's history, which ephemeral dev tools (like the Aspire dashboard) are not built to do. The collector ingests OpenTelemetry GenAI Semantic Convention (SemConv) spans, validates them against a conformance floor, and persists them durably.

The collector is a standards-correct OTLP backend. You point your existing OpenTelemetry-instrumented agents at it, and you view the telemetry in whatever OTLP-compatible viewer you already run (Grafana, Aspire, Jaeger, and so on). ClawIndex does not ship its own span dashboard — the OTel ecosystem already renders spans well, and reinventing that is not where ClawIndex adds value.

## What it does today

- Receives OTLP/HTTP trace data (POST /v1/traces, protobuf).
- Validates incoming GenAI SemConv spans against a conformance floor (operation, provider, model, input/output tokens, and a required agent-id GUID).
- Records conformance: spans that lack the useful floor fields are accepted but flagged non-conformant, so instrumentation quality is visible rather than silently passing.
- Pass-through on payload content: only the defined SemConv allowlist is read for projection. Payload content is never inspected, transformed, or scrubbed — that responsibility stays with the integrator.

## Where it's going

- Durable projection of complete spans into the persistent trace/span store.
- A single-tenant read API over persisted state.
- A durable agent-fleet economics and accountability layer — cost attribution, spend on unnecessary escalations, underperformance and bottleneck accountability, and trends over time. This is the differentiated value and the focus of the project; it is the question generic observability tools do not answer.

## Design constraints

- Pass-through on content. Payload content is never inspected, sanitized, or transformed. Reading standardized public SemConv attribute keys is not content inspection.
- Complete-spans-only ingestion. Spans are ingested when complete (ended). This aligns with how OpenTelemetry is overwhelmingly emitted and keeps the foundation simple and durable.
- Single-tenant. All reads, rollups, and analytics are scoped to one operator's own boundary. No cross-tenant data, ever.
- Bring-your-own-viewer for operational/SRE views. ClawIndex is an OTLP backend; existing viewers render spans. Custom UI is reserved for the economics/accountability analysis the ecosystem does not provide.

## License

AGPL-3.0. Copyright Range Point AI, 2026. See [LICENSE](LICENSE).

There is a separate proprietary managed tier (the hosted economics/accountability layer). This open-core repository never depends on or references it.

## Fan-out / destinations

ClawIndex can forward incoming telemetry byte-identically to any number of downstream OTLP/HTTP endpoints (your Grafana, Aspire, Datadog agent, etc.) so you only need to configure one collector endpoint on your agents.

Forwarding is durable (backed by SQLite) and off the request path — a slow or dead destination never delays or breaks `/v1/traces`. v1 makes one delivery attempt per item; failed deliveries are marked and not retried (best-effort, lossy under destination failure).

> **Note:** v1 forwarding is best-effort. If a destination is unreachable at delivery time, the payload is lost for that destination. Retry policy is a future slice.

### Configuration — appsettings.json

```json
{
  "Clawindex": {
    "Destinations": [
      {
        "Name":     "aspire",
        "Type":     "otlp-http",
        "Endpoint": "http://aspire:18890/v1/traces",
        "Enabled":  true
      },
      {
        "Name":     "datadog",
        "Type":     "otlp-http",
        "Endpoint": "http://datadog-agent:4318/v1/traces",
        "Enabled":  true,
        "Headers":  { "DD-API-KEY": "..." }
      }
    ]
  }
}
```

For Aspire: use the **HTTP** OTLP endpoint (port **18890**), not the gRPC default (18889).

### Configuration — environment variables only

Use the ASP.NET Core double-underscore convention. Hyphens in header key names are literal characters and do not need escaping:

```
Clawindex__Destinations__0__Name=aspire
Clawindex__Destinations__0__Type=otlp-http
Clawindex__Destinations__0__Endpoint=http://aspire:18890/v1/traces
Clawindex__Destinations__0__Enabled=true

Clawindex__Destinations__1__Name=datadog
Clawindex__Destinations__1__Type=otlp-http
Clawindex__Destinations__1__Endpoint=http://datadog-agent:4318/v1/traces
Clawindex__Destinations__1__Enabled=true
Clawindex__Destinations__1__Headers__DD-API-KEY=secret
```

Adding or changing a destination requires only a **container restart** — never an image rebuild.

> **Secret management:** supply auth header values via environment variables or a mounted config file. Do not commit secret values to appsettings.json.

### docker-compose example

```yaml
services:
  collector:
    image: clawindex-collector:latest
    ports:
      - "5000:8080"
    environment:
      CLAWINDEX_DB_PATH: /data/clawindex.db
      Clawindex__Destinations__0__Name: aspire
      Clawindex__Destinations__0__Type: otlp-http
      Clawindex__Destinations__0__Endpoint: http://aspire:18890/v1/traces
      Clawindex__Destinations__0__Enabled: "true"
    volumes:
      - clawindex-data:/data

  aspire:
    image: mcr.microsoft.com/dotnet/aspire-dashboard:latest
    ports:
      - "18888:18888"   # UI
      - "18890:18890"   # OTLP/HTTP

volumes:
  clawindex-data:
```

## Project documents

- [Strategic decision record](docs/strategic-decision-record.md) — product definition and the decisions that direct the build.
- [Ingestion contract & conformance floor](docs/read-api-ingestion-contract.md).
- [Fan-out spec](docs/spec-fanout.md) — design rationale for the forwarding feature.
