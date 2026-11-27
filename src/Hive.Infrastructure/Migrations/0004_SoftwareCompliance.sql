-- Cordon Hive Software Catalogue, Releases, Exemptions & Compliance Inventory Schema (PostgreSQL)

CREATE TABLE IF NOT EXISTS software_products (
    product_id UUID PRIMARY KEY,
    name TEXT NOT NULL,
    publisher TEXT NOT NULL,
    version_scheme TEXT NOT NULL,
    match_pattern TEXT NOT NULL,
    detection_revision BIGINT NOT NULL DEFAULT 1,
    protected_process_names_json JSONB NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS software_releases (
    release_id UUID PRIMARY KEY,
    product_id UUID NOT NULL REFERENCES software_products(product_id) ON DELETE CASCADE,
    version_raw TEXT NOT NULL,
    version_scheme TEXT NOT NULL,
    approval_state TEXT NOT NULL,
    validation_reference TEXT NOT NULL,
    package_sha256 TEXT NULL,
    manifest_digest TEXT NULL,
    created_by_sid TEXT NOT NULL,
    approved_by_sid TEXT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL,
    approved_at_utc TIMESTAMPTZ NULL,
    withdrawn_at_utc TIMESTAMPTZ NULL,
    withdrawn_reason TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_software_releases_product_id
    ON software_releases (product_id);

CREATE TABLE IF NOT EXISTS software_exemptions (
    exemption_id UUID PRIMARY KEY,
    device_id UUID NULL REFERENCES devices(device_id) ON DELETE CASCADE,
    product_id UUID NOT NULL REFERENCES software_products(product_id) ON DELETE CASCADE,
    allowed_version_raw TEXT NULL,
    reason TEXT NOT NULL,
    approved_by_sid TEXT NOT NULL,
    starts_at_utc TIMESTAMPTZ NOT NULL,
    expires_at_utc TIMESTAMPTZ NOT NULL,
    revoked_at_utc TIMESTAMPTZ NULL,
    revoked_by_sid TEXT NULL,
    revoked_reason TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_software_exemptions_product_id
    ON software_exemptions (product_id);

CREATE TABLE IF NOT EXISTS device_software_snapshots (
    device_id UUID PRIMARY KEY REFERENCES devices(device_id) ON DELETE CASCADE,
    detection_revision BIGINT NOT NULL,
    is_complete BOOLEAN NOT NULL,
    captured_at_utc TIMESTAMPTZ NOT NULL,
    received_at_utc TIMESTAMPTZ NOT NULL,
    items_json JSONB NOT NULL
);
