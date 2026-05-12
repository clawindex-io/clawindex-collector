# Phase 1 — OTel Projection Spike

## Objective

Extend Clawindex Collector to project validated Clawindex IR events into OpenTelemetry-compatible telemetry.

This phase exists to prove:

```text
Governed agent execution
↓
Clawindex IR
↓
OTel projection
↓
Standard observability tooling
```

This phase is intentionally a spike/proof-of-concept.

---

# Goals

The collector must:

- Read validated persisted events
- Project Clawindex IR into OTel-compatible spans/events
- Export OTLP telemetry
- Preserve correlation identifiers
- Visualize traces in Aspire Dashboard
- Demonstrate governed execution observability

---

# Primary Target

## Required

- Aspire Dashboard

## Stretch Goal

- Grafana Tempo + Grafana visualization

---

# Out of Scope

Do NOT implement:

- Clawindex UI
- Replay engine
- Risk analytics
- Multi-tenant support
- SIEM integrations
- Production scaling
- Full auth model
- Advanced orchestration graphs

---

# Architecture

```text
Agent / bouncer-md
        ↓
Clawindex Collector Intake
        ↓
Validated Event Store
        ↓
Projection Worker
        ↓
OTel Mapping Layer
        ↓
OTLP Export
        ↓
Aspire Dashboard
```

Stretch:

```text
OTLP Export
        ↓
Grafana Tempo
        ↓
Grafana
```

---

# New Components

## Projection Worker

Responsibilities:
- Read persisted validated events
- Transform Clawindex IR into OTel spans/events
- Preserve trace relationships
- Export telemetry

Recommended implementation:
- BackgroundService
- Polling worker
- Queue-ready architecture

---

## OTel Mapping Layer

Responsibilities:
- Map Clawindex events to spans/events
- Assign trace/span relationships
- Apply semantic attributes
- Align with GenAI SemConv where applicable

Design principles:
- Standards-first
- Clawindex-extended
- Vendor-neutral
- Testable
- Versionable

---

## OTLP Exporter

Responsibilities:
- Export spans/events to Aspire Dashboard
- Support configurable OTLP endpoint

---

# Initial Event Mapping

## Root Task Events

```text
agent.task.started
    → root span start

agent.task.completed
    → root span end

agent.task.failed
    → root span error/end
```

---

## Tool Calls

```text
tool.call.started
    → child span start

tool.call.completed
    → child span end

tool.call.failed
    → child span error/end
```

---

## Policy Events

```text
policy.evaluated
    → span event

policy.denied
    → span event + warning attributes

policy.escalated
    → span event
```

---

## Human Review

```text
human.review.requested
    → span event

human.review.completed
    → span event
```

---

# OTel Attribute Strategy

## Use standard OTel and GenAI SemConv first

Examples:

```text
gen_ai.operation.name
gen_ai.tool.name
gen_ai.agent.name
gen_ai.request.model
gen_ai.usage.input_tokens
gen_ai.usage.output_tokens
```

---

## Use clawindex.* only where needed

Examples:

```text
clawindex.schema.version
clawindex.event.type
clawindex.policy.decision
clawindex.policy.id
clawindex.replay.id
clawindex.side_effect.type
```

---

# Trace Correlation Rules

## Trace IDs

If incoming trace_id exists:
- preserve it

If missing:
- generate one

---

## Span Relationships

```text
agent.task → root span
tool.call → child span
policy.* → span event
human.review.* → span event
```

---

# Local Development Requirements

The implementation must support:

## Local .NET execution

Example:

```bash
dotnet run
```

---

## Docker execution

Example:

```bash
docker compose up --build
```

---

# README Requirements

The README.md MUST be updated with:

## Local setup instructions

Include:
- .NET SDK prerequisites
- restore/build/run commands
- local SQLite location
- Aspire Dashboard startup instructions

---

## Docker instructions

Include:
- docker build
- docker compose up
- exposed ports
- OTLP endpoint configuration

---

## Example commands

Include:
- curl examples for posting events
- sample event payload usage
- SQLite inspection examples
- Aspire dashboard access URL

---

# Acceptance Criteria

## AC1 — Aspire Visibility

Given:
- agent.task.started
- tool.call.started
- policy.evaluated
- tool.call.completed
- agent.task.completed

When processed:
- correlated traces appear in Aspire Dashboard

---

## AC2 — Trace Preservation

Given:
- incoming trace_id

When processed:
- the same trace_id appears in Aspire

---

## AC3 — Child Span Relationships

Given:
- tool.call events

When processed:
- tool spans appear beneath task spans

---

## AC4 — Policy Projection

Given:
- policy events

When processed:
- policy metadata appears as span events/attributes

---

## AC5 — Local Developer Experience

Given:
- a new developer

When following README instructions:
- the collector runs locally
- Aspire dashboard becomes accessible
- traces become visible

---

# Stretch Goals

## Grafana Tempo Integration

Add:
- Tempo
- Grafana
- OTLP export configuration

Goal:
- visualize traces in Grafana

---

## Docker Compose Expansion

Add services:
- tempo
- grafana

Expose:
- Grafana dashboard
- Tempo OTLP ingestion

---

# Non-Goals

This phase does NOT attempt to:

- replace observability platforms
- build Clawindex dashboards
- build replay UI
- build governance analytics
- build risk scoring
- build production telemetry pipelines

This phase proves:
- Clawindex IR can project cleanly into OTel ecosystems.

---

# Deliverables

- Projection worker
- OTel mapping layer
- OTLP export support
- Aspire Dashboard integration
- Updated README
- Docker support
- Example traces visible in Aspire

Stretch:
- Grafana Tempo support

---

# Recommended Codex Prompt

```text
Implement Phase 1 — OTel Projection Spike.

Read:
- docs/product-brief.md
- docs/intake-v1.md
- docs/event-envelope.md
- docs/future-otel-mapping.md
- docs/phase1-otel-projection.md

Requirements:
- Read validated persisted events
- Project events into OTel spans/events
- Export OTLP telemetry
- Support Aspire Dashboard locally
- Preserve trace relationships
- Use standards-first OTel attributes
- Use clawindex.* attributes only where required

Do not build:
- replay engine
- Clawindex UI
- analytics
- risk scoring
- multi-tenancy

Update README.md with:
- local setup
- Aspire instructions
- Docker instructions
- curl examples
- OTLP configuration
- troubleshooting guidance

Stretch goal:
- Grafana Tempo integration
```
