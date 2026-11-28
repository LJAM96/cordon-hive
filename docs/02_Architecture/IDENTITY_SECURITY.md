# Identity, authorisation and security

## Identities

The primary interactive user authentication mode for standalone LAN Linux hosting is **self-contained Local Hive Accounts (`LocalJwt`)** backed by PostgreSQL: PBKDF2-SHA512 salted password hashes, short-lived signed HS256 JWT access tokens, rotating hashed refresh tokens (protected at rest on Windows endpoints via DPAPI `CurrentUser`), forced initial password change for bootstrap accounts, and brute-force lockout. This requires **zero Active Directory domain join, SPN, or keytab administration**. Integrated Windows Authentication (`Negotiate` / Kerberos + LDAPS) or OIDC remains an optional future configuration when IT AD administration is available. Resolve immutable subject ID (`hive-user:<uuid>`), display name, roles and site scopes server-side; display name is never identity. Domain/central authentication is independent of Tailscale. P1–P7 use LAN connectivity; off-site identity routing and tailnet revocation are P8-only concerns. Cached local UI state is not fresh server authentication; if unavailable, local read-only view and already cached user-specific notifications only. Server commands require fresh authentication and current permissions.

Enroll each agent through administrator-authorised one-time enrollment, unique device UUID and client certificate (validated by Kestrel mTLS against the internal CA trust bundle mounted into the Linux stack); prefer non-exportable TPM-backed key where available. Software key fallback requires documented approval, restrictive ACLs and rotation. Certificate binds to enrollment, not asset name. Renewal, revocation, replacement, lost device and duplicate identity workflows required. Never accept a device ID header as proof of identity.

## Permissions

| Capability / optional AD group mapping (example) | Permission |
|---|---|
| CordonHiveUsers | Own/authorised session and device information; own acknowledgements |
| CordonHiveSupervisors | Scoped inventory, assignments, communications |
| CordonHiveSoftwareAdmins | Draft package/product, request approval, schedule approved releases |
| CordonHiveSoftwareApprovers | Independent validation/release approval; separation from uploader where practicable |
| CordonHiveRemoteAccess | Scoped RDP request, not automatic OS access |
| CordonHiveBreakGlassCustodians | Scoped reason-audited break-glass workstation credential reveal & assisted RDP checkout (strictly separate from ordinary Supervisor/Auditor) |
| CordonHiveAdministrators | Enrollment and platform policy; no implicit credential access |
| CordonHiveAuditors | Scoped read-only audit |

LocalJwt roles and scope are authoritative by default; optional AD group mapping is configurable. Server resolves actor and resource scope per operation; client visibility is not security. Re-evaluate permissions on sensitive commands and reconnect. No client-side role claims trusted. Separate publishing and signing keys; privileged actions attributable to individual accounts, not shared logins.

## Threat model

| Threat | Controls |
|---|---|
| Compromised agent | Unique certificate, device-scoped reads/writes, no peer API access, revocation, rate limits |
| Compromised server/package share | Independent signed immutable manifest, approved signer allowlist, hash verification, restricted package broker, staged rings |
| Malicious or mistaken administrator | Target preview, two-person approval, least privilege, audit, no arbitrary command channel |
| Credential leakage | Default individual/Vault/LAPS RDP; P6 DFU break-glass escrow uses AES-256-GCM bound with AAD (`deviceId`, `credentialId`, `keyVersion`) + separate Docker secret key (`/run/secrets/hive_vault_master_key`), mandatory-reason audit, `no-store` headers, and ephemeral `CredWriteW` cleanup; never plaintext in DB/logs/SQLite/`.rdp` |
| Untrusted local process | Named pipe ACL, impersonation/identity verification, bounded messages, per-session SID checks, no generic privileged execution |
| Spoofed notification | Server authorisation, authenticated publishers, signed/verified policy where privileged, separate source attribution |
| Stolen remote device | Device approval, encrypted disk, certificate/tailnet revocation, no cached privileged commands, no sensitive notification content in toast by default |
| Data leakage | TLS, minimal inventory, role scope, no process arguments/file paths, encrypted backups, retention and export audit |
| Tampered audit | Append-only application role, external protected export, restricted deletion and independently monitored integrity |

## Secrets and transport

TLS on LAN and tailnet; verify trust and hostname, no certificate bypass. Use internal CA or approved certificate issuance. Store server secrets (PostgreSQL credentials, JWT signing key, TLS private key, optional bootstrap admin password) in **Docker secrets** or host-protected read-only mounts (`0400` owned by the non-root container UID) — never baked into container images, environment variables in plaintext compose files, or committed to Git; private signing keys stay separate from ordinary application runtime, ideally offline or hardware protected. Restrict database account by function. Audit key issuance/rotation. Local named pipes deny remote access; server impersonates caller as needed and checks SID/session against operation; broker only accepts narrowly scoped commands from authorised service and revalidates independently. Avoid unsafe installer argument concatenation, shell execution and user-writable executable paths.

## Governance

Operational metadata can identify staff and reveal workload patterns. Complete security/IT approval, applicable data protection assessment, access/retention policy, quality impact assessment, incident response and forensic validation before live use. No claims of accreditation. Tailscale on personal accounts or isolated evidence networks is prohibited by design until formal approval.
