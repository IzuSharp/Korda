//! A20 maintainer-only entry points into the actual A01/A04/A05 implementation.
//! No alternative allocator, status mapping, runtime or ownership implementation.
use nativeweave_interop_abi::*;
use nativeweave_interop_errors::{BoundedDetail, ErrorFields, write_error};
use std::mem::{align_of, offset_of, size_of};

/// Complete sizes, alignments and field offsets for the five v1 records.
#[unsafe(no_mangle)]
pub extern "C" fn nw_a20_layout_v1(index: u32) -> u64 {
    let values = [
        size_of::<NwAbiInfoV1>(),
        align_of::<NwAbiInfoV1>(),
        offset_of!(NwAbiInfoV1, struct_size),
        offset_of!(NwAbiInfoV1, abi_major),
        offset_of!(NwAbiInfoV1, abi_minor),
        offset_of!(NwAbiInfoV1, pointer_width_bits),
        offset_of!(NwAbiInfoV1, capabilities),
        offset_of!(NwAbiInfoV1, reserved),
        size_of::<NwErrorInfoV1>(),
        align_of::<NwErrorInfoV1>(),
        offset_of!(NwErrorInfoV1, struct_size),
        offset_of!(NwErrorInfoV1, domain),
        offset_of!(NwErrorInfoV1, code),
        offset_of!(NwErrorInfoV1, flags),
        offset_of!(NwErrorInfoV1, correlation_id),
        size_of::<NwBytesViewV1>(),
        align_of::<NwBytesViewV1>(),
        offset_of!(NwBytesViewV1, data),
        offset_of!(NwBytesViewV1, length),
        size_of::<NwOperationCallbacksV1>(),
        align_of::<NwOperationCallbacksV1>(),
        offset_of!(NwOperationCallbacksV1, struct_size),
        offset_of!(NwOperationCallbacksV1, flags),
        offset_of!(NwOperationCallbacksV1, cookie),
        offset_of!(NwOperationCallbacksV1, on_terminal),
        size_of::<NwCallbacksV1>(),
        align_of::<NwCallbacksV1>(),
        offset_of!(NwCallbacksV1, struct_size),
        offset_of!(NwCallbacksV1, flags),
        offset_of!(NwCallbacksV1, cookie),
        offset_of!(NwCallbacksV1, on_event),
    ];
    values.get(index as usize).map_or(u64::MAX, |v| *v as u64)
}

/// Only numeric arithmetic is performed; no supplied address is dereferenced.
/// # Safety
/// `start` is an exclusive writable u64 slot.
#[allow(clippy::too_many_arguments)]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_a20_extent_v1(
    address: u64,
    length: u64,
    count: u64,
    width: u64,
    offset: u64,
    capacity: u64,
    limit: u64,
    alignment: u32,
    start: *mut u64,
) -> u32 {
    // SAFETY: forwarding the exact scalar arguments and exclusive output contract.
    unsafe {
        nativeweave_interop_memory::nw_memory_fixture_validate_v1(
            address, length, count, width, offset, capacity, limit, alignment, start,
        )
    }
}

/// Uses actual D08 UTF-8 truncation; invalid encodings leave the output untouched.
/// # Safety
/// Input is valid readable memory for length, output has capacity writable bytes,
/// and `written` is an exclusive u64 slot. These regions do not overlap.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_a20_utf8_v1(
    input: *const u8,
    length: u64,
    limit: u32,
    output: *mut u8,
    capacity: u32,
    written: *mut u64,
) -> u32 {
    if written.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: caller owns the independent writable result slot.
    unsafe {
        written.write(0);
    }
    if length > 4096 || (length != 0 && input.is_null()) {
        return NW_INVALID_ARGUMENT;
    }
    let bytes = if length == 0 {
        &[]
    } else {
        // SAFETY: bounded length and caller's valid-memory borrowing contract.
        unsafe { std::slice::from_raw_parts(input, length as usize) }
    };
    let Ok(text) = std::str::from_utf8(bytes) else {
        return NW_INVALID_ARGUMENT;
    };
    let detail = BoundedDetail::from_redacted(text, limit as usize);
    if detail.bytes().len() > capacity as usize || (!detail.bytes().is_empty() && output.is_null())
    {
        return NW_BUFFER_TOO_SMALL;
    }
    if !detail.bytes().is_empty() {
        // SAFETY: capacity and disjointness guarantee the exact bounded copy.
        unsafe {
            std::ptr::copy_nonoverlapping(detail.bytes().as_ptr(), output, detail.bytes().len());
        }
    }
    // SAFETY: same exclusive result slot; every publication follows validation.
    unsafe {
        written.write(detail.bytes().len() as u64);
    }
    NW_OK
}

/// Preserves the original status while exercising the actual optional error writer.
/// # Safety
/// `output` obeys write_error's truthful capacity/alignment/disjointness contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_a20_error_v1(
    status: u32,
    output: *mut NwErrorInfoV1,
    domain: u32,
    code: i32,
    correlation: u64,
) -> u32 {
    // SAFETY: the caller provides the exact fixed-record borrowing contract.
    unsafe {
        write_error(
            status,
            output,
            ErrorFields {
                domain,
                code,
                correlation_id: correlation,
            },
        )
    }
}

/// Invokes an explicitly supplied terminal table for isolated stale-cookie controls.
/// # Safety
/// This is deliberately a trusted maintainer seam: caller proves the cookie live,
/// except in an isolated documented negative child where process failure is expected.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_a20_notify_v1(table: *const NwOperationCallbacksV1) -> u32 {
    if table.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: caller provides a valid table and the stated cookie-lifetime contract.
    unsafe {
        match (*table).on_terminal {
            Some(callback) => callback((*table).cookie, NW_OK, std::ptr::null()),
            None => NW_INVALID_ARGUMENT,
        }
    }
}
