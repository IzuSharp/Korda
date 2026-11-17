use super::*;
use nativeweave_interop_abi::{NW_CANCELED, NW_OUT_OF_MEMORY};
use nativeweave_interop_errors::{EngineBoundary, Failure, create_owner};
use nativeweave_interop_memory::{
    Buffer, nw_memory_fixture_copy_v1, nw_memory_fixture_counter_v1, nw_memory_fixture_release_v1,
    nw_memory_fixture_view_v1,
};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};

static BOUNDARY: EngineBoundary = EngineBoundary::new();
static LIVE: AtomicU64 = AtomicU64::new(0);
static CREATED: AtomicU64 = AtomicU64::new(0);
static RELEASED: AtomicU64 = AtomicU64::new(0);
static BAD_RELEASE: AtomicU64 = AtomicU64::new(0);
static INPUT_BYTES: AtomicU64 = AtomicU64::new(0);

struct BufferOwner(*mut Buffer);
// SAFETY: this wrapper uniquely owns the immutable A04 Buffer and calls only its
// free-threaded creator destructor. It never clones the raw ownership pointer.
unsafe impl Send for BufferOwner {}
impl BufferOwner {
    fn into_raw(self) -> *mut Buffer {
        let value = self.0;
        std::mem::forget(self);
        value
    }
}
impl Drop for BufferOwner {
    fn drop(&mut self) {
        // SAFETY: unique owner from the same statically linked A04 creator.
        unsafe { nw_memory_fixture_release_v1(self.0) };
    }
}

pub struct Fixture {
    operation: Arc<Operation<BufferOwner>>,
    input: Vec<u8>,
    mode: u32,
    worker_started: AtomicBool,
}
impl Drop for Fixture {
    fn drop(&mut self) {
        INPUT_BYTES.fetch_sub(self.input.len() as u64, Ordering::SeqCst);
        LIVE.fetch_sub(1, Ordering::SeqCst);
        RELEASED.fetch_add(1, Ordering::SeqCst);
    }
}

fn result(bytes: &[u8]) -> Result<BufferOwner, u32> {
    let mut pointer = std::ptr::null_mut();
    // SAFETY: readable bounded owned input and one exclusive output owner slot.
    let status = unsafe {
        nw_memory_fixture_copy_v1(bytes.as_ptr(), bytes.len() as u64, 0, 0, &mut pointer)
    };
    if status == NW_OK {
        Ok(BufferOwner(pointer))
    } else {
        Err(status)
    }
}

fn complete(fixture: &Fixture, status: u32) -> u32 {
    let owned = if status == NW_OK {
        match result(&fixture.input) {
            Ok(value) => Some(value),
            Err(error) => return error,
        }
    } else {
        None
    };
    // SAFETY: caller retains fixture; operation's callback lease and managed
    // two-sided protocol preserve its cookie until retirement.
    unsafe {
        fixture.operation.complete(
            status,
            owned,
            ErrorFields {
                domain: 17,
                code: -71,
                correlation_id: 123,
            },
        )
    }
}

/// # Safety
/// Input is readable for length; output is an exclusive naturally aligned owner slot.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_async_fixture_create_v1(
    mode: u32,
    data: *const u8,
    length: u64,
    fail: u32,
    output: *mut *mut Fixture,
) -> u32 {
    // SAFETY: exact caller output is initialized before fallible work.
    unsafe {
        create_owner(&BOUNDARY, output, || {
            if length > 1024 || (length != 0 && data.is_null()) || fail == 1 {
                return Err(Failure::new(NW_INVALID_ARGUMENT, ErrorFields::default()));
            }
            let bytes = if length == 0 {
                &[]
            } else {
                // SAFETY: caller's input validity contract and checked <=1024 extent.
                std::slice::from_raw_parts(data, length as usize)
            };
            let mut input = Vec::new();
            input
                .try_reserve_exact(bytes.len())
                .map_err(|_| Failure::new(NW_OUT_OF_MEMORY, ErrorFields::default()))?;
            input.extend_from_slice(bytes);
            let fixture = Box::new(Fixture {
                operation: Arc::new(Operation::new()),
                input,
                mode,
                worker_started: AtomicBool::new(false),
            });
            LIVE.fetch_add(1, Ordering::SeqCst);
            CREATED.fetch_add(1, Ordering::SeqCst);
            INPUT_BYTES.fetch_add(length, Ordering::SeqCst);
            if fail == 2 {
                return Err(Failure::new(NW_OUT_OF_MEMORY, ErrorFields::default()));
            }
            Ok(fixture)
        })
    }
}

/// # Safety
/// Fixture is live under a control lease; callback table has its declared readable capacity.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_async_fixture_start_v1(
    pointer: *const Fixture,
    table: *const NwOperationCallbacksV1,
) -> u32 {
    if pointer.is_null() || table.is_null() || !table.is_aligned() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: read only the initialized capacity before reading a full record.
    if unsafe { table.cast::<u32>().read() } < size_of::<NwOperationCallbacksV1>() as u32 {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: caller provides live fixture and the validated complete record.
    let (fixture, callbacks) = unsafe { (&*pointer, table.read()) };
    if fixture.mode & 1 != 0 {
        return NW_INVALID_ARGUMENT;
    }
    let status = fixture.operation.start(callbacks);
    if status != NW_OK {
        return status;
    }
    if fixture.mode & 2 != 0 {
        let _ = complete(fixture, NW_OK);
    }
    if fixture.mode & 8 != 0 {
        return NW_INVALID_ARGUMENT;
    } // isolated defective-adapter probe
    NW_OK
}

/// # Safety
/// Pointer is a live fixture under its exact control lease.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_async_fixture_cancel_v1(pointer: *const Fixture) -> u32 {
    if pointer.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: live fixture guaranteed by caller.
    let fixture = unsafe { &*pointer };
    fixture.operation.request_cancel();
    if fixture.mode & 4 != 0 && fixture.operation.snapshot().0 == Phase::Running {
        let _ = complete(fixture, NW_CANCELED);
    }
    NW_OK
}

/// # Safety
/// Pointer is retained while native completion invokes the managed callback.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_async_fixture_complete_v1(pointer: *const Fixture, status: u32) -> u32 {
    if pointer.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: live control lease protects the concrete fixture.
    complete(unsafe { &*pointer }, status)
}

/// # Safety
/// Pointer is retained while the source reservation is established; the native
/// source thereafter prevents fixture release until its final access completes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_async_fixture_spawn_v1(pointer: *const Fixture, status: u32) -> u32 {
    if pointer.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: initial caller control lease protects source admission.
    let fixture = unsafe { &*pointer };
    if fixture.worker_started.swap(true, Ordering::SeqCst) {
        return NW_INVALID_STATE;
    }
    let source = match fixture.operation.reserve_source() {
        Ok(source) => source,
        Err(error) => return error,
    };
    let address = pointer as usize;
    match std::thread::Builder::new()
        .name("a07-conformance".into())
        .spawn(move || {
            // SAFETY: source prevents Retired/release until dropped below. No raw
            // fixture access occurs after its drop. This is a bounded test worker,
            // not an implementation of the optional engine executor or OS join gate.
            let fixture = unsafe { &*(address as *const Fixture) };
            let _ = complete(fixture, status);
            drop(source);
        }) {
        Ok(worker) => {
            drop(worker);
            NW_OK
        }
        Err(_) => NW_OUT_OF_MEMORY,
    }
}

/// # Safety
/// Live fixture and exclusively writable output owner slot; failure leaves null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_async_fixture_take_v1(
    pointer: *const Fixture,
    output: *mut *mut Buffer,
) -> u32 {
    if output.is_null() || !output.is_aligned() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: caller-owned output initialized before any failure.
    unsafe { output.write(std::ptr::null_mut()) };
    if pointer.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: physical control lease protects fixture until the move completes.
    let fixture = unsafe { &*pointer };
    if fixture.mode & 16 != 0 {
        return NW_OUT_OF_MEMORY;
    }
    match fixture.operation.take_result() {
        Ok(result) => {
            // SAFETY: one prepared owner is moved once; no fallible work follows.
            unsafe { output.write(result.into_raw()) };
            NW_OK
        }
        Err(status) => status,
    }
}

/// # Safety
/// Source is a live immutable A04 buffer; outputs are exclusive writable slots.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_async_fixture_view_v1(
    pointer: *const Buffer,
    address: *mut *const u8,
    length: *mut u64,
) -> u32 {
    let mut capacity = 0;
    // SAFETY: passes the exact creating crate's view contract and local capacity slot.
    unsafe { nw_memory_fixture_view_v1(pointer, address, length, &mut capacity) }
}

/// # Safety
/// Exactly one previously transferred result owner; no live borrowed view.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_async_fixture_buffer_release_v1(pointer: *mut Buffer) {
    // SAFETY: exact creator/allocator pairing from typed take.
    unsafe { nw_memory_fixture_release_v1(pointer) };
}

/// # Safety
/// Live fixture protected by a caller control lease.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_async_fixture_retire_v1(pointer: *const Fixture) -> u32 {
    if pointer.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: retained fixture throughout nonblocking check.
    let fixture = unsafe { &*pointer };
    if fixture.mode & 32 != 0 {
        return NW_INVALID_STATE;
    } // isolated cleanup-failure probe
    fixture.operation.try_retire()
}

/// # Safety
/// Live operation and exclusive output owner slot. The returned source owner has
/// a separate lifetime and must be dropped using source_release exactly once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_async_fixture_source_create_v1(
    pointer: *const Fixture,
    output: *mut *mut std::ffi::c_void,
) -> u32 {
    if output.is_null() || !output.is_aligned() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: exclusive output owner slot initialized before fallible work.
    unsafe { output.write(std::ptr::null_mut()) };
    if pointer.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: live operation control lease while a native source owner is created.
    match unsafe { (*pointer).operation.reserve_source() } {
        Ok(source) => {
            let owned = Box::new(source);
            // SAFETY: one independent source owner is transferred to the caller.
            unsafe { output.write(Box::into_raw(owned).cast()) };
            NW_OK
        }
        Err(status) => status,
    }
}

/// # Safety
/// Exactly one live source owner returned by source_create; no operation pointer is needed.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_async_fixture_source_release_v1(pointer: *mut std::ffi::c_void) {
    if !pointer.is_null() {
        // SAFETY: exact original boxed source owner; dropping it has no callback.
        drop(unsafe { Box::from_raw(pointer.cast::<WorkLease<BufferOwner>>()) });
    }
}

/// # Safety
/// Deliberately duplicated notice under a real callback lease; caller retains fixture.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_async_fixture_duplicate_notice_v1(pointer: *const Fixture) -> u32 {
    if pointer.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: retained fixture; malformed notice is test-only but its lifetime is valid.
    let fixture = unsafe { &*pointer };
    let (callbacks, status, error) = {
        let mut state = fixture.operation.state();
        if state.phase != Phase::TerminalReady {
            return NW_INVALID_STATE;
        }
        let Some(callbacks) = state.callbacks else {
            return NW_INVALID_STATE;
        };
        state.callbacks_in_flight += 1;
        (callbacks, state.terminal_status.unwrap(), state.error)
    };
    let _lease = CallbackLease(&fixture.operation);
    // SAFETY: separate native lease prevents quiet while valid rooted cookie is invoked.
    unsafe { callbacks.on_terminal.unwrap()(callbacks.cookie, status, &error) }
}

/// # Safety
/// Unique Created/Retired owner with no external controls in flight.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_async_fixture_release_v1(pointer: *mut Fixture) {
    if pointer.is_null() {
        return;
    }
    // SAFETY: caller retains original owner; broken preconditions retain it safely.
    if !unsafe { (*pointer).operation.can_release() } {
        BAD_RELEASE.fetch_add(1, Ordering::SeqCst);
        return;
    }
    // SAFETY: unique creating Box after the complete retirement protocol.
    drop(unsafe { Box::from_raw(pointer) });
}

#[unsafe(no_mangle)]
pub extern "C" fn nw_async_fixture_count_v1(kind: u32) -> u64 {
    match kind {
        0 => LIVE.load(Ordering::SeqCst),
        1 => CREATED.load(Ordering::SeqCst),
        2 => RELEASED.load(Ordering::SeqCst),
        3 => BAD_RELEASE.load(Ordering::SeqCst),
        4 => INPUT_BYTES.load(Ordering::SeqCst),
        5 => nw_memory_fixture_counter_v1(0),
        6 => nw_memory_fixture_counter_v1(4),
        _ => u64::MAX,
    }
}
