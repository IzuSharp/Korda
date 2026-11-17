//! Controlled old/new ABI fixtures linked to the real A01 ABI implementation.
//! They are not a shipped Engine or an upstream database compatibility claim.
use nativeweave_interop_abi::{NW_OK, NwAbiInfoV1, write_abi_v1};
use std::sync::atomic::{AtomicU32, Ordering};

static CALLS: AtomicU32 = AtomicU32::new(0);

#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_compat_get_abi_v1(major: u32, capacity: u32, out: *mut NwAbiInfoV1) -> u32 {
    let minor = if cfg!(new_native) { 1 } else { 0 };
    let capabilities = if cfg!(new_native) { 3 } else { 1 };
    // SAFETY: inherited bounded ABI caller contract; no pointer is retained.
    let status = unsafe { write_abi_v1(major, capacity, out, minor, capabilities) };
    if status == NW_OK {
        // Deliberate invalid records are confined to explicit negative fixtures.
        if cfg!(bad_major) { unsafe { (*out).abi_major = 2 }; }
        if cfg!(bad_layout) { unsafe { (*out).struct_size = 24 }; }
    }
    status
}

/// Direct Rust and C ABI calls share this bounded, allocation-free operation.
pub fn transform(value: u32) -> u32 {
    value.wrapping_add(if cfg!(bad_semantics) { 8 } else { 7 })
}

#[unsafe(no_mangle)]
pub extern "C" fn nw_compat_transform(value: u32) -> u32 {
    CALLS.fetch_add(1, Ordering::Relaxed);
    transform(value)
}

#[unsafe(no_mangle)]
#[cfg(new_native)]
pub extern "C" fn nw_compat_optional(value: u32) -> u32 {
    CALLS.fetch_add(1, Ordering::Relaxed);
    value.wrapping_mul(2)
}

#[unsafe(no_mangle)]
pub extern "C" fn nw_compat_calls() -> u32 { CALLS.load(Ordering::Relaxed) }

#[test]
fn direct_rust_oracle() {
    assert_eq!(transform(35), 42);
    assert_eq!(transform(u32::MAX), 6);
}
