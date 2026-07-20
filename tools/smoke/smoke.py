#!/usr/bin/env python3
"""
ClawIndex single-agent smoke test.

Emits conformant GenAI SemConv spans via a real OpenTelemetry SDK and OTLP/HTTP
exporter, then queries the collector API and asserts exact results — including
the calculated cost.

Usage:
    python smoke.py [--endpoint URL]

    --endpoint   Base URL of the ClawIndex collector (default: http://localhost:8080)
                 Also configurable via env var: CLAWINDEX_ENDPOINT
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from datetime import datetime, timedelta, timezone
from decimal import Decimal

import requests
from opentelemetry import trace
from opentelemetry.exporter.otlp.proto.http.trace_exporter import OTLPSpanExporter
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import SimpleSpanProcessor

# ---------------------------------------------------------------------------
# Agent identity
# A stable GUID satisfying all conformance-floor rules:
#   - non-empty after trim, >= 8 chars
#   - not in the placeholder denylist
#   - valid GUID format (required by GET /v1/agents/{id} path validation)
# ---------------------------------------------------------------------------
AGENT_ID = "a2c4e6f8-1b3d-5e7f-9a0b-2c4d6e8f0a1b"

# ---------------------------------------------------------------------------
# Model and pricing constants
#
# Must match an entry in src/Clawindex.Collector.Api/Economics/pricing.json.
# To test with a different model, update MODEL, PROVIDER, PRICE_INPUT_PER_MILLION,
# and PRICE_OUTPUT_PER_MILLION to match that model's entry in pricing.json.
# The model must exist in pricing.json or the cost assertion will fail.
# ---------------------------------------------------------------------------
PROVIDER  = "anthropic"
MODEL     = "claude-sonnet-4-6"
OPERATION = "chat"

PRICE_INPUT_PER_MILLION  = Decimal("3.00")   # claude-sonnet-4-6 input, per pricing.json
PRICE_OUTPUT_PER_MILLION = Decimal("15.00")  # claude-sonnet-4-6 output, per pricing.json

# ---------------------------------------------------------------------------
# Deterministic token counts — one entry per span, in emission order.
#
# Topology: 3 traces × 2 spans each (root + child) = 6 total spans.
# All spans carry the full conformance floor → conformance_ratio = 1.0.
# All spans have tokens + a priced model → cost_coverage = 1.0.
#
# Hand-computed expected cost (claude-sonnet-4-6, $3/$15 per million):
#
#   Span                 Input   Output
#   trace-1 root         1,000      500
#   trace-1 child          200      100
#   trace-2 root           800      400
#   trace-2 child          150       75
#   trace-3 root           500      250
#   trace-3 child          100       50
#   ─────────────────── ─────── ──────
#   TOTAL                2,750    1,375
#
#   cost = (2,750 × $3.00  +  1,375 × $15.00) / 1,000,000
#        = ($8,250  +  $20,625) / 1,000,000
#        = $0.000028875
# ---------------------------------------------------------------------------
_SIMULATED_CALLS: list[tuple[int, int]] = [
    (1000, 500),   # trace 1, root span
    (200,  100),   # trace 1, child span
    (800,  400),   # trace 2, root span
    (150,   75),   # trace 2, child span
    (500,  250),   # trace 3, root span
    (100,   50),   # trace 3, child span
]

_NUM_TRACES = 3
_NUM_SPANS  = len(_SIMULATED_CALLS)


def get_token_counts(call_index: int) -> tuple[int, int]:
    """
    Return (input_tokens, output_tokens) for one agent call.

    DEFAULT: returns deterministic hardcoded values — no API key required.

    # TODO [stub — implement in a later slice for real-LLM mode]:
    # from anthropic import Anthropic
    # client = Anthropic()
    # resp = client.messages.create(
    #     model=MODEL, max_tokens=1024,
    #     messages=[{"role": "user", "content": "ping"}],
    # )
    # return resp.usage.input_tokens, resp.usage.output_tokens
    """
    return _SIMULATED_CALLS[call_index]


# ---------------------------------------------------------------------------
# OTel setup
# ---------------------------------------------------------------------------

def setup_tracer(collector_base_url: str) -> trace.Tracer:
    """
    Configure TracerProvider with SimpleSpanProcessor + OTLP/HTTP exporter.

    SimpleSpanProcessor exports each span synchronously the moment it ends
    (i.e. when its `with` block exits). OTLPSpanExporter.export() blocks until
    the HTTP POST response is received. The collector persists spans to SQLite
    before returning the response. So all spans are in the DB by the time the
    last `with` block exits — no sleep or explicit flush is needed.

    Footgun: the constructor `endpoint=` takes the FULL URL including the path.
    Only the OTEL_EXPORTER_OTLP_ENDPOINT env var auto-appends /v1/traces.
    """
    exporter = OTLPSpanExporter(endpoint=f"{collector_base_url}/v1/traces")
    provider = TracerProvider()
    provider.add_span_processor(SimpleSpanProcessor(exporter))
    trace.set_tracer_provider(provider)
    return trace.get_tracer("clawindex-smoke")


def emit_spans(tracer: trace.Tracer) -> None:
    call_idx = 0
    for trace_num in range(1, _NUM_TRACES + 1):
        with tracer.start_as_current_span(f"agent-call-{trace_num}") as root:
            in_tok, out_tok = get_token_counts(call_idx)
            call_idx += 1
            root.set_attribute("gen_ai.operation.name",      OPERATION)
            root.set_attribute("gen_ai.provider.name",       PROVIDER)
            root.set_attribute("gen_ai.request.model",       MODEL)
            root.set_attribute("gen_ai.usage.input_tokens",  in_tok)
            root.set_attribute("gen_ai.usage.output_tokens", out_tok)
            root.set_attribute("clawindex.agent.id",         AGENT_ID)

            with tracer.start_as_current_span(f"tool-call-{trace_num}") as child:
                in_tok, out_tok = get_token_counts(call_idx)
                call_idx += 1
                child.set_attribute("gen_ai.operation.name",      OPERATION)
                child.set_attribute("gen_ai.provider.name",       PROVIDER)
                child.set_attribute("gen_ai.request.model",       MODEL)
                child.set_attribute("gen_ai.usage.input_tokens",  in_tok)
                child.set_attribute("gen_ai.usage.output_tokens", out_tok)
                child.set_attribute("clawindex.agent.id",         AGENT_ID)


# ---------------------------------------------------------------------------
# Assertions
# ---------------------------------------------------------------------------

class Results:
    def __init__(self) -> None:
        self.passed: list[str] = []
        self.failed: list[str] = []

    def check(self, label: str, actual: object, expected: object) -> None:
        if actual == expected:
            self.passed.append(label)
            print(f"  PASS  {label}")
        else:
            self.failed.append(label)
            print(f"  FAIL  {label}")
            print(f"          expected: {expected!r}")
            print(f"          actual:   {actual!r}")

    @property
    def all_passed(self) -> bool:
        return not self.failed


def _parse(resp: requests.Response) -> dict | list:
    resp.raise_for_status()
    # parse_float=Decimal preserves exact precision for the C# decimal cost field,
    # which may serialize as "0.000028875" or "2.8875E-5" depending on the runtime.
    return json.loads(resp.text, parse_float=Decimal)


def _dec(value: object) -> Decimal:
    return Decimal(str(value))


def run_assertions(
    endpoint: str,
    run_start: str,
    expected_cost: Decimal,
) -> Results:
    r = Results()

    # 1. Rollup — GET /v1/agents?since=<run_start>
    agents = _parse(requests.get(f"{endpoint}/v1/agents", params={"since": run_start}))
    agent  = next((a for a in agents if a["agent_id"] == AGENT_ID), None)

    r.check("agent present in /v1/agents",  bool(agent),  True)
    if agent is None:
        return r  # remaining checks are meaningless

    r.check("span_count",          agent["span_count"],               _NUM_SPANS)
    r.check("trace_count",         agent["trace_count"],              _NUM_TRACES)
    r.check("conformance_ratio",   _dec(agent["conformance_ratio"]),  Decimal("1"))
    r.check("cost_coverage",       _dec(agent["cost_coverage"]),      Decimal("1"))
    r.check("estimated_cost_usd",  _dec(agent["estimated_cost_usd"]), expected_cost)

    # 2. Detail — GET /v1/agents/{id}?since=<run_start>
    # Both rollup and recent_traces are window-scoped; since= is required on this
    # call too so repeated runs don't accumulate traces into the count assertion.
    detail = _parse(
        requests.get(f"{endpoint}/v1/agents/{AGENT_ID}", params={"since": run_start})
    )
    r.check("detail rollup span_count",      detail["rollup"]["span_count"], _NUM_SPANS)
    r.check("detail recent_traces count",    len(detail["recent_traces"]),   _NUM_TRACES)

    for t in detail["recent_traces"]:
        short = t["trace_id"][:12] + "…"
        r.check(f"trace {short} cost_coverage",
                _dec(t["cost_coverage"]), Decimal("1"))
        r.check(f"trace {short} estimated_cost_usd present",
                t["estimated_cost_usd"] is not None, True)

    return r


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main() -> None:
    parser = argparse.ArgumentParser(
        description="ClawIndex single-agent smoke test",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument(
        "--endpoint",
        default=os.environ.get("CLAWINDEX_ENDPOINT", "http://localhost:8080"),
        metavar="URL",
        help="Base URL of the ClawIndex collector (default: http://localhost:8080)",
    )
    args   = parser.parse_args()
    endpoint: str = args.endpoint.rstrip("/")

    # Pre-flight: fail fast with a clear message if the collector is not up
    try:
        health = requests.get(f"{endpoint}/v1/health", timeout=5)
        health.raise_for_status()
    except Exception as exc:
        print(f"ERROR: collector not reachable at {endpoint}")
        print(f"       {exc}")
        print()
        print("       Start the collector first:")
        print("         docker compose up   (from the repo root)")
        sys.exit(1)

    print(f"Collector : {endpoint}  (healthy)")
    print(f"Agent ID  : {AGENT_ID}")
    print(f"Model     : {PROVIDER}/{MODEL}")

    # Compute expected cost from constants
    total_input  = sum(i for i, _ in _SIMULATED_CALLS)
    total_output = sum(o for _, o in _SIMULATED_CALLS)
    expected_cost = (
        Decimal(total_input)  * PRICE_INPUT_PER_MILLION +
        Decimal(total_output) * PRICE_OUTPUT_PER_MILLION
    ) / Decimal("1000000")

    print(f"Tokens    : {total_input} input, {total_output} output across {_NUM_SPANS} spans")
    print(f"Expected  : ${expected_cost} ({_NUM_TRACES} traces, cost_coverage=1.0, conformance=1.0)")
    print()

    # Capture run_start 1 second before emitting to guarantee since < until
    run_start = (datetime.now(timezone.utc) - timedelta(seconds=1)).isoformat()

    print("Emitting spans…")
    tracer = setup_tracer(endpoint)
    emit_spans(tracer)
    print("Done. (SimpleSpanProcessor — all spans are in the collector synchronously.)")
    print()

    print("Asserting…")
    results = run_assertions(endpoint, run_start, expected_cost)
    print()

    total = len(results.passed) + len(results.failed)
    if results.all_passed:
        print(f"PASS  {total}/{total} checks")
        sys.exit(0)
    else:
        print(f"FAIL  {len(results.failed)}/{total} checks failed")
        sys.exit(1)


if __name__ == "__main__":
    main()
