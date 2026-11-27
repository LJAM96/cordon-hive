# Verification and operational acceptance

Status: proposed tests, no execution results. Use non-evidential test workloads, synthetic installers and controlled test accounts. Record environment, build/commit, steps, expected and actual results, logs, assessor and date. Failed or untested mandatory gate blocks production use.

| ID | Requirement | Test / expected outcome |
|---|---|---|
| V01 | FR01 | Standard/supervisor accounts use same binary; direct forbidden API calls denied, scope enforced |
| V02 | FR02 | Rename, DHCP change, asset reassignment; stable device ID and assignment history, duplicate rejected |
| V03 | FR03 | GUI closed/no login; telemetry continues; offline/stale not zero; unsupported counters unknown |
| V04 | FR04 | Synthetic MSI/MSIX/portable/multi-version products, 32/64bit/user scope; no repairs or unintended writes |
| V05 | FR05 | Publish draft, approval, tampered artifact, invalid signer, changed profile; only signed immutable approved release eligible |
| V06 | FR06 | Target app open, worker/service running, manual protection, stale guard, low CPU acquisition; all defer |
| V07 | FR06 | Process starts after initial check; final guard blocks; validate remaining third-party race or manual-only |
| V08 | FR06 | Installer attempts shutdown/reboot; validated profile must prevent, otherwise not approved for automation |
| V09 | FR06 | Crash midway, exit code success but wrong version, pending reboot; unknown/failed/pending, no blind retry |
| V10 | FR07 | Notification delivered, native toast suppressed, collapsed widget still pending; acknowledgement not update completion |
| V11 | FR07/10 | Server outage, offline acknowledgement, duplicate SignalR hints, expired cursor, restart; correct reconciliation |
| V12 | FR07/13 | User switch, RDP multiple sessions, no login, source spoof; no cross-user or cross-publisher data leak |
| V13 | FR08/09 | RDP active/protected/stale device, denied AD group, NLA/certificate failure, saved credentials disallowed; no bypass |
| V14 | FR12 (P8 only) | LAN to remote tailnet, DERP/metered path, DNS failure, domain controller unavailable; device sync continues, user auth fails closed |
| V15 | FR12 (P8 only) | Tailnet grants and LAN firewall negative tests, public scan, DB unreachable, revoked node/agent; deny |
| V16 | FR10 | Interrupted installer download, wrong ETag, corrupted bytes, exhausted space; resume only valid or restart, no execution |
| V17 | FR11 | Audit actor, target, timestamp, scope and correlation; no secrets; unauthorised deletion denied |
| V18 | FR14 | Exemption expiry/withdrawal, conflict and multiple versions; accurate compliance and safe reassessment |
| V19 | NFR | Named pipe spoof/remote client, malformed/oversized messages, unauthorized installer request; denied and bounded |
| V20 | NFR | Certificate expiry/revoke, clock skew, stale lease, manifest replay; fail closed |
| V21 | NFR | Widget Explorer restart, taskbar auto-hide, mixed DPI, negative monitor coords, fullscreen, theme, accessibility; fallback works |
| V22 | NFR | Performance benchmark idle and representative imaging/hashing workload; agreed overhead thresholds met |
| V23 | NFR | Restore database/artifacts, expired leases, stale events; no duplicate privileged execution; RPO/RTO measured |
| V24 | NFR | App/agent version mismatch, upgrade/downgrade, interrupted service update; safe compatible behaviour and recovery |

## Pilot metrics

Measure CPU/RAM, throughput delta, network volume, API latency, event delivery/reconciliation time, resource queue limits, compliance detection accuracy and widget idle resource use. Initial targets: toast submission within 5 seconds of persisted event on healthy LAN (not guarantee Windows displays it), reconciliation within 60 seconds after normal reconnect, online dashboard age <30 seconds, zero observed unexpected application termination or reboots. These are test targets, not measured performance. Set approved workload-specific overhead and sample sizes with quality lead.

## Release gates

P1 identity and enrollment; P2 performance/privacy; P3 inventory integrity; P4 notification durability and UI accessibility; P5 privileged security and safe install; P6 LAN RDP and session controls; P7 LAN recovery and operational sign-off; P8 optional remote connectivity. V14/V15 do not block LAN release. Verify equivalent LAN firewall, API/database isolation and device revocation during P1/P6/P7 as applicable. P8 adds end-to-end API-to-database reachability without direct tailnet database exposure, off-site DNS/Kerberos and reconnect tests. Retain risk exceptions explicitly, with owners and expiry; no waiver of protected-workload invariant by ordinary supervisor. Any unverified third-party installer remains manual/notify only.
