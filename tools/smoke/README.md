# ClawIndex Smoke Test

Single-agent end-to-end verification of a live ClawIndex deployment. Uses a **real
OpenTelemetry SDK** and **real OTLP/HTTP exporter** — not hand-rolled protobuf — to prove
that a genuine OTel-instrumented agent can emit spans to your collector and that the
cost calculations are correct.

Run this before wiring real agents to a new deployment.

## What it proves

- A real OTel exporter's wire format is accepted by `/v1/traces`.
- The collector ingests spans, projects agent rollups, and calculates cost.
- `conformance_ratio` and `cost_coverage` are `1.0` for fully-instrumented spans.
- The calculated `estimated_cost_usd` matches the hand-computed expected value exactly.

## Prerequisites

- Python 3.11+
- Docker (to run the collector via `docker compose`)
- No API key required — token counts are hardcoded deterministic values

## 1. Start the collector

From the **repo root**:

```bash
docker compose up
```

The collector binds to `http://localhost:8080`. Wait for the startup log line before
running the smoke test.

## 2. Install dependencies

```bash
cd tools/smoke
pip install -r requirements.txt
```

To pin exact versions with hashes (recommended for reproducible environments):

```bash
pip install pip-tools
pip-compile requirements.txt      # produces requirements.txt with pinned versions
pip install -r requirements.txt
```

## 3. Run

```bash
python smoke.py
```

With a non-default collector endpoint:

```bash
python smoke.py --endpoint http://my-host:8080
# or
CLAWINDEX_ENDPOINT=http://my-host:8080 python smoke.py
```

## What a pass looks like

```
Collector : http://localhost:8080  (healthy)
Agent ID  : a2c4e6f8-1b3d-5e7f-9a0b-2c4d6e8f0a1b
Model     : anthropic/claude-sonnet-4-6
Tokens    : 2750 input, 1375 output across 6 spans
Expected  : $0.000028875 (3 traces, cost_coverage=1.0, conformance=1.0)

Emitting spans…
Done. (SimpleSpanProcessor — all spans are in the collector synchronously.)

Asserting…
  PASS  agent present in /v1/agents
  PASS  span_count
  PASS  trace_count
  PASS  conformance_ratio
  PASS  cost_coverage
  PASS  estimated_cost_usd
  PASS  detail rollup span_count
  PASS  detail recent_traces count
  PASS  trace a2c4e6f80000… cost_coverage
  PASS  trace a2c4e6f80000… estimated_cost_usd present
  ... (one check per trace)

PASS  13/13 checks
```

A failure prints the expected and actual values for every failing check:

```
  FAIL  estimated_cost_usd
          expected: Decimal('0.000028875')
          actual:   Decimal('0.000032000')
```

Exit code 0 = all checks passed. Exit code 1 = one or more checks failed.

## Running repeatedly

The script is idempotent. It passes `?since=<run_start>` on every query, scoping rollup
assertions to spans emitted during that run only. You can run it against the same database
multiple times and get the same result each time.

## Changing the model

The model is hardcoded to `claude-sonnet-4-6`. To test with a different model:

1. Update `MODEL` and `PROVIDER` in `smoke.py`.
2. Update `PRICE_INPUT_PER_MILLION` and `PRICE_OUTPUT_PER_MILLION` to match the new model's
   entry in `src/Clawindex.Collector.Api/Economics/pricing.json`.
3. Re-compute the expected cost comment in `_SIMULATED_CALLS`.

The model must exist in `pricing.json` or the cost assertion will fail with a mismatch
(the collector will show `cost_coverage < 1.0` and `estimated_cost_usd = null`).

## How it works

- **3 traces × 2 spans each** (root + child) = 6 total spans, all carrying the full
  conformance floor: `gen_ai.operation.name`, `gen_ai.provider.name`,
  `gen_ai.request.model`, `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens`,
  `clawindex.agent.id`.
- **`SimpleSpanProcessor`**: spans are exported synchronously when each `with` block
  exits. The collector persists to SQLite before returning the HTTP response. No sleep
  or explicit flush is needed.
- **Exact cost assertion**: expected cost is computed from the hardcoded token constants
  and the model's pricing, using `decimal.Decimal` arithmetic to match C#'s `decimal`
  precision.
