# Durable notification system

## Model

One logical notification may target a user SID, device UUID, approved group or explicit audience snapshot. Define whether group membership is snapshotted or dynamic at publication (MVP snapshot). User notification follows the authenticated user across eligible devices; device notification follows asset and is visible only to authorised sessions. Keep sensitive text out of lock-screen previews by default. Admin previews audience before send. Priority and due date are independent from completion semantics.

| Type | Clears from actionable count when |
|---|---|
| Informational | Read or dismissed per policy |
| Announcement | Authorised recipient acknowledges |
| Required action | Explicit configured completion recorded by authorised actor |
| Software update | Agent independently verifies required applicable version or approved exemption/withdrawal changes requirement |
| Maintenance | Approved maintenance completion event |
| Critical condition | Authorised source verifies condition resolved |

Recipient states: pending delivery, delivered to app, read, acknowledged, completed, cancelled/withdrawn; timestamps may be separate, not a single mutually exclusive enum. Toast submission is not proof a human saw it. Acknowledging required update does not complete it. Due dates escalate visibly; never bypass workload safeguards. Reopened/new requirement receives revised state and audit.

## Delivery algorithm

Persist notification, recipient rows and outbox in one DB transaction. Background dispatcher sends SignalR hint to authorised groups. Session app fetches delta or authoritative snapshot; transactionally commits to per-user cache; then records delivery and optionally submits native Windows toast with dedupe tag. Widget computes unresolved actionable count from cache, not toast unread count. Read/ack commands persist with idempotency keys and reconcile server response. Offline acknowledgements can be queued with a pending-sync label, but server remains authoritative and may reject revoked/expired actions. If no active session, machine-scoped actions persist on agent/server; user messages fetched on next authenticated logon. Reconnect always reconciles missed events. Keep last known unresolved count plus stale flag on outage; never substitute zero or hide the obligations.

## Desktop presentation

Unelevated WinUI application uses AppNotificationManager; register activation before showing toasts, route payload ID to authorised detail view and re-fetch state. Do not trust activation arguments as authorisation. Honour Windows settings, focus assist, accessibility and secure desktop. The widget is a persistent local reminder even when a toast expires; closing flyout never acknowledges. Standard tray fallback, open notification centre, keyboard access. User can close software; managed startup and service health monitoring detect absence without invasive anti-termination mechanisms. Main window closure minimises to resident session host by default, explicit exit allowed by policy.

## Multi-session and privacy

One host per interactive session, cache partitioned by authenticated SID and scoped device. On lock, disconnect or user switch suppress sensitive presentation; on sign-out dispose session state and credentials. User B never sees user A's messages. RDP session has its own desktop and widget. Resolve audience using server identity rather than selected display name. Machine update notification can appear in multiple authorised sessions but is one device obligation; dedupe completion. Agent reports UI host availability separately.

## Shared Cordon notifications (later)

Design a publisher contract now: source ID, notification ID, type, title, safe summary, correlation ID, priority, completion rule, revision and target scope. Local authenticated pipe maps registered publisher identity; each source can create/update/resolve only its own permitted items. No arbitrary launch URI, executable commands or privilege escalation. Cordon Alert can publish dashboard tasks through an adapter rather than duplicating widget. Remote product-specific notifications remain distinct from admin commands. Federation is not required for initial Monitor release.
