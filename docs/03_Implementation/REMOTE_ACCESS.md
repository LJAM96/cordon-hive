# Remote access and credential handling

## Release scope

P6 implements native RDP over approved LAN routes only. Off-site RDP over Tailscale is deferred to P8 after LAN acceptance and separate network/security approval; it does not block P6.

## RDP launch workflow

Supervisor selects managed asset. API checks current AD role, asset scope and policy; returns approved hostname and advisory state (last seen, sessions, protection locks, route). Client confirms target and warns that RDP may disconnect or disrupt an interactive session. If protected or state unknown, default deny launch through Hive; exceptions require explicitly authorised process outside MVP and audit. Launch native mstsc using validated hostname or controlled `.rdp` settings without password or command injection. Require NLA, TLS certificate validation, authorised Windows login and host firewall; disable redirection of clipboard/drives/printers by default unless IT approves. Windows OS/edition and RDP configuration must support hosting; no automatic enablement by Hive. Prefer separate approved remote assistance for collaborative support.

## Credential options & DFU break-glass escrow

| Option | Decision |
|---|---|
| Supervisor's individual account | Default when connecting under custodian's own session |
| Windows Credential Manager | Optional per-supervisor/per-target saved credentials; OS policies may prevent use |
| Windows LAPS | Preferred managed local admin recovery if organisation supports it; directory-authorised access and audit |
| DFU break-glass workstation escrow (P6) | **Approved operational requirement** for urgent evidential extractions when an examiner is off-shift: AES-256-GCM encrypted per-workstation credential in PostgreSQL, keyed by an independent Docker secret (`/run/secrets/hive_vault_master_key`), strictly write-only for users and reason-audited for authorised break-glass custodians |

### Operational workflows (P6)

* **A. Manual user & custodian updates:** During onboarding or later in `Hive.App` (`Settings`), an assigned user can explicitly submit or update their workstation login username and password (write-only over TLS into encrypted escrow; never silently captured from Windows logon). Authorised break-glass custodians can also update or rotate an escrowed credential manually from the admin portal.
* **B. Physical sign-in plaintext reveal:** When physical access to a locked workstation is required, an authorised custodian enters a mandatory operational reason (e.g. case/extraction reference). The server verifies role/scope, writes an immutable `AuditEvent` (actor, target device, UTC timestamp, correlation ID, reason — never the password), and returns the decrypted credential with `Cache-Control: no-store`. `Hive.App` presents a masked click-to-reveal dialog with a 60-second auto-clear timer.
* **C. Assisted RDP as the actual user:** When a custodian requests assisted RDP as the escrowed workstation user, the server checks role/scope and protected-workload locks, audits checkout, and returns the credential over TLS with `no-store`. `Hive.App` injects a temporary session-scoped `TERMSRV/<hostname>` entry via Win32 `CredWriteW`, launches native `mstsc.exe /v:<hostname>` without embedding passwords in `.rdp` files or CLI arguments, and deletes the temporary entry via `CredDeleteW` shortly after launch.
* **Windows prerequisites & caveats for RDP-as-user:** The target Windows workstation must have RDP enabled and the escrowed user must belong to local `Remote Desktop Users` or `Administrators`. On single-session Windows 10/11 workstations, connecting via RDP as the currently signed-in user attaches directly to that user's active desktop session (preserving running extractions while locking the local screen). If Credential Guard blocks saved `TERMSRV/*` credentials, `Hive.App` falls back to audited reveal for manual entry at the NLA prompt.

Never capture credentials silently, store plaintext/reversible secrets without the separate KEK secret, embed credentials in `.rdp` files, write passwords to logs/SQLite caches, or expose escrowed credentials to ordinary `Supervisor`, `Examiner`, `SoftwareAdmin`, or `Auditor` accounts.

## Limits of enforcement and audit

Application gate controls launches from Hive only; it cannot prevent a supervisor using mstsc independently. Enforce actual RDP rights at Windows/network layer. Record request, actor, target, route, warning, deny/allow and correlation ID. A launch event is not proof of successful login; correlate Windows security/session events where policy allows. Do not label disconnected session as stopped workload; continue monitoring protected work. AD rights and LAN firewall policy apply in P6. Tailnet route grants and AD rights are independently required in P8. No public RDP, generic tunnel or remote shell. Test RDP when current user active, disconnected, protected, offline, domain unavailable, certificate mismatched and credential saving forbidden.
