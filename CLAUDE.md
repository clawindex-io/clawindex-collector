# ClawIndex Collector — Agent Guidance

## What This Is

ClawIndex is durable, vendor-neutral, OpenTelemetry-first observability for an operator's own agent fleet. It is the durable system-of-record that Aspire, which is ephemeral by design, is not.

This repo is the .NET intake, persistence, and OTLP projection layer. The next planned slice is a single-tenant read API over the persisted trace and span state already in SQLite.

## Core Principles

**Pass-through only.** Telemetry is ingested, validated, persisted, and projected to OTLP. The content of event payloads is never inspected, transformed, or modified. ClawIndex is a pipe and a durable store, not a data processor. This is a deliberate liability-driven constraint.

**Single-tenant only.** All reads and rollups are scoped to one operator's own data. Never build anything that reads across tenant boundaries. If a task seems to require it, stop and escalate.

**Open core.** This repo is licensed under AGPL-3.0. There is a separate proprietary managed tier. Never reference or copy private/managed-tier code into this repo.

## Workflow

- Branch off `main` for all work.
- Never commit to `main` directly.

## Product direction (see docs/strategic-decision-record.md)

- The differentiated product is the durable economics-and-accountability layer (cost attribution, unnecessary-escalation spend, bottleneck accountability, trends). The operational/SRE view is a bring-your-own-viewer on-ramp: operators point their existing Grafana/Aspire at the collector.
- Do NOT build a custom SRE/span dashboard. Be a standards-correct OTLP backend instead.
- Ingestion is complete-spans-only. Live open-span watching and real-time "human not responding" alerting are NOT P1 features or claims.
- OTLP conformance is a first-class requirement: "works with your existing viewer" must actually be true.
