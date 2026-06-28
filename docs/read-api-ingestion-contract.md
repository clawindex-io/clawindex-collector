# ClawIndex Collector — Ingestion Contract & Conformance Floor (v0.2)

## Purpose

Define the telemetry ingestion contract for the ClawIndex collector. The collector ingests OpenTelemetry GenAI Semantic Convention (SemConv) telemetry, persists it durably, and projects a defined set of standardized attributes into queryable, agent-keyed fields.

This document defines the **ingestion contract** and the **conformance floor**. It does not define the read API or the persistence schema changes; those are separate issues that depend on this contract.

## What Changed From v0.1

The v0.1 custom event envelope (lifecycle events such as `agent.task.started`, governance events such as `policy.evaluated`, the bouncer-md `source.system` vocabulary) is **abandoned**. ClawIndex is GenAI-SemConv-native. There is no governance event schema in the conventions.

The durable persistence layer (trace/span state, restart recovery, idempotency, placeholder and duplicate handling) is **retained and extended additively**, not rebuilt.

## Core Stance

**Pass-through on content.** The collector never inspects, sanitizes, scrubs, or transforms the *content* of telemetry payloads. Responsibility for payload content — including any sensitive or regulated data — remains entirely with the integrator, under their own enterprise data requirements.

**Projection of the standard is not inspection.** The collector reads a defined allowlist of public OpenTelemetry GenAI SemConv attribute keys (token usage, model, operation, agent identity) and projects them into queryable fields. Reading standardized, published spec attributes is categorically distinct from inspecting payload content and carries no content-handling liability.

**Single-tenant.** All reads, rollups, and comparisons are scoped to one operator's own boundary. No cross-tenant access of any kind.

## Ingestion Model

The collector accepts GenAI SemConv telemetry as spans with attributes. A span carries its operation, provider, model, token usage, and agent identity as span attributes, with span start/end as timing. This replaces the v0.1 model of discrete start/stop lifecycle events correlated into spans.

Span identity, trace correlation, and parent linkage follow standard OpenTelemetry trace/span semantics.

## Two-Tier Validation

Every received span is evaluated at two tiers.

**Tier 1 — Envelope-valid.** The span is well-formed and parseable. Envelope-valid spans are always persisted. The collector does not reject well-formed telemetry; rejecting data would break pass-through neutrality and lose operator history.

**Tier 2 — Conformance-complete.** The span additionally carries every field in the conformance floor (below), each satisfying its rule. Only conformance-complete spans count toward agent statistics that depend on those fields.

A span that is envelope-valid but not conformance-complete is **persisted, projected for timing and status, and flagged non-conformant**. It is never silently dropped.

## Conformance Floor

A span is conformance-complete when it carries all of the following, each satisfying its rule:

| Field | SemConv attribute | Rule |
|---|---|---|
| Operation | `gen_ai.operation.name` | Required, non-empty string |
| Provider | `gen_ai.provider.name` | Required, non-empty string |
| Model | `gen_ai.request.model` | Required, non-empty string |
| Input tokens | `gen_ai.usage.input_tokens` | Required, non-negative integer |
| Output tokens | `gen_ai.usage.output_tokens` | Required, non-negative integer |
| Agent identity | `gen_ai.agent.id` | Required, valid GUID; must not be the nil GUID (`00000000-0000-0000-0000-000000000000`) or the `00000000-0000-0000-0000-000000000001` sentinel |

### Agent identity requirements

Agent identity is a ClawIndex conformance requirement, not an optional SemConv attribute. The product is agent-centric; stable agent identity is the spine of every view.

- The value must be a valid GUID, carried in `gen_ai.agent.id`.
- The nil GUID and the `...0001` sentinel are rejected as conformant values; they indicate a placeholder or gaming attempt.
- The integrator generates one stable GUID per logical agent and persists it in their configuration. It is expected to remain stable for the agent's lifetime.
- The agent GUID is an **identifier, not a secret**. It appears in telemetry, the read API, and the dashboard. It must not be treated as a credential.

## Anti-Gaming Posture

Conformance is surfaced, not hidden. The collector tracks a **per-agent conformance ratio** (conformance-complete spans / total spans for that agent). This ratio is a first-class signal in the agent view, so an integrator who emits SemConv spans without the useful floor fields sees their own instrumentation quality reflected back, rather than silently passing as integrated.

Gaming the integration (placeholder GUIDs, missing token counts, stub attributes) becomes visible in the dashboard instead of producing empty or misleading agent statistics.

## Known Limitations

- The collector cannot enforce **GUID stability** across runs from its side; it can only document the expectation and, in a later slice, surface anomalies (e.g. a GUID seen once and never again).
- Token counts are taken as reported by the integrator. The collector does not independently verify them against provider billing.
- This contract defines ingestion and the conformance floor only. The persistence schema changes (first-class projected fields, conformance flag) and the agent-first read API are separate, dependent issues.

## Out Of Scope For This Contract

- Read API endpoint shapes (separate issue).
- Persistence schema changes (separate issue).
- Cost/pricing enrichment and the executive/ROI view (P2, depends on token fields defined here).
- Any cross-tenant aggregation (forbidden by invariant).

## Amendment: SemConv Transition Tolerance

The OpenTelemetry GenAI Semantic Conventions are in Development status and mid-transition. Some floor attributes have a current key and a prior key, and both are emitted in the wild depending on whether an integrator has opted into the newer convention version. To avoid penalizing conformant integrators for the convention's transition timing, the conformance floor is **tolerant** on transitioning attributes:

- A floor attribute is satisfied if either its current key or its accepted prior key is present and valid.
- The current key is preferred when both are present.
- Provider: accept `gen_ai.provider.name` (current, preferred) or `gen_ai.system` (prior).
- Token and other floor values are read tolerantly with respect to representation (integer or string-encoded integer), since emitters vary.

This tolerance is a transition measure. Prior keys carry a documented sunset and will be dropped once the GenAI conventions stabilize and the installed base has migrated. Until then, accepting both is the honest implementation of the OTel-first promise: an integrator pointing a current OTel stack at ClawIndex should be counted conformant regardless of which convention version their instrumentation defaults to.
