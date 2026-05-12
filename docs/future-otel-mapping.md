# Future OTel Mapping Strategy

## Purpose

This document defines the future mapping layer from Clawindex IR to OpenTelemetry and GenAI Semantic Conventions.

This functionality is intentionally excluded from Collector v0.1.

## Planned Pipeline

Raw Event Intake
        ↓
Validated Event Store
        ↓
Mapping Worker
        ↓
OTel Span/Event Generation
        ↓
OTLP Export
        ↓
Grafana / Aspire / DataDog / App Insights

## Initial Mapping Concepts

| Clawindex Event | OTel Projection |
|---|---|
| policy.evaluated | span event |
| tool.call.started | span start |
| tool.call.completed | span end |
| agent.task.started | root span start |
| agent.task.completed | root span end |

## Design Principles

- Preserve Clawindex IR as canonical operational model
- Use OTel as interoperability layer
- Support GenAI SemConv alignment
- Remain vendor-neutral
- Enable replay and debugging later
