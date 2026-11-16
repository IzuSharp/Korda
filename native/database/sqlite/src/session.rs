use crate::{
    Admission, BeginMode, ErrorKind, Options, ProviderError, Result, admission::Reservation,
};
use futures_util::{Stream, StreamExt};
use nativeweave_data_codecs::{Limits as CodecLimits, Parameters};
use nativeweave_data_contracts::{ConnectionDisposition, Profile, wire::Expectation};
use sqlx::{
    AssertSqlSafe, Connection, Executor, SqlSafeStr, Sqlite, SqliteConnection, Statement,
    pool::PoolConnection,
    sqlite::{SqliteArguments, SqliteRow},
};
use sqlx_core::transaction::{TransactionManager, rollback_ansi_transaction_sql};
use std::{
    pin::Pin,
    sync::{
        Arc,
        atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering},
    },
    task::{Context, Poll},
};
use tokio::{
    runtime::Handle,
    sync::{Notify, OwnedSemaphorePermit, Semaphore, mpsc, oneshot, watch},
};

type Manager = <Sqlite as sqlx::Database>::TransactionManager;
static NEXT_SESSION: AtomicU64 = AtomicU64::new(1);
static NEXT_OPERATION: AtomicU64 = AtomicU64::new(1);

fn identity(counter: &AtomicU64) -> Result<u64> {
    counter
        .try_update(Ordering::AcqRel, Ordering::Acquire, |n| n.checked_add(1))
        .map_err(|_| ProviderError::new(ErrorKind::Capacity, "SQLite identity exhausted"))
}

/// A cancellation token can be claimed by exactly one operation generation.
/// Cancel is an atomic store: no worker lock, channel send, allocation or managed call.
#[derive(Clone, Default)]
pub struct Cancellation(Arc<CancelState>);
#[derive(Default)]
struct CancelState {
    canceled: AtomicBool,
    claimed: AtomicBool,
    changed: Notify,
}
impl Cancellation {
    pub fn cancel(&self) {
        self.0.canceled.store(true, Ordering::Release);
        self.0.changed.notify_one();
    }
    pub fn is_canceled(&self) -> bool {
        self.0.canceled.load(Ordering::Acquire)
    }
    async fn canceled(&self) {
        while !self.is_canceled() {
            self.0.changed.notified().await;
        }
    }
    fn claim(&self) -> Result<()> {
        if self.0.claimed.swap(true, Ordering::AcqRel) {
            return Err(ProviderError::new(
                ErrorKind::Configuration,
                "cancellation belongs to an earlier operation generation",
            ));
        }
        if self.is_canceled() {
            return Err(ProviderError::new(
                ErrorKind::CanceledBeforeDispatch,
                "SQLite operation canceled before dispatch",
            ));
        }
        Ok(())
    }
}
/// Independent stop access remains valid after the business stream is dropped.
#[derive(Clone)]
pub struct StopControl {
    generation: u64,
    cancellation: Cancellation,
}
impl StopControl {
    pub fn generation(&self) -> u64 {
        self.generation
    }
    pub fn request_stop(&self) {
        self.cancellation.cancel();
    }
}

/// Borrowed B01 envelope and trusted expectation. They are validated/copied after reservation.
pub struct ParameterInput<'a> {
    pub bytes: &'a [u8],
    pub expected: Expectation,
}

/// Actual observations after SQLx relinquishes this operation's handle and progress capture.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct OperationRetirement {
    pub generation: u64,
    pub native_retired: bool,
    pub progress_handler_removed: bool,
    pub disposition: ConnectionDisposition,
    pub provider_rows_peak: usize,
    pub stop_requested: bool,
    /// The provider actually stopped before observing natural completion. A late
    /// request after EOF can set stop_requested without changing this terminal fact.
    pub stopped: bool,
}
#[derive(Clone)]
pub struct OperationReceipt {
    rx: watch::Receiver<Option<OperationRetirement>>,
}
impl OperationReceipt {
    pub fn snapshot(&self) -> Option<OperationRetirement> {
        self.rx.borrow().clone()
    }
    pub async fn join(mut self) -> Result<OperationRetirement> {
        loop {
            if let Some(value) = self.rx.borrow().clone() {
                return Ok(value);
            }
            self.rx.changed().await.map_err(|_| {
                ProviderError::new(
                    ErrorKind::Closed,
                    "SQLite operation owner ended without a retirement receipt",
                )
            })?;
        }
    }
}

/// The SQLx shutdown acknowledgement is observable; an OS JoinHandle is not exposed.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct SessionRetirement {
    pub closing: bool,
    pub active_operations: usize,
    pub worker_shutdown_acknowledged: bool,
    pub physical_lease_released: bool,
    pub pool_return_completed: bool,
    pub os_thread_joined: bool,
    pub close_error: Option<ProviderError>,
}
#[derive(Clone)]
pub struct SessionObserver {
    rx: watch::Receiver<SessionRetirement>,
}
impl SessionObserver {
    pub fn snapshot(&self) -> SessionRetirement {
        self.rx.borrow().clone()
    }
    pub async fn join(mut self) -> Result<SessionRetirement> {
        loop {
            let value = self.rx.borrow().clone();
            if value.physical_lease_released {
                return value.close_error.clone().map_or(Ok(value), Err);
            }
            self.rx.changed().await.map_err(|_| {
                ProviderError::new(
                    ErrorKind::Closed,
                    "SQLite session owner ended before acknowledged closure",
                )
            })?;
        }
    }
}

/// Bounded SQLx row owner. Drop requests stop; only the independent receipt proves retirement.
pub struct RowStream {
    rx: mpsc::Receiver<Result<SqliteRow>>,
    rows: Arc<AtomicUsize>,
    control: StopControl,
    receipt: OperationReceipt,
}
impl RowStream {
    pub fn stop_control(&self) -> StopControl {
        self.control.clone()
    }
    pub fn receipt(&self) -> OperationReceipt {
        self.receipt.clone()
    }
    pub fn buffered_rows(&self) -> usize {
        self.rows.load(Ordering::Acquire)
    }
}
impl Stream for RowStream {
    type Item = Result<SqliteRow>;
    fn poll_next(mut self: Pin<&mut Self>, context: &mut Context<'_>) -> Poll<Option<Self::Item>> {
        match self.rx.poll_recv(context) {
            Poll::Ready(Some(value)) => {
                self.rows.fetch_sub(1, Ordering::AcqRel);
                Poll::Ready(Some(value))
            }
            other => other,
        }
    }
}
impl Drop for RowStream {
    fn drop(&mut self) {
        self.control.request_stop();
        self.rx.close();
        while self.rx.try_recv().is_ok() {
            self.rows.fetch_sub(1, Ordering::AcqRel);
        }
    }
}

struct Shared {
    closing: AtomicBool,
    return_to_pool: AtomicBool,
    close: Notify,
    observation: watch::Sender<SessionRetirement>,
}
struct SessionInner {
    id: u64,
    options: Options,
    admission: Admission,
    business: mpsc::Sender<Command>,
    control: mpsc::Sender<Control>,
    control_capacity: Arc<Semaphore>,
    shared: Arc<Shared>,
}
impl Drop for SessionInner {
    fn drop(&mut self) {
        self.shared.closing.store(true, Ordering::Release);
        self.shared.close.notify_one();
    }
}
/// A lease on one physical connection; clone never creates another SQLite worker.
/// The host enforces DataSession and transaction command admission above this provider seam.
#[derive(Clone)]
pub struct Session(Arc<SessionInner>);

struct Query {
    sql: String,
    arguments: SqliteArguments,
    token: Cancellation,
    generation: u64,
    reservation: Reservation,
    receipt: watch::Sender<Option<OperationRetirement>>,
}
enum Command {
    Execute(Query, oneshot::Sender<Result<u64>>),
    Describe(
        Query,
        oneshot::Sender<Result<sqlx::sqlite::SqliteStatement>>,
    ),
    Stream(Query, mpsc::Sender<Result<SqliteRow>>, Arc<AtomicUsize>),
}
pub(crate) enum ControlKind {
    Begin(bool),
    Commit,
    Rollback,
    SavepointBegin,
    SavepointRollback,
    SavepointRelease,
    Recover,
}
struct Control {
    kind: ControlKind,
    result: oneshot::Sender<Result<()>>,
    _capacity: OwnedSemaphorePermit,
}

impl Session {
    /// Transfers a host-owned pool lease to a controlled engine-local async task.
    /// The caller keeps this runtime alive until the returned observer joins closure.
    pub fn start(
        connection: PoolConnection<Sqlite>,
        options: Options,
        admission: Admission,
        runtime: &Handle,
    ) -> Result<Self> {
        let id = identity(&NEXT_SESSION)?;
        let (business, rx) = mpsc::channel(admission.limits().max_commands);
        let (control, control_rx) = mpsc::channel(1);
        let (observation, _) = watch::channel(SessionRetirement::default());
        let shared = Arc::new(Shared {
            closing: AtomicBool::new(false),
            return_to_pool: AtomicBool::new(false),
            close: Notify::new(),
            observation,
        });
        runtime.spawn(run_actor(
            connection,
            options.clone(),
            admission.limits().progress_operations,
            rx,
            control_rx,
            shared.clone(),
        ));
        Ok(Self(Arc::new(SessionInner {
            id,
            options,
            admission,
            business,
            control,
            control_capacity: Arc::new(Semaphore::new(1)),
            shared,
        })))
    }
    pub fn session_id(&self) -> u64 {
        self.0.id
    }
    pub fn options(&self) -> &Options {
        &self.0.options
    }
    pub fn observer(&self) -> SessionObserver {
        SessionObserver {
            rx: self.0.shared.observation.subscribe(),
        }
    }
    pub fn request_close(&self) {
        self.0.shared.closing.store(true, Ordering::Release);
        self.0.shared.close.notify_one();
    }
    pub async fn close(&self) -> Result<SessionRetirement> {
        self.request_close();
        self.observer().join().await
    }
    /// Stops admission, explicitly rolls back remaining tracked scopes, and awaits
    /// SQLx's public return-to-pool future. This is a pool handoff, not worker closure.
    pub async fn return_to_pool(&self) -> Result<SessionRetirement> {
        self.0.shared.return_to_pool.store(true, Ordering::Release);
        self.request_close();
        self.observer().join().await
    }
    fn prepare(
        &self,
        sql: &str,
        parameters: Option<ParameterInput<'_>>,
        token: Cancellation,
    ) -> Result<(Query, OperationReceipt)> {
        if self.0.shared.closing.load(Ordering::Acquire) {
            return Err(closed());
        }
        token.claim()?;
        let limits = self.0.admission.limits();
        if sql.is_empty() || sql.len() > limits.max_sql_bytes || sql.contains('\0') {
            return Err(ProviderError::new(
                ErrorKind::Configuration,
                "invalid SQLite SQL byte length",
            ));
        }
        // The wire and provider argument copies can coexist. B07 checks provider expansion
        // against this explicit remaining budget; upstream allocations are separate evidence.
        let wire_bytes = parameters.as_ref().map_or(0, |p| p.bytes.len());
        let input_bytes = sql
            .len()
            .checked_add(wire_bytes.checked_mul(2).ok_or_else(capacity)?)
            .ok_or_else(capacity)?;
        let reservation = self.0.admission.reserve(input_bytes)?;
        let arguments = match parameters {
            Some(input) => {
                if input.expected.profile != Profile::Sqlite {
                    return Err(ProviderError::new(
                        ErrorKind::Codec,
                        "SQLite parameter profile required",
                    ));
                }
                let available = limits
                    .max_input_bytes
                    .saturating_sub(sql.len())
                    .min(wire_bytes.max(1));
                Parameters::decode(
                    input.bytes,
                    input.expected,
                    CodecLimits {
                        max_parameters: nativeweave_data_contracts::MAX_NODES,
                        max_parameter_bytes: available,
                        max_field_bytes: available,
                    },
                )?
                .sqlite()?
            }
            None => SqliteArguments::default(),
        };
        let generation = identity(&NEXT_OPERATION)?;
        let (receipt, rx) = watch::channel(None);
        Ok((
            Query {
                sql: sql.to_owned(),
                arguments,
                token,
                generation,
                reservation,
                receipt,
            },
            OperationReceipt { rx },
        ))
    }
    /// Executes in the retained provider coordinator; dropping this waiter never drops SQLx work.
    pub async fn execute(
        &self,
        sql: &str,
        parameters: Option<ParameterInput<'_>>,
        token: Cancellation,
    ) -> Result<u64> {
        let (query, _) = self.prepare(sql, parameters, token)?;
        let (tx, rx) = oneshot::channel();
        self.0
            .business
            .try_send(Command::Execute(query, tx))
            .map_err(|_| capacity())?;
        rx.await.map_err(|_| closed())?
    }
    /// Returns actual SQLx rows through a finite adapter channel. B06 owns wire batching.
    pub fn stream(
        &self,
        sql: &str,
        parameters: Option<ParameterInput<'_>>,
        token: Cancellation,
    ) -> Result<RowStream> {
        let (query, receipt) = self.prepare(sql, parameters, token)?;
        let control = StopControl {
            generation: query.generation,
            cancellation: query.token.clone(),
        };
        let (tx, rx) = mpsc::channel(self.0.admission.limits().row_channel_capacity);
        let rows = Arc::new(AtomicUsize::new(0));
        self.0
            .business
            .try_send(Command::Stream(query, tx, rows.clone()))
            .map_err(|_| capacity())?;
        Ok(RowStream {
            rx,
            rows,
            control,
            receipt,
        })
    }
    /// Returns owned SQLx statement metadata through the same bounded physical-session
    /// actor. Empty results can be shape-checked before streaming; computed NULL type
    /// metadata may remain unknown and must be checked against each actual row.
    pub async fn describe(
        &self,
        sql: &str,
        token: Cancellation,
    ) -> Result<sqlx::sqlite::SqliteStatement> {
        let (query, _) = self.prepare(sql, None, token)?;
        let (tx, rx) = oneshot::channel();
        self.0
            .business
            .try_send(Command::Describe(query, tx))
            .map_err(|_| capacity())?;
        rx.await.map_err(|_| closed())?
    }
    pub(crate) async fn control(&self, kind: ControlKind) -> Result<()> {
        if self.0.shared.closing.load(Ordering::Acquire) {
            return Err(closed());
        }
        let capacity = self
            .0
            .control_capacity
            .clone()
            .try_acquire_owned()
            .map_err(|_| capacity())?;
        let (tx, rx) = oneshot::channel();
        self.0
            .control
            .try_send(Control {
                kind,
                result: tx,
                _capacity: capacity,
            })
            .map_err(|_| closed())?;
        rx.await.map_err(|_| closed())?
    }
}
fn closed() -> ProviderError {
    ProviderError::new(ErrorKind::Closed, "SQLite physical session is closing")
}
fn capacity() -> ProviderError {
    ProviderError::new(ErrorKind::Capacity, "SQLite admission capacity exceeded")
}

async fn run_actor(
    mut connection: PoolConnection<Sqlite>,
    options: Options,
    progress_operations: i32,
    mut business: mpsc::Receiver<Command>,
    mut control: mpsc::Receiver<Control>,
    shared: Arc<Shared>,
) {
    loop {
        if shared.closing.load(Ordering::Acquire) {
            break;
        }
        tokio::select! {
            biased;
            _ = shared.close.notified() => {},
            item = control.recv() => if let Some(item) = item {
                shared.observation.send_modify(|s| s.active_operations = 1);
                let result = run_control(&mut connection, &options, item.kind).await;
                shared.observation.send_modify(|s| s.active_operations = 0);
                let _ = item.result.send(result);
            } else { break; },
            item = business.recv() => if let Some(item) = item {
                shared.observation.send_modify(|s| s.active_operations = 1);
                run_query(&mut connection, progress_operations, item, &shared).await;
                shared.observation.send_modify(|s| s.active_operations = 0);
            } else { break; },
        }
    }
    shared.closing.store(true, Ordering::Release);
    shared.observation.send_modify(|s| s.closing = true);
    business.close();
    control.close();
    while let Ok(command) = business.try_recv() {
        reject(command);
    }
    while let Ok(command) = control.try_recv() {
        let _ = command.result.send(Err(closed()));
    }
    // SQLx shutdown may synchronously send to its bounded worker queue. This code runs
    // only in the retained coordinator, never in a destructor or reverse callback.
    let mut returned = false;
    let result = if shared.return_to_pool.load(Ordering::Acquire)
        && run_control(&mut connection, &options, ControlKind::Rollback)
            .await
            .is_ok()
        && run_control(&mut connection, &options, ControlKind::Recover)
            .await
            .is_ok()
    {
        connection.return_to_pool().await;
        returned = true;
        Ok(())
    } else {
        connection.close().await.map_err(ProviderError::from)
    };
    shared.observation.send_modify(|s| {
        s.worker_shutdown_acknowledged = result.is_ok() && !returned;
        s.physical_lease_released = true;
        s.pool_return_completed = returned;
        s.close_error = result.err();
        // SQLx exposes no OS JoinHandle. Do not equate its shutdown ACK with thread join.
        s.os_thread_joined = false;
    });
}

fn reject(command: Command) {
    let (query, rows_peak) = match command {
        Command::Execute(query, tx) => {
            let _ = tx.send(Err(closed()));
            (query, 0)
        }
        Command::Describe(query, tx) => {
            let _ = tx.send(Err(closed()));
            (query, 0)
        }
        Command::Stream(query, tx, count) => {
            count.fetch_add(1, Ordering::AcqRel);
            if tx.try_send(Err(closed())).is_err() {
                count.fetch_sub(1, Ordering::AcqRel);
            }
            (query, 1)
        }
    };
    finish(
        query,
        ConnectionDisposition::Discarded,
        true,
        rows_peak,
        true,
    );
}

async fn run_query(
    connection: &mut SqliteConnection,
    progress_operations: i32,
    command: Command,
    shared: &Arc<Shared>,
) {
    let (mut query, execute, stream, describe) = match command {
        Command::Execute(query, tx) => (query, Some(tx), None, None),
        Command::Stream(query, tx, count) => (query, None, Some((tx, count)), None),
        Command::Describe(query, tx) => (query, None, None, Some(tx)),
    };
    query.reservation.start();
    let token = query.token.clone();
    let closing = shared.clone();
    let installed = if token.is_canceled() {
        Err(ProviderError::new(
            ErrorKind::CanceledBeforeDispatch,
            "SQLite operation canceled before worker dispatch",
        ))
    } else {
        connection
            .lock_handle()
            .await
            .map_err(ProviderError::from)
            .map(|mut handle| {
                handle.set_progress_handler(progress_operations, move || {
                    !token.is_canceled() && !closing.closing.load(Ordering::Acquire)
                });
            })
    };
    let mut peak = 0;
    let mut operation_error = installed.err();
    let mut affected = 0;
    let mut stopped = false;
    let mut description = None;
    if operation_error.is_none() && describe.is_some() {
        match connection
            .prepare(AssertSqlSafe(std::mem::take(&mut query.sql)).into_sql_str())
            .await
        {
            Ok(value) if value.columns().len() <= nativeweave_data_contracts::MAX_NODES => {
                description = Some(value)
            }
            Ok(_) => operation_error = Some(capacity()),
            Err(error) => operation_error = Some(error.into()),
        }
    }
    if operation_error.is_none() && describe.is_none() {
        let arguments = std::mem::take(&mut query.arguments);
        let prepared = sqlx::query_with(AssertSqlSafe(std::mem::take(&mut query.sql)), arguments);
        if let Some((sender, count)) = stream.as_ref() {
            let mut rows = prepared.fetch(&mut *connection);
            while let Some(row) = rows.next().await {
                let row = row.map_err(ProviderError::from);
                if let Err(error) = &row {
                    operation_error = Some(error.clone());
                    let retained = count.fetch_add(1, Ordering::AcqRel) + 1;
                    peak = peak.max(retained);
                    if sender.try_send(row).is_err() {
                        count.fetch_sub(1, Ordering::AcqRel);
                    }
                    break;
                }
                let retained = count.fetch_add(1, Ordering::AcqRel) + 1;
                peak = peak.max(retained);
                let sent = tokio::select! {
                    result = sender.send(row) => result.is_ok(),
                    _ = query.token.canceled() => false,
                    _ = shared.close.notified() => false,
                };
                if !sent {
                    count.fetch_sub(1, Ordering::AcqRel);
                    stopped = true;
                    break;
                }
                if operation_error.is_some() || query.token.is_canceled() {
                    stopped = true;
                    break;
                }
            }
            drop(rows);
        } else {
            match prepared.execute(&mut *connection).await {
                Ok(result) => affected = result.rows_affected(),
                Err(error) => operation_error = Some(error.into()),
            }
        }
    }
    // Lock acquisition is a worker barrier even if the row receiver/future was dropped.
    // No deadline can skip it. Clearing cached statements then probing transaction state
    // separates ignored reset results from a later observed same-connection readiness.
    let removed = match connection.lock_handle().await {
        Ok(mut handle) => {
            handle.remove_progress_handler();
            true
        }
        Err(error) => {
            operation_error.get_or_insert_with(|| error.into());
            false
        }
    };
    let clean = if removed {
        match connection.clear_cached_statements().await {
            Ok(()) => probe(connection).await.is_ok(),
            Err(_) => false,
        }
    } else {
        false
    };
    if !clean {
        shared.closing.store(true, Ordering::Release);
        shared.close.notify_one();
    }
    if let Some(tx) = execute {
        let _ = tx.send(operation_error.clone().map_or(Ok(affected), Err));
    }
    if let Some(tx) = describe {
        let _ = tx.send(match operation_error.clone() {
            Some(error) => Err(error),
            None => description.ok_or_else(closed),
        });
    }
    stopped |= operation_error.as_ref().is_some_and(|e| {
        matches!(
            e.kind,
            ErrorKind::Interrupted | ErrorKind::CanceledBeforeDispatch
        )
    });
    if let Some((tx, count)) = stream {
        if let Some(error) = operation_error {
            // Initial admission/install errors have not yet entered the row stream.
            if peak == 0 {
                count.fetch_add(1, Ordering::AcqRel);
                if tx.send(Err(error)).await.is_err() {
                    count.fetch_sub(1, Ordering::AcqRel);
                }
            }
        }
        drop(tx);
    }
    finish(
        query,
        if clean {
            ConnectionDisposition::Usable
        } else {
            ConnectionDisposition::Quarantined
        },
        removed,
        peak,
        stopped,
    );
}
fn finish(
    query: Query,
    disposition: ConnectionDisposition,
    removed: bool,
    peak: usize,
    stopped: bool,
) {
    let Query {
        generation,
        token,
        reservation,
        receipt,
        ..
    } = query;
    drop(reservation);
    receipt.send_replace(Some(OperationRetirement {
        generation,
        native_retired: removed,
        progress_handler_removed: removed,
        disposition,
        provider_rows_peak: peak,
        stop_requested: token.is_canceled(),
        stopped,
    }));
}

async fn probe(connection: &mut SqliteConnection) -> Result<()> {
    connection.ping().await?;
    let depth = Manager::get_transaction_depth(connection);
    let mut handle = connection.lock_handle().await?;
    // SAFETY: SQLx's public LockedSqliteHandle grants exclusive ownership of this
    // live sqlite3 handle for the guard lifetime. This read neither modifies nor retains it.
    let autocommit =
        unsafe { libsqlite3_sys::sqlite3_get_autocommit(handle.as_raw_handle().as_ptr()) } != 0;
    if autocommit != (depth == 0) {
        return Err(ProviderError::new(
            ErrorKind::Protocol,
            "SQLite engine and tracked transaction depth disagree",
        ));
    }
    Ok(())
}
async fn run_control(
    connection: &mut SqliteConnection,
    options: &Options,
    kind: ControlKind,
) -> Result<()> {
    match kind {
        ControlKind::Begin(read_only) => {
            if Manager::get_transaction_depth(connection) != 0 {
                return Err(ProviderError::new(
                    ErrorKind::Protocol,
                    "nested top-level SQLite transaction",
                ));
            }
            sqlx::query(if read_only {
                "PRAGMA query_only = ON"
            } else {
                "PRAGMA query_only = OFF"
            })
            .execute(&mut *connection)
            .await?;
            let statement = match options.begin {
                BeginMode::Deferred => "BEGIN DEFERRED",
                BeginMode::Immediate => "BEGIN IMMEDIATE",
            };
            Manager::begin(connection, Some(statement.into_sql_str())).await?;
        }
        ControlKind::Commit => {
            if Manager::get_transaction_depth(connection) == 0 {
                return Err(ProviderError::new(
                    ErrorKind::Protocol,
                    "SQLite commit has no active transaction",
                ));
            }
            while Manager::get_transaction_depth(connection) > 0 {
                Manager::commit(connection).await?;
            }
            // This is the trusted outer COMMIT acknowledgement. Later reset/probe errors
            // belong to recover(), and must not turn a confirmed commit into Unknown.
            return Ok(());
        }
        ControlKind::Rollback => {
            while Manager::get_transaction_depth(connection) > 0 {
                Manager::rollback(connection).await?;
            }
            return Ok(());
        }
        ControlKind::SavepointBegin => {
            if Manager::get_transaction_depth(connection) == 0 {
                return Err(ProviderError::new(
                    ErrorKind::Protocol,
                    "SQLite savepoint requires outer transaction",
                ));
            }
            Manager::begin(connection, None).await?;
        }
        ControlKind::SavepointRollback => {
            let depth = Manager::get_transaction_depth(connection);
            if depth < 2 {
                return Err(ProviderError::new(
                    ErrorKind::Protocol,
                    "SQLite savepoint is absent",
                ));
            }
            // SQLx's public generated statement preserves its own safe name. A retained
            // ROLLBACK TO must not call manager.rollback(), which decrements tracked depth.
            sqlx::query(rollback_ansi_transaction_sql(depth))
                .execute(&mut *connection)
                .await?;
        }
        ControlKind::SavepointRelease => {
            if Manager::get_transaction_depth(connection) < 2 {
                return Err(ProviderError::new(
                    ErrorKind::Protocol,
                    "SQLite savepoint is absent",
                ));
            }
            Manager::commit(connection).await?;
        }
        ControlKind::Recover => {
            if Manager::get_transaction_depth(connection) != 0 {
                return Err(ProviderError::new(
                    ErrorKind::Protocol,
                    "SQLite recovery still has a tracked transaction",
                ));
            }
            sqlx::query("PRAGMA query_only = OFF")
                .execute(&mut *connection)
                .await?;
            connection.clear_cached_statements().await?;
        }
    }
    probe(connection).await
}
