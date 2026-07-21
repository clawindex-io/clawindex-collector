#!/usr/bin/env python3
"""
ClawIndex chaos monkey — multi-agent fleet generator (issue #58).

Emits a deliberately varied fleet of 11 agents to populate ClawIndex with
meaningful economics data for evaluation. Each agent embodies a distinct
archetype: high-volume cheap, low-volume expensive, partial conformance, etc.

This is a generation tool, not an assertion harness. It prints expected
numbers; use the ClawIndex read API to compare them to what the collector
reports. See tools/smoke/smoke.py for the single-agent correctness test.

Usage:
    python chaos.py [--endpoint URL] [--seed INT] [--scale N]

    --endpoint   Base URL of the ClawIndex collector (default: http://localhost:8080)
    --seed       Reserved for future timing jitter; hardcoded token counts are
                 unaffected by seed. Default: 42.
    --scale      Multiply trace counts by N, preserving per-agent story proportions.
                 Token counts per span are unchanged; expected spend scales linearly.
                 Default: 1 (fleet total ≈ $52).
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from dataclasses import dataclass
from decimal import Decimal
from pathlib import Path
from typing import Optional

import requests
from opentelemetry.exporter.otlp.proto.http.trace_exporter import OTLPSpanExporter
from opentelemetry.sdk.resources import Resource
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import SimpleSpanProcessor
from opentelemetry.trace import Status, StatusCode, set_span_in_context

# ---------------------------------------------------------------------------
# Pricing — read pricing.json at runtime; no table duplication in this script.
# ---------------------------------------------------------------------------

_PRICING_JSON = (
    Path(__file__).parent.parent.parent
    / "src" / "Clawindex.Collector.Api" / "Economics" / "pricing.json"
)


def _load_pricing() -> dict[str, dict[str, Decimal]]:
    """Return {model: {"input": Decimal, "output": Decimal}}."""
    with _PRICING_JSON.open() as fh:
        data = json.load(fh)
    result: dict[str, dict[str, Decimal]] = {}
    for entry in data["entries"]:
        result.setdefault(entry["model"], {})[entry["token_type"]] = Decimal(
            str(entry["price_per_million_tokens"])
        )
    return result


_PRICING = _load_pricing()
_M = Decimal("1000000")


def _cost(model: str, input_tokens: int, output_tokens: int) -> Optional[Decimal]:
    """Return cost in USD, or None if the model is not in pricing.json."""
    if model not in _PRICING:
        return None
    rates = _PRICING[model]
    return (Decimal(input_tokens) * rates["input"] + Decimal(output_tokens) * rates["output"]) / _M


# ---------------------------------------------------------------------------
# OTel helpers
# ---------------------------------------------------------------------------

HAIKU  = "claude-haiku-4-5-20251001"
SONNET = "claude-sonnet-4-6"
OPUS   = "claude-opus-4-8"
OPNAME = "chat"


def _make_tracer(service_name: str, endpoint: str):
    """One TracerProvider per agent — never touches the global tracer provider."""
    resource = Resource.create({"service.name": service_name})
    exporter = OTLPSpanExporter(endpoint=f"{endpoint}/v1/traces")
    provider = TracerProvider(resource=resource)
    provider.add_span_processor(SimpleSpanProcessor(exporter))
    return provider.get_tracer("clawindex-chaos")


def _attrs(span, model: str, provider: str, in_tok: int, out_tok: int, agent_id: Optional[str]) -> None:
    span.set_attribute("gen_ai.operation.name",      OPNAME)
    span.set_attribute("gen_ai.provider.name",       provider)
    span.set_attribute("gen_ai.request.model",       model)
    span.set_attribute("gen_ai.usage.input_tokens",  in_tok)
    span.set_attribute("gen_ai.usage.output_tokens", out_tok)
    if agent_id is not None:
        span.set_attribute("clawindex.agent.id", agent_id)


def _attrs_no_tokens(span, model: str, provider: str, agent_id: str) -> None:
    span.set_attribute("gen_ai.operation.name",  OPNAME)
    span.set_attribute("gen_ai.provider.name",   provider)
    span.set_attribute("gen_ai.request.model",   model)
    span.set_attribute("clawindex.agent.id",     agent_id)


# ---------------------------------------------------------------------------
# Agent result
# ---------------------------------------------------------------------------

@dataclass
class AgentResult:
    service_name: str
    archetype: str
    spans_emitted: int
    error_spans: int
    defect: str
    priceable_input: int = 0
    priceable_output: int = 0
    estimated_cost: Optional[Decimal] = None
    cost_note: str = ""


# ---------------------------------------------------------------------------
# 1. invoice-processor — HIGH-VOL CHEAP
#    40 traces × 2 spans = 80 spans  |  haiku  |  8 000 in / 1 500 out per span
#    Expected cost: 80 × (8000×$0.80 + 1500×$4.00) / 1M = $0.992
# ---------------------------------------------------------------------------

def emit_invoice_processor(endpoint: str, scale: int) -> AgentResult:
    SERVICE  = "invoice-processor"
    AGENT_ID = "svc-invoice-proc-a1b2c3d4-e5f6-7890-abcd-ef1234567890"
    IN, OUT  = 8_000, 1_500
    tracer   = _make_tracer(SERVICE, endpoint)
    n_traces = 40 * scale
    for i in range(n_traces):
        with tracer.start_as_current_span(f"invoice-extract-{i}") as root:
            _attrs(root, HAIKU, "anthropic", IN, OUT, AGENT_ID)
            with tracer.start_as_current_span(f"invoice-validate-{i}") as child:
                _attrs(child, HAIKU, "anthropic", IN, OUT, AGENT_ID)
    spans = n_traces * 2
    cost  = _cost(HAIKU, spans * IN, spans * OUT)
    return AgentResult(
        service_name=SERVICE, archetype="HIGH-VOL CHEAP",
        spans_emitted=spans, error_spans=0, defect="—",
        priceable_input=spans * IN, priceable_output=spans * OUT, estimated_cost=cost,
    )


# ---------------------------------------------------------------------------
# 2. contract-analyst — LOW-VOL EXPENSIVE
#    5 traces × 1 span = 5 spans  |  opus  |  200 000 in / 40 000 out per span
#    Expected cost: 5 × (200000×$15 + 40000×$75) / 1M = $30.00
# ---------------------------------------------------------------------------

def emit_contract_analyst(endpoint: str, scale: int) -> AgentResult:
    SERVICE  = "contract-analyst"
    AGENT_ID = "svc-contract-ana-b2c3d4e5-f6a7-8901-bcde-f12345678901"
    IN, OUT  = 200_000, 40_000
    tracer   = _make_tracer(SERVICE, endpoint)
    n_traces = 5 * scale
    for i in range(n_traces):
        with tracer.start_as_current_span(f"contract-review-{i}") as span:
            _attrs(span, OPUS, "anthropic", IN, OUT, AGENT_ID)
    spans = n_traces
    cost  = _cost(OPUS, spans * IN, spans * OUT)
    return AgentResult(
        service_name=SERVICE, archetype="LOW-VOL EXPENSIVE",
        spans_emitted=spans, error_spans=0, defect="—",
        priceable_input=spans * IN, priceable_output=spans * OUT, estimated_cost=cost,
    )


# ---------------------------------------------------------------------------
# 3. escalation-router — BLEEDER
#    20 traces × 2 spans = 40 spans  |  sonnet  |  15 000 in / 3 000 out per span
#    Error mix: first 11*scale traces → all spans ERROR
#               next 1*scale trace   → root OK, child span ERROR
#               remaining 8*scale    → clean
#    Expected cost: 40 × (15000×$3 + 3000×$15) / 1M = $3.60
# ---------------------------------------------------------------------------

def emit_escalation_router(endpoint: str, scale: int) -> AgentResult:
    SERVICE  = "escalation-router"
    AGENT_ID = "svc-escalation-c3d4e5f6-a7b8-9012-cdef-123456789012"
    IN, OUT  = 15_000, 3_000
    tracer   = _make_tracer(SERVICE, endpoint)
    n_traces     = 20 * scale
    all_err_cut  = 11 * scale
    one_err_cut  = 12 * scale
    error_spans  = 0
    for i in range(n_traces):
        with tracer.start_as_current_span(f"escalate-root-{i}") as root:
            _attrs(root, SONNET, "anthropic", IN, OUT, AGENT_ID)
            if i < all_err_cut:
                root.set_status(Status(StatusCode.ERROR, "Escalation routing failed"))
                error_spans += 1
            with tracer.start_as_current_span(f"escalate-child-{i}") as child:
                _attrs(child, SONNET, "anthropic", IN, OUT, AGENT_ID)
                if i < all_err_cut or i < one_err_cut:
                    child.set_status(Status(StatusCode.ERROR, "Downstream unreachable"))
                    error_spans += 1
    spans = n_traces * 2
    cost  = _cost(SONNET, spans * IN, spans * OUT)
    return AgentResult(
        service_name=SERVICE, archetype="BLEEDER",
        spans_emitted=spans, error_spans=error_spans,
        defect="11/20 traces all-spans-errored; 1/20 child-only error",
        priceable_input=spans * IN, priceable_output=spans * OUT, estimated_cost=cost,
    )


# ---------------------------------------------------------------------------
# 4. research-assistant — MULTI-MODEL
#    10 traces × 2 spans = 20 spans  |  alternating haiku/opus per trace
#    even traces → haiku (5 000 in / 1 000 out);  odd → opus (40 000 in / 8 000 out)
#    Expected cost: 10×haiku($0.008) + 10×opus($1.20) = $0.08 + $12.00 = $12.08
# ---------------------------------------------------------------------------

def emit_research_assistant(endpoint: str, scale: int) -> AgentResult:
    SERVICE    = "research-assistant"
    AGENT_ID   = "svc-research-ass-d4e5f6a7-b8c9-0123-defa-234567890123"
    HK_IN, HK_OUT = 5_000, 1_000
    OP_IN, OP_OUT = 40_000, 8_000
    tracer = _make_tracer(SERVICE, endpoint)
    n_traces = 10 * scale
    hk_spans = op_spans = 0
    for i in range(n_traces):
        if i % 2 == 0:
            model, in_tok, out_tok = HAIKU, HK_IN, HK_OUT
            hk_spans += 2
        else:
            model, in_tok, out_tok = OPUS, OP_IN, OP_OUT
            op_spans += 2
        with tracer.start_as_current_span(f"research-plan-{i}") as root:
            _attrs(root, model, "anthropic", in_tok, out_tok, AGENT_ID)
            with tracer.start_as_current_span(f"research-synthesize-{i}") as child:
                _attrs(child, model, "anthropic", in_tok, out_tok, AGENT_ID)
    hk_cost = _cost(HAIKU, hk_spans * HK_IN, hk_spans * HK_OUT) or Decimal("0")
    op_cost  = _cost(OPUS,  op_spans  * OP_IN, op_spans  * OP_OUT)  or Decimal("0")
    total_in  = hk_spans * HK_IN  + op_spans * OP_IN
    total_out = hk_spans * HK_OUT + op_spans * OP_OUT
    return AgentResult(
        service_name=SERVICE, archetype="MULTI-MODEL",
        spans_emitted=n_traces * 2, error_spans=0, defect="—",
        priceable_input=total_in, priceable_output=total_out,
        estimated_cost=hk_cost + op_cost, cost_note="mixed haiku/opus per trace",
    )


# ---------------------------------------------------------------------------
# 5. legacy-crm-adapter — PARTIAL CONFORM (~50% spans missing tokens)
#    12 traces × 2 spans = 24 spans  |  sonnet  |  10 000 in / 2 000 out per conformant span
#    Even-indexed spans have tokens; odd-indexed spans omit gen_ai.usage.*.
#    Expected cost: 12 conformant spans × (10000×$3 + 2000×$15) / 1M = $0.72
# ---------------------------------------------------------------------------

def emit_legacy_crm_adapter(endpoint: str, scale: int) -> AgentResult:
    SERVICE  = "legacy-crm-adapter"
    AGENT_ID = "svc-legacy-crm-e5f6a7b8-c9d0-1234-efab-345678901234"
    IN, OUT  = 10_000, 2_000
    tracer   = _make_tracer(SERVICE, endpoint)
    n_traces = 12 * scale
    conformant = 0
    for i in range(n_traces):
        with tracer.start_as_current_span(f"crm-lookup-{i}") as root:
            if i % 2 == 0:
                _attrs(root, SONNET, "anthropic", IN, OUT, AGENT_ID)
                conformant += 1
            else:
                _attrs_no_tokens(root, SONNET, "anthropic", AGENT_ID)
            with tracer.start_as_current_span(f"crm-update-{i}") as child:
                if i % 2 != 0:
                    _attrs(child, SONNET, "anthropic", IN, OUT, AGENT_ID)
                    conformant += 1
                else:
                    _attrs_no_tokens(child, SONNET, "anthropic", AGENT_ID)
    cost = _cost(SONNET, conformant * IN, conformant * OUT)
    return AgentResult(
        service_name=SERVICE, archetype="PARTIAL CONFORM",
        spans_emitted=n_traces * 2, error_spans=0,
        defect="~50% spans missing gen_ai.usage.* (cost_coverage≈0.5)",
        priceable_input=conformant * IN, priceable_output=conformant * OUT,
        estimated_cost=cost, cost_note="~50% coverage",
    )


# ---------------------------------------------------------------------------
# 6. policy-checker — NO TOKENS (cost_coverage = 0)
#    8 traces × 1 span = 8 spans  |  sonnet  |  no gen_ai.usage.*
# ---------------------------------------------------------------------------

def emit_policy_checker(endpoint: str, scale: int) -> AgentResult:
    SERVICE  = "policy-checker"
    AGENT_ID = "svc-policy-chk-f6a7b8c9-d0e1-2345-fabc-456789012345"
    tracer   = _make_tracer(SERVICE, endpoint)
    n_traces = 8 * scale
    for i in range(n_traces):
        with tracer.start_as_current_span(f"policy-eval-{i}") as span:
            _attrs_no_tokens(span, SONNET, "anthropic", AGENT_ID)
    return AgentResult(
        service_name=SERVICE, archetype="NO TOKENS",
        spans_emitted=n_traces, error_spans=0,
        defect="no gen_ai.usage.* — conformance_ratio=0, cost_coverage=0",
        estimated_cost=None, cost_note="no token fields",
    )


# ---------------------------------------------------------------------------
# 7. experimental-llm — UNKNOWN MODEL (not in pricing.json)
#    6 traces × 1 span = 6 spans  |  x-proprietary-llm-v9  |  12 000 in / 2 500 out
# ---------------------------------------------------------------------------

def emit_experimental_llm(endpoint: str, scale: int) -> AgentResult:
    SERVICE       = "experimental-llm"
    AGENT_ID      = "svc-experimental-a7b8c9d0-e1f2-3456-abcd-567890123456"
    UNKNOWN_MODEL = "x-proprietary-llm-v9"
    IN, OUT       = 12_000, 2_500
    tracer        = _make_tracer(SERVICE, endpoint)
    n_traces      = 6 * scale
    for i in range(n_traces):
        with tracer.start_as_current_span(f"experimental-gen-{i}") as span:
            _attrs(span, UNKNOWN_MODEL, "x-labs", IN, OUT, AGENT_ID)
    return AgentResult(
        service_name=SERVICE, archetype="UNKNOWN MODEL",
        spans_emitted=n_traces, error_spans=0,
        defect=f"model '{UNKNOWN_MODEL}' not in pricing.json → cost=null",
        estimated_cost=None, cost_note=f"'{UNKNOWN_MODEL}' not in pricing.json",
    )


# ---------------------------------------------------------------------------
# 8. unidentifiable-agent — UNIDENTIFIABLE (present-but-invalid agent_id)
#    5 traces × 1 span = 5 spans  |  haiku  |  agent_id="bot" (3 chars < 8-char min)
#    Spans stored in span_state with agent_id IS NULL; absent from /v1/agents.
# ---------------------------------------------------------------------------

def emit_unidentifiable_agent(endpoint: str, scale: int) -> AgentResult:
    SERVICE = "unidentifiable-agent"
    INVALID = "bot"   # 3 chars — below the 8-char minimum in the conformance floor
    IN, OUT = 6_000, 1_200
    tracer  = _make_tracer(SERVICE, endpoint)
    n_traces = 5 * scale
    for i in range(n_traces):
        with tracer.start_as_current_span(f"unident-call-{i}") as span:
            _attrs(span, HAIKU, "anthropic", IN, OUT, INVALID)
    return AgentResult(
        service_name=SERVICE, archetype="UNIDENTIFIABLE (present-but-invalid)",
        spans_emitted=n_traces, error_spans=0,
        defect='agent_id="bot" (3 chars < 8-char min) → absent from /v1/agents',
        estimated_cost=None, cost_note="absent from /v1/agents",
    )


# ---------------------------------------------------------------------------
# 9. no-agent-id-agent — UNIDENTIFIABLE (absent entirely — no attribute emitted)
#    4 traces × 1 span = 4 spans  |  haiku  |  no clawindex.agent.id attribute
#    Spans stored in span_state with agent_id IS NULL; absent from /v1/agents.
# ---------------------------------------------------------------------------

def emit_no_agent_id_agent(endpoint: str, scale: int) -> AgentResult:
    SERVICE  = "no-agent-id-agent"
    IN, OUT  = 6_000, 1_200
    tracer   = _make_tracer(SERVICE, endpoint)
    n_traces = 4 * scale
    for i in range(n_traces):
        with tracer.start_as_current_span(f"no-id-call-{i}") as span:
            span.set_attribute("gen_ai.operation.name",      OPNAME)
            span.set_attribute("gen_ai.provider.name",       "anthropic")
            span.set_attribute("gen_ai.request.model",       HAIKU)
            span.set_attribute("gen_ai.usage.input_tokens",  IN)
            span.set_attribute("gen_ai.usage.output_tokens", OUT)
            # Intentionally omit clawindex.agent.id — absent entirely
    return AgentResult(
        service_name=SERVICE, archetype="UNIDENTIFIABLE (absent entirely)",
        spans_emitted=n_traces, error_spans=0,
        defect="no clawindex.agent.id attribute → absent from /v1/agents",
        estimated_cost=None, cost_note="absent from /v1/agents",
    )


# ---------------------------------------------------------------------------
# 10. helpdesk-triage — HEALTHY (5% error rate)
#     15 traces × 2 spans = 30 spans  |  sonnet  |  12 000 in / 2 500 out per span
#     First 1*scale traces are all-spans-errored (~5% of traces at scale=1)
#     Expected cost: 30 × (12000×$3 + 2500×$15) / 1M = $2.205
# ---------------------------------------------------------------------------

def emit_helpdesk_triage(endpoint: str, scale: int) -> AgentResult:
    SERVICE   = "helpdesk-triage"
    AGENT_ID  = "svc-helpdesk-tri-b8c9d0e1-f2a3-4567-bcde-678901234567"
    IN, OUT   = 12_000, 2_500
    tracer    = _make_tracer(SERVICE, endpoint)
    n_traces  = 15 * scale
    err_cut   = 1 * scale
    err_spans = 0
    for i in range(n_traces):
        with tracer.start_as_current_span(f"helpdesk-triage-{i}") as root:
            _attrs(root, SONNET, "anthropic", IN, OUT, AGENT_ID)
            if i < err_cut:
                root.set_status(Status(StatusCode.ERROR, "Ticket routing error"))
                err_spans += 1
            with tracer.start_as_current_span(f"helpdesk-classify-{i}") as child:
                _attrs(child, SONNET, "anthropic", IN, OUT, AGENT_ID)
                if i < err_cut:
                    child.set_status(Status(StatusCode.ERROR, "Classification failed"))
                    err_spans += 1
    spans = n_traces * 2
    cost  = _cost(SONNET, spans * IN, spans * OUT)
    return AgentResult(
        service_name=SERVICE, archetype="HEALTHY",
        spans_emitted=spans, error_spans=err_spans, defect="—",
        priceable_input=spans * IN, priceable_output=spans * OUT, estimated_cost=cost,
    )


# ---------------------------------------------------------------------------
# 11. document-pipeline — DEEP TRACE
#     5 traces × 4 spans = 20 spans  |  sonnet  |  18 000 in / 4 000 out per span
#     Topology per trace: root → child1 → grandchild  +  sibling child2 under root
#     Trace 0 only: child-before-parent ordering via set_span_in_context.
#       Grandchild and child1 arrive at the collector before root, exercising the
#       InsertPlaceholderSpanIfAbsentAsync path in DurableSpanSink.
#     Expected cost: 20 × (18000×$3 + 4000×$15) / 1M = $2.28
# ---------------------------------------------------------------------------

def emit_document_pipeline(endpoint: str, scale: int) -> AgentResult:
    SERVICE  = "document-pipeline"
    AGENT_ID = "svc-doc-pipe-c9d0e1f2-a3b4-5678-cdef-789012345678"
    IN, OUT  = 18_000, 4_000
    tracer   = _make_tracer(SERVICE, endpoint)
    n_traces = 5 * scale

    def _da(span):
        _attrs(span, SONNET, "anthropic", IN, OUT, AGENT_ID)

    for i in range(n_traces):
        if i == 0:
            # Child-before-parent: start root without ending it, emit children first.
            # root.end() is called last so the collector receives root after its children.
            root     = tracer.start_span("doc-extract")
            root_ctx = set_span_in_context(root)
            _da(root)

            child1     = tracer.start_span("doc-chunk-retrieval", context=root_ctx)
            child1_ctx = set_span_in_context(child1)
            _da(child1)

            with tracer.start_as_current_span("doc-embed", context=child1_ctx) as grandchild:
                _da(grandchild)
            # ← grandchild exported first

            child1.end()
            # ← child1 exported second

            with tracer.start_as_current_span("doc-summarize", context=root_ctx) as child2:
                _da(child2)
            # ← child2 exported third

            root.end()
            # ← root exported last; collector received children before parent
        else:
            with tracer.start_as_current_span(f"doc-extract-{i}") as root:
                _da(root)
                with tracer.start_as_current_span(f"doc-chunk-{i}") as child1:
                    _da(child1)
                    with tracer.start_as_current_span(f"doc-embed-{i}") as grandchild:
                        _da(grandchild)
                with tracer.start_as_current_span(f"doc-summarize-{i}") as child2:
                    _da(child2)

    spans = n_traces * 4
    cost  = _cost(SONNET, spans * IN, spans * OUT)
    return AgentResult(
        service_name=SERVICE, archetype="DEEP TRACE",
        spans_emitted=spans, error_spans=0,
        defect="child-before-parent in trace 1 (exercises placeholder path)",
        priceable_input=spans * IN, priceable_output=spans * OUT, estimated_cost=cost,
    )


# ---------------------------------------------------------------------------
# Summary output
# ---------------------------------------------------------------------------

_W = 70


def _hr():  print("─" * _W)
def _dhr(): print("═" * _W)


def _print_summary(
    results: list[AgentResult], endpoint: str, seed: int, scale: int
) -> None:
    _dhr()
    print(f"  ClawIndex Chaos Monkey — fleet emission complete")
    print(f"  seed={seed}  scale={scale}  endpoint={endpoint}")
    _dhr()
    print()
    print("ROSTER")
    _hr()
    print(f"  {'#':>2}  {'service.name':<28} {'archetype':<32} {'spans':>5} {'errs':>5}")
    _hr()
    for idx, r in enumerate(results, 1):
        print(f"  {idx:>2}  {r.service_name:<28} {r.archetype:<32} {r.spans_emitted:>5} {r.error_spans:>5}")
    _hr()
    total_spans = sum(r.spans_emitted for r in results)
    unattrib    = sum(
        r.spans_emitted for r in results
        if r.estimated_cost is None and "absent from" in r.defect
    )
    print(f"  Total spans: {total_spans}  ({unattrib} stored but unattributable — absent from /v1/agents)")
    print()

    priceable   = [r for r in results if r.estimated_cost is not None]
    unpriceable = [r for r in results if r.estimated_cost is None]
    fleet_cost  = sum((r.estimated_cost for r in priceable), Decimal("0"))

    print("EXPECTED ECONOMICS  (priceable agents only)")
    _hr()
    for r in priceable:
        note = f"  ({r.cost_note})" if r.cost_note else ""
        print(
            f"  {r.service_name:<28}  {r.priceable_input:>10,} in"
            f"  {r.priceable_output:>8,} out   ${r.estimated_cost:.6f}{note}"
        )
    _hr()
    tot_in  = sum(r.priceable_input  for r in priceable)
    tot_out = sum(r.priceable_output for r in priceable)
    print(f"  {'Fleet total':<28}  {tot_in:>10,} in  {tot_out:>8,} out   ${fleet_cost:.6f}")
    if unpriceable:
        print()
        print("  Excluded — uncomputable:")
        for r in unpriceable:
            print(f"    {r.service_name:<30} {r.cost_note}")
    print()

    spend_leader = max(priceable, key=lambda r: r.estimated_cost)
    vol_leader   = max(results,   key=lambda r: r.spans_emitted)
    bleeder      = next((r for r in results if "BLEEDER" in r.archetype), None)
    print("WHAT TO LOOK FOR")
    print(f"  Spend leader:  {spend_leader.service_name} — ${spend_leader.estimated_cost:.2f}"
          f" ({spend_leader.spans_emitted} spans, {100*spend_leader.estimated_cost/fleet_cost:.0f}% of fleet spend)")
    print(f"  Volume leader: {vol_leader.service_name} — {vol_leader.spans_emitted} spans, cheap per call")
    if bleeder:
        print(f"  The bleeder:   {bleeder.service_name} — {bleeder.error_spans} error spans, spend on failures")
    print(f"  Coverage gaps: legacy-crm-adapter (~50% cost_coverage), policy-checker (0%)")
    print(f"  Dark activity: {unattrib} spans stored; absent from /v1/agents (two failure modes)")
    print(f"  Time note:     Spans emit in real-time order. SDK does not support backdating.")
    print(f"                 All spans appear near emission time in viewer timeline.")
    _dhr()


# ---------------------------------------------------------------------------
# Emission order
# ---------------------------------------------------------------------------

_EMITTERS = [
    emit_invoice_processor,
    emit_contract_analyst,
    emit_escalation_router,
    emit_research_assistant,
    emit_legacy_crm_adapter,
    emit_policy_checker,
    emit_experimental_llm,
    emit_unidentifiable_agent,
    emit_no_agent_id_agent,
    emit_helpdesk_triage,
    emit_document_pipeline,
]


def main() -> None:
    parser = argparse.ArgumentParser(
        description="ClawIndex chaos monkey — multi-agent fleet generator",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument(
        "--endpoint",
        default=os.environ.get("CLAWINDEX_ENDPOINT", "http://localhost:8080"),
        metavar="URL",
        help="Base URL of the ClawIndex collector (default: http://localhost:8080)",
    )
    parser.add_argument(
        "--seed", type=int, default=42,
        help="Reserved for future timing jitter (default: 42)",
    )
    parser.add_argument(
        "--scale", type=int, default=1,
        help="Multiply trace counts by N; token counts per span unchanged (default: 1)",
    )
    args = parser.parse_args()

    if args.scale < 1:
        print(f"ERROR: --scale must be >= 1 (got {args.scale})")
        sys.exit(1)

    endpoint: str = args.endpoint.rstrip("/")

    try:
        requests.get(f"{endpoint}/v1/health", timeout=5).raise_for_status()
    except Exception as exc:
        print(f"ERROR: collector not reachable at {endpoint}")
        print(f"       {exc}")
        print()
        print("       Start the collector first:  docker compose up   (from repo root)")
        sys.exit(1)

    print(f"Collector : {endpoint}  (healthy)")
    print(f"Agents    : {len(_EMITTERS)}   seed={args.seed}   scale={args.scale}")
    print()

    results: list[AgentResult] = []
    for emitter in _EMITTERS:
        label = emitter.__name__.replace("emit_", "").replace("_", "-")
        print(f"  Emitting {label}…", end="", flush=True)
        result = emitter(endpoint, args.scale)
        results.append(result)
        print(f" {result.spans_emitted} spans")

    print()
    _print_summary(results, endpoint, args.seed, args.scale)


if __name__ == "__main__":
    main()
