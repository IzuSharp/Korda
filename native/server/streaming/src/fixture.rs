//! Opt-in component fixture exports; never exported by the default production rlib.
use super::*;
use nativeweave_interop_abi::*;
use nativeweave_interop_async::Operation;
use nativeweave_interop_errors::ErrorFields;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::atomic::AtomicU64;

static OPERATIONS: AtomicU64 = AtomicU64::new(0);
static BUFFERS: AtomicU64 = AtomicU64::new(0);
static CREDITS: AtomicU64 = AtomicU64::new(0);

struct Session {
    budget: Arc<Budget>,
    request: Arc<RequestBudget>,
}
struct FixtureOperation {
    operation: Operation<Option<OwnedBuffer>>,
    pending: Mutex<Option<Option<OwnedBuffer>>>,
    manual: bool,
}
fn guard(call: impl FnOnce() -> NwStatus) -> NwStatus {
    catch_unwind(AssertUnwindSafe(call)).unwrap_or(NW_NATIVE_PANIC)
}
fn status(error: StreamError) -> NwStatus {
    match error {
        StreamError::InvalidArgument => NW_INVALID_ARGUMENT,
        StreamError::Closed => NW_INVALID_STATE,
        StreamError::Canceled => NW_CANCELED,
        StreamError::ResourceLimit | StreamError::Transport => NW_DOMAIN_ERROR,
    }
}
fn valid<T>(value: *const T) -> bool {
    !value.is_null() && value.is_aligned()
}

/// # Safety
/// Output is writable for one pointer; session owners are returned only on success.
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_streaming_fixture_session_create(
    native: u64,
    managed: u64,
    per_request: u64,
    output: *mut *mut Session,
) -> u32 {
    guard(|| {
        if !valid(output) {
            return NW_INVALID_ARGUMENT;
        }
        // SAFETY: checked pointer shape; fixture caller supplies writable output.
        unsafe {
            output.write(std::ptr::null_mut());
        }
        let budget = match Budget::new(native, managed) {
            Ok(value) => value,
            Err(error) => return status(error),
        };
        let request = match budget.request(per_request) {
            Ok(value) => value,
            Err(error) => return status(error),
        };
        // SAFETY: ownership transfers to the successful caller.
        unsafe {
            output.write(Box::into_raw(Box::new(Session { budget, request })));
        }
        NW_OK
    })
}

/// # Safety
/// Session is a live fixture owner and output is writable; a second independent request shares its global pool.
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_streaming_fixture_session_child(
    session: *const Session,
    per_request: u64,
    output: *mut *mut Session,
) -> u32 {
    guard(|| {
        if !valid(session) || !valid(output) {
            return NW_INVALID_ARGUMENT;
        }
        // SAFETY: caller keeps the input session alive for this call.
        let session = unsafe { &*session };
        // SAFETY: output is caller-owned writable storage.
        unsafe {
            output.write(std::ptr::null_mut());
        }
        let request = match session.budget.request(per_request) {
            Ok(value) => value,
            Err(error) => return status(error),
        };
        // SAFETY: transfers sole returned owner.
        unsafe {
            output.write(Box::into_raw(Box::new(Session {
                budget: Arc::clone(&session.budget),
                request,
            })));
        }
        NW_OK
    })
}

/// # Safety
/// Session remains live during this call.
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_streaming_fixture_session_close(session: *const Session) -> u32 {
    guard(|| {
        if !valid(session) {
            return NW_INVALID_ARGUMENT;
        }
        // SAFETY: live borrowed fixture session.
        unsafe {
            (*session).request.close();
        }
        NW_OK
    })
}

/// # Safety
/// Session is the sole returned owner and no concurrent caller uses its pointer.
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_streaming_fixture_session_release(session: *mut Session) {
    if valid(session) {
        // SAFETY: caller transfers its unique Box owner; independent buffers retain their request anchors.
        drop(unsafe { Box::from_raw(session) });
    }
}

/// # Safety
/// Session is live and output points to four writable u64 counters.
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_streaming_fixture_snapshot(
    session: *const Session,
    output: *mut u64,
) -> u32 {
    guard(|| {
        if !valid(session) || !valid(output) {
            return NW_INVALID_ARGUMENT;
        }
        // SAFETY: caller leases the session.
        let session = unsafe { &*session };
        let global = session.budget.snapshot();
        let request = session.request.snapshot();
        // SAFETY: caller guarantees four writable counters.
        unsafe {
            std::ptr::copy_nonoverlapping(
                [global.0, global.1, request.0, request.1].as_ptr(),
                output,
                4,
            );
        }
        NW_OK
    })
}

/// # Safety
/// Session is live, bytes are readable for length and output is writable. Input is borrowed synchronously only.
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_streaming_fixture_read_create(
    session: *const Session,
    data: *const u8,
    length: u32,
    manual: u32,
    output: *mut *mut FixtureOperation,
) -> u32 {
    guard(|| {
        if !valid(session) || !valid(output) || manual > 1 || (length > 0 && data.is_null()) {
            return NW_INVALID_ARGUMENT;
        }
        // SAFETY: caller provides writable output and live session.
        unsafe {
            output.write(std::ptr::null_mut());
        }
        // SAFETY: borrowed live session.
        let request = unsafe { &(*session).request };
        if request.is_closed() {
            return NW_INVALID_STATE;
        }
        let buffer = if length == 0 {
            None
        } else {
            let reservation = match request.reserve_payload(length as usize) {
                Ok(value) => value,
                Err(error) => return status(error),
            };
            // SAFETY: non-null input is readable for caller-supplied length during this synchronous entry.
            let bytes = unsafe { std::slice::from_raw_parts(data, length as usize) };
            match reservation.copy(bytes) {
                Ok(value) => Some(value),
                Err(error) => return status(error),
            }
        };
        let operation = Box::new(FixtureOperation {
            operation: Operation::new(),
            pending: Mutex::new(Some(buffer)),
            manual: manual != 0,
        });
        // SAFETY: successful output transfers the sole operation owner.
        unsafe {
            output.write(Box::into_raw(operation));
        }
        OPERATIONS.fetch_add(1, Ordering::SeqCst);
        NW_OK
    })
}

fn complete(operation: &FixtureOperation, terminal_status: u32) -> u32 {
    let pending = lock(&operation.pending).take();
    let Some(result) = pending else {
        return NW_INVALID_STATE;
    };
    let result = if terminal_status == NW_OK {
        Some(result)
    } else {
        drop(result);
        None
    };
    // SAFETY: start accepted the exact fixed callback descriptor; Core retains it until actual retirement.
    unsafe {
        operation
            .operation
            .complete(terminal_status, result, ErrorFields::default())
    }
}

/// # Safety
/// Operation and callbacks are live; the callback root obeys Core retirement rules.
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_streaming_fixture_start(
    operation: *const FixtureOperation,
    callbacks: *const NwOperationCallbacksV1,
) -> u32 {
    guard(|| {
        if !valid(operation) || !valid(callbacks) {
            return NW_INVALID_ARGUMENT;
        }
        // SAFETY: both records are borrowed for this synchronous entry.
        let operation = unsafe { &*operation };
        // SAFETY: callback storage is borrowed and readable for this synchronous call.
        let callbacks = unsafe { *callbacks };
        let result = operation.operation.start(callbacks);
        if result != NW_OK {
            return result;
        }
        if operation.operation.cancel_requested() {
            return complete(operation, NW_CANCELED);
        }
        if !operation.manual {
            return complete(operation, NW_OK);
        }
        NW_OK
    })
}

/// # Safety
/// Operation is live under an admitted control lease.
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_streaming_fixture_cancel(operation: *const FixtureOperation) -> u32 {
    guard(|| {
        if !valid(operation) {
            return NW_INVALID_ARGUMENT;
        }
        // SAFETY: borrowed operation control.
        let operation = unsafe { &*operation };
        operation.operation.request_cancel();
        if operation.operation.snapshot().0 == nativeweave_interop_async::Phase::Running {
            let _ = complete(operation, NW_CANCELED);
        }
        NW_OK
    })
}

/// # Safety
/// Operation is live; output is writable for one owned-buffer pointer.
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_streaming_fixture_take(
    operation: *const FixtureOperation,
    output: *mut *mut OwnedBuffer,
) -> u32 {
    guard(|| {
        if !valid(operation) || !valid(output) {
            return NW_INVALID_ARGUMENT;
        }
        // SAFETY: writable result output initialized before taking ownership.
        unsafe {
            output.write(std::ptr::null_mut());
        }
        // SAFETY: live leased operation.
        match unsafe { &*operation }.operation.take_result() {
            Ok(Some(buffer)) => {
                // SAFETY: successful take transfers its buffer exactly once.
                unsafe {
                    output.write(Box::into_raw(Box::new(buffer)));
                }
                BUFFERS.fetch_add(1, Ordering::SeqCst);
                NW_OK
            }
            Ok(None) => NW_OK,
            Err(status) => status,
        }
    })
}

/// # Safety
/// Operation is live under a control lease.
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_streaming_fixture_retire(operation: *const FixtureOperation) -> u32 {
    guard(|| {
        if !valid(operation) {
            return NW_INVALID_ARGUMENT;
        }
        // SAFETY: caller leases the operation.
        unsafe { &*operation }.operation.try_retire()
    })
}

/// # Safety
/// Unique operation owner is never-started or actually retired; no other caller uses it.
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_streaming_fixture_release(operation: *mut FixtureOperation) {
    // SAFETY: caller promises a live unique operation pointer; the gate prevents uncertain reclamation.
    if valid(operation) && unsafe { &*operation }.operation.can_release() {
        // SAFETY: ownership is unique and retirement was checked.
        drop(unsafe { Box::from_raw(operation) });
        OPERATIONS.fetch_sub(1, Ordering::SeqCst);
    }
}

/// # Safety
/// Buffer remains alive; output is one writable u64.
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_streaming_fixture_buffer_length(
    buffer: *const OwnedBuffer,
    output: *mut u64,
) -> u32 {
    guard(|| {
        if !valid(buffer) || !valid(output) {
            return NW_INVALID_ARGUMENT;
        }
        // SAFETY: leased input and writable scalar output.
        unsafe {
            output.write((*buffer).len() as u64);
        }
        NW_OK
    })
}

/// # Safety
/// Buffer is live; destination is writable for capacity. No destination pointer is retained.
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_streaming_fixture_buffer_copy(
    buffer: *const OwnedBuffer,
    destination: *mut u8,
    capacity: u64,
) -> u32 {
    guard(|| {
        if !valid(buffer) || destination.is_null() {
            return NW_INVALID_ARGUMENT;
        }
        // SAFETY: live native owner under managed SafeHandle lease.
        let bytes = unsafe { &*buffer }.as_slice();
        if capacity < bytes.len() as u64 {
            return NW_BUFFER_TOO_SMALL;
        }
        // SAFETY: caller supplies nonoverlapping writable destination with checked capacity.
        unsafe {
            std::ptr::copy_nonoverlapping(bytes.as_ptr(), destination, bytes.len());
        }
        NW_OK
    })
}

/// # Safety
/// Buffer is live and output writable; credit owner is returned only on success.
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_streaming_fixture_buffer_credit(
    buffer: *const OwnedBuffer,
    capacity: u32,
    output: *mut *mut Credit,
) -> u32 {
    guard(|| {
        if !valid(buffer) || !valid(output) {
            return NW_INVALID_ARGUMENT;
        }
        // SAFETY: valid writable result and leased buffer.
        unsafe {
            output.write(std::ptr::null_mut());
        }
        // SAFETY: buffer remains live throughout grant admission.
        match unsafe { &*buffer }.acquire_managed(capacity as usize) {
            Ok(credit) => {
                // SAFETY: transfers a unique credit owner.
                unsafe {
                    output.write(Box::into_raw(Box::new(credit)));
                }
                CREDITS.fetch_add(1, Ordering::SeqCst);
                NW_OK
            }
            Err(error) => status(error),
        }
    })
}

/// # Safety
/// Session is live and output writable for one credit owner.
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_streaming_fixture_managed_credit(
    session: *const Session,
    capacity: u32,
    output: *mut *mut Credit,
) -> u32 {
    guard(|| {
        if !valid(session) || !valid(output) {
            return NW_INVALID_ARGUMENT;
        }
        // SAFETY: initialize caller result before admission.
        unsafe {
            output.write(std::ptr::null_mut());
        }
        // SAFETY: session is borrowed for this entry.
        match unsafe { &*session }
            .request
            .try_acquire(Side::Managed, capacity as u64)
        {
            Ok(credit) => {
                // SAFETY: sole new owner transfers on success.
                unsafe {
                    output.write(Box::into_raw(Box::new(credit)));
                }
                CREDITS.fetch_add(1, Ordering::SeqCst);
                NW_OK
            }
            Err(error) => status(error),
        }
    })
}

/// # Safety
/// Credit is its sole returned owner, released once.
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_streaming_fixture_credit_release(credit: *mut Credit) {
    if valid(credit) {
        // SAFETY: unique returned Box ownership.
        drop(unsafe { Box::from_raw(credit) });
        CREDITS.fetch_sub(1, Ordering::SeqCst);
    }
}

/// # Safety
/// Buffer is its sole returned owner, released once after all physical leases end.
#[unsafe(no_mangle)]
unsafe extern "C" fn nw_streaming_fixture_buffer_release(buffer: *mut OwnedBuffer) {
    if valid(buffer) {
        // SAFETY: sole returned buffer Box; Bytes keeps any independent native clones alive.
        drop(unsafe { Box::from_raw(buffer) });
        BUFFERS.fetch_sub(1, Ordering::SeqCst);
    }
}

#[unsafe(no_mangle)]
extern "C" fn nw_streaming_fixture_live(kind: u32) -> u64 {
    match kind {
        0 => OPERATIONS.load(Ordering::SeqCst),
        1 => BUFFERS.load(Ordering::SeqCst),
        2 => CREDITS.load(Ordering::SeqCst),
        _ => u64::MAX,
    }
}
