# Remote access and credential handling

## Release scope

P6 implements native RDP over approved LAN routes only. Off-site RDP over Tailscale is deferred to P8 after LAN acceptance and separate network/security approval; it does not block P6.

## RDP launch workflow

Supervisor selects managed asset. API checks current AD role, asset scope and policy; returns approved hostname and advisory state (last seen, sessions, protection locks, route). Client confirms target and warns that RDP may disconnect or disrupt an interactive session. If protected or state unknown, default deny launch through Hive; exceptions require explicitly authorised process outside MVP and audit. Launch native mstsc using validated hostname or controlled `.rdp` settings without password or command injection. Require NLA, TLS certificate validation, authorised Windows login and host firewall; disable redirection of clipboard/drives/printers by default unless IT approves. Windows OS/edition and RDP configuration must support hosting; no automatic enablement by Hive. Prefer separate approved remote assistance for collaborative support.

## Credential options

| Option | Decision |
|---|---|
| Supervisor's individual AD account | Default; no application password storage |
| Windows Credential Manager | Optional per-supervisor/per-target saved credentials; OS policies may prevent use |
| Windows LAPS | Preferred managed local admin recovery if organisation supports it; directory-authorised access and audit |
| Shared central credentials | Deferred; only approved dedicated secrets vault, not plaintext/encrypted DB field managed by app |

User-requested per-machine/per-user credential saving is represented as an optional association between authorised supervisor, target and credential reference. Never capture examiner login password, reuse a shared account by default, embed credentials in RDP files, expose secret through server API, or store them in SQLite/Postgres. Let Windows native credential prompt/vault handle secrets. LAPS access is privileged and separate from RDP launch. Credential Guard and Remote Credential Guard policies need compatibility testing; no weakening policy to enable saved credentials.

## Limits of enforcement and audit

Application gate controls launches from Hive only; it cannot prevent a supervisor using mstsc independently. Enforce actual RDP rights at Windows/network layer. Record request, actor, target, route, warning, deny/allow and correlation ID. A launch event is not proof of successful login; correlate Windows security/session events where policy allows. Do not label disconnected session as stopped workload; continue monitoring protected work. AD rights and LAN firewall policy apply in P6. Tailnet route grants and AD rights are independently required in P8. No public RDP, generic tunnel or remote shell. Test RDP when current user active, disconnected, protected, offline, domain unavailable, certificate mismatched and credential saving forbidden.
