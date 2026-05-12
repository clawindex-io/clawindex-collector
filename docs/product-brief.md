# Clawindex Collector v0.1 — Product Brief

## Objective

Build the first Clawindex collector service capable of receiving, validating,
persisting, and acknowledging governed agent telemetry events.

This phase focuses exclusively on intake.

## Scope

### In Scope

- Event intake API
- Envelope validation
- Event persistence
- Batch ingestion
- Acknowledgement responses
- Correlation metadata support

### Out of Scope

- OTLP export
- GenAI SemConv mapping
- Replay engine
- Clawindex UI
- Policy evaluation
- Multi-tenant support
- Advanced auth

## Primary Goal

Prove that governed agent/runtime telemetry can reliably enter a standardized operational pipeline.

## Initial Architecture

Agent / bouncer-md / adapter
        ↓
Clawindex Collector
        ↓
Validated + persisted event intake

## Initial Endpoints

- GET /v1/health
- GET /v1/schema
- POST /v1/events
- POST /v1/events/batch

## Success Criteria

- Valid events are accepted and stored
- Invalid events are rejected with clear validation errors
- Batch events support partial success
- Raw inbound payloads are preserved
- Correlation identifiers remain intact
