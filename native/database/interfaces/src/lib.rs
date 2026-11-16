//! Canonical data contracts shared by Rust database adapters.
//!
//! This crate owns metadata and value transport only. It starts no runtime, opens
//! no database, renders no SQL, and exports no Rust allocation through a C ABI.

pub mod schema;
pub mod wire;

use serde_json::Value;
use sha2::{Digest, Sha256};
use std::collections::{HashMap, HashSet};
use std::sync::Arc;

/// A deterministic validation failure before registration or database traffic.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ContractError(pub &'static str);
/// A contract operation that either returns a complete value or no new owner.
pub type Result<T> = std::result::Result<T, ContractError>;
/// Maximum encoded metadata/value bytes in the ordinary contract fixture profile.
pub const MAX_BYTES: usize = 4 * 1024 * 1024;
/// Maximum schema nodes in the ordinary bounded profile.
pub const MAX_NODES: usize = 4096;

/// Ordinary provider semantic profile, separate from the public CLR enum ordinal.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Profile {
    /// SQLite's ordinary profile.
    Sqlite = 1,
    /// PostgreSQL's ordinary profile.
    PostgreSql = 2,
}
impl TryFrom<u32> for Profile {
    type Error = ContractError;
    fn try_from(value: u32) -> Result<Self> {
        match value {
            1 => Ok(Self::Sqlite),
            2 => Ok(Self::PostgreSql),
            _ => Err(ContractError("unknown semantic profile")),
        }
    }
}

/// A logical scalar's exact immutable identity; configured native handles remain source-owned.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Codec {
    /// Logical ID from the fixed scalar registry.
    pub id: u32,
    /// Canonical JSON configuration, absent for unconfigured codecs.
    pub configuration: Option<String>,
    /// SQL NULL permission; it does not change the logical scalar ID.
    pub nullable: bool,
}
impl Codec {
    /// Reject unknown, irrelevant, malformed or unsupported required scalar configuration.
    pub fn validate(&self, profile: Profile) -> Result<()> {
        if !(1..=17).contains(&self.id) {
            return Err(ContractError("unknown required codec"));
        }
        match self.id {
            13 => {
                if profile == Profile::Sqlite {
                    return Err(ContractError(
                        "numeric decimal unsupported on ordinary SQLite",
                    ));
                }
                let value: Value = serde_json::from_str(
                    self.configuration
                        .as_deref()
                        .ok_or(ContractError("missing decimal configuration"))?,
                )
                .map_err(|_| ContractError("invalid decimal configuration"))?;
                keys(&value, &["precision", "scale"])?;
                let p = number(&value, "precision")?;
                let s = number(&value, "scale")?;
                if !(1..=29).contains(&p)
                    || s > 28
                    || s > p
                    || self.configuration.as_deref()
                        != Some(&format!("{{\"precision\":{p},\"scale\":{s}}}"))
                {
                    return Err(ContractError("invalid decimal configuration"));
                }
            }
            15 => {
                let value: Value = serde_json::from_str(
                    self.configuration
                        .as_deref()
                        .ok_or(ContractError("missing money configuration"))?,
                )
                .map_err(|_| ContractError("invalid money configuration"))?;
                keys(&value, &["scale"])?;
                let s = number(&value, "scale")?;
                if s > 18 || self.configuration.as_deref() != Some(&format!("{{\"scale\":{s}}}")) {
                    return Err(ContractError("invalid money configuration"));
                }
            }
            _ if self.configuration.is_some() => {
                return Err(ContractError("irrelevant codec configuration"));
            }
            _ => {}
        }
        Ok(())
    }
    /// Tests underlying scalar identity independently from optional FK nullability.
    pub fn same_scalar(&self, other: &Self) -> bool {
        self.id == other.id && self.configuration == other.configuration
    }
}

/// One immutable canonical column.
#[derive(Debug, Clone)]
pub struct Column {
    /// Stable SHA-derived ID.
    pub id: u32,
    /// Exact store name.
    pub name: String,
    /// Logical scalar identity.
    pub codec: Codec,
    /// Primary-key ordinal, or no key membership.
    pub key_ordinal: Option<u32>,
    /// Database-generated insert value.
    pub generated: bool,
    /// Explicit concurrency token.
    pub concurrency: bool,
    /// Exact declared collation, or none.
    pub collation: Option<String>,
    /// Positive byte capacity, or none.
    pub max_length: Option<u32>,
}

/// A validated owned model; native registration never borrows the caller's input.
#[derive(Debug, Clone)]
pub struct Model {
    /// Assembly-simple-name plus exact closed CLR FullName.
    pub(crate) logical_id: String,
    /// Stable SHA-derived table ID.
    pub(crate) table_id: u32,
    /// Explicit schema, or none.
    pub(crate) schema: Option<String>,
    /// Exact store table name.
    pub(crate) table: String,
    /// Columns sorted by stable ID.
    pub(crate) columns: Vec<Column>,
    /// Column IDs in constructor/projection order.
    pub(crate) projection: Vec<u32>,
    /// Complete canonical model bytes copied on parsing.
    pub(crate) canonical: Vec<u8>,
    /// SHA-256 of canonical model content.
    pub(crate) hash: [u8; 32],
}
impl Model {
    /// Returns the immutable canonical model bytes.
    pub fn bytes(&self) -> &[u8] {
        &self.canonical
    }
    /// Returns the exact complete model hash.
    pub fn hash(&self) -> [u8; 32] {
        self.hash
    }
    /// Returns the stable table identity.
    pub fn table_id(&self) -> u32 {
        self.table_id
    }
    /// Returns the validated columns without granting mutation.
    pub fn columns(&self) -> &[Column] {
        &self.columns
    }

    /// Validates all schema fields and canonical bytes before creating an owned model.
    pub fn parse(bytes: &[u8], profile: Profile) -> Result<Self> {
        if bytes.len() > MAX_BYTES {
            return Err(ContractError("metadata byte limit"));
        }
        let value: Value =
            serde_json::from_slice(bytes).map_err(|_| ContractError("invalid metadata JSON"))?;
        keys(&value, schema::MODEL_KEYS)?;
        if number(&value, "version")? != 1 {
            return Err(ContractError("unsupported metadata schema"));
        }
        let logical_id = name(&value, "logical_id")?.to_owned();
        let table = name(&value, "table")?.to_owned();
        if !logical_id.contains(':') {
            return Err(ContractError("invalid logical identity"));
        }
        let table_schema = optional_name(&value, "schema")?;
        let table_id = u32number(&value, "table_id")?;
        if table_id == 0
            || table_id
                != stable_id(&[
                    Some("NWDB-TABLE-ID-v1"),
                    Some(&logical_id),
                    table_schema.as_deref(),
                    Some(&table),
                ])
        {
            return Err(ContractError("table identity mismatch"));
        }
        let column_values = array(&value, "columns")?;
        if column_values.is_empty() || column_values.len() > MAX_NODES {
            return Err(ContractError("invalid column count"));
        }
        let mut columns = Vec::with_capacity(column_values.len());
        let mut names = HashSet::new();
        let mut ids = HashSet::new();
        for item in column_values {
            keys(item, schema::COLUMN_KEYS)?;
            let column_name = name(item, "name")?.to_owned();
            let id = u32number(item, "id")?;
            let policy_name = if profile == Profile::Sqlite {
                column_name.to_ascii_lowercase()
            } else {
                column_name.clone()
            };
            if !names.insert(policy_name)
                || !ids.insert(id)
                || id == 0
                || id
                    != stable_id(&[
                        Some("NWDB-COLUMN-ID-v1"),
                        Some(&logical_id),
                        table_schema.as_deref(),
                        Some(&table),
                        Some(&column_name),
                    ])
            {
                return Err(ContractError("duplicate or mismatched column identity"));
            }
            let codec = codec(item)?;
            codec.validate(profile)?;
            let key_ordinal = optional_number(item, "key_ordinal")?;
            if key_ordinal.is_some() && codec.nullable {
                return Err(ContractError("nullable primary key"));
            }
            let max_length = optional_number(item, "max_length")?;
            if max_length == Some(0) {
                return Err(ContractError("zero byte capacity"));
            }
            columns.push(Column {
                id,
                name: column_name,
                codec,
                key_ordinal,
                generated: boolean(item, "generated")?,
                concurrency: boolean(item, "concurrency")?,
                collation: optional_name(item, "collation")?,
                max_length,
            });
        }
        let projection = u32array(&value, "projection")?;
        if projection.len() != columns.len()
            || projection.iter().copied().collect::<HashSet<_>>() != ids
        {
            return Err(ContractError(
                "projection must cover every column exactly once",
            ));
        }
        if !array(&value, "relations")?.is_empty() {
            return Err(ContractError("v1 model relations must remain empty"));
        }
        let mut expected_key = 0;
        for id in &projection {
            let column = columns
                .iter()
                .find(|c| c.id == *id)
                .ok_or(ContractError("missing projection column"))?;
            if let Some(ordinal) = column.key_ordinal {
                if ordinal != expected_key {
                    return Err(ContractError("noncontiguous key declaration order"));
                }
                expected_key += 1;
            }
        }
        columns.sort_by_key(|c| c.id);
        let mut model = Model {
            logical_id,
            table_id,
            schema: table_schema,
            table,
            columns,
            projection,
            canonical: Vec::new(),
            hash: [0; 32],
        };
        model.canonical = model.encode();
        if model.canonical != bytes {
            return Err(ContractError("noncanonical or duplicate model property"));
        }
        model.hash = sha(&model.canonical);
        Ok(model)
    }
    /// Independently encodes the exact canonical JSON without a general reflection serializer.
    pub fn encode(&self) -> Vec<u8> {
        let columns = self.columns.iter().map(|c| format!("{{\"id\":{},\"name\":{},\"codec\":{},\"codec_config\":{},\"nullable\":{},\"key_ordinal\":{},\"generated\":{},\"concurrency\":{},\"collation\":{},\"max_length\":{}}}", c.id, quote(&c.name), c.codec.id, config_text(&c.codec), c.codec.nullable, optional_u32(c.key_ordinal), c.generated, c.concurrency, optional_string(c.collation.as_deref()), optional_u32(c.max_length))).collect::<Vec<_>>().join(",");
        format!("{{\"version\":1,\"logical_id\":{},\"table_id\":{},\"schema\":{},\"table\":{},\"columns\":[{}],\"projection\":[{}],\"relations\":[]}}", quote(&self.logical_id), self.table_id, optional_string(self.schema.as_deref()), quote(&self.table), columns, numbers(&self.projection)).into_bytes()
    }
    /// Returns key components in contiguous declaration order.
    pub fn keys(&self) -> Vec<&Column> {
        let mut keys = self
            .columns
            .iter()
            .filter(|c| c.key_ordinal.is_some())
            .collect::<Vec<_>>();
        keys.sort_by_key(|c| c.key_ordinal);
        keys
    }
    /// Resolves one declared ID without arbitrary schema inference.
    pub fn column(&self, id: u32) -> Result<&Column> {
        self.columns
            .iter()
            .find(|c| c.id == id)
            .ok_or(ContractError("unknown column"))
    }
}

/// One exact result field in its declared ordinal.
#[derive(Debug, Clone)]
pub struct ResultField {
    /// Exact alias.
    pub name: String,
    /// Logical configured scalar identity.
    pub codec: Codec,
}
/// A validated immutable logical projection; no managed materializer pointer is retained.
#[derive(Debug, Clone)]
pub struct Projection {
    /// Ordered result fields.
    pub(crate) fields: Vec<ResultField>,
    /// Canonical owned bytes.
    pub(crate) canonical: Vec<u8>,
    /// Exact logical shape hash.
    pub(crate) hash: [u8; 32],
}
impl Projection {
    /// Returns the immutable canonical shape.
    pub fn bytes(&self) -> &[u8] {
        &self.canonical
    }
    /// Returns the complete logical shape hash.
    pub fn hash(&self) -> [u8; 32] {
        self.hash
    }
    /// Returns the validated ordered fields without granting mutation.
    pub fn fields(&self) -> &[ResultField] {
        &self.fields
    }

    /// Validates a projection before any row can be interpreted as NULL.
    pub fn parse(bytes: &[u8], profile: Profile) -> Result<Self> {
        Self::parse_context(bytes, profile, false)
    }
    /// Validates the scalar-only single-field empty-alias sentinel.
    /// The admitted scalar operation must additionally require exactly one result row.
    pub fn parse_scalar(bytes: &[u8], profile: Profile) -> Result<Self> {
        Self::parse_context(bytes, profile, true)
    }
    fn parse_context(bytes: &[u8], profile: Profile, scalar: bool) -> Result<Self> {
        if bytes.len() > MAX_BYTES {
            return Err(ContractError("projection byte limit"));
        }
        let value: Value =
            serde_json::from_slice(bytes).map_err(|_| ContractError("invalid projection JSON"))?;
        keys(&value, schema::PROJECTION_KEYS)?;
        if number(&value, "version")? != 1 {
            return Err(ContractError("unsupported projection version"));
        }
        let values = array(&value, "fields")?;
        if values.is_empty() || values.len() > MAX_NODES || scalar && values.len() != 1 {
            return Err(ContractError("invalid projection count"));
        }
        let mut names = HashSet::new();
        let mut fields = Vec::with_capacity(values.len());
        for (ordinal, field) in values.iter().enumerate() {
            keys(field, schema::PROJECTION_FIELD_KEYS)?;
            if number(field, "ordinal")? != ordinal as u64 {
                return Err(ContractError("projection ordinal mismatch"));
            }
            let field_name = if scalar {
                if field["name"].as_str() != Some("") {
                    return Err(ContractError(
                        "scalar shape requires the empty-alias sentinel",
                    ));
                }
                String::new()
            } else {
                name(field, "name")?.to_owned()
            };
            if !names.insert(field_name.clone()) {
                return Err(ContractError("duplicate result alias"));
            }
            let codec = codec(field)?;
            codec.validate(profile)?;
            fields.push(ResultField {
                name: field_name,
                codec,
            });
        }
        let mut result = Self {
            fields,
            canonical: Vec::new(),
            hash: [0; 32],
        };
        result.canonical = result.encode();
        if result.canonical != bytes {
            return Err(ContractError("noncanonical projection"));
        }
        result.hash = sha(bytes);
        Ok(result)
    }
    /// Emits the complete logical shape with exact key order and string rules.
    pub fn encode(&self) -> Vec<u8> {
        let fields = self.fields.iter().enumerate().map(|(i, f)| format!("{{\"ordinal\":{i},\"name\":{},\"codec\":{},\"codec_config\":{},\"nullable\":{}}}", quote(&f.name), f.codec.id, config_text(&f.codec), f.codec.nullable)).collect::<Vec<_>>().join(",");
        format!("{{\"version\":1,\"fields\":[{fields}]}}").into_bytes()
    }
}

/// A finite source-local model registry with atomic validate/copy/retain semantics.
pub struct MetadataRegistry {
    profile: Profile,
    max_entries: usize,
    max_bytes: usize,
    bytes: usize,
    models: HashMap<[u8; 32], Arc<Model>>,
    table_ids: HashMap<u32, String>,
    column_ids: HashMap<u32, String>,
}
impl MetadataRegistry {
    /// Creates a bounded registry without a database, runtime or external effects.
    pub fn new(profile: Profile, max_entries: usize, max_bytes: usize) -> Result<Self> {
        if max_entries == 0 || max_bytes == 0 || max_entries > MAX_NODES || max_bytes > MAX_BYTES {
            return Err(ContractError("invalid registry limits"));
        }
        Ok(Self {
            profile,
            max_entries,
            max_bytes,
            bytes: 0,
            models: HashMap::new(),
            table_ids: HashMap::new(),
            column_ids: HashMap::new(),
        })
    }
    /// Validates and copies metadata before publication; failed registration changes no entries or byte count.
    pub fn register(&mut self, bytes: &[u8], expected_hash: [u8; 32]) -> Result<Arc<Model>> {
        let model = Model::parse(bytes, self.profile)?;
        if model.hash != expected_hash {
            return Err(ContractError("metadata content hash mismatch"));
        }
        if let Some(existing) = self.models.get(&model.hash) {
            if existing.canonical != bytes {
                return Err(ContractError("metadata hash collision"));
            }
            return Ok(Arc::clone(existing));
        }
        if self.models.len() >= self.max_entries
            || model.canonical.len() > self.max_bytes.saturating_sub(self.bytes)
        {
            return Err(ContractError("metadata registry budget"));
        }
        let table_identity = identity(&[
            Some(&model.logical_id),
            model.schema.as_deref(),
            Some(&model.table),
        ]);
        if self
            .table_ids
            .get(&model.table_id)
            .is_some_and(|old| old != &table_identity)
        {
            return Err(ContractError("table ID collision"));
        }
        let mut identities = Vec::with_capacity(model.columns.len());
        for c in &model.columns {
            let column_identity = identity(&[
                Some(&model.logical_id),
                model.schema.as_deref(),
                Some(&model.table),
                Some(&c.name),
            ]);
            if self
                .column_ids
                .get(&c.id)
                .is_some_and(|old| old != &column_identity)
            {
                return Err(ContractError("column ID collision"));
            }
            identities.push((c.id, column_identity));
        }
        let model = Arc::new(model);
        self.bytes += model.canonical.len();
        self.table_ids.insert(model.table_id, table_identity);
        self.column_ids.extend(identities);
        self.models.insert(model.hash, Arc::clone(&model));
        Ok(model)
    }
    /// Returns independently observable entry and retained canonical-byte counts.
    pub fn counts(&self) -> (usize, usize) {
        (self.models.len(), self.bytes)
    }
}

/// Native protocol outcome, independent from connection disposition and local retirement.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TransactionOutcome {
    /// No confirmed terminal outcome.
    InProgress,
    /// Commit confirmed.
    Committed,
    /// Rollback confirmed.
    RolledBack,
    /// A commit may have occurred; recovery is required.
    Unknown,
}
/// Physical protocol reuse policy; local owner release never establishes reusability.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ConnectionDisposition {
    /// Confirmed usable.
    Usable,
    /// Reset has not completed.
    ProviderResetPending,
    /// Isolated until recovery proves safety.
    Quarantined,
    /// Physical connection must not be reused.
    Discarded,
}
/// Local owner's independent memory-access state.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LocalLifetime {
    /// Business admission remains open.
    Open,
    /// Business admission closed; cleanup may continue.
    Closing,
    /// Native callbacks/buffers are quiescent.
    NativeRetired,
    /// Both native and managed roots are retired.
    Released,
}
/// Engine-internal physical session admission; providers implement business and cleanup admission separately.
pub trait SessionAdmission {
    /// One command/cursor/transaction call at a time; failure does not queue or partially admit work.
    fn try_admit_command(&self) -> Result<()>;
    /// Cleanup control is separate from canceled business admission and has its own bounded policy.
    fn request_cleanup(&self) -> Result<()>;
    /// Current independent protocol disposition.
    fn disposition(&self) -> ConnectionDisposition;
    /// Current local memory-access lifetime.
    fn lifetime(&self) -> LocalLifetime;
}

pub(crate) fn sha(bytes: &[u8]) -> [u8; 32] {
    Sha256::digest(bytes).into()
}
/// Computes the specified stable ID without randomized hashing or collision reassignment.
pub fn stable_id(parts: &[Option<&str>]) -> u32 {
    u32::from_le_bytes(
        sha(identity(parts).as_bytes())[..4]
            .try_into()
            .expect("fixed hash width"),
    )
}
fn identity(parts: &[Option<&str>]) -> String {
    format!(
        "[{}]",
        parts
            .iter()
            .map(|p| optional_string(*p))
            .collect::<Vec<_>>()
            .join(",")
    )
}
pub(crate) fn quote(value: &str) -> String {
    let mut output = String::with_capacity(value.len() + 2);
    output.push('"');
    for c in value.chars() {
        match c {
            '"' => output.push_str("\\\""),
            '\\' => output.push_str("\\\\"),
            '\u{0}'..='\u{1f}' => output.push_str(&format!("\\u00{:02x}", c as u32)),
            _ => output.push(c),
        }
    }
    output.push('"');
    output
}
fn optional_string(value: Option<&str>) -> String {
    value.map(quote).unwrap_or_else(|| "null".into())
}
fn optional_u32(value: Option<u32>) -> String {
    value
        .map(|v| v.to_string())
        .unwrap_or_else(|| "null".into())
}
pub(crate) fn numbers(values: &[u32]) -> String {
    values
        .iter()
        .map(u32::to_string)
        .collect::<Vec<_>>()
        .join(",")
}
fn config_text(codec: &Codec) -> &str {
    codec.configuration.as_deref().unwrap_or("null")
}
fn keys(value: &Value, expected: &[&str]) -> Result<()> {
    let object = value.as_object().ok_or(ContractError("object required"))?;
    if object.len() != expected.len() || object.keys().any(|k| !expected.contains(&k.as_str())) {
        return Err(ContractError("unknown or missing field"));
    }
    Ok(())
}
fn number(value: &Value, key: &str) -> Result<u64> {
    value
        .get(key)
        .and_then(Value::as_u64)
        .ok_or(ContractError("unsigned integer required"))
}
fn u32number(value: &Value, key: &str) -> Result<u32> {
    u32::try_from(number(value, key)?).map_err(|_| ContractError("u32 overflow"))
}
fn optional_number(value: &Value, key: &str) -> Result<Option<u32>> {
    if value.get(key) == Some(&Value::Null) {
        Ok(None)
    } else {
        u32number(value, key).map(Some)
    }
}
fn boolean(value: &Value, key: &str) -> Result<bool> {
    value
        .get(key)
        .and_then(Value::as_bool)
        .ok_or(ContractError("Boolean required"))
}
fn name<'a>(value: &'a Value, key: &str) -> Result<&'a str> {
    value
        .get(key)
        .and_then(Value::as_str)
        .filter(|s| !s.is_empty())
        .ok_or(ContractError("nonempty name required"))
}
fn optional_name(value: &Value, key: &str) -> Result<Option<String>> {
    if value.get(key) == Some(&Value::Null) {
        Ok(None)
    } else {
        name(value, key).map(|s| Some(s.to_owned()))
    }
}
fn array<'a>(value: &'a Value, key: &str) -> Result<&'a Vec<Value>> {
    value
        .get(key)
        .and_then(Value::as_array)
        .ok_or(ContractError("array required"))
}
fn u32array(value: &Value, key: &str) -> Result<Vec<u32>> {
    array(value, key)?
        .iter()
        .map(|v| {
            v.as_u64()
                .and_then(|n| u32::try_from(n).ok())
                .ok_or(ContractError("u32 vector required"))
        })
        .collect()
}
fn codec(value: &Value) -> Result<Codec> {
    let id = u32number(value, "codec")?;
    let nullable = boolean(value, "nullable")?;
    let config = value
        .get("codec_config")
        .ok_or(ContractError("codec configuration missing"))?;
    let configuration = if config.is_null() {
        None
    } else if id == 13 {
        keys(config, &["precision", "scale"])?;
        Some(format!(
            "{{\"precision\":{},\"scale\":{}}}",
            number(config, "precision")?,
            number(config, "scale")?
        ))
    } else if id == 15 {
        keys(config, &["scale"])?;
        Some(format!("{{\"scale\":{}}}", number(config, "scale")?))
    } else {
        return Err(ContractError("irrelevant configuration"));
    };
    Ok(Codec {
        id,
        configuration,
        nullable,
    })
}
