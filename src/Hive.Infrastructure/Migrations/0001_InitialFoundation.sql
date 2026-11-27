-- Cordon Hive Initial Foundation Schema (PostgreSQL)

CREATE TABLE IF NOT EXISTS schema_migrations (
    version TEXT PRIMARY KEY,
    applied_at_utc TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS devices (
    device_id UUID PRIMARY KEY,
    site_id TEXT NOT NULL,
    hostname TEXT NOT NULL,
    asset_number TEXT NULL,
    assigned_user_sid TEXT NULL,
    certificate_thumbprint TEXT NOT NULL,
    enrollment_state TEXT NOT NULL,
    enrolled_at_utc TIMESTAMPTZ NOT NULL,
    last_seen_utc TIMESTAMPTZ NULL,
    boot_id UUID NULL,
    agent_version TEXT NULL,
    policy_revision BIGINT NOT NULL DEFAULT 1,
    row_version BIGINT NOT NULL DEFAULT 1,
    CONSTRAINT uq_devices_certificate_thumbprint UNIQUE (certificate_thumbprint)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_devices_asset_number
    ON devices (asset_number)
    WHERE asset_number IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_devices_site_id
    ON devices (site_id);

CREATE INDEX IF NOT EXISTS ix_devices_assigned_user_sid
    ON devices (assigned_user_sid)
    WHERE assigned_user_sid IS NOT NULL;

CREATE TABLE IF NOT EXISTS device_assignments (
    assignment_id UUID PRIMARY KEY,
    device_id UUID NOT NULL REFERENCES devices(device_id) ON DELETE CASCADE,
    user_sid TEXT NULL,
    assigned_by_sid TEXT NOT NULL,
    valid_from_utc TIMESTAMPTZ NOT NULL,
    valid_to_utc TIMESTAMPTZ NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_device_assignments_active
    ON device_assignments (device_id)
    WHERE valid_to_utc IS NULL;

CREATE INDEX IF NOT EXISTS ix_device_assignments_device_valid_from
    ON device_assignments (device_id, valid_from_utc DESC);

CREATE TABLE IF NOT EXISTS device_sessions (
    device_id UUID NOT NULL REFERENCES devices(device_id) ON DELETE CASCADE,
    boot_id UUID NOT NULL,
    session_id INTEGER NOT NULL,
    user_sid TEXT NULL,
    session_state TEXT NOT NULL,
    observed_at_utc TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (device_id, boot_id, session_id)
);

CREATE TABLE IF NOT EXISTS enrollment_tokens (
    token_id UUID PRIMARY KEY,
    token_hash_sha256 TEXT NOT NULL,
    site_id TEXT NOT NULL,
    created_by_sid TEXT NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL,
    expires_at_utc TIMESTAMPTZ NOT NULL,
    redeemed_at_utc TIMESTAMPTZ NULL,
    redeemed_by_device_id UUID NULL REFERENCES devices(device_id),
    CONSTRAINT uq_enrollment_tokens_hash UNIQUE (token_hash_sha256)
);

CREATE TABLE IF NOT EXISTS outbox_events (
    event_id UUID PRIMARY KEY,
    event_type TEXT NOT NULL,
    resource_id UUID NOT NULL,
    revision BIGINT NOT NULL,
    site_id TEXT NULL,
    target_user_sid TEXT NULL,
    occurred_at_utc TIMESTAMPTZ NOT NULL,
    dispatched_at_utc TIMESTAMPTZ NULL
);

CREATE INDEX IF NOT EXISTS ix_outbox_events_pending
    ON outbox_events (occurred_at_utc ASC)
    WHERE dispatched_at_utc IS NULL;

CREATE TABLE IF NOT EXISTS audit_events (
    event_id UUID PRIMARY KEY,
    actor_type TEXT NOT NULL,
    actor_id TEXT NOT NULL,
    action TEXT NOT NULL,
    object_type TEXT NOT NULL,
    object_id TEXT NOT NULL,
    result TEXT NOT NULL,
    site_id TEXT NULL,
    correlation_id TEXT NOT NULL,
    occurred_at_utc TIMESTAMPTZ NOT NULL,
    summary_json TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_audit_events_occurred_at
    ON audit_events (occurred_at_utc DESC);

CREATE INDEX IF NOT EXISTS ix_audit_events_site_occurred
    ON audit_events (site_id, occurred_at_utc DESC);
