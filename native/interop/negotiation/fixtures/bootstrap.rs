//! Stateless A02 conformance fixtures, not distributable domain engines.
//! Each fixture consumes A01's actual wire types and failure-atomic writer.
use nativeweave_interop_abi::{NW_OK, NwAbiInfoV1, NwStatus, write_abi_v1};
use std::sync::atomic::{AtomicU32, Ordering};

static BOOTSTRAP_CALLS: AtomicU32 = AtomicU32::new(0);

unsafe fn bootstrap(major: u32, capacity: u32, output: *mut NwAbiInfoV1) -> NwStatus {
    BOOTSTRAP_CALLS.fetch_add(1, Ordering::Relaxed);
    let minor = if cfg!(minor_zero) { 0 } else { 3 };
    // The top bit is intentionally unknown to the package's named capability map.
    let capabilities = if cfg!(fixture_beta) {
        2
    } else {
        (1u64 << 63) | 1
    };
    // SAFETY: this forwards the caller's exact A01 writable-output preconditions.
    let status = unsafe { write_abi_v1(major, capacity, output, minor, capabilities) };
    if status != NW_OK {
        return status;
    }
    // These deliberately malformed, valid-memory fixtures test managed preflight;
    // they never modify bytes outside the complete initialized A01 prefix.
    // SAFETY: write_abi_v1 returned OK for this live, aligned complete output.
    unsafe {
        if cfg!(bad_size) {
            (*output).struct_size = 24;
        }
        if cfg!(future_size) {
            (*output).struct_size = 48;
        }
        if cfg!(bad_major) {
            (*output).abi_major = 2;
        }
        if cfg!(bad_pointer) {
            (*output).pointer_width_bits = 32;
        }
        if cfg!(bad_reserved) {
            (*output).reserved = 1;
        }
    }
    if cfg!(bad_pending) {
        return 1;
    }
    if cfg!(bad_unknown) {
        return u32::MAX;
    }
    status
}

/// Alpha engine-specific bootstrap; no runtime, instance, owner or worker exists.
///
/// # Safety
/// The output follows the exact A01 bootstrap writable-buffer contract.
#[cfg(not(fixture_beta))]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_a02_alpha_get_abi_v1(
    major: u32,
    capacity: u32,
    output: *mut NwAbiInfoV1,
) -> NwStatus {
    // SAFETY: caller provides the A01 buffer contract, forwarded unchanged.
    unsafe { bootstrap(major, capacity, output) }
}

/// Beta has a different bootstrap symbol and independent capability meaning.
///
/// # Safety
/// The output follows the exact A01 bootstrap writable-buffer contract.
#[cfg(fixture_beta)]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_a02_beta_get_abi_v1(
    major: u32,
    capacity: u32,
    output: *mut NwAbiInfoV1,
) -> NwStatus {
    // SAFETY: caller provides the A01 buffer contract, forwarded unchanged.
    unsafe { bootstrap(major, capacity, output) }
}

/// Counts actual alpha bootstrap calls without allocating or starting work.
#[cfg(not(fixture_beta))]
#[unsafe(no_mangle)]
pub extern "C" fn nw_a02_alpha_bootstrap_calls() -> u32 {
    BOOTSTRAP_CALLS.load(Ordering::Relaxed)
}

/// A bounded synchronous operation proves the fixed-import resolver's identity.
#[cfg(not(fixture_beta))]
#[unsafe(no_mangle)]
pub extern "C" fn nw_a02_alpha_probe(input: u64) -> u64 {
    input ^ 0xa102
}

/// A different operation result detects accidental cross-module symbol selection.
#[cfg(fixture_beta)]
#[unsafe(no_mangle)]
pub extern "C" fn nw_a02_beta_probe(input: u64) -> u64 {
    input ^ 0xb203
}
