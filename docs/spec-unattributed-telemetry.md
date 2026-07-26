# Implementation Spec — Unattributed Telemetry ("Dark Activity") Endpoint

## Goal

Surface spans that could not be attributed to any agent — invalid or absent
clawindex.agent.id — which are currently filtered out of /v1/agents entirely
(agent_id IS NOT NULL). Per docs/spec-fleet-dashboard.md (dashboard repo),
this is the "preferred, small" resolution to the documented API gap blocking
Band 3's DARK ACTIVITY section.

Frame this as a security/governance finding, not a cost/economics one. An
unattributed span means an unregistered or unknown source is emitting
telemetry — categorically different from an identified-but-uncostable agent.

## Reference

- #20/#21 established the AgentRollup read pattern and since/until window
  handling. Reuse both.
- #43 established clawindex.agent.id validation (AgentIdValidator.cs) as the
  single source of truth for what counts as a valid agent id.
- docs/read-api-ingestion-contract.md defines the existing read-API contract
  this endpoint extends.

## Scope

New endpoint: GET /v1/unattributed (or a field addition — see "Open question"
below; default assumption is a new endpoint unless review prefers otherwise).

For the current window (same since/until / trailing-30-day default as
/v1/agents), over span_state rows where agent_id IS NULL or fails
AgentIdValidator:

- count: total unattributed span count in the window.
- service_names: distinct service.name values observed among them.
- models: distinct model values observed among them (gen_ai.request.model or
  equivalent SemConv attribute, wherever it's captured on the span).
- time_range: earliest and latest span timestamp in the window.

## Constraints

- Single-tenant only. Read-time only over persisted span_state. No writes,
  no schema changes to ingestion or projection.
- Do NOT expose span content, payload, or any per-span detail beyond the
  four aggregate fields above. This is a metadata/count surface, not a
  drill-down — the entire point is these spans have no attributable
  identity, so nothing here should attempt to reconstruct one.
- No cross-tenant anything, consistent with every other endpoint in this
  API.

## Response shape

GET /v1/unattributed?since=...&until=...

Returns a JSON object with these fields:
  count           - integer, total unattributed span count in the window
  service_names   - array of strings, distinct service.name values observed
  models          - array of strings, distinct model values observed
  earliest_seen   - ISO 8601 timestamp string or null
  latest_seen     - ISO 8601 timestamp string or null

Example with data: count 42, service_names ["unknown-checkout-svc",
"legacy-batch-job"], models ["gpt-4o-mini", "claude-3-haiku"],
earliest_seen "2026-07-01T00:00:00Z", latest_seen "2026-07-24T18:32:00Z".

If count is 0, service_names and models return as empty arrays, earliest_seen
and latest_seen return null. Do not omit the fields — the dashboard's honesty
principle (present, do not fabricate or omit) applies to this endpoint's
zero-case as much as to the dashboard itself.

## Open question for review

Endpoint vs. field: a new GET /v1/unattributed is proposed over adding a
field to GET /v1/agents, since unattributed spans are explicitly NOT
agent-scoped — bolting them onto the agent rollup response would misrepresent
what the field means. Confirm this reasoning holds, or state a preferred
alternative.

## Tests

Following existing fixture/exact-assertion discipline (AgentRollupTests.cs
pattern):
- Fixture with a mix of attributed and unattributed spans (varied
  service.name and model values, spread across timestamps). Assert count,
  service_names, models, earliest_seen, latest_seen are all exactly correct.
- Fixture with zero unattributed spans. Assert count=0, empty arrays, null
  timestamps — not omitted fields, not exceptions.
- Fixture where attributed spans exist alongside unattributed ones. Assert
  attributed spans are correctly excluded from every returned figure.
- Since/until window handling test, consistent with #20's FakeTimeProvider
  pattern for the default trailing-30-day window.

## Out of scope

- Any drill-down into individual unattributed spans.
- Any attempt to infer or reconstruct a likely agent identity for these
  spans — they are unattributed by definition.
- Alerting, thresholds, or flagging logic — this endpoint reports numbers;
  interpretation belongs to the dashboard/operator, same principle as
  /v1/agents.

## Workflow

Branch off main. Never commit to main. Open a PR for review.
