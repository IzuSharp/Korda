//! Bounded native Body and allocation ownership for the required Rust Server.
//!
//! No runtime, listener or callback protocol is created here. B17 composes this
//! crate into its negotiated host and uses Core operations for start/take/retire.
//! Payload credit is an atomic nonwaiting request/global grant. Queue readiness
//! may wait; its reservation carries no payload credit until readiness succeeds.

use bytes::Bytes;
use http::HeaderMap;
use http_body::{Body, Frame, SizeHint};
use std::fmt;
use std::pin::Pin;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};
use std::task::{Context, Poll};
use tokio::sync::{Notify, mpsc};

/// Bounded native failure categories; the host translates them to Core status/error fields.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum StreamError {
    InvalidArgument,
    ResourceLimit,
    Canceled,
    Closed,
    Transport,
}
impl fmt::Display for StreamError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            Self::InvalidArgument => "invalid_argument",
            Self::ResourceLimit => "resource_limit",
            Self::Canceled => "canceled",
            Self::Closed => "invalid_state",
            Self::Transport => "transport_failure",
        })
    }
}
impl std::error::Error for StreamError {}

/// Independently accounted payload side.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Side {
    Native,
    Managed,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
struct Usage {
    native: u64,
    managed: u64,
}
impl Usage {
    fn get(&self, side: Side) -> u64 {
        match side {
            Side::Native => self.native,
            Side::Managed => self.managed,
        }
    }
    fn set(&mut self, side: Side, value: u64) {
        match side {
            Side::Native => self.native = value,
            Side::Managed => self.managed = value,
        }
    }
}

/// One engine instance's separately bounded native and managed payload ledgers.
pub struct Budget {
    limits: Usage,
    usage: Mutex<Usage>,
}
impl Budget {
    /// Creates fixed positive limits. This does not create a runtime or payload.
    pub fn new(native_bytes: u64, managed_bytes: u64) -> Result<Arc<Self>, StreamError> {
        if native_bytes == 0 || managed_bytes == 0 {
            return Err(StreamError::InvalidArgument);
        }
        Ok(Arc::new(Self {
            limits: Usage {
                native: native_bytes,
                managed: managed_bytes,
            },
            usage: Mutex::new(Usage::default()),
        }))
    }
    /// Reserves a fixed request credit anchor at logical request admission.
    pub fn request(
        self: &Arc<Self>,
        per_side_bytes: u64,
    ) -> Result<Arc<RequestBudget>, StreamError> {
        if per_side_bytes == 0 {
            return Err(StreamError::InvalidArgument);
        }
        Ok(Arc::new(RequestBudget {
            global: Arc::clone(self),
            limit: per_side_bytes,
            usage: Mutex::new(Usage::default()),
            closed: AtomicBool::new(false),
        }))
    }
    /// Returns exact controlled native/managed backing reservations, not process RSS.
    pub fn snapshot(&self) -> (u64, u64) {
        let usage = lock(&self.usage);
        (usage.native, usage.managed)
    }
}

/// A request's payload anchor outlives its managed scope while owned chunks remain.
pub struct RequestBudget {
    global: Arc<Budget>,
    limit: u64,
    usage: Mutex<Usage>,
    closed: AtomicBool,
}
impl RequestBudget {
    /// Acquires both request and global capacity in one critical section or changes neither.
    /// No payload waiter, automatic retry, allocation or callback occurs inside the gate.
    pub fn try_acquire(self: &Arc<Self>, side: Side, capacity: u64) -> Result<Credit, StreamError> {
        if capacity == 0 || capacity > usize::MAX as u64 {
            return Err(StreamError::InvalidArgument);
        }
        let mut global = lock(&self.global.usage);
        let mut request = lock(&self.usage);
        if capacity > self.limit.saturating_sub(request.get(side))
            || capacity
                > self
                    .global
                    .limits
                    .get(side)
                    .saturating_sub(global.get(side))
        {
            return Err(StreamError::ResourceLimit);
        }
        let global_next = global.get(side) + capacity;
        let request_next = request.get(side) + capacity;
        global.set(side, global_next);
        request.set(side, request_next);
        Ok(Credit {
            request: Arc::clone(self),
            side,
            capacity,
        })
    }
    /// Business body admission additionally checks the close latch; owned-buffer copies do not reopen it.
    pub fn reserve_payload(
        self: &Arc<Self>,
        capacity: usize,
    ) -> Result<PayloadReservation, StreamError> {
        if self.closed.load(Ordering::Acquire) {
            return Err(StreamError::Closed);
        }
        let credit = self.try_acquire(Side::Native, capacity as u64)?;
        // The request close race is linearized before transfer of a newly admitted grant.
        if self.closed.load(Ordering::Acquire) {
            return Err(StreamError::Closed);
        }
        Ok(PayloadReservation { credit, capacity })
    }
    /// Closes new business reservations without reclaiming granted or taken payload owners.
    pub fn close(&self) {
        self.closed.store(true, Ordering::Release);
    }
    /// Returns the separately tracked retained native and managed capacities.
    pub fn snapshot(&self) -> (u64, u64) {
        let usage = lock(&self.usage);
        (usage.native, usage.managed)
    }
    pub fn is_closed(&self) -> bool {
        self.closed.load(Ordering::Acquire)
    }
}

/// A unique granted capacity; its destructor returns both quotas exactly once.
pub struct Credit {
    request: Arc<RequestBudget>,
    side: Side,
    capacity: u64,
}
impl Credit {
    pub const fn capacity(&self) -> u64 {
        self.capacity
    }
}
impl Drop for Credit {
    fn drop(&mut self) {
        let mut global = lock(&self.request.global.usage);
        let mut request = lock(&self.request.usage);
        let global_next = global.get(self.side) - self.capacity;
        let request_next = request.get(self.side) - self.capacity;
        global.set(self.side, global_next);
        request.set(self.side, request_next);
    }
}

/// An admitted native capacity before allocation/copy. Dropping it rolls back the grant.
pub struct PayloadReservation {
    credit: Credit,
    capacity: usize,
}
impl PayloadReservation {
    pub const fn capacity(&self) -> usize {
        self.capacity
    }
    /// Copies once into an exact requested Box backing allocation after admission.
    /// Its full reserved capacity stays charged through every nonempty Bytes slice.
    pub fn copy(self, data: &[u8]) -> Result<OwnedBuffer, StreamError> {
        if data.is_empty() || data.len() > self.capacity {
            return Err(StreamError::InvalidArgument);
        }
        let request = Arc::clone(&self.credit.request);
        // Box allocation requests exactly this backing extent; allocator bookkeeping
        // and transport/TLS buffers are separate from the controlled payload ledger.
        let mut storage = Box::<[u8]>::new_uninit_slice(self.capacity);
        for (index, item) in storage.iter_mut().enumerate() {
            item.write(data.get(index).copied().unwrap_or(0));
        }
        // SAFETY: every element of the exact backing allocation was initialized above.
        let storage = unsafe { storage.assume_init() };
        let allocation = Allocation {
            storage,
            _credit: self.credit,
        };
        let bytes = Bytes::from_owner(allocation).slice(..data.len());
        Ok(OwnedBuffer {
            bytes,
            request,
            backing_capacity: self.capacity,
        })
    }
    /// Retains a mature protocol formatter's existing allocation under this grant.
    ///
    /// # Safety
    /// The caller must acquire this grant before formatting and prove that its
    /// capacity covers the formatter's entire backing allocation and every
    /// overlapping allocation during growth/conversion. `data` must be the only
    /// externally escaping owner: all clones/slices that survive this call must
    /// originate from the returned credited Bytes, never from an uncredited
    /// pre-adoption alias. The bound must come from the pinned formatter/allocator
    /// implementation, not merely the visible byte length.
    unsafe fn adopt_admitted(self, data: Bytes) -> Result<OwnedBuffer, StreamError> {
        if data.is_empty() || data.len() > self.capacity {
            return Err(StreamError::InvalidArgument);
        }
        let request = Arc::clone(&self.credit.request);
        let bytes = Bytes::from_owner(FormattedAllocation {
            storage: data,
            _credit: self.credit,
        });
        Ok(OwnedBuffer {
            bytes,
            request,
            backing_capacity: self.capacity,
        })
    }
}

struct FormattedAllocation {
    storage: Bytes,
    _credit: Credit,
}
impl AsRef<[u8]> for FormattedAllocation {
    fn as_ref(&self) -> &[u8] {
        &self.storage
    }
}

struct Allocation {
    storage: Box<[u8]>,
    _credit: Credit,
}
impl AsRef<[u8]> for Allocation {
    fn as_ref(&self) -> &[u8] {
        &self.storage
    }
}

/// Immutable native buffer with an independently retained request credit anchor.
#[derive(Clone)]
pub struct OwnedBuffer {
    bytes: Bytes,
    request: Arc<RequestBudget>,
    backing_capacity: usize,
}
impl OwnedBuffer {
    pub fn len(&self) -> usize {
        self.bytes.len()
    }
    pub fn is_empty(&self) -> bool {
        self.bytes.is_empty()
    }
    pub const fn backing_capacity(&self) -> usize {
        self.backing_capacity
    }
    pub fn as_slice(&self) -> &[u8] {
        &self.bytes
    }
    /// Retains the entire allocation; the visible subrange does not change its charge.
    pub fn slice(&self, start: usize, length: usize) -> Result<Self, StreamError> {
        if length == 0 || start > self.len() || length > self.len() - start {
            return Err(StreamError::InvalidArgument);
        }
        Ok(Self {
            bytes: self.bytes.slice(start..start + length),
            request: Arc::clone(&self.request),
            backing_capacity: self.backing_capacity,
        })
    }
    /// Temporary managed copies remain legal after dispatch closes the business request.
    pub fn acquire_managed(&self, capacity: usize) -> Result<Credit, StreamError> {
        self.request.try_acquire(Side::Managed, capacity as u64)
    }
    pub fn into_bytes(self) -> Bytes {
        self.bytes
    }
}

/// Sticky cancellation for bounded queue/network waits; no user callback runs here.
#[derive(Default)]
pub struct Cancellation {
    canceled: AtomicBool,
    changed: Notify,
}
impl Cancellation {
    pub fn cancel(&self) {
        self.canceled.store(true, Ordering::Release);
        self.changed.notify_waiters();
    }
    pub fn is_canceled(&self) -> bool {
        self.canceled.load(Ordering::Acquire)
    }
    pub async fn cancelled(&self) {
        loop {
            let notified = self.changed.notified();
            tokio::pin!(notified);
            notified.as_mut().enable();
            if self.is_canceled() {
                return;
            }
            notified.await;
        }
    }
}

enum Message {
    Data(Frame<Bytes>),
    End,
    Error(StreamError),
}

/// An output producer sharing one bounded metadata queue and request credit anchor.
#[derive(Clone)]
pub struct BodySender {
    sender: mpsc::Sender<Message>,
    request: Arc<RequestBudget>,
    maximum_chunk: usize,
    finished: Arc<AtomicBool>,
}
/// The actual http_body::Body consumed by the native HTTP protocol engine.
pub struct BoundedBody {
    receiver: mpsc::Receiver<Message>,
    ended: bool,
}
/// A queue slot plus a native grant; no backing bytes have been allocated yet.
pub struct WriteReservation {
    permit: mpsc::OwnedPermit<Message>,
    payload: PayloadReservation,
}
/// A copied input plus its original queue and byte grants. Creating this value
/// performs no queue send; an operation may own it before start, then either
/// commit it or drop it without publishing network work.
pub struct PreparedWrite {
    permit: mpsc::OwnedPermit<Message>,
    buffer: OwnedBuffer,
}
impl PreparedWrite {
    /// Transfers the admitted allocation without another copy. This is local
    /// enqueue only and does not certify peer consumption.
    pub fn commit(self) {
        self.permit
            .send(Message::Data(Frame::data(self.buffer.into_bytes())));
    }
}
impl WriteReservation {
    pub const fn capacity(&self) -> usize {
        self.payload.capacity()
    }
    /// Performs one bounded copy and transfers the queue and allocation ownership.
    /// The sender's completion is local enqueue, not proof of remote consumption.
    pub fn commit(self, data: &[u8]) -> Result<(), StreamError> {
        self.prepare(data)?.commit();
        Ok(())
    }
    /// Copies an ABI input before create returns, retaining its pre-reserved
    /// queue slot and whole backing capacity until start or unstarted release.
    pub fn prepare(self, data: &[u8]) -> Result<PreparedWrite, StreamError> {
        let buffer = self.payload.copy(data)?;
        Ok(PreparedWrite {
            permit: self.permit,
            buffer,
        })
    }
    /// Adopts an already admitted formatter output without copying it or enqueueing
    /// before operation start. Dropping the prepared owner rolls both grants back.
    ///
    /// # Safety
    /// The pregrant must cover full formatter backing capacity plus growth and
    /// conversion overlap before any formatting allocation. No uncredited clone
    /// or slice of `data` may escape; retained derivatives must be obtained from
    /// the credited output after commit. Visible length alone is not a proof.
    pub unsafe fn prepare_admitted(self, data: Bytes) -> Result<PreparedWrite, StreamError> {
        // SAFETY: the engine adapter supplies the formatter-specific bound and
        // no-alias proof required by this explicitly unsafe entry.
        let buffer = unsafe { self.payload.adopt_admitted(data)? };
        Ok(PreparedWrite {
            permit: self.permit,
            buffer,
        })
    }
}

/// Creates a Body without launching a runtime. All limits must be finite and positive.
pub fn body_channel(
    request: Arc<RequestBudget>,
    maximum_chunk: usize,
    slots: usize,
) -> Result<(BodySender, BoundedBody), StreamError> {
    if maximum_chunk == 0 || maximum_chunk as u64 > request.limit || slots == 0 || slots > 65_536 {
        return Err(StreamError::InvalidArgument);
    }
    let (sender, receiver) = mpsc::channel(slots);
    Ok((
        BodySender {
            sender,
            request,
            maximum_chunk,
            finished: Arc::new(AtomicBool::new(false)),
        },
        BoundedBody {
            receiver,
            ended: false,
        },
    ))
}
impl BodySender {
    /// Waits for queue metadata first, then tries the complete payload grant once.
    /// Cancellation and every rejection roll back the queue position and grants.
    pub async fn reserve(
        &self,
        capacity: usize,
        cancel: &Cancellation,
    ) -> Result<WriteReservation, StreamError> {
        if capacity == 0 || capacity > self.maximum_chunk {
            return Err(StreamError::InvalidArgument);
        }
        if self.request.is_closed() || self.finished.load(Ordering::Acquire) {
            return Err(StreamError::Closed);
        }
        let permit = tokio::select! {
            biased;
            () = cancel.cancelled() => return Err(StreamError::Canceled),
            permit = self.sender.clone().reserve_owned() => permit.map_err(|_| StreamError::Closed)?,
        };
        if cancel.is_canceled() {
            return Err(StreamError::Canceled);
        }
        if self.finished.load(Ordering::Acquire) {
            return Err(StreamError::Closed);
        }
        let payload = self.request.reserve_payload(capacity)?;
        Ok(WriteReservation { permit, payload })
    }
    /// Ends production after optional native-validated bounded trailer metadata.
    /// This needs no payload grant, including when taken input fills the request cap.
    pub async fn finish(
        &self,
        trailers: Option<HeaderMap>,
        cancel: &Cancellation,
    ) -> Result<(), StreamError> {
        if self.finished.swap(true, Ordering::AcqRel) {
            return Err(StreamError::Closed);
        }
        if let Some(headers) = trailers {
            self.send_control(Message::Data(Frame::trailers(headers)), cancel)
                .await?;
        }
        self.send_control(Message::End, cancel).await
    }
    pub async fn fail(&self, error: StreamError, cancel: &Cancellation) -> Result<(), StreamError> {
        if self.finished.swap(true, Ordering::AcqRel) {
            return Err(StreamError::Closed);
        }
        self.send_control(Message::Error(error), cancel).await
    }
    async fn send_control(
        &self,
        message: Message,
        cancel: &Cancellation,
    ) -> Result<(), StreamError> {
        tokio::select! {
            biased;
            () = cancel.cancelled() => Err(StreamError::Canceled),
            result = self.sender.send(message) => result.map_err(|_| StreamError::Closed),
        }
    }
    pub fn queue_capacity(&self) -> usize {
        self.sender.capacity()
    }
}

/// A typed owned read result. End is only this Body's end observation, not peer consumption.
pub enum ReadFrame {
    Data(OwnedBuffer),
    End(Option<HeaderMap>),
}

/// A bounded copy boundary from the native transport Body into independent application ownership.
/// The protocol layer must separately bound its source frame storage to maximum_chunk.
pub struct BodyReader<B: Body<Data = Bytes>> {
    body: Pin<Box<B>>,
    request: Arc<RequestBudget>,
    maximum_chunk: usize,
    pending: Option<OwnedBuffer>,
    ended: bool,
}
impl<B: Body<Data = Bytes>> BodyReader<B> {
    pub fn new(
        body: B,
        request: Arc<RequestBudget>,
        maximum_chunk: usize,
    ) -> Result<Self, StreamError> {
        if maximum_chunk == 0 || maximum_chunk as u64 > request.limit {
            return Err(StreamError::InvalidArgument);
        }
        Ok(Self {
            body: Box::pin(body),
            request,
            maximum_chunk,
            pending: None,
            ended: false,
        })
    }
    /// Exactly one mutable reader; acquires payload credit before polling network input.
    /// Cancellation drops an unused grant without revoking any previously taken chunk.
    pub async fn read(
        &mut self,
        maximum_bytes: usize,
        cancel: &Cancellation,
    ) -> Result<ReadFrame, StreamError> {
        if maximum_bytes == 0 || maximum_bytes > self.maximum_chunk {
            return Err(StreamError::InvalidArgument);
        }
        if self.request.is_closed() {
            return Err(StreamError::Closed);
        }
        if cancel.is_canceled() {
            return Err(StreamError::Canceled);
        }
        if let Some(buffer) = self.pending.take() {
            return self.split(buffer, maximum_bytes);
        }
        if self.ended {
            return Ok(ReadFrame::End(None));
        }
        let reservation = self.request.reserve_payload(self.maximum_chunk)?;
        for _ in 0..64 {
            let next = tokio::select! {
                biased;
                () = cancel.cancelled() => return Err(StreamError::Canceled),
                frame = std::future::poll_fn(|cx| self.body.as_mut().poll_frame(cx)) => frame,
            };
            match next {
                None => {
                    self.ended = true;
                    return Ok(ReadFrame::End(None));
                }
                Some(Err(_)) => return Err(StreamError::Transport),
                Some(Ok(frame)) => match frame.into_data() {
                    Ok(data) => {
                        if data.is_empty() {
                            continue;
                        }
                        if data.len() > self.maximum_chunk {
                            return Err(StreamError::ResourceLimit);
                        }
                        let buffer = reservation.copy(&data)?;
                        return self.split(buffer, maximum_bytes);
                    }
                    Err(frame) => {
                        self.ended = true;
                        return Ok(ReadFrame::End(frame.into_trailers().ok()));
                    }
                },
            }
        }
        Err(StreamError::ResourceLimit)
    }
    fn split(
        &mut self,
        buffer: OwnedBuffer,
        maximum_bytes: usize,
    ) -> Result<ReadFrame, StreamError> {
        if buffer.len() <= maximum_bytes {
            return Ok(ReadFrame::Data(buffer));
        }
        let output = buffer.slice(0, maximum_bytes)?;
        self.pending = Some(buffer.slice(maximum_bytes, buffer.len() - maximum_bytes)?);
        Ok(ReadFrame::Data(output))
    }
    pub const fn end_observed(&self) -> bool {
        self.ended
    }
}
impl BoundedBody {
    /// Stops new ordinary sends; previously issued permits remain real obligations.
    pub fn close(&mut self) {
        self.receiver.close();
    }
}
impl Body for BoundedBody {
    type Data = Bytes;
    type Error = StreamError;
    fn poll_frame(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
    ) -> Poll<Option<Result<Frame<Bytes>, StreamError>>> {
        if self.ended {
            return Poll::Ready(None);
        }
        match self.receiver.poll_recv(cx) {
            Poll::Pending => Poll::Pending,
            Poll::Ready(Some(Message::Data(frame))) => Poll::Ready(Some(Ok(frame))),
            Poll::Ready(Some(Message::Error(error))) => {
                self.ended = true;
                self.receiver.close();
                Poll::Ready(Some(Err(error)))
            }
            Poll::Ready(Some(Message::End) | None) => {
                self.ended = true;
                self.receiver.close();
                Poll::Ready(None)
            }
        }
    }
    fn is_end_stream(&self) -> bool {
        self.ended
    }
    fn size_hint(&self) -> SizeHint {
        SizeHint::default()
    }
}

fn lock<T>(gate: &Mutex<T>) -> MutexGuard<'_, T> {
    gate.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

#[cfg(feature = "conformance")]
mod fixture;
#[cfg(test)]
mod tests;
