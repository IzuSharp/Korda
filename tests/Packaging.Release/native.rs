//! Local packaging conformance module. Reuses the actual product bootstrap writer.
use nativeweave_interop_abi::{NwAbiInfoV1, NwStatus, write_abi_v1};

/// # Safety
/// The output must satisfy the exact product write_abi_v1 pointer/extent contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_packaging_get_abi_v1(
    requested_major: u32,
    out_capacity: u32,
    out_info: *mut NwAbiInfoV1,
) -> NwStatus {
    // SAFETY: forward the caller-owned output without retaining it. The product
    // writer checks requested version, null, capacity and alignment.
    let status = unsafe { write_abi_v1(requested_major, out_capacity, out_info, 0, 1) };
    #[cfg(wrong_abi)]
    if status == 0 {
        // SAFETY: a successful product writer established a valid complete output.
        // This deliberately incompatible package is isolated to a negative test.
        unsafe { (*out_info).abi_major = 2 };
    }
    status
}
