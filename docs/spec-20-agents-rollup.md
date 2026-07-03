# Implementation Spec — #20: GET /v1/agents (per-agent rollup)

## Goal

Add a read endpoint that returns per-agent rollups aggregated from the durable
span store. This is the first read API surface and the first aggregation. It
exposes what a generic OTLP viewer cannot compute (agent-level rollups, keyed on
the agent GUID, including the conformance ratio) and feeds the economics layer.

Read-only. No store mutation. No new ingestion.

## Reference

- Contract: docs/read-api-ingestion-contract.md.
- Product direction: docs/strategic-decision-record.md (economics/accountability is the product; SRE is bring-your-own-viewer; this endpoint is read-API surface, not a custom dashboard view).
- Store shape from #31: span_state carries span_id, trace_id, parent_span_id, agent_id (GUID string or null), status, started_at, ended_at, operation, provider, model, input_tokens, output_tokens, is_conformant (INTEGER 0/1), attributes_json.

## Endpoint

GET /v1/agents

Query parameters (all optional):
- since (ISO-8601 timestamp) — inclusive lower bound on span ended_at. Default: now - 30 days.
- until (ISO-8601 timestamp) — exclusive upper bound on span ended_at. Default: now.

Returns: JSON array of agent rollup objects (see fields). Empty array if no agents in window. 200 on success. 400 on unparseable since/until or if since >= until.

## Aggregation

A single new repository method, e.g. GetAgentRollupsAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken).

SQL: aggregate span_state, GROUP BY agent_id, over the window.

Filter rules (all must hold):
- agent_id IS NOT NULL — excludes placeholder spans (which have null agent_id) and any span without a resolved agent identity. This is the "real agent activity only" filter.
- ended_at >= @since AND ended_at < @until — the window. (Every projected span has a non-null ended_at under complete-spans-only, so this is safe.)

Per-agent fields (computed in SQL):
- agent_id            — the group key (GUID string)
- span_count          — COUNT(*)
- trace_count         — COUNT(DISTINCT trace_id)
- error_count         — COUNT of spans whose status = 'error'
- completed_count     — span_count - error_count (non-error real spans)
- error_rate          — error_count * 1.0 / span_count
- last_seen           — MAX(ended_at)
- conformance_ratio   — SUM(is_conformant) * 1.0 / COUNT(*)   (is_conformant is INTEGER 0/1, so this is safe)

Notes:
- Non-conformant-but-real spans (valid agent_id, missing floor fields) ARE included: they count in span_count and drag conformance_ratio down. That is the anti-gaming signal working as intended.
- status = 'error' is the ONLY error bucket. OTLP status is 'unset' by default and only 'error' when explicitly set, so error_rate reflects explicitly-flagged errors, not inferred failures. Do not invent statuses or infer failure the emitter did not declare.

## New record

AgentRollup (read model, distinct from SpanState):
agent_id (string), span_count (long), trace_count (long), completed_count (long),
error_count (long), error_rate (double), last_seen (DateTimeOffset),
conformance_ratio (double).

## Single-tenant / no cross-tenant

- The deployment is single-tenant today, so GROUP BY agent_id is inherently within one tenant.
- Write the query so that adding multi-tenancy later is a WHERE tenant_id = @tenant addition, NOT a restructure. Do not aggregate across any tenant boundary. State this explicitly in the plan.

## Constraints

- Read-only. No writes, no mutation of span_state/trace_state.
- Pass-through: only already-projected columns are read; no payload content inspection.
- Do not expose fields the store does not hold (no cost, no p95, no baselines — those are later economics-layer enrichment and would violate claims discipline).

## Endpoint wiring

Add GET /v1/agents in Program.cs (thin handler: parse/validate since/until, call
GetAgentRollupsAsync, return JSON). EventRepository is already registered.

## Tests

Follow the existing fixture pattern (temp SQLite, seed via the repository upsert
methods or via DurableSpanSink, then call the endpoint / repository method). Add:
- Two agents with distinct spans -> two rollup rows with correct span_count and trace_count (use an agent whose spans span multiple traces to verify COUNT DISTINCT trace_id).
- Error rate: agent with mixed 'error' and non-error spans -> correct error_count and error_rate.
- Conformance ratio: agent with a mix of conformant and non-conformant (but real, agent_id-bearing) spans -> correct ratio between 0 and 1.
- Placeholder exclusion: a trace with a placeholder parent (null agent_id) -> placeholder does NOT appear as an agent and does not inflate any count.
- Window: spans outside since/until are excluded, spans inside included; default window is trailing 30 days.
- Bad params: since >= until -> 400; unparseable timestamp -> 400.
- Empty: no spans in window -> empty array, 200.

## Out of scope

- GET /v1/agents/{id} and drill-down (#21/#22).
- Any cost/economics enrichment.
- Pagination (a fleet is small; all agents in one response is fine for now).
- Removing dead v0.1 repository methods (#29).

## Workflow

- Branch off main. Never commit to main directly. Open a PR for review.
