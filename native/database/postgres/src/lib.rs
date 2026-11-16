//! SQLx PostgreSQL component. B04 supplies the engine runtime, pool and admission
//! budgets. This adapter owns one fixed lease, never a second pool or executor.
mod config;
mod session;
mod stream;
mod transaction;
pub use config::{ConnectConfig, SessionConfig, TlsMode};
use nativeweave_interop_abi::{NwAbiInfoV1, NwStatus, write_abi_v1};
pub use session::{ColumnMetadata, Command, CommandKind, Session, SessionControl, SessionReceipt};
use std::sync::atomic::{AtomicU64, Ordering};
pub use stream::{RowStream, StreamControl, StreamReceipt};
pub use transaction::PostgresTransactionDriver;

/// Bounded provider categories; raw SQLx messages may contain secrets and are not retained.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ErrorKind {
    Configuration,
    Busy,
    Closed,
    Canceled,
    SqlState,
    Transport,
    Codec,
    Shape,
    Cleanup,
}
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProviderError {
    pub kind: ErrorKind,
    pub sqlstate: Option<String>,
    pub correlation_id: u64,
    pub message: &'static str,
}
static NEXT_ERROR: AtomicU64 = AtomicU64::new(1);
impl ProviderError {
    pub(crate) fn new(kind: ErrorKind, message: &'static str) -> Self {
        Self {
            kind,
            sqlstate: None,
            correlation_id: NEXT_ERROR.fetch_add(1, Ordering::Relaxed),
            message,
        }
    }
    pub fn invalidates_transaction(&self) -> bool {
        matches!(self.kind, ErrorKind::SqlState | ErrorKind::Transport)
    }
}
impl std::fmt::Display for ProviderError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "{:?} [{}] {}",
            self.kind, self.correlation_id, self.message
        )
    }
}
impl std::error::Error for ProviderError {}
impl From<sqlx::Error> for ProviderError {
    fn from(value: sqlx::Error) -> Self {
        let code = value
            .as_database_error()
            .and_then(|e| e.code())
            .map(|s| s.into_owned());
        let mut error = Self::new(
            if code.is_some() {
                ErrorKind::SqlState
            } else {
                ErrorKind::Transport
            },
            "PostgreSQL operation failed",
        );
        error.sqlstate =
            code.filter(|s| s.len() == 5 && s.bytes().all(|c| c.is_ascii_alphanumeric()));
        error
    }
}
impl From<nativeweave_data_codecs::CodecError> for ProviderError {
    fn from(_: nativeweave_data_codecs::CodecError) -> Self {
        Self::new(ErrorKind::Codec, "PostgreSQL value conversion failed")
    }
}
pub type Result<T> = std::result::Result<T, ProviderError>;

/// Stateless component preflight. Bit zero only identifies this provider
/// component; it does not advertise B04 datasource/session operation exports.
/// # Safety
/// The output must meet A01's writable output-buffer contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_postgres_component_get_abi_v1(
    major: u32,
    capacity: u32,
    output: *mut NwAbiInfoV1,
) -> NwStatus {
    // SAFETY: the caller's exact A01 preconditions are forwarded unchanged.
    unsafe { write_abi_v1(major, capacity, output, 0, 1) }
}
