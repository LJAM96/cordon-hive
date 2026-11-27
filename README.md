# Cordon Hive

Proposed engineering documentation baseline | v0.2 | 27 November 2026

Cordon Hive is a Windows workstation management platform for forensic laboratory environments. It provides central visibility, controlled software compliance, remote access and persistent notifications without interrupting protected forensic activity. This is a design specification, not an implemented product or certification claim.

Initial deployment is LAN-only: the server-side platform (`Hive.Server` / `Hive.Worker`) runs on a **Linux server via a Docker Stack** (`deploy/docker/`) against the existing LAN PostgreSQL server (not currently on Tailscale), while managed endpoints and the user/supervisor UI remain native Windows applications. LAN RDP remains in scope; off-site Tailscale connectivity is deferred until P8, after LAN operational acceptance. PostgreSQL remains private to the API in every deployment.

Start with [the documentation index](docs/README_START_HERE.md). All decisions are proposed unless marked as a requirement. No production infrastructure or repositories are changed by this documentation.
