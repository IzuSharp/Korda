//! B18 implementations behind the original 48 Server ABI entries.
use super::*;
use crate::engine::grpc as host_grpc;
use nativeweave_protocols_grpc::{
    ResponseMetadata,
    tonic::{Code, Status},
};

pub unsafe fn reserve(
    request: Arc<RequestState>,
    capacity: u64,
    out_op: *mut *mut NwServerOperation,
) -> Result<()> {
    let host = request.host()?;
    let call = host_grpc::state(&request)?;
    if capacity == 0 || capacity > call.descriptor.max_encoded_message_bytes as u64 {
        return Err(invalid());
    }
    if call.is_terminal() {
        return Err(ApiError::status(NW_INVALID_STATE));
    }
    let side = SideGuard::acquire(&request.write_active)?;
    let op = OpState::new(
        host,
        Some(request.clone()),
        Job::GrpcReserve(request, capacity as usize, side),
    )?;
    // SAFETY: caller initialized and validated this output before choosing the protocol branch.
    unsafe { publish(out_op, op) };
    Ok(())
}
pub unsafe fn read(
    request_ptr: *mut NwServerRequest,
    out_op: *mut *mut NwServerOperation,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: fixed ABI input/output borrows remain live for this synchronous create.
        unsafe { output(out_op)? };
        // SAFETY: the caller leases this creator-owned request through the complete synchronous entry.
        let request = unsafe { request(request_ptr)? };
        host_grpc::state(&request)?;
        let side = SideGuard::acquire(&request.read_active)?;
        let op = OpState::new(
            request.host()?,
            Some(request.clone()),
            Job::GrpcRead(request, side),
        )?;
        // SAFETY: publish one never-started typed operation after all validation.
        unsafe { publish(out_op, op) };
        Ok(())
    })
}
pub unsafe fn write(
    request_ptr: *mut NwServerRequest,
    token_ptr: *mut NwServerWriteReservation,
    message: NwBytesViewV1,
    out_op: *mut *mut NwServerOperation,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: caller leases the request, reservation and live bounded input until create returns.
        unsafe { output(out_op)? };
        // SAFETY: the caller leases this creator-owned request through the complete synchronous entry.
        let request = unsafe { request(request_ptr)? };
        let host = request.host()?;
        let call = host_grpc::state(&request)?;
        // SAFETY: the live creator reservation is borrowed for validation; transfer is deferred until every fallible check succeeds.
        let token = unsafe { owner(token_ptr)? }
            .inner
            .as_ref()
            .ok_or(ApiError::status(NW_INVALID_STATE))?;
        if token.kind != 1 || !Arc::ptr_eq(&token.request, &request) || call.is_terminal() {
            return Err(ApiError::status(NW_INVALID_STATE));
        }
        // SAFETY: bounded message view is copied only after reservation and operation-slot validation.
        let data =
            unsafe { config::bytes(message, call.descriptor.max_encoded_message_bytes as usize)? };
        if data.len() > token.reservation.capacity() {
            return Err(invalid());
        }
        let slot = host
            .operation_slots
            .clone()
            .try_acquire_owned()
            .map_err(|_| ApiError::domain(1))?;
        // SAFETY: every recoverable rejection precedes transfer; unique creator token moves exactly once.
        let mut wrapper = unsafe { Box::from_raw(token_ptr) };
        let token = wrapper.inner.take().expect("validated grpc token");
        let prepared = token.reservation.into_grpc().prepare(data);
        let op = OpState::with_slot(
            host,
            Some(request.clone()),
            Job::GrpcWrite(request, prepared, token.side),
            slot,
        );
        // SAFETY: copied payload, metadata permit and byte grant are now owned by this never-started operation.
        unsafe { publish(out_op, op) };
        Ok(())
    })
}
pub unsafe fn headers(
    request_ptr: *mut NwServerRequest,
    metadata: NwBytesViewV1,
    out_op: *mut *mut NwServerOperation,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: caller lends the exact request and bounded immutable metadata block synchronously.
        unsafe { output(out_op)? };
        // SAFETY: the caller leases this creator-owned request through the complete synchronous entry.
        let request = unsafe { request(request_ptr)? };
        let host = request.host()?;
        host_grpc::state(&request)?;
        let maximum = host.config.max_header_bytes.min(32_768);
        // SAFETY: length is checked before constructing a borrowed input slice.
        let data = unsafe { config::bytes(metadata, maximum)? };
        let metadata = ResponseMetadata::parse(data, maximum, &request.budget, false, 0)
            .map_err(host_grpc::error)?;
        let side = SideGuard::acquire(&request.write_active)?;
        let op = OpState::new(
            host,
            Some(request.clone()),
            Job::GrpcHeaders(request, metadata, side),
        )?;
        // SAFETY: no header commit occurs until start; unstarted release rolls the credit back.
        unsafe { publish(out_op, op) };
        Ok(())
    })
}
pub unsafe fn status(
    request_ptr: *mut NwServerRequest,
    code: u32,
    detail: NwBytesViewV1,
    trailers: NwBytesViewV1,
    out_op: *mut *mut NwServerOperation,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        // SAFETY: fixed scalar and immutable synchronous views use the existing Core create protocol.
        unsafe { output(out_op)? };
        // SAFETY: the caller leases this creator-owned request through the complete synchronous entry.
        let request = unsafe { request(request_ptr)? };
        let host = request.host()?;
        let call = host_grpc::state(&request)?;
        if code > 16 {
            return Err(invalid());
        }
        if call.is_terminal() {
            return Err(ApiError::status(NW_INVALID_STATE));
        }
        let maximum = host.config.max_header_bytes.min(32_768);
        // SAFETY: neither input view is retained, and both finite limits are checked before copies.
        let data = unsafe { config::bytes(trailers, maximum)? };
        // SAFETY: inspect bounded borrowed detail length before the complete metadata/detail pregrant.
        let detail_view = unsafe { config::bytes(detail, 4096)? };
        let metadata =
            ResponseMetadata::parse(data, maximum, &request.budget, true, detail_view.len())
                .map_err(host_grpc::error)?;
        // The native metadata pregrant also covers this bounded detail and Status copies.
        // SAFETY: admitted finite UTF-8 text is copied; protocol Status performs its own percent encoding.
        let detail = std::str::from_utf8(detail_view)
            .map_err(|_| invalid())?
            .to_owned();
        let status =
            Status::with_metadata(Code::from_i32(code as i32), detail, metadata.values.clone());
        let side = SideGuard::acquire(&request.write_active)?;
        let op = OpState::new(
            host,
            Some(request.clone()),
            Job::GrpcStatus(request, status, metadata, side),
        )?;
        // SAFETY: terminal output and request/scope retirement remain separate.
        unsafe { publish(out_op, op) };
        Ok(())
    })
}
pub unsafe fn metadata(
    metadata: *mut NwServerMetadata,
    out_view: *mut NwBytesViewV1,
    error: *mut NwErrorInfoV1,
) -> u32 {
    ffi(error, || {
        if out_view.is_null() || !out_view.is_aligned() {
            return Err(invalid());
        }
        // SAFETY: writable output always clears before owner-kind validation.
        unsafe { out_view.write(view(&[])) };
        // SAFETY: caller retains the independent creator-owned immutable metadata handle.
        let owner = unsafe { owner(metadata)? };
        let head = owner
            .inner
            .head
            .as_ref()
            .ok_or(ApiError::status(NW_INVALID_STATE))?;
        let block = head
            .grpc
            .as_ref()
            .filter(|_| head.kind == 1)
            .ok_or(ApiError::status(NW_INVALID_STATE))?;
        // SAFETY: view borrows this same immutable metadata owner, never managed storage or a dropped request.
        unsafe { out_view.write(view(&block.decoded)) };
        Ok(())
    })
}
