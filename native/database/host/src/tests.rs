//! Tests for this Host's composition of the existing Core operation, not a replacement executor.
use crate::{
    config::{Config, Limits, ProviderConfig},
    operation::{DbOperation, Value},
    runtime::Source,
};
use futures_util::FutureExt;
use nativeweave_interop_abi::*;
use std::sync::{
    Arc, Condvar, Mutex,
    atomic::{AtomicBool, AtomicUsize, Ordering},
};
use std::time::{Duration, Instant};

fn source() -> Arc<Source> {
    let limits = Limits {
        connections: 1,
        waiters: 2,
        admission: Duration::from_secs(1),
        buffered_rows: 8,
        buffered_bytes: 65536,
        batch_rows: 2,
        batch_bytes: 8192,
        field_bytes: 4096,
        nodes: 64,
        depth: 16,
        parameters: 8,
        parameter_bytes: 65536,
        prepared_plans: 4,
        prepared_bytes: 65536,
        command: Duration::from_secs(1),
    };
    #[cfg(feature = "sqlite")]
    let (profile, provider) = (
        nativeweave_data_contracts::Profile::Sqlite,
        ProviderConfig::Sqlite {
            path: ":memory:".into(),
            options: nativeweave_data_sqlite::Options {
                mode: nativeweave_data_sqlite::OpenMode::Memory,
                journal: nativeweave_data_sqlite::JournalMode::Memory,
                ..Default::default()
            },
        },
    );
    #[cfg(all(feature = "postgres", not(feature = "sqlite")))]
    let (profile, provider) = (
        nativeweave_data_contracts::Profile::PostgreSql,
        ProviderConfig::Postgres(nativeweave_data_postgres::ConnectConfig::new(
            "postgresql://unused@127.0.0.1/unused".into(),
        )),
    );
    Source::create(Config {
        profile,
        provider,
        limits,
        name: "host-operation-tests".into(),
    })
    .unwrap()
}
struct Probe {
    calls: AtomicUsize,
    status: AtomicUsize,
    entered: Mutex<bool>,
    released: Mutex<bool>,
    wake: Condvar,
    block: bool,
    result: u32,
    start_returned: AtomicBool,
    during_start: AtomicBool,
}
impl Probe {
    fn new(block: bool, result: u32) -> Self {
        Self {
            calls: AtomicUsize::new(0),
            status: AtomicUsize::new(usize::MAX),
            entered: Mutex::new(false),
            released: Mutex::new(false),
            wake: Condvar::new(),
            block,
            result,
            start_returned: AtomicBool::new(false),
            during_start: AtomicBool::new(false),
        }
    }
    fn callbacks(&self) -> NwOperationCallbacksV1 {
        NwOperationCallbacksV1 {
            struct_size: size_of::<NwOperationCallbacksV1>() as u32,
            flags: 0,
            cookie: self as *const Self as usize,
            on_terminal: Some(callback),
        }
    }
    fn wait(&self) {
        let state = self.entered.lock().unwrap();
        let (state, timeout) = self
            .wake
            .wait_timeout_while(state, Duration::from_secs(5), |entered| !*entered)
            .unwrap();
        assert!(
            *state && !timeout.timed_out(),
            "Host callback did not arrive"
        );
    }
    fn release(&self) {
        *self.released.lock().unwrap() = true;
        self.wake.notify_all();
    }
}
unsafe extern "C" fn callback(cookie: usize, status: u32, _: *const NwErrorInfoV1) -> u32 {
    // SAFETY: every test retains this pinned Box until operation retirement.
    let probe = unsafe { &*(cookie as *const Probe) };
    probe.calls.fetch_add(1, Ordering::AcqRel);
    probe.status.store(status as usize, Ordering::Release);
    probe.during_start.store(
        !probe.start_returned.load(Ordering::Acquire),
        Ordering::Release,
    );
    *probe.entered.lock().unwrap() = true;
    probe.wake.notify_all();
    if probe.block {
        let state = probe.released.lock().unwrap();
        let (state, _) = probe
            .wake
            .wait_timeout_while(state, Duration::from_secs(5), |released| !*released)
            .unwrap();
        if !*state {
            return NW_CONTRACT_VIOLATION;
        }
    }
    probe.result
}
fn retire(operation: &Arc<DbOperation>) {
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        match operation.core.try_retire() {
            NW_OK => break,
            NW_PENDING => {}
            other => panic!("unexpected retirement {other}"),
        }
        assert!(Instant::now() < deadline, "Host operation did not retire");
        std::thread::yield_now();
    }
    let (_, _, _, callbacks, workers, _) = operation.core.snapshot();
    assert_eq!((callbacks, workers), (0, 0));
    assert!(operation.core.can_release());
}
fn close(source: Arc<Source>) {
    assert_eq!(source.pending.load(Ordering::Acquire), 0);
    source.request_close();
    source.runtime.join();
    assert!(source.runtime.is_joined());
    let weak = Arc::downgrade(&source);
    drop(source);
    assert!(weak.upgrade().is_none());
}
#[test]
fn rejected_start_rolls_back_created_host_owner_without_callback() {
    let source = source();
    let probe = Box::new(Probe::new(false, NW_OK));
    let operation = DbOperation::create(
        source.clone(),
        100,
        false,
        Box::new(|_| async { Ok(Value::Count(4)) }.boxed()),
    )
    .unwrap();
    let mut callbacks = probe.callbacks();
    callbacks.flags = 1;
    // SAFETY: invalid registration remains rooted through the synchronous rejecting call.
    assert_eq!(unsafe { operation.start(callbacks) }, NW_INVALID_ARGUMENT);
    assert_eq!(probe.calls.load(Ordering::Acquire), 0);
    assert!(operation.core.can_release());
    drop(operation);
    close(source);
}
#[test]
fn callback_and_control_races_cannot_retire_or_transfer_twice() {
    let source = source();
    let probe = Box::new(Probe::new(true, NW_OK));
    let operation = DbOperation::create(
        source.clone(),
        100,
        false,
        Box::new(|_| async { Ok(Value::Count(41)) }.boxed()),
    )
    .unwrap();
    // SAFETY: callback's pinned state stays alive through successful retirement.
    assert_eq!(unsafe { operation.start(probe.callbacks()) }, NW_OK);
    probe.start_returned.store(true, Ordering::Release);
    probe.wait();
    assert_eq!(operation.core.try_retire(), NW_PENDING);
    assert!(!operation.core.can_release());
    // SAFETY: the duplicate start uses the same still-rooted callback, and must reject.
    assert_eq!(
        // SAFETY: duplicate registration uses the retained callback Box.
        unsafe { operation.start(probe.callbacks()) },
        NW_INVALID_STATE
    );
    // SAFETY: terminal has already been delivered; the duplicate attempt must be rejected without a second callback.
    assert_eq!(
        // SAFETY: the callback Box remains alive while the duplicate terminal is rejected.
        unsafe {
            operation
                .core
                .complete(NW_OK, Some(Value::Count(99)), Default::default())
        },
        NW_INVALID_STATE
    );
    let canceler = operation.clone();
    let thread = std::thread::spawn(move || {
        for _ in 0..32 {
            canceler.cancel.request();
            canceler.core.request_cancel();
            assert_eq!(canceler.core.try_retire(), NW_PENDING);
        }
    });
    thread.join().unwrap();
    assert_eq!(probe.calls.load(Ordering::Acquire), 1);
    let Value::Count(value) = operation.core.take_result().unwrap() else {
        panic!("wrong transfer")
    };
    assert_eq!(value, 41);
    assert!(operation.core.take_result().is_err());
    probe.release();
    retire(&operation);
    drop(operation);
    close(source);
}
#[test]
fn actual_retired_source_control_completes_inside_start() {
    let source = source();
    source.request_close();
    source.runtime.join();
    let probe = Box::new(Probe::new(false, NW_OK));
    let operation = DbOperation::create(
        source.clone(),
        100,
        true,
        Box::new(|_| async { Ok(Value::Count(1)) }.boxed()),
    )
    .unwrap();
    // SAFETY: synchronous callback's Box outlives start and retirement.
    assert_eq!(unsafe { operation.start(probe.callbacks()) }, NW_OK);
    probe.start_returned.store(true, Ordering::Release);
    assert!(probe.during_start.load(Ordering::Acquire));
    assert_eq!(probe.calls.load(Ordering::Acquire), 1);
    retire(&operation);
    drop(operation);
    close(source);
}
#[test]
fn accepted_callback_failure_poison_is_separate_from_retirement() {
    let source = source();
    let probe = Box::new(Probe::new(false, NW_CONTRACT_VIOLATION));
    let operation = DbOperation::create(
        source.clone(),
        100,
        false,
        Box::new(|_| async { Ok(Value::Count(2)) }.boxed()),
    )
    .unwrap();
    // SAFETY: the failure-returning callback remains rooted through native retirement.
    assert_eq!(unsafe { operation.start(probe.callbacks()) }, NW_OK);
    probe.wait();
    retire(&operation);
    assert!(operation.core.snapshot().5);
    assert_eq!(source.admit().unwrap_err().status, NW_ENGINE_POISONED);
    assert_eq!(probe.calls.load(Ordering::Acquire), 1);
    drop(operation);
    close(source);
}
#[test]
fn cancel_before_start_retains_source_until_native_canceled_terminal() {
    let source = source();
    let probe = Box::new(Probe::new(false, NW_OK));
    let operation = DbOperation::create(
        source.clone(),
        100,
        false,
        Box::new(|cancel| {
            async move {
                if cancel.requested() {
                    Err(crate::Error::canceled())
                } else {
                    Ok(Value::Count(1))
                }
            }
            .boxed()
        }),
    )
    .unwrap();
    operation.cancel.request();
    // SAFETY: callback state remains live through the actual terminal and native retirement.
    assert_eq!(unsafe { operation.start(probe.callbacks()) }, NW_OK);
    probe.wait();
    retire(&operation);
    assert_eq!(probe.status.load(Ordering::Acquire), NW_CANCELED as usize);
    assert!(operation.core.take_result().is_err());
    assert_eq!(probe.calls.load(Ordering::Acquire), 1);
    drop(operation);
    close(source);
}
