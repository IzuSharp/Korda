//! Bounded immutable binary projection; HTTP wire text remains a separate block.
use http::{HeaderMap, HeaderName, HeaderValue};
use nativeweave_server_streaming::{Credit, RequestBudget, Side};
use std::sync::Arc;
use tonic::{
    Status,
    metadata::{Ascii, Binary, MetadataKey, MetadataMap, MetadataValue},
};

const SEGMENT_CREDIT_BYTES: usize = 48;
// Pinned http 1.5 bucket fields plus maximal natural-alignment padding fit 128;
// extra-value links fit 96. These bounds support the documented map peak.
const _: () = assert!(
    size_of::<HeaderName>()
        + size_of::<HeaderValue>()
        + size_of::<Option<(usize, usize)>>()
        + size_of::<u16>()
        + 3 * align_of::<usize>()
        <= 128
);
const _: () = assert!(size_of::<HeaderValue>() + 4 * size_of::<usize>() <= 96);
const _: () = assert!(size_of::<Option<Segment<'static>>>() <= SEGMENT_CREDIT_BYTES);
pub fn minimum_capacity(maximum: usize) -> Option<usize> {
    maximum
        .checked_mul(2)?
        .checked_add(200 * SEGMENT_CREDIT_BYTES + 2048)
}

pub struct RequestMetadata {
    pub decoded: Vec<u8>,
    // Also covers the sibling original HTTP block, fixed borrowed descriptors,
    // and one temporary HeaderMap/decoded segment plus its copy overlap.
    _credit: Arc<Credit>,
}
impl RequestMetadata {
    pub fn reserved_capacity(&self) -> u64 {
        self._credit.capacity()
    }
    pub fn credit(&self) -> Arc<Credit> {
        self._credit.clone()
    }
    pub fn unshared(&self) -> bool {
        Arc::strong_count(&self._credit) == 1
    }
}
#[derive(Clone, Copy)]
struct Segment<'a> {
    name: &'a str,
    data: &'a [u8],
    binary: bool,
}

pub fn project(
    headers: &HeaderMap,
    maximum: usize,
    budget: &Arc<RequestBudget>,
) -> Result<RequestMetadata, Status> {
    let mut segments = [None; 200];
    let mut count = 0usize;
    let mut decoded_bound = 4usize;
    let mut wire_size = 4usize;
    let mut max_segment = 0usize;
    for (name, value) in headers {
        let name = name.as_str();
        wire_size = wire_size
            .checked_add(8 + name.len() + value.as_bytes().len())
            .ok_or_else(limit)?;
        let binary = name.ends_with("-bin");
        if binary {
            for part in value.as_bytes().split(|b| *b == b',') {
                let part = trim_ows(part);
                if part
                    .iter()
                    .any(|b| !b.is_ascii_alphanumeric() && !b"+/=".contains(b))
                {
                    return Err(Status::invalid_argument("binary metadata alphabet"));
                }
                let padding = part.iter().rev().take_while(|b| **b == b'=').count();
                if padding > 2
                    || (padding != 0 && !part.len().is_multiple_of(4))
                    || part[..part.len() - padding].contains(&b'=')
                    || part.len() % 4 == 1
                {
                    return Err(Status::invalid_argument("binary metadata padding"));
                }
                append(
                    &mut segments,
                    &mut count,
                    &mut decoded_bound,
                    Segment {
                        name,
                        data: part,
                        binary,
                    },
                    maximum,
                )?;
                max_segment = max_segment.max(part.len());
            }
        } else {
            if !value.as_bytes().is_ascii()
                || value.as_bytes().iter().any(|b| *b < 0x20 || *b > 0x7e)
            {
                return Err(Status::invalid_argument("ASCII metadata"));
            }
            append(
                &mut segments,
                &mut count,
                &mut decoded_bound,
                Segment {
                    name,
                    data: value.as_bytes(),
                    binary,
                },
                maximum,
            )?;
        }
    }
    if wire_size > maximum {
        return Err(limit());
    }
    // No per-segment string/vector/decoder exists until the complete split count
    // and both block upper bounds have passed. Scratch includes source HeaderValue
    // backing, decoded Bytes, exact block copy, and bounded map bookkeeping.
    let capacity = minimum_capacity(maximum)
        .and_then(|v| v.checked_add(max_segment.checked_mul(3)?))
        .ok_or_else(limit)?;
    let credit = budget
        .try_acquire(Side::Native, capacity as u64)
        .map_err(|_| limit())?;
    let mut decoded = Vec::with_capacity(maximum);
    decoded.extend_from_slice(&(count as u32).to_le_bytes());
    for segment in segments.into_iter().flatten() {
        if segment.binary {
            let mut single = HeaderMap::with_capacity(1);
            single.insert(
                HeaderName::from_bytes(segment.name.as_bytes()).map_err(|_| invalid())?,
                HeaderValue::from_bytes(segment.data).map_err(|_| invalid())?,
            );
            let map = MetadataMap::from_headers(single);
            let value = map
                .get_bin(segment.name)
                .ok_or_else(invalid)?
                .to_bytes()
                .map_err(|_| invalid())?;
            entry(&mut decoded, segment.name, &value, maximum)?;
        } else {
            entry(&mut decoded, segment.name, segment.data, maximum)?;
        }
    }
    Ok(RequestMetadata {
        decoded,
        _credit: Arc::new(credit),
    })
}
fn append<'a>(
    segments: &mut [Option<Segment<'a>>; 200],
    count: &mut usize,
    size: &mut usize,
    segment: Segment<'a>,
    maximum: usize,
) -> Result<(), Status> {
    if *count == segments.len() {
        return Err(limit());
    }
    // Validate the exact decoded upper bound before allocation; malformed padding
    // still goes through Tonic and cannot publish a partial block.
    let payload = if segment.binary {
        let padded = segment
            .data
            .iter()
            .rev()
            .take_while(|b| **b == b'=')
            .count();
        (segment.data.len() * 3 / 4).saturating_sub(padded)
    } else {
        segment.data.len()
    };
    *size = size
        .checked_add(8 + segment.name.len() + payload)
        .ok_or_else(limit)?;
    if *size > maximum {
        return Err(limit());
    }
    segments[*count] = Some(segment);
    *count += 1;
    Ok(())
}
fn trim_ows(mut data: &[u8]) -> &[u8] {
    while data.first().is_some_and(|b| matches!(b, b' ' | b'\t')) {
        data = &data[1..];
    }
    while data.last().is_some_and(|b| matches!(b, b' ' | b'\t')) {
        data = &data[..data.len() - 1];
    }
    data
}
fn entry(output: &mut Vec<u8>, name: &str, value: &[u8], maximum: usize) -> Result<(), Status> {
    if output
        .len()
        .checked_add(8 + name.len() + value.len())
        .is_none_or(|v| v > maximum)
    {
        return Err(limit());
    }
    output.extend_from_slice(&(name.len() as u32).to_le_bytes());
    output.extend_from_slice(&(value.len() as u32).to_le_bytes());
    output.extend_from_slice(name.as_bytes());
    output.extend_from_slice(value);
    Ok(())
}
pub struct ResponseMetadata {
    pub values: MetadataMap,
    _credit: Arc<Credit>,
}
impl ResponseMetadata {
    pub fn credit(&self) -> Arc<Credit> {
        self._credit.clone()
    }
    pub fn empty(budget: &Arc<RequestBudget>) -> Result<Self, Status> {
        Ok(Self {
            values: MetadataMap::new(),
            _credit: Arc::new(
                budget
                    .try_acquire(Side::Native, 1024)
                    .map_err(|_| limit())?,
            ),
        })
    }
    pub fn parse(
        data: &[u8],
        maximum: usize,
        budget: &Arc<RequestBudget>,
        trailers: bool,
        detail_bytes: usize,
    ) -> Result<Self, Status> {
        if data.len() < 4 || data.len() > maximum {
            return Err(invalid());
        }
        let read = |i: usize| -> Result<usize, Status> {
            let value = data.get(i..i + 4).ok_or_else(invalid)?;
            Ok(u32::from_le_bytes(value.try_into().expect("four bytes")) as usize)
        };
        let count = read(0)?;
        if count > 200 {
            return Err(limit());
        }
        // Eight times the bounded source covers base64 growth, HeaderMap value
        // copies and Status/response ownership overlap. The per-entry allowance
        // covers bounded map tables, retained value owners, and up to five live
        // map projections, including collision-driven table growth; none of these allocations may await payload credit.
        let capacity = data
            .len()
            .checked_mul(8)
            .and_then(|v| v.checked_add(count * 4096))
            .and_then(|v| v.checked_add(detail_bytes.checked_mul(16)?))
            .and_then(|v| v.checked_add(1024))
            .ok_or_else(limit)?;
        let credit = budget
            .try_acquire(Side::Native, capacity as u64)
            .map_err(|_| limit())?;
        let mut values = MetadataMap::with_capacity(count);
        let mut offset = 4usize;
        for _ in 0..count {
            let nl = read(offset)?;
            let vl = read(offset + 4)?;
            offset += 8;
            let end = offset
                .checked_add(nl)
                .and_then(|v| v.checked_add(vl))
                .filter(|v| *v <= data.len())
                .ok_or_else(invalid)?;
            let name = std::str::from_utf8(&data[offset..offset + nl]).map_err(|_| invalid())?;
            let value = &data[offset + nl..end];
            offset = end;
            if name.is_empty()
                || name.len() > 256
                || !name
                    .bytes()
                    .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b"-_.".contains(&b))
                || name.starts_with("grpc-")
                || matches!(
                    name,
                    "content-type" | "content-length" | "te" | "connection" | "transfer-encoding"
                )
            {
                return Err(invalid());
            }
            if name.ends_with("-bin") {
                values.append_bin(
                    name.parse::<MetadataKey<Binary>>().map_err(|_| invalid())?,
                    MetadataValue::from_bytes(value),
                );
            } else {
                if !value.is_ascii() || value.iter().any(|b| *b < 0x20 || *b > 0x7e) {
                    return Err(invalid());
                }
                values.append(
                    name.parse::<MetadataKey<Ascii>>().map_err(|_| invalid())?,
                    MetadataValue::try_from(value).map_err(|_| invalid())?,
                );
            }
        }
        if offset != data.len() {
            return Err(invalid());
        }
        let _ = trailers; // The same finite, protocol-reserved-name policy applies to both blocks.
        Ok(Self {
            values,
            _credit: Arc::new(credit),
        })
    }
}
fn invalid() -> Status {
    Status::invalid_argument("gRPC metadata")
}
fn limit() -> Status {
    Status::resource_exhausted("gRPC metadata quota")
}

/// A metadata value leaving the Body keeps the complete metadata batch grant.
/// http 1.5's public Bytes path adopts the existing immutable value without a
/// payload copy. HeaderMap/Hyper clones retain that same Bytes owner.
pub fn retain_headers(headers: &mut HeaderMap, credit: Arc<Credit>) {
    struct Retained {
        value: HeaderValue,
        _credit: Arc<Credit>,
    }
    impl AsRef<[u8]> for Retained {
        fn as_ref(&self) -> &[u8] {
            self.value.as_bytes()
        }
    }
    for value in headers.values_mut() {
        let original = std::mem::replace(value, HeaderValue::from_static(""));
        *value = HeaderValue::from_maybe_shared(bytes::Bytes::from_owner(Retained {
            value: original,
            _credit: credit.clone(),
        }))
        .expect("retained validated HeaderValue");
    }
}
