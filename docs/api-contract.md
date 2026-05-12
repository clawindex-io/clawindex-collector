# API Contract v0.1

## GET /v1/health

### Response

```json
{
  "status": "ok",
  "service": "clawindex-collector",
  "version": "0.1.0"
}
```

---

## GET /v1/schema

### Response

```json
{
  "schema_version": "0.1.0",
  "name": "clawindex.event.envelope"
}
```

---

## POST /v1/events

### Request

```json
{
  "schema_version": "0.1.0",
  "event_type": "policy.evaluated",
  "occurred_at": "2026-05-11T22:15:00Z",
  "source": {
    "system": "bouncer-md"
  },
  "payload": {
    "decision": "deny"
  }
}
```

### Success Response

```json
{
  "status": "accepted",
  "event_id": "evt_generated_123",
  "received_at": "2026-05-11T22:15:01Z",
  "schema_version": "0.1.0"
}
```

### Failure Response

```json
{
  "status": "rejected",
  "error": {
    "code": "validation_failed",
    "message": "Missing required field: event_type"
  }
}
```

---

## POST /v1/events/batch

### Request

```json
{
  "events": []
}
```

### Response

```json
{
  "status": "partial",
  "accepted_count": 1,
  "rejected_count": 1,
  "results": []
}
```
