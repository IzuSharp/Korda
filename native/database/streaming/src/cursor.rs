use crate::{
    Budget, CodecRow, ColumnMetadata, ErrorKind, Limits, Result, Shape, StreamError,
    budget::Reservation, error, row::StagedRow,
};
use futures_util::{Stream, StreamExt, future::BoxFuture};
use nativeweave_data_contracts::{ConnectionDisposition, wire::Batch};
use std::{pin::Pin, sync::Arc};
use tokio::runtime::Handle;

/// Separate local-safety/protocol observation supplied by the real provider.
#[derive(Clone, Copy, Debug)]
pub struct Retirement {
    /// True only after provider work can no longer access cursor/input memory.
    pub native_retired: bool,
    /// The provider actually stopped this execution; a late request alone is insufficient.
    pub canceled: bool,
    /// Whether the physical connection is usable, resetting, isolated, or discarded.
    pub disposition: ConnectionDisposition,
    /// Provider's own observed maximum retained row count, separate from wire bytes.
    pub provider_rows_peak: usize,
}
/// Minimal composition seam; provider cleanup must remain rooted independently.
pub struct Source<R> {
    /// Owned statement metadata, checked even when the query returns no rows.
    pub columns: Vec<ColumnMetadata>,
    /// Actual provider rows; no unbounded fetch_all or DTO mapping occurs here.
    pub rows: Pin<Box<dyn Stream<Item = std::result::Result<R, StreamError>> + Send>>,
    /// Nonblocking, idempotent stop request; never joins a worker or takes its I/O lock.
    pub request_stop: Arc<dyn Fn() + Send + Sync>,
    /// Independent provider cleanup receipt; dropping rows is not this receipt.
    pub join: BoxFuture<'static, Result<Retirement>>,
}
/// Completion of one fetch; the owner must subsequently be taken at most once.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Fetch {
    /// A complete owned batch is ready, including a zero-row EOS batch.
    Ready { rows: usize, end: bool },
    /// All batches have already been transferred and no fetch remains.
    End,
}
/// One independent batch owner; borrowing bytes cannot outlive this reference.
pub struct OwnedBatch {
    bytes: Vec<u8>,
    rows: usize,
    end: bool,
    reservation: Arc<Reservation>,
}
impl OwnedBatch {
    /// Borrows complete immutable wire bytes only while this owner is retained.
    pub fn bytes(&self) -> &[u8] {
        &self.bytes
    }
    /// Complete row count.
    pub fn rows(&self) -> usize {
        self.rows
    }
    /// Whether the provider reached its natural end before this batch was published.
    pub fn end(&self) -> bool {
        self.end
    }
}
impl Drop for OwnedBatch {
    fn drop(&mut self) {
        self.reservation.release_batch(self.bytes.len());
    }
}

/// One-consumer cursor with one active fetch and one live transferable batch.
///
/// Rust exclusive borrowing prohibits concurrent fetch/take/close. FFI hosts must
/// implement the same rejecting admission, not queue calls behind an I/O lock.
/// Explicit close joins the provider. Drop transfers the already-reserved cleanup
/// to the supplied engine runtime and does not report retirement. No runtime is
/// created here; the host must retain that runtime until its admitted work retires.
pub struct Cursor<R: CodecRow> {
    shape: Shape,
    limits: Limits,
    source: Option<ProviderGuard<R>>,
    reservation: Arc<Reservation>,
    pending: Option<StagedRow>,
    ready: Option<OwnedBatch>,
    ended: bool,
    closed: bool,
    fetch_incomplete: bool,
    retirement: Option<Retirement>,
}
impl<R: CodecRow> Cursor<R> {
    /// Reserves copy-path bytes before invoking the source factory or doing I/O.
    /// The factory receives no borrowed input and is responsible for bounded provider admission.
    pub async fn open<F, Fut>(
        shape: Shape,
        limits: Limits,
        budget: &Budget,
        cleanup_runtime: Handle,
        open: F,
    ) -> Result<Self>
    where
        F: FnOnce() -> Fut,
        Fut: std::future::Future<Output = Result<Source<R>>>,
    {
        shape.validate_limits(limits)?;
        let reservation = budget.reserve(shape.reservation_bytes(limits)?)?;
        let source = open().await?;
        let validation = shape.validate_columns(&source.columns);
        let mut source = ProviderGuard {
            source: Some(source),
            runtime: cleanup_runtime,
            reservation: Arc::clone(&reservation),
            failed: false,
        };
        if let Err(failure) = validation {
            // Cleanup failure must not hide the original shape failure; the
            // provider's independent owner still retains any unfinished cleanup.
            let _ = source.join(true).await;
            return Err(failure);
        }
        Ok(Self {
            shape,
            limits,
            source: Some(source),
            reservation,
            pending: None,
            ready: None,
            ended: false,
            closed: false,
            fetch_incomplete: false,
            retirement: None,
        })
    }
    /// Fetches on demand; no producer-side prefetch is introduced by this layer.
    /// Hosts must retain this accepted future until its native terminal result.
    /// Dropping it can discard already-consumed rows: a subsequent fetch rejects
    /// rather than pretending to resume at the previous position. Close/drop still
    /// requests stop and joins the independently rooted provider cleanup.
    pub async fn fetch(&mut self) -> Result<Fetch> {
        if self.fetch_incomplete {
            return Err(error(
                ErrorKind::InvalidState,
                "a fetch was abandoned; close the cursor instead of resuming",
            ));
        }
        if self.reservation.batch_live() {
            return Err(error(
                ErrorKind::InvalidState,
                "release the previous batch before another fetch",
            ));
        }
        if self.closed {
            return Err(error(ErrorKind::InvalidState, "cursor is closed"));
        }
        if self.ended {
            return Ok(Fetch::End);
        }
        self.fetch_incomplete = true;
        let result = self.fill().await;
        self.fetch_incomplete = false;
        match result {
            Ok(result) => Ok(result),
            Err(failure) => {
                let _ = self.close().await;
                Err(failure)
            }
        }
    }
    async fn fill(&mut self) -> Result<Fetch> {
        let mut rows = Vec::with_capacity(self.limits.max_rows);
        let mut bytes = self.shape.base_bytes;
        while rows.len() < self.limits.max_rows {
            let row = if let Some(row) = self.pending.take() {
                row
            } else {
                let next = self
                    .source
                    .as_mut()
                    .ok_or_else(|| error(ErrorKind::InvalidState, "provider source missing"))?
                    .source
                    .as_mut()
                    .ok_or_else(|| error(ErrorKind::InvalidState, "provider source retired"))?
                    .rows
                    .next()
                    .await;
                match next {
                    Some(Ok(row)) => StagedRow::decode(&row, &self.shape, self.limits)?,
                    Some(Err(failure)) => return Err(failure),
                    None => {
                        self.ended = true;
                        break;
                    }
                }
            };
            let next_bytes = bytes
                .checked_add(row.wire_bytes)
                .ok_or_else(|| error(ErrorKind::Capacity, "batch byte overflow"))?;
            if next_bytes > self.limits.max_batch_bytes {
                // One bounded already-decoded lookahead row is retained; it is
                // included in the reservation and no further row is pulled.
                self.pending = Some(row);
                break;
            }
            bytes = next_bytes;
            rows.push(row);
        }
        let values = rows
            .iter()
            .flat_map(|r| r.cells.iter().map(|c| c.as_deref()))
            .collect::<Vec<_>>();
        let encoded = Batch::encode(&self.shape.expected, rows.len() as u32, &values)?;
        if encoded.len() != bytes || encoded.len() > self.limits.max_batch_bytes {
            return Err(error(ErrorKind::Capacity, "wire accounting mismatch"));
        }
        self.reservation.publish(encoded.len())?;
        self.ready = Some(OwnedBatch {
            bytes: encoded,
            rows: rows.len(),
            end: self.ended,
            reservation: Arc::clone(&self.reservation),
        });
        if self.ended {
            let receipt = self.join_provider(false).await?;
            if receipt.canceled {
                return Err(error(ErrorKind::Canceled, "provider confirmed stream stop"));
            }
        }
        Ok(Fetch::Ready {
            rows: rows.len(),
            end: self.ended,
        })
    }
    /// Atomically moves the complete owner; repeated take yields no second owner.
    pub fn take_batch(&mut self) -> Result<OwnedBatch> {
        let batch = self
            .ready
            .take()
            .ok_or_else(|| error(ErrorKind::InvalidState, "no untaken batch is ready"))?;
        self.reservation.taken();
        Ok(batch)
    }
    /// Requests stop, releases untaken/staging owners and joins actual provider cleanup.
    /// Already-transferred batches remain independently valid and charged.
    pub async fn close(&mut self) -> Result<Retirement> {
        self.closed = true;
        self.pending.take();
        self.ready.take();
        self.join_provider(true).await
    }
    async fn join_provider(&mut self, stop: bool) -> Result<Retirement> {
        if let Some(receipt) = self.retirement {
            return Ok(receipt);
        }
        let Some(source) = self.source.as_mut() else {
            return Err(error(
                ErrorKind::CleanupPending,
                "provider retirement not confirmed",
            ));
        };
        let receipt = source.join(stop).await?;
        self.retirement = Some(receipt);
        self.source.take();
        Ok(receipt)
    }
    /// Reports only a provider-confirmed cleanup observation.
    pub fn retirement(&self) -> Option<Retirement> {
        self.retirement
    }
}
// This guard stays in the Cursor while close awaits. Canceling that wait never
// drops/restarts the provider future. Abandoning the whole cursor schedules that
// same future, retaining the cursor reservation until actual retirement.
struct ProviderGuard<R: Send + 'static> {
    source: Option<Source<R>>,
    runtime: Handle,
    reservation: Arc<Reservation>,
    failed: bool,
}
impl<R: Send + 'static> ProviderGuard<R> {
    async fn join(&mut self, stop: bool) -> Result<Retirement> {
        if self.failed {
            return Err(error(
                ErrorKind::CleanupPending,
                "provider retirement is indeterminate",
            ));
        }
        let source = self
            .source
            .as_mut()
            .ok_or_else(|| error(ErrorKind::InvalidState, "provider already retired"))?;
        if stop {
            (source.request_stop)();
        }
        source.rows = Box::pin(futures_util::stream::empty());
        let result = (&mut source.join).await;
        match result {
            Ok(receipt) if receipt.native_retired => {
                self.source.take();
                Ok(receipt)
            }
            _ => {
                self.failed = true;
                Err(error(
                    ErrorKind::CleanupPending,
                    "provider retirement is indeterminate",
                ))
            }
        }
    }
}
// If the host shuts down/aborts the cleanup executor or a provider cannot confirm
// safety, deliberately retain the bounded permit. An unconfirmed job cannot make
// new admission possible by merely being dropped.
struct CleanupCharge(Option<Arc<Reservation>>);
impl CleanupCharge {
    fn confirm(&mut self) {
        self.0.take();
    }
}
impl Drop for CleanupCharge {
    fn drop(&mut self) {
        if let Some(charge) = self.0.take() {
            std::mem::forget(charge);
        }
    }
}
impl<R: Send + 'static> Drop for ProviderGuard<R> {
    fn drop(&mut self) {
        let Some(mut source) = self.source.take() else {
            return;
        };
        (source.request_stop)();
        source.rows = Box::pin(futures_util::stream::empty());
        let mut charge = CleanupCharge(Some(Arc::clone(&self.reservation)));
        if self.failed {
            std::mem::forget(source);
            return;
        }
        self.runtime.spawn(async move {
            if matches!((&mut source.join).await, Ok(receipt) if receipt.native_retired) {
                charge.confirm();
            } else {
                // Retain the provider control receipt as well as admission if
                // safety is unknown; this is bounded isolation, never success.
                std::mem::forget(source);
            }
        });
    }
}
