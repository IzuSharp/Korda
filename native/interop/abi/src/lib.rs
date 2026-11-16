//! Fixed C ABI storage and bounded bootstrap writing, with no domain runtime.
//!
//! The generated declarations are the only shared wire type authority. Each engine
//! exports its own `nw_<engine>_get_abi_v1` entry point and calls [`write_abi_v1`]
//! with its actual compiled capability bits. This crate exports no native symbol.

mod generated;
pub use generated::*;

/// Writes a complete v1 bootstrap record into a caller-owned buffer.
///
/// Returns `INVALID_ARGUMENT` for null/misaligned output, `NOT_SUPPORTED` for a
/// non-v1 requested major, and `BUFFER_TOO_SMALL` for capacity below 32 bytes.
/// Failures write nothing. Success writes exactly 32 bytes; any larger caller
/// tail remains untouched. No pointer or owner is retained or transferred.
/// This function is bounded, reentrant and allocation-free; it starts no runtime
/// and invokes no callback. It contains no panic-producing operation.
///
/// # Safety
///
/// For a successful call, `out_info` must point to at least `out_capacity` bytes
/// of live writable, naturally aligned memory, not overlapping another active
/// input/output. A non-null address is not proof of valid memory. No concurrent
/// write to the output is permitted. The pointer is borrowed only until return.
pub unsafe fn write_abi_v1(
    requested_major: u32,
    out_capacity: u32,
    out_info: *mut NwAbiInfoV1,
    abi_minor: u32,
    capabilities: u64,
) -> NwStatus {
    if out_info.is_null() {
        return NW_INVALID_ARGUMENT;
    }
    if requested_major != 1 {
        return NW_NOT_SUPPORTED;
    }
    if out_capacity < size_of::<NwAbiInfoV1>() as u32 {
        return NW_BUFFER_TOO_SMALL;
    }
    if !out_info.is_aligned() {
        return NW_INVALID_ARGUMENT;
    }
    let value = NwAbiInfoV1 {
        struct_size: size_of::<NwAbiInfoV1>() as u32,
        abi_major: 1,
        abi_minor,
        pointer_width_bits: usize::BITS,
        capabilities,
        reserved: 0,
    };
    // SAFETY: caller guarantees a live, nonoverlapping writable range; the checks
    // above establish the complete record capacity and natural alignment.
    unsafe { out_info.write(value) };
    NW_OK
}

/// Accepts only the two canonical wire Boolean values, without Rust bool FFI.
pub const fn is_wire_boolean(value: u8) -> bool {
    value <= 1
}

/// Reports whether a scalar belongs to the v1 status inventory.
pub const fn is_known_status(value: NwStatus) -> bool {
    value <= NW_DOMAIN_ERROR
}

#[cfg(test)]
mod tests {
    use super::*;

    #[repr(C, align(8))]
    struct Aligned([u8; 64]);

    #[test]
    fn bootstrap_is_failure_atomic_and_preserves_the_tail() {
        for capacity in 0..32 {
            let mut output = Aligned([0xa5; 64]);
            // SAFETY: allocated aligned storage is live for the call.
            let status = unsafe { write_abi_v1(1, capacity, output.0.as_mut_ptr().cast(), 0, 0) };
            assert_eq!(status, NW_BUFFER_TOO_SMALL);
            assert_eq!(output.0, [0xa5; 64]);
        }
        for major in [0, 2, u32::MAX] {
            let mut output = Aligned([0xa5; 64]);
            // SAFETY: allocated aligned storage is live for the call.
            let status = unsafe { write_abi_v1(major, 64, output.0.as_mut_ptr().cast(), 0, 0) };
            assert_eq!(status, NW_NOT_SUPPORTED);
            assert_eq!(output.0, [0xa5; 64]);
        }
        assert_eq!(
            // SAFETY: a null pointer is an explicitly supported rejection case.
            unsafe { write_abi_v1(1, 32, core::ptr::null_mut(), 0, 0) },
            NW_INVALID_ARGUMENT
        );
        let mut output = Aligned([0xa5; 64]);
        // SAFETY: the pointer stays within valid allocated storage; it is rejected
        // for misalignment before dereference and before any write.
        let status = unsafe { write_abi_v1(1, 32, output.0.as_mut_ptr().add(1).cast(), 0, 0) };
        assert_eq!(status, NW_INVALID_ARGUMENT);
        assert_eq!(output.0, [0xa5; 64]);
        assert_eq!(
            // SAFETY: allocated aligned output has its declared writable capacity.
            unsafe {
                write_abi_v1(
                    1,
                    64,
                    output.0.as_mut_ptr().cast(),
                    3,
                    0x8000_0000_0000_0001,
                )
            },
            NW_OK
        );
        // SAFETY: a complete initialized ABI record was just written above.
        let record = unsafe { output.0.as_ptr().cast::<NwAbiInfoV1>().read() };
        assert_eq!(
            (
                record.struct_size,
                record.abi_major,
                record.abi_minor,
                record.pointer_width_bits
            ),
            (32, 1, 3, 64)
        );
        assert_eq!(record.capabilities, 0x8000_0000_0000_0001);
        assert_eq!(record.reserved, 0);
        assert_eq!(&output.0[32..], &[0xa5; 32]);
    }

    #[test]
    fn fixed_scalar_policies_reject_unknown_values() {
        let statuses = [
            NW_OK,
            NW_PENDING,
            NW_CANCELED,
            NW_INVALID_ARGUMENT,
            NW_INVALID_STATE,
            NW_OUT_OF_MEMORY,
            NW_NOT_SUPPORTED,
            NW_BUFFER_TOO_SMALL,
            NW_CALLBACK_FAILED,
            NW_NATIVE_PANIC,
            NW_ENGINE_POISONED,
            NW_CONTRACT_VIOLATION,
            NW_TIMEOUT,
            NW_DOMAIN_ERROR,
        ];
        for (index, value) in statuses.into_iter().enumerate() {
            assert_eq!(value, index as u32);
        }
        let error = NwErrorInfoV1 {
            struct_size: 24,
            domain: 0,
            code: 0,
            flags: 1,
            correlation_id: 0,
        };
        let operation = NwOperationCallbacksV1 {
            struct_size: 24,
            flags: 1,
            cookie: 0,
            on_terminal: None,
        };
        let callbacks = NwCallbacksV1 {
            struct_size: 24,
            flags: u32::MAX,
            cookie: 0,
            on_event: None,
        };
        assert!(!error.reserved_are_zero());
        assert!(!operation.reserved_are_zero());
        assert!(!callbacks.reserved_are_zero());
        for value in 0..=u8::MAX {
            assert_eq!(is_wire_boolean(value), value == 0 || value == 1);
        }
        for value in 0..14 {
            assert!(is_known_status(value));
        }
        for value in [14, 255, u32::MAX] {
            assert!(!is_known_status(value));
        }
        assert_eq!((NW_OK, NW_PENDING, NW_DOMAIN_ERROR), (0, 1, 13));
    }
}
