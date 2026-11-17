//! Optional engine-owned Tokio runtime, atomic admission budgets and actual join.
//! Core and synchronous engines never construct this component implicitly.
//! Waiters contain only bounded metadata. Acquire before allocating/retaining payload.

use nativeweave_interop_abi::{NW_CONTRACT_VIOLATION, NW_ENGINE_POISONED, NW_NATIVE_PANIC};
use nativeweave_interop_errors::{EngineBoundary, ErrorFields, Failure};
use std::collections::VecDeque;
use std::future::Future;
use std::panic::UnwindSafe;
use std::sync::atomic::{AtomicU8, AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex, MutexGuard};
use std::thread::JoinHandle;
use std::time::Instant;
use tokio::runtime::{Builder, Handle};
use tokio::sync::{Notify, oneshot};

const ISSUED: u8 = 0;
const RUNNING: u8 = 1;
const RETIRING: u8 = 2;
const RELEASED: u8 = 3;

/// Explicit per-instance limits. Zero budgets disable their respective admission.
#[derive(Clone, Copy, Debug)]
pub struct Limits {
    pub workers: usize,
    pub blocking_workers: usize,
    pub admitted: usize,
    pub waiters: usize,
    pub bytes: u64,
    pub cleanup: usize,
}

/// Admission rejects before materializing payload or creating a native operation.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AdmissionError {
    InvalidLimits,
    Impossible,
    Full,
    Closed,
    WrongOwner,
    AlreadyStarted,
}

/// Logical budgets and observed workers; these do not claim a whole-process RSS bound.
#[derive(Clone, Copy, Debug, Default)]
pub struct Snapshot {
    pub closing: bool,
    pub poisoned: bool,
    pub joined: bool,
    pub admitted: usize,
    pub waiters: usize,
    pub reserved_bytes: u64,
    pub issued: usize,
    pub running: usize,
    pub retiring: usize,
    pub tracked_tasks: usize,
    pub workers_started: u64,
    pub workers_stopped: u64,
    pub oldest_cleanup_millis: u64,
    pub panic_payloads: u64,
}

struct Waiter {
    id: u64,
    bytes: u64,
    sender: oneshot::Sender<Result<Arc<Admission>, AdmissionError>>,
}
#[derive(Clone, Copy)]
struct CleanupRecord {
    retiring_at: Option<Instant>,
}
struct State {
    closing: bool,
    admitted: usize,
    reserved_bytes: u64,
    issued: usize,
    running: usize,
    retiring: usize,
    tasks: usize,
    next_id: u64,
    waiters: VecDeque<Waiter>,
    cleanup: Vec<Option<CleanupRecord>>,
}
struct Shared {
    limits: Limits,
    state: Mutex<State>,
    changed: Condvar,
    cancellation: Notify,
    boundary: EngineBoundary,
    started: AtomicU64,
    stopped: AtomicU64,
    panic_payloads: AtomicU64,
}
impl Shared {
    fn lock(&self) -> MutexGuard<'_, State> {
        self.state
            .lock()
            .unwrap_or_else(|poison| poison.into_inner())
    }
    fn available(&self, state: &State, bytes: u64) -> bool {
        state.admitted < self.limits.admitted
            && state.admitted < self.limits.cleanup
            && bytes <= self.limits.bytes - state.reserved_bytes
    }
    fn reserve(self: &Arc<Self>, state: &mut State, bytes: u64) -> Arc<Admission> {
        let slot = state
            .cleanup
            .iter()
            .position(Option::is_none)
            .expect("reserved cleanup capacity");
        state.cleanup[slot] = Some(CleanupRecord { retiring_at: None });
        state.admitted += 1;
        state.issued += 1;
        state.reserved_bytes += bytes;
        Arc::new(Admission {
            shared: Arc::clone(self),
            bytes,
            slot,
            stage: AtomicU8::new(ISSUED),
        })
    }
    fn close(&self) {
        let waiters = {
            let mut state = self.lock();
            if state.closing {
                return;
            }
            state.closing = true;
            std::mem::take(&mut state.waiters)
        };
        // Neither receiver wakeup nor arbitrary payload destruction occurs under the gate.
        for waiter in waiters {
            let _ = waiter.sender.send(Err(AdmissionError::Closed));
        }
        self.cancellation.notify_waiters();
        self.changed.notify_all();
    }
    fn pump(self: &Arc<Self>) {
        loop {
            let selected = {
                let mut state = self.lock();
                if state.closing {
                    return;
                }
                let Some(first) = state.waiters.front() else {
                    return;
                };
                if !self.available(&state, first.bytes) {
                    return;
                }
                let waiter = state.waiters.pop_front().expect("checked first waiter");
                let admission = self.reserve(&mut state, waiter.bytes);
                (waiter.sender, admission)
            };
            if let Err(Ok(admission)) = selected.0.send(Ok(selected.1)) {
                // The receiver vanished after selection. Release without recursive pumping;
                // this loop handles any number of canceled metadata waiters with constant stack.
                admission.release(false);
            }
        }
    }
    fn poison(&self) {
        self.boundary.poison();
        self.close();
    }
}

/// Issued capacity survives Task completion and returns only with the final native owner.
/// Multiple Arc references are one reservation, not additional admission rights.
pub struct Admission {
    shared: Arc<Shared>,
    bytes: u64,
    slot: usize,
    stage: AtomicU8,
}
impl Admission {
    fn start(&self, shared: &Arc<Shared>) -> Result<(), AdmissionError> {
        if !Arc::ptr_eq(&self.shared, shared) {
            return Err(AdmissionError::WrongOwner);
        }
        let mut state = self.shared.lock();
        if state.closing {
            return Err(AdmissionError::Closed);
        }
        if self
            .stage
            .compare_exchange(ISSUED, RUNNING, Ordering::AcqRel, Ordering::Acquire)
            .is_err()
        {
            return Err(AdmissionError::AlreadyStarted);
        }
        state.issued -= 1;
        state.running += 1;
        state.tasks += 1;
        Ok(())
    }
    fn terminal(&self) {
        let mut state = self.shared.lock();
        if self
            .stage
            .compare_exchange(RUNNING, RETIRING, Ordering::AcqRel, Ordering::Acquire)
            .is_ok()
        {
            state.running -= 1;
            state.retiring += 1;
            state.tasks -= 1;
            state.cleanup[self.slot]
                .as_mut()
                .expect("reserved cleanup record")
                .retiring_at = Some(Instant::now());
        }
        self.shared.changed.notify_all();
    }
    fn release(&self, pump: bool) {
        {
            let mut state = self.shared.lock();
            let previous = self.stage.swap(RELEASED, Ordering::AcqRel);
            if previous == RELEASED {
                return;
            }
            // A running supervisor owns an Arc, so final-owner release cannot race its work.
            match previous {
                ISSUED => state.issued -= 1,
                RETIRING => state.retiring -= 1,
                _ => unreachable!("running supervisor still owns the admission"),
            }
            state.admitted -= 1;
            state.reserved_bytes -= self.bytes;
            state.cleanup[self.slot] = None;
        }
        self.shared.changed.notify_all();
        if pump {
            self.shared.pump();
        }
    }
    /// Charge attached to this reservation until actual owner retirement.
    pub fn bytes(&self) -> u64 {
        self.bytes
    }
}
impl Drop for Admission {
    fn drop(&mut self) {
        self.release(true);
    }
}

struct Waiting {
    shared: Arc<Shared>,
    id: u64,
}
impl Drop for Waiting {
    fn drop(&mut self) {
        let removed = {
            let mut state = self.shared.lock();
            state
                .waiters
                .iter()
                .position(|waiter| waiter.id == self.id)
                .and_then(|index| state.waiters.remove(index))
        };
        drop(removed); // wake/drop only after releasing the state gate
        self.shared.pump();
    }
}

struct JoinState {
    thread: Option<JoinHandle<()>>,
    joining: bool,
    joined: bool,
    failed: bool,
}

/// A concrete engine explicitly owns this instance. Handles never outlive its owned runtime.
pub struct EngineExecutor {
    shared: Arc<Shared>,
    handle: Handle,
    join: Mutex<JoinState>,
    joined: Condvar,
}
impl EngineExecutor {
    /// Builds bounded runtime workers and one owner/join thread, before publishing admission.
    /// Fixed Tokio/Arc/channel metadata uses upstream infallible allocations; process OOM/abort
    /// is not recoverable. Payload reservations remain explicit, atomic and separately bounded.
    pub fn new(limits: Limits) -> Result<Arc<Self>, AdmissionError> {
        if limits.workers == 0
            || limits.blocking_workers == 0
            || limits.workers > 64
            || limits.blocking_workers > 64
            || limits.admitted > 1_000_000
            || limits.waiters > 1_000_000
            || limits.cleanup > 1_000_000
        {
            return Err(AdmissionError::InvalidLimits);
        }
        let shared = Arc::new(Shared {
            limits,
            state: Mutex::new(State {
                closing: false,
                admitted: 0,
                reserved_bytes: 0,
                issued: 0,
                running: 0,
                retiring: 0,
                tasks: 0,
                next_id: 0,
                waiters: VecDeque::with_capacity(limits.waiters),
                cleanup: vec![None; limits.cleanup.min(limits.admitted)],
            }),
            changed: Condvar::new(),
            cancellation: Notify::new(),
            boundary: EngineBoundary::new(),
            started: AtomicU64::new(0),
            stopped: AtomicU64::new(0),
            panic_payloads: AtomicU64::new(0),
        });
        let start = Arc::clone(&shared);
        let stop = Arc::clone(&shared);
        let runtime = Builder::new_multi_thread()
            .worker_threads(limits.workers)
            .max_blocking_threads(limits.blocking_workers)
            .thread_name("NativeWeave engine")
            .on_thread_start(move || {
                start.started.fetch_add(1, Ordering::AcqRel);
            })
            .on_thread_stop(move || {
                stop.stopped.fetch_add(1, Ordering::AcqRel);
            })
            .build()
            .map_err(|_| AdmissionError::InvalidLimits)?;
        let handle = runtime.handle().clone();
        let owner = Arc::clone(&shared);
        let thread = std::thread::Builder::new()
            .name("NativeWeave runtime owner".into())
            .spawn(move || {
                let mut state = owner.lock();
                while !state.closing || state.admitted != 0 || state.tasks != 0 {
                    state = owner
                        .changed
                        .wait(state)
                        .unwrap_or_else(|poison| poison.into_inner());
                }
                drop(state);
                // Never shutdown_timeout/background: Drop actually joins the runtime workers,
                // including started blocking jobs. No admission gate is held here.
                drop(runtime);
            })
            .map_err(|_| AdmissionError::InvalidLimits)?;
        Ok(Arc::new(Self {
            shared,
            handle,
            join: Mutex::new(JoinState {
                thread: Some(thread),
                joining: false,
                joined: false,
                failed: false,
            }),
            joined: Condvar::new(),
        }))
    }
    fn validate_request(&self, bytes: u64) -> Result<(), AdmissionError> {
        if bytes > self.shared.limits.bytes
            || self.shared.limits.admitted == 0
            || self.shared.limits.cleanup == 0
        {
            return Err(AdmissionError::Impossible);
        }
        Ok(())
    }
    /// Attempts the whole operation/byte/cleanup reservation without bypassing queued waiters.
    pub fn try_acquire(&self, bytes: u64) -> Result<Arc<Admission>, AdmissionError> {
        self.validate_request(bytes)?;
        let mut state = self.shared.lock();
        if state.closing {
            return Err(AdmissionError::Closed);
        }
        if !state.waiters.is_empty() || !self.shared.available(&state, bytes) {
            return Err(AdmissionError::Full);
        }
        Ok(self.shared.reserve(&mut state, bytes))
    }
    /// FIFO metadata-only waiting. Dropping this future removes its slot and wakes the next head.
    /// The caller must not retain payload on credit it has not yet acquired.
    pub async fn acquire(&self, bytes: u64) -> Result<Arc<Admission>, AdmissionError> {
        self.validate_request(bytes)?;
        let (id, receiver) = {
            let mut state = self.shared.lock();
            if state.closing {
                return Err(AdmissionError::Closed);
            }
            if state.waiters.is_empty() && self.shared.available(&state, bytes) {
                return Ok(self.shared.reserve(&mut state, bytes));
            }
            if state.waiters.len() == self.shared.limits.waiters {
                return Err(AdmissionError::Full);
            }
            let id = state.next_id.checked_add(1).ok_or(AdmissionError::Full)?;
            state.next_id = id;
            let (sender, receiver) = oneshot::channel();
            state.waiters.push_back(Waiter { id, bytes, sender });
            (id, receiver)
        };
        let _waiting = Waiting {
            shared: Arc::clone(&self.shared),
            id,
        };
        receiver.await.unwrap_or(Err(AdmissionError::Closed))
    }
    /// Stops new reservations, wakes metadata waiters and notifies cooperative work.
    /// Issued permits, running tasks and retirement bytes remain charged until their owners end.
    pub fn request_close(&self) {
        self.shared.close();
    }
    pub fn is_closing(&self) -> bool {
        self.shared.lock().closing
    }
    /// A coalesced instance signal; no task or timer is allocated for repeated cancellation.
    pub async fn cancelled(&self) {
        loop {
            let notification = self.shared.cancellation.notified();
            if self.is_closing() {
                return;
            }
            notification.await;
        }
    }
    /// Runs one tracked native future plus its bounded completion supervisor.
    /// The supervisor observes Tokio's actual poll panic; dropping a JoinHandle is never evidence.
    pub fn spawn<R, F, C>(
        &self,
        admission: &Arc<Admission>,
        future: F,
        completion: C,
    ) -> Result<(), AdmissionError>
    where
        R: Send + UnwindSafe + 'static,
        F: Future<Output = Result<R, Failure>> + Send + 'static,
        C: FnOnce(Result<R, Failure>) + Send + UnwindSafe + 'static,
    {
        admission.start(&self.shared)?;
        let task = self.handle.spawn(future);
        self.supervise(admission, task, completion);
        Ok(())
    }
    /// Started blocking work is not aborted on close. Its reservation survives until real return.
    pub fn spawn_blocking<R, F, C>(
        &self,
        admission: &Arc<Admission>,
        body: F,
        completion: C,
    ) -> Result<(), AdmissionError>
    where
        R: Send + UnwindSafe + 'static,
        F: FnOnce() -> Result<R, Failure> + Send + 'static,
        C: FnOnce(Result<R, Failure>) + Send + UnwindSafe + 'static,
    {
        admission.start(&self.shared)?;
        let task = self.handle.spawn_blocking(body);
        self.supervise(admission, task, completion);
        Ok(())
    }
    fn supervise<R, C>(
        &self,
        admission: &Arc<Admission>,
        task: tokio::task::JoinHandle<Result<R, Failure>>,
        completion: C,
    ) where
        R: Send + UnwindSafe + 'static,
        C: FnOnce(Result<R, Failure>) + Send + UnwindSafe + 'static,
    {
        let shared = Arc::clone(&self.shared);
        let reservation = Arc::clone(admission);
        self.handle.spawn(async move {
            let mut outcome = match task.await {
                Ok(result) => result,
                Err(error) => {
                    shared.poison();
                    let status = if error.is_panic() {
                        shared.panic_payloads.fetch_add(1, Ordering::AcqRel);
                        std::mem::forget(error.into_panic()); // unknown payload Drop may panic
                        NW_NATIVE_PANIC
                    } else {
                        NW_CONTRACT_VIOLATION
                    };
                    Err(Failure::new(status, ErrorFields::default()))
                }
            };
            if let Err(failure) = &mut outcome {
                if failure.status <= 1 {
                    failure.status = NW_CONTRACT_VIOLATION;
                }
                if matches!(
                    failure.status,
                    NW_NATIVE_PANIC | NW_CONTRACT_VIOLATION | NW_ENGINE_POISONED
                ) {
                    shared.poison();
                }
            }
            // Explicitly unwind-safe completion captures; no blanket AssertUnwindSafe.
            let delivered = shared.boundary.cleanup(move || {
                completion(outcome);
                Ok(())
            });
            if delivered.is_err() {
                shared.poison();
            }
            reservation.terminal();
            // The concrete native owner retains its admission until A07 reclamation,
            // independently from native terminal and this supervisor's completion.
        });
    }
    /// Nonblocking observation followed by an actual finished-thread join; false means pending.
    pub fn try_join(&self) -> bool {
        {
            let state = self.join.lock().unwrap_or_else(|p| p.into_inner());
            if state.joined {
                return true;
            }
            if state.joining || !state.thread.as_ref().is_some_and(JoinHandle::is_finished) {
                return false;
            }
        }
        self.join()
    }
    /// Blocks only the designated native close worker; never a callback/finalizer/admission gate.
    /// Returns after the owner OS thread and all of its Tokio workers were actually joined.
    pub fn join(&self) -> bool {
        self.request_close();
        // A native task/completion/blocking body must not wait for the runtime
        // that owns its own obligation. Close is still requested, but only the
        // designated external close worker may perform a pending join.
        if Handle::try_current().is_ok() {
            return self.join.lock().unwrap_or_else(|p| p.into_inner()).joined;
        }
        let thread = {
            let mut state = self.join.lock().unwrap_or_else(|p| p.into_inner());
            while state.joining {
                state = self.joined.wait(state).unwrap_or_else(|p| p.into_inner());
            }
            if state.joined {
                return true;
            }
            if state.failed {
                return false;
            }
            state.joining = true;
            state.thread.take().expect("one runtime owner thread")
        };
        let success = if let Err(payload) = thread.join() {
            self.shared.poison();
            self.shared.panic_payloads.fetch_add(1, Ordering::AcqRel);
            std::mem::forget(payload);
            false
        } else {
            true
        };
        let mut state = self.join.lock().unwrap_or_else(|p| p.into_inner());
        state.joining = false;
        state.joined = success;
        state.failed = !success;
        self.joined.notify_all();
        success
    }
    /// Fixed diagnostics retain separate issued/running/retiring and worker-stop facts.
    pub fn snapshot(&self) -> Snapshot {
        let joined = self.join.lock().unwrap_or_else(|p| p.into_inner()).joined;
        let state = self.shared.lock();
        Snapshot {
            closing: state.closing,
            poisoned: self.shared.boundary.is_poisoned(),
            joined,
            admitted: state.admitted,
            waiters: state.waiters.len(),
            reserved_bytes: state.reserved_bytes,
            issued: state.issued,
            running: state.running,
            retiring: state.retiring,
            tracked_tasks: state.tasks,
            workers_started: self.shared.started.load(Ordering::Acquire),
            workers_stopped: self.shared.stopped.load(Ordering::Acquire),
            oldest_cleanup_millis: state
                .cleanup
                .iter()
                .flatten()
                .filter_map(|record| record.retiring_at)
                .map(|time| time.elapsed().as_millis().min(u64::MAX as u128) as u64)
                .max()
                .unwrap_or(0),
            panic_payloads: self.shared.panic_payloads.load(Ordering::Acquire)
                + self.shared.boundary.quarantined_payloads(),
        }
    }
}
impl Drop for EngineExecutor {
    fn drop(&mut self) {
        // Nonblocking fallback. The owner thread still roots runtime/state until obligations
        // retire. Only an explicit join can claim closed; timeout/finalization cannot do so.
        self.shared.close();
    }
}

#[cfg(feature = "conformance")]
mod fixture;
#[cfg(test)]
mod tests;
