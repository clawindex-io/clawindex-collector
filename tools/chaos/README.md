# ClawIndex Chaos Monkey

Multi-agent fleet generator for ClawIndex. Emits 11 deliberately varied agents — each
embodying a distinct economics archetype — so you can evaluate what the product surfaces
across a realistic fleet.

This is a **generation tool, not an assertion harness.** It prints expected numbers and
tells you what to look for; compare them to what the ClawIndex read API actually reports.
For single-agent correctness testing, see `tools/smoke/smoke.py`.

---

## What it generates

| # | service.name | Archetype | What to look for |
|---|---|---|---|
| 1 | invoice-processor | HIGH-VOL CHEAP | Volume leader: 80 spans, but $1 total |
| 2 | contract-analyst | LOW-VOL EXPENSIVE | Spend leader: 5 spans, $30 — invisible if sorted by volume |
| 3 | escalation-router | BLEEDER | 40 spans; 23/40 error spans; spend concentrated on failures |
| 4 | research-assistant | MULTI-MODEL | Mixed haiku/opus per trace; $12 despite only 20 spans |
| 5 | legacy-crm-adapter | PARTIAL CONFORM | ~50% cost_coverage |
| 6 | policy-checker | NO TOKENS | conformance_ratio=0, cost_coverage=0, cost=null |
| 7 | experimental-llm | UNKNOWN MODEL | has tokens, cost=null — model not in pricing.json |
| 8 | unidentifiable-agent | UNIDENTIFIABLE (invalid id) | agent_id="bot" (3 chars) — absent from /v1/agents |
| 9 | no-agent-id-agent | UNIDENTIFIABLE (absent) | no clawindex.agent.id — absent from /v1/agents |
| 10 | helpdesk-triage | HEALTHY | 30 spans, ~5% error rate |
| 11 | document-pipeline | DEEP TRACE | root→child1→grandchild + sibling; child-before-parent in trace 1 |

**Fleet total at `--scale 1` (default): ~$52**

```
contract-analyst:    $30.00   (58% of fleet spend, 5 spans = 2% of volume)
research-assistant:  $12.08   (23% of fleet spend, 20 spans)
escalation-router:    $3.60   (7%)
helpdesk-triage:      $2.21
document-pipeline:    $2.28
legacy-crm-adapter:   $0.72   (~50% coverage)
invoice-processor:    $0.99   (2% of spend, 80 spans = 33% of volume)
```

The central insight: **contract-analyst dominates spend with 2% of the span volume.**
Sorted by span count, it is near the bottom. Sorted by cost, it is far at the top.

---

## Prerequisites

- Python 3.11+
- ClawIndex collector running (see repo root `docker compose up`)
- No API keys required — all token counts are hardcoded constants

---

## Install and run

```bash
cd tools/chaos
python -m venv .venv
source .venv/bin/activate   # Windows: .venv\Scripts\activate
pip install -r requirements.txt

python chaos.py
```

With a non-default endpoint:

```bash
python chaos.py --endpoint http://my-host:8080
# or
CLAWINDEX_ENDPOINT=http://my-host:8080 python chaos.py
```

To generate more data proportionally:

```bash
python chaos.py --scale 5    # 5× the trace counts; token counts per span unchanged
```

---

## Sample output

```
Collector : http://localhost:8080  (healthy)
Agents    : 11   seed=42   scale=1

  Emitting invoice-processor… 80 spans
  Emitting contract-analyst… 5 spans
  ...

══════════════════════════════════════════════════════════════════════
  ClawIndex Chaos Monkey — fleet emission complete
  seed=42  scale=1  endpoint=http://localhost:8080
══════════════════════════════════════════════════════════════════════

ROSTER
──────────────────────────────────────────────────────────────────────
   #  service.name                 archetype                         spans   errs
   1  invoice-processor            HIGH-VOL CHEAP                       80      0
   2  contract-analyst             LOW-VOL EXPENSIVE                     5      0
  ...
──────────────────────────────────────────────────────────────────────
  Total spans: 242  (9 stored but unattributable — absent from /v1/agents)

EXPECTED ECONOMICS  (priceable agents only)
──────────────────────────────────────────────────────────────────────
  contract-analyst              1,000,000 in   200,000 out   $30.000000
  research-assistant            ...
  Fleet total                   ...
```

---

## The two unidentifiable cases

Agents 8 and 9 exercise two distinct failure modes that produce the same ClawIndex
outcome — absent from `/v1/agents`:

| Agent | What's wrong | Why it fails |
|---|---|---|
| `unidentifiable-agent` | `clawindex.agent.id = "bot"` | 3 chars, below 8-char minimum |
| `no-agent-id-agent` | no `clawindex.agent.id` attribute | attribute absent entirely |

Both agents have a valid `service.name` on their OTel Resource, so their spans appear
in downstream viewers (Aspire, Datadog, Grafana). The contrast — visible in your viewer,
absent from ClawIndex — is the point. Getting operators to add `clawindex.agent.id` is
the on-ramp; this shows what happens when they don't.

---

## The child-before-parent trace

`document-pipeline` trace 1 emits spans in reverse order using `set_span_in_context`:

```python
root     = tracer.start_span("doc-extract")
root_ctx = set_span_in_context(root)

child1     = tracer.start_span("doc-chunk-retrieval", context=root_ctx)
child1_ctx = set_span_in_context(child1)

with tracer.start_as_current_span("doc-embed", context=child1_ctx) as grandchild:
    ...
# ← grandchild exported (arrives at collector first)

child1.end()
# ← child1 exported

with tracer.start_as_current_span("doc-summarize", context=root_ctx) as child2:
    ...
# ← child2 exported

root.end()
# ← root exported last
```

The collector receives the grandchild before the root exists in `span_state`. This
exercises `InsertPlaceholderSpanIfAbsentAsync` in `DurableSpanSink` — the real
durability path for out-of-order multi-level traces.

---

## Pricing

The script reads pricing from `src/Clawindex.Collector.Api/Economics/pricing.json` at
startup — no pricing table is duplicated here. If the expected cost in the summary
differs from what the collector reports, it is a bug, not a stale copy.

`experimental-llm` uses model `x-proprietary-llm-v9`, which deliberately does not exist
in pricing.json. Its spans are stored with token counts but without a cost.

---

## Known limitation: time distribution

The OTel SDK sets span `start_time` and `end_time` to the real wall-clock time at which
spans are emitted. Spans from all 11 agents will cluster within the few seconds that
`chaos.py` takes to run; they will not spread across a realistic multi-hour operational
window.

To spread them across a realistic time window, you would need to either:
- Run `chaos.py` repeatedly over time (e.g. via a cron job with `--scale 1`)
- Backdate `start_time` / `end_time` in the OTLP protobuf before export (not
  supported by the high-level OTel SDK; requires hand-rolling the export layer)

For evaluating ClawIndex economics features, clustering is sufficient: all 242 spans
arrive within the `?since=` window and the rollups are correct. The timeline view in
downstream viewers will look unrealistic; the cost attribution will not.
