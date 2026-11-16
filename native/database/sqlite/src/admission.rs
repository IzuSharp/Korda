use crate::{ErrorKind, Limits, ProviderError, Result};
use std::sync::{
    Arc,
    atomic::{AtomicUsize, Ordering},
};
use tokio::sync::{OwnedSemaphorePermit, Semaphore};

/// Engine-shared finite count and input-byte reservations. No unbounded waiter queue.
#[derive(Clone)]
pub struct Admission(Arc<Inner>);
struct Inner {
    limits: Limits,
    commands: Arc<Semaphore>,
    bytes: Arc<Semaphore>,
    queued: AtomicUsize,
    active: AtomicUsize,
    input_bytes: AtomicUsize,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct AdmissionSnapshot {
    pub queued_commands: usize,
    pub active_commands: usize,
    pub retained_input_bytes: usize,
}
impl Admission {
    pub fn new(limits: Limits) -> Result<Self> {
        limits.validate()?;
        Ok(Self(Arc::new(Inner {
            limits,
            commands: Arc::new(Semaphore::new(limits.max_commands)),
            bytes: Arc::new(Semaphore::new(limits.max_input_bytes)),
            queued: AtomicUsize::new(0),
            active: AtomicUsize::new(0),
            input_bytes: AtomicUsize::new(0),
        })))
    }
    pub fn limits(&self) -> Limits {
        self.0.limits
    }
    pub fn snapshot(&self) -> AdmissionSnapshot {
        AdmissionSnapshot {
            queued_commands: self.0.queued.load(Ordering::Acquire),
            active_commands: self.0.active.load(Ordering::Acquire),
            retained_input_bytes: self.0.input_bytes.load(Ordering::Acquire),
        }
    }
    pub(crate) fn reserve(&self, bytes: usize) -> Result<Reservation> {
        let count = self
            .0
            .commands
            .clone()
            .try_acquire_owned()
            .map_err(|_| ProviderError::new(ErrorKind::Capacity, "SQLite command capacity"))?;
        let owned_bytes = self
            .0
            .bytes
            .clone()
            .try_acquire_many_owned(u32::try_from(bytes).map_err(|_| {
                ProviderError::new(ErrorKind::Capacity, "SQLite input byte overflow")
            })?)
            .map_err(|_| ProviderError::new(ErrorKind::Capacity, "SQLite input byte capacity"))?;
        self.0.queued.fetch_add(1, Ordering::AcqRel);
        self.0.input_bytes.fetch_add(bytes, Ordering::AcqRel);
        Ok(Reservation {
            admission: self.clone(),
            bytes,
            active: false,
            _count: count,
            _bytes: owned_bytes,
        })
    }
}
pub(crate) struct Reservation {
    admission: Admission,
    bytes: usize,
    active: bool,
    _count: OwnedSemaphorePermit,
    _bytes: OwnedSemaphorePermit,
}
impl Reservation {
    pub fn start(&mut self) {
        self.admission.0.queued.fetch_sub(1, Ordering::AcqRel);
        self.admission.0.active.fetch_add(1, Ordering::AcqRel);
        self.active = true;
    }
}
impl Drop for Reservation {
    fn drop(&mut self) {
        if self.active {
            self.admission.0.active.fetch_sub(1, Ordering::AcqRel);
        } else {
            self.admission.0.queued.fetch_sub(1, Ordering::AcqRel);
        }
        self.admission
            .0
            .input_bytes
            .fetch_sub(self.bytes, Ordering::AcqRel);
    }
}
