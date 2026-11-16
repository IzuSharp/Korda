// Generated from schema-v1.json; do not edit.
pub const SCHEMA_VERSION: usize = 1;
pub const WIRE_VERSION: usize = 1;
pub const HEADER_BYTES: usize = 96;
pub const DESCRIPTOR_BYTES: usize = 16;
pub const CELL_BYTES: usize = 24;
pub const CONTROL_HEADER_BYTES: usize = 16;
pub const CONTROL_ENTRY_BYTES: usize = 16;
pub const RELATION_CONTROL_KIND: usize = 23;
#[rustfmt::skip]
pub const MODEL_KEYS: &[&str] = &["version", "logical_id", "table_id", "schema", "table", "columns", "projection", "relations"];
#[rustfmt::skip]
pub const COLUMN_KEYS: &[&str] = &["id", "name", "codec", "codec_config", "nullable", "key_ordinal", "generated", "concurrency", "collation", "max_length"];
#[rustfmt::skip]
pub const PROJECTION_KEYS: &[&str] = &["version", "fields"];
#[rustfmt::skip]
pub const PROJECTION_FIELD_KEYS: &[&str] = &["ordinal", "name", "codec", "codec_config", "nullable"];
#[rustfmt::skip]
pub const RELATION_KEYS: &[&str] = &["version", "parent_model", "parent_table", "child_model", "child_table", "parent_columns", "child_columns", "child_nullable"];
