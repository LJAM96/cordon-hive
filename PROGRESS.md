# Cordon Hive — Engineering Progress & AI Agent Handoff

**Baseline Documentation:** `docs/` (v0.2, 27 November 2026 — updated for standalone Linux Docker Stack + `LocalJwt` auth)  
**Start Here:** [docs/README_START_HERE.md](docs/README_START_HERE.md)  
**Last Updated:** 2026-11-27  
**Current Active Phase:** **P3 — Software Compliance (Code & Automated Tests Complete — 305/305 tests passing)** → Ready for **P4 — Durable Notifications & Persistent Widget**  
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
* **WinUI 3 (`src/Hive.App`):** Uses `Microsoft.WindowsAppSDK` (`1.8.260921001`), unpackaged (`WindowsPackageType=None`), built for `x64` (`-p:Platform=x64`). Keep `Hive.App` decoupled from server DB drivers (`Npgsql`, `Microsoft.Data.Sqlite`) by using `LocalAgentIpcClient` directly over `NamedPipeClientStream`.
* **Local CI & Pre-Push Gate:**
  * Run `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/ci-local.ps1` from repo root to run static hygiene/invariant checks (`Win32_Product` WMI query ban, tracked secret/DB ban), build all 13 projects with `-p:TreatWarningsAsErrors=true` (including `Hive.App` `Debug|x64`), and run all 3 test suites.
  * **Git Remote & Hook Policy:** Remote is `https://github.com/LJAM96/cordon-hive.git` (`main`). Run `pwsh -File scripts/install-git-hooks.ps1` to bind `core.hooksPath = .githooks`, which automatically enforces `scripts/ci-local.ps1` on every `git push`. Future AI agents MUST commit & push per milestone and NEVER use `git push --no-verify`.
* **Server Hosting (Standalone Linux + Docker Stack + `LocalJwt`):**
  * `deploy/docker/Hive.Server.Dockerfile` builds `src/Hive.Server/Hive.Server.csproj` into a non-root `mcr.microsoft.com/dotnet/aspnet:10.0` image with `ca-certificates` and `curl` (no AD/Kerberos requirement).
  * `deploy/docker/docker-stack.yml` deploys `hive-server` with read-only rootfs, `cap_drop: [ALL]`, `no-new-privileges:true`, `Authentication__UserMode=LocalJwt`, Kestrel PEM TLS + `ClientCertificateMode.AllowCertificate`, Docker secrets mounted at `/run/secrets/*` (loaded via `AddKeyPerFile("/run/secrets", optional: true)`), and external LAN PostgreSQL connectivity.
  * `Hive.Server` fails closed at startup in `Production` if `Storage:Provider != "PostgreSQL"`.
* **Testing Note:** Automated integration tests run deterministically against `InMemory*Repository` and SQLite (`Microsoft.Data.Sqlite`), covering both `LocalJwt` (`EnableTestingHeaderAuth=false`) and `TestingHeaderAuth` suites. `Postgres*Repository` and `PostgreSqlMigrationRunner` (`0001_InitialFoundation.sql`..`0004_SoftwareCompliance.sql`) compile cleanly, while live Linux Docker + PostgreSQL runtime validation remains to be executed on the lab server.

---

## 3. Architectural & Security Invariants (Non-Negotiable)

Every contributor / AI agent MUST preserve these invariants:

1. **LAN-Only P1–P7 Boundary:** Do NOT add Tailscale SDKs, tailnet routing, Funnel, or remote ingress workarounds in P1–P7. PostgreSQL remains private to `Hive.Server` in every deployment.
2. **Standalone Linux Docker Stack / Windows Endpoints:** Keep `Hive.Contracts`, `Hive.Domain`, `Hive.Notifications`, `Hive.Infrastructure`, and `Hive.Server` cross-platform `net10.0` with zero AD domain-join requirement (`LocalJwt` default; `Negotiate` optional). Keep `Hive.App`, `Hive.Agent`, `Hive.Installer`, `Hive.Windows`, and `Cordon.Shared.Windows` native Windows.
3. **Server Authority & Fail Closed:** Never trust client-supplied SIDs, device IDs, or role claims (`X-Device-Id` headers, etc.). Resolve user subject/roles/sites server-side from verified JWT (`OnTokenValidated` checks `IsActive` in repository) or optional Negotiate, and bind device identity to client certificate thumbprints. Unknown, expired, locked, deactivated, or revoked identities fail closed (`401`/`403`/`423`).
4. **Separate Auth Boundaries:** Interactive user endpoints (`LocalJwt` / optional `Negotiate`) and device agent endpoints (`Certificate` / mTLS) are strictly separated. `TestingHeaderAuthenticationHandler` is registered ONLY when `ASPNETCORE_ENVIRONMENT=Testing` AND `Authentication:EnableTestingHeaderAuth=true`.
5. **Client Token Protection:** `Hive.App` protects access/refresh tokens at rest using Win32 DPAPI (`CurrentUser`, `CRYPTPROTECT_UI_FORBIDDEN`) in `DpapiTokenStore` and never persists plaintext passwords.
6. **No Arbitrary Privileged Execution:** `Hive.Installer` accepts only strictly typed `BrokerInstallRequest` (`TargetId`, `ReleaseId`, `ManifestDigest`, `LeaseId`) validated against allowlisted profiles—never shell commands, executable paths, or arbitrary arguments.
7. **Protected Forensic Workloads & Safe Inventory:** Never terminate processes or force reboots automatically. Unknown or stale protection state blocks installation. Never run `Win32_Product` WMI queries, never recursively scan evidence drives or user directories, and never collect case filenames/paths/command lines/window titles. `TelemetrySanitizer` strips any path segments/arguments and preserves `null` (`Unknown`) for unsupported metrics.
8. **Version, Compliance & Notification Semantics:** Never compare software versions lexicographically; use `SoftwareVersionComparer` (`SoftwareComplianceEvaluator`). Unknown, incomplete, conflicting, or unparseable detections never evaluate to `Compliant`. Release creators cannot self-approve (`CanApproveSoftwareRelease`). Acknowledging a `SoftwareUpdate` or `RequiredAction` notification never completes it (`NotificationRecipientState`).
9. **Local IPC Security:** Named pipes deny `NetworkSid` (local-only), apply restrictive DACLs, enforce `ProtocolConstants.MaxIpcMessageBytes` (64 KiB) framing limits (with iterative process-list trimming on `GetLocalTelemetry`), and derive caller SID via server impersonation (`RunAsClient`).

---

## 4. Phase Roadmap Status (`docs/03_Implementation/ROADMAP.md`)

| Phase | Name | Status | Summary of Deliverables | Exit Gate Status |
|---|---|---|---|---|
| **P0** | Discovery & Design Baseline | **Baseline Complete (Docs v0.2)** | Documentation stack in `docs/` (updated for standalone Linux Docker Stack ADR11 + `LocalJwt` ADR06), ADR register, threat model, Cordon Alert source review | Design baseline approved for prototyping; lab PKI/PostgreSQL infra setup pending |
| **P1** | Foundation & Auth Pivot | **Code & CI Complete** | 13-project solution (`CordonHive.sln`), `scripts/ci-local.ps1`, `deploy/docker/` stack, PostgreSQL DDL `0001_InitialFoundation.sql` & `0003_LocalAuthAccounts.sql` + `Npgsql`/`InMemory` repos, `Hive.Server` (`LocalJwt` HS256 + rotating refresh tokens + lockout + `/api/v1/auth/*` + `/api/v1/users/*` + `BootstrapAdminInitializer`, optional `Negotiate`, Device Cert auth, Kestrel PEM/mTLS, Production DB guard, SignalR `/hubs/state`, outbox dispatcher), canonical `IAgentLocalStore` SQLite store, `Cordon.Shared.Windows` geometry, `Hive.Windows` named-pipe IPC + WTS sessions, `Hive.Agent` (`enroll` CLI bootstrap + mTLS cert attachment + heartbeat + IPC), `Hive.Installer` disabled-by-default broker validator, `Hive.App` WinUI 3 shell with DPAPI token store & sign-in/password dialogs | Automated unit, integration (`LocalJwt` + `TestingHeaderAuth`), and Windows IPC/geometry tests pass. Remaining live lab gate: Docker image build + `0001`–`0004` against real PostgreSQL + TLS/mTLS cert on Linux host |
| **P2** | Visibility | **Code & CI Complete** | `TelemetryContracts.cs`, `TelemetrySanitizer` & `StorageWarningEvaluator`, `0002_TelemetryVisibility.sql`, `ITelemetryRepository` (`InMemory` + `Postgres`), `POST /api/v1/agent/metrics` & `GET /api/v1/workstations/{id}/telemetry`, `WindowsTelemetryCollector` (`GetSystemTimes`, `GlobalMemoryStatusEx`, `DriveInfo`, sanitized process snapshot), `TelemetryWorker` (offline queueing & drain), `GetLocalTelemetry` IPC with 64 KiB progressive trimming, WinUI `WorkstationDetailPage` & `ResourcesPage` | Automated domain, integration, and Windows collector/IPC cap tests pass (`0` warnings, `0` errors). Remaining lab gate: idle workstation CPU (<1%) and private working set (<150 MiB) benchmark on representative forensic workload + interactive UI review |
| **P3** | Software Compliance | **Code & CI Complete (305/305 tests passing)** | `SoftwareContracts.cs`, `SoftwareComplianceEvaluator` & dual-control `CanApproveSoftwareRelease` in `Hive.Domain`, `0004_SoftwareCompliance.sql` & `ISoftwareRepository` (`InMemory` + `Postgres`), `SoftwareEndpoints.cs` (`PUT /api/v1/agent/inventory`, `/api/v1/software/products|releases|exemptions|compliance`, `/api/v1/workstations/{id}/compliance`), `WindowsSoftwareInventoryCollector` (read-only 32/64-bit/user Registry + MSIX `AppxAllUserStore` + explicit portable file rules, zero `Win32_Product`), `InventoryWorker` (coalesced offline queue + `401`/`403` halt), WinUI `SoftwarePage` & `SoftwareViewModel` (role-gated catalogue/approval/exemptions, glyph+text compliance status) | Automated domain (`217`), integration (`36`), and Windows inventory (`52`) tests pass (`0` warnings, `0` errors). Remaining lab gate: live PostgreSQL verification + interactive WinUI `SoftwarePage` review |
| **P4** | Notifications | **Not Started** | Durable notification outbox/cursor sync, per-user encrypted/ACL SQLite cache, admin compose UI, WinUI `AppNotificationManager` toasts, `Cordon.Shared.Windows` overlay widget + tray fallback | Not Started |
| **P5** | Safe Deployment | **Not Started** | Protected workload locks/guard, signed immutable manifests, HTTPS Range/ETag package cache, execution leases, active `Hive.Installer` MSI pilot, verification & audit | Not Started |
| **P6** | LAN RDP | **Not Started** | Native `mstsc` LAN launch workflow, session protection warning/block, individual AD / Credential Manager / LAPS integration | Not Started |
| **P7** | Hardening | **Not Started** | Backup/restore drills, pilot rings, signed MSI/Velopack packaging, monitoring, operational acceptance | Not Started |
| **P8** | Deferred Remote Connectivity | **Deferred (Gated)** | Optional Tailscale pilot, remote API reachability, split DNS, remote RDP, metered downloads (requires P7 acceptance + IT/security approval) | Blocked on P7 |
| **P9** | Optional Extensions | **Deferred** | Shared Cordon publisher adapters (`INotificationPublisherContract`), experimental Explorer docking, remote assistance | Deferred |

---

## 5. Latest Verified CI Run (`scripts/ci-local.ps1`)

Executed `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/ci-local.ps1`:
* **Build Status:** All 13 projects built with **0 Warning(s), 0 Error(s)** (including `src/Hive.App/Hive.App.csproj` `Debug|x64` WinUI 3 XAML compilation).
* **Test Results:**
  * `tests/Hive.Domain.Tests`: **217 passed**, 0 failed, 0 skipped
  * `tests/Hive.Server.IntegrationTests`: **36 passed**, 0 failed, 0 skipped *(InMemory storage + SQLite; both `LocalJwt` and `TestingHeaderAuth`)*
  * `tests/Hive.Windows.Tests`: **52 passed**, 0 failed, 0 skipped
  * **Total:** **305 passed**, 0 failed, 0 skipped

---

## 6. Solution & File Inventory

### Root, CI & Linux Docker Deployment
* `CordonHive.sln` — Master solution (all 13 projects)
* `Directory.Build.props`, `Directory.Packages.props`, `.gitignore`
* `scripts/ci-local.ps1` — Local build & test runner
* `deploy/docker/Hive.Server.Dockerfile`, `deploy/docker/docker-stack.yml`, `deploy/docker/.env.example`, `deploy/docker/README.md`

### Core Cross-Platform Libraries (`net10.0`)
* `src/Hive.Contracts/` — `ProtocolConstants.cs`, `AuthContracts.cs`, `IdentityContracts.cs`, `EnrollmentContracts.cs`, `DeviceContracts.cs`, `AuditContracts.cs`, `SignalRContracts.cs`, `IpcContracts.cs`, `TelemetryContracts.cs`, `SoftwareContracts.cs`
* `src/Hive.Domain/` — `Identity/IdentityModels.cs`, `Identity/UserAccountModels.cs`, `Devices/DeviceModels.cs`, `Enrollment/EnrollmentToken.cs`, `Audit/AuditEvent.cs`, `Software/SoftwareVersionComparer.cs`, `Software/SoftwareComplianceModels.cs`, `Deployments/DeploymentStateMachine.cs`, `Telemetry/TelemetryModels.cs`
* `src/Hive.Notifications/` — `NotificationModels.cs` (`NotificationType`, `NotificationRecipientState`, `INotificationPublisherContract`)
* `src/Hive.Infrastructure/` — `Migrations/0001_InitialFoundation.sql`..`0004_SoftwareCompliance.sql`, `Migrations/PostgreSqlMigrationRunner.cs`, `Repositories/RepositoryInterfaces.cs`, `Repositories/InMemoryRepositories.cs`, `Repositories/PostgresRepositories.cs`, `Repositories/TelemetryRepositories.cs`, `Repositories/UserAccountRepositories.cs`, `Repositories/SoftwareRepositories.cs`, `Sqlite/AgentLocalStore.cs`
* `src/Hive.Server/` — `Program.cs`, `Authentication/HiveAuthentication.cs`, `Authentication/JwtTokenService.cs`, `Middleware/CorrelationIdMiddleware.cs`, `Endpoints/HiveApiEndpoints.cs`, `Endpoints/AuthAndUserEndpoints.cs`, `Endpoints/SoftwareEndpoints.cs`, `Hubs/StateHub.cs`, `Services/BootstrapAdminInitializer.cs`, `Services/OutboxDispatcherService.cs`, `appsettings.json`

### Windows Libraries, Services & App (`net10.0-windows10.0.26100.0`)
* `src/Cordon.Shared.Windows/` — `TaskbarGeometry.cs`, `DockGeometryCalculator.cs`, `BadgeDisplayFormatter.cs`, `IWidgetHost.cs`
* `src/Hive.Windows/` — `Ipc/NamedPipeClient.cs`, `Ipc/NamedPipeFraming.cs`, `Ipc/NamedPipeSecurityFactory.cs`, `Ipc/NamedPipeServerHost.cs`, `SystemInfo/WindowsMachineInfoProvider.cs`, `Telemetry/ITelemetryCollector.cs`, `Telemetry/WindowsTelemetryCollector.cs`, `Inventory/SoftwareInventoryCollector.cs`
* `src/Hive.Agent/` — `Program.cs`, `AgentOptions.cs`, `AgentHttpClientFactory.cs`, `AgentEnrollmentBootstrapper.cs`, `AgentRuntimeState.cs`, `HeartbeatWorker.cs`, `TelemetryWorker.cs`, `InventoryWorker.cs`, `AgentUserIpcWorker.cs`
* `src/Hive.Installer/` — `BrokerOptions.cs` (`Enabled = false` by default), `BrokerCommandValidator.cs` (no `Process.Start` / shell execution)
* `src/Hive.App/` — `Hive.App.csproj` (`x64` unpackaged WinUI 3), `app.manifest`, `appsettings.json`, `App.xaml/.cs`, `MainWindow.xaml/.cs`, `Services/` (`AppSettings.cs`, `DpapiTokenStore.cs`, `HiveApiClient.cs`, `LocalAgentIpcClient.cs`), `ViewModels/` (`MainViewModel.cs`, `WorkstationsViewModel.cs`, `WorkstationDetailViewModel.cs`, `ResourcesViewModel.cs`, `SoftwareViewModel.cs`, `TelemetryPresentation.cs`), `Views/` (`OverviewPage`, `WorkstationsPage`, `WorkstationDetailPage`, `ResourcesPage`, `SoftwarePage`, `SettingsPage`, plus phase stubs `DeploymentsPage`, `UpdatesPage`, `NotificationsPage`, `AuditPage`)

### Test Suites
* `tests/Hive.Domain.Tests/` — `ScopeAuthorizationTests.cs`, `LocalAuthDomainTests.cs`, `DeviceConnectivityTests.cs`, `SoftwareVersionComparerTests.cs`, `SoftwareComplianceEvaluatorTests.cs`, `DeploymentStateMachineTests.cs`, `NotificationStateTests.cs`, `EnrollmentTokenTests.cs`, `TelemetrySanitizerTests.cs`, `StorageWarningEvaluatorTests.cs`
* `tests/Hive.Server.IntegrationTests/` — `HiveServerFactory.cs`, `HiveServerIntegrationTests.cs`, `LocalAuthIntegrationTests.cs`, `AgentLocalStoreTests.cs`, `TelemetryIntegrationTests.cs`, `SoftwareComplianceIntegrationTests.cs`
* `tests/Hive.Windows.Tests/` — `BrokerValidatorTests.cs`, `DockGeometryTests.cs`, `NamedPipeIpcTests.cs`, `SqliteAgentLocalStoreTests.cs`, `WindowsTelemetryCollectorTests.cs`, `SoftwareInventoryCollectorTests.cs`, `InventoryWorkerTests.cs`

---

## 7. Known Lab / Non-Automated Validation Pending

1. **Linux Docker + Live PostgreSQL Smoke:** Build `deploy/docker/Hive.Server.Dockerfile` on Linux, deploy `deploy/docker/docker-stack.yml`, run `0001`–`0004` migrations against real PostgreSQL, verify `POST /api/v1/auth/login` + password change + Kestrel mTLS device auth, and add an opt-in `HIVE_TEST_POSTGRES` integration test run.
2. **P2 Resource Benchmark (`V22`):** Measure `Hive.Agent` idle CPU (<1% target) and private working set (<150 MiB target) on a representative workstation alongside non-evidential hashing/imaging test workloads.
3. **Interactive WinUI 3 Verification:** Launch `Hive.App` interactively against a running `Hive.Server` + `Hive.Agent` to verify sign-in/password dialogs, `WorkstationDetailPage`, `ResourcesPage`, `SoftwarePage`, DPI scaling (`100–200%`), dark/light/high-contrast themes, and Narrator accessibility.
4. **Future Hardening Before P5:** Add enrollment proof-of-possession (CSR or client-cert handshake) and PostgreSQL telemetry retention pruning job (`device_metrics` >7d, `device_process_snapshots` >24h).

---

## 8. Requirements & Verification Traceability

| Req ID | Description | Verification IDs | Phase | Current Implementation Status |
|---|---|---|---|---|
| `FR01` | Single WinUI app, server auth, scoped API | `V01` | P1 | Implemented & verified via `LocalAuthIntegrationTests` + `HiveServerIntegrationTests` + WinUI sign-in/route-guard |
| `FR02` | Stable device UUID, asset allocation, duplicate detection | `V02` | P1 | Implemented & verified via `HiveServerIntegrationTests` |
| `FR03` | Continuous service monitoring (CPU/RAM/disk/GPU/processes) | `V03`, `V22` | P1/P2 | Implemented & automated tests pass (`WindowsTelemetryCollectorTests`, `TelemetryIntegrationTests`); `V22` workload benchmark pending |
| `FR04` | Software inventory (MSI/MSIX/portable) with provenance | `V04` | P3 | Implemented & verified (`SoftwareInventoryCollectorTests`, `SoftwareComplianceIntegrationTests`; zero `Win32_Product`) |
| `FR05` | Approved version management & signed manifests | `V05` | P3/P5 | Catalogue, dual-control draft/approve/withdraw implemented & tested (`SoftwareComplianceEvaluatorTests`, `SoftwareComplianceIntegrationTests`); signed deploy manifests in P5 |
| `FR06` | Non-disruptive deployment & workload guard | `V06`–`V09` | P5 | `DeploymentStateMachine` & Broker validator skeleton tested |
| `FR07` | Durable notifications, toast, persistent widget | `V10`–`V12` | P4 | `Hive.Notifications` & `Cordon.Shared.Windows` dock geometry tested |
| `FR08` | Native LAN RDP launch & warnings | `V13` | P6 | Not Started |
| `FR09` | Optional saved RDP credentials / LAPS | `V13` | P6 | Not Started |
| `FR10` | Offline telemetry/cache & reconnect reconciliation | `V11`, `V16` | P1/P2/P4 | Bounded SQLite outbound queue + telemetry/inventory offline queue/drain implemented & tested |
| `FR11` | Attributable append-only audit log | `V17` | P1 | Implemented & verified across `HiveServerIntegrationTests`, `LocalAuthIntegrationTests`, `SoftwareComplianceIntegrationTests` |
| `FR12` | Hybrid Tailscale connectivity | `V14`, `V15` | **P8** | Intentionally Deferred |
| `FR13` | Shared notification surface (Cordon Alert reuse) | `V12` | P4/P9 | `INotificationPublisherContract` & `Cordon.Shared.Windows` seeded |
| `FR14` | Time-bounded software exemptions | `V18` | P3 | Implemented & verified (`SoftwareComplianceEvaluatorTests`, `SoftwareComplianceIntegrationTests`) |
| `NFR` | Named pipe ACLs, fail-closed certs, widget resilience, benchmarks | `V19`–`V24` | P1–P7 | `V19` (IPC framing/SID/64 KiB cap) & `V20` (token/cert/user revocation fail-closed) tested |

---

## 9. Next Immediate Steps (For Next Session / AI Agent)

1. **Start P4 — Durable Notifications & Persistent Widget (`docs/03_Implementation/NOTIFICATIONS.md`, `CORDON_ALERT_WIDGET.md`):**
   - Add notification DTOs (`NotificationDto`, `CreateNotificationRequest`, `NotificationSyncResponse`, `NotificationActionRequest`) to `Hive.Contracts`.
   - Add `0005_Notifications.sql` (`notifications`, `notification_recipients`, `notification_deliveries`) + `INotificationRepository` (`InMemory` + `Postgres`) + `POST /api/v1/notifications`, `GET /api/v1/me/notifications`, `POST /api/v1/me/notifications/{id}/read|ack|complete` + SignalR `StateChangedEnvelope` hints.
   - Implement per-user ACL-protected SQLite notification cache (`UserNotificationCache`) in `Hive.App` (or `Hive.Infrastructure`) so offline state & unresolved counts persist across restarts without leaking between Windows users.
   - Implement WinUI `AppNotificationManager` native toasts, `NotificationsPage` (including admin compose view), and the `Cordon.Shared.Windows`-backed **persistent taskbar overlay widget + tray fallback** (remembering: closing flyout or acknowledging a required action/update never marks it complete!).
2. **Optional Infrastructure Check:** Run `deploy/docker/` against your Linux server + PostgreSQL DB + `Hive.App` interactive verification, or add an opt-in `HIVE_TEST_POSTGRES` test pass.
