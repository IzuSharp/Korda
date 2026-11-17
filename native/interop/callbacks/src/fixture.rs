use super::*;
use nativeweave_interop_errors::{EngineBoundary, create_owner};
use std::sync::atomic::{AtomicU64, Ordering};

static BOUNDARY: EngineBoundary = EngineBoundary::new();
static CREATED: AtomicU64 = AtomicU64::new(0);
static RELEASED: AtomicU64 = AtomicU64::new(0);
static BAD_RELEASE: AtomicU64 = AtomicU64::new(0);

pub struct Fixture {
    subscription: Arc<Subscription>,
    mode: u32,
    held_source: Mutex<Option<WorkLease>>,
    callback_gate: (Mutex<(bool, bool)>, std::sync::Condvar),
}

/// # Safety
/// Output is an aligned exclusively borrowed owner slot.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_callbacks_fixture_create_v1(
    mode: u32,
    output: *mut *mut Fixture,
) -> u32 {
    // SAFETY: forwarded caller-owned output; no callbacks occur during creation.
    unsafe {
        create_owner(&BOUNDARY, output, || {
            let fixture = Box::new(Fixture {
                subscription: Arc::new(Subscription::new()),
                mode,
                held_source: Mutex::new(None),
                callback_gate: (Mutex::new((false, false)), std::sync::Condvar::new()),
            });
            CREATED.fetch_add(1, Ordering::Relaxed);
            Ok(fixture)
        })
    }
}

/// # Safety
/// Pointer is a live leased fixture; table initially has at least a readable u32 header.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_callbacks_fixture_start_v1(
    pointer: *const Fixture,
    table: *const NwCallbacksV1,
) -> u32 {
    if pointer.is_null() || table.is_null() || !table.is_aligned() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: the declared initialized capacity precedes reading the complete table.
    if unsafe { table.cast::<u32>().read() } < size_of::<NwCallbacksV1>() as u32 {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: valid live fixture and complete naturally aligned v1 record.
    let (fixture, callbacks) = unsafe { (&*pointer, table.read()) };
    if fixture.mode & 1 != 0 {
        return NW_INVALID_ARGUMENT;
    }
    let status = fixture.subscription.start(callbacks);
    if status != NW_OK {
        return status;
    }
    if fixture.mode & 2 != 0 {
        // SAFETY: start committed and this fixture follows the exact D05 lifetime.
        let _ = unsafe { fixture.subscription.emit(42, b"synchronous-start") };
    }
    // Even callback failure after commit is an accepted start, never a rejection.
    NW_OK
}

/// # Safety
/// Pointer is a live leased fixture. Data is readable for bytes until return.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_callbacks_fixture_emit_v1(
    pointer: *const Fixture,
    kind: u32,
    data: *const u8,
    bytes: u64,
) -> u32 {
    if pointer.is_null() || bytes > isize::MAX as u64 || (bytes != 0 && data.is_null()) {
        return NW_INVALID_ARGUMENT;
    }
    let payload = if bytes == 0 {
        &[]
    } else {
        // SAFETY: caller supplies the declared readable borrowed extent.
        unsafe { std::slice::from_raw_parts(data, bytes as usize) }
    };
    // SAFETY: caller retains live fixture and valid callback protocol throughout.
    unsafe { (*pointer).subscription.emit(kind, payload) }
}

/// # Safety
/// Test-only blocking boundary: caller retains the fixture until this call exits.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_callbacks_fixture_gated_emit_v1(pointer: *const Fixture) -> u32 {
    if pointer.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: test holds a physical control lease across the blocked native call.
    let fixture = unsafe { &*pointer };
    let lease = match fixture.subscription.admit_callback() {
        Ok(lease) => lease,
        Err(status) => return status,
    };
    let (gate, wake) = &fixture.callback_gate;
    let mut state = gate.lock().unwrap_or_else(|p| p.into_inner());
    state.0 = true;
    wake.notify_all();
    while !state.1 {
        state = wake.wait(state).unwrap_or_else(|p| p.into_inner());
    }
    drop(state);
    // SAFETY: exact production callback lease protects the registered cookie;
    // the gate deliberately pauses between admission and managed entry.
    unsafe { lease.invoke(7, b"gated") }
}

/// # Safety
/// Caller retains a live fixture and joins the corresponding gated call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_callbacks_fixture_gate_v1(
    pointer: *const Fixture,
    release: u32,
) -> u32 {
    if pointer.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: retained test control lease keeps this fixture alive.
    let fixture = unsafe { &*pointer };
    let (gate, wake) = &fixture.callback_gate;
    let mut state = gate.lock().unwrap_or_else(|p| p.into_inner());
    if release != 0 {
        state.1 = true;
        wake.notify_all();
    }
    if state.0 { NW_OK } else { NW_PENDING }
}

/// # Safety
/// Pointer is a live leased fixture.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_callbacks_fixture_stop_v1(pointer: *const Fixture) -> u32 {
    if pointer.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: live fixture retained by caller's control lease.
    unsafe { (*pointer).subscription.request_stop() }
}

/// # Safety
/// Pointer is a live leased fixture.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_callbacks_fixture_retire_v1(pointer: *const Fixture) -> u32 {
    if pointer.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: live fixture retained by caller's control lease.
    unsafe { (*pointer).subscription.try_retire() }
}

/// # Safety
/// Pointer is a live fixture with no concurrent release; held source is test-only.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_callbacks_fixture_source_v1(pointer: *const Fixture, hold: u32) -> u32 {
    if pointer.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: caller's lease keeps this fixture alive.
    let fixture = unsafe { &*pointer };
    let mut slot = fixture
        .held_source
        .lock()
        .unwrap_or_else(|p| p.into_inner());
    if hold != 0 {
        if slot.is_some() {
            return NW_INVALID_STATE;
        }
        match fixture.subscription.reserve_source() {
            Ok(lease) => {
                *slot = Some(lease);
                NW_OK
            }
            Err(status) => status,
        }
    } else {
        *slot = None;
        NW_OK
    }
}

/// # Safety
/// Unique owner in Created or Retired, with every managed control lease returned.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_callbacks_fixture_release_v1(pointer: *mut Fixture) {
    if pointer.is_null() {
        return;
    }
    // SAFETY: caller provides a live pointer. Violated release preconditions retain
    // the owner rather than dropping a potentially live cookie/source.
    let fixture = unsafe { &*pointer };
    if !fixture.subscription.can_release() {
        BAD_RELEASE.fetch_add(1, Ordering::Relaxed);
        return;
    }
    // SAFETY: unique original Box, quiescent and no admitted managed calls.
    drop(unsafe { Box::from_raw(pointer) });
    RELEASED.fetch_add(1, Ordering::Relaxed);
}

#[unsafe(no_mangle)]
pub extern "C" fn nw_callbacks_fixture_count_v1(kind: u32) -> u64 {
    match kind {
        0 => CREATED.load(Ordering::Relaxed),
        1 => RELEASED.load(Ordering::Relaxed),
        _ => BAD_RELEASE.load(Ordering::Relaxed),
    }
}

/// # Safety
/// Deliberately wrong-signature managed function is used only in an isolated fatal probe.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_callbacks_fixture_invalid_uco_v1(
    callback: unsafe extern "C" fn(u16) -> u8,
) -> u32 {
    // SAFETY: the isolated probe intentionally exercises the managed runtime's
    // UCO signature rejection; production never uses this signature.
    u32::from(unsafe { callback(120) })
}
