# Engineering roadmap and release gates

Implement vertical slices with tests and documents in each phase. No dates assumed; gates determine advancement. Work packages are proposed backlog, not existing code.

| Phase | Deliverables | Exit gate |
|---|---|---|
| P0 Discovery | IT/quality requirements, Identity/PKI/topology decisions, supported Windows builds, threat model, lab test environment; WinUI/Alert overlay spike | Approvals to prototype, no production access; decisions recorded |
| P1 Foundation | Solution, local CI, Linux Docker Stack (`deploy/docker/`), migrations, API, LocalJwt roles (optional Negotiate), enrollment, machine service, WinUI shell, heartbeat, scoped assets | Auth negative tests, LocalJwt/mTLS on Linux container verified, device revocation, no direct DB access, GUI roles verified |
| P2 Visibility | Telemetry, session inventory, device assignment, process/resource views, stale states, local user resource UI | Performance budget measured, non-disruptive baseline, privacy review |
| P3 Software compliance | Registry/MSIX/portable discovery, catalogue, approved releases, exemptions, validation evidence, read-only reporting | No repairs, correct version semantics, unknown not compliant |
| P4 Notifications | Durable outbox, sync, per-user cache, admin compose, toast, taskbar overlay, tray fallback, acknowledgement/completion | Offline/restart/reconnect/multi-session tests; no lost or leaked actions |
| P5 Safe deployment | Protected locks, package intake/signing, HTTPS cache, lease, broker, MSI pilot, verification, audit | Independent signer, tamper tests, workload guard, interruption and reboot tests; formal approval before real installs |
| P6 LAN RDP | Native RDP over approved LAN routes, individual credentials and scoped break-glass escrow, optional LAPS | RDP authorisation, session protection, LAN firewall, revocation, no public ingress |
| P7 Hardening | Recovery drills, pilot rings, validation records, signed packaging, monitoring, support runbook, operational acceptance | Restore test, security review, quality and IT sign-off, rollback/disable plan |
| P8 Deferred remote connectivity | Optional Tailscale pilot, approved API reachability, split DNS and optional domain connectivity, remote RDP, metered downloads | P7 accepted; IT approval; remote API-to-PostgreSQL path verified; tailnet grants, revocation and off-site LocalJwt tests (AD only if configured); no public ingress or client DB access |
| P9 Optional | Shared Cordon publisher adapters, remote assistance, richer history, experimental taskbar docking | Separate design and validation; no required dependency for MVP |

## Work sequencing

P0 includes early widget and LAN identity feasibility checks only; document the future remote boundary without provisioning Tailscale. P1 uses configurable canonical API endpoints and stable device/notification IDs; no Tailscale SDK, client, account, routes or tailnet-specific work is required. P2 and P3 can be parallel after identity/device model stable. P4 notification source model supports deployment status before P5. P5 broker is separately testable; never merge active privileged execution without negative tests. P6 is LAN RDP only. P7 completes staged LAN operational rollout independently of Tailscale. P8 starts only after P7 acceptance and infrastructure/security approval; pilot off-site read-only telemetry first, then notifications, package downloads and RDP as separately approved. The existing PostgreSQL server is not on Tailscale; prefer a LAN API host that can reach PostgreSQL and is later given an approved tailnet-facing endpoint. The database itself need not join the tailnet. If the API is hosted elsewhere, establish and verify an approved private backend route before the pilot. P9 optional extensions are independently scheduled.

## Definition of done for each work package

Requirements and traceability updated; code reviewed; unit/integration/negative tests pass; local Windows CI evidence attached; dependencies/licences and SBOM reviewed; migrations reversible via documented restore; failure behaviour tested; no secrets/test personal data; docs updated; versioned release signed. Security or safety regressions block release. Protect existing forensic operations by pilot on non-evidential workstations.

## MVP boundary

First usable release P1–P4: monitoring, read-only software compliance, announcements, persistent widget. Controlled installation and remote access are separately gated additions. Deployment can initially be notify-only. No Tailscale or off-site connectivity in P1–P7; no mandatory hybrid connectivity, general-purpose shared password vault, arbitrary commands or cross-suite federation.

## LAN-first expansion work packages (proposed, not started)

P7 live acceptance remains the immediate gate. P8 remains separately approved and optional; the expansion does not require P8 or renumber existing phases. Design/prototype may be done on synthetic test systems, but no live rollout before P7 and each package's own gate. Existing P9 optional items remain separate.

| Package | Requirements / dependencies | Exit gate |
|---|---|---|
| E1 Fleet foundations | FR15–17; P7 baseline, hardware/OS inventory, event health, retention | V25–V27, measured collector overhead and privacy |
| E2 Signed lifecycle | FR19; existing P5 signing, P7 recovery | V28, trusted update/recovery and mixed versions |
| E3 Power | FR18; E1 network capability, existing RDP checks | V29, subnet/relay negative tests |
| E4 Assets | FR21; E1 observations | V30, reconciliation and lifecycle |
| E5 Service desk | FR23; E4 for equipment fulfilment | V31, scope, diagnostics and allocation |
| E6 Laboratory mapping | FR22; E4 locations/assets | V32, map editor/accessibility/scopes |
| E7 Software centre | FR24; P3/P5, E2 lifecycle patterns, E5 request workflow | V33, approved self service and rollback |
| E8 Quality assurance | FR25; P3, E1 OS, E7 eligibility | V34, historical accuracy, independent approval |
| E9 Forensic operations | FR26; P2/P4/P5 guards, E1 health | V35, adapter capability matrix and fail-closed protection |
| E10 Benchmarks | FR20; E1 inventory, E2 signed profiles, E9 protection integration | V36, repeatability, no evidence writes/interruptions |

E1/E2 may proceed in parallel; E3 and E4 after E1; E5/E6 after E4; E7 and E8 coordinate policy contracts; E9 can begin with manual and first-party adapter spikes independently; E10 requires safety integration. Each vertical slice includes domain tests, real PostgreSQL integration, Windows tests, UI acceptance, migration/restore, dependency review and local CI; feature flags off until approval. Shared verification V37 covers integration and operational load. No feature deemed implemented by documentation.
