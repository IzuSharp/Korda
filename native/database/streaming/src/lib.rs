//! Bounded row ownership above actual provider streams.
//!
//! The provider retains its SQLx future and physical connection until its explicit
//! join receipt confirms retirement. This crate owns no runtime or SQL executor.
//! Its byte reservations cover its row staging and wire copies, not SQLx buffers,
//! allocator retention, managed DTOs, or process RSS.

mod budget;
mod cursor;
mod row;
mod shape;

pub use budget::{Budget, BudgetSnapshot};
pub use cursor::{Cursor, Fetch, OwnedBatch, Retirement, Source};
pub use row::CodecRow;
pub use shape::{ColumnMetadata, Limits, Shape};

use nativeweave_data_codecs::{CodecError, ErrorKind as CodecErrorKind};
use nativeweave_data_contracts::ContractError;

/// Stable internal classifications, translated by the creating engine.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ErrorKind {
    /// Shape identity, alias, ordinal, or database type differs.
    Shape,
    /// Checked scalar decoding failed.
    Conversion,
    /// The requested profile or scalar is unavailable.
    Capability,
    /// A finite cursor, row, or owned-byte quota is exhausted.
    Capacity,
    /// A single inline row/field cannot fit; v1 has no incremental field fallback.
    LargeValueRequiresStreaming,
    /// A fetch or transfer conflicts with the current owner state.
    InvalidState,
    /// Provider cancellation was confirmed independently of the caller's token.
    Canceled,
    /// A provider operation failed.
    Provider,
    /// A provider has not established local quiescence.
    CleanupPending,
}
/// A fixed, bounded diagnostic without SQL, credentials, or payload data.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct StreamError {
    /// Domain classification.
    pub kind: ErrorKind,
    /// Static safe diagnostic.
    pub message: &'static str,
}
impl std::fmt::Display for StreamError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{:?}: {}", self.kind, self.message)
    }
}
impl std::error::Error for StreamError {}
impl From<ContractError> for StreamError {
    fn from(value: ContractError) -> Self {
        error(ErrorKind::Shape, value.0)
    }
}
impl From<CodecError> for StreamError {
    fn from(value: CodecError) -> Self {
        error(
            match value.kind {
                CodecErrorKind::Shape => ErrorKind::Shape,
                CodecErrorKind::Capacity => ErrorKind::LargeValueRequiresStreaming,
                CodecErrorKind::Capability => ErrorKind::Capability,
                _ => ErrorKind::Conversion,
            },
            value.message,
        )
    }
}
/// A complete operation or a structured, bounded failure.
pub type Result<T> = std::result::Result<T, StreamError>;
pub(crate) fn error(kind: ErrorKind, message: &'static str) -> StreamError {
    StreamError { kind, message }
}
