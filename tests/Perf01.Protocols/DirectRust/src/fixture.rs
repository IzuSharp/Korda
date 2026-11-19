//! Typed codec for the exact five-field perf01.proto fixture. This does not use
//! NativeWeave, copy its codec, or add a protobuf dependency absent from the lock.
use bytes::{Buf, BufMut, Bytes};
use std::sync::Arc;
use tonic::{Status, codec::{BufferSettings, Codec, DecodeBuf, Decoder, EncodeBuf, Encoder}};
use crate::{Lease, Retained};

#[derive(Clone, Default)]
pub struct Payload {
    pub sequence: i64,
    pub data: Bytes,
    pub count: i32,
    pub digest: String,
    pub total_bytes: i64,
}
pub struct FixtureCodec {
    pub lease: Arc<Lease>,
    pub maximum: usize,
    pub stream_maximum: usize,
    pub single_input: bool,
}
impl Codec for FixtureCodec {
    type Encode = Payload;
    type Decode = Payload;
    type Encoder = FixtureEncoder;
    type Decoder = FixtureDecoder;
    fn encoder(&mut self) -> Self::Encoder { FixtureEncoder { total: 0, maximum: self.maximum, stream_maximum: self.stream_maximum } }
    fn decoder(&mut self) -> Self::Decoder {
        FixtureDecoder { lease: self.lease.clone(), total: 0, seen: 0, maximum: self.maximum,
            stream_maximum: self.stream_maximum, single_input: self.single_input }
    }
}
pub struct FixtureDecoder {
    lease: Arc<Lease>, total: usize, seen: usize, maximum: usize, stream_maximum: usize, single_input: bool,
}
impl Decoder for FixtureDecoder {
    type Item = Payload;
    type Error = Status;
    fn buffer_settings(&self) -> BufferSettings { BufferSettings::new(8192, 1) }
    fn decode(&mut self, source: &mut DecodeBuf<'_>) -> Result<Option<Payload>, Status> {
        let length = source.remaining();
        if self.single_input && self.seen != 0 { return Err(Status::invalid_argument("multiple unary messages")); }
        self.total = self.total.checked_add(length.max(1)).ok_or_else(|| Status::resource_exhausted("stream overflow"))?;
        if length > self.maximum || self.total > self.stream_maximum { return Err(Status::resource_exhausted("message/stream limit")); }
        let grant = self.lease.reserve(length.max(1))?;
        let mut storage = vec![0; length]; source.copy_to_slice(&mut storage);
        let bytes = Bytes::from_owner(Retained { data: Bytes::from(storage), _grant: grant, _lease: self.lease.clone() });
        self.seen += 1;
        decode(bytes).map(Some)
    }
}
pub struct FixtureEncoder { total: usize, maximum: usize, stream_maximum: usize }
impl Encoder for FixtureEncoder {
    type Item = Payload;
    type Error = Status;
    fn buffer_settings(&self) -> BufferSettings { BufferSettings::new(8192, 1) }
    fn encode(&mut self, value: Payload, destination: &mut EncodeBuf<'_>) -> Result<(), Status> {
        let size = encoded_len(&value);
        self.total = self.total.checked_add(size.max(1)).ok_or_else(|| Status::resource_exhausted("stream overflow"))?;
        if size > self.maximum || self.total > self.stream_maximum { return Err(Status::resource_exhausted("message/stream limit")); }
        if value.sequence != 0 { destination.put_u8(8); varint(value.sequence as u64, destination); }
        if !value.data.is_empty() { destination.put_u8(18); varint(value.data.len() as u64, destination); destination.put_slice(&value.data); }
        if value.count != 0 { destination.put_u8(24); varint(value.count as u64, destination); }
        if !value.digest.is_empty() { destination.put_u8(34); varint(value.digest.len() as u64, destination); destination.put_slice(value.digest.as_bytes()); }
        if value.total_bytes != 0 { destination.put_u8(40); varint(value.total_bytes as u64, destination); }
        Ok(())
    }
}
fn varint(mut value: u64, output: &mut impl BufMut) {
    while value >= 128 { output.put_u8((value as u8) | 128); value >>= 7; }
    output.put_u8(value as u8);
}
fn integer_len(value: u64) -> usize { ((64 - value.leading_zeros()).max(1) as usize).div_ceil(7) }
fn encoded_len(value: &Payload) -> usize {
    (if value.sequence != 0 { 1 + integer_len(value.sequence as u64) } else { 0 })
    + (if !value.data.is_empty() { 1 + integer_len(value.data.len() as u64) + value.data.len() } else { 0 })
    + (if value.count != 0 { 1 + integer_len(value.count as u64) } else { 0 })
    + (if !value.digest.is_empty() { 1 + integer_len(value.digest.len() as u64) + value.digest.len() } else { 0 })
    + (if value.total_bytes != 0 { 1 + integer_len(value.total_bytes as u64) } else { 0 })
}
fn take_varint(bytes: &[u8], position: &mut usize) -> Result<u64, Status> {
    let mut result = 0;
    for offset in 0..10 {
        let byte = *bytes.get(*position).ok_or_else(|| Status::invalid_argument("truncated protobuf varint"))?;
        *position += 1;
        if offset == 9 && byte > 1 { return Err(Status::invalid_argument("overflow protobuf varint")); }
        result |= ((byte & 127) as u64) << (7 * offset);
        if byte & 128 == 0 { return Ok(result); }
    }
    Err(Status::invalid_argument("overflow protobuf varint"))
}
fn take_bytes(bytes: &Bytes, position: &mut usize) -> Result<Bytes, Status> {
    let length: usize = take_varint(bytes, position)?.try_into().map_err(|_| Status::invalid_argument("protobuf length"))?;
    let end = position.checked_add(length).filter(|end| *end <= bytes.len()).ok_or_else(|| Status::invalid_argument("truncated protobuf bytes"))?;
    let value = bytes.slice(*position..end); *position = end; Ok(value)
}
fn decode(bytes: Bytes) -> Result<Payload, Status> {
    let mut value = Payload::default(); let mut position = 0;
    while position < bytes.len() {
        let tag = take_varint(&bytes, &mut position)?;
        if tag >> 3 == 0 || tag >> 3 > 536870911 { return Err(Status::invalid_argument("protobuf tag")); }
        match tag {
            8 => value.sequence = take_varint(&bytes, &mut position)? as i64,
            18 => value.data = take_bytes(&bytes, &mut position)?,
            24 => value.count = take_varint(&bytes, &mut position)? as i32,
            34 => value.digest = std::str::from_utf8(&take_bytes(&bytes, &mut position)?).map_err(|_| Status::invalid_argument("protobuf UTF8"))?.to_owned(),
            40 => value.total_bytes = take_varint(&bytes, &mut position)? as i64,
            _ => match tag & 7 {
                0 => { take_varint(&bytes, &mut position)?; }
                1 => position = position.checked_add(8).filter(|end| *end <= bytes.len()).ok_or_else(|| Status::invalid_argument("protobuf fixed64"))?,
                2 => { take_bytes(&bytes, &mut position)?; }
                5 => position = position.checked_add(4).filter(|end| *end <= bytes.len()).ok_or_else(|| Status::invalid_argument("protobuf fixed32"))?,
                _ => return Err(Status::invalid_argument("fixture codec does not support protobuf groups")),
            },
        }
    }
    Ok(value)
}
