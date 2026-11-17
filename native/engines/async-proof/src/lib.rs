//! Real bounded Hyper/Tokio HTTP1 proof engine. These exact exports are the
//! A09/A10/A11 inventory input; the network implementation is in engine.rs.
mod engine;
pub mod oracle;
use bytes::Bytes;
use engine::{
    COUNTERS, NativeResult, OperationState, RequestKind, ResponseState, State, count, lock,
};
use nativeweave_interop_abi::*;
use nativeweave_interop_async::Operation;
use nativeweave_interop_errors::{EngineBoundary, ErrorFields, Failure, create_owner};
use std::sync::{
    Arc, Mutex,
    atomic::{AtomicBool, Ordering},
};

static BOUNDARY: EngineBoundary = EngineBoundary::new();

struct WorkerCount;
impl Drop for WorkerCount {
    fn drop(&mut self) {
        COUNTERS[9].fetch_sub(1, Ordering::SeqCst);
    }
}

/// Fixed profile with independent stream, operation and retained-byte quotas.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct NwHttpLimitsV1 {
    pub struct_size: u32,
    pub flags: u32,
    pub streams: u32,
    pub operations: u32,
    pub retained_bytes: u32,
    pub chunk_bytes: u32,
}
/// Opaque host; one runtime/OS worker is created only by this engine's start.
pub struct NwHttpHost {
    state: Arc<State>,
    listener: Mutex<Option<std::net::TcpListener>>,
    worker: Mutex<Option<std::thread::JoinHandle<()>>>,
    worker_failed: AtomicBool,
}
impl Drop for NwHttpHost {
    fn drop(&mut self) {
        COUNTERS[0].fetch_sub(1, Ordering::SeqCst);
    }
}
/// Opaque A07 operation owner, independently retained through native source exit.
pub struct NwHttpOperation {
    inner: Arc<OperationState>,
}
impl Drop for NwHttpOperation {
    fn drop(&mut self) {
        COUNTERS[1].fetch_sub(1, Ordering::SeqCst);
        COUNTERS[13].fetch_add(1, Ordering::SeqCst);
    }
}
/// Opaque live HTTP response. Body and retained chunk ownership are independent.
pub struct NwHttpResponse {
    inner: Option<Arc<ResponseState>>,
    counted: bool,
}
impl Drop for NwHttpResponse {
    fn drop(&mut self) {
        if self.counted {
            COUNTERS[2].fetch_sub(1, Ordering::SeqCst);
        }
    }
}
/// Exact creator-owned immutable Bytes view; its backing quota follows the last Bytes owner.
pub struct NwHttpChunk {
    bytes: Bytes,
    counted: bool,
    _permit: Option<tokio::sync::OwnedSemaphorePermit>,
}
impl Drop for NwHttpChunk {
    fn drop(&mut self) {
        if self.counted {
            COUNTERS[3].fetch_sub(1, Ordering::SeqCst);
        }
    }
}

fn fail(status: u32) -> Failure {
    Failure::new(
        status,
        ErrorFields {
            domain: 0x48545450,
            code: 1,
            correlation_id: 0,
        },
    )
}
fn status(body: impl FnOnce() -> Result<(), Failure> + std::panic::UnwindSafe) -> u32 {
    match BOUNDARY.business(body) {
        Ok(()) => NW_OK,
        Err(failure) => failure.status,
    }
}
fn new_operation(state: Arc<State>, kind: RequestKind) -> Result<Box<NwHttpOperation>, Failure> {
    if state.is_closing() {
        return Err(fail(NW_CANCELED));
    }
    let permit = state
        .operations
        .clone()
        .try_acquire_owned()
        .map_err(|_| fail(NW_OUT_OF_MEMORY))?;
    let value = Box::new(NwHttpOperation {
        inner: Arc::new(OperationState {
            operation: Arc::new(Operation::new()),
            state,
            kind,
            cancel: tokio::sync::watch::channel(false).0,
            _permit: permit,
        }),
    });
    COUNTERS[1].fetch_add(1, Ordering::SeqCst);
    Ok(value)
}

/// # Safety
/// Output has its caller-declared capacity and natural alignment.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_asyncproof_get_abi_v1(
    major: u32,
    capacity: u32,
    output: *mut NwAbiInfoV1,
) -> u32 {
    // SAFETY: forwards the exact A01 capacity/alignment/exclusive-output contract.
    unsafe { write_abi_v1(major, capacity, output, 0, 1) }
}

/// # Safety
/// Profile is readable for its declared size; output is exclusive and writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_asyncproof_host_create_v1(
    limits: *const NwHttpLimitsV1,
    output: *mut *mut NwHttpHost,
) -> u32 {
    // SAFETY: exact owner output is initialized by the shared failure-atomic boundary.
    unsafe {
        create_owner(&BOUNDARY, output, || {
            if limits.is_null() || !limits.is_aligned() {
                return Err(fail(NW_INVALID_ARGUMENT));
            }
            // SAFETY: read only initialized capacity before the complete profile.
            if limits.cast::<u32>().read() < 24 {
                return Err(fail(NW_INVALID_ARGUMENT));
            }
            let profile = limits.read();
            if profile.flags != 0
                || !(1..=8).contains(&profile.streams)
                || !(1..=64).contains(&profile.operations)
                || !(1..=4096).contains(&profile.chunk_bytes)
                || profile.retained_bytes < profile.chunk_bytes
                || profile.retained_bytes > 1024 * 1024
            {
                return Err(fail(NW_INVALID_ARGUMENT));
            }
            let listener = std::net::TcpListener::bind((std::net::Ipv4Addr::LOCALHOST, 0))
                .map_err(|_| fail(NW_DOMAIN_ERROR))?;
            listener
                .set_nonblocking(true)
                .map_err(|_| fail(NW_DOMAIN_ERROR))?;
            let port = listener
                .local_addr()
                .map_err(|_| fail(NW_DOMAIN_ERROR))?
                .port();
            let value = Box::new(NwHttpHost {
                state: State::new(
                    port,
                    profile.streams,
                    profile.operations,
                    profile.retained_bytes,
                    profile.chunk_bytes,
                ),
                listener: Mutex::new(Some(listener)),
                worker: Mutex::new(None),
                worker_failed: AtomicBool::new(false),
            });
            COUNTERS[0].fetch_add(1, Ordering::SeqCst);
            Ok(value)
        })
    }
}

/// # Safety
/// Host is under a live control lease; callbacks are the exact rooted Core v1 record.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_asyncproof_host_start_v1(
    host: *const NwHttpHost,
    callbacks: *const NwCallbacksV1,
) -> u32 {
    status(|| {
        if host.is_null() || callbacks.is_null() || !callbacks.is_aligned() {
            return Err(fail(NW_INVALID_ARGUMENT));
        }
        // SAFETY: retained host and readable v1 header; capacity precedes full copy.
        let host = unsafe { &*host };
        // SAFETY: caller promises a readable aligned capacity header before the complete table.
        if unsafe { callbacks.cast::<u32>().read() } < size_of::<NwCallbacksV1>() as u32 {
            return Err(fail(NW_INVALID_ARGUMENT));
        }
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .map_err(|_| fail(NW_DOMAIN_ERROR))?;
        let listener = lock(&host.listener)
            .take()
            .ok_or_else(|| fail(NW_INVALID_STATE))?;
        // SAFETY: complete capacity/alignment checked above; callback lifetime is A06-owned.
        let status = host.state.callbacks.start(unsafe { callbacks.read() });
        if status != NW_OK {
            return Err(fail(status));
        }
        let source = host.state.callbacks.reserve_source().map_err(fail)?;
        let _ = host.state.handle.set(runtime.handle().clone());
        let state = host.state.clone();
        match std::thread::Builder::new()
            .name("nw-http-proof".into())
            .spawn(move || {
                let _source = source;
                COUNTERS[9].fetch_add(1, Ordering::SeqCst);
                let _worker_count = WorkerCount;
                runtime.block_on(state.run(listener));
                drop(runtime); // actual Tokio shutdown precedes OS-thread join acknowledgement
            }) {
            Ok(worker) => {
                *lock(&host.worker) = Some(worker);
                Ok(())
            }
            Err(_) => {
                host.state.close();
                host.state.callbacks.try_retire();
                Err(fail(NW_OUT_OF_MEMORY))
            }
        }
    })
}

/// # Safety
/// Host is retained for the nonblocking close request.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_asyncproof_host_stop_v1(host: *const NwHttpHost) -> u32 {
    if host.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: exact live owner and atomic admission/close state.
    unsafe { (*host).state.close() };
    NW_OK
}

/// # Safety
/// Host remains retained; only completed OS threads are joined here.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_asyncproof_host_retire_v1(host: *const NwHttpHost) -> u32 {
    if host.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: caller's physical control lease preserves host identity.
    let host = unsafe { &*host };
    if !host.state.is_closing() {
        return NW_PENDING;
    }
    if host.worker_failed.load(Ordering::Acquire) {
        return NW_NATIVE_PANIC;
    }
    {
        let mut worker = lock(&host.worker);
        if worker.as_ref().is_some_and(|worker| !worker.is_finished()) {
            return NW_PENDING;
        }
        if let Some(worker) = worker.take()
            && let Err(payload) = worker.join()
        {
            host.worker_failed.store(true, Ordering::Release);
            // Only ownership transfer of this opaque payload is asserted.
            // Resume immediately into A05, which poisons and deliberately
            // retains unknown payloads without invoking arbitrary Drop.
            let _: Result<(), Failure> =
                host.state
                    .boundary
                    .cleanup(std::panic::AssertUnwindSafe(|| {
                        std::panic::resume_unwind(payload)
                    }));
            return NW_NATIVE_PANIC;
        }
    }
    host.state.callbacks.try_retire()
}

/// # Safety
/// Unique never-started or fully retired host; no admitted managed/native controls.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_asyncproof_host_release_v1(host: *mut NwHttpHost) {
    if host.is_null() {
        return;
    }
    // SAFETY: caller retains original owner while checking the release predicate.
    let value = unsafe { &*host };
    if value.worker_failed.load(Ordering::Acquire)
        || !value.state.callbacks.can_release()
        || lock(&value.worker).is_some()
    {
        COUNTERS[15].fetch_add(1, Ordering::SeqCst);
        return;
    }
    // SAFETY: exact creating Box after the complete native/managed retirement protocol.
    drop(unsafe { Box::from_raw(host) });
}

/// # Safety
/// Live host and exclusive output slot.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_asyncproof_host_port_v1(
    host: *const NwHttpHost,
    port: *mut u32,
) -> u32 {
    if host.is_null() || port.is_null() || !port.is_aligned() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: caller retains this exact host owner.
    let host = unsafe { &*host };
    // SAFETY: exclusive writable scalar output.
    unsafe { port.write(host.state.port as u32) };
    NW_OK
}

/// # Safety
/// Called from an admitted A06 handler before application code; host remains rooted.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_asyncproof_handler_claim_v1(host: *const NwHttpHost, id: u64) -> u32 {
    if host.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: retained host; claim and closure use one native admission gate.
    unsafe { (*host).state.claim(id) }
}

/// # Safety
/// Admitted handler control retains host; payload is call-borrowed for length.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_asyncproof_handler_reply_v1(
    host: *const NwHttpHost,
    id: u64,
    status: u32,
    payload: *const u8,
    length: u64,
    delay_ms: u32,
) -> u32 {
    match BOUNDARY.business(|| {
        if host.is_null()
            || !(100..=599).contains(&status)
            || delay_ms > 1000
            || length > 4096
            || (length != 0 && payload.is_null())
        {
            return Err(fail(NW_INVALID_ARGUMENT));
        }
        // SAFETY: bounded readable call borrow; no managed address survives this call.
        let bytes = if length == 0 {
            &[]
        } else {
            // SAFETY: validated non-null readable range for this bounded call borrow.
            unsafe { std::slice::from_raw_parts(payload, length as usize) }
        };
        // SAFETY: live host through the admitted handler control.
        let result = unsafe { (*host).state.reply(id, status as u16, bytes, delay_ms) };
        if result == NW_OK {
            Ok(())
        } else {
            Err(fail(result))
        }
    }) {
        Ok(()) => NW_OK,
        Err(failure) => failure.status,
    }
}

/// # Safety
/// The active managed handler owns this cleanup capability even after ordinary closure.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_asyncproof_handler_ack_v1(host: *const NwHttpHost, id: u64) {
    if !host.is_null() {
        // SAFETY: A06 active-handler drain and its physical owner lease retain host until return.
        unsafe { (*host).state.acknowledge(id) };
    }
}

/// # Safety
/// Live admitted host and exclusive output owner slot; native create copies the route.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_asyncproof_get_create_v1(
    host: *const NwHttpHost,
    route: u32,
    output: *mut *mut NwHttpOperation,
) -> u32 {
    // SAFETY: exact shared create contract, initialized output before fallible work.
    unsafe {
        create_owner(&BOUNDARY, output, || {
            if host.is_null() {
                return Err(fail(NW_INVALID_ARGUMENT));
            }
            new_operation((*host).state.clone(), RequestKind::Get(route))
        })
    }
}

/// # Safety
/// Live response and exclusive output slot; its Arc is retained before returning.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_asyncproof_read_create_v1(
    response: *const NwHttpResponse,
    output: *mut *mut NwHttpOperation,
) -> u32 {
    // This narrow initialization closure only clones immutable Arc identities
    // and constructs a new owner. It never accesses/polls the !UnwindSafe Tokio
    // body mutex; the original response remains owned if construction unwinds.
    // SAFETY: failure-atomic output and live response owner throughout the Arc clone.
    unsafe {
        create_owner(
            &BOUNDARY,
            output,
            std::panic::AssertUnwindSafe(|| {
                if response.is_null() {
                    return Err(fail(NW_INVALID_ARGUMENT));
                }
                let response = (*response)
                    .inner
                    .as_ref()
                    .ok_or_else(|| fail(NW_INVALID_STATE))?;
                new_operation(response.state.clone(), RequestKind::Read(response.clone()))
            }),
        )
    }
}

/// # Safety
/// Created operation and exact rooted Core v1 callbacks, readable for declared size.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_asyncproof_operation_start_v1(
    operation: *const NwHttpOperation,
    callbacks: *const NwOperationCallbacksV1,
) -> u32 {
    if operation.is_null() || callbacks.is_null() || !callbacks.is_aligned() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: capacity validated before the complete fixed record is copied.
    if unsafe { callbacks.cast::<u32>().read() } < size_of::<NwOperationCallbacksV1>() as u32 {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: actual A07 control/root protocol protects this start frame and record.
    unsafe { (*operation).inner.start(callbacks.read()) }
}

/// # Safety
/// Live A07 control lease, including Created; cancellation never commits a terminal result.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_asyncproof_operation_cancel_v1(
    operation: *const NwHttpOperation,
) -> u32 {
    if operation.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: retained operation for its nonblocking cooperative request.
    let operation = unsafe { &(*operation).inner };
    operation.operation.request_cancel();
    operation.cancel.send_replace(true);
    NW_OK
}

/// # Safety
/// Live A07 control lease; OK means native callback and Future sources ended.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_asyncproof_operation_retire_v1(
    operation: *const NwHttpOperation,
) -> u32 {
    if operation.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: exact owner retained through the nonblocking retire check.
    let operation = unsafe { &*operation };
    operation.inner.operation.try_retire()
}

/// # Safety
/// Unique never-started/retired operation with all managed controls returned.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_asyncproof_operation_release_v1(operation: *mut NwHttpOperation) {
    if operation.is_null() {
        return;
    }
    // SAFETY: caller retains the creating owner while checking its state.
    let value = unsafe { &*operation };
    if !value.inner.operation.can_release() {
        COUNTERS[15].fetch_add(1, Ordering::SeqCst);
        return;
    }
    // SAFETY: exact Box owner following two-sided A07 retirement.
    drop(unsafe { Box::from_raw(operation) });
}

/// # Safety
/// Trusted A07 cleanup retains the operation while closing this engine's ordinary admission.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_asyncproof_operation_poison_v1(operation: *const NwHttpOperation) {
    if !operation.is_null() {
        // SAFETY: retained operation; trusted no-throw admission closure.
        let operation = unsafe { &*operation };
        operation.inner.state.close();
    }
}

/// # Safety
/// Live Get operation and exclusive writable outputs. Exactly one transfer; failure leaves empty.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_asyncproof_take_response_v1(
    operation: *const NwHttpOperation,
    output: *mut *mut NwHttpResponse,
    status: *mut u32,
) -> u32 {
    if output.is_null() || !output.is_aligned() || status.is_null() || !status.is_aligned() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: exclusive outputs are cleared before any failure.
    unsafe {
        output.write(std::ptr::null_mut());
        status.write(0)
    };
    if operation.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: physical operation control lease.
    let operation = unsafe { &(*operation).inner };
    if !matches!(operation.kind, RequestKind::Get(_)) {
        return NW_INVALID_STATE;
    }
    let mut owner = Box::new(NwHttpResponse {
        inner: None,
        counted: false,
    }); // allocate before ownership moves
    match operation.operation.take_result() {
        Ok(NativeResult::Response(response)) => {
            let code = response.status;
            owner.inner = Some(response);
            owner.counted = true;
            COUNTERS[2].fetch_add(1, Ordering::SeqCst);
            // SAFETY: prepared owner moves once; no fallible operation follows.
            unsafe {
                status.write(code as u32);
                output.write(Box::into_raw(owner))
            };
            NW_OK
        }
        Ok(_) => NW_CONTRACT_VIOLATION,
        Err(status) => status,
    }
}

/// # Safety
/// Live Read operation and exclusive outputs. EOF transfers a non-null empty owner
/// that must be released, with null data, zero length and end=1.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_asyncproof_take_chunk_v1(
    operation: *const NwHttpOperation,
    output: *mut *mut NwHttpChunk,
    data: *mut *const u8,
    length: *mut u64,
    end: *mut u32,
) -> u32 {
    if output.is_null()
        || !output.is_aligned()
        || data.is_null()
        || !data.is_aligned()
        || length.is_null()
        || !length.is_aligned()
        || end.is_null()
        || !end.is_aligned()
    {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: every exclusive output is initialized before failure or ownership transfer.
    unsafe {
        output.write(std::ptr::null_mut());
        data.write(std::ptr::null());
        length.write(0);
        end.write(0)
    };
    if operation.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: actual A07 control lease retains the operation.
    let operation = unsafe { &(*operation).inner };
    if !matches!(operation.kind, RequestKind::Read(_)) {
        return NW_INVALID_STATE;
    }
    let mut owner = Box::new(NwHttpChunk {
        bytes: Bytes::new(),
        counted: false,
        _permit: None,
    });
    match operation.operation.take_result() {
        Ok(NativeResult::Chunk(bytes, permit)) => {
            owner.bytes = bytes;
            owner._permit = Some(permit);
            owner.counted = true;
            COUNTERS[3].fetch_add(1, Ordering::SeqCst);
            // SAFETY: backing Bytes stay alive through their returned unique owner.
            unsafe {
                data.write(owner.bytes.as_ptr());
                length.write(owner.bytes.len() as u64);
                output.write(Box::into_raw(owner))
            };
            NW_OK
        }
        Ok(NativeResult::Eof) => {
            owner.counted = true;
            COUNTERS[3].fetch_add(1, Ordering::SeqCst);
            // The typed ownership contract transfers an owner on every OK.
            // This empty EOF sentinel is immediately released by the mapper.
            // SAFETY: exclusive output slots were checked and initialized above.
            unsafe {
                end.write(1);
                output.write(Box::into_raw(owner));
            };
            NW_OK
        }
        Ok(_) => NW_CONTRACT_VIOLATION,
        Err(status) => status,
    }
}

/// # Safety
/// Exact unique response owner; existing read operations retain their own Arc and observe cancellation.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_asyncproof_response_release_v1(response: *mut NwHttpResponse) {
    if !response.is_null() {
        // SAFETY: exact Box from typed take; Body input access is separately Arc-owned.
        let owner = unsafe { Box::from_raw(response) };
        if let Some(response) = &owner.inner {
            response.close.send_replace(true);
        }
        drop(owner);
    }
}

/// # Safety
/// Exact unique chunk owner; no live Core read lease remains.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_asyncproof_chunk_release_v1(chunk: *mut NwHttpChunk) {
    if !chunk.is_null() {
        // SAFETY: same creating Box/Bytes allocator; final backing owner returns byte quota.
        drop(unsafe { Box::from_raw(chunk) });
    }
}

/// # Safety
/// Exclusive writable scalar output. Counters disclose no native pointers.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_asyncproof_counters_v1(kind: u32, output: *mut u64) -> u32 {
    if kind >= 16 || output.is_null() || !output.is_aligned() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: caller provides the exclusive naturally aligned output.
    unsafe { output.write(count(kind as usize)) };
    NW_OK
}

#[cfg(test)]
mod tests;

include!("../generated/layout_assertions.rs");
