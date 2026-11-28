# Data and persistence specification

PostgreSQL is authoritative; SQLite is local cache/queue only. Use UUIDs, UTC timestamptz, FK constraints, explicit enums/state constraints, optimistic concurrency versions and migrations. Example logical schema, not executed DDL.

| Entity | Key fields and invariants |
|---|---|
| devices | device_id PK, asset_number unique where assigned, hostname, site_id, cert_binding, enrollment_state, last_seen_utc, policy_revision |
| device_assignments | device_id, immutable Hive user subject, assigned_by, valid_from/to; at most one active primary assignment |
| device_sessions | device_id, boot_id, session_id, user_sid, session_state, observed_at; session ID alone not durable |
| device_interfaces | device_id, type, hostname/IP, observed_at; reachability hints only |
| device_metrics | device_id, boot_id, sample_sequence, sampled_at, received_at, cpu/memory/gpu; unique sample key; time partition if justified |
| device_volumes | device_id, volume_id, total/free bytes, type, observed_at; no evidence paths or filenames |
| device_process_snapshots | device_id, snapshot_id, sampled_at, image name, PID + start time, CPU/memory; short retention |
| software_products | product_id, name, publisher, detection_revision, applicability, protected process profile |
| software_versions | release_id, product_id, version_raw, version_scheme, approval_state, validation_ref, immutable manifest digest |
| software_installations | device_id, product/detection_id, scope, observed version, detection source, observed_at, confidence/state |
| software_exemptions | device/product scope, allowed version, reason, approver, starts/expires; revocation audit |
| packages | package_id, release_id, content hash, size, signer, immutable storage key; never raw user path |
| deployment_jobs | job_id, release_id, target snapshot, policy, creation/approval, state |
| deployment_targets | job_id + device_id unique, state, reason, lease expiry, attempt, version |
| deployment_attempts | attempt_id, target, manifest digest, execution timestamps, exit/reboot codes, verification, sanitised logs |
| maintenance_locks | device/session/source, lock_id, reason code, start, released_at, owner; stale protection fails safe |
| notifications | notification_id, source, target type, priority, completion rule, due, revision, content, status |
| notification_recipients | notification_id + typed recipient key, delivery/read/ack/complete timestamps and version |
| notification_deliveries | recipient key, device/session, delivery attempt, toast request outcome; not proof of reading |
| outbox_events | event_id, sequence/partition, topic, recipient scope, payload, created/dispatched timestamps |
| device_credential_escrow | credential_id PK, device_id unique FK, account_username, ciphertext_aes_gcm, nonce, tag, key_version, updated_by_sid, updated_at_utc; separate Docker secret KEK; never plaintext |
| audit_events | event_id, actor type/id, action, object, result, correlation, UTC timestamps, redacted before/after |

## Version semantics

Use product-specific detection and comparison (numeric segments, semantic versions, vendor-defined versions, exact matching); never lexicographic comparison for arbitrary strings. Preserve original value and comparison policy. Multiple installed versions, architecture, user scope and portable binaries are possible. Unknown/missing/older/ahead/withdrawn/exempt are distinct. Applicability and approval are independent of vendor latest release. Do not auto-mark incomplete detection as compliant.

## Queue and reconciliation

SQLite tables for machine state, inventory snapshot, pending outbound events, event cursor, deployment journal and protected cache indexes. Separate per-user encrypted/ACL-protected notification store; service cannot indiscriminately broadcast user-targeted data. Events use durable IDs, idempotency, bounded retry and cursor-based reconciliation. Persist before reporting success. Server stores desired state and agent reports observed state; neither overwrites the other. Reconcile a snapshot if cursor expired. Never replay expired installation lease after reconnect.

## Proposed retention and storage sizing

Raw telemetry 7 days, 1-minute aggregates 30 days, hourly aggregates 180 days; process snapshots 24 hours by default, audit/approval/deployment records per approved organisational policy. These are provisional, not legal retention requirements. At 10-second sampling, 100 devices produce 864,000 samples/day before aggregation. Measure row/index size and WAL overhead before setting storage allocation. Bound local disk usage, preserve pending critical transitions, coalesce routine metrics, alert on pressure. Delete/aggregate via policy jobs with audited changes; legal holds and backups require separate policy.

## Migration and recovery

Test forward migration and restoration on representative data. Back up before incompatible migrations. Keep application backward compatible during staged rollout; DB changes expand/contract. Restore DB and corresponding immutable package manifests/artifacts consistently; revoke stale leases after restoration. Audit exports exclude secrets and sensitive content by default.

## Expansion
See [Expansion contracts](EXPANSION_CONTRACTS.md) for proposed additive entities and data boundaries. Preserve current migrations and retention gates; distinguish LocalJwt user subject from local Windows session SID.

## Offline and scale refinement
See [Resilience and peer assistance](RESILIENCE_AND_PEER_ASSIST.md) for data ownership, event dedupe/acknowledgement, queue quotas, reconnect order and user conflict rules. Keep PostgreSQL authoritative, SQLite local only. No peer DB replication. Measure WAL, indexes, batches and reconnect bursts before partitioning or adding infrastructure; optional standby is server-side physical replication, not a second writer.
