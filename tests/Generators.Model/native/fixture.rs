//! Narrow A09 conformance fixture; the ABI writer is the completed A01 implementation.
use nativeweave_interop_abi::{NwAbiInfoV1, write_abi_v1};

/// # Safety
/// The output pointer follows the A01 synchronous output contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_model_get_abi_v1(
    major: u32,
    capacity: u32,
    output: *mut NwAbiInfoV1,
) -> u32 {
    // SAFETY: the caller retains aligned writable storage for this synchronous call.
    unsafe { write_abi_v1(major, capacity, output, 0, 1) as u32 }
}

#[unsafe(no_mangle)]
pub extern "C" fn nw_model_validate_extent_v1(offset: u64, length: u64, capacity: u64) -> u32 {
    if offset <= capacity && length <= capacity - offset {
        0
    } else {
        3
    }
}
