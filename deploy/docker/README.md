# Cordon Hive — Linux Docker Stack Deployment

`Hive.Server` targets a **standalone Linux server host** via Docker Stack (`docker stack deploy`) for P1–P7 LAN-only deployments, connecting over the private LAN to the existing PostgreSQL database. Managed endpoints (`Hive.Agent`, `Hive.Installer`, `Hive.App`) remain native Windows binaries.

**No Windows Active Directory admin access, domain join, SPN, or keytab is required.** Interactive user authentication uses self-contained **Local Hive Accounts (`LocalJwt`)** backed by PostgreSQL (PBKDF2-SHA512 + signed JWT + rotating refresh tokens), while Windows endpoints authenticate via mTLS client certificates.

## 1. Build the Linux Container Image

From the repository root:

```bash
docker build -f deploy/docker/Hive.Server.Dockerfile -t cordon-hive-server:local .
```

## 2. Provision Host Config & Docker Secrets

Never bake secrets, keys, or passwords into images or commit them to Git.

1. Place the internal CA certificate chain (used for mTLS trust) on the Linux host:
   - `/etc/cordon-hive/internal-ca.crt`
2. Create the required Docker secrets:

```bash
# Database connection string secret
printf '%s' 'Host=pg.lab.internal;Port=5432;Database=cordon_hive;Username=hive_app;Password=REPLACE_ME;SSL Mode=Require' \
  | docker secret create hive_db_connection_string -

# 256+ bit random HMAC signing key for JWTs
openssl rand -base64 48 | tr -d '\n' | docker secret create hive_jwt_signing_key -

# Initial bootstrap admin password (at least 12 chars; remove secret after first login & password change)
printf '%s' 'REPLACE_WITH_LONG_INITIAL_PASSPHRASE' | docker secret create hive_bootstrap_admin_password -

# 256-bit AES-GCM master key for P6 DFU break-glass workstation credential escrow (keep backed up separately from DB)
openssl rand -base64 32 | tr -d '\n' | docker secret create hive_vault_master_key -

# TLS certificate & private key (PEM) for Kestrel HTTPS/mTLS
docker secret create hive_tls_cert /path/to/hive-server.crt
docker secret create hive_tls_key /path/to/hive-server.key
```

> Note: `Hive.Server` runs as non-root `$APP_UID` (`1654`), with `read_only: true`, `cap_drop: [ALL]`, and `no-new-privileges:true`.

## 3. Deploy the Stack (LAN-Only)

Initialize Docker Swarm if not already active (`docker swarm init`), then deploy:

```bash
docker stack deploy -c deploy/docker/docker-stack.yml cordon-hive
docker service ls
docker service logs -f cordon-hive_hive-server
```

## 4. Verify Before Pilot

- Confirm `/health` returns `200 OK` internally.
- Confirm unauthenticated `/api/v1/me` and `/api/v1/workstations` return `401`.
- Sign in via `POST /api/v1/auth/login` with the bootstrap admin account and change its password immediately (`POST /api/v1/auth/change-password`).
- Confirm workstation clients cannot reach PostgreSQL TCP `5432` directly.
