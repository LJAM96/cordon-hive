# Documentation index

Status: v0.2 implementation baseline plus proposed v0.3 laboratory expansion. Initial release is LAN-only; `Hive.Server` runs on a Linux host via a Docker Stack against the existing LAN PostgreSQL server, and Tailscale is deferred to P8 after P7 LAN acceptance. The existing PostgreSQL server is not on the tailnet. P1–P7 require no Tailscale installation, account or connectivity. Owner: project maintainer. Review required from IT, information security, laboratory quality and operational leads before production deployment.

| Document | Purpose |
|---|---|
| [Product requirements](01_Product/PRODUCT_REQUIREMENTS.md) | Scope, roles, functional requirements and acceptance criteria |
| [UX specification](01_Product/UX_SPECIFICATION.md) | Native WinUI screens and persistent widget behaviour |
| [System architecture](02_Architecture/SYSTEM_ARCHITECTURE.md) | Components, runtime boundaries and deployment topology |
| [Identity and security](02_Architecture/IDENTITY_SECURITY.md) | LocalJwt, optional AD, device trust, RDP credentials, permissions, threat model |
| [Networking and Tailscale](02_Architecture/NETWORKING_TAILSCALE.md) | LAN baseline and deferred P8 hybrid design |
| [Data model](02_Architecture/DATA_MODEL.md) | PostgreSQL, SQLite, persistence, retention |
| [API and IPC contracts](02_Architecture/API_IPC_CONTRACTS.md) | HTTP, SignalR, named pipe and message semantics |
| [Agent and telemetry](03_Implementation/AGENT_TELEMETRY.md) | Collection, inventory, protection and resource budgets |
| [Software management](03_Implementation/SOFTWARE_MANAGEMENT.md) | Validation, release policies and controlled execution |
| [Notification system](03_Implementation/NOTIFICATIONS.md) | Durable delivery, completion and multi-session behaviour |
| [Cordon Alert integration](03_Implementation/CORDON_ALERT_WIDGET.md) | Reviewed code, extraction and WinUI port design |
| [Remote access](03_Implementation/REMOTE_ACCESS.md) | RDP workflow, credential vault options and interruption controls |
| [Engineering roadmap](03_Implementation/ROADMAP.md) | Work packages, dependencies, release gates |
| [Deployment and recovery](04_Operations/DEPLOYMENT_RUNBOOK.md) | Configuration, rollout, backup and incident handling |
| [Verification plan](04_Operations/VERIFICATION_PLAN.md) | Test matrix, operational validation and acceptance |
| [Decisions and risks](05_Reference/DECISIONS_RISKS.md) | Proposed ADRs, open questions and risk register |
| [Sources](05_Reference/SOURCE_REFERENCES.md) | Source revision and technical references |

## Reading order

Product requirements → architecture → identity and networking → agent and notifications → software management → roadmap and verification. Treat the security and workload gates as mandatory prerequisites, not optional polish.

## Terminology

MUST denotes a required design invariant; SHOULD denotes a preferred implementation unless justified; MAY denotes optional capability. Numeric thresholds are initial engineering targets, not measured results. All examples use fictitious assets and versions.

## Expansion baseline v0.3 (proposed)
 
| Document | Purpose |
|---|---|
| [Laboratory expansion requirements](01_Product/LABORATORY_EXPANSION.md) | FR15–FR26 and role/UX scope |
| [Fleet intelligence](03_Implementation/FLEET_INTELLIGENCE.md) | Hardware, Windows, Event Logs, Wake on LAN, benchmarks |
| [Assets, support and laboratory](03_Implementation/ASSETS_SUPPORT_LABORATORY.md) | Equipment, tickets, requests, QR and floor plans |
| [Lifecycle, software centre and quality](03_Implementation/LIFECYCLE_SOFTWARE_CENTRE_QUALITY.md) | Signed updates, catalogue and validation |
| [Forensic operations](03_Implementation/FORENSIC_OPERATIONS.md) | Adapter capability and workload protection |
| [Expansion contracts](02_Architecture/EXPANSION_CONTRACTS.md) | Proposed persistence, REST, IPC and events |

Read the expansion requirements and specifications with the existing architecture and roadmap. These are unimplemented proposals; do not treat the existing P7 gate as satisfied or introduce Tailscale before P8.
