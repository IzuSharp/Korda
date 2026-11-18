CREATE TABLE receipts (
    event_id TEXT NOT NULL PRIMARY KEY, payload_hash TEXT NOT NULL,
    delivery_attempts BIGINT NOT NULL, effects BIGINT NOT NULL CHECK(effects = 1)
);
