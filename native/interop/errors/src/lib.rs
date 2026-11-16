//! Shared D08 failure records and unwind containment. No domain runtime or global engine.
//!
//! An engine embeds one [`EngineBoundary`]. Ordinary calls acquire admission and
//! execute within [`EngineBoundary::business`]. Asynchronous adapters must guard
//! the actual worker/poll body as well; guarding spawn alone cannot contain it.

use nativeweave_interop_abi::{
    NW_CONTRACT_VIOLATION, NW_ENGINE_POISONED, NW_INVALID_ARGUMENT, NW_NATIVE_PANIC, NW_OK,
    NW_PENDING, NwErrorInfoV1, NwStatus,
};
use std::panic::{UnwindSafe, catch_unwind};
use std::sync::atomic::{AtomicU64, Ordering};

const POISONED: u64 = 1 << 63;
const ACTIVE_MASK: u64 = POISONED - 1;

/// Fixed optional diagnostics. Zero correlation is valid; there is no owner transfer.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct ErrorFields {
    /// Registered domain identifier; zero is base interop.
    pub domain: u32,
    /// Original signed native provider code.
    pub code: i32,
    /// Opaque diagnostic correlation, never an authorization or resource handle.
    pub correlation_id: u64,
}

/// Writes exactly one complete v1 error record if it fits, returning the original status.
///
/// Success/pending/null and declared capacity below 24 write nothing. A short
/// diagnostic buffer never changes a business failure to BUFFER_TOO_SMALL. No
/// allocation, callback, string formatting or owner transfer occurs.
///
/// # Safety
/// A non-null pointer is naturally aligned and has at least four readable bytes.
/// Its initialized capacity header is no greater than the actual writable range.
/// That range is exclusively borrowed for the call and overlaps no other input/output.
pub unsafe fn write_error(
    status: NwStatus,
    output: *mut NwErrorInfoV1,
    fields: ErrorFields,
) -> NwStatus {
    if status == NW_OK || status == NW_PENDING || output.is_null() || !output.is_aligned() {
        return status;
    }
    // SAFETY: caller guarantees only the four-byte initialized header initially;
    // do not form a full record reference before validating that capacity.
    let capacity = unsafe { output.cast::<u32>().read() };
    if capacity < size_of::<NwErrorInfoV1>() as u32 {
        return status;
    }
    let value = NwErrorInfoV1 {
        struct_size: size_of::<NwErrorInfoV1>() as u32,
        domain: fields.domain,
        code: fields.code,
        flags: 0,
        correlation_id: fields.correlation_id,
    };
    // SAFETY: caller's truthful capacity and alignment allow exactly this complete
    // record; no access is made to a larger caller-owned tail.
    unsafe { output.write(value) };
    status
}

/// An ordinary status error plus optional fixed diagnostics; it owns no panic payload.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Failure {
    /// Non-success status. Construct using [`Failure::new`] to reject OK/PENDING.
    pub status: NwStatus,
    /// Fixed diagnostic values.
    pub fields: ErrorFields,
}

impl Failure {
    /// Retains unknown failure codes. Invalid success/pending becomes contract violation.
    pub const fn new(status: NwStatus, fields: ErrorFields) -> Self {
        Self {
            status: if status <= NW_PENDING {
                NW_CONTRACT_VIOLATION
            } else {
                status
            },
            fields,
        }
    }

    fn base(status: NwStatus) -> Self {
        Self::new(status, ErrorFields::default())
    }
}

/// Per-engine admission and panic quarantine. It never unloads code or claims retirement.
///
/// Poison is permanent. Already admitted calls may finish independently; callers
/// must retain their owners until those calls and callbacks actually retire.
/// Unknown caught payloads are deliberately forgotten, because Drop itself may
/// panic. The count is exact, but their reachable byte size is unknown. Host
/// admission bounds/restart policy must account for all concurrently accepted calls.
#[derive(Default)]
pub struct EngineBoundary {
    state: AtomicU64,
    quarantine_objects: AtomicU64,
}

impl EngineBoundary {
    /// Creates a runtime-free boundary with open admission and zero active calls.
    pub const fn new() -> Self {
        Self {
            state: AtomicU64::new(0),
            quarantine_objects: AtomicU64::new(0),
        }
    }

    /// Reports permanently closed business admission, not physical cleanup completion.
    pub fn is_poisoned(&self) -> bool {
        self.state.load(Ordering::Acquire) & POISONED != 0
    }

    /// Reports currently admitted business or trusted cleanup calls.
    pub fn active_calls(&self) -> u64 {
        self.state.load(Ordering::Acquire) & ACTIVE_MASK
    }

    /// Reports deliberately retained panic payload objects; their byte size is unknown.
    pub fn quarantined_payloads(&self) -> u64 {
        self.quarantine_objects.load(Ordering::Acquire)
    }

    /// Atomically closes new business admission. Dedicated cleanup remains available.
    pub fn poison(&self) {
        self.state.fetch_or(POISONED, Ordering::AcqRel);
    }

    fn admit(&self, cleanup: bool) -> Result<Admission<'_>, Failure> {
        let mut current = self.state.load(Ordering::Acquire);
        loop {
            if (!cleanup && current & POISONED != 0) || current & ACTIVE_MASK == ACTIVE_MASK {
                return Err(Failure::base(NW_ENGINE_POISONED));
            }
            match self.state.compare_exchange_weak(
                current,
                current + 1,
                Ordering::AcqRel,
                Ordering::Acquire,
            ) {
                Ok(_) => return Ok(Admission(self)),
                Err(observed) => current = observed,
            }
        }
    }

    fn execute<T, F>(&self, cleanup: bool, body: F) -> Result<T, Failure>
    where
        F: FnOnce() -> Result<T, Failure> + UnwindSafe,
    {
        let _admission = self.admit(cleanup)?;
        match catch_unwind(body) {
            Ok(Ok(value)) => Ok(value),
            Ok(Err(mut failure)) => {
                // Failure fields are public for adapters. Defensively normalize a
                // literal invalid failure so it cannot masquerade as success.
                if failure.status <= NW_PENDING {
                    failure.status = NW_CONTRACT_VIOLATION;
                }
                if matches!(
                    failure.status,
                    NW_CONTRACT_VIOLATION | NW_NATIVE_PANIC | NW_ENGINE_POISONED
                ) {
                    self.poison();
                }
                Err(failure)
            }
            Err(payload) => {
                self.poison();
                self.quarantine_objects.fetch_add(1, Ordering::AcqRel);
                std::mem::forget(payload);
                Err(Failure::base(NW_NATIVE_PANIC))
            }
        }
    }

    /// Runs ordinary work under admission and unwind containment.
    ///
    /// No blanket AssertUnwindSafe is used. Captures must meet Rust's unwind-safety
    /// contract; after panic, business state is not inspected again by this guard.
    pub fn business<T, F>(&self, body: F) -> Result<T, Failure>
    where
        F: FnOnce() -> Result<T, Failure> + UnwindSafe,
    {
        self.execute(false, body)
    }

    /// Contains a specifically trusted cleanup control after poison as well as before it.
    ///
    /// The caller must establish that captured fields are safe after partial work.
    /// Success means only that body returned; it is not a generic quiescence proof.
    pub fn cleanup<T, F>(&self, body: F) -> Result<T, Failure>
    where
        F: FnOnce() -> Result<T, Failure> + UnwindSafe,
    {
        self.execute(true, body)
    }
}

struct Admission<'a>(&'a EngineBoundary);
impl Drop for Admission<'_> {
    fn drop(&mut self) {
        self.0.state.fetch_sub(1, Ordering::Release);
    }
}

/// Maximum copied detail bytes, excluding no implicit NUL terminator.
pub const MAX_DETAIL_BYTES: usize = 4096;

/// Owned bounded UTF-8 diagnostic bytes, independent from the fixed record.
///
/// Only deliberately redacted public text belongs here. Ordinary status and panic
/// paths use static classifications and never copy raw panic text, SQL or credentials.
pub struct BoundedDetail {
    bytes: [u8; MAX_DETAIL_BYTES],
    length: usize,
    truncated: bool,
}

impl BoundedDetail {
    /// Copies at most min(limit,4096) UTF-8 bytes, stopping before a split code point.
    /// The input is borrowed only for this call; no heap allocation is needed.
    pub fn from_redacted(text: &str, limit: usize) -> Self {
        let mut length = text.len().min(limit.min(MAX_DETAIL_BYTES));
        while !text.is_char_boundary(length) {
            length -= 1;
        }
        let mut result = Self {
            bytes: [0; MAX_DETAIL_BYTES],
            length,
            truncated: length < text.len(),
        };
        result.bytes[..length].copy_from_slice(&text.as_bytes()[..length]);
        result
    }

    /// Borrows valid UTF-8 bytes while this owner is live; no pointer crosses an await.
    pub fn bytes(&self) -> &[u8] {
        &self.bytes[..self.length]
    }
    /// Reports omitted bytes without presenting a truncated value as complete.
    pub fn is_truncated(&self) -> bool {
        self.truncated
    }
}

/// Creates a single owned value with a null output until successful transfer.
///
/// # Safety
/// `output` points to a live, exclusively writable, aligned pointer slot. The
/// caller uses the matching adapter release exactly once after successful transfer.
pub unsafe fn create_owner<T, F>(
    boundary: &EngineBoundary,
    output: *mut *mut T,
    factory: F,
) -> NwStatus
where
    F: FnOnce() -> Result<Box<T>, Failure> + UnwindSafe,
{
    if output.is_null() || !output.is_aligned() {
        return NW_INVALID_ARGUMENT;
    }
    // SAFETY: valid caller-owned output slot; initialize before any fallible work.
    unsafe { output.write(std::ptr::null_mut()) };
    match boundary.business(factory) {
        Ok(owner) => {
            // SAFETY: the same live slot receives ownership exactly once.
            unsafe { output.write(Box::into_raw(owner)) };
            NW_OK
        }
        Err(failure) => failure.status,
    }
}

#[cfg(test)]
mod tests;
