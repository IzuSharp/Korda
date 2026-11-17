//! Real synchronous zlib proof engine over pinned miniz_oxide. No executor,
//! callbacks, timers or domain runtime are linked or started by this adapter.
//! All result storage belongs to this module until its unique release call.
use miniz_oxide::deflate::core::{
    CompressorOxide, TDEFLFlush, TDEFLStatus, compress, create_comp_flags_from_zip_params,
};
use miniz_oxide::inflate::TINFLStatus;
use miniz_oxide::inflate::core::{DecompressorOxide, decompress, inflate_flags};
use nativeweave_interop_abi::*;
use nativeweave_interop_errors::{EngineBoundary, ErrorFields, Failure, write_error};
use std::sync::atomic::{AtomicU64, Ordering};

const MAX_PROFILE_BYTES: u64 = 16 * 1024 * 1024;
const ERROR_DOMAIN: u32 = 0x5359_4e43;
const MALFORMED: i32 = 1;
const RAW_LIMIT: i32 = 2;
const ENCODED_LIMIT: i32 = 3;
const DECODED_LIMIT: i32 = 4;
const COMPRESSED_OUTPUT_LIMIT: i32 = 5;
static BOUNDARY: EngineBoundary = EngineBoundary::new();
static LIVE: AtomicU64 = AtomicU64::new(0);
static RELEASED: AtomicU64 = AtomicU64::new(0);
static CAPACITY: AtomicU64 = AtomicU64::new(0);

/// Fixed immutable profile. Input compression is also bounded by decoded_limit,
/// so every successful encoded result can be decoded using the same profile.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct NwCodecLimitsV1 {
    pub struct_size: u32,
    pub flags: u32,
    pub raw_limit: u64,
    pub encoded_limit: u64,
    pub decoded_limit: u64,
}

/// Opaque creating-allocator owner. Its private Vec layout never crosses the ABI.
pub struct NwCodecOwner {
    bytes: Vec<u8>,
}
impl Drop for NwCodecOwner {
    fn drop(&mut self) {
        CAPACITY.fetch_sub(self.bytes.capacity() as u64, Ordering::SeqCst);
        LIVE.fetch_sub(1, Ordering::SeqCst);
        RELEASED.fetch_add(1, Ordering::SeqCst);
    }
}

fn failure(status: u32, code: i32) -> Failure {
    Failure::new(
        status,
        ErrorFields {
            domain: ERROR_DOMAIN,
            code,
            correlation_id: 0,
        },
    )
}

fn buffer(limit: usize) -> Result<Vec<u8>, Failure> {
    let mut result = Vec::new();
    result
        .try_reserve_exact(limit)
        .map_err(|_| failure(NW_OUT_OF_MEMORY, 0))?;
    result.resize(limit, 0);
    Ok(result)
}

fn grow(output: &mut Vec<u8>, limit: usize) -> Result<(), Failure> {
    let next = output.len().saturating_mul(2).max(8).min(limit);
    if next <= output.len() {
        return Err(failure(NW_DOMAIN_ERROR, COMPRESSED_OUTPUT_LIMIT));
    }
    output
        .try_reserve_exact(next - output.len())
        .map_err(|_| failure(NW_OUT_OF_MEMORY, 0))?;
    output.resize(next, 0);
    Ok(())
}

fn encode(input: &[u8], limit: usize) -> Result<Vec<u8>, Failure> {
    let mut output = buffer((input.len() / 2).max(8).min(limit))?;
    // The upstream fixed Huffman workspace is infallible. The bounded Vec growth
    // below follows its public compressor loop, with fallible output reservations.
    let mut state = CompressorOxide::new(create_comp_flags_from_zip_params(6, 15, 0));
    let (mut read, mut written) = (0, 0);
    loop {
        let (status, used, produced) = compress(
            &mut state,
            &input[read..],
            &mut output[written..],
            TDEFLFlush::Finish,
        );
        if used > input.len() - read || produced > output.len() - written {
            return Err(failure(NW_CONTRACT_VIOLATION, 0));
        }
        read += used;
        written += produced;
        match status {
            TDEFLStatus::Done if read == input.len() => {
                output.truncate(written);
                return Ok(output);
            }
            TDEFLStatus::Okay => {
                // The cap is the decoder's own encoded-input cap. Exhaustion drops
                // all partial bytes before an owner is published.
                if output.len() - written < 30 || used == 0 && produced == 0 {
                    grow(&mut output, limit)?;
                }
            }
            _ => return Err(failure(NW_CONTRACT_VIOLATION, status as i32)),
        }
    }
}

fn decode(input: &[u8], limit: usize) -> Result<Vec<u8>, Failure> {
    let mut output = buffer(input.len().saturating_mul(2).min(limit))?;
    let mut state = DecompressorOxide::new();
    let flags = inflate_flags::TINFL_FLAG_PARSE_ZLIB_HEADER
        | inflate_flags::TINFL_FLAG_USING_NON_WRAPPING_OUTPUT_BUF;
    let (mut read, mut written) = (0, 0);
    loop {
        let (status, used, produced) =
            decompress(&mut state, &input[read..], &mut output, written, flags);
        if used > input.len() - read || produced > output.len() - written {
            return Err(failure(NW_CONTRACT_VIOLATION, 0));
        }
        read += used;
        written += produced;
        match status {
            TINFLStatus::Done if read == input.len() => {
                output.truncate(written);
                return Ok(output);
            }
            TINFLStatus::HasMoreOutput => {
                if output.len() == limit {
                    return Err(failure(NW_DOMAIN_ERROR, DECODED_LIMIT));
                }
                grow(&mut output, limit)?;
            }
            _ => return Err(failure(NW_DOMAIN_ERROR, MALFORMED)),
        }
    }
}

fn run(input: &[u8], mode: u32, limits: NwCodecLimitsV1) -> Result<Vec<u8>, Failure> {
    match mode {
        0 if input.len() as u64 <= limits.raw_limit => encode(input, limits.encoded_limit as usize),
        0 => Err(failure(NW_DOMAIN_ERROR, RAW_LIMIT)),
        1 if input.len() as u64 <= limits.encoded_limit => {
            decode(input, limits.decoded_limit as usize)
        }
        1 => Err(failure(NW_DOMAIN_ERROR, ENCODED_LIMIT)),
        _ => Err(failure(NW_INVALID_ARGUMENT, 0)),
    }
}

/// Uses the shared A01 bootstrap, advertising only synchronous zlib capability.
/// # Safety
/// Output follows A01's truthful capacity, alignment and exclusive-write contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_syncproof_get_abi_v1(
    major: u32,
    capacity: u32,
    output: *mut NwAbiInfoV1,
) -> u32 {
    // SAFETY: caller forwards the shared A01 writable-output contract.
    unsafe { write_abi_v1(major, capacity, output, 0, 1) }
}

/// Runs one bounded real miniz_oxide call, publishing no owner or partial bytes on failure.
/// # Safety
/// Input is readable for length bytes until return; null is allowed only for zero.
/// All output slots are aligned, valid, mutually disjoint and disjoint from input.
/// Limits is a readable aligned fixed record; error follows D08's truthful capacity contract.
/// Caller releases a successful owner exactly once after all synchronous reads end.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_syncproof_run_v1(
    input: *const u8,
    length: u64,
    mode: u32,
    limits: *const NwCodecLimitsV1,
    owner: *mut *mut NwCodecOwner,
    data: *mut *const u8,
    output_length: *mut u64,
    error: *mut NwErrorInfoV1,
) -> u32 {
    if owner.is_null()
        || data.is_null()
        || output_length.is_null()
        || !owner.is_aligned()
        || !data.is_aligned()
        || !output_length.is_aligned()
    {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: the caller supplies three live disjoint output slots.
    unsafe {
        owner.write(std::ptr::null_mut());
        data.write(std::ptr::null());
        output_length.write(0);
    }
    let result = BOUNDARY.business(|| {
        if limits.is_null()
            || !limits.is_aligned()
            || length > MAX_PROFILE_BYTES
            || (input.is_null() && length != 0)
            || (input as usize).checked_add(length as usize).is_none()
        {
            return Err(failure(NW_INVALID_ARGUMENT, 0));
        }
        // SAFETY: the pointer addresses one readable aligned fixed record.
        let limits = unsafe { limits.read() };
        if limits.struct_size != 32
            || limits.flags != 0
            || limits.raw_limit > limits.decoded_limit
            || limits.raw_limit > MAX_PROFILE_BYTES
            || limits.encoded_limit > MAX_PROFILE_BYTES
            || limits.decoded_limit > MAX_PROFILE_BYTES
        {
            return Err(failure(NW_INVALID_ARGUMENT, 0));
        }
        let bytes = if length == 0 {
            &[]
        } else {
            // SAFETY: checked finite numeric extent plus caller's readable-range obligation.
            unsafe { std::slice::from_raw_parts(input, length as usize) }
        };
        let bytes = run(bytes, mode, limits)?;
        // Box allocation remains an explicitly documented infallible fixed owner allocation.
        let result = Box::new(NwCodecOwner { bytes });
        LIVE.fetch_add(1, Ordering::SeqCst);
        CAPACITY.fetch_add(result.bytes.capacity() as u64, Ordering::SeqCst);
        Ok(result)
    });
    match result {
        Ok(result) => {
            // SAFETY: publish all fields only after every fallible operation completes.
            unsafe {
                data.write(if result.bytes.is_empty() {
                    std::ptr::null()
                } else {
                    result.bytes.as_ptr()
                });
                output_length.write(result.bytes.len() as u64);
                owner.write(Box::into_raw(result));
            }
            NW_OK
        }
        Err(failure) => {
            // SAFETY: D08 validates null/capacity before filling caller-owned diagnostics.
            unsafe { write_error(failure.status, error, failure.fields) }
        }
    }
}

/// Releases only an owner created by this module, using its original allocator.
/// # Safety
/// A non-null owner is uniquely owned, unreleased, and has no remaining borrowed readers.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_syncproof_release_v1(owner: *mut NwCodecOwner) {
    if !owner.is_null() {
        // SAFETY: caller returns the unique creator ownership right after every read ends.
        drop(unsafe { Box::from_raw(owner) });
    }
}

/// Reports exact owned-result counters, excluding upstream workspace and allocator retention.
/// # Safety
/// All four outputs are live aligned mutually disjoint u64 slots until return.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_syncproof_counters_v1(
    live: *mut u64,
    released: *mut u64,
    capacity: *mut u64,
    active: *mut u64,
) -> u32 {
    if live.is_null()
        || released.is_null()
        || capacity.is_null()
        || active.is_null()
        || !live.is_aligned()
        || !released.is_aligned()
        || !capacity.is_aligned()
        || !active.is_aligned()
    {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: caller guarantees four live aligned disjoint output slots.
    unsafe {
        live.write(LIVE.load(Ordering::SeqCst));
        released.write(RELEASED.load(Ordering::SeqCst));
        capacity.write(CAPACITY.load(Ordering::SeqCst));
        active.write(BOUNDARY.active_calls());
    }
    NW_OK
}

#[cfg(test)]
mod tests;
