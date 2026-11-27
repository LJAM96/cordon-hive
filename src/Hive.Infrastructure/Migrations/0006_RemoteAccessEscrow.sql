-- Cordon Hive Remote Access Escrow & Protected Mode Schema (PostgreSQL)

ALTER TABLE devices
    ADD COLUMN IF NOT EXISTS protected_mode_active BOOLEAN NOT NULL DEFAULT FALSE;

ALTER TABLE devices
    ADD COLUMN IF NOT EXISTS protected_mode_reason TEXT NULL;

CREATE TABLE IF NOT EXISTS device_credential_escrow (
    credential_id UUID PRIMARY KEY,
    device_id UUID NOT NULL UNIQUE REFERENCES devices(device_id) ON DELETE CASCADE,
    account_username TEXT NOT NULL,
    ciphertext_aes_gcm BYTEA NOT NULL,
    nonce BYTEA NOT NULL,
    tag BYTEA NOT NULL,
    key_version INTEGER NOT NULL DEFAULT 1,
    updated_by_sid TEXT NOT NULL,
    updated_at_utc TIMESTAMPTZ NOT NULL
);
