use crate::{Session, session::ControlKind};
use nativeweave_data_contracts::{ConnectionDisposition, Profile};
use nativeweave_data_transactions::{
    Capabilities, Confirmation, Options, ProtocolState, ProviderFailure, TransactionDriver,
};

/// B05 adapter bound to the same physical-session actor; it creates no second pool or state machine.
pub struct SqliteTransactionDriver {
    session: Option<Session>,
    session_id: u64,
}
impl SqliteTransactionDriver {
    pub fn new(session: Session) -> Self {
        Self {
            session_id: session.session_id(),
            session: Some(session),
        }
    }
    async fn call(&self, kind: ControlKind) -> Result<(), ProviderFailure> {
        let session = self.session.as_ref().ok_or(ProviderFailure {
            code: None,
            correlation_id: self.session_id,
        })?;
        session
            .control(kind)
            .await
            .map_err(|e| e.transaction_failure(self.session_id))
    }
}
impl TransactionDriver for SqliteTransactionDriver {
    fn capabilities(&self) -> Capabilities {
        Capabilities {
            profile: Profile::Sqlite,
            read_only: true,
        }
    }
    fn session_id(&self) -> u64 {
        self.session_id
    }
    async fn begin(&mut self, options: &Options) -> Result<(), ProviderFailure> {
        self.call(ControlKind::Begin(options.read_only)).await
    }
    async fn commit(&mut self) -> Result<Confirmation, ProviderFailure> {
        self.call(ControlKind::Commit)
            .await
            .map(|()| Confirmation::Committed)
    }
    async fn rollback(&mut self) -> Result<ProtocolState, ProviderFailure> {
        self.call(ControlKind::Rollback)
            .await
            .map(|()| ProtocolState::ResetPending)
    }
    async fn savepoint_begin(&mut self) -> Result<(), ProviderFailure> {
        self.call(ControlKind::SavepointBegin).await
    }
    async fn savepoint_rollback(&mut self) -> Result<(), ProviderFailure> {
        self.call(ControlKind::SavepointRollback).await
    }
    async fn savepoint_release(&mut self) -> Result<(), ProviderFailure> {
        self.call(ControlKind::SavepointRelease).await
    }
    async fn recover(&mut self) -> Result<ProtocolState, ProviderFailure> {
        self.call(ControlKind::Recover)
            .await
            .map(|()| ProtocolState::Clean)
    }
    async fn retire(&mut self, disposition: ConnectionDisposition) {
        if let Some(session) = self.session.take() {
            let observer = session.observer();
            let discard = !matches!(
                disposition,
                ConnectionDisposition::Usable | ConnectionDisposition::ProviderResetPending
            );
            if session.retire_transaction(discard).await.is_err() {
                // The upstream API did not establish worker retirement. Keep the
                // transaction's cleanup evidence/budget retained; a deadline cannot free it.
                let retained = observer;
                std::future::pending::<()>().await;
                drop(retained);
            }
        }
    }
}
