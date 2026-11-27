# Cordon Alert widget integration

## Source baseline

Reviewed private repository `LJAM96/cordon-alert`, branch `master`, commit `86886209e9ae8eb17b7f1559507580bc7c85e783`. Review is source-only, not runtime validation. See [references](../05_Reference/SOURCE_REFERENCES.md). Current app targets .NET 9 Windows with Avalonia 12.0.4, Fluent theme and Velopack. Main Monitor app remains WinUI 3/.NET 10. Reuse native geometry and shell behaviour, not a wholesale Avalonia dependency.

## Actual implementation findings

| Source | Observed behaviour |
|---|---|
| TaskbarInfo.cs L36–117 | SHAppBarMessage(ABM_GETTASKBARPOS), taskbar band/edge and screen work-area correction |
| TaskbarInfo.cs L119–343 | FindWindow("Shell_TrayWnd"), SetWindowLongPtr style changes, SetParent, ScreenToClient, SetWindowPos, region clipping, Z-order and detach fallback |
| WidgetWindow.cs L76–149 | Transparent undecorated window, no taskbar button, configurable topmost; 1s watcher, 10s collapse, 240ms single tap timer |
| WidgetWindow.cs L151–239 | SVG glyph or fallback, 20×20 count badge, loading spinner 20ms timer |
| WidgetWindow.cs L424–480, 798–811 | Task list count controls badge; loading/error replaces badge; no tasks hides it |
| WidgetWindow.cs L520–585 | Single tap toggles flyout; double tap opens dashboard |
| WidgetWindow.cs L665–737 | Expanding detaches child, clears clip region, places above taskbar; collapsing clips and reattaches |
| WidgetWindow.cs L950–1097 | Topmost fallback respects overlapping topmost windows, attach retry logic |
| WidgetWindow.cs L1130–1309 | Dock layout, work-area placement and visible region; primary taskbar lookup |
| App.cs L73–128, 375–453 | Startup, widget reconstruction on taskbar loss, 30s task polling and acknowledgement |
| StartupRegistration.cs | Per-user Run registry registration |

## Porting strategy

Extract `TaskbarEdge`, native rectangle/point and pure `DockGeometry` calculations into framework-neutral `Cordon.Shared.Windows`. Replace Avalonia PixelRect/PixelPoint, dispatcher, layout and window handle adapters with platform-neutral types and WinUI host. Isolate native interop and safe region lifetime (GDI ownership transfers only on successful SetWindowRgn). Keep native methods internal. Separate notification view model from shell placement, so same behaviour can be hosted by a dedicated process later if needed.

| Mode | Decision |
|---|---|
| Independent overlay (default) | Borderless non-activating compact window near/over permitted taskbar free space; separate native flyout or expanded window on explicit click. Use HWND and supported AppWindow/Win32 APIs. Test transparent rendering; if unavailable use a small native Win32 layered host with WinUI main/flyout rather than Avalonia dependency. |
| Explorer child docking (experimental) | Existing SetParent pattern behind explicit feature flag, isolated widget process if necessary; not supported as stable Windows shell extension. Never make it required. |
| Tray fallback | Shell notification area icon and menu, with native notification centre access; fallback when taskbar overlay unavailable or incompatible. |

## Geometry and behaviour

Use per-monitor DPI-aware v2, physical pixels for HWND coordinates and DIPs for XAML, convert explicitly. Query taskbar/work area on display/DPI/shell changes. Primary taskbar first; configurable secondary monitor position without assuming internal Explorer window class stability. Clamp to monitor bounds and avoid taskbar buttons/clock; account for auto-hide, negative coordinates and dynamic layout. If no safe free space, move immediately above taskbar or fall back to tray. Do not reserve space by modifying Explorer. Badge count `min(count, 99)` plus overflow indication; state shown with accessible label. No focus stealing, do not cover full-screen apps or secure desktop. Click opens flyout, optional double click main centre, 10-second collapse only when not interacting/accessibility focus is clear.

## Improvements to existing code before reuse

* Cross-process SetParent can reset child DPI awareness; Explorer restart and shell updates can break docking. Keep independent overlay default and validate experimentally in an isolated host.
* One-second taskbar polling becomes event-driven plus bounded recovery watchdog. Handle TaskbarCreated, display/DPI/session changes and shell restart; avoid event storms.
* Loading spinner currently runs at 20ms even when hidden. Stop on hidden/idle; honour reduced motion.
* Replace hardcoded debug log location in TaskbarInfo.cs with bounded per-user structured logs; remove personal paths and staff list from extracted library.
* Keep actionable count on load/error; present connectivity separately. Replace dashboard poll with durable notification adapter, not generic dashboard scraping.
* Recreate on Explorer restart without duplicating timers, native handles or event subscriptions. Clean up on exit; keep one host per interactive session.
* Fix coordinate/DPI assumptions, multiple monitors and fixed geometry. Validate native handle ownership and WinUI compatibility rather than transplanting XAML directly.

## Proposed modules

`TaskbarGeometry`, `TaskbarLocator`, `OverlayPlacement`, `TaskbarWatcher`, `NativeWindowInterop`, `IWidgetHost`, `NotificationBadgeViewModel`, `WidgetFlyout`, `TrayFallback`. Pure geometry tests use fabricated rectangles; native integration tests on Windows only. Package contracts independently of UI. No changes to Cordon Alert required for MVP; later adapter/migration is a separate project with its own approval and regression tests. Confirm rights to reuse source/assets and third-party licenses before distribution.

## Gate

Spike overlay plus toast with 0/1/99+ actions; simulate network loss, Explorer restart, RDP, DPI, auto-hide, full-screen and theme changes. Measure idle resource use. Compare visually with Alert. Ship default only after acceptance; native docking optional only if verified on supported Windows builds. No claim current code or WinUI port has been executed.
