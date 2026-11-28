# Windows agent and telemetry

## Service lifecycle

Install through a signed managed installer; run automatically under a restricted service identity. Broker has separate narrowly scoped privileged identity. Agent enrolls, loads verified policy, starts bounded collectors, publishes heartbeat and reconciles durable jobs. Shutdown flushes critical events within a bounded period. Service recovery uses backoff; repeated failures alert IT rather than creating restart loops. User GUI launches at interactive logon under the user's own identity. No session 0 UI.

## Collectors

| Signal | Method | Initial frequency |
|---|---|---|
| CPU total | Windows performance counters/system times | 2 seconds locally, 10 seconds centrally |
| RAM | GlobalMemoryStatusEx / counters | Same |
| GPU | GPU engine/adapter counters where supported | 10 seconds; unsupported is unknown |
| Disk activity | Performance counters per physical disk | 10 seconds, optional |
| Volume capacity | Windows volume APIs | 60 seconds |
| Process inventory | Process enumeration, sampled CPU delta, working set, PID/start time | 30 seconds, immediate targeted safety recheck |
| Sessions | WTS APIs/session change events | Event + reconciliation |
| Installed software | Registry 32/64 bit machine and authorised user scope, MSIX and portable rules | Startup, 15 minutes, post-install and change hints |
| Hardware | Targeted CIM/WMI and Windows APIs | Startup, daily |
| Heartbeat | Agent version, boot ID, policy revision, capabilities | 15 seconds |

Do not query Win32_Product for routine inventory due to possible MSI reconfiguration. Do not recursively scan evidence drives or all user directories. Portable software detection uses approved application locations, signed executable metadata or product-specific rules. Avoid process command lines, handles, paths and window titles in central reports. Privileged safety checks may inspect minimal locally needed metadata without uploading it.

## Safety and workload guard

Protected mode is a local enforcement mechanism independent of server connection. Support manual locks, trusted Cordon app leases and approved process/service detection rules. Lock ownership, reason code and session scope explicit; any active lock blocks disruptive maintenance. Unknown/stale protection status blocks deployment. For an app that cannot signal its work state reliably, require user confirmation and validated maintenance window. Idle CPU is never a safety signal. A process can start between check and installer execution: repeat checks immediately before start, acquire exclusive per-device maintenance mutex, and where supported obtain cooperating app maintenance handshake. Do not claim atomic prevention for third-party applications. Require controlled launch restrictions or explicit maintenance agreement if needed to eliminate race; otherwise manual-only.

## Resource budgets and reporting

Initial test targets: average agent CPU <1% on representative idle workstation, agent private working set <150 MiB, local UI/widget idle CPU near zero; numbers are provisional and measured in Phase 2. Cap queue, batch metrics, coalesce when offline, throttle disk I/O, use cancellable background collectors, backoff on resource pressure. Keep safety probes independent of routine telemetry throttling. No intensive WMI per tick. CPU measurements normalised across logical CPUs and explain semantics. Track collection errors, timestamps and unsupported metrics. Device state online when heartbeat <45s, stale 45–120s, offline after 120s (proposed configurable values). Do not equate offline with powered off.

## Acceptance

Benchmark with agent disabled/enabled across idle, hashing, imaging, video processing and low-space workloads using representative non-evidential test data. Measure throughput, CPU, memory, disk and network. No evidence writes, no application repairs, no unbounded collection. Test service crash, GUI close, sleep, boot, RDP sessions, time drift, missing counters, denied process access and network failure.

## Expansion
See [Fleet intelligence](FLEET_INTELLIGENCE.md) and [Forensic operations](FORENSIC_OPERATIONS.md). Existing collector budgets, local protection, unknown/stale semantics and evidence minimisation remain mandatory.

## Outage behaviour
Local collectors, protection and bounded diagnostics continue independently of the API. Preserve critical journal transitions, coalesce routine metrics with gap counters, backoff and reconcile with durable server acknowledgements. Never start new privileged activity on stale policy. See [Resilience and peer assistance](../02_Architecture/RESILIENCE_AND_PEER_ASSIST.md).
