# Event Envelope v0.1

## Canonical Event Shape

```json
{
  "schema_version": "0.1.0",
  "event_id": "evt_123",
  "event_type": "policy.evaluated",
  "occurred_at": "2026-05-11T22:15:00Z",
  "source": {
    "system": "bouncer-md",
    "component": "resolver",
    "version": "1.0.0"
  },
  "correlation": {
    "trace_id": "trace_abc",
    "span_id": "span_123",
    "task_id": "task_456",
    "agent_id": "agent_soil_report",
    "session_id": "session_789"
  },
  "payload": {
    "decision": "deny",
    "policy_id": "soil-data-scope",
    "reason": "Tool call attempted outside approved scope"
  }
}
```

## Required Fields

- schema_version
- event_type
- occurred_at
- source.system
- payload

## Optional Fields

- event_id
- source.component
- source.version
- correlation.*
- payload extensions

## Initial Event Types

- policy.evaluated
- policy.allowed
- policy.denied
- policy.escalated
- policy.error
- tool.call.started
- tool.call.completed
- tool.call.failed
- agent.task.started
- agent.task.completed
- agent.task.failed
- human.review.requested
- human.review.completed

## Design Principles

- Extensible
- Forward-compatible
- Vendor-neutral
- OTel-aligned
- Replay-friendly
- Governance-aware
