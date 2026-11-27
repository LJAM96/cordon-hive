# WinUI 3 experience specification

## Design system

Native C# WinUI 3 on .NET 10 LTS with Windows App SDK version pinned following compatibility testing. Use NavigationView, native controls, Mica where supported, fallback solid backgrounds, Segoe UI Variable and system accent/theme. Use MVVM and virtualised lists; no embedded web dashboard. Main window is unelevated. Native title bar, accessible names, keyboard shortcuts, UI Automation, visible focus and reduced-motion options.

## Supervisor navigation

| Page | Content and actions |
|---|---|
| Overview | Fleet totals, online/stale/offline, protected work, compliance, storage warnings, notifications requiring action; filter by site or group |
| Workstations | Search asset/hostname/assigned user, sort by last seen and load; select detail; allocate asset, initiate RDP if authorised |
| Workstation detail | Current session(s), assigned user, network path, CPU/RAM/GPU charts, per-volume capacity, processes, software, deployments, notification history, protection reasons |
| Users | AD-backed identity and asset assignment, current login association, scoped notification targets |
| Software | Discovered vs managed; product rules, approved versions, validation evidence and exemptions |
| Deployments | Draft, approved, staged, deferred, installing, verified, failed, unknown; target preview and cancellation before start |
| Notifications | Compose with priority, audience, completion type, due date; delivery/read/acknowledged/completed counts |
| Audit | Scoped filter, timestamp, actor, target, action, outcome and export controls |
| Settings | Role-dependent network, telemetry, repository and retention policy |

Show last update timestamp and stale indicators; never render offline as zero utilisation. Explain unknown inventory and protection status. RDP and deployment controls display blocked reasons. Bulk actions require target preview and explicit confirmation. Supervisor dashboard must not advertise access beyond role scope.

## User navigation

Overview, Resources, Software, Updates, Notifications, Settings. Dashboard displays local asset, connectivity, resource summaries, protected mode and outstanding actions. Software shows detected version, approved target, applicable exceptions, status and last checked. Update detail explains deferral, download progress and verification. User may request installation or defer where policy allows, but cannot bypass safety checks. Protection switch supports explicit release and reason; a deadline never silently clears it.

## Taskbar experience

A single bell or Cordon glyph near the taskbar, with count of unresolved actionable items; visible while main window is closed. Single click opens a compact Fluent flyout; double click opens main notification centre. Collapse after inactivity or click outside, retaining unresolved count. Optional hide when zero; standard tray fallback with equivalent accessibility. Show connectivity warning separately rather than replacing the count. No focus theft on incoming alerts, fullscreen or secure desktop; no obstruction of critical controls. Do not promise unclosable UI: user can terminate processes, Windows can suppress toasts, and the secure desktop is outside the widget's reach. Report UI health separately from action completion.

## View states

Loading with skeleton, empty, filtered, partial, stale, offline, error and permission denied. All dates local for display, UTC for storage. Distinguish machine targeting from user targeting. Software rows: compliant, update required, ahead/unapproved, not installed, exempt, unknown, pending reboot. Severity indicated by text and icon, not colour alone.

## Acceptance

Test dark/light/high contrast, 100–200% DPI and mixed monitors, 1366×768 and larger, keyboard only, Narrator, touch where relevant, taskbar auto-hide, fullscreen, multiple sessions and Explorer restart. Verify that regular users cannot navigate to or invoke supervisor features by modifying UI routes.
