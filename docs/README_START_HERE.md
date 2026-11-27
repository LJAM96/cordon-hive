# Documentation index

Status: proposed baseline v0.2 (updated for Linux Docker Stack server hosting). Initial release is LAN-only; `Hive.Server` runs on a Linux host via a Docker Stack against the existing LAN PostgreSQL server, and Tailscale is deferred to P8 after P7 LAN acceptance. The existing PostgreSQL server is not on the tailnet. P1–P7 require no Tailscale installation, account or connectivity. Owner: project maintainer. Review required from IT, information security, laboratory quality and operational leads before production deployment.

| Document | Purpose |
|---|---|
| [Product requirements](01_Product/PRODUCT_REQUIREMENTS.md) | Scope, roles, functional requirements and acceptance criteria |
| [UX specification](01_Product/UX_SPECIFICATION.md) | Native WinUI screens and persistent widget behaviour |
| [System architecture](02_Architecture/SYSTEM_ARCHITECTURE.md) | Components, runtime boundaries and deployment topology |
| [Identity and security](02_Architecture/IDENTITY_SECURITY.md) | AD, device trust, RDP credentials, permissions, threat model |
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
