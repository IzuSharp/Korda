//! D06 terminal/result/retirement state reused inside concrete engine adapters.
//!
//! This is not a Future ABI or an executor. Engines own their actual Future,
//! runtime and inputs; one WorkLease covers each still-live native source. The
//! Rust result type stays native and crosses only an engine-specific typed take.

use nativeweave_interop_abi::{
    NW_CONTRACT_VIOLATION, NW_INVALID_ARGUMENT, NW_INVALID_STATE, NW_OK, NW_PENDING, NwErrorInfoV1,
    NwOperationCallbacksV1, NwStatus,
};
use nativeweave_interop_errors::ErrorFields;
use std::sync::{Arc, Mutex, MutexGuard};

/// Native business state; result transfer remains possible after Retired.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Phase {
    Created,
    Running,
    TerminalReady,
    Retired,
}

struct State<R> {
    phase: Phase,
    callbacks: Option<NwOperationCallbacksV1>,
    cancel_requested: bool,
    terminal_status: Option<NwStatus>,
    error: NwErrorInfoV1,
    result: Option<R>,
    taken: bool,
    callbacks_in_flight: usize,
    sources: usize,
    delivery_failed: bool,
}

/// Concrete-engine operation identity with fixed callback and ownership state.
pub struct Operation<R> {
    state: Mutex<State<R>>,
}

impl<R> Default for Operation<R> {
    fn default() -> Self {
        Self::new()
    }
}
impl<R> Operation<R> {
    /// Creates an operation with callback admission closed and no terminal state.
    pub const fn new() -> Self {
        Self {
            state: Mutex::new(State {
                phase: Phase::Created,
                callbacks: None,
                cancel_requested: false,
                terminal_status: None,
                error: NwErrorInfoV1 {
                    struct_size: 24,
                    domain: 0,
                    code: 0,
                    flags: 0,
                    correlation_id: 0,
                },
                result: None,
                taken: false,
                callbacks_in_flight: 0,
                sources: 0,
                delivery_failed: false,
            }),
        }
    }

    fn state(&self) -> MutexGuard<'_, State<R>> {
        // Only fixed bookkeeping and ownership moves happen under this gate;
        // callback, result destruction and engine polling happen after unlocking.
        self.state.lock().unwrap_or_else(|p| p.into_inner())
    }

    /// Validates/copies the callback record then commits responsibility once.
    pub fn start(&self, callbacks: NwOperationCallbacksV1) -> NwStatus {
        if callbacks.struct_size < size_of::<NwOperationCallbacksV1>() as u32
            || callbacks.flags != 0
            || callbacks.cookie == 0
            || callbacks.on_terminal.is_none()
        {
            return NW_INVALID_ARGUMENT;
        }
        let mut state = self.state();
        if state.phase != Phase::Created {
            return NW_INVALID_STATE;
        }
        state.callbacks = Some(callbacks);
        state.phase = Phase::Running;
        NW_OK
    }

    /// Records a cooperative request, including before start; it never sets a terminal outcome.
    pub fn request_cancel(&self) -> NwStatus {
        self.state().cancel_requested = true;
        NW_OK
    }

    /// Whether native execution has received cancellation; an engine chooses its actual outcome.
    pub fn cancel_requested(&self) -> bool {
        self.state().cancel_requested
    }

    /// Commits exactly one status/result before invoking the static callback outside the gate.
    ///
    /// On rejection the supplied result remains owned by this call and is dropped
    /// outside the state gate. Callback failure never rewrites status or notifies twice.
    /// # Safety
    /// The registered function/cookie obeys the ABI; its root stays live until native retirement.
    pub unsafe fn complete(
        &self,
        status: NwStatus,
        result: Option<R>,
        fields: ErrorFields,
    ) -> NwStatus {
        if status == NW_PENDING {
            return NW_INVALID_ARGUMENT;
        }
        let callbacks;
        let error = NwErrorInfoV1 {
            struct_size: 24,
            domain: fields.domain,
            code: fields.code,
            flags: 0,
            correlation_id: fields.correlation_id,
        };
        {
            let mut state = self.state();
            if state.phase != Phase::Running || state.terminal_status.is_some() {
                return NW_INVALID_STATE;
            }
            let Some(value) = state.callbacks else {
                return NW_CONTRACT_VIOLATION;
            };
            callbacks = value;
            state.result = result;
            state.error = error;
            state.terminal_status = Some(status);
            state.phase = Phase::TerminalReady;
            state.callbacks_in_flight += 1;
        }
        let _lease = CallbackLease(self);
        // SAFETY: the acquired native callback lease protects the copied table,
        // and the fixed error snapshot remains on this stack until return.
        let result = unsafe { callbacks.on_terminal.unwrap()(callbacks.cookie, status, &error) };
        if result != NW_OK {
            self.state().delivery_failed = true;
        }
        NW_OK
    }

    /// Moves the already prepared native result exactly once; no allocation follows the move.
    pub fn take_result(&self) -> Result<R, NwStatus> {
        let mut state = self.state();
        if !matches!(state.phase, Phase::TerminalReady | Phase::Retired)
            || state.taken
            || state.terminal_status != Some(NW_OK)
        {
            return Err(NW_INVALID_STATE);
        }
        let result = state.result.take().ok_or(NW_INVALID_STATE)?;
        state.taken = true;
        Ok(result)
    }

    /// Reserves a native worker/future source before it can retain callback state or inputs.
    pub fn reserve_source(self: &Arc<Self>) -> Result<WorkLease<R>, NwStatus> {
        {
            let mut state = self.state();
            if state.phase != Phase::Running {
                return Err(NW_INVALID_STATE);
            }
            state.sources = state.sources.checked_add(1).ok_or(NW_CONTRACT_VIOLATION)?;
        }
        Ok(WorkLease(Arc::clone(self)))
    }

    /// Proves no future callback/input access. It is independent from result transfer.
    pub fn try_retire(&self) -> NwStatus {
        let mut state = self.state();
        match state.phase {
            Phase::Created => NW_INVALID_STATE,
            Phase::Running => NW_PENDING,
            Phase::Retired => NW_OK,
            Phase::TerminalReady => {
                if state.callbacks_in_flight != 0 || state.sources != 0 {
                    return NW_PENDING;
                }
                state.phase = Phase::Retired;
                state.callbacks = None;
                NW_OK
            }
        }
    }

    /// Legal final-owner states; callers additionally ensure all external control calls returned.
    pub fn can_release(&self) -> bool {
        let state = self.state();
        matches!(state.phase, Phase::Created | Phase::Retired)
            && state.callbacks_in_flight == 0
            && state.sources == 0
    }

    /// Fixed status/counter diagnostics without cookie, pointer or borrowed payload disclosure.
    pub fn snapshot(&self) -> (Phase, Option<NwStatus>, bool, usize, usize, bool) {
        let state = self.state();
        (
            state.phase,
            state.terminal_status,
            state.taken,
            state.callbacks_in_flight,
            state.sources,
            state.delivery_failed,
        )
    }
}

struct CallbackLease<'a, R>(&'a Operation<R>);
impl<R> Drop for CallbackLease<'_, R> {
    fn drop(&mut self) {
        self.0.state().callbacks_in_flight -= 1;
    }
}

/// One native access source, retained independently from the operation's application Task.
pub struct WorkLease<R>(Arc<Operation<R>>);
impl<R> WorkLease<R> {
    /// Borrows the operation while this native source still owns its input access obligation.
    pub fn operation(&self) -> &Operation<R> {
        &self.0
    }
}
impl<R> Drop for WorkLease<R> {
    fn drop(&mut self) {
        self.0.state().sources -= 1;
    }
}

#[cfg(feature = "conformance")]
mod fixture;
#[cfg(test)]
mod tests;
