//! D07 contiguous extent validation and real Vec/Arc buffer conformance owners.
//!
//! The opaque owner retains allocator identity, backing capacity and one budget
//! charge. Slices share the backing charge until its final Arc is dropped. No
//! global allocator, executor, native-backed managed Memory or strided profile.
use nativeweave_interop_abi::{NW_INVALID_ARGUMENT, NW_OK, NW_OUT_OF_MEMORY, NwStatus};
use nativeweave_interop_errors::{EngineBoundary, ErrorFields, Failure, create_owner};
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};

/// Validates a contiguous numeric descriptor without constructing references.
#[allow(clippy::too_many_arguments)]
pub fn checked_extent(
    address: u64,
    length: u64,
    count: u64,
    width: u64,
    offset: u64,
    capacity: u64,
    limit: u64,
    alignment: u32,
) -> Option<usize> {
    if width == 0
        || !alignment.is_power_of_two()
        || length > isize::MAX as u64
        || length > limit
        || address > usize::MAX as u64
        || capacity > usize::MAX as u64
        || count.checked_mul(width)? != length
        || offset.checked_add(length)? > capacity
    {
        return None;
    }
    let start = address.checked_add(offset)?;
    let end = start.checked_add(length)?;
    if end > usize::MAX as u64
        || start % u64::from(alignment) != 0
        || (address == 0 && (length != 0 || offset != 0))
    {
        return None;
    }
    usize::try_from(start).ok()
}

static OWNERS: AtomicU64 = AtomicU64::new(0);
static RELEASES: AtomicU64 = AtomicU64::new(0);
static BACKINGS: AtomicU64 = AtomicU64::new(0);
static BACKING_RELEASES: AtomicU64 = AtomicU64::new(0);
static CHARGED: AtomicU64 = AtomicU64::new(0);
static BOUNDARY: EngineBoundary = EngineBoundary::new();
const MAX_BYTES: usize = 8 * 1024 * 1024;
struct Backing {
    bytes: Vec<u8>,
}
impl Drop for Backing {
    fn drop(&mut self) {
        CHARGED.fetch_sub(self.bytes.capacity() as u64, Ordering::SeqCst);
        BACKINGS.fetch_sub(1, Ordering::SeqCst);
        BACKING_RELEASES.fetch_add(1, Ordering::SeqCst);
    }
}
/// Opaque fixture owner; capacity/allocator private layout never crosses the ABI.
pub struct Buffer {
    backing: Arc<Backing>,
    offset: usize,
    length: usize,
}
impl Drop for Buffer {
    fn drop(&mut self) {
        OWNERS.fetch_sub(1, Ordering::SeqCst);
        RELEASES.fetch_add(1, Ordering::SeqCst);
    }
}
fn owned(backing: Arc<Backing>, offset: usize, length: usize) -> Box<Buffer> {
    let owner = Box::new(Buffer {
        backing,
        offset,
        length,
    });
    OWNERS.fetch_add(1, Ordering::SeqCst);
    owner
}
fn failure(status: NwStatus) -> Failure {
    Failure::new(status, ErrorFields::default())
}

/// Copies a bounded borrowed input into this module's immutable owned Vec.
/// # Safety
/// Input is readable for length bytes until return, output is one exclusive slot.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_memory_fixture_copy_v1(
    data: *const u8,
    length: u64,
    reserve: u64,
    fail: u32,
    output: *mut *mut Buffer,
) -> NwStatus {
    // SAFETY: caller gives a writable output slot; helper publishes only on success.
    unsafe {
        create_owner(&BOUNDARY, output, || {
            if checked_extent(
                data as u64,
                length,
                length,
                1,
                0,
                length,
                MAX_BYTES as u64,
                1,
            )
            .is_none()
            {
                return Err(failure(NW_INVALID_ARGUMENT));
            }
            let size = usize::try_from(length).map_err(|_| failure(NW_INVALID_ARGUMENT))?;
            let capacity = length
                .checked_add(reserve)
                .filter(|n| *n <= MAX_BYTES as u64)
                .ok_or_else(|| failure(NW_INVALID_ARGUMENT))? as usize;
            let mut bytes = Vec::new();
            // The injected reserve failure executes the actual fallible Vec path;
            // no enormous allocation is attempted because usize::MAX is invalid.
            let requested = if fail == 1 { usize::MAX } else { capacity };
            bytes
                .try_reserve_exact(requested)
                .map_err(|_| failure(NW_OUT_OF_MEMORY))?;
            let borrowed = if size == 0 {
                &[]
            } else {
                // SAFETY: checked extent plus the caller's address-validity contract.
                std::slice::from_raw_parts(data, size)
            };
            bytes.extend_from_slice(borrowed);
            let charge = bytes.capacity();
            let backing = Arc::new(Backing { bytes });
            BACKINGS.fetch_add(1, Ordering::SeqCst);
            CHARGED.fetch_add(charge as u64, Ordering::SeqCst);
            let owner = owned(backing, 0, size);
            if fail == 2 {
                return Err(failure(NW_OUT_OF_MEMORY));
            } // local guard drops unpublished output
            Ok(owner)
        })
    }
}

/// Creates an independently owned view retaining the complete backing capacity.
/// # Safety
/// Source is leased; output is a writable disjoint pointer slot.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_memory_fixture_slice_v1(
    source: *const Buffer,
    offset: u64,
    length: u64,
    output: *mut *mut Buffer,
) -> NwStatus {
    // SAFETY: create_owner owns output initialization and one-time publication.
    unsafe {
        create_owner(&BOUNDARY, output, || {
            if source.is_null() {
                return Err(failure(NW_INVALID_ARGUMENT));
            }
            // SAFETY: caller holds the source's physical lease across this operation.
            let source = &*source;
            let end = offset
                .checked_add(length)
                .filter(|n| *n <= source.length as u64)
                .ok_or_else(|| failure(NW_INVALID_ARGUMENT))?;
            let _ = end;
            Ok(owned(
                source.backing.clone(),
                source.offset + offset as usize,
                length as usize,
            ))
        })
    }
}

/// Produces a view under an existing owner lease; it transfers no allocation.
/// # Safety
/// Owner is live; address/length/capacity outputs are exclusive aligned slots.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_memory_fixture_view_v1(
    owner: *const Buffer,
    address: *mut *const u8,
    length: *mut u64,
    capacity: *mut u64,
) -> NwStatus {
    if owner.is_null() || address.is_null() || length.is_null() || capacity.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: caller supplies live leased owner and valid outputs. Offsets were checked at creation.
    unsafe {
        let owner = &*owner;
        address.write(if owner.length == 0 {
            std::ptr::null()
        } else {
            owner.backing.bytes.as_ptr().add(owner.offset)
        });
        length.write(owner.length as u64);
        capacity.write(owner.backing.bytes.capacity() as u64);
    }
    NW_OK
}

/// Releases only owners created by this module; last backing release returns its charge.
/// # Safety
/// Non-null owner is returned exactly once after every admitted borrower finishes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_memory_fixture_release_v1(owner: *mut Buffer) {
    if !owner.is_null() {
        // SAFETY: pointer came from this module's Box and ownership returns once.
        drop(unsafe { Box::from_raw(owner) });
    }
}
/// Reports cumulative owner, backing and physical-capacity counters.
#[unsafe(no_mangle)]
pub extern "C" fn nw_memory_fixture_counter_v1(kind: u32) -> u64 {
    match kind {
        0 => OWNERS.load(Ordering::SeqCst),
        1 => RELEASES.load(Ordering::SeqCst),
        2 => BACKINGS.load(Ordering::SeqCst),
        3 => BACKING_RELEASES.load(Ordering::SeqCst),
        4 => CHARGED.load(Ordering::SeqCst),
        _ => u64::MAX,
    }
}
/// Runs the independent native numeric validator without dereferencing addresses.
/// # Safety
/// `start` points to one exclusive writable u64 slot.
#[allow(clippy::too_many_arguments)]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_memory_fixture_validate_v1(
    address: u64,
    length: u64,
    count: u64,
    width: u64,
    offset: u64,
    capacity: u64,
    limit: u64,
    alignment: u32,
    start: *mut u64,
) -> NwStatus {
    if start.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: caller owns the declared output slot; no input address is dereferenced.
    unsafe {
        start.write(0);
    }
    match checked_extent(
        address, length, count, width, offset, capacity, limit, alignment,
    ) {
        Some(value) => {
            // SAFETY: same exclusive output slot receives a validated numeric start.
            unsafe {
                start.write(value as u64);
            }
            NW_OK
        }
        None => NW_INVALID_ARGUMENT,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn complete_extent() {
        assert_eq!(checked_extent(0, 0, 0, 1, 0, 0, 0, 1), Some(0));
        assert_eq!(checked_extent(0, 1, 1, 1, 0, 1, 1, 1), None);
        assert_eq!(checked_extent(8, 12, 3, 4, 0, 12, 12, 4), Some(8));
        assert_eq!(checked_extent(8, 12, 3, 4, u64::MAX, 12, 12, 4), None);
        assert_eq!(
            checked_extent(8, u64::MAX, u64::MAX, 2, 0, u64::MAX, u64::MAX, 1),
            None
        );
    }
    #[test]
    fn backing_charge_survives_slice() {
        let mut original = std::ptr::null_mut();
        let mut slice = std::ptr::null_mut();
        // SAFETY: input array stays alive, slots are exclusive, owners released once.
        unsafe {
            assert_eq!(
                nw_memory_fixture_copy_v1([1, 2, 3, 4].as_ptr(), 4, 1020, 0, &mut original),
                NW_OK
            );
            assert_eq!(
                nw_memory_fixture_slice_v1(original, 2, 1, &mut slice),
                NW_OK
            );
            nw_memory_fixture_release_v1(original);
            assert_eq!(nw_memory_fixture_counter_v1(4), 1024);
            nw_memory_fixture_release_v1(slice);
            assert_eq!(nw_memory_fixture_counter_v1(4), 0);
        }
    }
}
