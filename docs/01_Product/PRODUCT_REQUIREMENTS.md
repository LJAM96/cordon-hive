# Product requirements

## Purpose and scope

One native Windows application serves both supervisors and standard users. A separate service monitors each managed device irrespective of interactive login. Central administration uses existing AD identities and PostgreSQL. The initial platform supports LAN operation only (P1–P7). Optional organisational tailnet connectivity for approved remote devices is deferred to P8 after LAN acceptance; the existing PostgreSQL server is not currently on Tailscale. It is not a forensic acquisition tool, generic remote shell, EDR replacement or proof of ISO/IEC 17025 compliance.

## Personas

| Role | Scope |
|---|---|
| Examiner | Own workstation, software status, received notifications and protected work mode |
| Supervisor | Assigned fleet, allocation, resource views, announcements and permitted remote access requests |
| Software administrator | Catalogue, validation references, releases and deployment scheduling |
| Security administrator | Enrollment, identities, certificates, network policy and credential integration |
| Auditor | Read only scoped audit and compliance records |

Users may hold several roles. Role and asset scope are checked by the server. Supervisor status alone does not confer installer publication, credential access or RDP rights.

## Requirements and acceptance

| ID | Requirement | Acceptance |
|---|---|---|
| FR01 | Single WinUI app, integrated Windows authentication | Same package shows different navigation for different authorised AD users; direct API calls outside scope denied |
| FR02 | Asset registration and allocation | Stable device UUID survives IP, name and user changes; duplicate asset labels are flagged; assigned and actual user distinct |
| FR03 | Continuous monitoring | Service reports CPU, RAM, disk utilisation, optional GPU and running processes while GUI is closed or no session exists |
| FR04 | Software inventory | MSI/uninstall entries, MSIX and configured portable detection with provenance; unknown is not compliant |
| FR05 | Approved version management | Administrator records product, target version, validation evidence, package hash, applicability and deployment policy |
| FR06 | Non disruptive deployment | Protected workload or unknown safety state blocks installation; no automatic process termination or reboot |
| FR07 | Notifications | Target user, device or group; native toast plus persistent taskbar indicator; server tracks acknowledgement and completion separately |
| FR08 | Remote access | Launch native RDP over permitted LAN route in P6; tailnet route only in P8; distinct authorisation and session warning; no public RDP |
| FR09 | Optional saved RDP credentials | Per supervisor OS credential storage or approved vault/LAPS workflow; never cleartext in application DB |
| FR10 | Offline behaviour | Local telemetry and cached pending actions remain usable; reconnect reconciles missed messages without duplication |
| FR11 | Audit | Attributable administrative actions and device outcomes with timestamps, correlation IDs and before/after state where relevant |
| FR12 | Hybrid connectivity (deferred P8) | Remote agent reconnects without inbound agent port or internet exposure of management services; explicit policy controls eligibility |
| FR13 | Shared notification surface | Cordon Alert taskbar behaviour reusable; future suite publishers cannot acknowledge or resolve another publisher's required action |
| FR14 | Software exemptions | Time bounded scoped exceptions with reason, approver and expiry; visible in compliance reporting |

## Nonfunctional requirements

* No mandatory cloud dependencies for LAN-only deployments; Tailscale mode has external dependencies and is not airgapped.
* Avoid collection of case data, file names, command lines, window titles, screenshots and keystrokes by default. Process names and usernames remain sensitive operational metadata.
* Desktop remains responsive while background inventory runs; telemetry is bounded, low priority and suspendable under pressure.
* All privileged actions fail closed on ambiguous identity, integrity, policy or safety state.
* Retain enough audit evidence to reconstruct decisions; retention, staff visibility and lawful use require organisational approval.
* Support accessible keyboard navigation, high contrast, scaling and reduced motion.

## Explicit non-goals for first release

Remote shell, forced logoff, forced reboot, unattended deployment of unvalidated vendor releases, arbitrary command execution, remote evidence access, screen surveillance and public internet ingress. Tailscale is a deferred P8 deployment option, not embedded bespoke VPN code. Cross-suite notification federation is a later expansion.
