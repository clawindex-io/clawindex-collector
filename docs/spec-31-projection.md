# Implementation Spec — #31: Complete-Spans Projection into Durable Store

## Goal

Replace the #17a no-op InMemorySpanSink with a real sink that projects validated
complete spans directly into the durable trace/span store. Reshape SpanState and
TraceState in place to be SemConv-native and complete-span-native. Absorbs the
folded persistence-field work (old #18).

No worker, no intake table, no open-span machinery. A complete span arrives whole
and is upserted whole.

## Reference

- Contract: docs/read-api-ingestion-contract.md, including the Complete-Spans-Only and SemConv Transition Tolerance amendments.
- Product direction: docs/strategic-decision-record.md (complete-spans-only; economics layer is the product; no open-span watching).
- #17a delivered: ValidatedSpan, IValidatedSpanSink, the OTLP /v1/traces endpoint. The endpoint already calls sink.AcceptAsync(validatedSpans). This issue provides the real sink.

## Decisions (locked)

- **Reshape in place, no migration.** Pre-production, no live data to preserve. Redefine the span_state and trace_state table schemas directly in InitializeAsync; do not write ALTER/migration logic.
- **Direct projection, no worker.** The sink reshapes each ValidatedSpan and upserts directly. The old events-table + projection-worker pipeline is NOT used by this path. A queue/worker is only revisited if a bus is added in front for load — not now.
- **Legacy projection detached.** See Coexistence below.

## Scope

### 1. Reshape SpanState (in place)

Remove v0.1-era fields: SourceStartEventId, SourceEndEventId, TaskId.
Make EndedAt non-nullable (projected spans are always complete).
Add SemConv-native fields:
- Operation (string?, gen_ai.operation.name)
- Provider (string?, gen_ai.provider.name or gen_ai.system)
- Model (string?, gen_ai.request.model)
- InputTokens (long?)
- OutputTokens (long?)
- AgentId stays (now the gen_ai.agent.id GUID as string)
- IsConformant (bool)
Keep: SpanId, TraceId, ParentSpanId, SpanName, SpanKind, Status, StartedAt, EndedAt, AttributesJson (raw attributes preserved opaquely), UpdatedAt.

### 2. Reshape TraceState (in place)

Remove TaskId (v0.1). Keep TraceId, RootSpanId, AgentId, Status, StartedAt, EndedAt, UpdatedAt.
Status reflects trace lifecycle: open (root not yet closed) or finalized.
EndedAt set when the trace finalizes.

### 3. Redefine the SQLite schema

In EventRepository.InitializeAsync, redefine the span_state and trace_state
table definitions to match the reshaped records (new columns, dropped columns).
No migration. If simplest, drop-and-recreate these two tables on init for now,
since there is no production data. Leave the events table and its schema alone.

### 4. Real sink: DurableSpanSink implements IValidatedSpanSink

Replace InMemorySpanSink as the registered IValidatedSpanSink (keep
InMemorySpanSink available for tests).

For each ValidatedSpan in the batch:
- Map to a reshaped SpanState (real OTLP span_id/trace_id, parent_span_id, the
  projected SemConv fields, IsConformant, Status from the span's OTLP status,
  StartedAt/EndedAt from the span times, AttributesJson from RawAttributes).
- **Idempotent upsert keyed on span_id.** Receiving the same span_id twice is a
  no-op overwrite with identical data — never a double-insert or corruption.
  Use the repository's existing upsert primitive (or an INSERT ... ON CONFLICT).
- **Parent linkage / placeholder parents.** If the span has a parent_span_id not
  yet present in span_state for this trace, synthesize a minimal placeholder
  parent span row (status=placeholder) so the tree stays connected; when the
  real parent later arrives, the upsert on its span_id replaces the placeholder
  with the real span. A placeholder must never overwrite a real span.
- **Trace state.** Upsert trace_state: a span with no parent_span_id is the root
  (set RootSpanId). Update trace StartedAt to the earliest span start, EndedAt
  when finalized.

### 5. Trace finalization

- A trace finalizes when its root span (the span with no parent) is present and
  closed (it is, by definition — complete-spans-only). On root arrival, mark the
  trace finalized with EndedAt = root span EndedAt.
- Quiet-period fallback (a trace whose root never arrives) is SPLIT OUT into its
  own follow-up issue and is NOT part of this slice. Do not implement a
  background sweep here. A trace with no root simply remains open until that
  follow-up lands. Do not stub a timer.

## Constraints

- Pass-through on content: only the projected SemConv allowlist fields are
  promoted to columns; the rest live in AttributesJson untouched.
- Single-tenant. No cross-tenant logic.
- Incomplete spans (no end time) must NOT reach projection. If any arrive at the
  sink, drop them from projection (they should already be filtered upstream;
  defend here too) — do not store partial spans.
- Idempotency and placeholder-vs-real ordering are the correctness core. Get
  those right; everything else is mechanical.

## Coexistence with the legacy path (decided)

After reshaping span_state, the legacy OtelEventMapper/OtelProjectionWorker will
not compile against the new record (it sets SourceStartEventId etc.).

DECISION: detach the legacy projection. Stop registering OtelProjectionWorker so
it no longer runs. The v0.1 /v1/events path may still accept and store events to
the events table, but it no longer projects to span_state. This is acceptable
because v0.1 is being retired imminently.

If detaching the worker still leaves the legacy mapper referenced in a way that
breaks the build, remove that reference (e.g. drop the DI registration and any
now-dead call site) — but do NOT spend effort adapting the mapper to the new
record; it is being deleted. Keep changes minimal and build green. Full removal
of the legacy events table, mapper, worker, and /v1/events endpoints is a
near-term follow-up (#29).

## Tests

Follow the existing fixture pattern. Use a real DurableSpanSink against a temp
SQLite db. Add:
- Conformant complete span projects -> one row in span_state with correct
  SemConv fields and IsConformant=true; trace_state has the trace.
- Same span_id posted twice -> still one row, no duplicate, no corruption (idempotency).
- Child span arrives before parent -> placeholder parent row created; when real
  parent arrives, placeholder replaced, real fields present, child still linked.
- Real parent arrives first, then child -> normal linkage, no placeholder.
- Root span (no parent) present -> trace finalized, EndedAt set.
- Non-conformant-but-complete span -> projected, IsConformant=false (still stored).
- A multi-span trace -> all spans present, tree connected, trace finalized on root.

## Out of scope

- Removing the legacy events table / worker / endpoints (#29).
- The read API endpoints (#20/#21/#22).
- Quiet-period finalization for rootless traces (split into its own follow-up issue).
- Any open-span / in-flight handling — not part of complete-spans-only.

## Workflow

- Branch off main. Never commit to main directly. Open a PR for review.
