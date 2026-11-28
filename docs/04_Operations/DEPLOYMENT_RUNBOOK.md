# Deployment, operations and recovery runbook

Proposed procedure; not executed. Commands and infrastructure specifics must be written against approved environment during P0/P1. Do not deploy to live casework endpoints until gates are passed.

## Prerequisites

* **Linux server host**: Standalone Linux OS with Docker Engine (Swarm mode for `docker stack deploy` or Docker Compose), non-root container UID mapping, TLS server certificate/key + internal PKI CA bundle, private API hostname, existing LAN PostgreSQL instance with separate DB and restricted account, controlled artifact host volume/storage, backup destination. No Active Directory domain join, SPN, or keytab is required for `LocalJwt` mode.
* **Windows managed endpoints**: Supported Windows 10/11 builds (domain-joined or workgroup), signed endpoint packages (`Hive.Agent`, `Hive.Installer`, `Hive.App`), and host firewall policies.
* Verify storage/CPU capacity, NTP time sync, and outbound HTTPS. Separate development, test, pilot and production. P1–P7 use the existing LAN PostgreSQL instance, which is not on Tailscale. Optional tailnet tenant is a P8 prerequisite only, after LAN acceptance and IT approval; the Linux Docker host may join the tailnet while the database remains private.

## Deployment sequence

* Provision TLS server certificate/key, internal CA bundle, DB role/schema, JWT signing secret, and immutable artifact host directory with non-root permissions (`deploy/docker/`).
* Create Docker secrets (`hive_db_connection_string`, `hive_jwt_signing_key`, `hive_tls_cert`, `hive_tls_key`, and initial `hive_bootstrap_admin_password`) outside Git and outside container images. Verify network/firewall rules prevent workstations from connecting directly to PostgreSQL (`5432`).
* Build/pull pinned `Hive.Server` Linux container image and deploy the stack (`docker stack deploy -c deploy/docker/docker-stack.yml cordon-hive`), mounting read-only configs, secrets, and the artifact volume.
* Apply versioned PostgreSQL migrations (`0001_InitialFoundation.sql`..`0003_LocalAuthAccounts.sql`, etc.). Verify `/health`, `LocalJwt` login (`POST /api/v1/auth/login`), immediate bootstrap password change, mTLS device auth, and confirm unauthenticated and unauthorised requests fail closed.
* Distribute signed agent/broker installer to test devices; broker disabled initially. Enroll certificate via approved one-time token, allocate asset and test revoke/re-enroll.
* Install same signed WinUI app per device, configure per-user startup and toast activation. Confirm role routing and widget persists after main window closes.
* Exercise LAN only, then isolated profile separately. Only in the separate approved P8 hybrid pilot, apply restricted tailnet policy, unattended mode, split DNS/AD connectivity and firewall checks. Verify negative routes and post-reboot agent access.
* Enable read-only compliance, notification delivery and pilot metrics. Verify baseline before enabling any install or RDP action. Enable approved releases gradually with target cap and failure stop criteria.

## Operational controls

Daily service/DB/queue health, certificate expiry, failed enrollments, missing agents, stale notifications and package integrity. Weekly capacity and compliance trend review. Monthly role/credential access review and documented recovery rehearsal frequency per policy. Monitor application version compatibility. No secrets or case information in logs. Central audit export to protected store. Document personnel access and retention.

## Backups and recovery

Initial proposed RPO 24h and RTO 4h, to be agreed. Encrypted PostgreSQL backups (PITR if configured), immutable artifacts/manifests, configuration and audit export; protect private signing keys separately and test restoration. Restore DB with matching artifact generation; verify signatures, certificate revocation and current policy. Invalidate outstanding deployment leases before reopening. Reconcile device state, notification cursors and uncertain installs. Avoid replaying prior privileged commands after restore. Drill in test environment.

## Incident and emergency stop

If suspected compromise or unsafe install: disable new deployment leases, revoke affected signer/device credentials, pause rollout, isolate management plane using approved IT process, preserve logs and evidence, inform security/quality leads. Do not issue blanket kill/reboot. Running installer requires specific safe recovery decision; agent preserves journal. Notify staff through approved channel. Re-enable only after documented investigation, key rotation and validation. P8 tailnet emergency: revoke node/route/grant; retain LAN operation where authorised. RDP emergency: revoke actual host/group/firewall rights, not only GUI button.

## Endpoint removal

Revoke device certificate/tailnet access, cancel pending jobs, record decommission and retain approved audit. Uninstall service/broker and GUI through approved maintenance; remove startup registration and protected caches according to retention policy. Do not touch evidence or unrelated installed software. Reassigned asset gets new assignment record; reimaged device re-enrolls with identity history retained.

## Expansion rollout
After P7 acceptance, enable each expansion capability on non-evidential pilot workstations only after its V25–V37 gates and approval. Configure site-specific permissions, retention, allowed collectors/relays, package trust and feature flags; all new features disabled by default. Back up DB/artifacts before migrations, verify restore and mixed client compatibility. Disable feature on errors, revoke pending leases/requests, preserve audit and local protection, never force reboot or terminate forensic work. P8 remains independently gated.
