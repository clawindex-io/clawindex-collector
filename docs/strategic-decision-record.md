# Strategic Decision Record — Product Definition (v1)

Status: accepted
Date: 2026-06-28

This record captures three linked product decisions that redirect what ClawIndex is and what gets built. It is intentionally short and standing: future work checks itself against it. It supersedes the SRE-dashboard-as-product framing and the live-stuck-detection framing in earlier product notes and the demo.

## One-sentence thesis

ClawIndex answers the question Datadog and generic observability won't: **how much money is being spent on stuck agents, on agents escalating when they didn't need to, and where is the accountability for that drag** — for the durable history of an operator's own agent fleet.

## Decision 1 — The product is the economics-and-accountability layer, not a dashboard

The differentiated value is durable agent-fleet economics and accountability: cost attribution, unnecessary-escalation spend, human-bottleneck accountability, and trend over quarters. This is the thing generic observability tools structurally do not do, because their model is short-retention live operational telemetry sold to teams who are good at observability — and our buyer is the team that is not.

The SRE/operational view is not the product. It is the data on-ramp that collects the spans the economics layer needs. We build just enough of it and do not try to win on it. We are not building a better Datadog.

## Decision 2 — Complete-spans-only is the ingestion foundation

P1 ingestion accepts complete (ended) spans only. Spans with no end time are not ingested as live open state.

Rationale: this aligns with how OTLP is overwhelmingly emitted by default (export on span end), and it removes the most failure-prone machinery (live open-span storage, restart recovery of in-flight spans, reconciliation) from the critical path. Economics and accountability are inherently post-hoc and aggregate, so they lose nothing from this.

What this does NOT cost us:
- Failed/errored agents — completed error spans, full fidelity.
- Churning/slow/cost-spiking agents — detectable as completed-span rate/volume/pattern.
- Stuck agents — reported after resolution (timeout error span, or very-late completion) and, later, via silence-inference against derived baselines.
- Human bottleneck (the "Burt" signal) — an aggregate accountability analytic in the economics view, NOT a live alert. Real-time "a human isn't responding" has near-zero operational value; the value is the aggregate finding, surfaced to whoever owns the process.

Live open-span watching is explicitly NOT a P1 claim. Real-time stuck detection, if built, is a later silence-inference feature on top of this foundation, and requires durable history first to compute baselines.

## Decision 3 — SRE visibility is bring-your-own-viewer; custom UI is only the economics layer

We do not build a custom SRE/span dashboard. ClawIndex is a standards-correct OTLP backend; existing OTLP-compatible viewers (Grafana, Aspire, Jaeger, etc.) already render span/trace/health views. Operators point the viewer they already run at ClawIndex. Building our own span renderer would reinvent, worse, what the ecosystem already ships.

The wedge for the immature team: stop paying premium retention prices to watch agent spans; point agents at ClawIndex (open source, self-host or managed), view in the Grafana/Aspire you already run, and get the cost-and-accountability analysis the generic tools don't provide.

The only custom-built UI is the economics-and-accountability view, because no generic OTLP viewer does cost attribution, unnecessary-escalation spend, or accountability rollups — those require our pricing matrix, baselines, and derived analytics. This may begin as exports/reports before becoming an interactive surface.

Consequence: OTLP conformance becomes a first-class product requirement. "Works with your existing viewer" is only true if we are genuinely conformant, so conformance is a real line item, not an afterthought.

## Open-core / managed line

- Open core (AGPL): the OTLP collector — ingest complete SemConv spans, durable store, read API. Bring-your-own-viewer. Self-hostable. The adoption on-ramp.
- Managed / proprietary: the economics-and-accountability layer — pricing matrix, cost and unnecessary-escalation rollups, human-bottleneck accountability, trend-over-time. The differentiated, hosted, charged-for surface, sitting where a self-hoster cannot trivially replicate it.

## Standing guidance for future work

- Do NOT build a custom SRE/span dashboard. Be a good OTLP backend instead.
- Do NOT make live-open-span or real-time "human not responding" a P1 feature or claim.
- The SRE on-ramp gets just enough; effort concentrates on the durable store and the economics layer.
- Every surface is single-tenant; all economics/accountability is intra-tenant.

## Hedge

Even if ClawIndex does not become a standalone company, the exercise produces a durable risk-and-governance consulting asset for Range Point: a built framework and concrete findings for the dollar cost of unnecessary escalation and stuck/underperforming agents.
