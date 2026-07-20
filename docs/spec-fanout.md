# Implementation Spec — Fan-out: ClawIndex as single point of contact (v1)

## Goal

Make ClawIndex the single telemetry endpoint an operator configures. Agents point
at ClawIndex; ClawIndex persists for economics AND forwards the telemetry
verbatim to N configured downstream destinations (their Datadog, Aspire, Grafana,
etc.).

This is core product, not a convenience. The buyer's alternative is running an
OTel Collector to fan out themselves, which for a direct-pump shop means standing
up infrastructure they do not have. "Config your destinations, we handle the
rest" is part of what they are buying.

## Non-negotiable properties

- **Persist first.** The durable write for economics is the guarantee. Forwarding
  is downstream of it and never precedes it.
- **Forwarding never blocks or breaks ingestion.** A slow, erroring, or dead
  destination MUST NOT add latency to, or cause failure of, the /v1/traces
  request. Fire-and-forget after the durable write.
- **Byte-identical forwarding.** The payload forwarded is exactly the payload
  received. No transformation, no reshaping, no enrichment, no filtering.
  Pass-through on content holds — ClawIndex extracts the SemConv allowlist for
  its OWN projection only; it never modifies what it relays.
- **Request-level fan-out.** One incoming OTLP POST (one ExportTraceServiceRequest)
  is one queue item, forwarded whole to each destination. Do NOT decompose into
  spans and reconstruct requests — that breaks byte-identity and forces downstream
  reassembly. Any need for per-span routing or reshaping is a paid consulting
  engagement, not an open-core feature.
- **N destinations, extensible.** Config takes a LIST from day one. The
  destination abstraction must make adding a new destination TYPE (protocol) a
  new implementation, not a restructure.

## Transport (v1)

- **OTLP/HTTP only.** Protobuf body, POST to the destination's configured URL.
- The destination abstraction MUST be protocol-pluggable: define an interface
  (e.g. ITelemetryDestination) with an HTTP implementation now. Adding OTLP/gRPC
  later is a new implementation registered by config type, not a refactor.
- Document for Aspire users: point at Aspire's HTTP OTLP endpoint (commonly 18890),
  not its gRPC default (18889).

## Configuration — no image rebuild, ever

Destinations are read from IConfiguration as an array. NOTHING about destinations
is compiled into the image.

Shape (appsettings.json):
  "Clawindex": {
    "Destinations": [
      { "Name": "datadog", "Type": "otlp-http", "Endpoint": "http://datadog-agent:4318/v1/traces", "Enabled": true },
      { "Name": "aspire",  "Type": "otlp-http", "Endpoint": "http://aspire:18890/v1/traces",      "Enabled": true }
    ]
  }

Must ALSO be settable entirely via environment variables using the standard
double-underscore syntax, e.g.:
  Clawindex__Destinations__0__Name=datadog
  Clawindex__Destinations__0__Type=otlp-http
  Clawindex__Destinations__0__Endpoint=http://datadog-agent:4318/v1/traces
  Clawindex__Destinations__0__Enabled=true

Also support a mounted appsettings file. The README must show BOTH patterns with
a docker-compose example. Adding or changing a destination must require at most a
container restart — never an image rebuild.

Optional per-destination headers (for backends requiring an API key header) are
supported as a dictionary in config, since real destinations often need auth.
Header VALUES must never be logged. Document that secrets should be supplied via
environment variables, not committed config files.

## Durable forward queue

Forwarding is backed by a durable queue so buffering is structural from v1, not a
retrofit.

- On successful ingest, after the durable span write, enqueue ONE row containing
  the raw received payload bytes (and content-type), plus enqueue-time.
- A background worker drains the queue and POSTs each item to each enabled
  destination.
- Track per-item, per-destination delivery status and attempt count.
- Restart-safe: queue lives in the existing SQLite store; in-flight items are
  recoverable after restart (reset in-progress on startup).
- The existing v0.1 projection-status pattern (status + attempts + restart reset,
  as in the old GetUnprojectedAsync / MarkProjected / MarkProjectionAttempt
  machinery) is the precedent for this shape. Reuse the pattern.

## Deliberately deferred to the NEXT slice (do not build now)

- Retry policy: backoff curve, max attempts, dead-letter handling.
- Bounded queue growth and drop policy when full.
- Backpressure behavior.
- Operational visibility: queue depth, delivery lag, failure counters.

v1 may attempt delivery once and mark failure without retrying. The QUEUE
STRUCTURE must exist; the POLICY does not. Note plainly in the README that v1
forwarding is best-effort and lossy under destination failure.

## Out of scope

- OTLP/gRPC export (next slice; abstraction must accommodate it).
- Non-OTel input. Open core accepts OTel-conformant telemetry. Shaping homegrown
  telemetry into OTel is a paid Range Point services engagement. ClawIndex relays
  what it receives; if a downstream rejects non-conformant telemetry, that is
  between the integrator and their provider.
- Any transformation of forwarded payloads.
- Per-span routing / filtering / reconstruction.
- Hot config reload without restart (restart is acceptable; image rebuild is not).

## Tests

- Ingest with one configured destination -> payload is enqueued and forwarded;
  bytes received at a stub destination are byte-identical to what was POSTed in.
- Ingest with N destinations -> forwarded to each.
- Destination unreachable -> /v1/traces still returns success, span still persisted,
  forward marked failed. Ingestion unaffected.
- Slow destination -> /v1/traces latency unaffected (forwarding is off the request path).
- Zero destinations configured -> ingestion works normally; no errors.
- Destination disabled in config -> not forwarded to.
- Restart with items queued -> items still present and drainable after restart.
- Config supplied via environment variables only (no appsettings entry) -> destinations load correctly.
- Header values are not present in logs.

## Workflow

Branch off main. Never commit to main. Open a PR for review.
