//! Bounded complete-message bridge. It neither starts a runtime nor performs FFI.
use crate::{
    Descriptor, MessageBuffer,
    codec::{Allocation, CodecMemory, FrameCredits},
    metadata::ResponseMetadata,
    quota,
};
use bytes::Bytes;
use futures_core::Stream;
use nativeweave_server_streaming::{Cancellation, Credit, RequestBudget, Side};
use std::{
    pin::Pin,
    sync::{
        Arc, Mutex,
        atomic::{AtomicU32, AtomicU64, Ordering},
    },
    task::{Context, Poll},
};
use tokio::sync::{Notify, mpsc};
use tonic::{Code, Status, Streaming, metadata::MetadataMap};

pub enum Inbound {
    One(Option<Bytes>),
    Stream(Box<StreamInput>),
    End,
}
/// An input owner whose codec grant drops after Tonic's complete Streaming value.
pub struct StreamInput {
    inner: Streaming<Bytes>,
    // Tonic drops its decoder before its internal BytesMut. This outer field is
    // destroyed after inner, preserving the grant through that destructor order.
    _memory: Arc<CodecMemory>,
}
impl Inbound {
    pub(crate) fn stream(inner: Streaming<Bytes>, memory: Arc<CodecMemory>) -> Self {
        Self::Stream(Box::new(StreamInput {
            inner,
            _memory: memory,
        }))
    }
}
pub struct Call {
    pub descriptor: Arc<Descriptor>,
    pub budget: Arc<RequestBudget>,
    input: tokio::sync::Mutex<Inbound>,
    stopped: Cancellation,
    output: mpsc::Sender<Outbound>,
    receiver: Mutex<Option<mpsc::Receiver<Outbound>>>,
    phase: AtomicU32,
    initial: Mutex<Option<ResponseMetadata>>,
    ready: Notify,
    terminal: Mutex<Option<Terminal>>,
    outbound_bytes: AtomicU64,
    outbound_messages: AtomicU64,
    pub(crate) frames: Arc<FrameCredits>,
}
pub(crate) struct Item {
    bytes: Bytes,
    credit: Arc<Credit>,
}
pub(crate) enum Outbound {
    Data(Item),
    Terminal,
}
pub struct Terminal {
    pub status: Status,
    _metadata: ResponseMetadata,
}
pub struct Reservation {
    permit: mpsc::OwnedPermit<Outbound>,
    credit: Credit,
    capacity: usize,
}
pub struct Prepared {
    permit: mpsc::OwnedPermit<Outbound>,
    item: Item,
    length: usize,
}
impl Reservation {
    pub fn capacity(&self) -> usize {
        self.capacity
    }
    pub fn prepare(self, data: &[u8]) -> Prepared {
        assert!(
            data.len() <= self.capacity,
            "validated synchronous create length"
        );
        let credit = Arc::new(self.credit);
        let bytes = Bytes::from_owner(Allocation {
            storage: data.to_vec().into_boxed_slice(),
            _credit: credit.clone(),
        });
        Prepared {
            permit: self.permit,
            item: Item { bytes, credit },
            length: data.len(),
        }
    }
}
impl Call {
    pub fn new(descriptor: Arc<Descriptor>, budget: Arc<RequestBudget>) -> Arc<Self> {
        let (output, receiver) = mpsc::channel(1);
        Arc::new(Self {
            descriptor,
            budget,
            input: tokio::sync::Mutex::new(Inbound::End),
            stopped: Cancellation::default(),
            output,
            receiver: Mutex::new(Some(receiver)),
            phase: AtomicU32::new(0),
            initial: Mutex::new(None),
            ready: Notify::new(),
            terminal: Mutex::new(None),
            outbound_bytes: AtomicU64::new(0),
            outbound_messages: AtomicU64::new(0),
            frames: Arc::default(),
        })
    }
    pub async fn set_input(&self, input: Inbound) {
        *self.input.lock().await = input;
    }
    pub fn stop_input(&self) {
        self.stopped.cancel();
        if let Ok(mut input) = self.input.try_lock() {
            *input = Inbound::End;
        }
    }
    pub async fn retire_input(&self) {
        self.stop_input();
        *self.input.lock().await = Inbound::End;
    }
    pub async fn read(&self) -> Result<Option<MessageBuffer>, Status> {
        let mut input = self.input.lock().await;
        if self.stopped.is_canceled() {
            *input = Inbound::End;
            return Err(Status::cancelled("inbound stopped"));
        }
        let next = match &mut *input {
            Inbound::One(value) => Ok(value.take()),
            Inbound::Stream(stream) => {
                tokio::select! { biased; ()=self.stopped.cancelled()=>Err(Status::cancelled("inbound stopped")), value=stream.inner.message()=>value }
            }
            Inbound::End => Ok(None),
        };
        if !matches!(next, Ok(Some(_))) {
            *input = Inbound::End;
        }
        Ok(next?.map(|bytes| MessageBuffer {
            bytes,
            budget: self.budget.clone(),
        }))
    }
    pub fn is_terminal(&self) -> bool {
        self.phase.load(Ordering::Acquire) == 2
    }
    pub fn headers(&self, metadata: ResponseMetadata) -> Result<(), Status> {
        let mut initial = crate::lock(&self.initial);
        if self.phase.load(Ordering::Acquire) != 0 {
            return Err(Status::failed_precondition("headers committed"));
        }
        *initial = Some(metadata);
        self.phase.store(1, Ordering::Release);
        drop(initial);
        self.ready.notify_waiters();
        Ok(())
    }
    fn ensure_headers(&self) -> Result<(), Status> {
        if self.phase.load(Ordering::Acquire) == 0 {
            self.headers(ResponseMetadata::empty(&self.budget)?)?;
        }
        Ok(())
    }
    pub async fn reserve(
        &self,
        capacity: usize,
        cancel: &Cancellation,
    ) -> Result<Reservation, Status> {
        if capacity == 0 || capacity > self.descriptor.max_encoded_message_bytes as usize {
            return Err(Status::invalid_argument("message capacity"));
        }
        if self.is_terminal() || self.budget.is_closed() {
            return Err(Status::failed_precondition("terminal call"));
        }
        let permit = tokio::select! { biased; ()=cancel.cancelled()=>return Err(Status::cancelled("write canceled")), value=self.output.clone().reserve_owned()=>value.map_err(|_| Status::cancelled("response closed"))? };
        if self.is_terminal() || self.budget.is_closed() || cancel.is_canceled() {
            return Err(Status::cancelled("write closed"));
        }
        // Codec scratch is already covered before Grpc constructs either buffer.
        // Message backing and the eventual framed copy use this grant in sequence.
        let credit = self
            .budget
            .try_acquire(Side::Native, (capacity + 5) as u64)
            .map_err(|_| quota())?;
        Ok(Reservation {
            permit,
            credit,
            capacity,
        })
    }
    pub fn write(&self, prepared: Prepared) -> Result<(), Status> {
        if self.is_terminal() || self.budget.is_closed() {
            return Err(Status::failed_precondition("terminal call"));
        }
        let messages = self.outbound_messages.load(Ordering::Acquire);
        if self.descriptor.method_kind.single_output() && messages != 0 {
            return Err(Status::internal("multiple unary responses"));
        }
        let bytes = self
            .outbound_bytes
            .load(Ordering::Acquire)
            .checked_add(prepared.length.max(1) as u64)
            .filter(|v| *v <= self.descriptor.max_stream_bytes)
            .ok_or_else(quota)?;
        self.ensure_headers()?;
        self.outbound_bytes.store(bytes, Ordering::Release);
        self.outbound_messages
            .store(messages + 1, Ordering::Release);
        prepared.permit.send(Outbound::Data(prepared.item));
        Ok(())
    }
    pub async fn finish(
        &self,
        status: Status,
        metadata: ResponseMetadata,
        cancel: &Cancellation,
    ) -> Result<(), Status> {
        if self.is_terminal() {
            return Err(Status::failed_precondition("status committed"));
        }
        // Only queue metadata waits. Commit the terminal winner after its slot
        // exists, so cancellation during admission cannot leave a fake terminal.
        let permit = tokio::select! { biased; ()=cancel.cancelled()=>return Err(Status::cancelled("terminal canceled")),
        result=self.output.clone().reserve_owned()=>result.map_err(|_|Status::cancelled("response closed"))? };
        if cancel.is_canceled() {
            return Err(Status::cancelled("terminal canceled"));
        }
        self.ensure_headers()?;
        if self
            .phase
            .compare_exchange(1, 2, Ordering::AcqRel, Ordering::Acquire)
            .is_err()
        {
            return Err(Status::failed_precondition("status committed"));
        }
        *crate::lock(&self.terminal) = Some(Terminal {
            status,
            _metadata: metadata,
        });
        permit.send(Outbound::Terminal);
        Ok(())
    }
    async fn headers_ready(&self) -> MetadataMap {
        loop {
            let notified = self.ready.notified();
            if self.phase.load(Ordering::Acquire) != 0 {
                return crate::lock(&self.initial)
                    .as_ref()
                    .map(|v| v.values.clone())
                    .unwrap_or_default();
            }
            notified.await;
        }
    }
    pub(crate) async fn unary_response(&self) -> Result<tonic::Response<Bytes>, Status> {
        let mut receiver = crate::lock(&self.receiver)
            .take()
            .ok_or_else(|| Status::internal("response taken"))?;
        let mut item = None;
        while let Some(value) = receiver.recv().await {
            match value {
                Outbound::Data(value) => {
                    if item.replace(value).is_some() {
                        return Err(Status::internal("multiple unary responses"));
                    }
                }
                Outbound::Terminal => break,
            }
        }
        let status = crate::lock(&self.terminal)
            .as_ref()
            .map(|v| v.status.clone())
            .unwrap_or_else(|| Status::cancelled("response closed"));
        if status.code() != Code::Ok {
            return Err(status);
        }
        let item = item.ok_or_else(|| Status::internal("missing response message"))?;
        *crate::lock(&self.frames.0) = Some(item.credit);
        let mut response = tonic::Response::new(item.bytes);
        *response.metadata_mut() = self.headers_ready().await;
        Ok(response)
    }
    pub(crate) async fn stream_response(
        self: &Arc<Self>,
    ) -> Result<tonic::Response<Output>, Status> {
        let metadata = self.headers_ready().await;
        let receiver = crate::lock(&self.receiver)
            .take()
            .ok_or_else(|| Status::internal("response taken"))?;
        let mut response = tonic::Response::new(Output {
            receiver,
            call: self.clone(),
            ended: false,
        });
        *response.metadata_mut() = metadata;
        Ok(response)
    }
    pub fn initial_credit(&self) -> Option<Arc<Credit>> {
        crate::lock(&self.initial)
            .as_ref()
            .map(ResponseMetadata::credit)
    }
    pub fn terminal_credit(&self) -> Option<Arc<Credit>> {
        crate::lock(&self.terminal)
            .as_ref()
            .map(|v| v._metadata.credit())
    }
    pub fn release_metadata(&self) {
        crate::lock(&self.receiver).take();
        crate::lock(&self.initial).take();
        crate::lock(&self.terminal).take();
        crate::lock(&self.frames.0).take();
    }
    pub fn terminal_status(&self) -> Option<Status> {
        crate::lock(&self.terminal)
            .as_ref()
            .map(|v| v.status.clone())
    }
}
pub struct Output {
    receiver: mpsc::Receiver<Outbound>,
    call: Arc<Call>,
    ended: bool,
}
impl Stream for Output {
    type Item = Result<Bytes, Status>;
    fn poll_next(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Option<Self::Item>> {
        if self.ended {
            return Poll::Ready(None);
        }
        match self.receiver.poll_recv(cx) {
            Poll::Ready(Some(Outbound::Data(value))) => {
                let previous = crate::lock(&self.call.frames.0).replace(value.credit);
                assert!(previous.is_none(), "one Tonic item per frame");
                Poll::Ready(Some(Ok(value.bytes)))
            }
            Poll::Ready(Some(Outbound::Terminal)) => {
                self.ended = true;
                Poll::Ready(Some(Err(self
                    .call
                    .terminal_status()
                    .unwrap_or_else(|| Status::internal("missing terminal status")))))
            }
            Poll::Ready(None) => {
                self.ended = true;
                Poll::Ready(Some(Err(Status::cancelled("output closed"))))
            }
            Poll::Pending => Poll::Pending,
        }
    }
}
