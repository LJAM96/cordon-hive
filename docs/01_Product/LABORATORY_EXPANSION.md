# Laboratory management expansion — proposed v0.3

This is a future backlog, not implemented functionality. P7 live LAN acceptance remains mandatory; P8 Tailscale remains optional and gated independently. All expansion packages can operate on the LAN, without internet or AD. No evidence acquisition, case content, generic remote execution or staff surveillance. Reuse existing WinUI 3 app, agent, restricted broker, LocalJwt/mTLS API, PostgreSQL, SQLite, SignalR and notifications.

| ID | Requirement | Acceptance / design reference |
|---|---|---|
| FR15 | Extended hardware inventory, live/historical CPU/GPU/VRAM, temperature, throttling, disk I/O/SMART/endurance, network and health warnings | Provenance and unknown states; bounded collection; Fleet Intelligence |
| FR16 | Windows edition/version/build/UBR, patch status, accepted version policies and exceptions | Correct numeric comparison, approved scope, independent OS update policy; Fleet Intelligence |
| FR17 | Windows Event Log health monitoring | Allowlisted provider/event rules, durable cursor, deduplication, sanitised structured events; Fleet Intelligence |
| FR18 | Wake on LAN, bulk/scheduled wake and wake then RDP | Enrolled target and approved relay/subnet, audited bounded attempts, fresh heartbeat confirmation; Fleet Intelligence |
| FR19 | Signed Hive agent/application/widget/broker updates | Independent signing, staged rollout, recovery, anti-replay, workload protection; Lifecycle and Software Centre |
| FR20 | Hardware and forensic workload benchmarking | Versioned synthetic workloads, opt-in protected scratch space, comparable results; Fleet Intelligence |
| FR21 | Hardware/asset management | Automatic hardware history plus manually registered equipment, QR references, warranty, maintenance, transfer, verification, retirement; Assets and Support |
| FR22 | Interactive laboratory floor plans | Site/building/floor/room editor, asset placement and live overlays, scoped read and edit; Assets and Support |
| FR23 | Technical support and equipment requests | Fault tickets, missing/damaged reports, software/equipment requests, diagnostic preview, assignment and audit; Assets and Support |
| FR24 | Internal software centre | Approved catalogue, historical versions, self service, requests, maintenance windows, local cache, history, release notes, rollback and rings; Lifecycle and Software Centre |
| FR25 | Advanced forensic software validation | Version/module/configuration approval, report links, review dates, integrity checks, historical compliance and exports; Lifecycle and Software Centre |
| FR26 | Forensic operation monitoring | Confirmed vs inferred jobs, progress/ETA where reliable, completion/failure/stall alerts, protected workloads; Forensic Operations |

## Roles and navigation

Keep one native application; navigation is capability-driven, not a security boundary. Examiner: own workstation, resources, software centre, support/my requests, notifications and permitted floor plan. Supervisor: scoped fleet, hardware health, operations, laboratory map, equipment allocation and support queue. Proposed capabilities (not existing role names): fleet policy administrator, asset custodian, support responder, quality approver, benchmark operator, wake operator, updater publisher/independent approver. Auditors read scoped history; no implicit credentials or execution rights. Server authorises every read/mutation and field-level export. Roles may overlap only where separation of duties permits.

## Experience

Overview highlights fleet freshness, health, Windows and software compliance, active/protected jobs, open tickets, and equipment faults. Workstation detail has hardware, OS, health timeline, benchmarks, software, support and operations. Software Centre has searchable cards, eligible versions, release notes, request/install/defer actions, progress and history. Quality Assurance has validation register and as-of date reports. Laboratory offers floor plan/list equivalents with zoom, keyboard navigation, selectable assets and overlays. Support provides guided issue/equipment forms, reviewed diagnostics and request timeline. Administration manages policies, update rings, signers and thresholds. Standard users cannot see hidden fleet data through direct API calls. Accessible text labels, high contrast, mixed DPI and stale/unknown states throughout.

## Explicit non-goals

No automatic Windows patch installation, forced reboot/shutdown, unattended benchmark under forensic load, general log collection, arbitrary scripting, evidence file access, automatic vendor licence bypass, or claim of accreditation. Floor plans and operational data are sensitive. Do not make expansion a prerequisite for existing P7 acceptance; individual feature flags default off until their own approval.

## Resilience requirements (proposed)
FR27: preserve local monitoring, protected workload enforcement and bounded offline queues during API/database outage; central mutations and new privileged work fail closed; show stale/pending state. FR28 (optional): centrally authorised peer/site package cache with independently verified bytes and central fallback; no peer authority. FR29 (optional): server-side availability and tested recovery against agreed RPO/RTO; no multi-master. See [Resilience and peer assistance](../02_Architecture/RESILIENCE_AND_PEER_ASSIST.md). V38–V41 verify these requirements. P7/P8 gates remain unchanged.
