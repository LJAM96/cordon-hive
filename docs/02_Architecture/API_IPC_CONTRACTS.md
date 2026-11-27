# API, real-time and IPC contracts

## Contract conventions

Proposed REST `/api/v1`, JSON DTOs, ISO 8601 UTC, UUID IDs, explicit protocol and capability versions, pagination with bounded limits, correlation ID, standard ProblemDetails. TLS required. Mutation endpoints use idempotency key plus actor/scope binding and optimistic concurrency (`If-Match` or row version). Return 401 unauthenticated, 403 unauthorised, 409 conflict, 422 invalid state, 429 throttled. Do not reveal other users' resources via errors. Separate device mTLS endpoints from user authentication endpoints.

| Endpoint | Actor | Behaviour |
|---|---|---|
| POST /devices/enroll | Enrollment authority | Redeem short-lived single-use proof, register key |
| POST /agent/heartbeat | Device | Report boot/session/agent version/capabilities; device identity derived from certificate |
| POST /agent/metrics | Device | Bounded batches, dedupe by device/boot/sequence |
| PUT /agent/inventory | Device | Versioned snapshot with detection provenance |
| GET /agent/sync?cursor=... | Device | Device-scoped pending policy, deployment state and events |
| POST /agent/deployments/{id}/lease | Device | Request fresh short-lived execution permit for eligible target |
| POST /agent/deployments/{id}/events | Device | State transitions/verification, idempotent and validated |
| GET /me | User | Authorised identity, roles and scopes |
| GET /me/notifications | User | Only permitted notifications and versioned state |
| POST /me/notifications/{id}/ack | User | Acknowledge own recipient; never implicitly complete update |
| GET /workstations | Supervisor | Scoped fleet and freshness |
| PATCH /workstations/{id}/assignment | Supervisor | Allocate, concurrency checked |
| POST /software/releases | Software admin | Draft immutable release metadata |
| POST /software/releases/{id}/approve | Approver | Review validation, signer and digest |
| POST /deployments | Software admin | Preview/confirm targets and approved release |
| GET /packages/{id}/content | Eligible device | Immutable authorised HTTPS stream, Range/ETag |
| POST /notifications | Authorised publisher | Validate audience, priority and completion rule |
| POST /workstations/{id}/rdp-request | RDP-authorised user | Audit request, return permitted endpoint and warnings; not credentials |

Endpoints are sketches, not a stable published API. User-specific notification payloads should be fetched by authenticated user rather than indiscriminately through device sync. Service can receive opaque hints or device-scoped actions only. For offline signed-in users, previously authorised cache stays isolated by SID.

## SignalR

Server pushes `StateChanged` envelope with event ID, type, resource ID, revision and scope-safe hint, not arbitrary executable payload. Server assigns groups from authenticated identity; clients cannot join arbitrary group names. Connections reconnect with jitter; after reconnect fetch delta or snapshot regardless of push success. Do not use hub delivery as durable proof. Expired sessions reauthenticate.

Example envelope:

```json
{
  "schemaVersion": 1,
  "eventId": "a6ef73b3-994a-48b9-a812-92e6c868e5fb",
  "type": "notification.changed",
  "resourceId": "f9dca0c8-7195-4767-a307-aa7a5e9d23ea",
  "revision": 3,
  "occurredAtUtc": "2026-11-27T12:00:00Z"
}
```

## Local IPC

Named pipe with restrictive DACL, local-only access, authenticated caller SID/session, version handshake, bounded framing, message size and timeouts. Separate user-facing and broker pipes. User messages: local resources, local applicable update status, set/release own protection lock, request eligible update, widget health. Never accept a user-supplied SID as authority. Broker messages: approved target ID, verified manifest reference and fresh permit; broker rechecks signature, staging ACL, policy, workload and path. No arbitrary executable path, shell string or credential transfer. Rate limit and audit rejected requests. Future suite publisher API is separate, with publisher registration and limited authority; source cannot complete another source's action.
