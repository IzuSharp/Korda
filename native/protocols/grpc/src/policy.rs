//! Immutable registration and the strict public gRPC timeout policy.
use http::{HeaderMap, Method, Request, Version};
use serde::Deserialize;
use std::time::{Duration, Instant};
use tonic::Status;

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum MethodKind {
    Unary,
    ClientStreaming,
    ServerStreaming,
    Duplex,
}
impl MethodKind {
    pub fn single_input(self) -> bool {
        matches!(self, Self::Unary | Self::ServerStreaming)
    }
    pub fn single_output(self) -> bool {
        matches!(self, Self::Unary | Self::ClientStreaming)
    }
}

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Descriptor {
    pub version: u32,
    pub kind: String,
    pub handler_id: u64,
    pub method: String,
    pub method_kind: MethodKind,
    pub request_codec_id: u64,
    pub response_codec_id: u64,
    pub max_encoded_message_bytes: u32,
    pub max_decoded_message_bytes: u32,
    pub max_stream_bytes: u64,
    pub timeout_ns: u64,
    pub compression: String,
}
impl Descriptor {
    pub fn parse(
        bytes: &[u8],
        path: &str,
        handler: u64,
        timeout: Duration,
        stream: u64,
    ) -> Result<Self, Status> {
        if bytes.len() > 16_384 {
            return Err(Status::invalid_argument("descriptor size"));
        }
        // serde's fixed record rejects duplicates, unknown fields, fractional/negative IDs,
        // and overflowing integer literals. Its default recursion bound remains enabled.
        let value: Self =
            serde_json::from_slice(bytes).map_err(|_| Status::invalid_argument("descriptor"))?;
        let mut components = value.method.strip_prefix('/').unwrap_or("").split('/');
        let service = components.next().unwrap_or("");
        let method = components.next().unwrap_or("");
        let identifier = |value: &str| {
            !value.is_empty()
                && value
                    .bytes()
                    .next()
                    .is_some_and(|b| b.is_ascii_alphabetic() || b == b'_')
                && value
                    .bytes()
                    .all(|b| b.is_ascii_alphanumeric() || b == b'_')
        };
        if value.version != 1
            || value.kind != "grpc"
            || value.handler_id != handler
            || handler == 0
            || value.method != path
            || path.len() > 4096
            || components.next().is_some()
            || !service.split('.').all(identifier)
            || !identifier(method)
            || value.request_codec_id == 0
            || value.response_codec_id == 0
            || value.max_encoded_message_bytes == 0
            || value.max_decoded_message_bytes == 0
            || value.max_stream_bytes == 0
            || value.max_stream_bytes != stream
            || value.timeout_ns == 0
            || value.timeout_ns > 86_400_000_000_000
            || Duration::from_nanos(value.timeout_ns) != timeout
            || value.compression != "identity"
        {
            return Err(Status::invalid_argument("descriptor contract"));
        }
        Ok(value)
    }
}

pub fn deadline(headers: &HeaderMap, route: Duration, now: Instant) -> Result<Instant, Status> {
    let mut values = headers.get_all("grpc-timeout").iter();
    let duration = if let Some(value) = values.next() {
        if values.next().is_some() {
            return Err(Status::invalid_argument("duplicate grpc-timeout"));
        }
        let bytes = value.as_bytes();
        if !(2..=9).contains(&bytes.len())
            || !bytes[..bytes.len() - 1].iter().all(u8::is_ascii_digit)
        {
            return Err(Status::invalid_argument("grpc-timeout"));
        }
        let number = bytes[..bytes.len() - 1]
            .iter()
            .fold(0u64, |n, b| n * 10 + u64::from(b - b'0'));
        let factor = match bytes[bytes.len() - 1] {
            b'H' => 3_600_000_000_000u64,
            b'M' => 60_000_000_000,
            b'S' => 1_000_000_000,
            b'm' => 1_000_000,
            b'u' => 1000,
            b'n' => 1,
            _ => return Err(Status::invalid_argument("grpc-timeout unit")),
        };
        // Compare in u128 and cap to the finite route budget before Instant addition.
        let ns = u128::from(number) * u128::from(factor);
        if ns == 0 {
            return Err(Status::deadline_exceeded("expired grpc-timeout"));
        }
        Duration::from_nanos(ns.min(route.as_nanos()) as u64)
    } else {
        route
    };
    now.checked_add(duration)
        .ok_or_else(|| Status::invalid_argument("deadline range"))
}

pub fn validate_http<B>(request: &Request<B>) -> Result<(), Status> {
    if request.version() != Version::HTTP_2 || *request.method() != Method::POST {
        return Err(Status::unimplemented("gRPC requires HTTP/2 POST"));
    }
    let content = request
        .headers()
        .get(http::header::CONTENT_TYPE)
        .and_then(|v| v.to_str().ok())
        .unwrap_or("");
    let content = content.split(';').next().unwrap_or("").trim();
    if content != "application/grpc" && content != "application/grpc+proto" {
        return Err(Status::invalid_argument("gRPC content type"));
    }
    Ok(())
}

/// Bytes that every admitted method allocates/reserves before managed dispatch.
/// Message owners and conditional binary decode scratch remain runtime grants.
pub fn admission_floor(encoded: u32, chunk: usize, max_header: usize) -> Option<u64> {
    u64::from(encoded)
        .checked_mul(2)?
        .checked_add((chunk as u64).checked_mul(2)?)?
        .checked_add(10)?
        .checked_add(crate::metadata::minimum_capacity(max_header.min(32_768))? as u64)
}
