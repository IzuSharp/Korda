//! SQLite execution through the pinned SQLx worker and bundled SQLite engine.
//!
//! The B04 host supplies its runtime, SQLx pool, physical lease and shared
//! admission owner. This component starts no global executor. Commands retain
//! their SQLx futures even if a business waiter disappears. Shutdown receipts
//! distinguish the observable worker acknowledgement from an unavailable OS join.

mod admission;
mod config;
mod error;
mod session;
mod transaction;

pub use admission::{Admission, AdmissionSnapshot};
pub use config::{BeginMode, JournalMode, Limits, OpenMode, Options, SynchronousMode};
pub use error::{ErrorKind, ProviderError, Result};
pub use session::{
    Cancellation, OperationReceipt, OperationRetirement, ParameterInput, RowStream, Session,
    SessionObserver, SessionRetirement, StopControl,
};
pub use transaction::SqliteTransactionDriver;

/// Exact amalgamation compiled by the locked bundled dependency.
pub const SQLITE_VERSION: &str = "3.51.3";

/// Reports the component profile capability only; this is not the B04 database host ABI.
/// No runtime, worker, connection or callback starts during bootstrap.
///
/// # Safety
/// `out_info` must satisfy the live writable-buffer contract of the canonical A01 writer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_sqlite_get_abi_v1(
    requested_major: u32,
    out_capacity: u32,
    out_info: *mut nativeweave_interop_abi::NwAbiInfoV1,
) -> nativeweave_interop_abi::NwStatus {
    // SAFETY: the caller supplies the canonical bootstrap buffer; A01 validates
    // complete capacity/alignment and retains no pointer or owner.
    unsafe { nativeweave_interop_abi::write_abi_v1(requested_major, out_capacity, out_info, 0, 1) }
}
