# Implementation Spec — Unattributed Telemetry ("Dark Activity") in GET /v1/agents

## Goal

Surface spans that could not be attributed to any agent — invalid or absent
clawindex.agent.id — which are currently filtered out of /v1/agents entirely
(agent_id IS NOT NULL). Per docs/spec-fleet-dashboard.md (dashboard repo),
this is the "preferred, small" resolution to the documented API gap blocking
Band 3's DARK ACTIVITY section.

Frame this as a security/governance finding, not a cost/economics one. An
unattributed span means an unregistered or unknown source is emitting
telemetry — categorically different from an identified-but-uncostable agent.

## Design: single call, not a separate endpoint

Unattributed telemetry is returned as a new top-level sibling field in the
existing GET /v1/agents response, NOT a separate GET /v1/unattributed
endpoint. The dashboard makes one fetch to /v1/agents; it should not need a
second round trip to get the other half of the same picture.

This field sits alongside the agents array, not inside it and not on any
individual agent row — unattributed spans are explicitly not agent-scoped,
so they must not be shaped like one.

Response shape:

{
  "agents": [ ...existing per-agent rollups, unchanged... ],
  "unattributed": {
    "count": 42,
    "service_names": ["unknown-checkout-svc", "legacy-batch-job"],
    "models": ["gpt-4o-mini", "claude-3-haiku"],
    "earliest_seen": "2026-07-01T00:00:00Z",
    "latest_seen": "2026-07-24T18:32:00Z"
  }
}

If count is 0: service_names and models return as empty arrays, earliest_seen
and latest_seen return null. Do not omit these fields — the dashboard's
honesty principle (present, do not fabricate or omit) applies to this
zero-case exactly as it does to the dashboard itself.

## MVP framing — no backward-compatibility concern

There are no current users of this API. The response shape is changing from
a bare array to an object with agents + unattributed keys, and that is fine
— there is nothing external to preserve compatibility with.

The ONE thing this must remain compatible with: the chaos fleet (tools/chaos/
chaos.py) and the dashboard's existing verification flow against it. The
dashboard's data.js currently parses the /v1/agents response as a bare
array; this response-shape change means data.js needs a corresponding
one-line update (read response.agents instead of the response itself) as
part of landing this feature end-to-end. That is expected, coordinated work
— not a compatibility break to design around.

## Reference

- #20/#21 established the AgentRollup read pattern and since/until window
  handling. Reuse both for the unattributed aggregation's window.
- #43 established clawindex.agent.id validation (AgentIdValidator.cs) as the
  single source of truth for what counts as a valid agent id.
- docs/read-api-ingestion-contract.md defines the existing read-API contract
  this response extends.
- The locked GenAI SemConv conformance floor (from the conformance-floor
  design discussion) names gen_ai.request.model as the model attribute,
  alongside gen_ai.operation.name, gen_ai.provider.name, and the two
  usage.*_tokens fields. The "models" field below uses this same attribute
  for consistency with how conformance and cost estimation already key off
  it elsewhere in the system.

## Scope

For the current window (same since/until / trailing-30-day default already
used by /v1/agents), over span_state rows where agent_id IS NULL or fails
AgentIdValidator:

- count: total unattributed span count in the window.
- service_names: distinct service.name values observed among them.
- models: distinct gen_ai.request.model values observed among them.
- earliest_seen / latest_seen: earliest and latest span timestamp in the
  window.

## OPEN QUESTION — must resolve during planning, before implementation

It is not yet confirmed whether spans that fail AgentIdValidator are:
(a) persisted with the invalid agent_id value as-is,
(b) nulled out before persistence, or
(c) rejected entirely and never stored.

This determines the actual WHERE clause for "unattributed" — if (b) or (c),
"agent_id IS NULL" alone may be sufficient and "fails AgentIdValidator" is
dead code; if (a), the query must independently re-validate agent_id at read
time, which duplicates validation logic between ingestion and this read path
and should be flagged as a maintenance point (two places to keep in sync).

The plan step must read the actual ingestion/mapper code (wherever
AgentIdValidator is invoked in the write path, e.g. the SemConv conformance
validator and/or DurableSpanSink) to confirm which of (a)/(b)/(c) is true,
and write the query accordingly. Do not assume; verify against the code.

## Constraints

- Single-tenant only. Read-time only over persisted span_state. No writes,
  no schema changes to ingestion or projection.
- Do NOT expose span content, payload, or any per-span detail beyond the
  five aggregate fields above. This is a metadata/count surface, not a
  drill-down — the entire point is these spans have no attributable
  identity, so nothing here should attempt to reconstruct one.
- No cross-tenant anything, consistent with every other endpoint in this
  API.

## Tests

Following existing fixture/exact-assertion discipline (AgentRollupTests.cs
pattern):
- Fixture with a mix of attributed and unattributed spans (varied
  service.name and gen_ai.request.model values, spread across timestamps).
  Assert the unattributed object's count, service_names, models,
  earliest_seen, latest_seen are all exactly correct, and the agents array
  is unaffected.
- Fixture with zero unattributed spans. Assert count=0, empty arrays, null
  timestamps — not omitted fields, not exceptions.
- Fixture confirming attributed spans are correctly excluded from every
  unattributed figure.
- Since/until window handling test, consistent with #20's FakeTimeProvider
  pattern for the default trailing-30-day window.
- A test specifically exercising whichever of (a)/(b)/(c) above turns out to
  be true, so the resolved behavior has a regression test pinning it.
- Confirm the chaos fleet still parses correctly end-to-end against the new
  response shape (agents key present and correctly populated).

## Out of scope

- Any drill-down into individual unattributed spans.
- Any attempt to infer or reconstruct a likely agent identity for these
  spans — they are unattributed by definition.
- Alerting, thresholds, or flagging logic — this response reports numbers;
  interpretation belongs to the dashboard/operator, same principle as the
  rest of /v1/agents.

## Workflow

Branch off main. Never commit to main. Open a PR for review.
