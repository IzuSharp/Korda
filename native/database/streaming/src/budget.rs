use crate::{ErrorKind, Result, error};
use std::sync::{Arc, Mutex};

/// Observable explicit reservations and live transferable wire owners.
#[derive(Default, Clone, Copy, Debug)]
pub struct BudgetSnapshot {
    /// Cursor reservations, retained by any outstanding independent batch owner.
    pub live_cursors: usize,
    /// Conservative reserved bytes for all native owned-copy paths in this module.
    pub reserved_bytes: usize,
    /// Maximum simultaneous reserved bytes.
    pub peak_reserved_bytes: usize,
    /// Live batch owners, whether ready or transferred.
    pub live_batches: usize,
    /// Exact live serialized bytes in those owners.
    pub live_batch_bytes: usize,
    /// Peak simultaneous serialized bytes.
    pub peak_batch_bytes: usize,
    /// Total transferred batch owners.
    pub batches_taken: usize,
}
struct State {
    snapshot: BudgetSnapshot,
    max_cursors: usize,
    max_bytes: usize,
}
/// Finite admission shared by cursors of one engine; no waiters or hidden queue.
#[derive(Clone)]
pub struct Budget(Arc<Mutex<State>>);
impl Budget {
    /// Creates an explicit finite cursor and byte budget.
    pub fn new(max_cursors: usize, max_bytes: usize) -> Result<Self> {
        if max_cursors == 0
            || max_cursors > 4096
            || max_bytes == 0
            || max_bytes > isize::MAX as usize
        {
            return Err(error(
                ErrorKind::Capacity,
                "invalid stream admission budget",
            ));
        }
        Ok(Self(Arc::new(Mutex::new(State {
            snapshot: BudgetSnapshot::default(),
            max_cursors,
            max_bytes,
        }))))
    }
    /// Reads accounting without treating it as process RSS or provider allocations.
    pub fn snapshot(&self) -> BudgetSnapshot {
        self.0.lock().unwrap_or_else(|p| p.into_inner()).snapshot
    }
    pub(crate) fn reserve(&self, bytes: usize) -> Result<Arc<Reservation>> {
        let mut s = self.0.lock().unwrap_or_else(|p| p.into_inner());
        if s.snapshot.live_cursors >= s.max_cursors
            || bytes > s.max_bytes.saturating_sub(s.snapshot.reserved_bytes)
        {
            return Err(error(
                ErrorKind::Capacity,
                "stream count or owned-byte admission exhausted",
            ));
        }
        s.snapshot.live_cursors += 1;
        s.snapshot.reserved_bytes += bytes;
        s.snapshot.peak_reserved_bytes = s
            .snapshot
            .peak_reserved_bytes
            .max(s.snapshot.reserved_bytes);
        Ok(Arc::new(Reservation {
            budget: self.clone(),
            bytes,
            live_batch: Mutex::new(false),
        }))
    }
}
pub(crate) struct Reservation {
    budget: Budget,
    bytes: usize,
    live_batch: Mutex<bool>,
}
impl Reservation {
    pub(crate) fn batch_live(&self) -> bool {
        *self.live_batch.lock().unwrap_or_else(|p| p.into_inner())
    }
    pub(crate) fn publish(&self, bytes: usize) -> Result<()> {
        let mut live = self.live_batch.lock().unwrap_or_else(|p| p.into_inner());
        if *live {
            return Err(error(
                ErrorKind::InvalidState,
                "previous batch owner is still live",
            ));
        }
        let mut state = self.budget.0.lock().unwrap_or_else(|p| p.into_inner());
        state.snapshot.live_batches += 1;
        state.snapshot.live_batch_bytes += bytes;
        state.snapshot.peak_batch_bytes = state
            .snapshot
            .peak_batch_bytes
            .max(state.snapshot.live_batch_bytes);
        *live = true;
        Ok(())
    }
    pub(crate) fn taken(&self) {
        let mut state = self.budget.0.lock().unwrap_or_else(|p| p.into_inner());
        state.snapshot.batches_taken = state.snapshot.batches_taken.saturating_add(1);
    }
    pub(crate) fn release_batch(&self, bytes: usize) {
        let mut live = self.live_batch.lock().unwrap_or_else(|p| p.into_inner());
        let mut state = self.budget.0.lock().unwrap_or_else(|p| p.into_inner());
        state.snapshot.live_batches -= 1;
        state.snapshot.live_batch_bytes -= bytes;
        *live = false;
    }
}
impl Drop for Reservation {
    fn drop(&mut self) {
        let mut state = self.budget.0.lock().unwrap_or_else(|p| p.into_inner());
        state.snapshot.live_cursors -= 1;
        state.snapshot.reserved_bytes -= self.bytes;
    }
}
