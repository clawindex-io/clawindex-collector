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

## Project documents

- [Strategic decision record](docs/strategic-decision-record.md) — product definition and the decisions that direct the build.
- [Ingestion contract & conformance floor](docs/read-api-ingestion-contract.md).
