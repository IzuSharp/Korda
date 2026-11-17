//! Opt-in component conformance adapter. No symbols are present in a normal engine link.
use super::*;
use nativeweave_interop_abi::{
    NW_CANCELED, NW_INVALID_ARGUMENT, NW_INVALID_STATE, NW_OK, NW_OUT_OF_MEMORY, NW_PENDING,
    NwOperationCallbacksV1, NwStatus,
};
use nativeweave_interop_async::{Operation, WorkLease};
use std::ptr;
use std::sync::atomic::AtomicBool;

static JOBS_CREATED: AtomicU64 = AtomicU64::new(0);
static JOBS_RELEASED: AtomicU64 = AtomicU64::new(0);
static ENGINES_CREATED: AtomicU64 = AtomicU64::new(0);
static ENGINES_RELEASED: AtomicU64 = AtomicU64::new(0);
static REJECTED: AtomicU64 = AtomicU64::new(0);
static BAD_RELEASES: AtomicU64 = AtomicU64::new(0);
static CLOSES_CREATED: AtomicU64 = AtomicU64::new(0);
static CLOSES_RELEASED: AtomicU64 = AtomicU64::new(0);

struct Engine {
    executor: Arc<EngineExecutor>,
    gate: Mutex<bool>,
    released: Condvar,
    blocking_entered: AtomicU64,
    close_created: AtomicBool,
}
struct Job {
    operation: Arc<Operation<u64>>,
    held: Mutex<Option<WorkLease<u64>>>,
    // Field order is intentional: destroy actual payload before returning its credit.
    payload: Arc<Vec<u8>>,
    admission: Arc<Admission>,
    engine: Arc<Engine>,
    mode: u32,
}
#[repr(C)]
#[derive(Clone, Copy, Default)]
struct JoinResult {
    joined: u64,
    started: u64,
    stopped: u64,
    outstanding: u64,
    bytes: u64,
}
struct Observer {
    installed: bool,
    thread: Option<JoinHandle<()>>,
    joining: bool,
    joined: bool,
    failed: bool,
}
struct Close {
    operation: Arc<Operation<JoinResult>>,
    engine: Arc<Engine>,
    observer: Mutex<Observer>,
}

fn admission_status(error: AdmissionError) -> NwStatus {
    match error {
        AdmissionError::Impossible | AdmissionError::InvalidLimits => NW_INVALID_ARGUMENT,
        AdmissionError::Full => NW_OUT_OF_MEMORY,
        _ => NW_INVALID_STATE,
    }
}
fn finish<R: Send>(operation: &Operation<R>, outcome: Result<R, Failure>) {
    let (status, value, error) = match outcome {
        Ok(value) => (NW_OK, Some(value), ErrorFields::default()),
        Err(failure) => (failure.status, None, failure.fields),
    };
    // SAFETY: the A07 source lease or unsettled start roots the exact callback context.
    // No engine/admission mutex is held while publishing this single terminal.
    unsafe {
        operation.complete(status, value, error);
    }
}

#[unsafe(no_mangle)]
unsafe extern "C" fn nw_execution_fixture_create_v1(
    workers: u32,
    admitted: u32,
    waiters: u32,
    bytes: u64,
    cleanup: u32,
    output: *mut *mut Arc<Engine>,
) -> NwStatus {
    if output.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: caller supplies one writable output slot and holds no published owner yet.
    unsafe {
        *output = ptr::null_mut();
    }
    let executor = match EngineExecutor::new(Limits {
        workers: workers as usize,
        blocking_workers: 1,
        admitted: admitted as usize,
        waiters: waiters as usize,
        bytes,
        cleanup: cleanup as usize,
    }) {
        Ok(value) => value,
        Err(error) => return admission_status(error),
    };
    let engine = Arc::new(Engine {
        executor,
        gate: Mutex::new(false),
        released: Condvar::new(),
        blocking_entered: AtomicU64::new(0),
        close_created: AtomicBool::new(false),
    });
    // SAFETY: exclusive result slot receives one concrete owner.
    unsafe {
        *output = Box::into_raw(Box::new(engine));
    }
    ENGINES_CREATED.fetch_add(1, Ordering::AcqRel);
    NW_OK
}

#[unsafe(no_mangle)]
unsafe extern "C" fn nw_execution_fixture_request_close_v1(engine: *const Arc<Engine>) {
    // SAFETY: trusted fixture callers retain the exact engine owner throughout control FFI.
    if let Some(engine) = unsafe { engine.as_ref() } {
        engine.executor.request_close();
    }
}

#[unsafe(no_mangle)]
unsafe extern "C" fn nw_execution_fixture_engine_release_v1(engine: *mut Arc<Engine>) {
    if engine.is_null() {
        return;
    }
    // SAFETY: the owning SafeHandle releases this unique box exactly once.
    let owner = unsafe { Box::from_raw(engine) };
    owner.executor.request_close();
    drop(owner);
    ENGINES_RELEASED.fetch_add(1, Ordering::AcqRel);
}

#[unsafe(no_mangle)]
unsafe extern "C" fn nw_execution_fixture_job_create_v1(
    engine: *const Arc<Engine>,
    mode: u32,
    data: *const u8,
    length: u64,
    output: *mut *mut Job,
) -> NwStatus {
    if output.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: caller supplied one writable result slot.
    unsafe {
        *output = ptr::null_mut();
    }
    // SAFETY: engine points at a held engine owner, never an untrusted cookie probe.
    let Some(engine) = (unsafe { engine.as_ref() }) else {
        return NW_INVALID_ARGUMENT;
    };
    if mode > 5 || length > 4096 || (length != 0 && data.is_null()) {
        return NW_INVALID_ARGUMENT;
    }
    let admission = match engine.executor.try_acquire(length) {
        Ok(value) => value,
        Err(error) => {
            REJECTED.fetch_add(1, Ordering::AcqRel);
            return admission_status(error);
        }
    };
    let mut payload = Vec::new();
    if payload.try_reserve_exact(length as usize).is_err() {
        return NW_OUT_OF_MEMORY;
    }
    if length != 0 {
        // SAFETY: this immediate borrowed input is readable for exactly length bytes.
        payload.extend_from_slice(unsafe { std::slice::from_raw_parts(data, length as usize) });
    }
    let job = Job {
        operation: Arc::new(Operation::new()),
        held: Mutex::new(None),
        payload: Arc::new(payload),
        admission,
        engine: Arc::clone(engine),
        mode,
    };
    // SAFETY: move the unique native owner only after every fallible setup step succeeded.
    unsafe {
        *output = Box::into_raw(Box::new(job));
    }
    JOBS_CREATED.fetch_add(1, Ordering::AcqRel);
    NW_OK
}

#[unsafe(no_mangle)]
unsafe extern "C" fn nw_execution_fixture_job_start_v1(
    job: *const Job,
    callbacks: *const NwOperationCallbacksV1,
) -> NwStatus {
    // SAFETY: exact concrete owner and callback table are leased through this start call.
    let (Some(job), Some(callbacks)) = (unsafe { job.as_ref() }, unsafe { callbacks.as_ref() })
    else {
        return NW_INVALID_ARGUMENT;
    };
    let status = job.operation.start(*callbacks);
    if status != NW_OK {
        return status;
    }
    if job.mode == 5 {
        *job.held.lock().unwrap_or_else(|p| p.into_inner()) = job.operation.reserve_source().ok();
    }
    let source = match job.operation.reserve_source() {
        Ok(value) => value,
        Err(status) => {
            finish(
                &job.operation,
                Err(Failure::new(status, ErrorFields::default())),
            );
            return NW_OK;
        }
    };
    let operation = Arc::clone(&job.operation);
    let engine = Arc::clone(&job.engine);
    let payload = Arc::clone(&job.payload);
    let mode = job.mode;
    let completion = move |result| {
        finish(source.operation(), result);
    };
    let started = if mode == 1 || mode == 4 {
        job.engine.executor.spawn_blocking(
            &job.admission,
            move || {
                if mode == 4 {
                    panic!("actual A08 blocking execution panic");
                }
                engine.blocking_entered.fetch_add(1, Ordering::AcqRel);
                let mut released = engine.gate.lock().unwrap_or_else(|p| p.into_inner());
                while !*released {
                    released = engine
                        .released
                        .wait(released)
                        .unwrap_or_else(|p| p.into_inner());
                }
                drop(released);
                if operation.cancel_requested() || engine.executor.is_closing() {
                    Err(Failure::new(NW_CANCELED, ErrorFields::default()))
                } else {
                    Ok(payload.iter().map(|byte| u64::from(*byte)).sum())
                }
            },
            completion,
        )
    } else {
        job.engine.executor.spawn(
            &job.admission,
            async move {
                if mode == 3 {
                    panic!("actual A08 future poll panic");
                }
                if mode == 2 {
                    engine.executor.cancelled().await;
                }
                if operation.cancel_requested() || engine.executor.is_closing() {
                    Err(Failure::new(NW_CANCELED, ErrorFields::default()))
                } else {
                    Ok(payload.iter().map(|byte| u64::from(*byte)).sum())
                }
            },
            completion,
        )
    };
    if started.is_err() {
        // start already committed the callback table. A close/issued-permit race becomes
        // one accepted canceled terminal, never callback-then-start-rejection.
        finish(
            &job.operation,
            Err(Failure::new(NW_CANCELED, ErrorFields::default())),
        );
    }
    NW_OK
}

#[unsafe(no_mangle)]
unsafe extern "C" fn nw_execution_fixture_job_cancel_v1(job: *const Job) -> NwStatus {
    // SAFETY: a managed control lease owns the exact Job for this call.
    unsafe { job.as_ref() }.map_or(NW_INVALID_ARGUMENT, |value| {
        value.operation.request_cancel()
    })
}
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_execution_fixture_job_take_v1(
    job: *const Job,
    output: *mut u64,
) -> NwStatus {
    if output.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: held concrete Job and writable scalar output.
    let Some(job) = (unsafe { job.as_ref() }) else {
        return NW_INVALID_ARGUMENT;
    };
    // SAFETY: the output is initialized on every valid output path.
    unsafe {
        *output = 0;
    }
    match job.operation.take_result() {
        Ok(value) => {
            /* SAFETY: the caller owns this scalar output. */
            unsafe {
                *output = value;
            }
            NW_OK
        }
        Err(status) => status,
    }
}
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_execution_fixture_job_retire_v1(job: *const Job) -> NwStatus {
    // SAFETY: exact owner is kept alive by the A07 control lease.
    unsafe { job.as_ref() }.map_or(NW_INVALID_ARGUMENT, |value| value.operation.try_retire())
}
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_execution_fixture_source_take_v1(
    job: *const Job,
    output: *mut *mut WorkLease<u64>,
) -> NwStatus {
    if output.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: caller owns this writable result slot.
    unsafe {
        *output = ptr::null_mut();
    }
    // SAFETY: exact owner is kept alive by the A07 control lease.
    let Some(job) = (unsafe { job.as_ref() }) else {
        return NW_INVALID_ARGUMENT;
    };
    let source = job.held.lock().unwrap_or_else(|p| p.into_inner()).take();
    match source {
        Some(source) => {
            // SAFETY: transfer exactly one tracked source to its independent SafeHandle.
            unsafe {
                *output = Box::into_raw(Box::new(source));
            }
            NW_OK
        }
        None => NW_INVALID_STATE,
    }
}
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_execution_fixture_source_release_v1(source: *mut WorkLease<u64>) {
    if source.is_null() {
        return;
    }
    // SAFETY: unique source box is released exactly once by its owning SafeHandle.
    drop(unsafe { Box::from_raw(source) });
}
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_execution_fixture_job_release_v1(job: *mut Job) {
    // SAFETY: this is the unique concrete owner passed to the owning SafeHandle.
    let Some(value) = (unsafe { job.as_ref() }) else {
        return;
    };
    if !value.operation.can_release() {
        BAD_RELEASES.fetch_add(1, Ordering::AcqRel);
        return;
    }
    // SAFETY: Created or fully Retired means no native source/callback can access this box.
    drop(unsafe { Box::from_raw(job) });
    JOBS_RELEASED.fetch_add(1, Ordering::AcqRel);
}

#[unsafe(no_mangle)]
unsafe extern "C" fn nw_execution_fixture_open_gate_v1(engine: *const Arc<Engine>) {
    // SAFETY: held concrete Engine owner.
    if let Some(engine) = unsafe { engine.as_ref() } {
        *engine.gate.lock().unwrap_or_else(|p| p.into_inner()) = true;
        engine.released.notify_all();
    }
}
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_execution_fixture_snapshot_v1(
    engine: *const Arc<Engine>,
    kind: u32,
) -> u64 {
    // SAFETY: held concrete Engine owner.
    let Some(engine) = (unsafe { engine.as_ref() }) else {
        return u64::MAX;
    };
    let state = engine.executor.snapshot();
    match kind {
        0 => u64::from(state.closing),
        1 => u64::from(state.joined),
        2 => state.admitted as u64,
        3 => state.reserved_bytes,
        4 => state.issued as u64,
        5 => state.running as u64,
        6 => state.retiring as u64,
        7 => state.workers_started,
        8 => state.workers_stopped,
        9 => state.oldest_cleanup_millis,
        10 => engine.blocking_entered.load(Ordering::Acquire),
        11 => u64::from(state.poisoned),
        12 => state.panic_payloads,
        13 => state.tracked_tasks as u64,
        _ => u64::MAX,
    }
}

#[unsafe(no_mangle)]
unsafe extern "C" fn nw_execution_fixture_close_create_v1(
    engine: *const Arc<Engine>,
    output: *mut *mut Close,
) -> NwStatus {
    if output.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: caller owns the initialized result slot.
    unsafe {
        *output = ptr::null_mut();
    }
    // SAFETY: held exact concrete Engine owner.
    let Some(engine) = (unsafe { engine.as_ref() }) else {
        return NW_INVALID_ARGUMENT;
    };
    if engine.close_created.swap(true, Ordering::AcqRel) {
        return NW_INVALID_STATE;
    }
    let close = Close {
        operation: Arc::new(Operation::new()),
        engine: Arc::clone(engine),
        observer: Mutex::new(Observer {
            installed: false,
            thread: None,
            joining: false,
            joined: false,
            failed: false,
        }),
    };
    // SAFETY: publish exactly one bounded close-control owner for this instance.
    unsafe {
        *output = Box::into_raw(Box::new(close));
    }
    CLOSES_CREATED.fetch_add(1, Ordering::AcqRel);
    NW_OK
}

#[unsafe(no_mangle)]
unsafe extern "C" fn nw_execution_fixture_close_start_v1(
    close: *const Close,
    callbacks: *const NwOperationCallbacksV1,
) -> NwStatus {
    // SAFETY: held concrete Close owner and exact callback table for the full start interval.
    let (Some(close), Some(callbacks)) = (unsafe { close.as_ref() }, unsafe { callbacks.as_ref() })
    else {
        return NW_INVALID_ARGUMENT;
    };
    let status = close.operation.start(*callbacks);
    if status != NW_OK {
        return status;
    }
    close.engine.executor.request_close();
    let source = match close.operation.reserve_source() {
        Ok(value) => value,
        Err(status) => {
            finish(
                &close.operation,
                Err(Failure::new(status, ErrorFields::default())),
            );
            return NW_OK;
        }
    };
    let engine = Arc::clone(&close.engine);
    let thread = std::thread::Builder::new()
        .name("NativeWeave close observer".into())
        .spawn(move || {
            let joined = engine.executor.join();
            let state = engine.executor.snapshot();
            if joined {
                finish(
                    source.operation(),
                    Ok(JoinResult {
                        joined: 1,
                        started: state.workers_started,
                        stopped: state.workers_stopped,
                        outstanding: state.admitted as u64,
                        bytes: state.reserved_bytes,
                    }),
                );
            } else {
                finish(
                    source.operation(),
                    Err(Failure::new(NW_CONTRACT_VIOLATION, ErrorFields::default())),
                );
            }
        });
    let failed = thread.is_err();
    {
        let mut observer = close.observer.lock().unwrap_or_else(|p| p.into_inner());
        observer.installed = true;
        observer.joined = failed; // no observer was acquired if thread creation failed
        observer.thread = thread.ok();
    }
    if failed {
        finish(
            &close.operation,
            Err(Failure::new(NW_OUT_OF_MEMORY, ErrorFields::default())),
        );
    }
    NW_OK
}
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_execution_fixture_close_cancel_v1(close: *const Close) -> NwStatus {
    // SAFETY: held exact close-control owner. Waiting cancellation is not a native stop override.
    unsafe { close.as_ref() }.map_or(NW_INVALID_ARGUMENT, |_| NW_OK)
}
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_execution_fixture_close_take_v1(
    close: *const Close,
    output: *mut JoinResult,
) -> NwStatus {
    if output.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: held concrete Close and one writable blittable result.
    let Some(close) = (unsafe { close.as_ref() }) else {
        return NW_INVALID_ARGUMENT;
    };
    // SAFETY: initialize the exact result before returning any status.
    unsafe {
        *output = JoinResult::default();
    }
    match close.operation.take_result() {
        Ok(value) => {
            /* SAFETY: exact caller-owned output. */
            unsafe {
                *output = value;
            }
            NW_OK
        }
        Err(status) => status,
    }
}
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_execution_fixture_close_retire_v1(close: *const Close) -> NwStatus {
    // SAFETY: A07 control lease retains this concrete Close during the whole call.
    let Some(close) = (unsafe { close.as_ref() }) else {
        return NW_INVALID_ARGUMENT;
    };
    let thread = {
        let mut observer = close.observer.lock().unwrap_or_else(|p| p.into_inner());
        if observer.failed {
            return NW_CONTRACT_VIOLATION;
        }
        if !observer.installed || observer.joining {
            return NW_PENDING;
        }
        if observer.joined {
            return close.operation.try_retire();
        }
        if !observer
            .thread
            .as_ref()
            .is_some_and(JoinHandle::is_finished)
        {
            return NW_PENDING;
        }
        observer.joining = true;
        observer.thread.take().expect("installed observer")
    };
    let succeeded = match thread.join() {
        Ok(()) => true,
        Err(payload) => {
            std::mem::forget(payload);
            close.engine.executor.shared.poison();
            false
        }
    };
    {
        let mut observer = close.observer.lock().unwrap_or_else(|p| p.into_inner());
        observer.joining = false;
        observer.joined = succeeded;
        observer.failed = !succeeded;
    }
    if succeeded {
        close.operation.try_retire()
    } else {
        NW_CONTRACT_VIOLATION
    }
}
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_execution_fixture_close_release_v1(close: *mut Close) {
    // SAFETY: exact unique box held by its owning managed SafeHandle.
    let Some(value) = (unsafe { close.as_ref() }) else {
        return;
    };
    if !value.operation.can_release() {
        BAD_RELEASES.fetch_add(1, Ordering::AcqRel);
        return;
    }
    value.engine.executor.request_close();
    // SAFETY: Created or Retired, with observer join included in the retirement acknowledgement.
    drop(unsafe { Box::from_raw(close) });
    CLOSES_RELEASED.fetch_add(1, Ordering::AcqRel);
}
#[unsafe(no_mangle)]
extern "C" fn nw_execution_fixture_count_v1(kind: u32) -> u64 {
    match kind {
        0 => JOBS_CREATED.load(Ordering::Acquire),
        1 => JOBS_RELEASED.load(Ordering::Acquire),
        2 => JOBS_CREATED.load(Ordering::Acquire) - JOBS_RELEASED.load(Ordering::Acquire),
        3 => ENGINES_CREATED.load(Ordering::Acquire),
        4 => ENGINES_RELEASED.load(Ordering::Acquire),
        5 => REJECTED.load(Ordering::Acquire),
        6 => BAD_RELEASES.load(Ordering::Acquire),
        7 => CLOSES_CREATED.load(Ordering::Acquire),
        8 => CLOSES_RELEASED.load(Ordering::Acquire),
        _ => u64::MAX,
    }
}
