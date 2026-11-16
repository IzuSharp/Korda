use crate::{ErrorKind, Result, error};
use nativeweave_data_contracts::{
    MAX_BYTES, MAX_NODES, Profile, Projection,
    wire::{Batch, Descriptor, Expectation},
};

/// Provider-observed statement columns, including empty-result metadata.
#[derive(Clone, Debug)]
pub struct ColumnMetadata {
    /// Zero-based physical result ordinal.
    pub ordinal: usize,
    /// Exact provider alias, without case folding.
    pub name: String,
    /// SQLx type name. SQLite computed NULL/unknown columns use None.
    pub type_name: Option<String>,
}
/// Per-fetch finite ordinary-inline limits; these are independent of driver RSS.
#[derive(Clone, Copy, Debug)]
pub struct Limits {
    /// Maximum complete rows in one batch.
    pub max_rows: usize,
    /// Maximum serialized header, descriptors, cells and payload together.
    pub max_batch_bytes: usize,
    /// Maximum one inline field, additionally constrained by the single-row budget.
    pub max_field_bytes: usize,
}
impl Default for Limits {
    fn default() -> Self {
        Self {
            max_rows: 256,
            max_batch_bytes: 1024 * 1024,
            max_field_bytes: MAX_BYTES,
        }
    }
}
impl Limits {
    /// Validates bounded counts before allocation or provider admission.
    pub fn validate(self) -> Result<()> {
        if self.max_rows == 0
            || self.max_rows > MAX_NODES
            || self.max_batch_bytes == 0
            || self.max_batch_bytes > MAX_BYTES
            || self.max_field_bytes == 0
            || self.max_field_bytes > MAX_BYTES
        {
            return Err(error(
                ErrorKind::Capacity,
                "invalid finite streaming limits",
            ));
        }
        Ok(())
    }
}
/// Canonical B01 projection and admitted source-bound identity.
pub struct Shape {
    pub(crate) projection: Projection,
    pub(crate) expected: Expectation,
    pub(crate) base_bytes: usize,
    pub(crate) row_table_bytes: usize,
}
impl Shape {
    /// Parses the sole canonical projection and validates every source-bound codec.
    /// `scalar` permits only the specified single empty-alias sentinel.
    pub fn parse(
        bytes: &[u8],
        profile: Profile,
        projection_id: u32,
        bound_codecs: &[u32],
        scalar: bool,
    ) -> Result<Self> {
        let projection = if scalar {
            Projection::parse_scalar(bytes, profile)?
        } else {
            Projection::parse(bytes, profile)?
        };
        if bound_codecs.len() != projection.fields().len() {
            return Err(error(ErrorKind::Shape, "source-bound codec count differs"));
        }
        let mut configured = std::collections::HashMap::new();
        for (field, bound) in projection.fields().iter().zip(bound_codecs) {
            if profile == Profile::PostgreSql && field.codec.id == 14 {
                return Err(error(
                    ErrorKind::Capability,
                    "DecimalText requires the SQLite text profile",
                ));
            }
            if field.codec.configuration.is_some()
                && let Some(previous) = configured.insert(*bound, &field.codec)
                && !previous.same_scalar(&field.codec)
            {
                return Err(error(
                    ErrorKind::Shape,
                    "configured codec ID aliases different definitions",
                ));
            }
        }
        let descriptors = projection
            .fields()
            .iter()
            .zip(bound_codecs)
            .enumerate()
            .map(|(i, (field, id))| Descriptor {
                id: i as u32,
                codec: field.codec.clone(),
                bound_codec: *id,
            })
            .collect::<Vec<_>>();
        let expected = Expectation {
            kind: 2,
            profile,
            hash: projection.hash(),
            entity_id: 0,
            projection_id,
            descriptors,
            exact_rows: None,
        };
        // The canonical owner validates configured identities, reserved fields and EOS shape.
        let encoded = Batch::encode(&expected, 0, &[])?;
        let row_table_bytes = expected
            .descriptors
            .len()
            .checked_mul(24)
            .ok_or_else(|| error(ErrorKind::Capacity, "row table overflow"))?;
        Ok(Self {
            projection,
            expected,
            base_bytes: encoded.len(),
            row_table_bytes,
        })
    }
    /// Exact admitted expectation for independently validating transferred batches.
    pub fn expectation(&self) -> &Expectation {
        &self.expected
    }
    /// Counts every header/descriptor/cell byte before accepting the first row.
    pub fn validate_limits(&self, limits: Limits) -> Result<()> {
        limits.validate()?;
        if self
            .base_bytes
            .checked_add(self.row_table_bytes)
            .is_none_or(|n| n > limits.max_batch_bytes)
        {
            return Err(error(
                ErrorKind::LargeValueRequiresStreaming,
                "single row descriptors exceed batch capacity",
            ));
        }
        Ok(())
    }
    /// Checks the entire provider statement shape before interpreting any SQL NULL.
    pub fn validate_columns(&self, columns: &[ColumnMetadata]) -> Result<()> {
        self.check_count(columns.len())?;
        for (i, actual) in columns.iter().enumerate() {
            self.check_column(i, actual.ordinal, &actual.name, actual.type_name.as_deref())?;
        }
        Ok(())
    }
    pub(crate) fn check_count(&self, count: usize) -> Result<()> {
        if count != self.expected.descriptors.len() {
            return Err(error(
                ErrorKind::Shape,
                "complete result column count differs",
            ));
        }
        Ok(())
    }
    pub(crate) fn check_column(
        &self,
        index: usize,
        ordinal: usize,
        name: &str,
        type_name: Option<&str>,
    ) -> Result<()> {
        let field = &self.projection.fields()[index];
        if ordinal != index || !field.name.is_empty() && name != field.name {
            return Err(error(
                ErrorKind::Shape,
                "result ordinal or exact alias differs",
            ));
        }
        if !type_matches(self.expected.profile, field.codec.id, type_name) {
            return Err(error(ErrorKind::Shape, "result database type differs"));
        }
        Ok(())
    }
    pub(crate) fn reservation_bytes(&self, limits: Limits) -> Result<usize> {
        // Five batch-size credits cover staging, the next row, reference table,
        // final wire allocation and conversion scratch. Metadata/control vector
        // storage is separately included; allocator bookkeeping is not promised.
        limits
            .max_batch_bytes
            .checked_mul(5)
            .and_then(|n| {
                n.checked_add(
                    limits
                        .max_rows
                        .checked_mul(std::mem::size_of::<crate::row::StagedRow>())?,
                )
            })
            .and_then(|n| n.checked_add(self.projection.bytes().len().checked_mul(3)?))
            .and_then(|n| {
                n.checked_add(self.expected.descriptors.len().checked_mul(
                    std::mem::size_of::<Descriptor>() + std::mem::size_of::<ColumnMetadata>(),
                )?)
            })
            .ok_or_else(|| error(ErrorKind::Capacity, "stream reservation overflow"))
    }
}

pub(crate) fn type_matches(profile: Profile, id: u32, name: Option<&str>) -> bool {
    match profile {
        Profile::PostgreSql => match id {
            1 => name == Some("INT2"),
            2 | 17 => name == Some("INT4"),
            3 | 15 => name == Some("INT8"),
            4 => name == Some("BOOL"),
            5 => matches!(
                name,
                Some("TEXT" | "VARCHAR" | "BPCHAR" | "NAME" | "UNKNOWN" | "citext")
            ),
            6 => name == Some("BYTEA"),
            7 => name == Some("UUID"),
            8 => name == Some("DATE"),
            9 => name == Some("TIME"),
            10 => name == Some("TIMESTAMPTZ"),
            11 => name == Some("FLOAT8"),
            12 => name == Some("FLOAT4"),
            13 => name == Some("NUMERIC"),
            16 => matches!(name, Some("JSON" | "JSONB")),
            _ => false,
        },
        Profile::Sqlite => {
            // SQLite's dynamic computed NULL has no observable storage type. Its
            // ordinal/alias/declared codec are still validated; NULL is handled later.
            if name.is_none() || name == Some("NULL") {
                return id != 13;
            }
            match id {
                1 | 2 | 3 | 4 | 10 | 15 | 17 => name == Some("INTEGER"),
                5 | 8 | 9 | 14 | 16 => name == Some("TEXT"),
                6 | 7 => name == Some("BLOB"),
                11 | 12 => name == Some("REAL"),
                _ => false,
            }
        }
    }
}
