use crate::{ErrorKind, ProviderError, Session};
use nativeweave_data_contracts::{ConnectionDisposition, Profile};
use nativeweave_data_transactions::{
    Capabilities, Confirmation, Isolation, Options, ProtocolState, ProviderFailure,
    TransactionDriver,
};
use sqlx::{Executor, postgres::PgTransactionManager};
use sqlx_core::{sql_str::SqlSafeStr, transaction::TransactionManager};
use std::sync::atomic::Ordering;

/// A same-session B05 adapter. Cloning the Session retains its single pool lease;
/// transaction policy, admission/outcome and cleanup snapshots remain in B05.
pub struct PostgresTransactionDriver {
    pub(crate) session: Session,
}
fn failure(error: ProviderError) -> ProviderFailure {
    ProviderFailure {
        code: error.sqlstate,
        correlation_id: error.correlation_id,
    }
}
impl PostgresTransactionDriver {
    fn unavailable() -> ProviderFailure {
        failure(ProviderError::new(
            ErrorKind::Closed,
            "PostgreSQL transaction session retired",
        ))
    }
    async fn tracked(&mut self, operation: u8) -> std::result::Result<(), ProviderFailure> {
        let inner = &self.session.inner;
        let _operation = inner.admit().map_err(failure)?;
        let mut guard = inner.conn.lock().await;
        let connection = guard.as_mut().ok_or_else(Self::unavailable)?;
        let result = match operation {
            0 => PgTransactionManager::begin(&mut **connection, None).await,
            1 => PgTransactionManager::commit(&mut **connection).await,
            _ => {
                let depth = PgTransactionManager::get_transaction_depth(&**connection);
                if depth < 2 {
                    return Err(Self::unavailable());
                }
                // ROLLBACK TO keeps the savepoint: do not decrement SQLx's depth.
                (&mut **connection)
                    .execute(sqlx_core::transaction::rollback_ansi_transaction_sql(depth))
                    .await
                    .map(|_| ())
            }
        };
        result.map_err(|e| {
            let error = ProviderError::from(e);
            inner.observe(&error);
            failure(error)
        })?;
        if operation == 2 {
            inner.transaction_failed.store(false, Ordering::Release);
        }
        inner.drain(connection).await.map_err(failure)
    }
}
impl TransactionDriver for PostgresTransactionDriver {
    fn capabilities(&self) -> Capabilities {
        Capabilities {
            profile: Profile::PostgreSql,
            read_only: true,
        }
    }
    fn session_id(&self) -> u64 {
        self.session.backend_pid() as u64
    }
    async fn begin(&mut self, options: &Options) -> std::result::Result<(), ProviderFailure> {
        options
            .validate(self.capabilities())
            .map_err(|_| Self::unavailable())?;
        let inner = &self.session.inner;
        let _operation = inner.admit().map_err(failure)?;
        let mut guard = inner.conn.lock().await;
        let connection = guard.as_mut().ok_or_else(Self::unavailable)?;
        if inner.transaction_active.load(Ordering::Acquire) {
            return Err(Self::unavailable());
        }
        let isolation = match options.isolation {
            None | Some(Isolation::ReadCommitted) => "READ COMMITTED",
            Some(Isolation::RepeatableRead) => "REPEATABLE READ",
            Some(Isolation::Serializable) => "SERIALIZABLE",
            _ => return Err(Self::unavailable()),
        };
        let statement = format!(
            "BEGIN ISOLATION LEVEL {isolation} {}",
            if options.read_only {
                "READ ONLY"
            } else {
                "READ WRITE"
            }
        );
        PgTransactionManager::begin(
            &mut **connection,
            Some(sqlx::AssertSqlSafe(statement).into_sql_str()),
        )
        .await
        .map_err(|e| failure(e.into()))?;
        inner.transaction_active.store(true, Ordering::Release);
        inner.transaction_failed.store(false, Ordering::Release);
        Ok(())
    }
    async fn commit(&mut self) -> std::result::Result<Confirmation, ProviderFailure> {
        let inner = &self.session.inner;
        let _operation = inner.admit().map_err(failure)?;
        let mut guard = inner.conn.lock().await;
        let connection = guard.as_mut().ok_or_else(Self::unavailable)?;
        // SQLx's public transaction manager does not expose COMMIT versus
        // ROLLBACK command tags. A harmless statement probes the actual failed
        // state on this same session before dispatch. A failed probe rolls back
        // explicitly and can never report Committed after PG's COMMIT->ROLLBACK.
        if let Err(error) = (&mut **connection).execute("SELECT 1").await {
            let error = ProviderError::from(error);
            inner.observe(&error);
            if error.sqlstate.as_deref() == Some("25P02") {
                while PgTransactionManager::get_transaction_depth(&**connection) > 0 {
                    PgTransactionManager::rollback(&mut **connection)
                        .await
                        .map_err(|e| failure(e.into()))?;
                }
                inner.transaction_active.store(false, Ordering::Release);
                inner.transaction_failed.store(false, Ordering::Release);
                inner.drain(connection).await.map_err(failure)?;
                return Ok(Confirmation::RolledBack(failure(error)));
            }
            return Err(failure(error));
        }
        if PgTransactionManager::get_transaction_depth(&**connection) != 1 {
            return Err(Self::unavailable());
        }
        PgTransactionManager::commit(&mut **connection)
            .await
            .map_err(|e| {
                let error = ProviderError::from(e);
                inner.observe(&error);
                failure(error)
            })?;
        inner.transaction_active.store(false, Ordering::Release);
        inner.transaction_failed.store(false, Ordering::Release);
        inner.drain(connection).await.map_err(failure)?;
        Ok(Confirmation::Committed)
    }
    async fn rollback(&mut self) -> std::result::Result<ProtocolState, ProviderFailure> {
        let inner = &self.session.inner;
        // Cleanup remains possible after business admission closed/quarantined.
        let mut guard = inner.conn.lock().await;
        let connection = guard.as_mut().ok_or_else(Self::unavailable)?;
        while PgTransactionManager::get_transaction_depth(&**connection) > 0 {
            PgTransactionManager::rollback(&mut **connection)
                .await
                .map_err(|e| {
                    let error = ProviderError::from(e);
                    inner.observe(&error);
                    failure(error)
                })?;
        }
        inner.transaction_active.store(false, Ordering::Release);
        inner.transaction_failed.store(false, Ordering::Release);
        inner.drain(connection).await.map_err(failure)?;
        Ok(ProtocolState::Clean)
    }
    async fn savepoint_begin(&mut self) -> std::result::Result<(), ProviderFailure> {
        self.tracked(0).await
    }
    async fn savepoint_rollback(&mut self) -> std::result::Result<(), ProviderFailure> {
        self.tracked(2).await
    }
    async fn savepoint_release(&mut self) -> std::result::Result<(), ProviderFailure> {
        self.tracked(1).await
    }
    async fn recover(&mut self) -> std::result::Result<ProtocolState, ProviderFailure> {
        if self.session.inner.untrusted.load(Ordering::Acquire) {
            return Ok(ProtocolState::Untrusted);
        }
        if self
            .session
            .inner
            .transaction_active
            .load(Ordering::Acquire)
        {
            return self.rollback().await;
        }
        let mut guard = self.session.inner.conn.lock().await;
        let connection = guard.as_mut().ok_or_else(Self::unavailable)?;
        self.session
            .inner
            .drain(connection)
            .await
            .map_err(failure)?;
        Ok(ProtocolState::Clean)
    }
    async fn retire(&mut self, disposition: ConnectionDisposition) {
        if matches!(
            disposition,
            ConnectionDisposition::Quarantined | ConnectionDisposition::Discarded
        ) {
            self.session.inner.untrusted.store(true, Ordering::Release);
            self.session.close().await;
        }
        if self.session.inner.owners.load(Ordering::Acquire) == 1
            || self.session.inner.closing.load(Ordering::Acquire)
        {
            // If the parent public session was dropped, transaction retirement
            // also joins its last physical lease instead of starting cleanup
            // only from the driver's later Drop.
            self.session.close().await;
        }
        // Otherwise the live parent session still owns its clean lease; no
        // transaction accessor remains. B04 later joins its close/pool closure.
    }
}
