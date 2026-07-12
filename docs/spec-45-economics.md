# Implementation Spec — #45: v1 Economics (near-truth cost estimation)

## Goal

Compute near-truth cost ESTIMATES from persisted token data, and surface the
accountability findings that are the product's differentiated value: which agents
are expensive, what failing/stuck traces cost, and where cost cannot be determined.

Open core. Cloud provides the knobs and dials later (current-pricing maintenance,
negotiated-rate override tooling, higher analytics).

## Non-negotiable philosophy

- Every dollar figure is an ESTIMATE. Never present it as authoritative.
- The real value is RELATIVE accountability (rankings, patterns). This survives
  imperfect pricing because a uniform price delta scales all agents equally.
- NEVER guess. A span without token data cannot be costed. Report it as an
  explicit coverage gap ("could not determine"). Do NOT infer, average, or
  extrapolate missing token counts. Fabricated cost would corrupt the rankings
  (the one reliable output) and hide the instrumentation problem.

## Pricing model

### Storage shape (date-ready, flat behavior in v1)
A pricing table with rows keyed by (provider, model, token_type) carrying:
- price_per_token (or price per 1M tokens — pick one and be consistent)
- effective_from (date)
- last_confirmed (date) — when this price was last verified against the provider

v1 BEHAVIOR IS FLAT: always resolve to the latest row for a (provider, model,
token_type). Do not implement date-range resolution.

BUT: include effective_from in the schema from day one, and make the lookup
function TAKE the span's timestamp as a parameter (even though v1 ignores it).
This makes adding proper effective-date resolution later a function change, not
a schema/API restructure. Same pattern as the multi-tenancy readiness in #20.

### Source
Ships as a snapshot of PUBLIC LIST pricing. Zero customer configuration required
for the product to be useful.

### Last-known-price fallback
If a model's price cannot be resolved (provider stopped publishing, unknown
model string), fall back to the last known price for that model and flag the
estimate as stale, carrying last_confirmed. If there is NO known price at all
for the model, the span is UNCOSTED (coverage gap) — do not guess.

## Cost computation

Per span, computed at READ time (never written back to span_state — it would go
stale when pricing changes, and it is derivable):

  cost = (input_tokens * input_price) + (output_tokens * output_price)

Looked up by (provider, model). Only for spans that have BOTH token values.
No caching adjustment in v1 (see limitations).

## Coverage gap

- A span lacking token data, or whose model has no resolvable price, is UNCOSTED.
- Per agent (and per trace), surface COST COVERAGE: costed_spans / total_spans.
- Report the uncosted portion as unquantified real spend — NOT as zero.
- User-facing framing (use this language): "Estimated spend $X, covering N% of
  this agent's activity. The remaining M% has no token data and represents
  additional real spend that could not be determined."
- The gap is itself an accountability finding: an agent you cannot cost is a
  governance problem.

## Outputs (read endpoints)

Consistent with #20/#21 conventions (since/until window, default trailing 30d,
single-tenant, read-only, honest primitives).

1. Per-agent estimated spend — extend or complement the agent rollup:
   estimated_cost, costed_span_count, uncosted_span_count, cost_coverage,
   priced_as_of, pricing_stale (bool).

2. Per-trace estimated cost — so "this stuck/errored trace cost approximately $X"
   becomes real. Same coverage fields per trace.

3. Relative rankings / accountability findings:
   - Most expensive agents (by estimated spend) in the window.
   - Estimated spend attributable to ERROR traces (traces containing error spans)
     — the "what are failing agents costing you" number.
   Both labeled estimate, both carrying coverage.

Endpoint shape is the implementer's call within these conventions — propose it in
the plan (e.g. extending /v1/agents with cost fields vs a separate /v1/economics
surface). Argue for one in the plan.

## Known limitations (must be documented in code/docs and surfaced where relevant)

- FREE/PROMOTIONAL TOKENS: an agent burning credits shows cost it is not paying.
  This is the ONE case that distorts RELATIVE rankings (it is non-uniform across
  agents). ClawIndex cannot know about credits. The finance/back-office owner
  resolves this.
- CACHED TOKENS: not distinguished in v1. Providers price cached input lower, so
  input-cost estimates run HIGH for cache-heavy agents.
- NEGOTIATED RATES: priced at public list by default. Enterprises with contracted
  rates will see estimates that differ from their invoice. Override tooling is a
  cloud-tier feature, deferred.
- HISTORICAL PRICING: v1 prices all spans at CURRENT rates (flat). If provider
  pricing changes, historical estimates shift, and trend comparisons across a
  price change reflect both usage AND price movement. Document this.
- NON-TOKEN COSTS (paid tool APIs, image generation, embeddings) are out of scope.
  Do NOT claim "total agent cost" — claim "estimated LLM token cost."

## Constraints

- Single-tenant. No cross-tenant anything.
- Read-only. Cost computed at read time; nothing written back to span_state.
- Pass-through unchanged.
- Do not expose any figure the data cannot back.
- No inference of missing token counts. None. Anywhere.

## Out of scope

- Current-pricing maintenance, negotiated-rate override tooling (cloud tier).
- Caching-aware pricing.
- Effective-date price resolution (schema is date-ready; behavior is flat in v1).
- Any UI (the economics surface is a separate slice in clawindex-dashboard).

## Tests

Follow the existing fixture pattern.
- Conformant spans with tokens + known model -> correct estimated cost.
- Span with tokens but UNKNOWN model (no price) -> uncosted, counts toward coverage gap, NOT costed as zero.
- Span with no token data -> uncosted, coverage gap, never guessed.
- Mixed agent (some costed, some not) -> estimated_cost covers only costed spans; cost_coverage is correct; uncosted_span_count is correct.
- Stale pricing (last_confirmed old / fallback used) -> pricing_stale flag set, priced_as_of correct.
- Error-trace spend: traces containing error spans -> correct estimated spend attributable to them.
- Most-expensive-agents ranking -> correct order.
- Window: cost respects since/until, consistent with #20/#21.
- Placeholder spans (null agent_id) never appear and never affect cost or coverage.
- NO test asserts an inferred/estimated token count. If one appears, the design is wrong.

## Workflow

- Branch off main. Never commit to main directly. Open a PR for review.
