# Implementation Spec — #17a: OTLP Ingestion + SemConv Conformance Validation

## Goal

Add a real OTLP/HTTP receive endpoint to the collector and validate incoming
GenAI SemConv spans against the conformance floor. This is the greenfield half
of the ingestion remodel. It MUST NOT touch the durable trace/span recovery,
idempotency, or projection-status machinery — that is #17b.

## Reference

- Contract: docs/read-api-ingestion-contract.md (conformance floor, two-tier validation, pass-through stance).
- OTLP/HTTP: POST /v1/traces, body is a protobuf-encoded ExportTraceServiceRequest, Content-Type application/x-protobuf (binary) or application/json (OTLP/JSON). Default per OTLP spec.

## Scope (build this)

1. **OTLP proto types.** Vendor the official OpenTelemetry proto definitions
   (opentelemetry/proto/**) into the project and generate C# types via a
   `<Protobuf>` include in the csproj. Do not hand-author OTLP message types.
   Target the trace service only (ExportTraceServiceRequest / ResourceSpans /
   ScopeSpans / Span). Metrics and logs are out of scope.

2. **OTLP/HTTP receive endpoint.** Add `POST /v1/traces` accepting
   Content-Type application/x-protobuf (binary protobuf). Deserialize the body
   to ExportTraceServiceRequest. OTLP/JSON support is a stretch goal, not
   required for this slice. gRPC (port 4317) is explicitly deferred to a later
   slice; this slice is HTTP only.

3. **Span flattening.** Walk ResourceSpans -> ScopeSpans -> Span, producing a
   flat list of spans, each with its attributes (and the relevant resource
   attributes) accessible as a key/value lookup.

4. **Conformance validator.** For each span, run two-tier validation per the
   contract:
   - Tier 1 (envelope-valid): the span is well-formed (has trace_id, span_id,
     name, start/end). Always passes downstream.
   - Tier 2 (conformance-complete): the span additionally carries every floor
     field below, each satisfying its rule.

   Conformance floor (read from span attributes):
   - gen_ai.operation.name : present, non-empty string
   - gen_ai.provider.name  : present, non-empty string
   - gen_ai.request.model  : present, non-empty string
   - gen_ai.usage.input_tokens  : present, integer >= 0
   - gen_ai.usage.output_tokens : present, integer >= 0
   - gen_ai.agent.id : present, parses as a GUID, and is NOT
     00000000-0000-0000-0000-000000000000 (nil) and NOT
     00000000-0000-0000-0000-000000000001 (sentinel)

   A span failing any Tier 2 rule is NOT rejected. It is marked
   non-conformant and still passed downstream. A span failing Tier 1 is
   malformed; return a per-span rejection in the response but continue
   processing the rest (partial success, mirroring existing batch behavior).

5. **ValidatedSpan boundary type.** Produce a `ValidatedSpan` record carrying:
   the OTel identity (trace_id, span_id, parent_span_id, name, kind,
   start/end timestamps), the projected SemConv floor fields (operation,
   provider, model, input_tokens, output_tokens, agent_id), an
   IsConformant flag, and the raw span attributes preserved opaquely.
   This is the ONLY output of this slice.

6. **Sink interface.** Define an interface (e.g. IValidatedSpanSink) with a
   single method to accept validated spans. In THIS slice, provide a
   no-op / in-memory implementation only. Projecting validated spans into the
   durable trace/span store is #17b and must not be implemented here. The
   endpoint returns an OTLP-compliant ExportTraceServiceResponse.

## Constraints (enforce these)

- **Pass-through on content.** Only the named SemConv allowlist attributes are
  read for projection. All other attribute content is preserved opaquely and
  never inspected, transformed, or scrubbed.
- **Single-tenant.** No tenant logic, no cross-tenant anything.
- **Do not touch** EventRepository's trace_state / span_state / event_span_map
  logic, restart recovery, idempotency, or projection-status handling. If a
  change appears to require touching them, STOP — that is #17b.
- The legacy v0.1 envelope endpoints (/v1/events, /v1/events/batch) and the
  bouncer-md event vocabulary are being abandoned. This slice ADDS the OTLP
  path; a follow-up removes the legacy path once #17b lands. Do not delete the
  legacy path in this slice unless trivially clean — additive first.

## Tests (required, match existing pattern)

Follow the existing CollectorApiTests / CollectorFixture pattern (WebApplicationFactory, real HTTP calls). Add:
- Posts a conformant OTLP/HTTP trace -> 200, span surfaces as conformant via the in-memory sink.
- Posts a span missing token fields -> accepted, surfaces as non-conformant (NOT rejected).
- Posts a span with nil GUID agent id -> non-conformant.
- Posts a span with the ...0001 sentinel GUID -> non-conformant.
- Posts a span with a malformed (non-GUID) agent id -> non-conformant.
- Posts a malformed/empty body -> clean error response, no crash.
- A golden conformant ResourceSpans fixture (replacing the old soil-report
  envelope fixtures) lives in examples/ and is used by tests.

## Out of scope (do NOT build here)

- gRPC OTLP (port 4317) — later slice.
- OTLP/JSON — stretch only.
- Projection into durable trace/span state — #17b.
- Reshaping the events table columns — #17b.
- Metrics or logs signals.
- Read API endpoints.
- Removing the legacy envelope path (additive-first).

## Workflow

- Branch off main. Never commit to main directly. Open a PR for review.
