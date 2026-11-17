//! Reusable D05 callback admission and retirement; no runtime or mandatory worker.
//!
//! A domain embeds an Arc<Subscription> and reserves one WorkLease for every
//! native source that can still access its cookie/input. Admission copies the
//! callback table and counts a callback before leaving the mutex. No managed
//! code runs under that mutex. Engine-specific payloads remain borrowed.

use nativeweave_interop_abi::{
    NW_CONTRACT_VIOLATION, NW_INVALID_ARGUMENT, NW_INVALID_STATE, NW_OK, NW_PENDING, NwCallbacksV1,
    NwStatus,
};
use std::sync::{Arc, Mutex, MutexGuard};

/// Native subscription state, separate from queued managed handler retirement.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Phase {
    Created,
    Active,
    StopRequested,
    Retired,
}

struct State {
    phase: Phase,
    callbacks: Option<NwCallbacksV1>,
    active_callbacks: usize,
    active_sources: usize,
}

/// Engine-local callback state. Mutable callback data never changes after start.
pub struct Subscription {
    state: Mutex<State>,
}

impl Default for Subscription {
    fn default() -> Self {
        Self::new()
    }
}

impl Subscription {
    /// Creates a never-started subscription without callback authority.
    pub const fn new() -> Self {
        Self {
            state: Mutex::new(State {
                phase: Phase::Created,
                callbacks: None,
                active_callbacks: 0,
                active_sources: 0,
            }),
        }
    }

    fn state(&self) -> MutexGuard<'_, State> {
        // This mutex encloses only fixed, non-callback bookkeeping. No user or
        // fallible engine code runs there. A poisoned mutex cannot reopen ingress.
        self.state.lock().unwrap_or_else(|poison| {
            let mut state = poison.into_inner();
            if state.phase == Phase::Active {
                state.phase = Phase::StopRequested;
            }
            state
        })
    }

    /// Validates/copies the v1 record and atomically opens callback admission once.
    pub fn start(&self, callbacks: NwCallbacksV1) -> NwStatus {
        if callbacks.struct_size < size_of::<NwCallbacksV1>() as u32
            || callbacks.flags != 0
            || callbacks.cookie == 0
            || callbacks.on_event.is_none()
        {
            return NW_INVALID_ARGUMENT;
        }
        let mut state = self.state();
        if state.phase != Phase::Created {
            return NW_INVALID_STATE;
        }
        state.callbacks = Some(callbacks);
        state.phase = Phase::Active;
        NW_OK
    }

    /// Invokes one borrowed notification under a callback lease, outside the mutex.
    ///
    /// # Safety
    /// The registered unmanaged function obeys its ABI and uses its cookie only
    /// during the call. Its managed root remains live until try_retire returns OK.
    pub unsafe fn emit(&self, kind: u32, payload: &[u8]) -> NwStatus {
        let lease = match self.admit_callback() {
            Ok(lease) => lease,
            Err(status) => return status,
        };
        // SAFETY: caller's callback ABI/lifetime contract is protected by lease.
        unsafe { lease.invoke(kind, payload) }
    }

    fn admit_callback(&self) -> Result<CallbackLease<'_>, NwStatus> {
        let mut state = self.state();
        if state.phase != Phase::Active {
            return Err(NW_INVALID_STATE);
        }
        let callbacks = state.callbacks.ok_or(NW_CONTRACT_VIOLATION)?;
        state.active_callbacks = state
            .active_callbacks
            .checked_add(1)
            .ok_or(NW_CONTRACT_VIOLATION)?;
        Ok(CallbackLease {
            subscription: self,
            callbacks,
        })
    }

    /// Reserves a native source before handing work to another executor.
    pub fn reserve_source(self: &Arc<Self>) -> Result<WorkLease, NwStatus> {
        {
            let mut state = self.state();
            if state.phase != Phase::Active {
                return Err(NW_INVALID_STATE);
            }
            state.active_sources = state
                .active_sources
                .checked_add(1)
                .ok_or(NW_CONTRACT_VIOLATION)?;
        }
        Ok(WorkLease(Arc::clone(self)))
    }

    /// Closes ordinary callback admission without waiting for admitted callbacks.
    pub fn request_stop(&self) -> NwStatus {
        let mut state = self.state();
        if state.phase == Phase::Active {
            state.phase = Phase::StopRequested;
        }
        NW_OK
    }

    /// Permanently proves native quiet; no callback, wait or external call occurs.
    pub fn try_retire(&self) -> NwStatus {
        let mut state = self.state();
        match state.phase {
            Phase::Created => NW_INVALID_STATE,
            Phase::Active => NW_PENDING,
            Phase::Retired => NW_OK,
            Phase::StopRequested => {
                if state.active_callbacks != 0 || state.active_sources != 0 {
                    return NW_PENDING;
                }
                state.phase = Phase::Retired;
                state.callbacks = None;
                NW_OK
            }
        }
    }

    /// Only a never-started or proven quiet owner can be released by its adapter.
    pub fn can_release(&self) -> bool {
        let state = self.state();
        matches!(state.phase, Phase::Created | Phase::Retired)
            && state.active_callbacks == 0
            && state.active_sources == 0
    }

    /// Fixed bookkeeping for bounded diagnostics; no cookie or payload is disclosed.
    pub fn snapshot(&self) -> (Phase, usize, usize) {
        let state = self.state();
        (state.phase, state.active_callbacks, state.active_sources)
    }
}

struct CallbackLease<'a> {
    subscription: &'a Subscription,
    callbacks: NwCallbacksV1,
}
impl CallbackLease<'_> {
    unsafe fn invoke(&self, kind: u32, payload: &[u8]) -> NwStatus {
        let Some(callback) = self.callbacks.on_event else {
            return NW_CONTRACT_VIOLATION;
        };
        // SAFETY: start validated the ABI; this lease prevents cookie retirement
        // and the borrowed payload lives until the synchronous callback returns.
        let result = unsafe {
            callback(
                self.callbacks.cookie,
                kind,
                payload.as_ptr().cast(),
                payload.len() as u64,
            )
        };
        if result != NW_OK {
            self.subscription.request_stop();
        }
        result
    }
}
impl Drop for CallbackLease<'_> {
    fn drop(&mut self) {
        let mut state = self.subscription.state();
        state.active_callbacks -= 1;
    }
}

/// One accepted native source; dropping it proves that source cannot access state again.
pub struct WorkLease(Arc<Subscription>);
impl WorkLease {
    /// Borrows the native owner for the source's bounded work.
    pub fn subscription(&self) -> &Subscription {
        &self.0
    }
}
impl Drop for WorkLease {
    fn drop(&mut self) {
        let mut state = self.0.state();
        state.active_sources -= 1;
    }
}

// Exact exported conformance fixture. Product engines reuse the Rust type above
// and their own generated prefixes; this test adapter is not a public Engine API.
#[cfg(feature = "conformance")]
mod fixture;

#[cfg(test)]
mod tests;
