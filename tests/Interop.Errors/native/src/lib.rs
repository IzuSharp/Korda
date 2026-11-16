//! Narrow ABI fixture for the actual A05 product boundary; no database or server host.
use nativeweave_interop_abi::*;
use nativeweave_interop_errors::{EngineBoundary, ErrorFields, Failure, create_owner, write_error};

/// Test-owned engine state. No runtime is created.
pub struct Fixture {
    boundary: EngineBoundary,
}

/// Creates one fixture owner.
/// # Safety
/// Output is a valid, exclusively writable pointer slot.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_errors_create(output: *mut *mut Fixture) -> NwStatus {
    let boundary = EngineBoundary::new();
    // SAFETY: forwards the same caller-owned output slot and its preconditions.
    unsafe {
        create_owner(&boundary, output, || {
            Ok(Box::new(Fixture {
                boundary: EngineBoundary::new(),
            }))
        })
    }
}

/// Exercises ordinary failure, caught panic and post-poison admission.
/// # Safety
/// Fixture is a live owner and error follows the D08 optional-record preconditions.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_errors_run(
    owner: *const Fixture,
    mode: u32,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    if owner.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: fixture pointer is live and shared immutably for this call.
    let owner = unsafe { &*owner };
    let result = owner.boundary.business(|| match mode {
        0 => Ok(()),
        1 => Err(Failure::new(
            NW_DOMAIN_ERROR,
            ErrorFields {
                domain: 7,
                code: -42,
                correlation_id: 73,
            },
        )),
        2 => panic!("fixture panic: static non-sensitive diagnostic"),
        _ => Err(Failure::new(NW_CONTRACT_VIOLATION, ErrorFields::default())),
    });
    match result {
        Ok(()) => NW_OK,
        // SAFETY: preserves optional error-buffer contract without touching unknown tails.
        Err(failure) => unsafe { write_error(failure.status, error, failure.fields) },
    }
}

/// Releases the sole fixture owner through trusted cleanup, even after poison.
/// # Safety
/// Owner is null or the unaliased owner returned by create, with no admitted call active.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_errors_release(owner: *mut Fixture) -> NwStatus {
    if owner.is_null() {
        return NW_OK;
    }
    // SAFETY: caller transfers the unique fixture back after all calls finish.
    let owner = unsafe { Box::from_raw(owner) };
    match owner.boundary.cleanup(|| Ok(())) {
        Ok(()) => NW_OK,
        Err(e) => e.status,
    }
}

/// Passes a fixed terminal outcome to a valid managed callback without rewriting it.
/// # Safety
/// Callback has exact C ABI and cookie lifetime; bridge points to writable u32.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_errors_notify(
    callback: Option<unsafe extern "C" fn(usize, u32) -> u32>,
    cookie: usize,
    original_terminal: u32,
    bridge: *mut u32,
) -> u32 {
    if bridge.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: valid out slot, initialized before fallible callback work.
    unsafe { bridge.write(NW_OK) };
    if let Some(callback) = callback {
        // SAFETY: caller provides a valid exactly typed callback and retained cookie.
        let status = unsafe { callback(cookie, original_terminal) };
        // SAFETY: the out slot stays live and exclusively borrowed for the call.
        unsafe { bridge.write(status) };
    }
    original_terminal
}

/// Invokes an intentionally invalid managed callback in a child-process negative test.
/// # Safety
/// Used only in a process whose fatal termination is expected; callback code is callable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_errors_invalid_uco(callback: unsafe extern "C" fn(u8) -> u32) -> u32 {
    // SAFETY: this isolated diagnostic intentionally invokes a runtime-rejected UCO signature.
    unsafe { callback(1) }
}
