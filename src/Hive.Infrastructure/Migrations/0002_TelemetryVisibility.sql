-- Cordon Hive Telemetry & Visibility Schema (PostgreSQL)

CREATE TABLE IF NOT EXISTS device_metrics (
    device_id UUID NOT NULL REFERENCES devices(device_id) ON DELETE CASCADE,
    boot_id UUID NOT NULL,
    sample_sequence BIGINT NOT NULL,
    sampled_at_utc TIMESTAMPTZ NOT NULL,
    received_at_utc TIMESTAMPTZ NOT NULL,
    cpu_percent DOUBLE PRECISION NULL,
    memory_used_percent DOUBLE PRECISION NULL,
    memory_used_bytes BIGINT NULL,
    memory_total_bytes BIGINT NULL,
    gpu_percent DOUBLE PRECISION NULL,
    disk_active_percent DOUBLE PRECISION NULL,
    PRIMARY KEY (device_id, boot_id, sample_sequence)
);

CREATE INDEX IF NOT EXISTS ix_device_metrics_device_sampled
    ON device_metrics (device_id, sampled_at_utc DESC);

CREATE TABLE IF NOT EXISTS device_volumes (
    device_id UUID NOT NULL REFERENCES devices(device_id) ON DELETE CASCADE,
    volume_id TEXT NOT NULL,
    drive_type TEXT NOT NULL,
    total_bytes BIGINT NOT NULL,
    free_bytes BIGINT NOT NULL,
    observed_at_utc TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (device_id, volume_id)
);

CREATE TABLE IF NOT EXISTS device_process_snapshots (
    snapshot_id UUID PRIMARY KEY,
    device_id UUID NOT NULL REFERENCES devices(device_id) ON DELETE CASCADE,
    sampled_at_utc TIMESTAMPTZ NOT NULL,
    processes_json JSONB NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_device_process_snapshots_device_sampled
    ON device_process_snapshots (device_id, sampled_at_utc DESC);
