//! D04 conformance owners: opaque Box identities and one native Arc parent lease.
//!
//! These exports exercise the real managed admission core; they are not a
//! generic engine ABI or a global production runtime. Destructors run no user
//! code, never wait, and only the creating module releases an owner.
use nativeweave_interop_abi::{NW_INVALID_ARGUMENT, NW_OK, NW_OUT_OF_MEMORY, NwStatus};
use nativeweave_interop_errors::{EngineBoundary, ErrorFields, Failure, create_owner};
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};

static LIVE: AtomicU64 = AtomicU64::new(0);
static RELEASES: AtomicU64 = AtomicU64::new(0);
static STATES: AtomicU64 = AtomicU64::new(0);
static STATE_RELEASES: AtomicU64 = AtomicU64::new(0);
static NEXT_ID: AtomicU64 = AtomicU64::new(1);
static CALLS: AtomicU64 = AtomicU64::new(0);
static BOUNDARY: EngineBoundary = EngineBoundary::new();

struct ParentState {
    value: u64,
}
impl Drop for ParentState {
    fn drop(&mut self) {
        STATES.fetch_sub(1, Ordering::SeqCst);
        STATE_RELEASES.fetch_add(1, Ordering::SeqCst);
    }
}
/// Opaque fixture identity. Its private allocation and Arc layout never cross the ABI.
pub struct Owner {
    state: Arc<ParentState>,
    id: u64,
}
impl Drop for Owner {
    fn drop(&mut self) {
        LIVE.fetch_sub(1, Ordering::SeqCst);
        RELEASES.fetch_add(1, Ordering::SeqCst);
    }
}
fn owner(state: Arc<ParentState>) -> Box<Owner> {
    let result = Box::new(Owner {
        state,
        id: NEXT_ID.fetch_add(1, Ordering::SeqCst),
    });
    LIVE.fetch_add(1, Ordering::SeqCst);
    result
}

/// Creates one independent parent owner, clearing output before publication.
/// # Safety
/// `output` is one live, aligned, exclusively writable pointer slot.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_lifetime_fixture_create_v1(
    value: u64,
    output: *mut *mut Owner,
) -> NwStatus {
    // SAFETY: caller supplies the declared output slot; shared helper owns failure publication.
    unsafe {
        create_owner(&BOUNDARY, output, || {
            let state = Arc::new(ParentState { value });
            STATES.fetch_add(1, Ordering::SeqCst);
            Ok(owner(state))
        })
    }
}

/// Retains exactly one native parent state; a controlled failure drops that retain.
/// # Safety
/// `parent` has an admitted managed lease; `output` is writable and disjoint.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_lifetime_fixture_child_v1(
    parent: *const Owner,
    fail: u32,
    output: *mut *mut Owner,
) -> NwStatus {
    if parent.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: parent stays alive under the caller's admitted lease.
    let state = unsafe { &*parent }.state.clone();
    // SAFETY: output follows create_owner's slot contract; failure retains no child.
    unsafe {
        create_owner(&BOUNDARY, output, || {
            if fail != 0 {
                return Err(Failure::new(NW_OUT_OF_MEMORY, ErrorFields::default()));
            }
            Ok(owner(state))
        })
    }
}

/// Reads immutable state while the caller holds an admitted owner lease.
/// # Safety
/// `owner` is a live owner and both outputs are aligned writable slots.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_lifetime_fixture_read_v1(
    owner: *const Owner,
    value: *mut u64,
    id: *mut u64,
) -> NwStatus {
    if owner.is_null() || value.is_null() || id.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: valid leased owner and exclusive output slots are the caller's contract.
    unsafe {
        let borrowed = &*owner;
        value.write(borrowed.state.value);
        id.write(borrowed.id);
    }
    CALLS.fetch_add(1, Ordering::SeqCst);
    NW_OK
}

/// Returns ownership exactly once to this module; no waits or callback paths exist.
/// # Safety
/// A non-null owner was created here and all borrowed uses have ended.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_lifetime_fixture_release_v1(owner: *mut Owner) {
    if !owner.is_null() {
        // SAFETY: ownership was transferred by this module and is returned once.
        drop(unsafe { Box::from_raw(owner) });
    }
}

/// Reads cumulative counters; no resource pointer is needed.
#[unsafe(no_mangle)]
pub extern "C" fn nw_lifetime_fixture_counter_v1(kind: u32) -> u64 {
    match kind {
        0 => LIVE.load(Ordering::SeqCst),
        1 => RELEASES.load(Ordering::SeqCst),
        2 => STATES.load(Ordering::SeqCst),
        3 => STATE_RELEASES.load(Ordering::SeqCst),
        4 => CALLS.load(Ordering::SeqCst),
        _ => u64::MAX,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn child_retains_real_parent_state() {
        let mut parent = std::ptr::null_mut();
        let mut child = std::ptr::null_mut();
        // SAFETY: this test exclusively owns all slots and releases each owner exactly once.
        unsafe {
            assert_eq!(nw_lifetime_fixture_create_v1(42, &mut parent), NW_OK);
            assert_eq!(nw_lifetime_fixture_child_v1(parent, 0, &mut child), NW_OK);
            nw_lifetime_fixture_release_v1(parent);
            assert_eq!(nw_lifetime_fixture_counter_v1(2), 1);
            let mut value = 0;
            let mut id = 0;
            assert_eq!(
                nw_lifetime_fixture_read_v1(child, &mut value, &mut id),
                NW_OK
            );
            assert_eq!(value, 42);
            nw_lifetime_fixture_release_v1(child);
            assert_eq!(nw_lifetime_fixture_counter_v1(0), 0);
            assert_eq!(nw_lifetime_fixture_counter_v1(2), 0);
        }
    }
}
