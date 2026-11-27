# Cordon Hive — Engineering Progress & AI Agent Handoff

**Baseline Documentation:** `docs/` (v0.2, 27 November 2026 — updated for standalone Linux Docker Stack + `LocalJwt` auth + DFU Break-Glass Escrow P6)  
**Start Here:** [docs/README_START_HERE.md](docs/README_START_HERE.md)  
**Last Updated:** 2026-11-27  
**Current Active Phase:** **P4 — Durable Notifications & Persistent Widget (Code & Automated Tests Complete — 338/338 tests passing)** → Ready for **P5 — Safe Deployment** & **P6 — LAN RDP + DFU Break-Glass Escrow**  
**Deployment Scope:** **LAN-only (P1–P7)**. Server runs on a **standalone Linux server via Docker Stack** (`deploy/docker/`) against the existing LAN PostgreSQL database using **self-contained Local Hive Accounts (`LocalJwt`)** (zero Active Directory domain join, SPN, or keytab required); endpoints and UI remain native Windows. Tailscale/hybrid remote connectivity is **strictly deferred to P8** after P7 LAN operational acceptance.

---

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
  * `deploy/docker/docker-stack.yml` deploys `hive-server` with read-only rootfs, `cap_drop: [ALL]`, `no-new-privileges:true`, `Authentication__UserMode=LocalJwt`, Kestrel PEM TLS + `ClientCertificateMode.AllowCertificate`, Docker secrets mounted at `/run/secrets/*` (loaded via `AddKeyPerFile("/run/secrets", optional: true)`), and external LAN PostgreSQL connectivity.
  * `Hive.Server` fails closed at startup in `Production` if `Storage:Provider != "PostgreSQL"`.
* **Testing Note:** Automated integration tests run deterministically against `InMemory*Repository` and SQLite (`Microsoft.Data.Sqlite`), covering `LocalJwt` (`EnableTestingHeaderAuth=false`) and `TestingHeaderAuth` suites. `Postgres*Repository` and `PostgreSqlMigrationRunner` (`0001_InitialFoundation.sql`..`0005_Notifications.sql`) compile cleanly, while live Linux Docker + PostgreSQL runtime validation remains to be executed on the lab server.

---

## 3. Architectural & Security Invariants (Non-Negotiable)

Every contributor / AI agent MUST preserve these invariants:

1. **LAN-Only P1–P7 Boundary:** Do NOT add Tailscale SDKs, tailnet routing, Funnel, or remote ingress workarounds in P1–P7. PostgreSQL remains private to `Hive.Server` in every deployment.
2. **Standalone Linux Docker Stack / Windows Endpoints:** Keep `Hive.Contracts`, `Hive.Domain`, `Hive.Notifications`, `Hive.Infrastructure`, and `Hive.Server` cross-platform `net10.0` with zero AD domain-join requirement (`LocalJwt` default; `Negotiate` optional). Keep `Hive.App`, `Hive.Agent`, `Hive.Installer`, `Hive.Windows`, and `Cordon.Shared.Windows` native Windows.
3. **Server Authority & Fail Closed:** Never trust client-supplied SIDs, device IDs, or role claims (`X-Device-Id` headers, etc.). Resolve user subject/roles/sites server-side from verified JWT (`OnTokenValidated` checks `IsActive` in repository) or optional Negotiate, and bind device identity to client certificate thumbprints. Unknown, expired, locked, deactivated, or revoked identities fail closed (`401`/`403`/`423`).
4. **Separate Auth Boundaries:** Interactive user endpoints (`LocalJwt` / optional `Negotiate`) and device agent endpoints (`Certificate` / mTLS) are strictly separated. `TestingHeaderAuthenticationHandler` is registered ONLY when `ASPNETCORE_ENVIRONMENT=Testing` AND `Authentication:EnableTestingHeaderAuth=true`.
5. **Client Token & Cache Protection:** `Hive.App` protects access/refresh tokens (`DpapiTokenStore`) and per-user offline notification caches (`UserNotificationCache`, partitioned by SHA-256 of `UserSid`) using Win32 DPAPI (`CurrentUser`, `CRYPTPROTECT_UI_FORBIDDEN`) and never persists plaintext passwords.
6. **No Arbitrary Privileged Execution:** `Hive.Installer` accepts only strictly typed `BrokerInstallRequest` (`TargetId`, `ReleaseId`, `ManifestDigest`, `LeaseId`) validated against allowlisted profiles—never shell commands, executable paths, or arbitrary arguments.
7. **Protected Forensic Workloads & Safe Inventory:** Never terminate processes or force reboots automatically. Unknown or stale protection state blocks installation. Never run `Win32_Product` WMI queries, never recursively scan evidence drives or user directories, and never collect case filenames/paths/command lines/window titles. `TelemetrySanitizer` strips any path segments/arguments and preserves `null` (`Unknown`) for unsupported metrics.
8. **Version, Compliance & Notification Semantics:** Never compare software versions lexicographically; use `SoftwareVersionComparer` (`SoftwareComplianceEvaluator`). Unknown, incomplete, conflicting, or unparseable detections never evaluate to `Compliant`. Release creators cannot self-approve (`CanApproveSoftwareRelease`). Acknowledging a `SoftwareUpdate` or `RequiredAction` notification never completes it (`NotificationRecipientState`), and regular users cannot call `/complete` on `SoftwareUpdate` (`CanCompleteNotification`). Connectivity warnings never replace or zero the unresolved widget count.
9. **Local IPC Security:** Named pipes deny `NetworkSid` (local-only), apply restrictive DACLs, enforce `ProtocolConstants.MaxIpcMessageBytes` (64 KiB) framing limits (with iterative process-list trimming on `GetLocalTelemetry`), and derive caller SID via server impersonation (`RunAsClient`).
10. **P6 DFU Break-Glass Escrow Rules:** Never log passwords or store plaintext in DB/SQLite/`.rdp`; encrypt with AES-256-GCM bound to AAD using an independent Linux Docker secret (`/run/secrets/hive_vault_master_key`), require mandatory reason + audit on reveal/checkout, and delete temporary `CredWriteW` `TERMSRV/*` entries immediately after `mstsc` launch.

---

## 4. Phase Roadmap Status (`docs/03_Implementation/ROADMAP.md`)

| Phase | Name | Status | Summary of Deliverables | Exit Gate Status |
|---|---|---|---|---|
| **P0** | Discovery & Design Baseline | **Baseline Complete (Docs v0.2)** | Documentation stack in `docs/` (updated for standalone Linux Docker Stack ADR11, `LocalJwt` ADR06, and DFU Break-Glass Escrow ADR10), ADR register, threat model, Cordon Alert source review | Design baseline approved for prototyping; lab PKI/PostgreSQL infra setup pending |
| **P1** | Foundation & Auth Pivot | **Code & CI Complete** | 13-project solution (`CordonHive.sln`), `scripts/ci-local.ps1`, `.githooks/pre-push`, `deploy/docker/` stack, PostgreSQL DDL `0001_InitialFoundation.sql` & `0003_LocalAuthAccounts.sql` + `Npgsql`/`InMemory` repos, `Hive.Server` (`LocalJwt` HS256 + rotating refresh tokens + lockout + `/api/v1/auth/*` + `/api/v1/users/*` + `BootstrapAdminInitializer`, optional `Negotiate`, Device Cert auth, Kestrel PEM/mTLS, Production DB guard, SignalR `/hubs/state`, outbox dispatcher), canonical `IAgentLocalStore` SQLite store, `Cordon.Shared.Windows` geometry, `Hive.Windows` named-pipe IPC + WTS sessions, `Hive.Agent` (`enroll` CLI + mTLS cert + heartbeat + IPC), `Hive.Installer` disabled-by-default broker validator, `Hive.App` WinUI 3 shell with DPAPI token store & sign-in/password dialogs | Automated unit, integration (`LocalJwt` + `TestingHeaderAuth`), and Windows IPC/geometry tests pass. Remaining live lab gate: Docker image build + `0001`–`0005` against real PostgreSQL + TLS/mTLS cert on Linux host |
| **P2** | Visibility | **Code & CI Complete** | `TelemetryContracts.cs`, `TelemetrySanitizer` & `StorageWarningEvaluator`, `0002_TelemetryVisibility.sql`, `ITelemetryRepository` (`InMemory` + `Postgres`), `POST /api/v1/agent/metrics` & `GET /api/v1/workstations/{id}/telemetry`, `WindowsTelemetryCollector` (`GetSystemTimes`, `GlobalMemoryStatusEx`, `DriveInfo`, sanitized process snapshot), `TelemetryWorker` (offline queueing & drain), `GetLocalTelemetry` IPC with 64 KiB progressive trimming, WinUI `WorkstationDetailPage` & `ResourcesPage` | Automated domain, integration, and Windows collector/IPC cap tests pass (`0` warnings, `0` errors). Remaining lab gate: idle workstation CPU (<1%) and private working set (<150 MiB) benchmark + interactive UI review |
| **P3** | Software Compliance | **Code & CI Complete** | `SoftwareContracts.cs`, `SoftwareComplianceEvaluator` & dual-control `CanApproveSoftwareRelease`, `0004_SoftwareCompliance.sql` & `ISoftwareRepository` (`InMemory` + `Postgres`), `SoftwareEndpoints.cs`, `WindowsSoftwareInventoryCollector` (read-only Registry + MSIX + portable rules, zero `Win32_Product`), `InventoryWorker`, WinUI `SoftwarePage` & `SoftwareViewModel` | Automated domain, integration, and Windows inventory tests pass (`0` warnings, `0` errors). Remaining lab gate: live PostgreSQL verification + interactive WinUI `SoftwarePage` review |
| **P4** | Notifications & Widget | **Code & CI Complete (338/338 tests passing)** | `NotificationContracts.cs`, `NotificationEntity`/`NotificationRecipientState`, `ScopeAuthorizationService` (`CanPublishNotifications`, `CanCancelNotification`, `CanCompleteNotification`), `0005_Notifications.sql` & `INotificationRepository` (`InMemory` + `Postgres`, cursor sync + `SnapshotReset`, `Idempotency-Key`, optimistic concurrency), `NotificationEndpoints.cs`, `TaskbarLocator` + `NotificationBadgeState` + `TrayIconFallbackHost` in `Cordon.Shared.Windows`, WinUI `UserNotificationCache` (DPAPI per-SID), `AppToastNotifier`, `NotificationWidgetWindow` (10s auto-collapse flyout + tray fallback), `NotificationsViewModel` & `NotificationsPage` (compose/cancel/delivery counts + offline queue) | Automated domain (`241`), integration (`41`), and Windows widget (`56`) tests pass (`0` warnings, `0` errors). Remaining lab gate: Explorer restart/mixed DPI/multi-session/toast interactive Windows smoke test (`V21`) |
| **P5** | Safe Deployment | **Not Started** | Protected workload locks/guard, signed immutable manifests, HTTPS Range/ETag package cache, execution leases, active `Hive.Installer` MSI pilot, verification & audit | Not Started |
| **P6** | LAN RDP & Break-Glass Escrow | **Docs Complete; Implementation Not Started** | Native `mstsc` LAN launch workflow, session protection warning/block, AES-256-GCM `device_credential_escrow` (manual user/custodian updates, reason-audited plaintext reveal, ephemeral `CredWriteW` assisted RDP-as-user) | Not Started |
| **P7** | Hardening | **Not Started** | Backup/restore drills, pilot rings, signed MSI/Velopack packaging, monitoring, operational acceptance | Not Started |
| **P8** | Deferred Remote Connectivity | **Deferred (Gated)** | Optional Tailscale pilot, remote API reachability, split DNS, remote RDP, metered downloads (requires P7 acceptance + IT/security approval) | Blocked on P7 |
| **P9** | Optional Extensions | **Deferred** | Shared Cordon publisher adapters (`INotificationPublisherContract`), experimental Explorer docking, remote assistance | Deferred |

---

## 5. Latest Verified CI Run (`scripts/ci-local.ps1` + `.githooks/pre-push`)

Executed `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/ci-local.ps1`:
* **Static Hygiene & Invariant Gate:** PASS (`Win32_Product` query check & tracked secret/DB check).
* **Build Status:** All 13 projects built with `-p:TreatWarningsAsErrors=true` (**0 Warning(s), 0 Error(s)**, including `src/Hive.App/Hive.App.csproj` `Debug|x64` WinUI 3 XAML compilation).
* **Test Results:**
  * `tests/Hive.Domain.Tests`: **241 passed**, 0 failed, 0 skipped
  * `tests/Hive.Server.IntegrationTests`: **41 passed**, 0 failed, 0 skipped *(InMemory storage + SQLite; `LocalJwt` + `TestingHeaderAuth`)*
  * `tests/Hive.Windows.Tests`: **56 passed**, 0 failed, 0 skipped
  * **Total:** **338 passed**, 0 failed, 0 skipped

---

## 6. Solution & File Inventory

### Root, CI, Git Hooks & Linux Docker Deployment
* `CordonHive.sln`, `Directory.Build.props`, `Directory.Packages.props`, `.gitignore`
* `.githooks/pre-push`, `scripts/ci-local.ps1`, `scripts/install-git-hooks.ps1`
* `deploy/docker/Hive.Server.Dockerfile`, `deploy/docker/docker-stack.yml`, `deploy/docker/.env.example`, `deploy/docker/README.md`

### Core Cross-Platform Libraries (`net10.0`)
* `src/Hive.Contracts/` — `ProtocolConstants.cs`, `AuthContracts.cs`, `IdentityContracts.cs`, `EnrollmentContracts.cs`, `DeviceContracts.cs`, `AuditContracts.cs`, `SignalRContracts.cs`, `IpcContracts.cs`, `TelemetryContracts.cs`, `SoftwareContracts.cs`, `NotificationContracts.cs`
* `src/Hive.Domain/` — `Identity/IdentityModels.cs`, `Identity/UserAccountModels.cs`, `Devices/DeviceModels.cs`, `Enrollment/EnrollmentToken.cs`, `Audit/AuditEvent.cs`, `Software/SoftwareVersionComparer.cs`, `Software/SoftwareComplianceModels.cs`, `Deployments/DeploymentStateMachine.cs`, `Telemetry/TelemetryModels.cs`
* `src/Hive.Notifications/` — `NotificationModels.cs`
* `src/Hive.Infrastructure/` — `Migrations/0001_InitialFoundation.sql`..`0005_Notifications.sql`, `Migrations/PostgreSqlMigrationRunner.cs`, `Repositories/` (`RepositoryInterfaces.cs`, `InMemoryRepositories.cs`, `PostgresRepositories.cs`, `TelemetryRepositories.cs`, `UserAccountRepositories.cs`, `SoftwareRepositories.cs`, `NotificationRepositories.cs`), `Sqlite/AgentLocalStore.cs`
* `src/Hive.Server/` — `Program.cs`, `Authentication/HiveAuthentication.cs`, `Authentication/JwtTokenService.cs`, `Middleware/CorrelationIdMiddleware.cs`, `Endpoints/` (`HiveApiEndpoints.cs`, `AuthAndUserEndpoints.cs`, `SoftwareEndpoints.cs`, `NotificationEndpoints.cs`), `Hubs/StateHub.cs`, `Services/BootstrapAdminInitializer.cs`, `Services/OutboxDispatcherService.cs`, `appsettings.json`

### Windows Libraries, Services & App (`net10.0-windows10.0.26100.0`)
* `src/Cordon.Shared.Windows/` — `TaskbarGeometry.cs`, `DockGeometryCalculator.cs`, `BadgeDisplayFormatter.cs`, `IWidgetHost.cs`, `TaskbarLocator.cs`, `NotificationBadgeState.cs`, `TrayIconFallbackHost.cs`
* `src/Hive.Windows/` — `Ipc/` (`NamedPipeClient.cs`, `NamedPipeFraming.cs`, `NamedPipeSecurityFactory.cs`, `NamedPipeServerHost.cs`), `SystemInfo/WindowsMachineInfoProvider.cs`, `Telemetry/` (`ITelemetryCollector.cs`, `WindowsTelemetryCollector.cs`), `Inventory/SoftwareInventoryCollector.cs`
* `src/Hive.Agent/` — `Program.cs`, `AgentOptions.cs`, `AgentHttpClientFactory.cs`, `AgentEnrollmentBootstrapper.cs`, `AgentRuntimeState.cs`, `HeartbeatWorker.cs`, `TelemetryWorker.cs`, `InventoryWorker.cs`, `AgentUserIpcWorker.cs`
* `src/Hive.Installer/` — `BrokerOptions.cs` (`Enabled = false` by default), `BrokerCommandValidator.cs` (no `Process.Start` / shell execution)
* `src/Hive.App/` — `Hive.App.csproj` (`x64` unpackaged WinUI 3), `app.manifest`, `appsettings.json`, `App.xaml/.cs`, `MainWindow.xaml/.cs`, `Services/` (`AppSettings.cs`, `DpapiStorageHelper.cs`, `DpapiTokenStore.cs`, `UserNotificationCache.cs`, `AppToastNotifier.cs`, `HiveApiClient.cs`, `LocalAgentIpcClient.cs`), `ViewModels/` (`MainViewModel.cs`, `WorkstationsViewModel.cs`, `WorkstationDetailViewModel.cs`, `ResourcesViewModel.cs`, `SoftwareViewModel.cs`, `NotificationsViewModel.cs`, `TelemetryPresentation.cs`), `Views/` (`OverviewPage`, `WorkstationsPage`, `WorkstationDetailPage`, `ResourcesPage`, `SoftwarePage`, `NotificationsPage`, `NotificationWidgetWindow`, `SettingsPage`, plus phase stubs `DeploymentsPage`, `UpdatesPage`, `AuditPage`)

### Test Suites
* `tests/Hive.Domain.Tests/` — `ScopeAuthorizationTests.cs`, `LocalAuthDomainTests.cs`, `DeviceConnectivityTests.cs`, `SoftwareVersionComparerTests.cs`, `SoftwareComplianceEvaluatorTests.cs`, `DeploymentStateMachineTests.cs`, `NotificationStateTests.cs`, `NotificationAuthorizationTests.cs`, `EnrollmentTokenTests.cs`, `TelemetrySanitizerTests.cs`, `StorageWarningEvaluatorTests.cs`
* `tests/Hive.Server.IntegrationTests/` — `HiveServerFactory.cs`, `HiveServerIntegrationTests.cs`, `LocalAuthIntegrationTests.cs`, `AgentLocalStoreTests.cs`, `TelemetryIntegrationTests.cs`, `SoftwareComplianceIntegrationTests.cs`, `NotificationIntegrationTests.cs`
* `tests/Hive.Windows.Tests/` — `BrokerValidatorTests.cs`, `DockGeometryTests.cs`, `NotificationWidgetTests.cs`, `NamedPipeIpcTests.cs`, `SqliteAgentLocalStoreTests.cs`, `WindowsTelemetryCollectorTests.cs`, `SoftwareInventoryCollectorTests.cs`, `InventoryWorkerTests.cs`

---

## 7. Known Lab / Non-Automated Validation Pending

1. **Linux Docker + Live PostgreSQL Smoke:** Build `deploy/docker/Hive.Server.Dockerfile` on Linux, deploy `deploy/docker/docker-stack.yml`, run `0001`–`0005` migrations against real PostgreSQL, verify `POST /api/v1/auth/login` + password change + Kestrel mTLS device auth, and add an opt-in `HIVE_TEST_POSTGRES` integration test run.
2. **P2 Resource Benchmark (`V22`) & P4 Interactive Widget/Toast Test (`V21`):** Measure `Hive.Agent` idle CPU (<1% target) and private working set (<150 MiB target); interactively test `NotificationWidgetWindow`, Explorer restart, mixed DPI (`100–200%`), tray fallback, and Windows toast activation.
3. **Future Hardening Before P5:** Add enrollment proof-of-possession (CSR or client-cert handshake) and PostgreSQL telemetry retention pruning job (`device_metrics` >7d, `device_process_snapshots` >24h).

---

## 8. Requirements & Verification Traceability

| Req ID | Description | Verification IDs | Phase | Current Implementation Status |
|---|---|---|---|---|
| `FR01` | Single WinUI app, server auth, scoped API | `V01` | P1 | Implemented & verified (`LocalAuthIntegrationTests`, `HiveServerIntegrationTests`, WinUI sign-in & route guard) |
| `FR02` | Stable device UUID, asset allocation, duplicate detection | `V02` | P1 | Implemented & verified (`HiveServerIntegrationTests`) |
| `FR03` | Continuous service monitoring (CPU/RAM/disk/GPU/processes) | `V03`, `V22` | P1/P2 | Implemented & automated tests pass (`WindowsTelemetryCollectorTests`, `TelemetryIntegrationTests`); `V22` benchmark pending |
| `FR04` | Software inventory (MSI/MSIX/portable) with provenance | `V04` | P3 | Implemented & verified (`SoftwareInventoryCollectorTests`, `SoftwareComplianceIntegrationTests`; zero `Win32_Product`) |
| `FR05` | Approved version management & signed manifests | `V05` | P3/P5 | Catalogue, dual-control draft/approve/withdraw implemented & tested; signed deploy manifests in P5 |
| `FR06` | Non-disruptive deployment & workload guard | `V06`–`V09` | P5 | `DeploymentStateMachine` & Broker validator skeleton tested |
| `FR07` | Durable notifications, toast, persistent widget | `V10`–`V12` | P4 | Implemented & automated tests pass (`NotificationIntegrationTests`, `NotificationWidgetTests`, `NotificationAuthorizationTests`) |
| `FR08` | Native LAN RDP launch & warnings | `V13` | P6 | Not Started |
| `FR09` | DFU break-glass workstation credential escrow & assisted RDP | `V13` | P6 | Docs complete (`REMOTE_ACCESS.md`, `IDENTITY_SECURITY.md`, `DATA_MODEL.md`, `ADR10`); implementation in P6 |
| `FR10` | Offline telemetry/cache & reconnect reconciliation | `V11`, `V16` | P1/P2/P4 | Bounded SQLite outbound queue + DPAPI per-user `UserNotificationCache` offline queue/sync implemented & tested |
| `FR11` | Attributable append-only audit log | `V17` | P1 | Implemented & verified across P1–P4 integration tests |
| `FR12` | Hybrid Tailscale connectivity | `V14`, `V15` | **P8** | Intentionally Deferred |
| `FR13` | Shared notification surface (Cordon Alert reuse) | `V12` | P4/P9 | `INotificationPublisherContract` & `Cordon.Shared.Windows` (`TaskbarLocator`, `DockGeometryCalculator`, `TrayIconFallbackHost`) implemented |
| `FR14` | Time-bounded software exemptions | `V18` | P3 | Implemented & verified (`SoftwareComplianceEvaluatorTests`, `SoftwareComplianceIntegrationTests`) |
| `NFR` | Named pipe ACLs, fail-closed certs, widget resilience, benchmarks | `V19`–`V24` | P1–P7 | `V19` (IPC framing/SID/64 KiB cap) & `V20` (token/cert/user revocation fail-closed) tested |

---

## 9. Next Immediate Steps (For Next Session / AI Agent)

1. **Start P5 — Safe Deployment (`docs/03_Implementation/SOFTWARE_MANAGEMENT.md`):**
   - Implement protected workload locks (`maintenance_locks`, manual examiner protection lock via local IPC + active process/service safety evaluator) so active forensic workloads fail closed.
   - Add immutable signed manifest generation/verification (`ManifestDigest`, `PackageSha256`), HTTPS package upload & `Range`/`ETag` resumable download (`GET /api/v1/packages/{id}/content`), short-lived execution leases (`POST /api/v1/agent/deployments/{id}/lease`), and activate `Hive.Installer` for allowlisted silent no-reboot MSI profiles.
   - Implement `DeploymentsPage` and `UpdatesPage` in `Hive.App`.
2. **Start P6 — LAN RDP & DFU Break-Glass Credential Escrow (`docs/03_Implementation/REMOTE_ACCESS.md`):**
   - Implement `0006_RemoteAccessEscrow.sql` (`device_credential_escrow`), `BreakGlassCustodian` role, AES-256-GCM encryption bound to `/run/secrets/hive_vault_master_key`, write-only user/custodian credential update endpoints, mandatory-reason audited reveal (`Cache-Control: no-store`), and `Hive.App` ephemeral `CredWriteW` + `mstsc.exe` launch + `CredDeleteW` cleanup.
