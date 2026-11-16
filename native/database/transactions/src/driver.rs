//! The small provider seam implemented by the SQLx PostgreSQL and SQLite adapters.

use crate::{Capabilities, Options};
use nativeweave_data_contracts::ConnectionDisposition;
use std::future::Future;

/// Evidence about transaction effects, not inferred from absence of an exception.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Confirmation {
    Committed,
    RolledBack(ProviderFailure),
}
/// Provider protocol evidence on the same physical session.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ProtocolState {
    /// Transaction ended and the session may accept an ordinary command.
    Clean,
    /// Only known serialized provider maintenance remains before the next command.
    ResetPending,
    /// Protocol or ownership cannot be trusted; no new tenant may borrow it.
    Untrusted,
}
/// Bounded provider diagnostics. Never include connection strings, SQL or parameter values.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProviderFailure {
    pub code: Option<String>,
    pub correlation_id: u64,
}
/// Session-local SQLx operations. All calls use the original physical-session lease.
///
/// Driver futures must be `Send` and must not detach untracked native accesses.
/// `commit` only returns `Committed` after a trustworthy commit confirmation and
/// must distinguish a failed-transaction rollback. SQLx objects stay inside one
/// engine; the managed facade sees only snapshots and owned operation results.
///
/// `retire` is a **join**, not a request: it completes only after this transaction's
/// driver/callback/worker obligations have exited and its parent-session lease may
/// be released. Usable sessions may stay with their owning DataSession. For an
/// untrusted session it must await physical closure/worker retirement before
/// returning. `close_on_drop`, dropping an active Future, and early pool detach do
/// not implement this method. The owning engine retains its runtime during it.
pub trait TransactionDriver: Send + 'static {
    fn capabilities(&self) -> Capabilities;
    fn session_id(&self) -> u64;
    fn begin(
        &mut self,
        options: &Options,
    ) -> impl Future<Output = Result<(), ProviderFailure>> + Send;
    fn commit(&mut self) -> impl Future<Output = Result<Confirmation, ProviderFailure>> + Send;
    fn rollback(&mut self) -> impl Future<Output = Result<ProtocolState, ProviderFailure>> + Send;
    /// Begin/release use SQLx tracked nesting; identifiers never come from user SQL.
    fn savepoint_begin(&mut self) -> impl Future<Output = Result<(), ProviderFailure>> + Send;
    /// Roll back the top savepoint and retain that top scope for a later release.
    fn savepoint_rollback(&mut self) -> impl Future<Output = Result<(), ProviderFailure>> + Send;
    fn savepoint_release(&mut self) -> impl Future<Output = Result<(), ProviderFailure>> + Send;
    /// Drain/probe/reset using the original session and independent cleanup admission.
    fn recover(&mut self) -> impl Future<Output = Result<ProtocolState, ProviderFailure>> + Send;
    fn retire(&mut self, disposition: ConnectionDisposition) -> impl Future<Output = ()> + Send;
}
