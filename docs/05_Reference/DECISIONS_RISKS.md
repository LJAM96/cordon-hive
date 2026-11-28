# Decision register and outstanding risks

All ADRs below are proposed; confirm with maintainer and organisational owners before freezing implementation.

| ADR | Proposed decision | Rationale / tradeoff |
|---|---|---|
| ADR01 | Cordon Hive | Product name confirmed by the user |
| ADR02 | Single WinUI 3 app, separate service/broker | Native UX and privilege isolation; multiple process lifecycle complexity |
| ADR03 | PostgreSQL behind API | Existing infrastructure, robust transactional data; MySQL also viable but not supported initially |
| ADR04 | SignalR hints + durable sync | Fast updates with offline reliability; outbox and cursor complexity |
| ADR05 | HTTPS package distribution, optional SMB intake | Remote resilience and narrower endpoint access; storage and streaming service needed |
| ADR06 | Self-contained LocalJwt + device mTLS (AD/OIDC optional) | Works on standalone Linux + workgroup/AD workstations with zero IT AD changes; separates user JWT and machine mTLS trust |
| ADR07 | Tailscale deferred to P8, optional and policy-gated | Existing PostgreSQL host not on tailnet; P1–P7 LAN-only; P8 remote API reach without public RDP or direct client DB access; external control plane not airgapped |
| ADR08 | Overlay default, Explorer child experimental | Preserve Alert look with lower shell coupling; transparent WinUI spike required |
| ADR09 | Protected workloads fail closed | No forced app termination/reboot; mandatory updates may stay deferred |
| ADR10 | DFU break-glass encrypted escrow + ephemeral CredWriteW (P6), individual/LAPS optional | Supports urgent DFU evidential access when examiners are away (manual user/custodian updates, reason-audited plaintext reveal, RDP-as-user) via AES-256-GCM + separate Docker secret KEK (`/run/secrets/hive_vault_master_key`) |
| ADR11 | Linux server deployed via Docker Stack (no AD join) | Confirmed by maintainer; cross-platform `.NET 10` non-root containers (`deploy/docker/`) run standalone on LAN without AD domain join, SPN, or keytab |
| ADR12 | Local CI and phased rollout | Test on Windows and protect lab infrastructure; requires maintained test devices |

## Risk register

| Risk | Severity | Owner to confirm | Mitigation / gate |
|---|---|---|---|
| Privileged deployment supply chain compromise | Critical | Security/software lead | Independent signing, broker allowlist, approval, pilot, revocation; P5 |
| Disrupted acquisition/processing | Critical | Quality/operations | Guard, protected locks, installer validation, manual fallback; P5 |
| External access to isolated forensic environment | Critical | Network/security | Separate deployments, restrictive grants and firewall, approval; LAN isolation P0–P7, hybrid P8 |
| Saved credentials exposed or misused | High | Identity/security | Individual credentials or explicit P6 AES-GCM escrow, separate key, reason-audited custodian access |
| Optional remote AD unavailable | High | IT | Canonical hostname/SPN, DNS/routes, fail closed, device cert; P8 |
| Explorer docking incompatibility | Medium | Desktop engineering | Default overlay/tray, isolated experimental mode; P0/P4 |
| Staff privacy and case metadata leakage | High | Data protection/quality | Minimise collection, scopes, retention, safe toast; P2/P4 |
| Inventory false compliance | High | Software lead | Detection provenance, version semantics, unknown state; P3 |
| Offline notification falsely cleared | High | Engineering | Durable cache, idempotency, server completion; P4 |
| Update race/installer ignores no-reboot | Critical | Software lead | Final check, cooperative guard, manual-only where uncertain; P5 |
| Package storage/DB exhaustion | Medium | Infrastructure | Quotas, retention, staged cache, capacity tests; P7 |
| Linux container secret or TLS/JWT configuration drift | High | Infrastructure/Security | Non-root read-only containers, Docker secrets (`0400`), Production fail-closed DB/key checks, automated auth negative tests; P1/P7 |

## Questions requiring confirmation

* Final product name, repo destination, supported Windows client editions/builds and number of devices/sites.
* Linux server distribution and Docker Swarm vs Compose mode (standalone Linux + Docker Stack + LocalJwt confirmed), existing PostgreSQL version, storage capacity and internal PKI certificate issuance.
* P8 only: whether remote use is permitted at all, approved tailnet tenant, data-handling constraints and network segmentation.
* RDP host policy, session interruption tolerances, whether saved credentials or LAPS permitted.
* Actual forensic products/versions, installer types, vendor license terms, validation evidence workflow and reliable workload signals.
* Retention, privacy assessment, audit access and notification visibility rules.
* Whether to maintain Cordon Alert separately or later migrate its widget into shared library; ownership/licensing of source/assets.
* Target performance budgets, recovery objectives and local CI/test hardware.

## Explicit limitations

This documentation update performs no code, deployment, compile, DB migration or Windows tests. Existing implementation evidence is reported in PROGRESS.md; expansion remains proposed. Technical references support design decisions, not compatibility guarantees. Sample policies, schemas, names, metrics and frequencies require validation. No accreditation, information security or organisational approval is implied.

## Proposed expansion decisions and risks
| ADR | Decision | Rationale |
|---|---|---|
| ADR13 | Modular LAN-first expansion | Reuse existing application/service/API, preserve P7/P8 gates |
| ADR14 | Typed local wake relay | No public broadcast, arbitrary packets or inbound agent management |
| ADR15 | Dedicated signed component lifecycle | Independent signer, anti-replay, recovery, preserve protection |
| ADR16 | Observations separated from approved state | Unknown never compliant; historical observations not continuous proof |
| ADR17 | Sanitised diagnostic and adapter signals only | No raw event/case data or surveillance |
| ADR18 | Registered assets separate from observed hardware | Reconciliation and audit; exclude exhibits |

| Risk | Mitigation / gate |
|---|---|
| Sensor access/driver supply chain and telemetry load | Native first, licence and driver approval, V25/V37 |
| Wake crosses network boundary | Approved relay/interface/target, no bridge, V29 |
| Updater bricks agent or loses protection | Independent recovery and compatibility, V28 |
| Diagnostics/floorplans expose sensitive information | Preview, allowlist, scope, retention, V31/V32 |
| Wrong quality status or false forensic progress | Provenance, effective times, unknown state, adapter verification, V34/V35 |
| Benchmark disrupts acquisition | Synthetic scratch, explicit consent, shared guard, V36 |

Open decisions: approved sensor libraries/vendors, lab VLAN and power states, update key custody and offline revocation policy, OS baseline owners, asset verification/retention policy, permitted floor plans, support attachment scanning, vendor adapter availability/licences, benchmark profiles and performance budgets. No new production approvals implied.
