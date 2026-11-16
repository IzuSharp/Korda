use crate::{ErrorKind, ProviderError, Result, SessionConfig};
use nativeweave_data_codecs::Parameters;
use sqlx::{
    AssertSqlSafe, Column, Connection, Executor, Postgres, Statement, TypeInfo,
    pool::PoolConnection, postgres::PgArguments,
};
use sqlx_core::sql_str::SqlSafeStr;
use std::sync::{
    Arc,
    atomic::{AtomicBool, AtomicUsize, Ordering},
};
use tokio::sync::{Mutex, Notify, oneshot, watch};

/// Whether an explicitly audited command can alter session ownership/settings.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CommandKind {
    Ordinary,
    SessionMutating,
}
/// An already capability/type-checked native generated statement or audited
/// expert statement. This constructor does not sanitize SQL. B04/B11 must never
/// label arbitrary session/transaction control as Ordinary.
pub struct Command {
    pub(crate) sql: String,
    pub(crate) parameters: Option<Parameters>,
    pub(crate) kind: CommandKind,
}
impl Command {
    pub fn generated(sql: String, parameters: Option<Parameters>) -> Self {
        Self {
            sql,
            parameters,
            kind: CommandKind::Ordinary,
        }
    }
    pub fn expert(sql: String, parameters: Option<Parameters>, kind: CommandKind) -> Self {
        Self {
            sql,
            parameters,
            kind,
        }
    }
    pub(crate) fn arguments(&self) -> Result<PgArguments> {
        self.parameters
            .as_ref()
            .map(Parameters::postgres)
            .transpose()
            .map(|a| a.unwrap_or_default())
            .map_err(Into::into)
    }
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ColumnMetadata {
    pub ordinal: usize,
    pub name: String,
    pub type_name: String,
}
#[derive(Clone, Debug, Default)]
pub struct SessionReceipt {
    pub backend_pid: i32,
    pub retired: bool,
    pub physical_closed: bool,
    pub close_confirmed: bool,
    pub returned_to_pool: bool,
    pub quarantined: bool,
}
pub(crate) struct Inner {
    pub conn: Mutex<Option<PoolConnection<Postgres>>>,
    pub config: SessionConfig,
    pub pid: i32,
    pub active: AtomicBool,
    pub operation_done: Notify,
    pub closing: AtomicBool,
    pub untrusted: AtomicBool,
    pub transaction_active: AtomicBool,
    pub transaction_failed: AtomicBool,
    pub discard: AtomicBool,
    pub owners: AtomicUsize,
    pub receipt: watch::Sender<SessionReceipt>,
    pub runtime: tokio::runtime::Handle,
}
pub(crate) struct Operation(pub Arc<Inner>);
impl Drop for Operation {
    fn drop(&mut self) {
        self.0.active.store(false, Ordering::Release);
        self.0.operation_done.notify_waiters();
    }
}
impl Inner {
    pub fn admit(self: &Arc<Self>) -> Result<Operation> {
        if self.closing.load(Ordering::Acquire) || self.untrusted.load(Ordering::Acquire) {
            return Err(ProviderError::new(
                ErrorKind::Closed,
                "PostgreSQL session closed",
            ));
        }
        self.active
            .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
            .map_err(|_| {
                ProviderError::new(
                    ErrorKind::Busy,
                    "PostgreSQL session already has an operation",
                )
            })?;
        if self.closing.load(Ordering::Acquire) {
            self.active.store(false, Ordering::Release);
            self.operation_done.notify_waiters();
            return Err(ProviderError::new(
                ErrorKind::Closed,
                "PostgreSQL session closed",
            ));
        }
        Ok(Operation(Arc::clone(self)))
    }
    pub fn validate(&self, command: &Command) -> Result<()> {
        if command.sql.is_empty()
            || command.sql.len() > self.config.max_sql_bytes
            || command.sql.contains('\0')
        {
            return Err(ProviderError::new(
                ErrorKind::Configuration,
                "invalid statement length",
            ));
        }
        if command.kind == CommandKind::SessionMutating {
            if self.transaction_active.load(Ordering::Acquire) {
                return Err(ProviderError::new(
                    ErrorKind::Configuration,
                    "session mutation rejected during transaction",
                ));
            }
            self.discard.store(true, Ordering::Release);
        }
        Ok(())
    }
    pub fn observe(&self, error: &ProviderError) {
        if error.kind == ErrorKind::Transport {
            self.untrusted.store(true, Ordering::Release);
        }
        if error.invalidates_transaction() && self.transaction_active.load(Ordering::Acquire) {
            self.transaction_failed.store(true, Ordering::Release);
        }
    }
    /// Public SQLx ping sends Sync and consumes all pending messages/ReadyForQuery.
    /// This is protocol evidence, independent of the transaction's durable outcome.
    pub async fn drain(&self, connection: &mut PoolConnection<Postgres>) -> Result<()> {
        match tokio::time::timeout(self.config.cleanup_timeout, connection.ping()).await {
            Ok(Ok(())) => Ok(()),
            _ => {
                self.untrusted.store(true, Ordering::Release);
                Err(ProviderError::new(
                    ErrorKind::Cleanup,
                    "PostgreSQL protocol drain failed",
                ))
            }
        }
    }
    pub fn request_close(self: &Arc<Self>) {
        if self.closing.swap(true, Ordering::AcqRel) {
            return;
        }
        let inner = Arc::clone(self);
        self.runtime.spawn(async move {
            // Wait even for an admitted worker that has not acquired the lock yet.
            // Closure cannot overtake queued native accessors.
            loop {
                let completed = inner.operation_done.notified();
                tokio::pin!(completed);
                completed.as_mut().enable();
                if !inner.active.load(Ordering::Acquire) {
                    break;
                }
                completed.await;
            }
            // The active operation owns this lock until its native accessors exit.
            let mut guard = inner.conn.lock().await;
            if let Some(mut connection) = guard.take() {
                let mut quarantined = inner.untrusted.load(Ordering::Acquire);
                if inner.transaction_active.load(Ordering::Acquire) {
                    quarantined = true;
                }
                let return_ok = !quarantined
                    && !inner.discard.load(Ordering::Acquire)
                    && inner.drain(&mut connection).await.is_ok();
                if return_ok {
                    // Explicitly await SQLx maintenance; Drop alone is not receipt.
                    connection.return_to_pool().await;
                    inner.receipt.send_replace(SessionReceipt {
                        backend_pid: inner.pid,
                        retired: true,
                        returned_to_pool: true,
                        ..Default::default()
                    });
                } else {
                    quarantined |= inner.untrusted.load(Ordering::Acquire);
                    // PoolConnection::close retains its permit through actual close.
                    // An error still destroys the owned transport before this receipt;
                    // it cannot establish a remote transaction/lock outcome.
                    let confirmed = connection.close().await.is_ok();
                    inner.receipt.send_replace(SessionReceipt {
                        backend_pid: inner.pid,
                        retired: true,
                        physical_closed: true,
                        close_confirmed: confirmed,
                        quarantined,
                        ..Default::default()
                    });
                }
            }
        });
    }
}
/// Fixed physical-session owner. B04 supplies its existing pool lease and runtime.
/// Clone shares one operation gate; it never opens an additional connection.
pub struct Session {
    pub(crate) inner: Arc<Inner>,
}
impl Clone for Session {
    fn clone(&self) -> Self {
        self.inner.owners.fetch_add(1, Ordering::Relaxed);
        Self {
            inner: Arc::clone(&self.inner),
        }
    }
}
impl Drop for Session {
    fn drop(&mut self) {
        if self.inner.owners.fetch_sub(1, Ordering::AcqRel) == 1 {
            self.inner.request_close();
        }
    }
}
impl Session {
    /// Initializes the explicit fixed statement-timeout profile before publishing
    /// the session. B04 must retain runtime/pool ownership until close is joined.
    pub async fn from_lease(
        mut connection: PoolConnection<Postgres>,
        config: SessionConfig,
    ) -> Result<Self> {
        config.validate()?;
        let initialized = async {
            let timeout = format!("{}ms", config.statement_timeout.as_millis());
            sqlx::query("SELECT set_config('statement_timeout', $1, false)")
                .bind(timeout)
                .execute(&mut *connection)
                .await?;
            let pid: i32 = sqlx::query_scalar("SELECT pg_backend_pid()")
                .fetch_one(&mut *connection)
                .await?;
            Ok::<_, sqlx::Error>(pid)
        }
        .await;
        let pid = match initialized {
            Ok(pid) => pid,
            Err(error) => {
                let _ = connection.close().await;
                return Err(error.into());
            }
        };
        let (receipt, _) = watch::channel(SessionReceipt {
            backend_pid: pid,
            ..Default::default()
        });
        Ok(Self {
            inner: Arc::new(Inner {
                conn: Mutex::new(Some(connection)),
                config,
                pid,
                active: AtomicBool::new(false),
                operation_done: Notify::new(),
                closing: AtomicBool::new(false),
                untrusted: AtomicBool::new(false),
                transaction_active: AtomicBool::new(false),
                transaction_failed: AtomicBool::new(false),
                discard: AtomicBool::new(false),
                owners: AtomicUsize::new(1),
                receipt,
                runtime: tokio::runtime::Handle::current(),
            }),
        })
    }
    pub fn backend_pid(&self) -> i32 {
        self.inner.pid
    }
    pub fn control(&self) -> SessionControl {
        SessionControl {
            inner: Arc::clone(&self.inner),
        }
    }
    pub fn transaction_driver(&self) -> crate::PostgresTransactionDriver {
        crate::PostgresTransactionDriver {
            session: Some(self.clone()),
            session_id: self.backend_pid() as u64,
        }
    }
    pub async fn close(&self) -> SessionReceipt {
        self.inner.request_close();
        self.control().join().await
    }
    /// The independent worker owns the operation even if this waiter is dropped.
    /// No result is permission to reuse until its protocol drain has completed.
    pub async fn execute(&self, command: Command) -> Result<u64> {
        self.inner.validate(&command)?;
        let arguments = command.arguments()?;
        let operation = self.inner.admit()?;
        let inner = Arc::clone(&self.inner);
        let (sender, receiver) = oneshot::channel();
        self.inner.runtime.spawn(async move {
            let mut guard = inner.conn.lock().await;
            let result = if let Some(connection) = guard.as_mut() {
                let result = sqlx::query_with(AssertSqlSafe(command.sql), arguments)
                    .execute(&mut **connection)
                    .await
                    .map(|r| r.rows_affected())
                    .map_err(ProviderError::from);
                if let Err(error) = &result {
                    inner.observe(error);
                }
                let drain = inner.drain(connection).await;
                result.and_then(|value| drain.map(|_| value))
            } else {
                Err(ProviderError::new(
                    ErrorKind::Closed,
                    "PostgreSQL session retired",
                ))
            };
            let damaged = inner.untrusted.load(Ordering::Acquire);
            drop(guard);
            drop(operation);
            if damaged {
                inner.request_close();
            }
            let _ = sender.send(result);
        });
        receiver.await.map_err(|_| {
            ProviderError::new(ErrorKind::Transport, "PostgreSQL operation owner failed")
        })?
    }
    /// Prepares through SQLx and snapshots types even when the result has no rows.
    pub async fn describe(&self, command: Command) -> Result<Vec<ColumnMetadata>> {
        self.inner.validate(&command)?;
        let operation = self.inner.admit()?;
        let inner = Arc::clone(&self.inner);
        let (sender, receiver) = oneshot::channel();
        self.inner.runtime.spawn(async move {
            let mut guard = inner.conn.lock().await;
            let result = if let Some(connection) = guard.as_mut() {
                (&mut **connection)
                    .prepare(AssertSqlSafe(command.sql).into_sql_str())
                    .await
                    .map(|d| {
                        d.columns()
                            .iter()
                            .map(|c| ColumnMetadata {
                                ordinal: c.ordinal(),
                                name: c.name().to_owned(),
                                type_name: c.type_info().name().to_owned(),
                            })
                            .collect()
                    })
                    .map_err(ProviderError::from)
            } else {
                Err(ProviderError::new(
                    ErrorKind::Closed,
                    "PostgreSQL session retired",
                ))
            };
            if let Err(error) = &result {
                inner.observe(error);
            }
            let drain = if let Some(connection) = guard.as_mut() {
                inner.drain(connection).await
            } else {
                Ok(())
            };
            drop(guard);
            drop(operation);
            if inner.untrusted.load(Ordering::Acquire) {
                inner.request_close();
            }
            let _ = sender.send(result.and_then(|r| drain.map(|_| r)));
        });
        receiver.await.map_err(|_| {
            ProviderError::new(ErrorKind::Transport, "PostgreSQL describe owner failed")
        })?
    }
}
/// Retirement remains independently observable after the public owner is dropped.
#[derive(Clone)]
pub struct SessionControl {
    inner: Arc<Inner>,
}
impl SessionControl {
    pub fn request_close(&self) {
        self.inner.request_close();
    }
    pub fn snapshot(&self) -> SessionReceipt {
        self.inner.receipt.borrow().clone()
    }
    pub async fn join(&self) -> SessionReceipt {
        let mut changed = self.inner.receipt.subscribe();
        loop {
            let value = changed.borrow_and_update().clone();
            if value.retired {
                return value;
            }
            if changed.changed().await.is_err() {
                return self.snapshot();
            }
        }
    }
}
impl SessionControl {
    pub(crate) fn from_inner(inner: Arc<Inner>) -> Self {
        Self { inner }
    }
}
