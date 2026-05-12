# Intake v1 Specification

## Objective

Implement the first operational intake pipeline for Clawindex Collector.

## Functional Requirements

### Single Event Intake

Endpoint:
POST /v1/events

Capabilities:
- Accept JSON event payloads
- Validate required envelope fields
- Generate event_id if missing
- Generate received_at server-side
- Persist accepted events
- Return acknowledgement response

### Batch Event Intake

Endpoint:
POST /v1/events/batch

Capabilities:
- Accept arrays of events
- Validate each event independently
- Support partial success/failure
- Return per-event processing results

## Validation Rules

Reject if:
- schema_version missing
- event_type missing
- occurred_at missing or invalid
- source missing
- source.system missing
- payload missing
- payload not object

Allow if:
- event_id missing
- correlation missing
- unknown event_type
- source.component missing
- source.version missing

## Acceptance Criteria

### AC1 — Health Check

Given the service is running
When GET /v1/health is called
Then 200 OK is returned.

### AC2 — Valid Event

Given a valid event
When posted to /v1/events
Then it is stored and acknowledged.

### AC3 — Invalid Event

Given an event missing event_type
When posted to /v1/events
Then the request is rejected.

### AC4 — Batch Intake

Given multiple events
When posted to /v1/events/batch
Then valid events are accepted and invalid events rejected independently.
