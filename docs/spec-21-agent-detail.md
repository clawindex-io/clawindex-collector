# Implementation Spec — #21: GET /v1/agents/{id} (single agent detail)

## Goal

Return one agent's rollup plus a limited list of its most recent traces, each
trace flagged with its error signal. Read-only. Builds on the #20 pattern; adds
a per-trace aggregation and pagination.

The recent-trace error signal is the point: it surfaces which of an agent's
traces went wrong, feeding the "what are failing agents costing" thesis.

## Reference

- #20 delivered GetAgentRollupsAsync (per-agent rollup) and GET /v1/agents. Reuse the AgentRollup shape and the since/until window handling.
- Store shape from #31: span_state (span_id, trace_id, parent_span_id, agent_id, status, started_at, ended_at, operation, provider, model, input_tokens, output_tokens, is_conformant, attributes_json); trace_state (trace_id, root_span_id, agent_id, status ['open'|'finalized'], started_at, ended_at, updated_at).
- Timestamps are canonical UTC "O" strings; window comparison is lexicographic (Option-1 invariant, same as #20).

## Endpoint

GET /v1/agents/{id}

Path:
- id — MUST be a GUID. If it does not parse as a GUID -> 400.

Query parameters (all optional):
- since (ISO-8601) — inclusive lower bound on ended_at. Default: now - 30 days.
- until (ISO-8601) — exclusive upper bound on ended_at. Default: now.
- limit (int) — max recent traces to return. Default 50. Values > 200 are CLAMPED to 200 (not an error). Values <= 0 or non-numeric -> 400.

## Response (200)

{
  "agent_id": "<guid>",
  "rollup": {
    "span_count": <long>,
    "trace_count": <long>,
    "error_count": <long>,
    "error_rate": <double>,
    "last_seen": "<iso-8601>",
    "conformance_ratio": <double>
  },
  "recent_traces": [
    {
      "trace_id": "<hex>",
      "status": "<open|finalized>",
      "started_at": "<iso-8601>",
      "duration_ms": <long|null>,
      "span_count": <long>,
      "error_count": <long>
    }
  ]
}

- rollup is the same shape/semantics as #20 but for a single agent (WHERE agent_id = @id AND window). No completed_count (consumer derives).
- recent_traces is ordered most-recent-first by started_at DESC, capped at limit.
- last_seen: if the agent has no spans in window, last_seen is null and rollup counts are 0.

## Unknown or quiet agent

If the agent id is a valid GUID but has no spans in the window: return 200 with
zeroed rollup (counts 0, ratios 0, last_seen null) and an empty recent_traces
array. NEVER 404.

Rationale: there is no agent registry — an agent exists only as a side effect of
spans. "No data" cannot be distinguished from "never existed," and conflating a
quiet agent with a missing one would be misleading. 200-for-both also avoids an
enumeration oracle: a prober cannot tell real agent GUIDs from fake ones by
status code.

## Status codes

- 200 — success, including the no-data-in-window case.
- 400 — id is not a GUID; since/until unparseable; since >= until; limit non-numeric or <= 0.
- (No 404. limit > 200 clamps, does not error.)

## Aggregations (two)

### 1. Single-agent rollup
Reuse the #20 aggregation constrained to one agent:
SELECT COUNT(*), COUNT(DISTINCT trace_id), COUNT(CASE WHEN status='error' THEN 1 END),
       error_rate, MAX(ended_at), SUM(is_conformant)*1.0/COUNT(*)
FROM span_state
WHERE agent_id = $id AND agent_id IS NOT NULL
  AND ended_at >= $since AND ended_at < $until;
Returns a single AgentRollup, or a zeroed rollup if no rows. Refactor/reuse the
#20 SQL rather than duplicating it.

### 2. Recent-trace rollup (new)
Per-trace aggregation over this agent's spans, most recent first, limited.
Pull the error_count and span_count from the span aggregation, but pull the
authoritative trace lifecycle status and duration bounds from trace_state (NOT
from the windowed spans — a window that clips some of a trace's spans must not
distort the trace's real start/end).

Preferred single query: aggregate span_state GROUP BY trace_id for this agent in
window, LEFT JOIN trace_state ON trace_id to get status, started_at, ended_at:
SELECT s.trace_id,
       COUNT(*)                                    AS span_count,
       COUNT(CASE WHEN s.status='error' THEN 1 END) AS error_count,
       t.status                                    AS trace_status,
       t.started_at                                AS trace_started_at,
       t.ended_at                                  AS trace_ended_at
FROM span_state s
LEFT JOIN trace_state t ON t.trace_id = s.trace_id
WHERE s.agent_id = $id AND s.agent_id IS NOT NULL
  AND s.ended_at >= $since AND s.ended_at < $until
GROUP BY s.trace_id, t.status, t.started_at, t.ended_at
ORDER BY t.started_at DESC
LIMIT $limit;
If the LEFT JOIN proves impractical, a two-step fallback (aggregate span_state,
then read trace_state for the returned trace_ids) is acceptable — call out the
choice in the plan.

duration_ms = (trace_ended_at - trace_started_at) in milliseconds, computed from
trace_state values; null when trace status = 'open' / trace_ended_at is null.
status comes from trace_state. error_count comes from the span aggregation. Both
are real facts; do not conflate them.

## Single-tenant / no cross-tenant

- All queries filter WHERE agent_id = $id within the single tenant. Written so
  multi-tenancy is a later WHERE tenant_id addition, not a restructure. State
  explicitly in the plan.

## Constraints

- Read-only. No mutation.
- Pass-through: only projected columns read; no payload content inspection.
- No fields the store lacks (no cost/p95/baselines).
- limit MUST be enforced in SQL (LIMIT), not by fetching all and trimming in memory.

## Endpoint wiring

GET /v1/agents/{id} in Program.cs. Thin handler: validate id is GUID, parse/
default since/until (reuse #20 logic — factor it if duplicated), parse/clamp
limit, call the two repository methods, assemble the response object, return
Results.Ok(...). Reuse the Rejected(...) 400 shape.

## Tests

Follow the #20 / existing fixture pattern (temp SQLite; seed via
UpsertSpanStateAsync and UpsertTraceStateAsync; call repository methods and the
endpoint). Add:

- Agent with spans across multiple traces -> rollup counts correct; recent_traces
  lists those traces with correct per-trace span_count and error_count.
- Per-trace error signal: a trace with an 'error' span -> that trace's error_count
  > 0; a clean trace -> error_count 0.
- duration_ms: finalized trace -> non-null, correct ms; open trace -> null.
- status: finalized vs open reflected from trace_state.
- duration integrity: a trace whose earliest span is clipped by the window still
  reports its authoritative trace_state duration, not the windowed-span span.
- limit: seed > limit traces, default 50 caps correctly; explicit limit respected;
  limit > 200 clamps to 200; ordering is most-recent-first by started_at.
- Unknown/quiet agent (valid GUID, no spans in window) -> 200, zeroed rollup,
  empty recent_traces (NOT 404).
- Non-GUID id -> 400.
- since >= until -> 400; unparseable timestamp -> 400; limit <= 0 -> 400.
- Window excludes out-of-window traces from both rollup and recent_traces.
- Placeholder spans (null agent_id) never appear and never inflate counts.
- Use FakeTimeProvider for any default-window (trailing 30 day) endpoint test.
- Conformance-ratio seeding for a single agent must use UpsertSpanStateAsync with
  non-null agent_id and is_conformant=0 (not MakeSpan(isConformant:false)).

## Out of scope

- GET /v1/agents/{id}/traces and GET /v1/traces/{id} span-tree drill-down (#22).
- Cost/economics enrichment.
- Numeric-timestamp hardening (its own follow-up).
- Removing dead v0.1 repository methods (#29).

## Workflow

- Branch off main. Never commit to main directly. Open a PR for review.
