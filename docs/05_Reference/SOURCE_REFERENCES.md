# Source references

Reviewed 27 November 2026. Internal repository source is pinned for reproducibility. Public technical documentation supports the design; exact package versions and Windows behaviours must be revalidated during implementation. Links are references, not dependencies to download into an isolated environment.

## Cordon Alert source

Private repository: [LJAM96/cordon-alert](https://github.com/LJAM96/cordon-alert). Reviewed commit [86886209e9ae8eb17b7f1559507580bc7c85e783](https://github.com/LJAM96/cordon-alert/commit/86886209e9ae8eb17b7f1559507580bc7c85e783).

* [Project configuration](https://github.com/LJAM96/cordon-alert/blob/86886209e9ae8eb17b7f1559507580bc7c85e783/CordonAlert/CordonAlert.csproj) — target framework, Avalonia and dependencies.
* [TaskbarInfo.cs](https://github.com/LJAM96/cordon-alert/blob/86886209e9ae8eb17b7f1559507580bc7c85e783/CordonAlert/TaskbarInfo.cs) — native interop, geometry, docking.
* [WidgetWindow.cs](https://github.com/LJAM96/cordon-alert/blob/86886209e9ae8eb17b7f1559507580bc7c85e783/CordonAlert/WidgetWindow.cs) — badge, window clipping and expansion.
* [App.cs](https://github.com/LJAM96/cordon-alert/blob/86886209e9ae8eb17b7f1559507580bc7c85e783/CordonAlert/App.cs) — lifecycle, polling, acknowledgment.
* [StartupRegistration.cs](https://github.com/LJAM96/cordon-alert/blob/86886209e9ae8eb17b7f1559507580bc7c85e783/CordonAlert/StartupRegistration.cs) and [Program.cs](https://github.com/LJAM96/cordon-alert/blob/86886209e9ae8eb17b7f1559507580bc7c85e783/CordonAlert/Program.cs) — startup, process lifecycle.

## Verified public technical references

| Source | Relevance |
|---|---|
| [ASP.NET Core Windows Authentication (.NET 10)](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/windowsauth?view=aspnetcore-10.0) | Negotiate/Kerberos and hosting constraints |
| [SignalR authentication and authorisation](https://learn.microsoft.com/aspnet/core/signalr/authn-and-authz) | Identity and hub authorisation |
| [Windows App SDK app notifications quickstart](https://learn.microsoft.com/en-gb/windows/apps/windows-app-sdk/notifications/app-notifications/app-notifications-quickstart?tabs=cs) | Toast activation; elevated app limitation |
| [SetParent](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setparent) | Cross-process parent and DPI risks |
| [High DPI desktop development](https://learn.microsoft.com/en-gb/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows) | DPI-aware coordinate handling |
| [Restart Manager](https://learn.microsoft.com/en-us/windows/win32/rstmgr/about-restart-manager) | Installer interactions and file-in-use checks |
| [Tailscale unattended mode](https://tailscale.com/docs/how-to/run-unattended) | Windows node access after reboot/logoff |
| [Tailscale RDP](https://tailscale.com/docs/solutions/access-remote-desktops-using-windows-rdp) | Private remote access |
| [Tailscale subnet routers](https://tailscale.com/docs/features/subnet-routers) | Scoped access to internal services |
| [Tailscale grants](https://tailscale.com/docs/features/access-control/grants) | Network authorisation |
| [Tailscale client preferences](https://tailscale.com/docs/features/client/manage-preferences?tab=windows) | DNS and routing settings |

## Additional verification before coding

Confirm current Microsoft documentation for Windows LAPS, Credential Guard and Remote Credential Guard, Win32_Product inventory side effects, AppWindow/Mica, Windows service identity, named pipe ACLs, installer reboot controls and .NET/Windows App SDK compatibility. For deferred P8 only, review Tailscale licensing, coordination/relay model and organisational security documentation. Check third-party dependency licenses and offline redistribution rights. Do not assume private repo access or existing code confers permission to redistribute third-party assets.
