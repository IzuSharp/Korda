//! Tonic-only framing with explicitly granted codec and escaping message backing.
use crate::{Descriptor, MethodKind, quota};
use bytes::{Buf, BufMut, Bytes};
use http_body::{Body, Frame, SizeHint};
use nativeweave_server_streaming::{Credit, RequestBudget, Side};
use std::{
    pin::Pin,
    sync::{Arc, Mutex},
    task::{Context, Poll},
};
use tonic::{
    Status,
    codec::{BufferSettings, Codec, DecodeBuf, Decoder, EncodeBuf, Encoder},
};

pub struct MessageBuffer {
    pub bytes: Bytes,
    pub budget: Arc<RequestBudget>,
}
impl MessageBuffer {
    pub fn as_slice(&self) -> &[u8] {
        &self.bytes
    }
    pub fn len(&self) -> usize {
        self.bytes.len()
    }
    pub fn is_empty(&self) -> bool {
        self.bytes.is_empty()
    }
    pub fn acquire_managed(&self, capacity: usize) -> Result<Credit, Status> {
        self.budget
            .try_acquire(Side::Managed, capacity as u64)
            .map_err(|_| quota())
    }
}
pub(crate) struct Allocation {
    pub storage: Box<[u8]>,
    pub _credit: Arc<Credit>,
}
impl AsRef<[u8]> for Allocation {
    fn as_ref(&self) -> &[u8] {
        &self.storage
    }
}
#[derive(Default)]
pub(crate) struct FrameCredits(pub Mutex<Option<Arc<Credit>>>);
pub(crate) struct CodecMemory {
    _credit: Credit,
    decode_capacity: usize,
    encode_capacity: usize,
}
pub struct RawCodec {
    descriptor: Arc<Descriptor>,
    budget: Arc<RequestBudget>,
    pub(crate) memory: Arc<CodecMemory>,
    frames: Arc<FrameCredits>,
}
impl RawCodec {
    pub(crate) fn new(
        descriptor: Arc<Descriptor>,
        budget: Arc<RequestBudget>,
        chunk: usize,
        frames: Arc<FrameCredits>,
    ) -> Result<Self, Status> {
        let maximum = descriptor.max_encoded_message_bytes as usize;
        let decode_capacity = maximum
            .checked_add(chunk.checked_mul(2).ok_or_else(quota)?)
            .and_then(|v| v.checked_add(5))
            .ok_or_else(quota)?;
        let encode_capacity = maximum.checked_add(5).ok_or_else(quota)?;
        let credit = budget
            .try_acquire(
                Side::Native,
                decode_capacity
                    .checked_add(encode_capacity)
                    .ok_or_else(quota)? as u64,
            )
            .map_err(|_| quota())?;
        Ok(Self {
            descriptor,
            budget,
            memory: Arc::new(CodecMemory {
                _credit: credit,
                decode_capacity,
                encode_capacity,
            }),
            frames,
        })
    }
}
impl Codec for RawCodec {
    type Encode = Bytes;
    type Decode = Bytes;
    type Encoder = RawEncoder;
    type Decoder = RawDecoder;
    fn encoder(&mut self) -> Self::Encoder {
        RawEncoder {
            memory: self.memory.clone(),
            frames: self.frames.clone(),
        }
    }
    fn decoder(&mut self) -> Self::Decoder {
        // This allowance is fresh for this concrete incoming call, never shared by
        // a method descriptor, connection or server registry.
        RawDecoder {
            kind: self.descriptor.method_kind,
            remaining: 1,
            total: 0,
            maximum: self.descriptor.max_decoded_message_bytes as usize,
            stream: self.descriptor.max_stream_bytes,
            budget: self.budget.clone(),
            memory: self.memory.clone(),
        }
    }
}
pub struct RawDecoder {
    kind: MethodKind,
    remaining: u8,
    total: u64,
    maximum: usize,
    stream: u64,
    budget: Arc<RequestBudget>,
    memory: Arc<CodecMemory>,
}
impl Decoder for RawDecoder {
    type Item = Bytes;
    type Error = Status;
    fn buffer_settings(&self) -> BufferSettings {
        BufferSettings::new(self.memory.decode_capacity, 1)
    }
    fn decode(&mut self, src: &mut DecodeBuf<'_>) -> Result<Option<Bytes>, Status> {
        if self.kind.single_input() && self.remaining == 0 {
            return Err(Status::invalid_argument("multiple unary messages"));
        }
        let length = src.remaining();
        if length > self.maximum {
            return Err(Status::resource_exhausted("decoded message limit"));
        }
        // Empty protobuf messages count as one unit, preventing an infinite zero-byte stream.
        self.total = self
            .total
            .checked_add(length.max(1) as u64)
            .filter(|v| *v <= self.stream)
            .ok_or_else(quota)?;
        let credit = self
            .budget
            .try_acquire(Side::Native, length.max(1) as u64)
            .map_err(|_| quota())?;
        let mut storage = vec![0; length].into_boxed_slice();
        src.copy_to_slice(&mut storage);
        self.remaining = 0;
        Ok(Some(Bytes::from_owner(Allocation {
            storage,
            _credit: Arc::new(credit),
        })))
    }
}
pub struct RawEncoder {
    memory: Arc<CodecMemory>,
    frames: Arc<FrameCredits>,
}
impl Encoder for RawEncoder {
    type Item = Bytes;
    type Error = Status;
    fn buffer_settings(&self) -> BufferSettings {
        BufferSettings::new(self.memory.encode_capacity, 1)
    }
    fn encode(&mut self, item: Bytes, dst: &mut EncodeBuf<'_>) -> Result<(), Status> {
        if crate::lock(&self.frames.0).is_none() {
            return Err(Status::internal("unreserved native message"));
        }
        if item.len() + 5 > self.memory.encode_capacity {
            return Err(Status::out_of_range("encoded message limit"));
        }
        dst.put_slice(&item);
        // No native payload alias is exported. Dropping item frees its exact Box;
        // the grant moves through FrameCredits to the eventual escaping framed copy.
        drop(item);
        Ok(())
    }
}

/// No Tonic BytesMut alias crosses this boundary. A fixed eager codec buffer is
/// reused only after the previous Tonic frame was copied and dropped. Each frame
/// gets the message's already-reserved grant, including its five framing bytes.
pub(crate) struct CreditedBody<B> {
    pub inner: B,
    pub frames: Arc<FrameCredits>,
    // Declared after inner: Tonic's encoder drops before its BytesMut fields, so
    // this outer guard keeps the shared grant until every backing is destroyed.
    pub _memory: Arc<CodecMemory>,
}
impl<B: Body<Data = Bytes> + Unpin> Body for CreditedBody<B> {
    type Data = Bytes;
    type Error = B::Error;
    fn poll_frame(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
    ) -> Poll<Option<Result<Frame<Bytes>, B::Error>>> {
        match Pin::new(&mut self.inner).poll_frame(cx) {
            Poll::Ready(Some(Ok(frame))) => match frame.into_data() {
                Ok(data) => {
                    let credit = crate::lock(&self.frames.0)
                        .take()
                        .expect("one Tonic frame per reserved message");
                    assert!(
                        data.len() as u64 <= credit.capacity(),
                        "Tonic frame exceeded its pregrant"
                    );
                    let storage = data.as_ref().to_vec().into_boxed_slice();
                    drop(data);
                    Poll::Ready(Some(Ok(Frame::data(Bytes::from_owner(Allocation {
                        storage,
                        _credit: credit,
                    })))))
                }
                Err(frame) => Poll::Ready(Some(Ok(frame))),
            },
            result => result,
        }
    }
    fn is_end_stream(&self) -> bool {
        self.inner.is_end_stream()
    }
    fn size_hint(&self) -> SizeHint {
        self.inner.size_hint()
    }
}

/// Bounds each delivery to Tonic without parsing any gRPC frame. The parent Body
/// retains ownership of its transport allocation; only one data frame is pending.
pub(crate) struct ChunkBody<B> {
    pub inner: B,
    pub pending: Option<Bytes>,
    pub maximum: usize,
}
impl<B: Body<Data = Bytes> + Unpin> Body for ChunkBody<B> {
    type Data = Bytes;
    type Error = B::Error;
    fn poll_frame(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
    ) -> Poll<Option<Result<Frame<Bytes>, B::Error>>> {
        loop {
            if let Some(mut bytes) = self.pending.take() {
                let amount = bytes.len().min(self.maximum);
                let next = bytes.split_to(amount);
                if !bytes.is_empty() {
                    self.pending = Some(bytes);
                }
                if !next.is_empty() {
                    return Poll::Ready(Some(Ok(Frame::data(next))));
                }
            }
            match Pin::new(&mut self.inner).poll_frame(cx) {
                Poll::Ready(Some(Ok(frame))) => match frame.into_data() {
                    Ok(bytes) => self.pending = Some(bytes),
                    Err(frame) => return Poll::Ready(Some(Ok(frame))),
                },
                result => return result,
            }
        }
    }
    fn is_end_stream(&self) -> bool {
        self.pending.is_none() && self.inner.is_end_stream()
    }
}
