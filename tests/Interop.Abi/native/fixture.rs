//! Test-only engine entry point. Never included in a product native asset.
use nativeweave_interop_abi::{NwAbiInfoV1, NwStatus, write_abi_v1};

/// Test-only synchronous import target, exercising the actual product writer.
///
/// # Safety
/// Same output validity, nonoverlap and synchronous-borrow contract as write_abi_v1.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_abi_conformance_get_abi_v1(
    requested_major: u32,
    out_capacity: u32,
    out_info: *mut NwAbiInfoV1,
) -> NwStatus {
    // SAFETY: forwards the exact caller-owned pointer and bounds without retaining
    // them; write_abi_v1 checks null, requested major, capacity and alignment.
    unsafe {
        write_abi_v1(
            requested_major,
            out_capacity,
            out_info,
            0,
            0x8000_0000_0000_0001,
        )
    }
}
