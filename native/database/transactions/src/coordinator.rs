use crate::*;
use nativeweave_data_contracts::{
    ConnectionDisposition as Disposition, LocalLifetime, TransactionOutcome as Outcome,
};
use std::sync::{
    Arc, Mutex,
    atomic::{AtomicU64, AtomicUsize, Ordering},
};
use std::time::Duration;
use tokio::sync::{Mutex as AsyncMutex, oneshot, watch};

static NEXT_GENERATION: AtomicU64 = AtomicU64::new(1);
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Operation {
    Commit,
    Rollback,
    Savepoint,
    Command,
}
struct State {
    snapshot: Snapshot,
    closing: bool,
    operation: Option<Operation>,
    primary: Option<Result<(), Error>>,
    savepoints: Vec<SavepointToken>,
    next_savepoint: u64,
    cleanup_failure: Option<CleanupStatus>,
}
struct Inner<D> {
    state: Mutex<State>,
    driver: AsyncMutex<Option<D>>,
    options: Options,
    changed: watch::Sender<u64>,
    owners: AtomicUsize,
    runtime: tokio::runtime::Handle,
}
impl<D> Inner<D> {
    fn lock(&self) -> std::sync::MutexGuard<'_, State> {
        // No provider/user code runs while this lock is held. A poisoned lock is
        // a process-local contract failure, not permission to continue reuse.
        self.state.lock().expect("transaction state poisoned")
    }
    fn notify(&self) {
        self.changed
            .send_modify(|epoch| *epoch = epoch.wrapping_add(1));
    }
}
/// One native transaction owner. Clones share admission and never create another transaction.
/// Dropping the last public owner transfers cleanup to the retained engine task.
pub struct Transaction<D: TransactionDriver> {
    inner: Arc<Inner<D>>,
}
impl<D: TransactionDriver> Clone for Transaction<D> {
    fn clone(&self) -> Self {
        self.inner.owners.fetch_add(1, Ordering::Relaxed);
        Self {
            inner: Arc::clone(&self.inner),
        }
    }
}
impl<D: TransactionDriver> Drop for Transaction<D> {
    fn drop(&mut self) {
        if self.inner.owners.fetch_sub(1, Ordering::AcqRel) == 1 {
            {
                let mut state = self.inner.lock();
                if state.snapshot.local_resources_released {
                    state.snapshot.lifetime = LocalLifetime::Released;
                    return;
                }
            }
            request_close(Arc::clone(&self.inner));
        }
    }
}
impl<D: TransactionDriver> Transaction<D> {
    /// Validate capability/option requests before calling BEGIN. Once dispatched,
    /// an abandoned waiter cannot abandon the owning driver future or its cleanup.
    pub async fn begin(
        mut driver: D,
        options: Options,
        admission: &AdmissionToken,
    ) -> Result<Self, Error> {
        options.validate(driver.capabilities())?;
        if admission.is_canceled() {
            return Err(Error::CanceledBeforeDispatch);
        }
        let generation = NEXT_GENERATION
            .try_update(Ordering::Relaxed, Ordering::Relaxed, |n| n.checked_add(1))
            .map_err(|_| Error::GenerationExhausted)?;
        let session_id = driver.session_id();
        let (sender, receiver) = oneshot::channel();
        tokio::spawn(async move {
            match driver.begin(&options).await {
                Ok(()) => {
                    let (changed, _) = watch::channel(0);
                    let tx = Self {
                        inner: Arc::new(Inner {
                            state: Mutex::new(State {
                                snapshot: Snapshot {
                                    state: Lifecycle::Active,
                                    outcome: Outcome::InProgress,
                                    disposition: Disposition::Usable,
                                    lifetime: LocalLifetime::Open,
                                    commit_attempted: false,
                                    cleanup: CleanupStatus::NotStarted,
                                    correlation_id: 0,
                                    provider_code: None,
                                    deadline_exceeded: false,
                                    local_resources_released: false,
                                    generation,
                                    session_id,
                                    resources: Resources {
                                        physical_lease: 1,
                                        driver_operations: 0,
                                        cleanup_owners: 0,
                                    },
                                },
                                closing: false,
                                operation: None,
                                primary: None,
                                savepoints: Vec::new(),
                                next_savepoint: 1,
                                cleanup_failure: None,
                            }),
                            driver: AsyncMutex::new(Some(driver)),
                            options,
                            changed,
                            owners: AtomicUsize::new(1),
                            runtime: tokio::runtime::Handle::current(),
                        }),
                    };
                    // If the waiter left, dropping its unsent owner starts cleanup.
                    let _ = sender.send(Ok(tx));
                }
                Err(error) => {
                    driver.retire(Disposition::Discarded).await;
                    let _ = sender.send(Err(Error::Provider {
                        provider_code: error.code,
                        correlation_id: error.correlation_id,
                    }));
                }
            }
        });
        receiver.await.map_err(|_| Error::InvalidState)?
    }
    /// Reads the four independent dimensions without performing provider traffic.
    pub fn snapshot(&self) -> Snapshot {
        self.inner.lock().snapshot.clone()
    }
    /// An owned recovery identity, independent from native connection lifetime.
    pub fn recovery(&self) -> Option<&RecoveryIdentity> {
        self.inner.options.recovery.as_ref()
    }
    /// Atomically closes commit admission before dispatch. Request cancellation is
    /// read exactly here; after this boundary the independent deadline owns progress.
    pub async fn commit(&self, admission: &AdmissionToken) -> Result<(), Error> {
        {
            let mut state = self.inner.lock();
            if state.closing
                || state.operation.is_some()
                || state.snapshot.state != Lifecycle::Active
            {
                return Err(Error::InvalidState);
            }
            if admission.is_canceled() {
                return Err(Error::CanceledBeforeDispatch);
            }
            admit_finish(&mut state, true);
        }
        spawn_finish(Arc::clone(&self.inner), true);
        wait_primary(&self.inner).await
    }
    /// An explicit rollback observes cancellation only before admission. Automatic
    /// close always uses separate cleanup admission and never this canceled token.
    pub async fn rollback(&self, admission: &AdmissionToken) -> Result<(), Error> {
        {
            let mut state = self.inner.lock();
            if state.closing
                || state.operation.is_some()
                || !matches!(state.snapshot.state, Lifecycle::Active | Lifecycle::Failed)
            {
                return Err(Error::InvalidState);
            }
            if admission.is_canceled() {
                return Err(Error::CanceledBeforeDispatch);
            }
            admit_finish(&mut state, false);
        }
        spawn_finish(Arc::clone(&self.inner), false);
        wait_primary(&self.inner).await
    }
    /// Starts closure even when the wait budget is zero; a bounded waiter cannot
    /// cancel cleanup. `join` is the separate unbounded safety join.
    pub fn request_close(&self) {
        request_close(Arc::clone(&self.inner));
    }
    pub async fn close(&self, wait_budget: Duration) -> Snapshot {
        request_close(Arc::clone(&self.inner));
        let future = wait_retired(&self.inner);
        let _ = tokio::time::timeout(wait_budget, future).await;
        self.snapshot()
    }
    /// Joins actual local retirement and suppresses cleanup errors already in the
    /// immutable snapshot, preserving an earlier business/commit exception.
    pub async fn join(&self) -> Snapshot {
        request_close(Arc::clone(&self.inner));
        wait_retired(&self.inner).await;
        self.snapshot()
    }
    /// Starts one tracked savepoint on the same physical connection.
    pub async fn create_savepoint(
        &self,
        admission: &AdmissionToken,
    ) -> Result<SavepointToken, Error> {
        let token;
        {
            let mut state = self.inner.lock();
            if state.closing
                || state.operation.is_some()
                || state.snapshot.state != Lifecycle::Active
            {
                return Err(Error::InvalidState);
            }
            if admission.is_canceled() {
                return Err(Error::CanceledBeforeDispatch);
            }
            token = SavepointToken {
                generation: state.snapshot.generation,
                sequence: state.next_savepoint,
            };
            state.next_savepoint = state
                .next_savepoint
                .checked_add(1)
                .ok_or(Error::GenerationExhausted)?;
            state.operation = Some(Operation::Savepoint);
            state.snapshot.resources.driver_operations = 1;
        }
        control(Arc::clone(&self.inner), token, 0).await?;
        Ok(token)
    }
    /// Rolls back to the top entry, retaining it and restoring Active only after
    /// the actual provider confirms the savepoint recovery.
    pub async fn rollback_savepoint(
        &self,
        token: SavepointToken,
        admission: &AdmissionToken,
    ) -> Result<(), Error> {
        self.admit_savepoint(token, admission, true)?;
        control(Arc::clone(&self.inner), token, 1).await
    }
    /// Releases only the top entry; never commits the caller-owned outer transaction.
    pub async fn release_savepoint(
        &self,
        token: SavepointToken,
        admission: &AdmissionToken,
    ) -> Result<(), Error> {
        self.admit_savepoint(token, admission, false)?;
        control(Arc::clone(&self.inner), token, 2).await
    }
    fn admit_savepoint(
        &self,
        token: SavepointToken,
        admission: &AdmissionToken,
        recovery: bool,
    ) -> Result<(), Error> {
        let mut state = self.inner.lock();
        if state.closing
            || state.operation.is_some()
            || !matches!(state.snapshot.state, Lifecycle::Active | Lifecycle::Failed)
        {
            return Err(Error::InvalidState);
        }
        if state.snapshot.state == Lifecycle::Failed && !recovery {
            return Err(Error::InvalidState);
        }
        if token.generation != state.snapshot.generation || state.savepoints.last() != Some(&token)
        {
            return Err(Error::SavepointOrder);
        }
        if admission.is_canceled() {
            return Err(Error::CanceledBeforeDispatch);
        }
        state.operation = Some(Operation::Savepoint);
        state.snapshot.resources.driver_operations = 1;
        Ok(())
    }
    /// Disposing an unreleased top entry rolls back then releases. Non-top disposal
    /// closes outer admission, records cleanup failure and never reorders SQL.
    pub async fn dispose_savepoint(&self, token: SavepointToken) -> Result<(), Error> {
        let invalid;
        {
            let mut state = self.inner.lock();
            if state.snapshot.state == Lifecycle::Closed || !state.savepoints.contains(&token) {
                return Ok(());
            }
            invalid = state.savepoints.last() != Some(&token);
            if invalid {
                state.snapshot.cleanup = CleanupStatus::Failed;
                state.cleanup_failure = Some(CleanupStatus::Failed);
                state.snapshot.provider_code = Some("savepoint-order".to_owned());
            }
        }
        if invalid {
            request_close(Arc::clone(&self.inner));
            return Err(Error::SavepointOrder);
        }
        let admission = AdmissionToken::default();
        self.rollback_savepoint(token, &admission).await?;
        self.release_savepoint(token, &admission).await
    }
    /// Provider integration holds this guard until the real command/cursor future
    /// and its native accessors retire. It is separate from the business waiter.
    pub fn admit_command(
        &self,
        session_mutating: bool,
        admission: &AdmissionToken,
    ) -> Result<CommandLease<D>, Error> {
        let mut state = self.inner.lock();
        if session_mutating
            || state.closing
            || state.operation.is_some()
            || state.snapshot.state != Lifecycle::Active
        {
            return Err(Error::InvalidState);
        }
        if admission.is_canceled() {
            return Err(Error::CanceledBeforeDispatch);
        }
        state.operation = Some(Operation::Command);
        state.snapshot.resources.driver_operations = 1;
        Ok(CommandLease {
            inner: Some(Arc::clone(&self.inner)),
        })
    }
}
/// Same-session command admission. Call `complete` only after native accessors
/// retire; abandoning an unconfirmed operation quarantines the transaction.
pub struct CommandLease<D: TransactionDriver> {
    inner: Option<Arc<Inner<D>>>,
}
impl<D: TransactionDriver> CommandLease<D> {
    pub fn complete(mut self, invalidates_transaction: bool, protocol: ProtocolState) {
        let inner = self.inner.take().expect("command lease completed once");
        {
            let mut state = inner.lock();
            state.operation = None;
            state.snapshot.resources.driver_operations = 0;
            if invalidates_transaction {
                state.snapshot.state = Lifecycle::Failed;
            }
            state.snapshot.disposition = disposition(protocol);
            if protocol == ProtocolState::Untrusted {
                state.closing = true;
            }
        }
        inner.notify();
        continue_close(inner);
    }
}
impl<D: TransactionDriver> Drop for CommandLease<D> {
    fn drop(&mut self) {
        if let Some(inner) = self.inner.take() {
            {
                let mut state = inner.lock();
                state.operation = None;
                state.snapshot.resources.driver_operations = 0;
                state.snapshot.state = Lifecycle::Failed;
                state.snapshot.disposition = Disposition::Quarantined;
                state.closing = true;
            }
            inner.notify();
            continue_close(inner);
        }
    }
}
fn admit_finish(state: &mut State, commit: bool) {
    state.snapshot.state = if commit {
        Lifecycle::CommitInFlight
    } else {
        Lifecycle::RollbackInFlight
    };
    state.snapshot.commit_attempted |= commit;
    state.operation = Some(if commit {
        Operation::Commit
    } else {
        Operation::Rollback
    });
    state.snapshot.resources.driver_operations = 1;
    state.snapshot.resources.cleanup_owners = 1;
    state.snapshot.lifetime = LocalLifetime::Closing;
    state.snapshot.cleanup = CleanupStatus::Pending;
}
fn disposition(protocol: ProtocolState) -> Disposition {
    match protocol {
        ProtocolState::Clean => Disposition::Usable,
        ProtocolState::ResetPending => Disposition::ProviderResetPending,
        ProtocolState::Untrusted => Disposition::Quarantined,
    }
}
async fn wait_primary<D>(inner: &Inner<D>) -> Result<(), Error> {
    let mut changed = inner.changed.subscribe();
    loop {
        if let Some(result) = inner.lock().primary.clone() {
            return result;
        }
        changed.changed().await.map_err(|_| Error::InvalidState)?;
    }
}
async fn wait_retired<D>(inner: &Inner<D>) {
    let mut changed = inner.changed.subscribe();
    loop {
        if inner.lock().snapshot.local_resources_released {
            return;
        }
        if changed.changed().await.is_err() {
            return;
        }
    }
}
fn request_close<D: TransactionDriver>(inner: Arc<Inner<D>>) {
    {
        let mut state = inner.lock();
        if state.snapshot.local_resources_released {
            return;
        }
        state.closing = true;
        state.snapshot.lifetime = LocalLifetime::Closing;
        state.snapshot.cleanup = CleanupStatus::Pending;
        // While an accessor remains, a bounded close may not advertise reuse.
        if state.operation.is_some() {
            state.snapshot.disposition = Disposition::Quarantined;
        }
    }
    inner.notify();
    continue_close(inner);
}
fn continue_close<D: TransactionDriver>(inner: Arc<Inner<D>>) {
    let start;
    {
        let mut state = inner.lock();
        start = state.closing
            && state.operation.is_none()
            && matches!(state.snapshot.state, Lifecycle::Active | Lifecycle::Failed);
        if start {
            admit_finish(&mut state, false);
        }
    }
    if start {
        spawn_finish(inner, false);
    }
}
fn spawn_finish<D: TransactionDriver>(inner: Arc<Inner<D>>, commit: bool) {
    let runtime = inner.runtime.clone();
    runtime.spawn(async move {
        let mut guard = inner.driver.lock().await;
        let driver = guard.as_mut().expect("driver retained until retirement");
        if commit {
            let result = {
                let call = driver.commit();
                tokio::pin!(call);
                tokio::select! {
                    result = &mut call => result,
                    _ = tokio::time::sleep(inner.options.commit_timeout) => {
                        mark_deadline(&inner, true);
                        // The call remains pinned and polled to retirement. In
                        // particular SQLite worker ownership cannot be dropped.
                        call.await
                    }
                }
            };
            {
                let mut state = inner.lock();
                if state.snapshot.outcome != Outcome::Unknown {
                    match &result {
                        Ok(Confirmation::Committed) => { state.snapshot.outcome = Outcome::Committed; state.primary = Some(Ok(())); }
                        Ok(Confirmation::RolledBack(failure)) => {
                            state.snapshot.outcome = Outcome::RolledBack;
                            set_diagnostic(&mut state, failure);
                            state.primary = Some(Err(Error::Provider { provider_code: failure.code.clone(), correlation_id: failure.correlation_id }));
                        }
                        Err(error) => { state.snapshot.outcome = Outcome::Unknown; state.snapshot.disposition = Disposition::Quarantined; state.primary = Some(Err(Error::CommitUnknown { provider_code: error.code.clone(), correlation_id: error.correlation_id })); }
                    }
                }
                if let Err(error) = &result { set_diagnostic(&mut state, error); }
                state.snapshot.state = Lifecycle::Closed;
                state.closing = true;
            }
            inner.notify();
            if result.is_err() { cleanup_rollback(&inner, driver).await; }
        } else {
            let result = rollback_call(&inner, driver, true).await;
            {
                let mut state = inner.lock();
                state.snapshot.state = Lifecycle::Closed;
                state.closing = true;
                match result {
                    Ok(protocol) => {
                        if state.snapshot.outcome != Outcome::Unknown { state.snapshot.outcome = Outcome::RolledBack; }
                        state.snapshot.disposition = disposition(protocol);
                        if state.primary.is_none() { state.primary = Some(Ok(())); }
                    }
                    Err(error) => {
                        state.snapshot.cleanup = CleanupStatus::RollbackUnconfirmed;
                        state.cleanup_failure = Some(CleanupStatus::RollbackUnconfirmed);
                        state.snapshot.disposition = Disposition::Quarantined;
                        set_diagnostic(&mut state, &error);
                        if state.primary.is_none() { state.primary = Some(Err(Error::Cleanup { provider_code: error.code, correlation_id: error.correlation_id })); }
                    }
                }
            }
            inner.notify();
        }
        // Recover runs only after the original driver future terminates. A late
        // cleanup response can establish reuse safety, never the original effect.
        let recovery = {
            let call = driver.recover();
            tokio::pin!(call);
            tokio::select! {
                result = &mut call => result,
                _ = tokio::time::sleep(inner.options.cleanup_timeout) => { mark_cleanup_deadline(&inner); call.await }
            }
        };
        {
            let mut state = inner.lock();
            match recovery {
                Ok(protocol) => { state.snapshot.disposition = disposition(protocol); }
                Err(error) => { state.snapshot.disposition = Disposition::Quarantined; state.snapshot.cleanup = CleanupStatus::Failed; state.cleanup_failure = Some(CleanupStatus::Failed); set_diagnostic(&mut state, &error); }
            }
            state.operation = None;
            state.snapshot.resources.driver_operations = 0;
        }
        let retirement = {
            let state = inner.lock();
            match state.snapshot.disposition {
                Disposition::Quarantined | Disposition::Discarded => Disposition::Discarded,
                other => other,
            }
        };
        {
            let call = driver.retire(retirement);
            tokio::pin!(call);
            tokio::select! {
                _ = &mut call => {},
                _ = tokio::time::sleep(inner.options.cleanup_timeout) => { mark_cleanup_deadline(&inner); call.await; }
            }
        }
        // Only observed retirement lets the owning driver (and parent lease) go.
        guard.take();
        {
            let mut state = inner.lock();
            state.snapshot.disposition = retirement;
            state.snapshot.lifetime = if inner.owners.load(Ordering::Acquire) == 0 { LocalLifetime::Released } else { LocalLifetime::NativeRetired };
            state.snapshot.local_resources_released = true;
            state.snapshot.resources = Resources { physical_lease: 0, driver_operations: 0, cleanup_owners: 0 };
            state.snapshot.cleanup = state.cleanup_failure.unwrap_or(CleanupStatus::Succeeded);
            state.savepoints.clear();
        }
        inner.notify();
    });
}
async fn rollback_call<D: TransactionDriver>(
    inner: &Inner<D>,
    driver: &mut D,
    explicit: bool,
) -> Result<ProtocolState, ProviderFailure> {
    let call = driver.rollback();
    tokio::pin!(call);
    tokio::select! {
        result = &mut call => result,
        _ = tokio::time::sleep(inner.options.cleanup_timeout) => {
            if explicit { mark_deadline(inner, false); } else { mark_cleanup_deadline(inner); }
            call.await
        }
    }
}
async fn cleanup_rollback<D: TransactionDriver>(inner: &Inner<D>, driver: &mut D) {
    let result = rollback_call(inner, driver, false).await;
    let mut state = inner.lock();
    match result {
        Ok(protocol) => {
            state.snapshot.disposition = disposition(protocol);
        }
        Err(error) => {
            state.snapshot.cleanup = CleanupStatus::Failed;
            state.cleanup_failure = Some(CleanupStatus::Failed);
            state.snapshot.disposition = Disposition::Quarantined;
            set_diagnostic(&mut state, &error);
        }
    }
    // No assignment to outcome: Unknown survives rollback/no-transaction responses.
}
fn set_diagnostic(state: &mut State, error: &ProviderFailure) {
    state.snapshot.provider_code = error.code.clone();
    state.snapshot.correlation_id = error.correlation_id;
}
fn mark_deadline<D>(inner: &Inner<D>, commit: bool) {
    {
        let mut state = inner.lock();
        state.snapshot.state = Lifecycle::Closed;
        state.snapshot.deadline_exceeded = true;
        state.snapshot.disposition = Disposition::Quarantined;
        state.snapshot.cleanup = CleanupStatus::Pending;
        state.closing = true;
        if commit {
            state.snapshot.outcome = Outcome::Unknown;
            state.primary = Some(Err(Error::CommitUnknown {
                provider_code: None,
                correlation_id: 0,
            }));
        } else {
            state.primary = Some(Err(Error::Cleanup {
                provider_code: None,
                correlation_id: 0,
            }));
        }
    }
    inner.notify();
}
fn mark_cleanup_deadline<D>(inner: &Inner<D>) {
    {
        let mut state = inner.lock();
        state.snapshot.deadline_exceeded = true;
        state.snapshot.disposition = Disposition::Quarantined;
        state.snapshot.cleanup = CleanupStatus::Pending;
    }
    inner.notify();
}
async fn control<D: TransactionDriver>(
    inner: Arc<Inner<D>>,
    token: SavepointToken,
    action: u8,
) -> Result<(), Error> {
    let (sender, receiver) = oneshot::channel();
    let runtime = inner.runtime.clone();
    runtime.spawn(async move {
        let result;
        {
            let mut driver = inner.driver.lock().await;
            let driver = driver.as_mut().expect("savepoint holds driver lease");
            result = match action {
                0 => driver.savepoint_begin().await,
                1 => driver.savepoint_rollback().await,
                _ => driver.savepoint_release().await,
            };
        }
        let mapped;
        {
            let mut state = inner.lock();
            state.operation = None;
            state.snapshot.resources.driver_operations = 0;
            mapped = match result {
                Ok(()) => {
                    match action {
                        0 => state.savepoints.push(token),
                        1 => state.snapshot.state = Lifecycle::Active,
                        _ => {
                            state.savepoints.pop();
                        }
                    }
                    Ok(())
                }
                Err(error) => {
                    state.snapshot.state = Lifecycle::Failed;
                    Err(Error::Provider {
                        provider_code: error.code,
                        correlation_id: error.correlation_id,
                    })
                }
            };
        }
        inner.notify();
        let _ = sender.send(mapped);
        continue_close(inner);
    });
    receiver.await.map_err(|_| Error::InvalidState)?
}
