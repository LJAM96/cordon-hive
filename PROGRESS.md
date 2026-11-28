# Cordon Hive — Engineering Progress & AI Agent Handoff

**Baseline Documentation:** `docs/` (v0.2, 27 November 2026 — updated for standalone Linux Docker Stack + `LocalJwt` auth + DFU Break-Glass Escrow P6)  
**Start Here:** [docs/README_START_HERE.md](docs/README_START_HERE.md)  
**Last Updated:** 2026-11-28 (documentation-only expansion proposal; historical test results unchanged)  
**Current Active Phase:** **P7 — Hardening (P1–P7 LAN Scope Code & CI Complete — 582/582 tests passing)** → Ready for Live Lab Operational Acceptance & Gated **P8 — Deferred Remote Connectivity**  
**Deployment Scope:** **LAN-only (P1–P7)**. Server runs on a **standalone Linux server via Docker Stack** (`deploy/docker/`) against the existing LAN PostgreSQL database using **self-contained Local Hive Accounts (`LocalJwt`)** (zero Active Directory domain join, SPN, or keytab required); endpoints and UI remain native Windows. Tailscale/hybrid remote connectivity is **strictly deferred to P8** after P7 LAN operational acceptance.

---

## Proposed v0.3 expansion backlog (not started)

See [FR15–FR29](docs/01_Product/LABORATORY_EXPANSION.md) and [E1–E13](docs/03_Implementation/ROADMAP.md). P7 live acceptance remains outstanding; P8 remains separately gated. [Resilience architecture](docs/02_Architecture/RESILIENCE_AND_PEER_ASSIST.md) retains central PostgreSQL, endpoint-local SQLite and fail-closed offline operation; E11 resilience/capacity, E12 optional peer package transport and E13 optional HA are proposed only. No P2P database or distributed compute implementation is authorised. No implementation, migration, local CI rerun or production test is claimed by this documentation change. The 582 tests below are the previously reported result for the existing code.

## 1. Purpose of This Document

This file is the single source of truth for implementation progress, architectural invariants, verified build/test results, environment setup, and next actions so any human engineer or subsequent AI coding agent can pick up cleanly without re-discovering the codebase or violating design gates.

> **Rule for AI Agents:** Update this file whenever a phase, deliverable, verification gate, or architectural decision changes. Never mark a gate or test passing unless you ran it after your final edit and observed it pass.

---

## 2. Environment & Build Notes

* **OS:** Windows 11 / NT 10.0.26200.0, Windows SDK `10.0.26100.0`
* **.NET SDK:** `11.0.100` installed alongside `.NET 10.0.15` runtimes.
  * Projects target `net10.0` and `net10.0-windows10.0.26100.0` (`SupportedOSPlatformVersion=10.0.19041.0`).
  * **Do NOT create a `global.json` pinning a `10.0.x` SDK**, because only SDKs `9.0.321` and `11.0.100` are installed on this host.
* **WinUI 3 (`src/Hive.App`):** Uses `Microsoft.WindowsAppSDK` (`1.8.260921001`), unpackaged (`WindowsPackageType=None`), built for `x64` (`-p:Platform=x64`). Keep `Hive.App` decoupled from server DB drivers (`Npgsql`, `Microsoft.Data.Sqlite`) by using `LocalAgentIpcClient` directly over `NamedPipeClientStream` and DPAPI files (`DpapiTokenStore`, `UserNotificationCache`).
* **Local CI & Pre-Push Gate:**
  * Run `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/ci-local.ps1` from repo root to run static hygiene/invariant checks (`Win32_Product` WMI query ban, tracked secret/DB ban), build all 13 projects with `-p:TreatWarningsAsErrors=true` (including `Hive.App` `Debug|x64`), and run all 3 test suites.
  * **Git Remote & Hook Policy:** Remote is `https://github.com/LJAM96/cordon-hive.git` (`main`). Run `pwsh -File scripts/install-git-hooks.ps1` to bind `core.hooksPath = .githooks`, which automatically enforces `scripts/ci-local.ps1` on every `git push`. Future AI agents MUST commit & push per milestone and NEVER use `git push --no-verify`.
* **Server Hosting (Standalone Linux + Docker Stack + `LocalJwt`):**
  * `deploy/docker/Hive.Server.Dockerfile` builds `src/Hive.Server/Hive.Server.csproj` into a non-root `mcr.microsoft.com/dotnet/aspnet:10.0` image with `ca-certificates` and `curl` (no AD/Kerberos requirement).
  * `deploy/docker/docker-stack.yml` deploys `hive-server` with read-only rootfs, `cap_drop: [ALL]`, `no-new-privileges:true`, `Authentication__UserMode=LocalJwt`, Kestrel PEM TLS + `ClientCertificateMode.AllowCertificate`, Docker secrets mounted at `/run/secrets/*` (`hive_db_connection_string`, `hive_jwt_signing_key`, `hive_tls_cert`, `hive_tls_key`, `hive_bootstrap_admin_password`, `hive_vault_master_key`), and external LAN PostgreSQL connectivity.
  * `Hive.Server` fails closed at startup in `Production` if `Storage:Provider != "PostgreSQL"`.
* **Testing Note:** Automated integration tests run deterministically against `InMemory*Repository` and SQLite (`Microsoft.Data.Sqlite`), covering `LocalJwt` (`EnableTestingHeaderAuth=false`) and `TestingHeaderAuth` suites. `Postgres*Repository` and `PostgreSqlMigrationRunner` (`0001_InitialFoundation.sql`..`0007_SafeDeployments.sql`) compile cleanly, while live Linux Docker + PostgreSQL runtime validation remains to be executed on the lab server.

---

## 3. Architectural & Security Invariants (Non-Negotiable)

Every contributor / AI agent MUST preserve these invariants:

1. **LAN-Only P1–P7 Boundary:** Do NOT add Tailscale SDKs, tailnet routing, Funnel, or remote ingress workarounds in P1–P7. PostgreSQL remains private to `Hive.Server` in every deployment.
2. **Standalone Linux Docker Stack / Windows Endpoints:** Keep `Hive.Contracts`, `Hive.Domain`, `Hive.Notifications`, `Hive.Infrastructure`, and `Hive.Server` cross-platform `net10.0` with zero AD domain-join requirement (`LocalJwt` default; `Negotiate` optional). Keep `Hive.App`, `Hive.Agent`, `Hive.Installer`, `Hive.Windows`, and `Cordon.Shared.Windows` native Windows.
3. **Server Authority & Fail Closed:** Never trust client-supplied SIDs, device IDs, or role claims (`X-Device-Id` headers, etc.). Resolve user subject/roles/sites server-side from verified JWT (`OnTokenValidated` checks `IsActive` in repository) or optional Negotiate, and bind device identity to client certificate thumbprints. Unknown, expired, locked, deactivated, or revoked identities fail closed (`401`/`403`/`423`).
4. **Separate Auth Boundaries:** Interactive user endpoints (`LocalJwt` / optional `Negotiate`) and device agent endpoints (`Certificate` / mTLS) are strictly separated. `TestingHeaderAuthenticationHandler` is registered ONLY when `ASPNETCORE_ENVIRONMENT=Testing` AND `Authentication:EnableTestingHeaderAuth=true`.
5. **Client Token & Cache Protection:** `Hive.App` protects access/refresh tokens (`DpapiTokenStore`) and per-user offline notification caches (`UserNotificationCache`, partitioned by SHA-256 of `UserSid`) using Win32 DPAPI (`CurrentUser`, `CRYPTPROTECT_UI_FORBIDDEN`) and never persists plaintext passwords.
6. **No Arbitrary Privileged Execution:** `Hive.Installer` accepts only strictly typed `BrokerInstallRequest` (`TargetId`, `ReleaseId`, `ManifestDigest`, `LeaseId`) validated against allowlisted profiles—never shell commands, executable paths, or arbitrary arguments.
7. **Protected Forensic Workloads & Safe Inventory:** Never terminate processes or force reboots automatically. Unknown or stale protection state blocks installation or assisted RDP. Never run `Win32_Product` WMI queries, never recursively scan evidence drives or user directories, and never collect case filenames/paths/command lines/window titles. `TelemetrySanitizer` strips any path segments/arguments and preserves `null` (`Unknown`) for unsupported metrics.
8. **Version, Compliance & Notification Semantics:** Never compare software versions lexicographically; use `SoftwareVersionComparer` (`SoftwareComplianceEvaluator`). Unknown, incomplete, conflicting, or unparseable detections never evaluate to `Compliant`. Release creators cannot self-approve (`CanApproveSoftwareRelease`). Acknowledging a `SoftwareUpdate` or `RequiredAction` notification never completes it (`NotificationRecipientState`), and regular users cannot call `/complete` on `SoftwareUpdate` (`CanCompleteNotification`). Connectivity warnings never replace or zero the unresolved widget count.
9. **Local IPC Security:** Named pipes deny `NetworkSid` (local-only), apply restrictive DACLs, enforce `ProtocolConstants.MaxIpcMessageBytes` (64 KiB) framing limits (with iterative process-list trimming on `GetLocalTelemetry`), and derive caller SID via server impersonation (`RunAsClient`).
10. **P6 DFU Break-Glass Escrow Rules:** Never log passwords or store plaintext in DB/SQLite/`.rdp`; encrypt with AES-256-GCM bound to AAD `(keyVersion, deviceId, credentialId)` using an independent Linux Docker secret (`/run/secrets/hive_vault_master_key`), require explicit `BreakGlassCustodian` role + mandatory reason + immutable audit (`Cache-Control: no-store`) on reveal/checkout (zero implicit reveal rights for `Administrator`/`Supervisor`), enforce NLA + TLS cert level 2 + disabled clipboard/drives/printers, and delete temporary `CredWriteW` `TERMSRV/*` entries immediately after `mstsc` launch.

---

## 4. Phase Roadmap Status (`docs/03_Implementation/ROADMAP.md`)

| Phase | Name | Status | Summary of Deliverables | Exit Gate Status |
|---|---|---|---|---|
| **P0** | Discovery & Design Baseline | **Baseline Complete (Docs v0.2)** | Documentation stack in `docs/` (updated for standalone Linux Docker Stack ADR11, `LocalJwt` ADR06, and DFU Break-Glass Escrow ADR10), ADR register, threat model, Cordon Alert source review | Design baseline approved for prototyping; lab PKI/PostgreSQL infra setup pending |
| **P1** | Foundation & Auth Pivot | **Code & CI Complete** | 13-project solution (`CordonHive.sln`), `scripts/ci-local.ps1`, `.githooks/pre-push`, `deploy/docker/` stack, PostgreSQL DDL `0001_InitialFoundation.sql` & `0003_LocalAuthAccounts.sql` + `Npgsql`/`InMemory` repos, `Hive.Server` (`LocalJwt` HS256 + rotating refresh tokens + lockout + `/api/v1/auth/*` + `/api/v1/users/*` + `BootstrapAdminInitializer`, optional `Negotiate`, Device Cert auth, Kestrel PEM/mTLS, Production DB guard, SignalR `/hubs/state`, outbox dispatcher), canonical `IAgentLocalStore` SQLite store, `Cordon.Shared.Windows` geometry, `Hive.Windows` named-pipe IPC + WTS sessions, `Hive.Agent` (`enroll` CLI + mTLS cert + heartbeat + IPC), `Hive.Installer` disabled-by-default broker validator, `Hive.App` WinUI 3 shell with DPAPI token store & sign-in/password dialogs | Automated unit, integration (`LocalJwt` + `TestingHeaderAuth`), and Windows IPC/geometry tests pass. Remaining live lab gate: Docker image build + `0001`–`0006` against real PostgreSQL + TLS/mTLS cert on Linux host |
| **P2** | Visibility | **Code & CI Complete** | `TelemetryContracts.cs`, `TelemetrySanitizer` & `StorageWarningEvaluator`, `0002_TelemetryVisibility.sql`, `ITelemetryRepository` (`InMemory` + `Postgres`), `POST /api/v1/agent/metrics` & `GET /api/v1/workstations/{id}/telemetry`, `WindowsTelemetryCollector` (`GetSystemTimes`, `GlobalMemoryStatusEx`, `DriveInfo`, sanitized process snapshot), `TelemetryWorker` (offline queueing & drain), `GetLocalTelemetry` IPC with 64 KiB progressive trimming, WinUI `WorkstationDetailPage` & `ResourcesPage` | Automated domain, integration, and Windows collector/IPC cap tests pass (`0` warnings, `0` errors). Remaining lab gate: idle workstation CPU (<1%) and private working set (<150 MiB) benchmark + interactive UI review |
| **P3** | Software Compliance | **Code & CI Complete** | `SoftwareContracts.cs`, `SoftwareComplianceEvaluator` & dual-control `CanApproveSoftwareRelease`, `0004_SoftwareCompliance.sql` & `ISoftwareRepository` (`InMemory` + `Postgres`), `SoftwareEndpoints.cs`, `WindowsSoftwareInventoryCollector` (read-only Registry + MSIX + portable rules, zero `Win32_Product`), `InventoryWorker`, WinUI `SoftwarePage` & `SoftwareViewModel` | Automated domain, integration, and Windows inventory tests pass (`0` warnings, `0` errors). Remaining lab gate: live PostgreSQL verification + interactive WinUI `SoftwarePage` review |
| **P4** | Notifications & Widget | **Code & CI Complete** | `NotificationContracts.cs`, `NotificationEntity`/`NotificationRecipientState`, `ScopeAuthorizationService` (`CanPublishNotifications`, `CanCancelNotification`, `CanCompleteNotification`), `0005_Notifications.sql` & `INotificationRepository` (`InMemory` + `Postgres`, cursor sync + `SnapshotReset`, `Idempotency-Key`, optimistic concurrency), `NotificationEndpoints.cs`, `TaskbarLocator` + `NotificationBadgeState` + `TrayIconFallbackHost` in `Cordon.Shared.Windows`, WinUI `UserNotificationCache` (DPAPI per-SID), `AppToastNotifier`, `NotificationWidgetWindow` (10s auto-collapse flyout + tray fallback), `NotificationsViewModel` & `NotificationsPage` | Automated domain, integration, and Windows widget tests pass (`0` warnings, `0` errors). Remaining lab gate: Explorer restart/mixed DPI/multi-session/toast interactive Windows smoke test (`V21`) |
| **P5** | Safe Deployment | **Code & CI Complete (510/510 total)** | `DeploymentContracts.cs`, `IpcContracts.cs` (`GetProtectionLock`/`SetProtectionLock`), `DeploymentModels.cs` (`ReleaseManifestCrypto` ECDSA P-256 + SHA-256 canonical JSON, fail-closed `WorkloadSafetyGuard` with zero CPU-idle dependency), `ScopeAuthorizationService` (`CanSignSoftwareRelease`, `CanViewDeployments`, `CanManageDeployments`), `0007_SafeDeployments.sql` & `DeploymentRepositories.cs` (`FileSystemPackageArtifactStore`, `InMemoryDeploymentRepository`, `PostgresDeploymentRepository`), `ReleaseSigningService.cs`, `DeploymentEndpoints.cs` (package upload, HTTP `Range`/`ETag` download, manifest signing, confirmed job creation, target cancel, agent sync/leases/events), `ResumablePackageDownloader.cs`, `SafeMsiBrokerExecutor.cs` (disabled by default, exclusive maintenance `Mutex`, structured `ArgumentList` `/i ... /qn /norestart REBOOT=ReallySuppress`, exit `0`/`3010`/`1641` handling), `AgentUserIpcWorker` & `HeartbeatWorker` lock persistence, WinUI `DeploymentsPage`/`UpdatesPage` | Automated domain (`+81`), integration (`+5`), and Windows broker/downloader (`+14`) tests pass (`0` warnings, `0` errors). Remaining lab gate: signed pilot MSI on target LAN workstation + `0007` PostgreSQL verification |
| **P6** | LAN RDP & Break-Glass Escrow | **Code & CI Complete (410/410 tests passing)** | `RemoteAccessContracts.cs`, `HeartbeatRequest`/`Device` `ProtectedModeActive` tracking, `HiveRole.BreakGlassCustodian`, `EscrowCredentialCrypto` (AES-256-GCM + AAD), `RdpPolicyEvaluator`, `0006_RemoteAccessEscrow.sql` & `IEscrowCredentialRepository` (`InMemory` + `Postgres`), `EscrowVaultService`, `RemoteAccessEndpoints.cs` (`/rdp-request`, `/escrow-credential`, `/reveal` with `no-store` & zero secrets in audit), `WindowsRdpCredentialManager` (`CredWriteW`/`CredDeleteW`), `NativeRdpLauncher` & WinUI `Win32RdpLauncher`, `SettingsPage` write-only user escrow form, `WorkstationDetailPage` custodian rotate, 60s auto-clear reveal dialog, LAN RDP & Assisted RDP-as-user | Automated domain (`294`), integration (`45`), and Windows RDP (`71`) tests pass (`0` warnings, `0` errors). Remaining lab gate: live `mstsc.exe` LAN RDP & Credential Guard check against target Windows workstation (`V13`) |
| **P7** | Hardening | **Code & CI Complete (582/582 total)** | `OperationsContracts.cs`, `EnrollmentProofOfPossession` (RSA/ECDSA challenge signing & `AgentEnrollmentBootstrapper` + server `EnrollmentOptions` enforcement), `VersionCompatibilityEvaluator`, `TelemetryRetentionHostedService` + `PruneExpiredAsync` (>7d metrics / >24h snapshots), `DeploymentControlState` + emergency stop (`POST /api/v1/operations/emergency-stop`, `InvalidateAllActiveLeasesAsync`), `GET /health/ready` + Docker Stack healthcheck, `GET /api/v1/audit` filters & `GET /api/v1/audit/export` (`csv`/`jsonl` `no-store` + CSV formula protection), WinUI `AuditViewModel`/`AuditPage`, `scripts/backup-hive.ps1`, `scripts/restore-hive-drill.ps1`, `scripts/package-release.ps1`, opt-in `HIVE_TEST_POSTGRES` live DB test | Automated domain (`423`), integration (`56`), and Windows benchmark/PoP/script (`103`) tests pass (`0` warnings, `0` errors). Remaining lab gate: live Linux Docker + PostgreSQL smoke/restore rehearsal & IT sign-off (`V22`–`V24`) |
| **P8** | Deferred Remote Connectivity | **Deferred (Gated)** | Optional Tailscale pilot, remote API reachability, split DNS, remote RDP, metered downloads (requires P7 acceptance + IT/security approval) | Blocked on P7 |
| **P9** | Optional Extensions | **Deferred** | Shared Cordon publisher adapters (`INotificationPublisherContract`), experimental Explorer docking, remote assistance | Deferred |

---

## 5. Latest Verified CI Run (`scripts/ci-local.ps1` + `.githooks/pre-push`)

Executed `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/ci-local.ps1`:
* **Static Hygiene & Invariant Gate:** PASS (`Win32_Product` query check & tracked secret/DB check).
* **Build Status:** All 13 projects built with `-p:TreatWarningsAsErrors=true` (**0 Warning(s), 0 Error(s)**, including `src/Hive.App/Hive.App.csproj` `Debug|x64` WinUI 3 XAML compilation).
* **Test Results:**
  * `tests/Hive.Domain.Tests`: **423 passed**, 0 failed, 0 skipped
  * `tests/Hive.Server.IntegrationTests`: **56 passed**, 0 failed, 0 skipped *(InMemory storage + SQLite; `LocalJwt` + `TestingHeaderAuth`; plus opt-in `HIVE_TEST_POSTGRES` suite)*
  * `tests/Hive.Windows.Tests`: **103 passed**, 0 failed, 0 skipped
  * **Total:** **582 passed**, 0 failed, 0 skipped

---

## 6. Solution & File Inventory

### Root, CI, Git Hooks & Linux Docker Deployment
* `CordonHive.sln`, `Directory.Build.props`, `Directory.Packages.props`, `.gitignore`
* `.githooks/pre-push`, `scripts/ci-local.ps1`, `scripts/install-git-hooks.ps1`, `scripts/backup-hive.ps1`, `scripts/restore-hive-drill.ps1`, `scripts/package-release.ps1`
* `deploy/docker/Hive.Server.Dockerfile`, `deploy/docker/docker-stack.yml`, `deploy/docker/.env.example`, `deploy/docker/README.md`

### Core Cross-Platform Libraries (`net10.0`)
* `src/Hive.Contracts/` — `ProtocolConstants.cs`, `AuthContracts.cs`, `IdentityContracts.cs`, `EnrollmentContracts.cs`, `DeviceContracts.cs`, `AuditContracts.cs`, `SignalRContracts.cs`, `IpcContracts.cs`, `TelemetryContracts.cs`, `SoftwareContracts.cs`, `NotificationContracts.cs`, `RemoteAccessContracts.cs`, `DeploymentContracts.cs`, `OperationsContracts.cs`
* `src/Hive.Domain/` — `Identity/IdentityModels.cs`, `Identity/UserAccountModels.cs`, `Devices/DeviceModels.cs`, `Enrollment/EnrollmentToken.cs`, `Enrollment/EnrollmentProofOfPossession.cs`, `Audit/AuditEvent.cs`, `Software/SoftwareVersionComparer.cs`, `Software/SoftwareComplianceModels.cs`, `Deployments/DeploymentStateMachine.cs`, `Deployments/DeploymentModels.cs`, `Telemetry/TelemetryModels.cs`, `RemoteAccess/RemoteAccessModels.cs`, `Operations/VersionCompatibilityEvaluator.cs`
* `src/Hive.Notifications/` — `NotificationModels.cs`
* `src/Hive.Infrastructure/` — `Migrations/0001_InitialFoundation.sql`..`0007_SafeDeployments.sql`, `Migrations/PostgreSqlMigrationRunner.cs`, `Repositories/` (`RepositoryInterfaces.cs`, `InMemoryRepositories.cs`, `PostgresRepositories.cs`, `TelemetryRepositories.cs`, `UserAccountRepositories.cs`, `SoftwareRepositories.cs`, `NotificationRepositories.cs`, `EscrowCredentialRepositories.cs`, `DeploymentRepositories.cs`), `Sqlite/AgentLocalStore.cs`
* `src/Hive.Server/` — `Program.cs`, `Authentication/HiveAuthentication.cs`, `Authentication/JwtTokenService.cs`, `Middleware/CorrelationIdMiddleware.cs`, `Endpoints/` (`HiveApiEndpoints.cs`, `AuthAndUserEndpoints.cs`, `SoftwareEndpoints.cs`, `NotificationEndpoints.cs`, `RemoteAccessEndpoints.cs`, `DeploymentEndpoints.cs`, `OperationsEndpoints.cs`), `Hubs/StateHub.cs`, `Services/` (`BootstrapAdminInitializer.cs`, `OutboxDispatcherService.cs`, `EscrowVaultService.cs`, `ReleaseSigningService.cs`, `TelemetryRetentionHostedService.cs`, `DeploymentControlState.cs`), `appsettings.json`

### Windows Libraries, Services & App (`net10.0-windows10.0.26100.0`)
* `src/Cordon.Shared.Windows/` — `TaskbarGeometry.cs`, `DockGeometryCalculator.cs`, `BadgeDisplayFormatter.cs`, `IWidgetHost.cs`, `TaskbarLocator.cs`, `NotificationBadgeState.cs`, `TrayIconFallbackHost.cs`
* `src/Hive.Windows/` — `Ipc/` (`NamedPipeClient.cs`, `NamedPipeFraming.cs`, `NamedPipeSecurityFactory.cs`, `NamedPipeServerHost.cs`), `SystemInfo/WindowsMachineInfoProvider.cs`, `Telemetry/` (`ITelemetryCollector.cs`, `WindowsTelemetryCollector.cs`), `Inventory/SoftwareInventoryCollector.cs`, `RemoteAccess/` (`WindowsRdpCredentialManager.cs`, `NativeRdpLauncher.cs`), `Deployments/ResumablePackageDownloader.cs`
* `src/Hive.Agent/` — `Program.cs`, `AgentOptions.cs`, `AgentHttpClientFactory.cs`, `AgentEnrollmentBootstrapper.cs`, `AgentRuntimeState.cs`, `HeartbeatWorker.cs`, `TelemetryWorker.cs`, `InventoryWorker.cs`, `AgentUserIpcWorker.cs`
* `src/Hive.Installer/` — `BrokerOptions.cs` (`Enabled = false` by default), `BrokerCommandValidator.cs`, `SafeMsiBrokerExecutor.cs` (structured `ArgumentList` `/i ... /qn /norestart REBOOT=ReallySuppress` only; no shell execution)
* `src/Hive.App/` — `Hive.App.csproj` (`x64` unpackaged WinUI 3), `app.manifest`, `appsettings.json`, `App.xaml/.cs`, `MainWindow.xaml/.cs`, `Services/` (`AppSettings.cs`, `DpapiStorageHelper.cs`, `DpapiTokenStore.cs`, `UserNotificationCache.cs`, `AppToastNotifier.cs`, `HiveApiClient.cs`, `LocalAgentIpcClient.cs`, `Win32RdpLauncher.cs`), `ViewModels/` (`MainViewModel.cs`, `WorkstationsViewModel.cs`, `WorkstationDetailViewModel.cs`, `ResourcesViewModel.cs`, `SoftwareViewModel.cs`, `NotificationsViewModel.cs`, `DeploymentsViewModel.cs`, `UpdatesViewModel.cs`, `AuditViewModel.cs`, `TelemetryPresentation.cs`), `Views/` (`OverviewPage`, `WorkstationsPage`, `WorkstationDetailPage`, `ResourcesPage`, `SoftwarePage`, `NotificationsPage`, `NotificationWidgetWindow`, `DeploymentsPage`, `UpdatesPage`, `AuditPage`, `SettingsPage`)

### Test Suites
* `tests/Hive.Domain.Tests/` — `ScopeAuthorizationTests.cs`, `LocalAuthDomainTests.cs`, `DeviceConnectivityTests.cs`, `SoftwareVersionComparerTests.cs`, `SoftwareComplianceEvaluatorTests.cs`, `DeploymentStateMachineTests.cs`, `SafeDeploymentDomainTests.cs`, `NotificationStateTests.cs`, `NotificationAuthorizationTests.cs`, `EnrollmentTokenTests.cs`, `TelemetrySanitizerTests.cs`, `StorageWarningEvaluatorTests.cs`, `RemoteAccessDomainTests.cs`, `HardeningDomainTests.cs`
* `tests/Hive.Server.IntegrationTests/` — `HiveServerFactory.cs`, `HiveServerIntegrationTests.cs`, `LocalAuthIntegrationTests.cs`, `AgentLocalStoreTests.cs`, `TelemetryIntegrationTests.cs`, `SoftwareComplianceIntegrationTests.cs`, `NotificationIntegrationTests.cs`, `RemoteAccessIntegrationTests.cs`, `DeploymentIntegrationTests.cs`, `HardeningAndRecoveryIntegrationTests.cs`
* `tests/Hive.Windows.Tests/` — `BrokerValidatorTests.cs`, `DockGeometryTests.cs`, `NotificationWidgetTests.cs`, `NamedPipeIpcTests.cs`, `SqliteAgentLocalStoreTests.cs`, `WindowsTelemetryCollectorTests.cs`, `SoftwareInventoryCollectorTests.cs`, `InventoryWorkerTests.cs`, `RemoteAccessWindowsTests.cs`, `SafeDeploymentWindowsTests.cs`, `HardeningBenchmarkAndCompatibilityTests.cs`

---

## 7. Known Lab / Non-Automated Validation Pending

1. **Linux Docker + Live PostgreSQL Smoke:** Run `HIVE_TEST_POSTGRES` migration test (`0001`–`0007`), build `deploy/docker/Hive.Server.Dockerfile` on Linux, deploy `deploy/docker/docker-stack.yml`, verify `GET /health/ready` + `POST /api/v1/auth/login` + Kestrel mTLS device PoP enrollment, and rehearse `scripts/backup-hive.ps1` + `scripts/restore-hive-drill.ps1`.
2. **Interactive Lab & Hardware Sign-Off (`V05`, `V13`, `V21`–`V24`):** Measure sustained `Hive.Agent` idle CPU (<1%) and working set (<150 MiB) on an imaging/hashing bench (`V22`), verify `NotificationWidgetWindow`/toasts across Explorer restart & mixed DPI (`V21`), deploy a signed pilot MSI to a non-evidential LAN workstation (`V05`), and verify live `mstsc.exe` Assisted RDP against a target Windows host (`V13`).
3. **Artifact Signing:** Sign outputs produced by `scripts/package-release.ps1` with organization Authenticode / container signing keys before pilot distribution.

---

## 8. Requirements & Verification Traceability

| Req ID | Description | Verification IDs | Phase | Current Implementation Status |
|---|---|---|---|---|
| `FR01` | Single WinUI app, server auth, scoped API | `V01` | P1 | Implemented & verified (`LocalAuthIntegrationTests`, `HiveServerIntegrationTests`, WinUI sign-in & route guard) |
| `FR02` | Stable device UUID, asset allocation, duplicate detection | `V02` | P1 | Implemented & verified (`HiveServerIntegrationTests`) |
| `FR03` | Continuous service monitoring (CPU/RAM/disk/GPU/processes) | `V03`, `V22` | P1/P2 | Implemented & automated tests pass (`WindowsTelemetryCollectorTests`, `TelemetryIntegrationTests`); `V22` benchmark pending |
| `FR04` | Software inventory (MSI/MSIX/portable) with provenance | `V04` | P3 | Implemented & verified (`SoftwareInventoryCollectorTests`, `SoftwareComplianceIntegrationTests`; zero `Win32_Product`) |
| `FR05` | Approved version management & signed manifests | `V05` | P3/P5 | Implemented & verified (`SoftwareComplianceIntegrationTests`, `DeploymentIntegrationTests`, `SafeDeploymentDomainTests` ECDSA P-256 + SHA-256) |
| `FR06` | Non-disruptive deployment & workload guard | `V06`–`V09` | P5 | Implemented & verified (`WorkloadSafetyGuard`, `SafeMsiBrokerExecutor`, `ResumablePackageDownloader`, `SafeDeploymentWindowsTests`, `DeploymentIntegrationTests`) |
| `FR07` | Durable notifications, toast, persistent widget | `V10`–`V12` | P4 | Implemented & automated tests pass (`NotificationIntegrationTests`, `NotificationWidgetTests`, `NotificationAuthorizationTests`) |
| `FR08` | Native LAN RDP launch & warnings | `V13` | P6 | Implemented & automated tests pass (`RemoteAccessIntegrationTests`, `RemoteAccessWindowsTests`, `RemoteAccessDomainTests`); live `mstsc` lab test pending |
| `FR09` | DFU break-glass workstation credential escrow & assisted RDP | `V13` | P6 | Implemented & automated tests pass (AES-256-GCM escrow, manual updates, 60s reveal, `CredWriteW` assisted RDP) |
| `FR10` | Offline telemetry/cache & reconnect reconciliation | `V11`, `V16` | P1/P2/P4 | Bounded SQLite outbound queue + DPAPI per-user `UserNotificationCache` offline queue/sync implemented & tested |
| `FR11` | Attributable append-only audit log | `V17` | P1/P7 | Implemented & verified across P1–P7 integration tests, `AuditPage` console filter/pagination, and `/api/v1/audit/export` (`csv`/`jsonl`, `no-store`) |
| `FR12` | Hybrid Tailscale connectivity | `V14`, `V15` | **P8** | Intentionally Deferred |
| `FR13` | Shared notification surface (Cordon Alert reuse) | `V12` | P4/P9 | `INotificationPublisherContract` & `Cordon.Shared.Windows` (`TaskbarLocator`, `DockGeometryCalculator`, `TrayIconFallbackHost`) implemented |
| `FR14` | Time-bounded software exemptions | `V18` | P3 | Implemented & verified (`SoftwareComplianceEvaluatorTests`, `SoftwareComplianceIntegrationTests`) |
| `NFR` | Named pipe ACLs, fail-closed certs, retention, recovery, benchmarks | `V19`–`V24` | P1–P7 | `V19` (IPC ACL/64 KiB cap), `V20` (PoP & fail-closed certs/revocation), `V22` (collector benchmark), `V23` (emergency stop/lease invalidation & restore drill), `V24` (retention, version compatibility, release packaging) automated |

---

## 9. Next Immediate Steps (For Next Session / AI Agent)

1. **Lab Operational Acceptance (Sign-Off Before P8):**
   - Run live Linux Docker + PostgreSQL + `HIVE_TEST_POSTGRES` verification, `scripts/backup-hive.ps1` + `scripts/restore-hive-drill.ps1`, Authenticode release signing (`scripts/package-release.ps1`), pilot MSI test (`V05`), interactive widget smoke (`V21`), and live LAN RDP (`V13`).
2. **Gated Future Phase — P8 Deferred Remote Connectivity (`docs/03_Implementation/ROADMAP.md`):**
   - Only after P7 LAN operational acceptance and explicit IT/security sign-off, evaluate Tailscale off-site reachability (`V14`, `V15`).
