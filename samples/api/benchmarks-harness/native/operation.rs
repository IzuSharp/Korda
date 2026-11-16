//! A21 scalar overhead fixture. Not a Database/ORM/Server engine.

pub fn mix(mut value: u64) -> u64 {
    for _ in 0..8 {
        value = (value ^ (value >> 27)).wrapping_mul(0x3c79_ac49_2ba7_b653);
    }
    value
}

/// Writes the same deterministic scalar as the direct-native target.
///
/// # Safety
/// A non-null aligned output must reference writable caller-owned u64 storage
/// for this synchronous call. No pointer is retained and no owner is returned.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_benchmark_mix_v1(value: u64, output: *mut u64) -> u32 {
    if output.is_null() || !(output as usize).is_multiple_of(align_of::<u64>()) {
        return 1;
    }
    // SAFETY: the documented caller contract supplies writable aligned storage.
    unsafe { output.write(mix(value)) };
    0
}
