//! Fallible db-wire scalar conversion shared by database providers.
//!
//! B01 remains the sole owner of logical metadata and wire layout. This crate
//! copies bounded admitted values, builds SQLx arguments, and returns owned wire
//! scalars. It owns no executor, connection, SQL builder or public CLR model.

mod postgres;
mod scalar;
mod sea_query_values;
mod sqlite;
pub use postgres::{append_postgres, read_postgres};
pub use scalar::{instant_bytes, time_bytes};
pub use sqlite::{append_sqlite, read_sqlite};

use nativeweave_data_contracts::wire::{Batch, Expectation};
use nativeweave_data_contracts::{Codec, ContractError, MAX_BYTES, MAX_NODES, Profile};
use sqlx::{Arguments, postgres::PgArguments, sqlite::SqliteArguments};

/// Stable adapter-local classification; no parameter value enters diagnostics.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ErrorKind {
    /// Invalid, lossy or out-of-range scalar conversion.
    Conversion,
    /// The requested provider does not support this explicit scalar profile.
    Capability,
    /// A finite count or byte limit would be exceeded.
    Capacity,
    /// The database result type or parameter identity differs from the admitted shape.
    Shape,
    /// SQLx rejected argument encoding; the partial local owner is dropped.
    Encoding,
}
/// Bounded error with no connection string, SQL text or parameter contents.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CodecError {
    /// Classification to map into the existing database error contract.
    pub kind: ErrorKind,
    /// Constant diagnostic text.
    pub message: &'static str,
}
impl std::fmt::Display for CodecError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{:?}: {}", self.kind, self.message)
    }
}
impl std::error::Error for CodecError {}
impl From<ContractError> for CodecError {
    fn from(value: ContractError) -> Self {
        Self {
            kind: ErrorKind::Conversion,
            message: value.0,
        }
    }
}
/// A complete conversion or no publishable result/argument owner.
pub type Result<T> = std::result::Result<T, CodecError>;
pub(crate) fn error(kind: ErrorKind, message: &'static str) -> CodecError {
    CodecError { kind, message }
}

/// Explicit finite capacities; zero and values exceeding the B01 profile reject.
#[derive(Clone, Copy, Debug)]
pub struct Limits {
    /// Maximum argument count.
    pub max_parameters: usize,
    /// Maximum input envelope and conservative provider-encoded argument bytes.
    pub max_parameter_bytes: usize,
    /// Maximum one field's wire/provider representation.
    pub max_field_bytes: usize,
}
impl Default for Limits {
    fn default() -> Self {
        Self {
            max_parameters: MAX_NODES,
            max_parameter_bytes: MAX_BYTES,
            max_field_bytes: MAX_BYTES,
        }
    }
}
impl Limits {
    /// Checks capacities before parsing or allocation.
    pub fn validate(self) -> Result<()> {
        if self.max_parameters == 0
            || self.max_parameters > MAX_NODES
            || self.max_parameter_bytes == 0
            || self.max_parameter_bytes > MAX_BYTES
            || self.max_field_bytes == 0
            || self.max_field_bytes > self.max_parameter_bytes
        {
            return Err(error(ErrorKind::Capacity, "invalid codec limits"));
        }
        Ok(())
    }
}

/// Owned, validated scalar snapshots for one admitted parameter/key row.
///
/// An async command may retain this owner; the caller's borrowed input is never
/// retained. Every provider invocation creates fresh arguments. On any failure,
/// the local partially built arguments are dropped before an error is returned.
pub struct Parameters {
    pub(crate) profile: Profile,
    pub(crate) fields: Vec<(Codec, Option<Vec<u8>>)>,
    pub(crate) limits: Limits,
}
impl Parameters {
    /// Validates exact B01 kind, hash, IDs, bound codecs, NULL flags and bytes.
    /// The supplied expectation comes from the admitted operation, never wire data.
    pub fn decode(bytes: &[u8], mut expected: Expectation, limits: Limits) -> Result<Self> {
        limits.validate()?;
        if !matches!(expected.kind, 1 | 3 | 4) || expected.exact_rows.is_some_and(|n| n != 1) {
            return Err(error(
                ErrorKind::Shape,
                "parameters require a value/parameter/key payload",
            ));
        }
        if expected.descriptors.len() > limits.max_parameters
            || bytes.len() > limits.max_parameter_bytes
        {
            return Err(error(ErrorKind::Capacity, "parameter envelope/count limit"));
        }
        for descriptor in &expected.descriptors {
            check_profile(&descriptor.codec, expected.profile)?;
        }
        expected.exact_rows = Some(1);
        let batch = Batch::parse(bytes, expected.clone())?;
        let mut fields = Vec::with_capacity(expected.descriptors.len());
        let mut provider_bytes = 0usize;
        for (i, descriptor) in expected.descriptors.iter().enumerate() {
            let cell = batch.cell(0, i)?;
            check_profile(&descriptor.codec, expected.profile)?;
            let size = scalar::provider_size(&descriptor.codec, cell, expected.profile)?;
            if cell.is_some_and(|x| x.len() > limits.max_field_bytes)
                || size > limits.max_field_bytes
            {
                return Err(error(ErrorKind::Capacity, "parameter field byte limit"));
            }
            // Four-byte framing is included for every parameter, including NULL.
            provider_bytes = provider_bytes
                .checked_add(size)
                .and_then(|n| n.checked_add(4))
                .ok_or_else(|| error(ErrorKind::Capacity, "parameter encoded byte overflow"))?;
            if provider_bytes > limits.max_parameter_bytes {
                return Err(error(ErrorKind::Capacity, "provider argument byte limit"));
            }
            fields.push((descriptor.codec.clone(), cell.map(<[u8]>::to_vec)));
        }
        Ok(Self {
            profile: expected.profile,
            fields,
            limits,
        })
    }
    /// The exact argument count, including typed SQL NULLs.
    pub fn len(&self) -> usize {
        self.fields.len()
    }
    /// Whether the admitted command has no parameters.
    pub fn is_empty(&self) -> bool {
        self.fields.is_empty()
    }
    /// Builds PostgreSQL arguments, propagating every SQLx `Arguments::add` error.
    pub fn postgres(&self) -> Result<PgArguments> {
        if self.profile != Profile::PostgreSql {
            return Err(error(ErrorKind::Capability, "PostgreSQL profile required"));
        }
        let mut args = PgArguments::default();
        args.reserve(self.fields.len(), self.limits.max_parameter_bytes.min(4096));
        for (codec, value) in &self.fields {
            postgres::push(&mut args, codec, value.as_deref())?;
        }
        if args.len() != self.fields.len() {
            return Err(error(ErrorKind::Encoding, "SQLx argument count mismatch"));
        }
        Ok(args)
    }
    /// Builds SQLite arguments using the exact TEXT/BLOB/INTEGER/REAL profiles.
    pub fn sqlite(&self) -> Result<SqliteArguments> {
        if self.profile != Profile::Sqlite {
            return Err(error(ErrorKind::Capability, "SQLite profile required"));
        }
        let mut args = SqliteArguments::default();
        args.reserve(self.fields.len(), 0);
        for (codec, value) in &self.fields {
            sqlite::push(&mut args, codec, value.as_deref())?;
        }
        if args.len() != self.fields.len() {
            return Err(error(ErrorKind::Encoding, "SQLx argument count mismatch"));
        }
        Ok(args)
    }
}

pub(crate) fn check_profile(codec: &Codec, profile: Profile) -> Result<()> {
    if profile == Profile::Sqlite && codec.id == 13 {
        return Err(error(
            ErrorKind::Capability,
            "numeric decimal requires PostgreSQL",
        ));
    }
    if profile == Profile::PostgreSql && codec.id == 14 {
        return Err(error(
            ErrorKind::Capability,
            "DecimalText is the SQLite text-only profile",
        ));
    }
    codec.validate(profile)?;
    Ok(())
}
