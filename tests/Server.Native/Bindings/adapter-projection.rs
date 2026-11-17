// Generated exact source projection of A09-supported primitive/pointer forms.
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

#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_release(server: *mut NwServer) {
    // SAFETY: release is valid only for unstarted or successfully retired owners, and never waits.
    if unsafe { nw_server_try_retire(server) } == NW_OK {
        // SAFETY: The matching native retirement/never-started predicate above permits exactly one creator release.
        unsafe { release(server) }
    }
}

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

#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_operation_try_retire(operation: *mut NwServerOperation) -> u32 {
    // SAFETY: operation remains leased through native quiescence; Core owns its callback state.
    match unsafe { owner(operation) } {
        Ok(op) => op.inner.operation.try_retire(),
        Err(e) => e.status,
    }
}

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

#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_metadata_release(metadata: *mut NwServerMetadata) {
    // SAFETY: immutable metadata owns its independent storage; release is exact-once under its lease.
    unsafe { release(metadata) }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_request_release(request: *mut NwServerRequest) {
    // SAFETY: only a native-retired request can relinquish the creator-owned ABI allocation.
    if unsafe { nw_server_request_try_retire(request) } == NW_OK {
        // SAFETY: The matching native retirement/never-started predicate above permits exactly one creator release.
        unsafe { release(request) }
    }
}

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

#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_managed_credit_release(credit: *mut NwServerManagedCredit) {
    // SAFETY: the unique credit owner releases its full backing reservation exactly once.
    unsafe { release(credit) }
}

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

#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_write_reservation_release(
    reservation: *mut NwServerWriteReservation,
) {
    // SAFETY: one unused reservation owns both queue and payload grants; Drop rolls both back without waiting.
    unsafe { release(reservation) }
}

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

#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_server_buffer_release(buffer: *mut NwServerBuffer) {
    // SAFETY: this creator allocation owns its independently retained backing and credit until exact-once release.
    unsafe { release(buffer) }
}

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
