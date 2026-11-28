# Expansion logical schema and API contracts (proposed, not DDL)

Reuse existing repositories, domain state machines and DTOs; assign migrations after inspecting latest migration sequence at implementation. PostgreSQL authoritative; SQLite bounded machine queue only, per-user protected drafts/cache separate. UTC, UUID, site scope, version/If-Match, FK/check constraints, append-only history and transactional outbox. User subject is LocalJwt immutable ID (Windows SID only for local session mapping); agent identity derived from mTLS. No new DB credentials or inbound agent listener. Existing API prefix /api/v1; routes below proposals subject to collision review. User mutations use idempotency and fresh permissions, agents use certificate and sequence. Bounded pagination, ProblemDetails, explicit unknown/stale values.

| Logical tables (additive) | Key data / constraints |
|---|---|
| hardware_observations, hardware_changes | device, component identity, source/confidence, observed UTC, typed fields; unique device/sequence; no evidence media |
| os_observations, os_policies, os_policy_revisions, os_exceptions | device/build/UBR/edition/KB observation, scope and effective interval, approval, reason/expiry; deterministic precedence |
| health_events, health_rules, metric_aggregates | device/provider/channel generation/record id unique, safe fields, time window, rule revision, units |
| wake_capabilities, wake_requests, wake_attempts | target MAC/interface/subnet restricted, relay enrollment, permit/expiry, requester, status, dedup and rate limit |
| component_releases, update_rings, update_attempts | component/version/sequence/digest, independent approval, compatibility, target, recovery journal; reuse packages/deployments where possible |
| benchmark_profiles, benchmark_runs, benchmark_results | immutable profile digest/version, device, lease, synthetic dataset hash, environment, units, valid/invalid |
| assets, asset_assignments, asset_events | unique asset number, device optional, location, category, condition, serial provenance, history, one active primary assignment |
| locations, floorplans, floorplan_revisions, asset_placements | hierarchy, scoped background blob, normalized coordinates, row version; unique active placement per asset |
| support_tickets, ticket_comments, ticket_events, ticket_attachments, equipment_requests | requester, scope, type/status, assigned user, asset optional, approval and fulfilment; safe blob references |
| software_requests, maintenance_windows, catalogue_metadata | eligible release refs, requester, window UTC, approval; use existing deployment targets, packages and release tables |
| validation_records, validation_revisions, configuration_profiles, integrity_observations | product/release/module refs, scope, effective/recorded timestamps, report digest, approver, review dates, expected/observed digest |
| operation_observations, operation_states, adapter_registry | opaque operation ID/device/sequence unique, source/confidence, nullable progress/ETA, protection reference, capability revision |

| Proposed route family | Actor / behavior |
|---|---|
| PUT /agent/hardware; PUT /agent/os; POST /agent/health-events | Device snapshot/events, provenance/dedupe, no caller-specified device identity |
| GET /workstations/{id}/hardware, /os, /health, /history | Scoped users, bounded time windows and freshness |
| /os-policies, /health-rules | Policy admin draft/revise, preview, activate, audit |
| POST /workstations/{id}/wake; POST /wake-requests/bulk; GET /wake-requests/{id} | Wake-authorised user, preview and bounded target list, reason/expiry |
| POST /agent/wake/{id}/attempt | Approved relay only, permit validation, state report; heartbeat confirms target |
| /component-releases, /update-rings; /agent/component-updates/{id}/events | Publisher/independent approver/device; reuse typed lease and package delivery |
| /benchmark-profiles; POST /workstations/{id}/benchmark-requests; POST /agent/benchmarks/{id}/events | Approved profile/consent/lease, local safety, bounded results |
| /assets, /assets/{id}/events, /locations, /floorplans | Custodian/editor/scoped reads, transfer and revision conflict |
| /support/tickets, /support/tickets/{id}/comments, /attachments, /equipment-requests | Own reporter or assigned support scope; approval/fulfilment and safe uploads |
| GET /software/catalogue; POST /software/requests; POST /software/self-service-install | Eligibility check, request or existing deployment job |
| /quality/validations, /quality/configurations, /quality/compliance-history, /quality/export | Quality approver/auditor scoped, as-of provenance and export audit |
| POST /agent/operations; GET /operations | Device typed status; scoped users; no case payload |
 
Route families shorthand, not deployed endpoints. SignalR sends existing scoped StateChanged hints (hardware.changed, health.changed, ticket.changed, asset.changed, floorplan.changed, validation.changed, operation.changed, wake.changed, benchmark.changed, component-update.changed) then clients fetch durable state. Notifications use source-qualified ID, dedup, appropriate completion semantics. IPC extensions typed and version negotiated: GetLocalHardware/Health/OS, RequestDiagnosticPreview, SetBenchmarkConsent, PublishOperationStatus via separate registered publisher boundary; all names proposed, pipe ACL/impersonation and 64 KiB cap retained. Privileged operations never accept paths, arguments, raw packets or user claims. Typed local broker permit includes target/profile/digest/lease only. No sensitive fields in audit/outbox or error messages. Define retention and export policies before enabling, test expand/contract migrations on real PostgreSQL, backup/restore and backward compatibility for mixed versions.
