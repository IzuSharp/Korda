//! Linux owner-thread conformance over real !Send Rc/Cell state. This is not a
//! window, GPU, driver or event-loop implementation. The host explicitly pumps.
use nativeweave_interop_abi::*;
use nativeweave_interop_callbacks::{Subscription, WorkLease};
use nativeweave_interop_errors::EngineBoundary;
use std::cell::{Cell, RefCell};
use std::collections::VecDeque;
use std::rc::Rc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};
use std::thread::{self, ThreadId};

const MAX_OWNERS: usize = 8;
const MAX_QUEUE: usize = 8;
static NEXT_ID: AtomicU64 = AtomicU64::new(1);
static AFFINE_LIVE: AtomicU64 = AtomicU64::new(0);
static AFFINE_DROPPED: AtomicU64 = AtomicU64::new(0);
static HANDLE_LIVE: AtomicU64 = AtomicU64::new(0);
static OBSERVER_LIVE: AtomicU64 = AtomicU64::new(0);
static WORK_LIVE: AtomicU64 = AtomicU64::new(0);
static WORK_DROPPED: AtomicU64 = AtomicU64::new(0);
static BOUNDARY: EngineBoundary = EngineBoundary::new();

struct Gate {
    closing: bool,
    queue: VecDeque<Accepted>,
    limit: usize,
    running: u64,
    completed: u64,
    closed_items: u64,
}
struct Control {
    id: u64,
    owner: ThreadId,
    gate: Mutex<Gate>,
    subscription: Arc<Subscription>,
    release_requested: AtomicBool,
    retired: AtomicBool,
    owner_exited: AtomicBool,
    touches: AtomicU64,
}
impl Control {
    fn gate(&self) -> MutexGuard<'_, Gate> {
        self.gate.lock().unwrap_or_else(|error| {
            let mut gate = error.into_inner();
            gate.closing = true;
            gate
        })
    }
    fn close(&self) {
        let pending = {
            let mut gate = self.gate();
            gate.closing = true;
            gate.closed_items += gate.queue.len() as u64;
            std::mem::take(&mut gate.queue)
        };
        self.subscription.request_stop();
        drop(pending); // Each accepted command ends its A06 native source lease.
    }
    fn quiet(&self) -> NwStatus {
        let gate = self.gate();
        if !gate.closing || !gate.queue.is_empty() || gate.running != 0 {
            return NW_PENDING;
        }
        drop(gate);
        self.subscription.try_retire()
    }
}
struct Affine {
    control: Arc<Control>,
    value: Rc<Cell<u64>>, // Never stored in Send/Sync control or dereferenced off owner.
}
struct Owners(Vec<Affine>);
impl Drop for Owners {
    fn drop(&mut self) {
        for affine in self.0.drain(..) {
            affine.control.owner_exited.store(true, Ordering::Release);
            affine.control.close();
            // The host abandoned owner progress. Preserve up to MAX_OWNERS local
            // roots rather than falsely retiring while managed leases may exist.
            std::mem::forget(affine);
        }
    }
}
thread_local! { static OWNERS: RefCell<Owners> = const { RefCell::new(Owners(Vec::new())) }; }

/// Opaque thread-safe callback control owner, released only after A03/A06 drain.
pub struct NwAffinityHandle(Arc<Control>);
/// Read-only lifetime diagnostics/control reference; does not contain affine state.
pub struct NwAffinityObserver(Arc<Control>);
/// A fixed typed command, returned unchanged when post is rejected.
pub struct NwAffinityWork(u64);
impl Drop for NwAffinityWork {
    fn drop(&mut self) {
        WORK_LIVE.fetch_sub(1, Ordering::SeqCst);
        WORK_DROPPED.fetch_add(1, Ordering::SeqCst);
    }
}
struct Accepted {
    item: Box<NwAffinityWork>,
    _source: WorkLease,
}
struct Running<'a>(&'a Control);
impl Drop for Running<'_> {
    fn drop(&mut self) {
        self.0.gate().running -= 1;
    }
}

/// Fixed counters. Retired means actual Rc destruction occurred on its owner.
#[repr(C)]
#[derive(Default)]
pub struct NwAffinitySnapshot {
    pub queued: u64,
    pub running: u64,
    pub completed: u64,
    pub closed_items: u64,
    pub touches: u64,
    pub release_requested: u64,
    pub retired: u64,
    pub owner_exited: u64,
    pub native_callbacks: u64,
    pub native_sources: u64,
}
fn call(body: impl FnOnce() -> NwStatus + std::panic::UnwindSafe) -> NwStatus {
    BOUNDARY
        .business(|| Ok(body()))
        .unwrap_or_else(|error| error.status)
}
fn cleanup(body: impl FnOnce() + std::panic::UnwindSafe) {
    let _ = BOUNDARY.cleanup(|| {
        body();
        Ok(())
    });
}
fn affine_value(control: &Control) -> Option<Rc<Cell<u64>>> {
    // Every caller establishes owner identity before touching TLS or Rc.
    OWNERS
        .try_with(|owners| {
            owners
                .borrow()
                .0
                .iter()
                .find(|item| item.control.id == control.id)
                .map(|item| Rc::clone(&item.value))
        })
        .ok()
        .flatten()
}
fn retire_on_owner(control: &Control) {
    if !control.release_requested.load(Ordering::Acquire) || !control.subscription.can_release() {
        return;
    }
    let gate = control.gate();
    if !gate.queue.is_empty() || gate.running != 0 {
        return;
    }
    drop(gate);
    let removed = OWNERS
        .try_with(|owners| {
            let mut owners = owners.borrow_mut();
            owners
                .0
                .iter()
                .position(|item| item.control.id == control.id)
                .map(|index| owners.0.swap_remove(index))
        })
        .ok()
        .flatten();
    if let Some(affine) = removed {
        drop(affine.value); // Last Rc, after all queued/native/managed/control leases.
        AFFINE_LIVE.fetch_sub(1, Ordering::SeqCst);
        AFFINE_DROPPED.fetch_add(1, Ordering::SeqCst);
        control.retired.store(true, Ordering::Release);
    }
}

/// Shared A01 bootstrap; the one bit is the finite explicit-owner-pump profile.
/// # Safety
/// Output follows the common aligned writable-capacity ABI contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_extension_get_abi_v1(
    major: u32,
    capacity: u32,
    output: *mut NwAbiInfoV1,
) -> NwStatus {
    // SAFETY: caller forwards the A01 output contract.
    unsafe { write_abi_v1(major, capacity, output, 0, 1) }
}

/// Creates an affine object on this actual OS thread and two distinct control owners.
/// # Safety
/// Both outputs are live aligned disjoint pointer slots until return.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_extension_create_v1(
    limit: u32,
    handle: *mut *mut NwAffinityHandle,
    observer: *mut *mut NwAffinityObserver,
) -> NwStatus {
    if handle.is_null() || observer.is_null() || !handle.is_aligned() || !observer.is_aligned() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: caller supplies live disjoint output slots.
    unsafe {
        handle.write(std::ptr::null_mut());
        observer.write(std::ptr::null_mut());
    }
    call(|| {
        if limit == 0 || limit as usize > MAX_QUEUE {
            return NW_INVALID_ARGUMENT;
        }
        OWNERS.with(|owners| {
            let mut owners = owners.borrow_mut();
            if owners.0.len() == MAX_OWNERS {
                return NW_PENDING;
            }
            if owners.0.try_reserve_exact(1).is_err() {
                return NW_OUT_OF_MEMORY;
            }
            let mut queue = VecDeque::new();
            if queue.try_reserve_exact(limit as usize).is_err() {
                return NW_OUT_OF_MEMORY;
            }
            let id = match NEXT_ID
                .try_update(Ordering::Relaxed, Ordering::Relaxed, |id| id.checked_add(1))
            {
                Ok(id) => id,
                Err(_) => return NW_INVALID_STATE,
            };
            let control = Arc::new(Control {
                id,
                owner: thread::current().id(),
                gate: Mutex::new(Gate {
                    closing: false,
                    queue,
                    limit: limit as usize,
                    running: 0,
                    completed: 0,
                    closed_items: 0,
                }),
                subscription: Arc::new(Subscription::new()),
                release_requested: AtomicBool::new(false),
                retired: AtomicBool::new(false),
                owner_exited: AtomicBool::new(false),
                touches: AtomicU64::new(0),
            });
            let native_handle = Box::new(NwAffinityHandle(Arc::clone(&control)));
            let native_observer = Box::new(NwAffinityObserver(Arc::clone(&control)));
            owners.0.push(Affine {
                control,
                value: Rc::new(Cell::new(0)),
            });
            AFFINE_LIVE.fetch_add(1, Ordering::SeqCst);
            HANDLE_LIVE.fetch_add(1, Ordering::SeqCst);
            OBSERVER_LIVE.fetch_add(1, Ordering::SeqCst);
            // SAFETY: publish only after the complete allocation/ownership setup.
            unsafe {
                handle.write(Box::into_raw(native_handle));
                observer.write(Box::into_raw(native_observer));
            }
            NW_OK
        })
    })
}

/// Registers A06's fixed callback table; no native worker is created.
/// # Safety
/// Handle/table are live aligned borrows. The managed root obeys A06 retirement.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_extension_start_v1(
    handle: *const NwAffinityHandle,
    table: *const NwCallbacksV1,
) -> NwStatus {
    if handle.is_null() || table.is_null() || !handle.is_aligned() || !table.is_aligned() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: caller holds control lifetime and supplies the complete fixed record.
    call(|| {
        // SAFETY: the caller's control lease and fixed record borrow remain live.
        let control = unsafe { &(*handle).0 };
        let gate = control.gate();
        if gate.closing {
            return NW_INVALID_STATE;
        }
        // SAFETY: complete fixed callback table; copied before releasing the gate.
        control.subscription.start(unsafe { table.read() })
    })
}

/// Creates one fixed command. Box/Rc/Arc OOM remains an explicitly infallible abort boundary.
/// # Safety
/// Output is one live aligned pointer slot.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_extension_work_v1(
    value: u64,
    output: *mut *mut NwAffinityWork,
) -> NwStatus {
    if output.is_null() || !output.is_aligned() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: caller supplies a writable output slot.
    unsafe {
        output.write(std::ptr::null_mut());
    }
    call(|| {
        let work = Box::new(NwAffinityWork(value));
        WORK_LIVE.fetch_add(1, Ordering::SeqCst);
        // SAFETY: complete successful owner transfer to the supplied output slot.
        unsafe {
            output.write(Box::into_raw(work));
        }
        NW_OK
    })
}

/// Transfers a work owner only on OK. PENDING/full and INVALID_STATE/closed leave it untouched.
/// # Safety
/// Handle has an A03 control lease; work is a live unique creating-allocator owner.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_extension_post_v1(
    handle: *const NwAffinityHandle,
    work: *mut NwAffinityWork,
) -> NwStatus {
    if handle.is_null() || work.is_null() || !handle.is_aligned() || !work.is_aligned() {
        return NW_INVALID_ARGUMENT;
    }
    call(|| {
        // SAFETY: handle lifetime is protected by the caller's A03 control lease.
        let control = unsafe { &(*handle).0 };
        let mut gate = control.gate();
        if gate.closing {
            return NW_INVALID_STATE;
        }
        if gate.queue.len() == gate.limit {
            return NW_PENDING;
        }
        let source = match control.subscription.reserve_source() {
            Ok(source) => source,
            Err(status) => return status,
        };
        gate.queue.push_back(Accepted {
            // SAFETY: only this successful branch consumes the caller's unique owner.
            item: unsafe { Box::from_raw(work) },
            _source: source,
        });
        NW_OK
    })
}

/// Reads !Send state only after checking the real owner ThreadId.
/// # Safety
/// Observer and output are live aligned borrows with no overlapping mutable output.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_extension_read_v1(
    observer: *const NwAffinityObserver,
    output: *mut u64,
) -> NwStatus {
    if observer.is_null() || output.is_null() || !observer.is_aligned() || !output.is_aligned() {
        return NW_INVALID_ARGUMENT;
    }
    call(|| {
        // SAFETY: caller leases the observer's thread-safe control owner.
        let control = unsafe { &(*observer).0 };
        if control.owner != thread::current().id() {
            return NW_NOT_SUPPORTED;
        }
        if control.gate().closing {
            return NW_INVALID_STATE;
        }
        let Some(value) = affine_value(control) else {
            return NW_INVALID_STATE;
        };
        control.touches.fetch_add(1, Ordering::SeqCst);
        // SAFETY: live output slot; Rc/Cell is accessed solely on its owner thread.
        unsafe {
            output.write(value.get());
        }
        NW_OK
    })
}

/// Executes at most budget commands and performs any now-safe owner-thread destruction.
/// # Safety
/// Observer is a live leased control reference. Registered callbacks obey A06.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_extension_pump_v1(
    observer: *const NwAffinityObserver,
    budget: u32,
) -> NwStatus {
    if observer.is_null() || !observer.is_aligned() || budget == 0 || budget as usize > MAX_QUEUE {
        return NW_INVALID_ARGUMENT;
    }
    call(|| {
        // SAFETY: the observer lifetime is leased by the caller.
        let control = unsafe { &(*observer).0 };
        if control.owner != thread::current().id() {
            return NW_NOT_SUPPORTED;
        }
        for _ in 0..budget {
            let work = {
                let mut gate = control.gate();
                if gate.running != 0 {
                    return NW_PENDING;
                }
                if gate.closing {
                    break;
                }
                let Some(work) = gate.queue.pop_front() else {
                    break;
                };
                gate.running = 1;
                work
            };
            let running = Running(control);
            let Some(value) = affine_value(control) else {
                return NW_INVALID_STATE;
            };
            value.set(value.get().wrapping_add(work.item.0));
            control.touches.fetch_add(1, Ordering::SeqCst);
            control.gate().completed += 1;
            let payload = value.get().to_le_bytes();
            drop(value);
            // SAFETY: A06 work/callback leases protect the cookie; bytes are callback-only borrows.
            let status = unsafe { control.subscription.emit(1, &payload) };
            drop(work);
            drop(running);
            if status != NW_OK {
                control.close();
                break;
            }
        }
        retire_on_owner(control);
        NW_OK
    })
}

/// Closes ingress and terminalizes each accepted queued command without touching Rc.
/// # Safety
/// Handle is a live A03-leased control reference.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_extension_stop_v1(handle: *const NwAffinityHandle) -> NwStatus {
    if handle.is_null() || !handle.is_aligned() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: caller protects the control lifetime; close accesses only Send/Sync bookkeeping.
    call(|| unsafe {
        (*handle).0.close();
        NW_OK
    })
}

/// Observes native callback/source quiet, separately from managed handler drain and affine retirement.
/// # Safety
/// Handle is a live A03-leased control reference.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_extension_retire_v1(handle: *const NwAffinityHandle) -> NwStatus {
    if handle.is_null() || !handle.is_aligned() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: caller protects the control lifetime; no affine data is accessed.
    call(|| unsafe { (*handle).0.quiet() })
}

/// Reports status without touching the Rc; ClosePending persists until an owner pump destroys it.
/// # Safety
/// Observer and complete output record are live aligned nonoverlapping storage.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_extension_snapshot_v1(
    observer: *const NwAffinityObserver,
    output: *mut NwAffinitySnapshot,
) -> NwStatus {
    if observer.is_null() || output.is_null() || !observer.is_aligned() || !output.is_aligned() {
        return NW_INVALID_ARGUMENT;
    }
    call(|| {
        // SAFETY: observer/output follow the fixed leased-record contract.
        let control = unsafe { &(*observer).0 };
        let gate = control.gate();
        let (_, callbacks, sources) = control.subscription.snapshot();
        let value = NwAffinitySnapshot {
            queued: gate.queue.len() as u64,
            running: gate.running,
            completed: gate.completed,
            closed_items: gate.closed_items,
            touches: control.touches.load(Ordering::SeqCst),
            release_requested: control.release_requested.load(Ordering::Acquire) as u64,
            retired: control.retired.load(Ordering::Acquire) as u64,
            owner_exited: control.owner_exited.load(Ordering::Acquire) as u64,
            native_callbacks: callbacks as u64,
            native_sources: sources as u64,
        };
        // SAFETY: one complete initialized record is written to the caller's slot.
        unsafe {
            output.write(value);
        }
        NW_OK
    })
}

/// Bounded any-thread fallback: requests release; it never destroys affine state.
/// # Safety
/// Returns the sole main owner after A03 controls and A06 managed/native leases drain.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_extension_release_v1(handle: *mut NwAffinityHandle) {
    if handle.is_null() {
        return;
    }
    cleanup(|| {
        // SAFETY: caller returns its unique creating-allocator handle.
        let owned = unsafe { Box::from_raw(handle) };
        owned.0.close();
        let _ = owned.0.quiet();
        if !owned.0.subscription.can_release() {
            std::mem::forget(owned); // Unsafe/indeterminate caller: retain, never free live callback state.
            return;
        }
        owned.0.release_requested.store(true, Ordering::Release);
        HANDLE_LIVE.fetch_sub(1, Ordering::SeqCst);
    });
}

/// Releases only a Send/Sync diagnostic reference, never the affine resource.
/// # Safety
/// Observer is a unique owner returned exactly once after its control leases end.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_extension_observer_release_v1(observer: *mut NwAffinityObserver) {
    if observer.is_null() {
        return;
    }
    cleanup(|| {
        // SAFETY: unique matching creator owner; its fields contain no Rc or Cell.
        drop(unsafe { Box::from_raw(observer) });
        OBSERVER_LIVE.fetch_sub(1, Ordering::SeqCst);
    });
}

/// Releases an unaccepted command. Accepted commands belong to the native queue.
/// # Safety
/// Work is a unique live owner, not previously accepted by post.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_extension_work_release_v1(work: *mut NwAffinityWork) {
    if !work.is_null() {
        // SAFETY: caller returns the unique unaccepted creating-allocator owner.
        cleanup(|| drop(unsafe { Box::from_raw(work) }));
    }
}

/// Bounded diagnostic counters, excluding allocator retention and unrelated runtimes.
#[unsafe(no_mangle)]
pub extern "C" fn nw_extension_count_v1(kind: u32) -> u64 {
    match kind {
        0 => AFFINE_LIVE.load(Ordering::SeqCst),
        1 => AFFINE_DROPPED.load(Ordering::SeqCst),
        2 => HANDLE_LIVE.load(Ordering::SeqCst),
        3 => OBSERVER_LIVE.load(Ordering::SeqCst),
        4 => WORK_LIVE.load(Ordering::SeqCst),
        5 => WORK_DROPPED.load(Ordering::SeqCst),
        6 => BOUNDARY.active_calls(),
        _ => u64::MAX,
    }
}

#[cfg(test)]
mod tests;
