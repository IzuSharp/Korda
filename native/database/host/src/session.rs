use crate::{
    Error, Result,
    config::ProviderConfig,
    runtime::{CURSORS, Pool, SESSIONS, Source},
    sql::SqlPlan,
};
use futures_util::TryStreamExt;
use nativeweave_data_contracts::ConnectionDisposition;
use nativeweave_data_streaming::{ColumnMetadata, Cursor, OwnedBatch, Retirement, Shape};
use nativeweave_data_transactions::{
    AdmissionToken, Capabilities, CommandLease, Confirmation, Options as TxOptions, ProtocolState,
    ProviderFailure, SavepointToken, Snapshot, Transaction, TransactionDriver,
};
use sqlx::{Column, Statement, TypeInfo};
use std::{
    sync::{
        Arc, Mutex, Weak,
        atomic::{AtomicBool, Ordering},
    },
    time::Instant,
};
use tokio::sync::{Mutex as AsyncMutex, Notify, OwnedSemaphorePermit};

pub enum ProviderSession {
    #[cfg(feature = "postgres")]
    Postgres(nativeweave_data_postgres::Session),
    #[cfg(feature = "sqlite")]
    Sqlite(nativeweave_data_sqlite::Session),
}
pub struct Session {
    pub source: Arc<Source>,
    pub provider: ProviderSession,
    pub closing: AtomicBool,
    pub retired: AtomicBool,
    active: AtomicBool,
    discard: AtomicBool,
    changed: Notify,
    permit: Mutex<Option<OwnedSemaphorePermit>>,
    pub transaction: Mutex<Option<Weak<TransactionOwner>>>,
    cursors: Mutex<Vec<Weak<CursorOwner>>>,
}
impl Session {
    pub async fn open(
        source: Arc<Source>,
        cancel: Arc<crate::operation::Cancel>,
    ) -> Result<Arc<Self>> {
        source.admit()?;
        if cancel.requested() {
            return Err(Error::canceled());
        }
        source
            .waiting
            .try_update(Ordering::AcqRel, Ordering::Acquire, |n| {
                (n < source.config.limits.waiters).then_some(n + 1)
            })
            .map_err(|_| Error::capacity())?;
        struct Waiting(Arc<Source>);
        impl Drop for Waiting {
            fn drop(&mut self) {
                self.0.waiting.fetch_sub(1, Ordering::AcqRel);
            }
        }
        let waiting = Waiting(source.clone());
        let permit = tokio::select! {biased;_ = cancel.notified()=>return Err(Error::canceled()),value=tokio::time::timeout(source.config.limits.admission,source.sessions.clone().acquire_owned())=>value.map_err(|_|Error::capacity())?.map_err(|_|Error::closed())?};
        drop(waiting);
        source.admit()?;
        let provider = match (&source.pool, &source.config.provider) {
            #[cfg(feature = "postgres")]
            (Pool::Postgres(pool), ProviderConfig::Postgres(_)) => {
                let lease = tokio::select! {biased;_=cancel.notified()=>return Err(Error::canceled()),value=pool.acquire()=>value?};
                let session = nativeweave_data_postgres::Session::from_lease(
                    lease,
                    nativeweave_data_postgres::SessionConfig {
                        statement_timeout: source.config.limits.command,
                        cleanup_timeout: std::time::Duration::from_secs(5),
                        max_sql_bytes: source.config.limits.parameter_bytes,
                    },
                )
                .await?;
                ProviderSession::Postgres(session)
            }
            #[cfg(feature = "sqlite")]
            (Pool::Sqlite(pool), ProviderConfig::Sqlite { options, .. }) => {
                let lease = tokio::select! {biased;_=cancel.notified()=>return Err(Error::canceled()),value=pool.acquire()=>value?};
                ProviderSession::Sqlite(nativeweave_data_sqlite::Session::start(
                    lease,
                    options.clone(),
                    nativeweave_data_sqlite::Admission::new(source.config.limits.sqlite())?,
                    &source.runtime.handle,
                )?)
            }
            #[allow(unreachable_patterns)]
            _ => return Err(Error::state("provider profile mismatch")),
        };
        let result = Arc::new(Self {
            source: source.clone(),
            provider,
            closing: AtomicBool::new(false),
            retired: AtomicBool::new(false),
            active: AtomicBool::new(false),
            discard: AtomicBool::new(false),
            changed: Notify::new(),
            permit: Mutex::new(Some(permit)),
            transaction: Mutex::new(None),
            cursors: Mutex::new(Vec::new()),
        });
        SESSIONS.fetch_add(1, Ordering::Relaxed);
        source.add_session(&result);
        if cancel.requested() || source.closing.load(Ordering::Acquire) {
            result.request_close();
            result.join().await;
            return Err(Error::canceled());
        }
        Ok(result)
    }
    pub fn admit(self: &Arc<Self>, transaction_control: bool) -> Result<CommandGate> {
        self.source.admit()?;
        if self.closing.load(Ordering::Acquire) {
            return Err(Error::closed());
        }
        if self
            .active
            .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
            .is_err()
        {
            return Err(Error::new(
                nativeweave_interop_abi::NW_DOMAIN_ERROR,
                7,
                "concurrent session operation",
            ));
        }
        let mut gate = CommandGate {
            session: self.clone(),
            transaction: None,
            dispatched: false,
        };
        if self.closing.load(Ordering::Acquire) || self.source.closing.load(Ordering::Acquire) {
            return Err(Error::closed());
        }
        if !transaction_control {
            if let Some(tx) = self
                .transaction
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .as_ref()
                .and_then(Weak::upgrade)
            {
                if !tx.transaction.snapshot().local_resources_released {
                    gate.transaction = Some(
                        tx.transaction
                            .admit_command(false, &AdmissionToken::default())?,
                    );
                }
            }
        }
        Ok(gate)
    }
    pub fn request_close(self: &Arc<Self>) {
        if self.closing.swap(true, Ordering::AcqRel) {
            return;
        }
        let cursors = self
            .cursors
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .iter()
            .filter_map(Weak::upgrade)
            .collect::<Vec<_>>();
        for cursor in cursors {
            cursor.request_close();
        }
        if let Some(tx) = self
            .transaction
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .as_ref()
            .and_then(Weak::upgrade)
        {
            tx.transaction.request_close();
        }
        let session = self.clone();
        self.source.runtime.handle.spawn(async move {
            loop {
                let ready = session.changed.notified();
                tokio::pin!(ready);
                ready.as_mut().enable();
                if !session.active.load(Ordering::Acquire) {
                    break;
                }
                ready.await;
            }
            let tx = session
                .transaction
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .as_ref()
                .and_then(Weak::upgrade);
            if let Some(tx) = tx {
                tx.transaction.join().await;
            }
            match &session.provider {
                #[cfg(feature = "postgres")]
                ProviderSession::Postgres(value) => {
                    value.close().await;
                }
                #[cfg(feature = "sqlite")]
                ProviderSession::Sqlite(value) => {
                    if session.discard.load(Ordering::Acquire) {
                        let _ = value.close().await;
                    } else {
                        let _ = value.return_to_pool().await;
                    }
                }
            }
            session
                .permit
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .take();
            session.retired.store(true, Ordering::Release);
            session.changed.notify_waiters();
            session.source.changed.notify_waiters();
        });
    }
    pub async fn join(&self) {
        loop {
            let wait = self.changed.notified();
            tokio::pin!(wait);
            wait.as_mut().enable();
            if self.retired.load(Ordering::Acquire) {
                return;
            }
            wait.await;
        }
    }
    pub async fn execute(
        self: &Arc<Self>,
        plan: Arc<SqlPlan>,
        cancel: Arc<crate::operation::Cancel>,
        mut gate: CommandGate,
    ) -> Result<crate::operation::Value> {
        if cancel.requested() {
            return Err(Error::canceled());
        }
        if plan.timeout.is_zero() {
            return Err(Error::new(
                nativeweave_interop_abi::NW_TIMEOUT,
                8,
                "command deadline expired",
            ));
        }
        let deadline = Instant::now()
            .checked_add(plan.timeout)
            .ok_or_else(|| Error::invalid("command deadline"))?;
        gate.dispatched = true;
        if plan.kind == 2 {
            self.discard.store(true, Ordering::Release);
        }
        if plan.result_kind == 0 {
            let work = self.execute_count(&plan, cancel.clone());
            tokio::pin!(work);
            let mut timed_out = false;
            let result = tokio::select! { biased; value = &mut work => value, _ = tokio::time::sleep_until(tokio::time::Instant::from_std(deadline)) => { timed_out = true; cancel.request(); work.await } };
            gate.complete(result.is_err());
            if plan.kind == 2 {
                self.request_close();
                self.join().await;
            }
            if timed_out {
                return Err(Error::new(
                    nativeweave_interop_abi::NW_TIMEOUT,
                    8,
                    "command total deadline expired",
                ));
            }
            Ok(crate::operation::Value::Count(
                i64::try_from(result?).map_err(|_| Error::capacity())?,
            ))
        } else {
            let scalar_count = plan.scalar_count;
            let cursor =
                CursorOwner::open(self.clone(), plan.clone(), cancel.clone(), gate, deadline)
                    .await?;
            {
                let mut children = self.cursors.lock().unwrap_or_else(|e| e.into_inner());
                children.retain(|c| {
                    c.upgrade()
                        .is_some_and(|c| !c.retired.load(Ordering::Acquire))
                });
                children.push(Arc::downgrade(&cursor));
            }
            if self.closing.load(Ordering::Acquire) || self.source.closing.load(Ordering::Acquire) {
                cursor.request_close();
                cursor.join().await;
                return Err(Error::closed());
            }
            if scalar_count {
                let fetched = cursor
                    .fetch(cancel, 2, self.source.config.limits.batch_bytes)
                    .await;
                let result = fetched.and_then(|buffer| {
                    let projection = plan
                        .projection
                        .as_ref()
                        .ok_or_else(|| Error::state("scalar projection missing"))?;
                    let bound = projection
                        .projection
                        .fields()
                        .iter()
                        .map(|f| projection.codecs.bound(&f.codec))
                        .collect::<std::result::Result<Vec<_>, _>>()?;
                    let shape = Shape::parse(
                        projection.projection.bytes(),
                        self.source.config.profile,
                        projection.id,
                        &bound,
                        true,
                    )?;
                    let batch = nativeweave_data_contracts::wire::Batch::parse(
                        buffer.view(),
                        shape.expectation().clone(),
                    )?;
                    if batch.rows() != 1 {
                        return Err(Error::new(
                            nativeweave_interop_abi::NW_DOMAIN_ERROR,
                            4,
                            "count cardinality",
                        ));
                    }
                    let value = batch
                        .cell(0, 0)?
                        .ok_or_else(|| Error::state("null native count"))?;
                    Ok(i64::from_le_bytes(
                        value
                            .try_into()
                            .map_err(|_| Error::state("count scalar width"))?,
                    ))
                });
                cursor.request_close();
                cursor.join().await;
                return Ok(crate::operation::Value::Count(result?));
            }
            Ok(crate::operation::Value::Owner(
                crate::abi::OwnerValue::Cursor(cursor),
            ))
        }
    }
    async fn execute_count(
        &self,
        plan: &SqlPlan,
        cancel: Arc<crate::operation::Cancel>,
    ) -> Result<u64> {
        match &self.provider {
            #[cfg(feature = "postgres")]
            ProviderSession::Postgres(session) => {
                let parameters = nativeweave_data_codecs::Parameters::decode(
                    &plan.parameters,
                    plan.expectation.clone(),
                    self.source.config.limits.codec(),
                )?;
                let command = nativeweave_data_postgres::Command::expert(
                    plan.sql.clone(),
                    Some(parameters),
                    if plan.kind == 2 {
                        nativeweave_data_postgres::CommandKind::SessionMutating
                    } else {
                        nativeweave_data_postgres::CommandKind::Ordinary
                    },
                );
                // B02 retains and drains the dispatched future; a request cannot fabricate rollback.
                Ok(session.execute(command).await?)
            }
            #[cfg(feature = "sqlite")]
            ProviderSession::Sqlite(session) => {
                let token = nativeweave_data_sqlite::Cancellation::default();
                let stop = token.clone();
                cancel.install(Arc::new(move || {
                    stop.cancel();
                }));
                Ok(session
                    .execute(
                        &plan.sql,
                        Some(nativeweave_data_sqlite::ParameterInput {
                            bytes: &plan.parameters,
                            expected: plan.expectation.clone(),
                        }),
                        token,
                    )
                    .await?)
            }
        }
    }
    pub async fn begin(
        self: &Arc<Self>,
        options: TxOptions,
        cancel: Arc<crate::operation::Cancel>,
        gate: CommandGate,
    ) -> Result<Arc<TransactionOwner>> {
        if self
            .transaction
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .as_ref()
            .and_then(Weak::upgrade)
            .is_some_and(|t| !t.transaction.snapshot().local_resources_released)
        {
            return Err(Error::state("transaction already active"));
        }
        let driver = match &self.provider {
            #[cfg(feature = "postgres")]
            ProviderSession::Postgres(s) => Driver::Postgres(s.transaction_driver()),
            #[cfg(feature = "sqlite")]
            ProviderSession::Sqlite(s) => Driver::Sqlite(
                nativeweave_data_sqlite::SqliteTransactionDriver::new(s.clone()),
            ),
        };
        let admission = AdmissionToken::default();
        if cancel.requested() {
            admission.cancel();
        }
        let transaction = Transaction::begin(driver, options, &admission).await?;
        let owner = Arc::new(TransactionOwner {
            session: self.clone(),
            transaction,
        });
        *self.transaction.lock().unwrap_or_else(|e| e.into_inner()) = Some(Arc::downgrade(&owner));
        gate.complete(false);
        Ok(owner)
    }
}
impl Drop for Session {
    fn drop(&mut self) {
        SESSIONS.fetch_sub(1, Ordering::Relaxed);
    }
}
pub struct CommandGate {
    session: Arc<Session>,
    transaction: Option<CommandLease<Driver>>,
    dispatched: bool,
}
impl CommandGate {
    pub fn complete(mut self, failed: bool) {
        if let Some(lease) = self.transaction.take() {
            lease.complete(failed, ProtocolState::Clean);
        }
    }
}
impl Drop for CommandGate {
    fn drop(&mut self) {
        if let Some(lease) = self.transaction.take() {
            if !self.dispatched {
                lease.complete(false, ProtocolState::Clean);
            }
        }
        self.session.active.store(false, Ordering::Release);
        self.session.changed.notify_waiters();
    }
}

pub enum NativeCursor {
    #[cfg(feature = "postgres")]
    Postgres(Cursor<sqlx::postgres::PgRow>),
    #[cfg(feature = "sqlite")]
    Sqlite(Cursor<sqlx::sqlite::SqliteRow>),
}
impl NativeCursor {
    async fn fetch(
        &mut self,
        rows: usize,
        bytes: usize,
    ) -> nativeweave_data_streaming::Result<OwnedBatch> {
        match self {
            #[cfg(feature = "postgres")]
            Self::Postgres(c) => {
                c.fetch_bounded(rows, bytes).await?;
                c.take_batch()
            }
            #[cfg(feature = "sqlite")]
            Self::Sqlite(c) => {
                c.fetch_bounded(rows, bytes).await?;
                c.take_batch()
            }
        }
    }
    async fn close(&mut self) -> nativeweave_data_streaming::Result<Retirement> {
        match self {
            #[cfg(feature = "postgres")]
            Self::Postgres(c) => c.close().await,
            #[cfg(feature = "sqlite")]
            Self::Sqlite(c) => c.close().await,
        }
    }
}
pub struct CursorOwner {
    pub session: Arc<Session>,
    cursor: AsyncMutex<NativeCursor>,
    gate: Mutex<Option<CommandGate>>,
    stop: Arc<dyn Fn() + Send + Sync>,
    pub closing: AtomicBool,
    pub retired: AtomicBool,
    pub active: AtomicBool,
    changed: Notify,
    pub last_end: AtomicBool,
    pub deadline: Instant,
    pub projection_id: u32,
    pub batch_live: AtomicBool,
    end_wire: Vec<u8>,
    end_sent: AtomicBool,
    retirement: Mutex<Option<nativeweave_data_streaming::Result<Retirement>>>,
}
fn stream_error(_: impl std::fmt::Display) -> nativeweave_data_streaming::StreamError {
    nativeweave_data_streaming::StreamError {
        kind: nativeweave_data_streaming::ErrorKind::Provider,
        message: "native provider stream failed",
    }
}
impl CursorOwner {
    async fn open(
        session: Arc<Session>,
        plan: Arc<SqlPlan>,
        cancel: Arc<crate::operation::Cancel>,
        gate: CommandGate,
        deadline: Instant,
    ) -> Result<Arc<Self>> {
        let projection = plan
            .projection
            .as_ref()
            .ok_or_else(|| Error::invalid("query projection missing"))?;
        let bound = projection
            .projection
            .fields()
            .iter()
            .map(|f| projection.codecs.bound(&f.codec))
            .collect::<std::result::Result<Vec<_>, _>>()?;
        let shape = Shape::parse(
            projection.projection.bytes(),
            session.source.config.profile,
            projection.id,
            &bound,
            plan.result_kind == 1,
        )?;
        let end_wire =
            nativeweave_data_contracts::wire::Batch::encode(shape.expectation(), 0, &[])?;
        let limits = session.source.config.limits.stream(
            if plan.result_kind == 1 {
                2
            } else {
                session.source.config.limits.batch_rows
            },
            session.source.config.limits.batch_bytes,
        );
        let (cursor, stop): (NativeCursor, Arc<dyn Fn() + Send + Sync>) = match &session.provider {
            #[cfg(feature = "postgres")]
            ProviderSession::Postgres(provider) => {
                let parameters = nativeweave_data_codecs::Parameters::decode(
                    &plan.parameters,
                    plan.expectation.clone(),
                    session.source.config.limits.codec(),
                )?;
                let columns = provider
                    .describe(nativeweave_data_postgres::Command::generated(
                        plan.sql.clone(),
                        None,
                    ))
                    .await?
                    .into_iter()
                    .map(|c| ColumnMetadata {
                        ordinal: c.ordinal,
                        name: c.name,
                        type_name: Some(c.type_name),
                    })
                    .collect();
                let rows = provider.stream(nativeweave_data_postgres::Command::generated(
                    plan.sql.clone(),
                    Some(parameters),
                ))?;
                let control = rows.control();
                let stopping = control.clone();
                let stop: Arc<dyn Fn() + Send + Sync> = Arc::new(move || stopping.request_stop());
                let request_stop = stop.clone();
                let native = nativeweave_data_streaming::Source {
                    columns,
                    rows: Box::pin(rows.map_err(stream_error)),
                    request_stop,
                    join: Box::pin(async move {
                        let r = control.join().await;
                        Ok(Retirement {
                            native_retired: r.retired,
                            canceled: r.stopped,
                            disposition: if r.protocol_clean {
                                ConnectionDisposition::Usable
                            } else {
                                ConnectionDisposition::Quarantined
                            },
                            provider_rows_peak: r.retained_rows_peak,
                        })
                    }),
                };
                let cursor = Cursor::open(
                    shape,
                    limits,
                    &session.source.stream_budget,
                    session.source.runtime.handle.clone(),
                    || async { Ok(native) },
                )
                .await?;
                (NativeCursor::Postgres(cursor), stop)
            }
            #[cfg(feature = "sqlite")]
            ProviderSession::Sqlite(provider) => {
                let statement = provider
                    .describe(&plan.sql, nativeweave_data_sqlite::Cancellation::default())
                    .await?;
                let columns = statement
                    .columns()
                    .iter()
                    .map(|c| ColumnMetadata {
                        ordinal: c.ordinal(),
                        name: c.name().to_owned(),
                        type_name: Some(c.type_info().name().to_owned()),
                    })
                    .collect();
                let token = nativeweave_data_sqlite::Cancellation::default();
                let rows = provider.stream(
                    &plan.sql,
                    Some(nativeweave_data_sqlite::ParameterInput {
                        bytes: &plan.parameters,
                        expected: plan.expectation.clone(),
                    }),
                    token,
                )?;
                let control = rows.stop_control();
                let receipt = rows.receipt();
                let stop: Arc<dyn Fn() + Send + Sync> = Arc::new(move || control.request_stop());
                let request_stop = stop.clone();
                let native = nativeweave_data_streaming::Source {
                    columns,
                    rows: Box::pin(rows.map_err(stream_error)),
                    request_stop,
                    join: Box::pin(async move {
                        let r = receipt.join().await.map_err(stream_error)?;
                        Ok(Retirement {
                            native_retired: r.native_retired,
                            canceled: r.stopped,
                            disposition: ConnectionDisposition::Usable,
                            provider_rows_peak: r.provider_rows_peak,
                        })
                    }),
                };
                let cursor = Cursor::open(
                    shape,
                    limits,
                    &session.source.stream_budget,
                    session.source.runtime.handle.clone(),
                    || async { Ok(native) },
                )
                .await?;
                (NativeCursor::Sqlite(cursor), stop)
            }
        };
        cancel.install(stop.clone());
        CURSORS.fetch_add(1, Ordering::Relaxed);
        Ok(Arc::new(Self {
            session,
            cursor: AsyncMutex::new(cursor),
            gate: Mutex::new(Some(gate)),
            stop,
            closing: AtomicBool::new(false),
            retired: AtomicBool::new(false),
            active: AtomicBool::new(false),
            changed: Notify::new(),
            last_end: AtomicBool::new(false),
            deadline,
            projection_id: projection.id,
            batch_live: AtomicBool::new(false),
            end_wire,
            end_sent: AtomicBool::new(false),
            retirement: Mutex::new(None),
        }))
    }
    pub async fn fetch(
        self: &Arc<Self>,
        cancel: Arc<crate::operation::Cancel>,
        rows: usize,
        bytes: usize,
    ) -> Result<crate::abi::Buffer> {
        if self.closing.load(Ordering::Acquire) {
            return Err(Error::closed());
        }
        if self.active.swap(true, Ordering::AcqRel) {
            return Err(Error::new(
                nativeweave_interop_abi::NW_DOMAIN_ERROR,
                7,
                "concurrent cursor fetch",
            ));
        }
        struct Active(Arc<CursorOwner>);
        impl Drop for Active {
            fn drop(&mut self) {
                self.0.active.store(false, Ordering::Release);
            }
        }
        let _active = Active(self.clone());
        if self.batch_live.swap(true, Ordering::AcqRel) {
            return Err(Error::state("release the previous batch before fetching"));
        }
        struct BatchClaim(Arc<CursorOwner>, bool);
        impl Drop for BatchClaim {
            fn drop(&mut self) {
                if !self.1 {
                    self.0.batch_live.store(false, Ordering::Release);
                }
            }
        }
        let mut claim = BatchClaim(self.clone(), false);
        if self.last_end.load(Ordering::Acquire) {
            if self.end_sent.swap(true, Ordering::AcqRel) {
                return Err(Error::state("cursor end was already transferred"));
            }
            claim.1 = true;
            return Ok(crate::abi::Buffer::cursor_end(
                self.clone(),
                self.end_wire.clone(),
            ));
        }
        cancel.install(self.stop.clone());
        let (result, stopped, timed_out) = {
            let mut cursor = self.cursor.lock().await;
            let work = cursor.fetch(rows, bytes);
            tokio::pin!(work);
            let mut stopped = false;
            let mut timed_out = false;
            let result = tokio::select! {
                biased;
                _ = cancel.notified() => { stopped = true; (self.stop)(); work.await },
                _ = tokio::time::sleep_until(tokio::time::Instant::from_std(self.deadline)) => { timed_out = true; (self.stop)(); work.await },
                value = &mut work => value,
            };
            (result, stopped, timed_out)
        };
        if stopped || timed_out {
            drop(result);
            self.request_close();
            self.join().await;
            return Err(if timed_out {
                Error::new(
                    nativeweave_interop_abi::NW_TIMEOUT,
                    8,
                    "query total deadline expired",
                )
            } else {
                Error::canceled()
            });
        }
        match result {
            Ok(batch) => {
                self.last_end.store(batch.end(), Ordering::Release);
                if batch.rows() == 0 {
                    self.end_sent.store(true, Ordering::Release);
                }
                claim.1 = true;
                Ok(crate::abi::Buffer::cursor_batch(self.clone(), batch))
            }
            Err(error) => {
                self.request_close();
                self.join().await;
                Err(error.into())
            }
        }
    }
    pub fn snapshot(&self) -> Vec<u8> {
        let state = self.retirement.lock().unwrap_or_else(|e| e.into_inner());
        let (cleanup, disposition, safe) = match state.as_ref() {
            Some(Ok(receipt)) => (
                if receipt.native_retired { 2 } else { 1 },
                receipt.disposition as u32,
                receipt.native_retired,
            ),
            Some(Err(_)) => (4, ConnectionDisposition::Quarantined as u32, false),
            None => (
                if self.closing.load(Ordering::Acquire) {
                    1
                } else {
                    0
                },
                0,
                false,
            ),
        };
        crate::abi::snapshot(0, 0, false, cleanup, disposition, safe, false, 0, None)
    }
    pub fn request_close(self: &Arc<Self>) {
        if self.closing.swap(true, Ordering::AcqRel) {
            return;
        }
        (self.stop)();
        let cursor = self.clone();
        self.session.source.runtime.handle.spawn(async move {
            let result = cursor.cursor.lock().await.close().await;
            let gate = cursor.gate.lock().unwrap_or_else(|e| e.into_inner()).take();
            if let Some(gate) = gate {
                gate.complete(result.is_err());
            }
            *cursor.retirement.lock().unwrap_or_else(|e| e.into_inner()) = Some(result);
            cursor.retired.store(true, Ordering::Release);
            cursor.changed.notify_waiters();
        });
    }
    pub async fn join(&self) {
        loop {
            let ready = self.changed.notified();
            tokio::pin!(ready);
            ready.as_mut().enable();
            if self.retired.load(Ordering::Acquire) {
                return;
            }
            ready.await;
        }
    }
}
impl Drop for CursorOwner {
    fn drop(&mut self) {
        CURSORS.fetch_sub(1, Ordering::Relaxed);
    }
}

pub enum Driver {
    #[cfg(feature = "postgres")]
    Postgres(nativeweave_data_postgres::PostgresTransactionDriver),
    #[cfg(feature = "sqlite")]
    Sqlite(nativeweave_data_sqlite::SqliteTransactionDriver),
}
macro_rules! dispatch{($self:expr,$method:ident $(,$argument:expr)*)=>{match $self{
    #[cfg(feature="postgres")]Driver::Postgres(d)=>d.$method($($argument),*),
    #[cfg(feature="sqlite")]Driver::Sqlite(d)=>d.$method($($argument),*),
}}}
macro_rules! dispatch_async{($self:expr,$method:ident $(,$argument:expr)*)=>{match $self{
    #[cfg(feature="postgres")]Driver::Postgres(d)=>d.$method($($argument),*).await,
    #[cfg(feature="sqlite")]Driver::Sqlite(d)=>d.$method($($argument),*).await,
}}}
impl TransactionDriver for Driver {
    fn capabilities(&self) -> Capabilities {
        dispatch!(self, capabilities)
    }
    fn session_id(&self) -> u64 {
        dispatch!(self, session_id)
    }
    async fn begin(&mut self, o: &TxOptions) -> std::result::Result<(), ProviderFailure> {
        dispatch_async!(self, begin, o)
    }
    async fn commit(&mut self) -> std::result::Result<Confirmation, ProviderFailure> {
        dispatch_async!(self, commit)
    }
    async fn rollback(&mut self) -> std::result::Result<ProtocolState, ProviderFailure> {
        dispatch_async!(self, rollback)
    }
    async fn savepoint_begin(&mut self) -> std::result::Result<(), ProviderFailure> {
        dispatch_async!(self, savepoint_begin)
    }
    async fn savepoint_rollback(&mut self) -> std::result::Result<(), ProviderFailure> {
        dispatch_async!(self, savepoint_rollback)
    }
    async fn savepoint_release(&mut self) -> std::result::Result<(), ProviderFailure> {
        dispatch_async!(self, savepoint_release)
    }
    async fn recover(&mut self) -> std::result::Result<ProtocolState, ProviderFailure> {
        dispatch_async!(self, recover)
    }
    async fn retire(&mut self, d: ConnectionDisposition) {
        dispatch_async!(self, retire, d)
    }
}
pub struct TransactionOwner {
    pub session: Arc<Session>,
    pub transaction: Transaction<Driver>,
}
impl TransactionOwner {
    pub fn snapshot(&self) -> Snapshot {
        self.transaction.snapshot()
    }
}
pub struct SavepointOwner {
    pub transaction: Arc<TransactionOwner>,
    pub token: SavepointToken,
}
