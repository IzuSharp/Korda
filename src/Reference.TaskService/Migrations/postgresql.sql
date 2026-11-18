CREATE TABLE operations (
    scope TEXT NOT NULL, request_key TEXT NOT NULL, request_hash TEXT NOT NULL,
    operation_id TEXT NOT NULL UNIQUE, tenant_id TEXT NOT NULL, principal_id TEXT NOT NULL,
    semantic TEXT NOT NULL, response_version BIGINT NOT NULL, response_status BIGINT NOT NULL,
    response_bytes BYTEA NOT NULL, completed_at BIGINT NOT NULL,
    PRIMARY KEY (scope, request_key)
);
CREATE INDEX operations_retention ON operations(completed_at, operation_id);
CREATE TABLE items (
    tenant_id TEXT NOT NULL, item_id TEXT NOT NULL, operation_id TEXT NOT NULL UNIQUE,
    title TEXT NOT NULL, created_at BIGINT NOT NULL, version BIGINT NOT NULL,
    PRIMARY KEY (tenant_id, item_id)
);
CREATE INDEX items_keyset ON items(tenant_id, created_at, item_id);
CREATE TABLE outbox (
    event_id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT NOT NULL, operation_id TEXT NOT NULL,
    payload_version BIGINT NOT NULL, payload BYTEA NOT NULL, delivery_state BIGINT NOT NULL,
    attempt_count BIGINT NOT NULL, available_at BIGINT NOT NULL, lease_until BIGINT NOT NULL,
    completed_at BIGINT NOT NULL
);
CREATE INDEX outbox_claim ON outbox(delivery_state, available_at, lease_until, event_id);
CREATE INDEX outbox_operation ON outbox(operation_id);
CREATE TABLE attachments (
    tenant_id TEXT NOT NULL, item_id TEXT NOT NULL, content_hash TEXT NOT NULL,
    content_length BIGINT NOT NULL, operation_id TEXT NOT NULL, version BIGINT NOT NULL,
    PRIMARY KEY (tenant_id, item_id),
    FOREIGN KEY (tenant_id, item_id) REFERENCES items(tenant_id, item_id)
);
CREATE INDEX attachments_hash ON attachments(content_hash);
