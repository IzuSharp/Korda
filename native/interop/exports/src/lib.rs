//! A11 executable export-conformance component. The bootstrap calls the actual A01
//! implementation. Owned scalar resources and synchronous notifications are bounded
//! generator fixtures, not an A07 runtime, domain engine or published application API.
use nativeweave_interop_abi::*;
use std::sync::atomic::{AtomicUsize, Ordering};

static LIVE: AtomicUsize = AtomicUsize::new(0);
static RELEASED: AtomicUsize = AtomicUsize::new(0);

/// Opaque creator-owned scalar resource. Never exported by value or inspected in C#.
pub struct NwFixtureOwner {
    value: u64,
}

/// Calls the actual A01 bootstrap writer with this component's fixed capability.
/// # Safety
/// `output` follows A01's complete writable-range, alignment and nonaliasing rules.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_exports_get_abi_v1(
    major: u32,
    capacity: u32,
    output: *mut NwAbiInfoV1,
) -> u32 {
    // SAFETY: forwarded caller contract; A01 validates null, alignment and capacity.
    unsafe { write_abi_v1(major, capacity, output, 0, 1) }
}

/// Creates one fixture owner; zero values fail without transferring ownership.
/// # Safety
/// `output` is null or points to one naturally aligned writable pointer slot.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_exports_create_v1(value: u64, output: *mut *mut NwFixtureOwner) -> u32 {
    if output.is_null() || !output.is_aligned() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: the caller provides a valid unique output slot.
    unsafe { output.write(core::ptr::null_mut()) };
    if value == 0 {
        return NW_INVALID_ARGUMENT;
    }
    let owner = Box::into_raw(Box::new(NwFixtureOwner { value }));
    LIVE.fetch_add(1, Ordering::SeqCst);
    // SAFETY: successful creation transfers this sole Box ownership right.
    unsafe { output.write(owner) };
    NW_OK
}

/// Reads one admitted owner; value 13 is a deliberate ordinary domain failure.
/// # Safety
/// Non-null `owner` is live until return; `output` is one disjoint writable u64 slot.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_exports_read_v1(owner: *const NwFixtureOwner, output: *mut u64) -> u32 {
    if owner.is_null() || output.is_null() || !output.is_aligned() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: live, immutable owner and writable disjoint output are caller obligations.
    let value = unsafe { (*owner).value };
    if value == 13 {
        return NW_DOMAIN_ERROR;
    }
    // SAFETY: output validity was established by the caller and checked for alignment.
    unsafe { output.write(value) };
    NW_OK
}

/// Releases exactly the creator's Box ownership right. Null is a bounded no-op.
/// # Safety
/// A non-null owner was produced here and is not used by any admitted call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_exports_release_v1(owner: *mut NwFixtureOwner) {
    if !owner.is_null() {
        // SAFETY: sole creator/releaser pair; the caller proves all borrows ended.
        drop(unsafe { Box::from_raw(owner) });
        LIVE.fetch_sub(1, Ordering::SeqCst);
        RELEASED.fetch_add(1, Ordering::SeqCst);
    }
}

/// Reports live and released fixture owners for actual managed/native parity tests.
/// # Safety
/// Both output pointers are aligned, valid and nonoverlapping usize slots.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_exports_counters_v1(live: *mut usize, released: *mut usize) -> u32 {
    if live.is_null()
        || released.is_null()
        || !live.is_aligned()
        || !released.is_aligned()
        || live == released
    {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: caller guarantees two live disjoint output slots.
    unsafe {
        live.write(LIVE.load(Ordering::SeqCst));
        released.write(RELEASED.load(Ordering::SeqCst));
    }
    NW_OK
}

/// Fixed synchronous notification signature shared with the generated reverse entry.
/// # Safety
/// This primitive-only conformance export has no additional caller obligations.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_exports_notification_v1(_cookie: usize, status: u32) -> u32 {
    if is_known_status(status) {
        NW_OK
    } else {
        NW_INVALID_ARGUMENT
    }
}

/// Invokes a supplied exact C callback synchronously; no pointer or cookie is retained.
/// # Safety
/// Nonzero `callback` identifies a live exact `unsafe extern C fn(usize,u32)->u32`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_exports_invoke_v1(callback: usize, cookie: usize, status: u32) -> u32 {
    if callback == 0 {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: the explicit caller contract supplies the exact callback signature;
    // this test component invokes it only before returning to that same caller.
    let notify: unsafe extern "C" fn(usize, u32) -> u32 = unsafe { core::mem::transmute(callback) };
    // SAFETY: callback/cookie lifetime extends through this synchronous invocation.
    unsafe { notify(cookie, status) }
}

/// Fixed synchronous start: notification occurs before start returns, then the owner is read.
/// # Safety
/// Owner and callback remain live through return, with the exact callback signature above.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_exports_start_v1(
    owner: *mut NwFixtureOwner,
    callback: usize,
    cookie: usize,
    status: u32,
) -> u32 {
    if owner.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: the caller keeps the owner, callback and cookie alive through this call.
    let callback_status = unsafe { nw_exports_invoke_v1(callback, cookie, status) };
    // SAFETY: access after notification deliberately proves setup/owner lease retention.
    std::hint::black_box(unsafe { (*owner).value });
    callback_status
}

/// Bounded fixed-fixture close authority; there are no asynchronous callback sources.
/// # Safety
/// Non-null owner points to a live resource until this synchronous call returns.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_exports_close_v1(owner: *mut NwFixtureOwner) -> u32 {
    if owner.is_null() {
        NW_INVALID_ARGUMENT
    } else {
        NW_OK
    }
}

/// Bounded fixed-fixture drain authority; the caller already returned from invocation.
/// # Safety
/// The owner is live and the calling thread completed its synchronous invocation.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_exports_drain_v1(owner: *mut NwFixtureOwner) -> u32 {
    if owner.is_null() {
        NW_INVALID_ARGUMENT
    } else {
        NW_OK
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn native_owner_and_error_postconditions() {
        let mut owner = core::ptr::dangling_mut::<NwFixtureOwner>();
        // SAFETY: owner slot lives for every call; each successful creation is released once.
        unsafe {
            assert_eq!(nw_exports_create_v1(0, &mut owner), NW_INVALID_ARGUMENT);
            assert!(owner.is_null());
            assert_eq!(nw_exports_create_v1(37, &mut owner), NW_OK);
            let mut value = 0;
            assert_eq!(nw_exports_read_v1(owner, &mut value), NW_OK);
            assert_eq!(value, 37);
            nw_exports_release_v1(owner);
            assert_eq!(nw_exports_create_v1(13, &mut owner), NW_OK);
            value = 42;
            assert_eq!(nw_exports_read_v1(owner, &mut value), NW_DOMAIN_ERROR);
            assert_eq!(value, 42);
            nw_exports_release_v1(owner);
        }
        assert_eq!(LIVE.load(Ordering::SeqCst), 0);
        assert_eq!(RELEASED.load(Ordering::SeqCst), 2);
    }
}
