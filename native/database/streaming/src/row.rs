use crate::{ErrorKind, Limits, Result, Shape, error};
use nativeweave_data_codecs::{read_postgres, read_sqlite};
use nativeweave_data_contracts::Profile;
use sqlx::{Column, Row, TypeInfo, ValueRef, postgres::PgRow, sqlite::SqliteRow};

/// Actual typed SQLx rows accepted by this component, never DTO constructors.
pub trait CodecRow: Send + 'static {
    /// Checks complete count/order/aliases/types before any nullable scalar decode.
    fn preflight(&self, shape: &Shape) -> Result<()>;
    /// Copies one scalar through B07, respecting a finite pre-copy field limit.
    fn scalar(&self, ordinal: usize, shape: &Shape, max_bytes: usize) -> Result<Option<Vec<u8>>>;
}
impl CodecRow for PgRow {
    fn preflight(&self, shape: &Shape) -> Result<()> {
        if shape.expected.profile != Profile::PostgreSql {
            return Err(error(ErrorKind::Shape, "PostgreSQL row profile mismatch"));
        }
        shape.check_count(self.len())?;
        for (i, column) in self.columns().iter().enumerate() {
            shape.check_column(
                i,
                column.ordinal(),
                column.name(),
                Some(column.type_info().name()),
            )?;
        }
        Ok(())
    }
    fn scalar(&self, ordinal: usize, shape: &Shape, max_bytes: usize) -> Result<Option<Vec<u8>>> {
        Ok(read_postgres(
            self,
            ordinal,
            &shape.expected.descriptors[ordinal].codec,
            max_bytes,
        )?)
    }
}
impl CodecRow for SqliteRow {
    fn preflight(&self, shape: &Shape) -> Result<()> {
        if shape.expected.profile != Profile::Sqlite {
            return Err(error(ErrorKind::Shape, "SQLite row profile mismatch"));
        }
        shape.check_count(self.len())?;
        for (i, c) in self.columns().iter().enumerate() {
            let raw = self
                .try_get_raw(c.ordinal())
                .map_err(|_| error(ErrorKind::Shape, "missing SQLite ordinal"))?;
            // Preserve declared type for a NULL column where SQLx has one. A
            // computed NULL cannot supply a dynamic storage class.
            if raw.is_null() {
                shape.check_column(i, c.ordinal(), c.name(), Some(c.type_info().name()))?;
            } else {
                shape.check_column(i, c.ordinal(), c.name(), Some(raw.type_info().name()))?;
            }
        }
        Ok(())
    }
    fn scalar(&self, ordinal: usize, shape: &Shape, max_bytes: usize) -> Result<Option<Vec<u8>>> {
        Ok(read_sqlite(
            self,
            ordinal,
            &shape.expected.descriptors[ordinal].codec,
            max_bytes,
        )?)
    }
}
pub(crate) struct StagedRow {
    pub cells: Vec<Option<Vec<u8>>>,
    pub wire_bytes: usize,
}
impl StagedRow {
    pub(crate) fn decode<R: CodecRow>(row: &R, shape: &Shape, limits: Limits) -> Result<Self> {
        row.preflight(shape)?;
        let mut cells = Vec::with_capacity(shape.expected.descriptors.len());
        let mut bytes = shape.row_table_bytes;
        for ordinal in 0..shape.expected.descriptors.len() {
            let remaining = limits.max_batch_bytes - shape.base_bytes - bytes;
            let cell = row.scalar(ordinal, shape, limits.max_field_bytes.min(remaining.max(1)))?;
            let length = cell.as_ref().map_or(0, Vec::len);
            if length > remaining || length > limits.max_field_bytes {
                return Err(error(
                    ErrorKind::LargeValueRequiresStreaming,
                    "single inline row exceeds batch capacity",
                ));
            }
            bytes = bytes
                .checked_add(length)
                .ok_or_else(|| error(ErrorKind::Capacity, "row payload overflow"))?;
            cells.push(cell);
        }
        Ok(Self {
            cells,
            wire_bytes: bytes,
        })
    }
}
