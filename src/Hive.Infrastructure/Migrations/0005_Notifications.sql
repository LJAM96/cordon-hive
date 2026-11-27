-- Cordon Hive Durable Notifications, Recipients, Deliveries & Idempotency Schema (PostgreSQL)

CREATE SEQUENCE IF NOT EXISTS notification_change_seq
    START WITH 1
    INCREMENT BY 1;

CREATE TABLE IF NOT EXISTS notifications (
    notification_id UUID PRIMARY KEY,
    source_id TEXT NOT NULL,
    notification_type TEXT NOT NULL,
    priority TEXT NOT NULL,
    title TEXT NOT NULL,
    safe_summary TEXT NOT NULL,
    target_type TEXT NOT NULL,
    created_by_sid TEXT NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL,
    due_at_utc TIMESTAMPTZ NULL,
    revision BIGINT NOT NULL DEFAULT 1,
    cancelled_at_utc TIMESTAMPTZ NULL
);

CREATE TABLE IF NOT EXISTS notification_recipients (
    notification_id UUID NOT NULL REFERENCES notifications(notification_id) ON DELETE CASCADE,
    recipient_key TEXT NOT NULL,
    change_seq BIGINT NOT NULL,
    delivered_at_utc TIMESTAMPTZ NULL,
    read_at_utc TIMESTAMPTZ NULL,
    acknowledged_at_utc TIMESTAMPTZ NULL,
    completed_at_utc TIMESTAMPTZ NULL,
    cancelled_at_utc TIMESTAMPTZ NULL,
    row_version BIGINT NOT NULL DEFAULT 1,
    PRIMARY KEY (notification_id, recipient_key)
);

CREATE INDEX IF NOT EXISTS ix_notification_recipients_key_seq
    ON notification_recipients (recipient_key, change_seq);

CREATE TABLE IF NOT EXISTS notification_deliveries (
    delivery_id UUID PRIMARY KEY,
    notification_id UUID NOT NULL,
    recipient_key TEXT NOT NULL,
    device_id UUID NULL,
    session_id INTEGER NULL,
    outcome TEXT NOT NULL,
    recorded_at_utc TIMESTAMPTZ NOT NULL,
    FOREIGN KEY (notification_id, recipient_key)
        REFERENCES notification_recipients(notification_id, recipient_key)
        ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS notification_idempotency (
    idempotency_key TEXT PRIMARY KEY,
    actor_sid TEXT NOT NULL,
    action TEXT NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL
);
