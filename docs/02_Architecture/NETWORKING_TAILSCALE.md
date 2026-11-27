# Network and Tailscale design

## Release boundary

P1–P7 are LAN-only. The server-side stack (`Hive.Server`) runs on a **Linux server via a Docker Stack**, publishing only HTTPS/TLS (`443 -> 8443`) on the approved LAN interface and reaching the existing LAN PostgreSQL server over private LAN networking. The existing PostgreSQL server is not currently on Tailscale. All central features use the internal API over the LAN; an off-site agent without an approved route reports disconnected and retains local state. No remote access workaround, public port forwarding, Tailscale enrollment, subnet router or external control plane is required for the LAN release. The hybrid design below is retained solely for P8 and is not an initial implementation requirement.

In P8, preferably connect the Linux Docker host (not PostgreSQL) to the approved tailnet while it retains private LAN connectivity to PostgreSQL. If the API stack is separate from the database LAN, an approved backend route must be established first. Validate DNS, certificates, SPNs, firewall and end-to-end API/database reachability before remote rollout. Never grant workstation clients direct PostgreSQL access or expose PostgreSQL via Docker Swarm ingress ports.

## Modes

| Mode | Transport | Constraints |
|---|---|---|
| LAN | Internal HTTPS, AD DNS, local RDP | No Tailscale required |
| Hybrid (P8 only) | Approved organisational tailnet plus LAN | External control plane, possible DERP relay; policy approval required |
| Isolated | Separate internal network and deployment instance | No externally connected node, subnet router or shared gateway bridging evidence network |

Prefer one canonical HTTPS service hostname and SPN, with approved split DNS/routing resolving the same service over LAN or tailnet. Do not silently switch to a raw IP/alternate name that breaks Kerberos or TLS. Probe only configured endpoints with bounded backoff. Record active path, last successful contact and reachability. Agent initiates outbound connection; no inbound agent listener. Tailscale is optional independently managed software, not a bundled identity system.

## Access matrix

| Source | Destination | Port/protocol | Rule |
|---|---|---|---|
| Managed device | Hive API/package endpoint | TCP 443 | Allow scoped device access |
| User/supervisor workstation | API | TCP 443 | Allow; AD/application permissions still required |
| Approved supervisor endpoints | Approved managed devices | TCP 3389 (UDP optional after testing) | Allow only RDP-authorised group/asset scope, Windows firewall + NLA |
| Remote domain member | Approved AD/DNS endpoints | Organisation-defined AD ports | Only if necessary, route narrowly and test Kerberos/DNS/time |
| Server | PostgreSQL | TCP 5432 | Private server network only |
| Ordinary endpoint | Other endpoint or DB | Any | Deny by default |

For P8 only, use tailnet grants with explicit tagged device groups and approved supervisor identities. User-owned supervisor endpoint identity versus tagged machine identity must be designed deliberately: a device tag is not proof of interactive user membership. Remove default broad grants; test both permitted and prohibited paths. Tailnet policy does not govern direct LAN traffic; enforce equivalent Windows firewall/network controls. Restrict device enrollment, key expiry/rotation, posture and admin access. No Funnel, public port forward, broad subnet advertisement or exit-node dependency. A subnet router for internal AD requires explicit assessment of routes, DNS, firewall, redundancy and audit.

## Windows endpoint configuration

In P8 only, IT installs supported Tailscale client through managed deployment; enable unattended mode where approved and verify reachability before login after reboot. Test account ownership, device reauthentication, node expiry and user switching. Use device inventory to record tailnet DNS/IP as reachability hints, not credentials or immutable identity. RDP uses approved hostname, TLS certificate validation and NLA; no automatic acceptance of certificate warnings. Route selection is a convenience, not permission bypass.

## Remote resilience

In P8, a remote device may be behind NAT, on metered links or via DERP. Support resumable HTTPS downloads (Range + immutable ETag), bandwidth limit, configurable metered-network policy, staged verification and backoff. Cache only authorised content; don't fetch massive packages on cellular by default. Telemetry coalesces and caps queue; notification/deployment events get durable priority. Test VPN loss, captive portal, sleep, reconnect, network switching and DNS failures. Do not indicate online solely from Tailscale status; use fresh authenticated agent heartbeat.

## Approval gate

P8 requires P7 LAN acceptance and separate infrastructure/security approval. Standard Tailscale depends on an external coordination service, and relay use is possible despite encrypted device traffic. It is not a fully offline transport. An isolated instance remains separate. Determine approved tailnet tenant, licensing, control-plane data handling, topology, firewall ownership and domain connectivity with IT. Self-hosted alternatives are not assumed equivalent or approved; evaluate separately if required.
