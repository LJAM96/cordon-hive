-- Cordon Hive Safe Software Deployment Packages, Signed Manifests, Jobs, Targets & Attempts Schema (PostgreSQL)

CREATE TABLE IF NOT EXISTS packages (
    package_id UUID PRIMARY KEY,
    file_name TEXT NOT NULL,
    sha256 TEXT NOT NULL,
    size_bytes BIGINT NOT NULL,
    storage_path TEXT NOT NULL,
    uploaded_by_sid TEXT NOT NULL,
    uploaded_at_utc TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_packages_sha256
    ON packages (sha256);

CREATE INDEX IF NOT EXISTS ix_packages_uploaded_at
    ON packages (uploaded_at_utc DESC);

CREATE TABLE IF NOT EXISTS release_manifests (
    release_id UUID PRIMARY KEY REFERENCES software_releases(release_id) ON DELETE CASCADE,
    product_id UUID NOT NULL REFERENCES software_products(product_id) ON DELETE CASCADE,
    package_sha256 TEXT NOT NULL,
    key_id TEXT NOT NULL,
    algorithm TEXT NOT NULL,
    public_key_pem TEXT NOT NULL,
    manifest_canonical_json TEXT NOT NULL,
    manifest_json JSONB NOT NULL,
    signature_base64 TEXT NOT NULL,
    issued_at_utc TIMESTAMPTZ NOT NULL,
    expires_at_utc TIMESTAMPTZ NOT NULL,
    signed_by_sid TEXT NOT NULL,
    signed_at_utc TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_release_manifests_product_id
    ON release_manifests (product_id);

CREATE TABLE IF NOT EXISTS deployment_jobs (
    job_id UUID PRIMARY KEY,
    site_id TEXT NOT NULL,
    release_id UUID NOT NULL REFERENCES software_releases(release_id),
    product_name TEXT NOT NULL,
    version TEXT NOT NULL,
    policy TEXT NOT NULL,
    created_by_sid TEXT NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_deployment_jobs_site_created
    ON deployment_jobs (site_id, created_at_utc DESC);

CREATE INDEX IF NOT EXISTS ix_deployment_jobs_release_id
    ON deployment_jobs (release_id);

CREATE TABLE IF NOT EXISTS deployment_targets (
    target_id UUID PRIMARY KEY,
    job_id UUID NOT NULL REFERENCES deployment_jobs(job_id) ON DELETE CASCADE,
    device_id UUID NOT NULL REFERENCES devices(device_id) ON DELETE CASCADE,
    state TEXT NOT NULL,
    attempt_count INTEGER NOT NULL DEFAULT 0,
    active_lease_id UUID NULL,
    lease_expires_utc TIMESTAMPTZ NULL,
    last_reason_code TEXT NULL,
    last_message TEXT NULL,
    deferred_until_utc TIMESTAMPTZ NULL,
    updated_at_utc TIMESTAMPTZ NOT NULL,
    CONSTRAINT uq_deployment_targets_job_device UNIQUE (job_id, device_id)
);

CREATE INDEX IF NOT EXISTS ix_deployment_targets_job_id
    ON deployment_targets (job_id);

CREATE INDEX IF NOT EXISTS ix_deployment_targets_device_state
    ON deployment_targets (device_id, state);

CREATE INDEX IF NOT EXISTS ix_deployment_targets_active_lease
    ON deployment_targets (device_id, active_lease_id)
    WHERE active_lease_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS deployment_attempts (
    attempt_id UUID PRIMARY KEY,
    target_id UUID NOT NULL REFERENCES deployment_targets(target_id) ON DELETE CASCADE,
    attempt_number INTEGER NOT NULL,
    resulting_state TEXT NOT NULL,
    reason_code TEXT NOT NULL,
    message TEXT NULL,
    exit_code INTEGER NULL,
    occurred_at_utc TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_deployment_attempts_target_id
    ON deployment_attempts (target_id, occurred_at_utc ASC);
