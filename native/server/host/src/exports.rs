//! Exact C ABI. Opaque owners are creator-owned; every release is nonblocking.
use crate::{
    ApiError, Result, config,
    engine::{Host, Job, OpResult, OpState, RequestState, Reservation, SideGuard},
    invalid, lock,
    metadata::{Metadata, view},
    wire::*,
};
use nativeweave_interop_abi::*;
use nativeweave_interop_errors::{EngineBoundary, Failure, write_error};
use nativeweave_server_streaming::{Credit, OwnedBuffer, Side};
use std::{
    collections::HashSet,
    sync::{Arc, Weak, atomic::Ordering},
};

static BOUNDARY: EngineBoundary = EngineBoundary::new();
pub struct NwServer {
    pub(crate) inner: Arc<Host>,
}
pub struct NwServerRequest {
    pub(crate) inner: Arc<RequestState>,
}
pub struct NwServerOperation {
    pub(crate) inner: Arc<OpState>,
}
pub struct NwServerMetadata {
    inner: Metadata,
}
pub struct NwServerBuffer {
    buffer: Option<OwnedBuffer>,
    host: Weak<Host>,
}
pub struct NwServerManagedCredit {
    credit: Option<Credit>,
    host: Weak<Host>,
}
pub struct NwServerWriteReservation {
    inner: Option<Reservation>,
}
impl Drop for NwServerBuffer {
    fn drop(&mut self) {
        drop(self.buffer.take());
        if let Some(host) = self.host.upgrade() {
            host.reap();
        }
    }
}
impl Drop for NwServerManagedCredit {
    fn drop(&mut self) {
        drop(self.credit.take());
        if let Some(host) = self.host.upgrade() {
            host.reap();
        }
    }
}

fn ffi(
    error: *mut NwErrorInfoV1,
    body: impl FnOnce() -> Result<()> + std::panic::UnwindSafe,
) -> u32 {
    match BOUNDARY.business(|| body().map_err(|e| Failure::new(e.status, e.fields()))) {
        Ok(()) => NW_OK,
        Err(e) => {
            // SAFETY: the caller lends an optional truthful-capacity diagnostic record; the shared writer checks its prefix.
            unsafe { write_error(e.status, error, e.fields) }
        }
    }
}
unsafe fn owner<'a, T>(ptr: *mut T) -> Result<&'a T> {
    if ptr.is_null() || !ptr.is_aligned() {
        Err(invalid())
    } else {
        // SAFETY: opaque owner provenance and a lease for this call are required by the public C ABI.
        Ok(unsafe { &*ptr })
    }
}
unsafe fn output<T>(out: *mut *mut T) -> Result<()> {
    if out.is_null() || !out.is_aligned() {
        return Err(invalid());
    }
    // SAFETY: caller supplies a writable, exclusive output pointer.
    unsafe { out.write(std::ptr::null_mut()) };
    Ok(())
}
unsafe fn capacity<T>(out: *mut T) -> Result<()> {
    if out.is_null() || !out.is_aligned() {
        return Err(invalid());
    }
    // SAFETY: ABI permits reading the initialized capacity prefix before the full output record.
    if unsafe { out.cast::<u32>().read() } < size_of::<T>() as u32 {
        Err(ApiError::status(NW_BUFFER_TOO_SMALL))
    } else {
        Ok(())
    }
}
unsafe fn release<T>(ptr: *mut T) {
    if !ptr.is_null() {
        // SAFETY: successful owner protocol transfers exactly one creator allocation to this release.
        drop(unsafe { Box::from_raw(ptr) });
    }
}
unsafe fn publish(out: *mut *mut NwServerOperation, operation: Arc<OpState>) {
    // SAFETY: output was initialized/validated before fallible work; ownership transfers only now.
    unsafe {
        out.write(Box::into_raw(Box::new(NwServerOperation {
            inner: operation,
        })))
    };
}
unsafe fn request(ptr: *mut NwServerRequest) -> Result<Arc<RequestState>> {
    // SAFETY: input is one live creator-owned request with a managed/native admission lease.
    let request = unsafe { owner(ptr)? }.inner.clone();
    request.business()?;
    Ok(request)
}

/// Exact Server ABI entry `nw_server_get_abi_v1`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_get_abi_v1(
    requested_major: u32,
    out_capacity: u32,
    out_info: *mut NwAbiInfoV1,
) -> u32 {
    if requested_major != 1 {
        return NW_NOT_SUPPORTED;
    }
    if out_info.is_null() || !out_info.is_aligned() {
        return NW_INVALID_ARGUMENT;
    }
    if out_capacity < size_of::<NwAbiInfoV1>() as u32 {
        return NW_BUFFER_TOO_SMALL;
    }
    // SAFETY: explicit capacity and alignment cover the fixed result; unknown caller tail is untouched.
    unsafe {
        out_info.write(NwAbiInfoV1 {
            struct_size: 32,
            abi_major: 1,
            abi_minor: 0,
            pointer_width_bits: 64,
            capabilities: 79,
            reserved: 0,
        })
    };
    NW_OK
}
/// Exact Server ABI entry `nw_server_create`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_create(
    config: *const NwServerConfigV1,
    dispatch: *const NwServerDispatchV1,
    out_server: *mut *mut NwServer,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: descriptor and output borrows end before this entry returns.
        unsafe { output(out_server)? };
        // SAFETY: The ABI caller retains this creator-owned handle and synchronous view/output borrows; the surrounding validation and owner protocol authorize this access.
        let dispatch = unsafe { config::record(dispatch)? };
        if dispatch.version != 1
            || dispatch.cookie == 0
            || dispatch.on_request == 0
            || dispatch.on_control == 0
        {
            return Err(invalid());
        }
        // SAFETY: configuration is synchronously validated and copied, creating no listener or worker.
        let config = unsafe { config::config(config)? };
        let host = Host::new(config, dispatch)?;
        // SAFETY: completed unique creator allocation transfers through the preinitialized output.
        unsafe { out_server.write(Box::into_raw(Box::new(NwServer { inner: host }))) };
        Ok(())
    })
}
/// Exact Server ABI entry `nw_server_routes_set`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_routes_set(
    server: *mut NwServer,
    routes: *const NwServerRouteV1,
    count: u32,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: caller holds the server owner and complete bounded route array for this call.
        let server = &unsafe { owner(server)? }.inner;
        if server.state.load(Ordering::Acquire) != 0
            || count > 16384
            || (count != 0 && (routes.is_null() || !routes.is_aligned()))
        {
            return Err(invalid());
        }
        let mut copy = Vec::with_capacity(count as usize);
        let mut keys = HashSet::new();
        let mut ids = HashSet::new();
        let mut native_paths = matchit::Router::new();
        let mut unique_paths = HashSet::new();
        for i in 0..count as usize {
            // SAFETY: i is inside the caller-provided bounded route array.
            let route = unsafe { config::route(routes.add(i))? };
            if !ids.insert(route.id) {
                return Err(invalid());
            }
            if 3u64 * route.max_json_bytes as u64
                > server
                    .config
                    .managed_payload_bytes
                    .min(server.config.request_retained_bytes)
            {
                return Err(invalid());
            }
            for method in &route.methods {
                if !keys.insert((route.canonical.clone(), method.clone())) {
                    return Err(invalid());
                }
            }
            if unique_paths.insert(route.canonical.clone()) {
                // Matchit is Axum's pinned public route parser. Validate before
                // freezing the registration table; conflicts never panic at start.
                native_paths
                    .insert(route.canonical.clone(), ())
                    .map_err(|_| invalid())?;
            }
            copy.push(route);
        }
        let mut current = lock(&server.routes);
        if current.is_some() {
            return Err(ApiError::status(NW_INVALID_STATE));
        }
        *current = Some(copy);
        Ok(())
    })
}
/// Exact Server ABI entry `nw_server_start_create`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_start_create(
    server: *mut NwServer,
    out_op: *mut *mut NwServerOperation,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: live owner/output are borrowed only during creation; no worker is started here.
        unsafe { output(out_op)? };
        // SAFETY: The ABI caller retains this creator-owned handle through the synchronous entry; the owner allocation and its typed contents remain live.
        let host = unsafe { owner(server)? }.inner.clone();
        if host.state.load(Ordering::Acquire) != 0 || lock(&host.routes).is_none() {
            return Err(ApiError::status(NW_INVALID_STATE));
        }
        let op = OpState::new(host, None, Job::Start)?;
        // SAFETY: successful creation transfers the never-started operation owner.
        unsafe { publish(out_op, op) };
        Ok(())
    })
}
/// Exact Server ABI entry `nw_server_bound_addresses`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_bound_addresses(
    server: *mut NwServer,
    out_addresses: *mut *mut NwServerMetadata,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: borrowed server and initialized owner output.
        unsafe { output(out_addresses)? };
        // SAFETY: The ABI caller retains this creator-owned handle through the synchronous entry; the owner allocation and its typed contents remain live.
        let host = &unsafe { owner(server)? }.inner;
        if host.state.load(Ordering::Acquire) < 2 {
            return Err(ApiError::status(NW_INVALID_STATE));
        }
        let addresses = lock(&host.addresses);
        let block = config::encode(addresses.iter().map(|(a, b)| (a.as_str(), b.as_str())));
        // SAFETY: unique immutable metadata owner transfers on success only.
        unsafe {
            out_addresses.write(Box::into_raw(Box::new(NwServerMetadata {
                inner: Metadata { head: None, block },
            })))
        };
        Ok(())
    })
}
/// Exact Server ABI entry `nw_server_get_snapshot`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_get_snapshot(
    server: *mut NwServer,
    out_snapshot: *mut NwServerSnapshotV1,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: capacity is checked before writing the known fixed prefix.
        unsafe { capacity(out_snapshot)? };
        // SAFETY: The caller leases the creator-owned handle and supplies live synchronous input/output storage; this entry preserves its typed owner contract.
        let host = &unsafe { owner(server)? }.inner;
        let (native, managed) = host.budget.snapshot();
        let retiring = host.retiring.load(Ordering::Acquire);
        let total = host.config.active_requests - host.active.available_permits();
        let value = NwServerSnapshotV1 {
            struct_size: size_of::<NwServerSnapshotV1>() as u32,
            version: 1,
            state: host.state.load(Ordering::Acquire),
            reserved: 0,
            active_requests: total.saturating_sub(retiring) as u64,
            queued_requests: (host.config.queued_requests - host.queued.available_permits()) as u64,
            retiring_requests: retiring as u64,
            retained_managed_bytes: managed,
            retained_native_bytes: native,
            rejected_requests: host.rejected.load(Ordering::Relaxed),
            callback_failures: host.callback_failures.load(Ordering::Relaxed),
            diagnostic_drops: 0,
        };
        // SAFETY: complete v1 output fits the caller's declared record; no tail write.
        unsafe { out_snapshot.write(value) };
        Ok(())
    })
}
/// Exact Server ABI entry `nw_server_close_create`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_close_create(
    server: *mut NwServer,
    wait_timeout_ns: u64,
    out_op: *mut *mut NwServerOperation,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: live server and borrowed output satisfy create-before-start protocol.
        unsafe { output(out_op)? };
        // SAFETY: The caller leases the creator-owned handle and supplies live synchronous input/output storage; this entry preserves its typed owner contract.
        let host = unsafe { owner(server)? }.inner.clone();
        let timeout = config::duration(wait_timeout_ns, true)?;
        let op = OpState::new(host, None, Job::Close(timeout))?;
        // SAFETY: successful unique operation transfer.
        unsafe { publish(out_op, op) };
        Ok(())
    })
}
/// Exact Server ABI entry `nw_server_tls_reload_create`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_tls_reload_create(
    server: *mut NwServer,
    listener_name: NwBytesViewV1,
    tls_snapshot: *const NwServerListenerV1,
    out_op: *mut *mut NwServerOperation,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: descriptors and file-name views are consumed before synchronous return.
        unsafe { output(out_op)? };
        // SAFETY: The caller leases the creator-owned handle and supplies live synchronous input/output storage; this entry preserves its typed owner contract.
        let host = unsafe { owner(server)? }.inner.clone();
        // SAFETY: The ABI caller retains this creator-owned handle and synchronous view/output borrows; the surrounding validation and owner protocol authorize this access.
        let name = unsafe { config::text(listener_name, 128)? };
        // SAFETY: The ABI caller retains this creator-owned handle and synchronous view/output borrows; the surrounding validation and owner protocol authorize this access.
        let snapshot = unsafe { config::record(tls_snapshot)? };
        if host.state.load(Ordering::Acquire) != 2 {
            return Err(ApiError::status(NW_INVALID_STATE));
        }
        let index = host
            .config
            .listeners
            .iter()
            .position(|l| l.name == name)
            .ok_or(invalid())?;
        let listener = &host.config.listeners[index];
        if listener
            .tls
            .read()
            .unwrap_or_else(|p| p.into_inner())
            .is_none()
            || snapshot.version != 1
            || snapshot.reserved != 0
            || snapshot.flags & 1 == 0
            || snapshot.flags & !3 != 0
            || snapshot.protocols != listener.protocols
        {
            return Err(invalid());
        }
        // SAFETY: the full replacement snapshot is validated before creating an operation or publishing configuration.
        let tls = unsafe { config::tls(&snapshot)? };
        let op = OpState::new(host, None, Job::Reload(index, tls))?;
        // SAFETY: successful operation transfer retains both its host and validated replacement.
        unsafe { publish(out_op, op) };
        Ok(())
    })
}
/// Exact Server ABI entry `nw_server_try_retire`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_try_retire(server: *mut NwServer) -> u32 {
    // SAFETY: caller retains the server through a cleanup lease until OK.
    let Ok(server) = (unsafe { owner(server) }) else {
        return NW_INVALID_ARGUMENT;
    };
    let host = &server.inner;
    if host.state.load(Ordering::Acquire) == 0 && host.live_ops.load(Ordering::Acquire) == 0 {
        return NW_OK;
    }
    let result = host.close_result();
    if result.close_status == 0 && host.live_ops.load(Ordering::Acquire) == 0 {
        NW_OK
    } else {
        NW_PENDING
    }
}
/// Exact Server ABI entry `nw_server_release`.
///
/// # Safety
/// The pointer must identify one live creator-owned allocation. Request, operation and Server handles require their matching successful retirement gate (or documented never-started path); release is exact-once and no later access is valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_release(server: *mut NwServer) {
    // SAFETY: release is valid only for unstarted or successfully retired owners, and never waits.
    if unsafe { nw_server_try_retire(server) } == NW_OK {
        // SAFETY: The matching native retirement/never-started predicate above permits exactly one creator release.
        unsafe { release(server) }
    }
}
/// Exact Server ABI entry `nw_server_operation_start`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased. The exact Core callback function and cookie remain rooted until operation_try_retire returns OK, including when completion races this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_operation_start(
    operation: *mut NwServerOperation,
    callbacks: *const NwOperationCallbacksV1,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: both operation owner and callback prefix are borrowed for setup; Core copies the table.
        let op = unsafe { owner(operation)? }.inner.clone();
        // SAFETY: The ABI caller retains this creator-owned handle and synchronous view/output borrows; the surrounding validation and owner protocol authorize this access.
        let callbacks = unsafe { config::record(callbacks)? };
        let status = op.operation.start(callbacks);
        if status != NW_OK {
            return Err(ApiError::status(status));
        }
        let job = lock(&op.job)
            .take()
            .ok_or(ApiError::status(NW_INVALID_STATE))?;
        if let Err(error) = op.execute(job) {
            op.complete(Err(error));
        }
        Ok(())
    })
}
/// Exact Server ABI entry `nw_server_operation_request_cancel`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_operation_request_cancel(
    operation: *mut NwServerOperation,
) -> u32 {
    // SAFETY: Core retains a cleanup lease around this synchronous cancellation request.
    let Ok(op) = (unsafe { owner(operation) }) else {
        return NW_INVALID_ARGUMENT;
    };
    op.inner.cancel.cancel();
    op.inner.operation.request_cancel()
}
/// Exact Server ABI entry `nw_server_operation_take_void`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_operation_take_void(
    operation: *mut NwServerOperation,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: typed take occurs once under the operation cleanup lease.
        // SAFETY: The ABI caller retains this creator-owned handle through the synchronous entry; the owner allocation and its typed contents remain live.
        let op = &unsafe { owner(operation)? }.inner;
        if op.result_kind != 0 {
            return Err(ApiError::status(NW_INVALID_STATE));
        }
        match op.operation.take_result().map_err(ApiError::status)? {
            OpResult::Void => Ok(()),
            _ => Err(ApiError::status(NW_CONTRACT_VIOLATION)),
        }
    })
}
/// Exact Server ABI entry `nw_server_operation_take_write_reservation`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_operation_take_write_reservation(
    operation: *mut NwServerOperation,
    out_reservation: *mut *mut NwServerWriteReservation,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: output is cleared before any fallible typed take and owns no previous value.
        unsafe { output(out_reservation)? };
        // SAFETY: The caller leases the creator-owned handle and supplies live synchronous input/output storage; this entry preserves its typed owner contract.
        let op = &unsafe { owner(operation)? }.inner;
        if op.result_kind != 3 {
            return Err(ApiError::status(NW_INVALID_STATE));
        }
        let mut wrapper = Box::new(NwServerWriteReservation { inner: None });
        match op.operation.take_result().map_err(ApiError::status)? {
            OpResult::Reservation(value) => wrapper.inner = Some(value),
            _ => return Err(ApiError::status(NW_CONTRACT_VIOLATION)),
        }
        // SAFETY: already prepared result owner transfers exactly once, after a successful move.
        unsafe { out_reservation.write(Box::into_raw(wrapper)) };
        Ok(())
    })
}
/// Exact Server ABI entry `nw_server_operation_take_close`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_operation_take_close(
    operation: *mut NwServerOperation,
    out_close: *mut NwServerCloseResultV1,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: validate full caller output capacity before consuming a typed result.
        unsafe { capacity(out_close)? };
        // SAFETY: The caller leases the creator-owned handle and supplies live synchronous input/output storage; this entry preserves its typed owner contract.
        let op = &unsafe { owner(operation)? }.inner;
        if op.result_kind != 1 {
            return Err(ApiError::status(NW_INVALID_STATE));
        }
        let OpResult::Close(result) = op.operation.take_result().map_err(ApiError::status)? else {
            return Err(ApiError::status(NW_CONTRACT_VIOLATION));
        };
        // SAFETY: exactly the known output prefix is written after the one successful take.
        unsafe { out_close.write(result) };
        Ok(())
    })
}
/// Exact Server ABI entry `nw_server_operation_try_retire`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_operation_try_retire(operation: *mut NwServerOperation) -> u32 {
    // SAFETY: operation remains leased through native quiescence; Core owns its callback state.
    match unsafe { owner(operation) } {
        Ok(op) => op.inner.operation.try_retire(),
        Err(e) => e.status,
    }
}
/// Exact Server ABI entry `nw_server_operation_release`.
///
/// # Safety
/// The pointer must identify one live creator-owned allocation. Request, operation and Server handles require their matching successful retirement gate (or documented never-started path); release is exact-once and no later access is valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_operation_release(operation: *mut NwServerOperation) {
    // SAFETY: only never-started or Core-retired operations may release the creating allocation.
    if let Ok(op) = unsafe { owner(operation) }
        && op.inner.operation.can_release()
    {
        // SAFETY: The matching native retirement/never-started predicate above permits exactly one creator release.
        unsafe { release(operation) }
    }
}
/// Exact Server ABI entry `nw_server_request_take`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_request_take(
    server: *mut NwServer,
    ticket: u64,
    out_request: *mut *mut NwServerRequest,
    out_metadata: *mut *mut NwServerMetadata,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: both outputs are live distinct pointer slots and are initialized before ticket lookup.
        unsafe {
            output(out_request)?;
            output(out_metadata)?
        };
        // SAFETY: The caller leases the creator-owned handle and supplies live synchronous input/output storage; this entry preserves its typed owner contract.
        let host = &unsafe { owner(server)? }.inner;
        let request = lock(&host.requests)
            .get(&ticket)
            .cloned()
            .ok_or(ApiError::status(NW_INVALID_STATE))?;
        let metadata = Box::new(NwServerMetadata {
            inner: Metadata {
                head: Some(request.head.clone()),
                block: Vec::new(),
            },
        });
        let wrapper = Box::new(NwServerRequest {
            inner: request.clone(),
        });
        if request
            .taken
            .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
            .is_err()
        {
            return Err(ApiError::status(NW_INVALID_STATE));
        }
        // SAFETY: prepared request and independent metadata allocations transfer together on success.
        unsafe {
            out_request.write(Box::into_raw(wrapper));
            out_metadata.write(Box::into_raw(metadata))
        };
        Ok(())
    })
}
/// Exact Server ABI entry `nw_server_metadata_read`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_metadata_read(
    metadata: *mut NwServerMetadata,
    out_info: *mut NwServerRequestInfoV1,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: output capacity is checked first; returned views remain owned by this metadata handle.
        unsafe { capacity(out_info)? };
        // SAFETY: The ABI caller retains this creator-owned handle and synchronous view/output borrows; the surrounding validation and owner protocol authorize this access.
        let head = unsafe { owner(metadata)? }
            .inner
            .head
            .as_ref()
            .ok_or(ApiError::status(NW_INVALID_STATE))?;
        // SAFETY: exact known fixed prefix fits the caller's output; backing views are immutable and retained.
        unsafe { out_info.write(head.wire()) };
        Ok(())
    })
}
/// Exact Server ABI entry `nw_server_dispatch_complete`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_dispatch_complete(
    request: *mut NwServerRequest,
    application_status: u32,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        if ![NW_OK, NW_CANCELED, NW_TIMEOUT, NW_DOMAIN_ERROR].contains(&application_status) {
            return Err(invalid());
        }
        // SAFETY: the live request's dispatch lease remains held until this exact-once completion.
        let request = unsafe { owner(request)? }.inner.clone();
        if !request.mark_dispatch_done() {
            return Err(ApiError::status(NW_INVALID_STATE));
        }
        request.budget.close();
        request.discard_input();
        if request.body_done.load(Ordering::Acquire) {
            request.finished.cancel();
        }
        if application_status != NW_OK {
            request.abort(application_status);
        }
        if let Ok(host) = request.host() {
            host.reap();
        }
        Ok(())
    })
}
/// Exact Server ABI entry `nw_server_request_abort`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_request_abort(request: *mut NwServerRequest) -> u32 {
    // SAFETY: synchronous request cancellation is retained by the caller's cleanup lease.
    match unsafe { owner(request) } {
        Ok(r) => {
            r.inner.abort(NW_CANCELED);
            NW_OK
        }
        Err(e) => e.status,
    }
}
/// Exact Server ABI entry `nw_server_request_try_retire`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_request_try_retire(request: *mut NwServerRequest) -> u32 {
    // SAFETY: the request and managed ticket stay rooted until this gate acknowledges callback quiescence.
    match unsafe { owner(request) } {
        Ok(r) => {
            let status = r.inner.try_retire();
            if let Ok(host) = r.inner.host() {
                host.reap();
            }
            status
        }
        Err(e) => e.status,
    }
}
/// Exact Server ABI entry `nw_server_metadata_release`.
///
/// # Safety
/// The pointer must identify one live creator-owned allocation. Request, operation and Server handles require their matching successful retirement gate (or documented never-started path); release is exact-once and no later access is valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_metadata_release(metadata: *mut NwServerMetadata) {
    // SAFETY: immutable metadata owns its independent storage; release is exact-once under its lease.
    unsafe { release(metadata) }
}
/// Exact Server ABI entry `nw_server_request_release`.
///
/// # Safety
/// The pointer must identify one live creator-owned allocation. Request, operation and Server handles require their matching successful retirement gate (or documented never-started path); release is exact-once and no later access is valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_request_release(request: *mut NwServerRequest) {
    // SAFETY: only a native-retired request can relinquish the creator-owned ABI allocation.
    if unsafe { nw_server_request_try_retire(request) } == NW_OK {
        // SAFETY: The matching native retirement/never-started predicate above permits exactly one creator release.
        unsafe { release(request) }
    }
}
/// Exact Server ABI entry `nw_server_managed_credit_acquire`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_managed_credit_acquire(
    request_ptr: *mut NwServerRequest,
    capacity: u64,
    out_credit: *mut *mut NwServerManagedCredit,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: request and owner output are caller-retained synchronous borrows.
        unsafe { output(out_credit)? };
        // SAFETY: The ABI caller retains this creator-owned handle through the synchronous entry; the owner allocation and its typed contents remain live.
        let request = unsafe { request(request_ptr)? };
        let credit = request.budget.try_acquire(Side::Managed, capacity)?;
        // SAFETY: admitted credit transfers only after successful all-or-none quota reservation.
        unsafe {
            out_credit.write(Box::into_raw(Box::new(NwServerManagedCredit {
                credit: Some(credit),
                host: request.host.clone(),
            })))
        };
        Ok(())
    })
}
/// Exact Server ABI entry `nw_server_managed_credit_release`.
///
/// # Safety
/// The pointer must identify one live creator-owned allocation. Request, operation and Server handles require their matching successful retirement gate (or documented never-started path); release is exact-once and no later access is valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_managed_credit_release(credit: *mut NwServerManagedCredit) {
    // SAFETY: the unique credit owner releases its full backing reservation exactly once.
    unsafe { release(credit) }
}
/// Exact Server ABI entry `nw_server_write_reserve_create`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_write_reserve_create(
    request_ptr: *mut NwServerRequest,
    kind: u32,
    native_capacity: u64,
    out_op: *mut *mut NwServerOperation,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: scalar metadata only is copied; no payload pointer is retained while queue admission waits.
        unsafe { output(out_op)? };
        // SAFETY: The caller leases the creator-owned handle and supplies live synchronous input/output storage; this entry preserves its typed owner contract.
        let request = unsafe { request(request_ptr)? };
        let host = request.host()?;
        if kind != 0 && kind != 2 {
            return Err(ApiError::status(NW_NOT_SUPPORTED));
        }
        let maximum = (host.config.max_chunk_bytes as u64)
            .checked_mul(if kind == 2 { 3 } else { 1 })
            .ok_or(invalid())?;
        if native_capacity == 0 || native_capacity > maximum {
            return Err(invalid());
        }
        if native_capacity > host.config.request_retained_bytes {
            return Err(ApiError::domain(1));
        }
        let response = lock(&request.response);
        if response.phase != 1 || (kind == 2 && !response.sse) {
            return Err(ApiError::status(NW_INVALID_STATE));
        }
        drop(response);
        let side = SideGuard::acquire(&request.write_active)?;
        let op = OpState::new(
            host,
            Some(request.clone()),
            Job::Reserve(request, kind, native_capacity as usize, side),
        )?;
        // SAFETY: successful operation transfer precedes all queue/payload work.
        unsafe { publish(out_op, op) };
        Ok(())
    })
}
/// Exact Server ABI entry `nw_server_write_reservation_release`.
///
/// # Safety
/// The pointer must identify one live creator-owned allocation. Request, operation and Server handles require their matching successful retirement gate (or documented never-started path); release is exact-once and no later access is valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_write_reservation_release(
    reservation: *mut NwServerWriteReservation,
) {
    // SAFETY: one unused reservation owns both queue and payload grants; Drop rolls both back without waiting.
    unsafe { release(reservation) }
}
/// Exact Server ABI entry `nw_server_body_read_create`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_body_read_create(
    request_ptr: *mut NwServerRequest,
    max_bytes: u32,
    out_op: *mut *mut NwServerOperation,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: request and scalar inputs remain valid for synchronous create.
        unsafe { output(out_op)? };
        // SAFETY: The caller leases the creator-owned handle and supplies live synchronous input/output storage; this entry preserves its typed owner contract.
        let request = unsafe { request(request_ptr)? };
        let host = request.host()?;
        if request.head.kind != 0 {
            return Err(ApiError::status(NW_INVALID_STATE));
        }
        if max_bytes == 0 || max_bytes as usize > host.config.max_chunk_bytes {
            return Err(invalid());
        }
        let side = SideGuard::acquire(&request.read_active)?;
        let op = OpState::new(
            host,
            Some(request.clone()),
            Job::Read(request, max_bytes as usize, side),
        )?;
        // SAFETY: never-started read owner transfers without polling network or granting payload bytes.
        unsafe { publish(out_op, op) };
        Ok(())
    })
}
/// Exact Server ABI entry `nw_server_operation_take_chunk`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_operation_take_chunk(
    operation: *mut NwServerOperation,
    out_frame_kind: *mut u32,
    out_buffer: *mut *mut NwServerBuffer,
    out_trailers: *mut *mut NwServerMetadata,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: every owner output clears before fallible work; the scalar requires independent live storage.
        unsafe {
            output(out_buffer)?;
            output(out_trailers)?
        };
        if out_frame_kind.is_null() || !out_frame_kind.is_aligned() {
            return Err(invalid());
        }
        // SAFETY: The caller leases the creator-owned handle and supplies live synchronous input/output storage; this entry preserves its typed owner contract.
        let op = &unsafe { owner(operation)? }.inner;
        if op.result_kind != 2 {
            return Err(ApiError::status(NW_INVALID_STATE));
        }
        let mut buffer = Box::new(NwServerBuffer {
            buffer: None,
            host: Arc::downgrade(&op.host),
        });
        let mut metadata = Box::new(NwServerMetadata {
            inner: Metadata {
                head: None,
                block: Vec::new(),
            },
        });
        let OpResult::Chunk(frame) = op.operation.take_result().map_err(ApiError::status)? else {
            return Err(ApiError::status(NW_CONTRACT_VIOLATION));
        };
        match frame {
            crate::engine::ChunkResult::Data(value) => {
                buffer.buffer = Some(value);
                // SAFETY: move prepared data wrapper and discriminator together after successful typed take.
                unsafe {
                    out_frame_kind.write(0);
                    out_buffer.write(Box::into_raw(buffer))
                };
            }
            crate::engine::ChunkResult::End(trailers) => {
                if let Some(trailers) = trailers {
                    metadata.inner.block = trailers;
                    // SAFETY: independently owned bounded trailers transfer only with this successful End observation.
                    unsafe { out_trailers.write(Box::into_raw(metadata)) };
                }
                // SAFETY: scalar output was validated before result transfer.
                unsafe { out_frame_kind.write(1) };
            }
        }
        Ok(())
    })
}
/// Exact Server ABI entry `nw_server_buffer_length`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_buffer_length(
    buffer: *mut NwServerBuffer,
    out_length: *mut u64,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        if out_length.is_null() || !out_length.is_aligned() {
            return Err(invalid());
        }
        // SAFETY: one leased immutable native buffer and writable scalar output.
        // SAFETY: The ABI caller retains this creator-owned handle through the synchronous entry; the owner allocation and its typed contents remain live.
        let buffer = unsafe { owner(buffer)? }
            .buffer
            .as_ref()
            .ok_or(ApiError::status(NW_INVALID_STATE))?;
        // SAFETY: The ABI caller retains this creator-owned handle and synchronous view/output borrows; the surrounding validation and owner protocol authorize this access.
        unsafe { out_length.write(buffer.len() as u64) };
        Ok(())
    })
}
/// Exact Server ABI entry `nw_server_buffer_copy`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_buffer_copy(
    buffer: *mut NwServerBuffer,
    destination: *mut u8,
    capacity: u64,
    out_written: *mut u64,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        if out_written.is_null() || !out_written.is_aligned() {
            return Err(invalid());
        }
        // SAFETY: scalar is caller-owned; initialize before capacity/provenance validation.
        unsafe { out_written.write(0) };
        // SAFETY: The caller leases the creator-owned handle and supplies live synchronous input/output storage; this entry preserves its typed owner contract.
        let buffer = unsafe { owner(buffer)? }
            .buffer
            .as_ref()
            .ok_or(ApiError::status(NW_INVALID_STATE))?;
        if capacity < buffer.len() as u64 {
            return Err(ApiError::status(NW_BUFFER_TOO_SMALL));
        }
        if destination.is_null() {
            return Err(invalid());
        }
        // SAFETY: caller provides exclusive nonoverlapping destination of declared size; immutable backing remains leased.
        unsafe {
            std::ptr::copy_nonoverlapping(buffer.as_slice().as_ptr(), destination, buffer.len());
            out_written.write(buffer.len() as u64)
        };
        Ok(())
    })
}
/// Exact Server ABI entry `nw_server_buffer_managed_credit_acquire`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_buffer_managed_credit_acquire(
    buffer: *mut NwServerBuffer,
    capacity: u64,
    out_credit: *mut *mut NwServerManagedCredit,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: retained buffer credit anchor permits bounded copy reservations after request retirement.
        unsafe { output(out_credit)? };
        // SAFETY: The ABI caller retains this creator-owned handle and synchronous view/output borrows; the surrounding validation and owner protocol authorize this access.
        let buffer = unsafe { owner(buffer)? };
        let amount = usize::try_from(capacity).map_err(|_| invalid())?;
        let credit = buffer
            .buffer
            .as_ref()
            .ok_or(ApiError::status(NW_INVALID_STATE))?
            .acquire_managed(amount)?;
        // SAFETY: successful complete quota grant transfers through the initialized owner output.
        unsafe {
            out_credit.write(Box::into_raw(Box::new(NwServerManagedCredit {
                credit: Some(credit),
                host: buffer.host.clone(),
            })))
        };
        Ok(())
    })
}
/// Exact Server ABI entry `nw_server_buffer_release`.
///
/// # Safety
/// The pointer must identify one live creator-owned allocation. Request, operation and Server handles require their matching successful retirement gate (or documented never-started path); release is exact-once and no later access is valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_buffer_release(buffer: *mut NwServerBuffer) {
    // SAFETY: this creator allocation owns its independently retained backing and credit until exact-once release.
    unsafe { release(buffer) }
}
/// Exact Server ABI entry `nw_server_response_begin_create`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_response_begin_create(
    request_ptr: *mut NwServerRequest,
    http_status: u32,
    headers: NwBytesViewV1,
    out_op: *mut *mut NwServerOperation,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: borrowed head bytes are bounded and copied before create returns.
        unsafe { output(out_op)? };
        // SAFETY: The caller leases the creator-owned handle and supplies live synchronous input/output storage; this entry preserves its typed owner contract.
        let request = unsafe { request(request_ptr)? };
        let host = request.host()?;
        if !(200..=599).contains(&http_status) {
            return Err(invalid());
        }
        let headers = config::headers(
            // SAFETY: The ABI caller retains this creator-owned handle and synchronous view/output borrows; the surrounding validation and owner protocol authorize this access.
            unsafe { config::bytes(headers, host.config.max_header_bytes)? },
            host.config.max_header_bytes,
            false,
        )?;
        if request.head.kind != 0 || lock(&request.response).phase != 0 {
            return Err(ApiError::status(NW_INVALID_STATE));
        }
        let side = SideGuard::acquire(&request.write_active)?;
        let op = OpState::new(
            host,
            Some(request.clone()),
            Job::Begin(request, http_status as u16, headers, side),
        )?;
        // SAFETY: head commit remains deferred until this owner is started successfully.
        unsafe { publish(out_op, op) };
        Ok(())
    })
}
/// Exact Server ABI entry `nw_server_response_write_create`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_response_write_create(
    request_ptr: *mut NwServerRequest,
    reservation: *mut NwServerWriteReservation,
    bytes: NwBytesViewV1,
    out_op: *mut *mut NwServerOperation,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: all aliases remain caller-owned until validation and operation-slot admission succeed.
        unsafe { output(out_op)? };
        // SAFETY: The caller leases the creator-owned handle and supplies live synchronous input/output storage; this entry preserves its typed owner contract.
        let request = unsafe { request(request_ptr)? };
        let host = request.host()?;
        // SAFETY: The ABI caller retains this creator-owned handle and synchronous view/output borrows; the surrounding validation and owner protocol authorize this access.
        let token = unsafe { owner(reservation)? }
            .inner
            .as_ref()
            .ok_or(ApiError::status(NW_INVALID_STATE))?;
        if !Arc::ptr_eq(&token.request, &request) || token.kind != 0 {
            return Err(ApiError::status(NW_INVALID_STATE));
        }
        // SAFETY: The ABI caller retains this creator-owned handle and synchronous view/output borrows; the surrounding validation and owner protocol authorize this access.
        let data = unsafe { config::bytes(bytes, host.config.max_chunk_bytes)? };
        if data.is_empty() || data.len() > token.reservation.capacity() {
            return Err(invalid());
        }
        {
            let response = lock(&request.response);
            if response.phase != 1 || matches!(response.status, 204 | 205 | 304) {
                return Err(ApiError::status(NW_INVALID_STATE));
            }
        }
        let slot = host
            .operation_slots
            .clone()
            .try_acquire_owned()
            .map_err(|_| ApiError::domain(1))?;
        // SAFETY: the unique token is consumed only after every recoverable rejection; the borrowed bytes are copied now.
        let mut wrapper = unsafe { Box::from_raw(reservation) };
        let token = wrapper.inner.take().expect("validated reservation");
        let prepared = token.reservation.prepare(data)?;
        let op = OpState::with_slot(
            host,
            Some(request.clone()),
            Job::Write(request, prepared, data.len(), token.side),
            slot,
        );
        // SAFETY: successful creation has consumed the token and retained an independent copied native allocation.
        unsafe { publish(out_op, op) };
        Ok(())
    })
}
/// Exact Server ABI entry `nw_server_response_flush_create`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_response_flush_create(
    request_ptr: *mut NwServerRequest,
    out_op: *mut *mut NwServerOperation,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: scalar-only operation creation retains the request owner, never managed payload pointers.
        unsafe { output(out_op)? };
        // SAFETY: The caller leases the creator-owned handle and supplies live synchronous input/output storage; this entry preserves its typed owner contract.
        let request = unsafe { request(request_ptr)? };
        let host = request.host()?;
        let side = SideGuard::acquire(&request.write_active)?;
        let op = OpState::new(host, Some(request.clone()), Job::Flush(request, side))?;
        // SAFETY: unique never-started operation ownership transfer.
        unsafe { publish(out_op, op) };
        Ok(())
    })
}
/// Exact Server ABI entry `nw_server_response_finish_create`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_response_finish_create(
    request_ptr: *mut NwServerRequest,
    trailers: NwBytesViewV1,
    out_op: *mut *mut NwServerOperation,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: trailers are fully validated/copied before return and no payload byte reservation is needed.
        unsafe { output(out_op)? };
        // SAFETY: The caller leases the creator-owned handle and supplies live synchronous input/output storage; this entry preserves its typed owner contract.
        let request = unsafe { request(request_ptr)? };
        let host = request.host()?;
        let trailers = config::headers(
            // SAFETY: The ABI caller retains this creator-owned handle and synchronous view/output borrows; the surrounding validation and owner protocol authorize this access.
            unsafe { config::bytes(trailers, host.config.max_header_bytes)? },
            host.config.max_header_bytes,
            true,
        )?;
        let side = SideGuard::acquire(&request.write_active)?;
        let op = OpState::new(
            host,
            Some(request.clone()),
            Job::Finish(request, trailers, side),
        )?;
        // SAFETY: completion work begins only after accepted operation_start.
        unsafe { publish(out_op, op) };
        Ok(())
    })
}
/// Exact Server ABI entry `nw_server_metadata_headers`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_metadata_headers(
    metadata: *mut NwServerMetadata,
    out_headers: *mut NwBytesViewV1,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        if out_headers.is_null() || !out_headers.is_aligned() {
            return Err(invalid());
        }
        // SAFETY: leased immutable metadata retains the exact block until owner release; view borrow ends with the lease.
        let metadata = &unsafe { owner(metadata)? }.inner;
        let block = metadata
            .head
            .as_ref()
            .map(|x| &x.headers)
            .unwrap_or(&metadata.block);
        // SAFETY: The ABI caller retains this creator-owned handle and synchronous view/output borrows; the surrounding validation and owner protocol authorize this access.
        unsafe { out_headers.write(view(block)) };
        Ok(())
    })
}

/// Exact Server ABI entry `nw_server_sse_write_create`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_sse_write_create(
    request_ptr: *mut NwServerRequest,
    reservation: *mut NwServerWriteReservation,
    event: *const NwServerSseEventV1,
    out_op: *mut *mut NwServerOperation,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        use axum::response::{IntoResponse, Sse, sse::Event};
        use http_body::Body;
        use std::{
            convert::Infallible,
            pin::Pin,
            task::{Context, Poll, Waker},
            time::Duration,
        };
        // SAFETY: bounded call borrows are validated completely before consuming
        // the caller's reservation or invoking a panic-capable Axum builder.
        // SAFETY: The caller leases the creator-owned handle and supplies live synchronous input/output storage; this entry preserves its typed owner contract.
        unsafe { output(out_op)? };
        // SAFETY: The ABI caller retains this creator-owned handle and synchronous view/output borrows; the surrounding validation and owner protocol authorize this access.
        let request = unsafe { request(request_ptr)? };
        let host = request.host()?;
        // SAFETY: The ABI caller retains this creator-owned handle and synchronous view/output borrows; the surrounding validation and owner protocol authorize this access.
        let event = unsafe { config::record(event)? };
        if event.version != 1
            || event.reserved != 0
            || event.present_flags & !7 != 0
            || event.retry_milliseconds > 86_400_000
            || (event.present_flags & 1 == 0 && event.event_name.length != 0)
            || (event.present_flags & 2 == 0 && event.id.length != 0)
            || (event.present_flags & 4 == 0 && event.retry_milliseconds != 0)
        {
            return Err(invalid());
        }
        // SAFETY: these UTF-8 views are borrowed only until this synchronous entry returns.
        let data =
            // SAFETY: The caller leases the creator-owned handle and supplies live synchronous input/output storage; this entry preserves its typed owner contract.
            std::str::from_utf8(unsafe { config::bytes(event.data, host.config.max_chunk_bytes)? })
                .map_err(|_| invalid())?;
        // SAFETY: same bounded synchronous metadata borrow; no native string copy is needed.
        let name = std::str::from_utf8(unsafe {
            config::bytes(event.event_name, host.config.max_chunk_bytes)?
        })
        .map_err(|_| invalid())?;
        // SAFETY: same bounded synchronous metadata borrow.
        let id =
            // SAFETY: The caller leases the creator-owned handle and supplies live synchronous input/output storage; this entry preserves its typed owner contract.
            std::str::from_utf8(unsafe { config::bytes(event.id, host.config.max_chunk_bytes)? })
                .map_err(|_| invalid())?;
        if name.contains(['\r', '\n']) || id.contains(['\r', '\n', '\0']) {
            return Err(invalid());
        }
        let policy = data
            .len()
            .checked_mul(6)
            .and_then(|n| n.checked_add(name.len() + id.len() + 128))
            .ok_or(invalid())?;
        // Axum 0.8.9 writes six prefix bytes after every CR/LF, plus bounded
        // event/id/retry fields. This also handles dense newline input where
        // the design's policy formula alone is not an allocation upper bound.
        let exact_upper = data
            .len()
            .checked_add(6 * (data.bytes().filter(|b| matches!(b, b'\r' | b'\n')).count() + 1))
            .and_then(|n| n.checked_add(name.len() + id.len() + 64))
            .ok_or(invalid())?;
        if policy > host.config.max_chunk_bytes || exact_upper > host.config.max_chunk_bytes {
            return Err(ApiError::domain(1));
        }
        let required = policy
            .max(exact_upper)
            .max(8)
            .checked_mul(3)
            .ok_or(invalid())?;
        // SAFETY: the reservation remains caller-owned until all validation and
        // operation metadata admission below succeed.
        // SAFETY: The caller leases the creator-owned handle and supplies live synchronous input/output storage; this entry preserves its typed owner contract.
        let token = unsafe { owner(reservation)? }
            .inner
            .as_ref()
            .ok_or(ApiError::status(NW_INVALID_STATE))?;
        if !Arc::ptr_eq(&token.request, &request) || token.kind != 2 {
            return Err(ApiError::status(NW_INVALID_STATE));
        }
        if token.reservation.capacity() < required {
            return Err(invalid());
        }
        {
            let response = lock(&request.response);
            if response.phase != 1 || !response.sse {
                return Err(ApiError::status(NW_INVALID_STATE));
            }
        }
        let slot = host
            .operation_slots
            .clone()
            .try_acquire_owned()
            .map_err(|_| ApiError::domain(1))?;
        // All allocation from this point is covered by the already-granted peak.
        let mut value = Event::default().data(data);
        if event.present_flags & 1 != 0 {
            value = value.event(name);
        }
        if event.present_flags & 2 != 0 {
            value = value.id(id);
        }
        if event.present_flags & 4 != 0 {
            value = value.retry(Duration::from_millis(event.retry_milliseconds));
        }
        let mut body = Sse::new(futures_util::stream::iter([Ok::<_, Infallible>(value)]))
            .into_response()
            .into_body();
        let frame = match Pin::new(&mut body).poll_frame(&mut Context::from_waker(Waker::noop())) {
            Poll::Ready(Some(Ok(frame))) => frame,
            _ => return Err(ApiError::status(NW_CONTRACT_VIOLATION)),
        };
        let bytes = frame
            .into_data()
            .map_err(|_| ApiError::status(NW_CONTRACT_VIOLATION))?;
        if bytes.len() > host.config.max_chunk_bytes {
            return Err(ApiError::status(NW_CONTRACT_VIOLATION));
        }
        let length = bytes.len();
        drop(body);
        // SAFETY: successful create alone consumes the unique request/kind-bound token.
        let mut wrapper = unsafe { Box::from_raw(reservation) };
        let token = wrapper.inner.take().expect("validated reservation");
        // SAFETY: pinned Axum emits one unaliased BytesMut allocation. Its
        // monotone Vec growth has old+new peak <=3*max(8,expanded bound), granted
        // before Event construction above; freeze/Body projection is zero-copy.
        // SAFETY: The caller leases the creator-owned handle and supplies live synchronous input/output storage; this entry preserves its typed owner contract.
        let prepared = unsafe { token.reservation.prepare_admitted(bytes)? };
        let operation = OpState::with_slot(
            host,
            Some(request.clone()),
            Job::Write(request, prepared, length, token.side),
            slot,
        );
        // SAFETY: the copied/formatted input is retained, but no network enqueue
        // occurs until this successfully created operation is started.
        // SAFETY: The caller leases the creator-owned handle and supplies live synchronous input/output storage; this entry preserves its typed owner contract.
        unsafe { publish(out_op, operation) };
        Ok(())
    })
}

/// Exact Server ABI entry `nw_server_grpc_read_create`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_grpc_read_create(
    _request: *mut NwServerRequest,
    out_op: *mut *mut NwServerOperation,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: unsupported optional protocols transfer no owner and always initialize the output.
        unsafe { output(out_op)? };
        Err(ApiError::status(NW_NOT_SUPPORTED))
    })
}

/// Exact Server ABI entry `nw_server_grpc_write_create`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_grpc_write_create(
    _request: *mut NwServerRequest,
    _reservation: *mut NwServerWriteReservation,
    _message: NwBytesViewV1,
    out_op: *mut *mut NwServerOperation,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: unsupported optional protocols transfer no owner and always initialize the output.
        unsafe { output(out_op)? };
        Err(ApiError::status(NW_NOT_SUPPORTED))
    })
}

/// Exact Server ABI entry `nw_server_grpc_headers_create`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_grpc_headers_create(
    _request: *mut NwServerRequest,
    _metadata: NwBytesViewV1,
    out_op: *mut *mut NwServerOperation,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: unsupported optional protocols transfer no owner and always initialize the output.
        unsafe { output(out_op)? };
        Err(ApiError::status(NW_NOT_SUPPORTED))
    })
}

/// Exact Server ABI entry `nw_server_grpc_status_create`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_grpc_status_create(
    _request: *mut NwServerRequest,
    _grpc_status: u32,
    _detail: NwBytesViewV1,
    _trailers: NwBytesViewV1,
    out_op: *mut *mut NwServerOperation,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: unsupported optional protocols transfer no owner and always initialize the output.
        unsafe { output(out_op)? };
        Err(ApiError::status(NW_NOT_SUPPORTED))
    })
}

/// Exact Server ABI entry `nw_server_mcp_gate_resume_create`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_mcp_gate_resume_create(
    _request: *mut NwServerRequest,
    _decision: u32,
    out_op: *mut *mut NwServerOperation,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: unsupported optional protocols transfer no owner and always initialize the output.
        unsafe { output(out_op)? };
        Err(ApiError::status(NW_NOT_SUPPORTED))
    })
}

/// Exact Server ABI entry `nw_server_mcp_reply_create`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_mcp_reply_create(
    _request: *mut NwServerRequest,
    _reservation: *mut NwServerWriteReservation,
    _reply_kind: u32,
    _result_json: NwBytesViewV1,
    out_op: *mut *mut NwServerOperation,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: unsupported optional protocols transfer no owner and always initialize the output.
        unsafe { output(out_op)? };
        Err(ApiError::status(NW_NOT_SUPPORTED))
    })
}

/// Exact Server ABI entry `nw_server_mcp_call_take`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_mcp_call_take(
    _request: *mut NwServerRequest,
    out_call_kind: *mut u32,
    out_arguments: *mut *mut NwServerBuffer,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: unsupported protocol output remains null; no argument owner is transferred.
        unsafe { output(out_arguments)? };
        if out_call_kind.is_null() || !out_call_kind.is_aligned() {
            return Err(invalid());
        }
        Err(ApiError::status(NW_NOT_SUPPORTED))
    })
}
/// Exact Server ABI entry `nw_server_metadata_grpc_headers`.
///
/// # Safety
/// Every input record/view must remain valid for the synchronous call; initialized output capacities must be truthful, naturally aligned, exclusive and nonoverlapping. Opaque handles must come from this creating module and remain leased.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_metadata_grpc_headers(
    _metadata: *mut NwServerMetadata,
    out_metadata: *mut NwBytesViewV1,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        if out_metadata.is_null() || !out_metadata.is_aligned() {
            return Err(invalid());
        }
        // SAFETY: unavailable optional decoded metadata is always a null/zero view.
        unsafe { out_metadata.write(view(&[])) };
        Err(ApiError::status(NW_NOT_SUPPORTED))
    })
}

include!("../generated/layout_assertions.rs");
