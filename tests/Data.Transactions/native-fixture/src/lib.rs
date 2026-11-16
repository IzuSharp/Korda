//! Component ABI fixture only. It calls the real product coordinator with
//! controlled completion signals; it is not a distributed database engine or a
//! substitute for the separate real SQLx-provider composition tests.
use nativeweave_data_contracts::{
    ConnectionDisposition, LocalLifetime, Profile, TransactionOutcome,
};
use nativeweave_data_transactions::*;
use std::sync::{Arc, OnceLock};
use std::time::Duration;
use tokio::sync::Semaphore;

fn runtime() -> &'static tokio::runtime::Runtime {
    static RUNTIME: OnceLock<tokio::runtime::Runtime> = OnceLock::new();
    RUNTIME.get_or_init(|| {
        tokio::runtime::Builder::new_multi_thread()
            .worker_threads(2)
            .enable_time()
            .build()
            .expect("fixture runtime")
    })
}
struct Signals {
    mode: u32,
    proceed: Semaphore,
}
struct Driver(Arc<Signals>);
impl TransactionDriver for Driver {
    fn capabilities(&self) -> Capabilities {
        Capabilities {
            profile: Profile::PostgreSql,
            read_only: true,
        }
    }
    fn session_id(&self) -> u64 {
        401
    }
    async fn begin(&mut self, _: &Options) -> Result<(), ProviderFailure> {
        Ok(())
    }
    async fn commit(&mut self) -> Result<Confirmation, ProviderFailure> {
        if self.0.mode == 3 {
            self.0
                .proceed
                .acquire()
                .await
                .expect("fixture gate")
                .forget();
        }
        match self.0.mode {
            1 => Err(failure()),
            2 => Ok(Confirmation::RolledBack(failure())),
            _ => Ok(Confirmation::Committed),
        }
    }
    async fn rollback(&mut self) -> Result<ProtocolState, ProviderFailure> {
        if self.0.mode == 4 {
            Err(failure())
        } else {
            Ok(ProtocolState::Clean)
        }
    }
    async fn savepoint_begin(&mut self) -> Result<(), ProviderFailure> {
        Ok(())
    }
    async fn savepoint_rollback(&mut self) -> Result<(), ProviderFailure> {
        Ok(())
    }
    async fn savepoint_release(&mut self) -> Result<(), ProviderFailure> {
        Ok(())
    }
    async fn recover(&mut self) -> Result<ProtocolState, ProviderFailure> {
        Ok(if self.0.mode == 4 {
            ProtocolState::Untrusted
        } else {
            ProtocolState::Clean
        })
    }
    async fn retire(&mut self, _: ConnectionDisposition) {
        if self.0.mode == 5 {
            self.0
                .proceed
                .acquire()
                .await
                .expect("fixture gate")
                .forget();
        }
    }
}
fn failure() -> ProviderFailure {
    ProviderFailure {
        code: Some("08006".into()),
        correlation_id: 71,
    }
}
struct Fixture {
    tx: Transaction<Driver>,
    signals: Arc<Signals>,
    points: std::sync::Mutex<Vec<SavepointToken>>,
}
fn error(result: Result<(), Error>) -> u32 {
    match result {
        Ok(()) => 0,
        Err(Error::CanceledBeforeDispatch) => 2,
        Err(Error::CommitUnknown { .. }) => 3,
        Err(Error::Provider { .. }) => 4,
        Err(Error::Cleanup { .. }) => 5,
        Err(Error::SavepointOrder) => 6,
        Err(_) => 1,
    }
}
fn token(canceled: u32) -> AdmissionToken {
    let t = AdmissionToken::default();
    if canceled != 0 {
        t.cancel();
    }
    t
}
#[repr(C)]
pub struct SnapshotWire {
    values: [u64; 16],
}
fn wire(snapshot: Snapshot) -> SnapshotWire {
    SnapshotWire {
        values: [
            match snapshot.state {
                Lifecycle::Active => 0,
                Lifecycle::Failed => 1,
                Lifecycle::CommitInFlight => 2,
                Lifecycle::RollbackInFlight => 3,
                Lifecycle::Closed => 4,
            },
            match snapshot.outcome {
                TransactionOutcome::InProgress => 0,
                TransactionOutcome::Committed => 1,
                TransactionOutcome::RolledBack => 2,
                TransactionOutcome::Unknown => 3,
            },
            u64::from(snapshot.commit_attempted),
            match snapshot.cleanup {
                CleanupStatus::NotStarted => 0,
                CleanupStatus::Pending => 1,
                CleanupStatus::Succeeded => 2,
                CleanupStatus::RollbackUnconfirmed => 3,
                CleanupStatus::Failed => 4,
            },
            match snapshot.disposition {
                ConnectionDisposition::Usable => 0,
                ConnectionDisposition::ProviderResetPending => 1,
                ConnectionDisposition::Quarantined => 2,
                ConnectionDisposition::Discarded => 3,
            },
            u64::from(snapshot.local_resources_released),
            u64::from(snapshot.deadline_exceeded),
            snapshot.correlation_id,
            u64::from(snapshot.provider_code.is_some()),
            snapshot.generation,
            snapshot.session_id,
            u64::from(snapshot.resources.physical_lease),
            u64::from(snapshot.resources.driver_operations),
            u64::from(snapshot.resources.cleanup_owners),
            match snapshot.lifetime {
                LocalLifetime::Open => 0,
                LocalLifetime::Closing => 1,
                LocalLifetime::NativeRetired => 2,
                LocalLifetime::Released => 3,
            },
            0,
        ],
    }
}
/// Creates a fixture owner; mode selects controlled provider completions.
#[unsafe(no_mangle)]
pub extern "C" fn nw_tx_fixture_create(
    mode: u32,
    commit_ms: u64,
    cleanup_ms: u64,
) -> *mut std::ffi::c_void {
    let signals = Arc::new(Signals {
        mode,
        proceed: Semaphore::new(0),
    });
    let options = Options {
        commit_timeout: Duration::from_millis(commit_ms),
        cleanup_timeout: Duration::from_millis(cleanup_ms),
        ..Options::default()
    };
    match runtime().block_on(Transaction::begin(
        Driver(Arc::clone(&signals)),
        options,
        &AdmissionToken::default(),
    )) {
        Ok(tx) => Box::into_raw(Box::new(Fixture {
            tx,
            signals,
            points: std::sync::Mutex::new(Vec::new()),
        }))
        .cast(),
        Err(_) => std::ptr::null_mut(),
    }
}
/// # Safety
/// `owner` is a live fixture owner retained by the managed SafeHandle for this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_tx_fixture_finish(
    owner: *mut std::ffi::c_void,
    commit: u32,
    canceled: u32,
) -> u32 {
    // SAFETY: the fixture caller retains this exact Box-backed owner until return.
    let fixture = unsafe { &*owner.cast::<Fixture>() };
    error(runtime().block_on(async {
        if commit != 0 {
            fixture.tx.commit(&token(canceled)).await
        } else {
            fixture.tx.rollback(&token(canceled)).await
        }
    }))
}
/// # Safety
/// `owner` is live and `output` points to one writable SnapshotWire.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_tx_fixture_snapshot(
    owner: *mut std::ffi::c_void,
    output: *mut SnapshotWire,
) {
    // SAFETY: caller guarantees live nonoverlapping fixture and output storage.
    unsafe {
        output.write(wire((&*owner.cast::<Fixture>()).tx.snapshot()));
    }
}
/// # Safety
/// `owner` is retained for the call. This only requests retained engine cleanup.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_tx_fixture_request_close(owner: *mut std::ffi::c_void) {
    // SAFETY: caller holds a live fixture owner through the request.
    unsafe {
        (&*owner.cast::<Fixture>()).tx.request_close();
    }
}
/// # Safety
/// `owner` is retained for the complete join.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_tx_fixture_join(owner: *mut std::ffi::c_void) {
    // SAFETY: the managed interop call retains this fixture owner during the join.
    let fixture = unsafe { &*owner.cast::<Fixture>() };
    runtime().block_on(fixture.tx.join());
}
/// # Safety
/// `owner` is retained throughout this bounded wait; ticks are nonnegative 100ns units.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_tx_fixture_wait_close(owner: *mut std::ffi::c_void, ticks: u64) {
    // SAFETY: caller holds a live fixture owner throughout the wait.
    let fixture = unsafe { &*owner.cast::<Fixture>() };
    runtime().block_on(fixture.tx.close(
        Duration::from_secs(ticks / 10_000_000) + Duration::from_nanos(ticks % 10_000_000 * 100),
    ));
}
/// # Safety
/// `owner` is retained for this fixture-control call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_tx_fixture_proceed(owner: *mut std::ffi::c_void) {
    // SAFETY: caller holds the live fixture owner while opening its test gate.
    unsafe {
        (&*owner.cast::<Fixture>()).signals.proceed.add_permits(1);
    }
}
/// # Safety
/// `owner` is retained; this simulates completion of one invalidating statement.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_tx_fixture_fail_command(owner: *mut std::ffi::c_void) -> u32 {
    // SAFETY: caller holds the fixture owner for the complete command admission.
    let fixture = unsafe { &*owner.cast::<Fixture>() };
    match fixture.tx.admit_command(false, &AdmissionToken::default()) {
        Ok(lease) => {
            lease.complete(true, ProtocolState::Clean);
            0
        }
        Err(e) => error(Err(e)),
    }
}
/// # Safety
/// `owner` is live, `sequence` is writable; zero publishes no savepoint on failure.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_tx_fixture_savepoint_create(
    owner: *mut std::ffi::c_void,
    canceled: u32,
    sequence: *mut u64,
) -> u32 {
    // SAFETY: caller retains owner and one writable output for this call.
    let fixture = unsafe { &*owner.cast::<Fixture>() };
    // SAFETY: the caller supplies one writable u64 output for initialization.
    unsafe {
        sequence.write(0);
    }
    match runtime().block_on(fixture.tx.create_savepoint(&token(canceled))) {
        Ok(value) => {
            fixture.points.lock().expect("fixture points").push(value);
            // SAFETY: caller supplied one writable u64 output.
            unsafe {
                sequence.write(value.sequence());
            }
            0
        }
        Err(e) => error(Err(e)),
    }
}
/// # Safety
/// `owner` is live; sequence must identify a previously issued fixture savepoint.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_tx_fixture_savepoint_action(
    owner: *mut std::ffi::c_void,
    sequence: u64,
    action: u32,
    canceled: u32,
) -> u32 {
    // SAFETY: caller retains the owner throughout the operation.
    let fixture = unsafe { &*owner.cast::<Fixture>() };
    let found = fixture
        .points
        .lock()
        .expect("fixture points")
        .iter()
        .copied()
        .find(|p| p.sequence() == sequence);
    let Some(point) = found else {
        return 1;
    };
    error(runtime().block_on(async {
        match action {
            0 => fixture.tx.rollback_savepoint(point, &token(canceled)).await,
            1 => fixture.tx.release_savepoint(point, &token(canceled)).await,
            _ => fixture.tx.dispose_savepoint(point).await,
        }
    }))
}
/// # Safety
/// `owner` is the uniquely transferred fixture allocation; there are no concurrent
/// ABI accesses. Drop requests background cleanup and does not block a finalizer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_tx_fixture_release(owner: *mut std::ffi::c_void) {
    // SAFETY: final SafeHandle release transfers this Box exactly once.
    unsafe {
        drop(Box::from_raw(owner.cast::<Fixture>()));
    }
}
