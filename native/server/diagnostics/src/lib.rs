//! Owned diagnostic metadata and per-poll scopes. This module installs no global
//! subscriber, listener, provider, runtime or network exporter in any build mode.
use http::HeaderMap;
use std::{
    collections::HashSet,
    future::Future,
    sync::atomic::{AtomicU64, Ordering},
    time::Instant,
};
pub use tracing;
use tracing::{Dispatch, Instrument, Span, instrument::WithSubscriber};

/// Exact D19 domain extension. IDs use W3C byte order; no pointer crosses FFI.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct TraceContextV1 {
    pub size: u32,
    pub version: u16,
    pub trace_flags: u8,
    pub reserved: u8,
    pub trace_id: [u8; 16],
    pub parent_span_id: [u8; 8],
}
const _: () = {
    assert!(size_of::<TraceContextV1>() == 32);
    assert!(std::mem::offset_of!(TraceContextV1, trace_id) == 8);
    assert!(std::mem::offset_of!(TraceContextV1, parent_span_id) == 24);
};
impl TraceContextV1 {
    pub fn valid(&self) -> bool {
        self.size == 32
            && self.version == 1
            && self.trace_flags & !1 == 0
            && self.reserved == 0
            && self.trace_id != [0; 16]
            && self.parent_span_id != [0; 8]
    }
    pub fn encode(&self) -> Option<[u8; 32]> {
        if !self.valid() {
            return None;
        }
        let mut b = [0; 32];
        b[..4].copy_from_slice(&32u32.to_le_bytes());
        b[4..6].copy_from_slice(&1u16.to_le_bytes());
        b[6] = self.trace_flags;
        b[8..24].copy_from_slice(&self.trace_id);
        b[24..].copy_from_slice(&self.parent_span_id);
        Some(b)
    }
    pub fn decode(b: &[u8]) -> Option<Self> {
        if b.len() != 32 {
            return None;
        }
        let value = Self {
            size: u32::from_le_bytes(b[..4].try_into().ok()?),
            version: u16::from_le_bytes(b[4..6].try_into().ok()?),
            trace_flags: b[6],
            reserved: b[7],
            trace_id: b[8..24].try_into().ok()?,
            parent_span_id: b[24..32].try_into().ok()?,
        };
        value.valid().then_some(value)
    }
}
fn hex<const N: usize>(text: &str) -> Option<[u8; N]> {
    if text.len() != N * 2 {
        return None;
    }
    let mut bytes = [0; N];
    fn digit(b: u8) -> Option<u8> {
        match b {
            b'0'..=b'9' => Some(b - b'0'),
            b'a'..=b'f' => Some(b - b'a' + 10),
            _ => None,
        }
    }
    for (i, pair) in text.as_bytes().as_chunks::<2>().0.iter().enumerate() {
        bytes[i] = digit(pair[0])? * 16 + digit(pair[1])?;
    }
    Some(bytes)
}
/// Bounded W3C traceparent validation; unknown future flags are not retained.
pub fn trace_parent(text: &str) -> Option<TraceContextV1> {
    let bytes = text.as_bytes();
    if bytes.len() < 55
        || bytes.len() > 512
        || bytes[2] != b'-'
        || bytes[35] != b'-'
        || bytes[52] != b'-'
    {
        return None;
    }
    let version = hex::<1>(text.get(..2)?)?[0];
    if version == 255 || version == 0 && bytes.len() != 55 || bytes.len() > 55 && bytes[55] != b'-'
    {
        return None;
    }
    // W3C 3.2.4: do not parse or assume the contents of future extension fields.
    let value = TraceContextV1 {
        size: 32,
        version: 1,
        trace_flags: hex::<1>(text.get(53..55)?)?[0] & 1,
        reserved: 0,
        trace_id: hex(text.get(3..35)?)?,
        parent_span_id: hex(text.get(36..52)?)?,
    };
    value.valid().then_some(value)
}
fn key_part(value: &str, first_alpha: bool, max: usize) -> bool {
    let b = value.as_bytes();
    !b.is_empty()
        && b.len() <= max
        && (b[0].is_ascii_lowercase() || !first_alpha && b[0].is_ascii_digit())
        && b[1..]
            .iter()
            .all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || b"_-*/".contains(c))
}
/// Validates the complete bounded tracestate list; baggage is never inspected.
pub fn trace_state(text: &str) -> bool {
    if text.len() > 512 || !text.is_ascii() {
        return false;
    }
    if text.is_empty() {
        return true;
    }
    let mut names = HashSet::new();
    let mut count = 0;
    for item in text.split(',') {
        count += 1;
        if count > 32 {
            return false;
        }
        let item = item.trim_matches([' ', '\t']);
        if item.is_empty() {
            continue;
        }
        let Some((key, value)) = item.split_once('=') else {
            return false;
        };
        let valid = if let Some((tenant, system)) = key.split_once('@') {
            key_part(tenant, false, 241) && key_part(system, true, 14)
        } else {
            key_part(key, true, 256)
        };
        if !valid
            || !names.insert(key)
            || value.is_empty()
            || value.len() > 256
            || value.ends_with(' ')
            || !value
                .bytes()
                .all(|c| (0x20..=0x7e).contains(&c) && c != b',' && c != b'=')
        {
            return false;
        }
    }
    true
}
#[derive(Default)]
pub struct CapturedContext {
    pub context: Option<TraceContextV1>,
    pub state: Vec<u8>,
}
impl CapturedContext {
    pub fn encoded(&self) -> Vec<u8> {
        self.context
            .and_then(|v| v.encode())
            .map(Vec::from)
            .unwrap_or_default()
    }
}
/// Engine-owned dispatcher and fixed counters. Capture does not install it globally.
pub struct Diagnostics {
    dispatcher: Dispatch,
    dropped: AtomicU64,
}
// This closed domain holder owns only an immutable Dispatch and an atomic drop
// counter. It never lends mutable business state to a subscriber. Server ABI
// unwinds permanently poison EngineBoundary; asynchronous subscriber unwinds
// fault/close the Host before the affected future is ever polled again. Cleanup
// retains the normal ownership join. This marker does not claim that arbitrary
// subscriber state may be resumed after a panic and does not weaken ffi bounds.
impl std::panic::RefUnwindSafe for Diagnostics {}

impl Default for Diagnostics {
    fn default() -> Self {
        Self::new()
    }
}
impl Diagnostics {
    pub fn new() -> Self {
        Self {
            dispatcher: tracing::dispatcher::get_default(Clone::clone),
            dropped: AtomicU64::new(0),
        }
    }
    pub fn dropped(&self) -> u64 {
        self.dropped.load(Ordering::Relaxed)
    }
    pub fn capture(&self, headers: &HeaderMap) -> CapturedContext {
        let mut values = headers.get_all("traceparent").iter();
        let Some(parent) = values.next() else {
            if headers.contains_key("tracestate") {
                self.dropped.fetch_add(1, Ordering::Relaxed);
            }
            return CapturedContext::default();
        };
        let context = parent.to_str().ok().and_then(trace_parent);
        if values.next().is_some() || context.is_none() {
            self.dropped.fetch_add(1, Ordering::Relaxed);
            return CapturedContext::default();
        }
        // W3C 3.3: preserve valid traceparent even when tracestate is malformed.
        // Combine multiple field lines in received order, within the same byte cap.
        let mut state = String::new();
        let mut first = true;
        let mut bad = false;
        for value in headers.get_all("tracestate") {
            let Ok(value) = value.to_str() else {
                bad = true;
                break;
            };
            if value.len() + usize::from(!first) > 512 - state.len() {
                bad = true;
                break;
            }
            if !first {
                state.push(',');
            }
            first = false;
            state.push_str(value);
        }
        if bad || !trace_state(&state) {
            self.dropped.fetch_add(1, Ordering::Relaxed);
            state.clear();
        }
        CapturedContext {
            context,
            state: state.into_bytes(),
        }
    }
    pub fn request(
        &self,
        route: &str,
        protocol: &'static str,
        context: Option<TraceContextV1>,
    ) -> RequestTrace {
        let span = tracing::dispatcher::with_default(&self.dispatcher, || {
            if tracing::enabled!(tracing::Level::INFO) {
                let mut end = route.len().min(256);
                while !route.is_char_boundary(end) {
                    end -= 1;
                }
                tracing::info_span!(
                    "nativeweave.server.request",
                    route = &route[..end],
                    protocol,
                    parent_trace_id = tracing::field::Empty,
                    parent_span_id = tracing::field::Empty,
                    trace_flags = tracing::field::Empty,
                    retirement_ns = tracing::field::Empty
                )
            } else {
                Span::none()
            }
        });
        if !span.is_disabled()
            && let Some(context) = context
        {
            span.record(
                "parent_trace_id",
                tracing::field::display(Hex(&context.trace_id)),
            );
            span.record(
                "parent_span_id",
                tracing::field::display(Hex(&context.parent_span_id)),
            );
            span.record("trace_flags", context.trace_flags);
        }
        let started = (!span.is_disabled()).then(Instant::now);
        RequestTrace {
            span,
            dispatcher: self.dispatcher.clone(),
            context,
            started,
        }
    }
    pub fn instrument<F: Future>(&self, future: F) -> impl Future<Output = F::Output> + use<F> {
        future.with_subscriber(self.dispatcher.clone())
    }
}
/// The optional fixed parent context stays with the native request span. It is
/// structural correlation metadata, never a metric dimension or authorization.
pub struct RequestTrace {
    span: Span,
    dispatcher: Dispatch,
    context: Option<TraceContextV1>,
    started: Option<Instant>,
}
impl RequestTrace {
    pub fn context(&self) -> Option<TraceContextV1> {
        self.context
    }
    pub fn enabled(&self) -> bool {
        !self.span.is_disabled()
    }
    /// A synchronous callback/poll scope; the guard never survives an await.
    pub fn in_scope<T>(&self, action: impl FnOnce() -> T) -> T {
        tracing::dispatcher::with_default(&self.dispatcher, || self.span.in_scope(action))
    }
    pub fn instrument<F: Future>(&self, future: F) -> impl Future<Output = F::Output> + use<F> {
        future
            .instrument(self.span.clone())
            .with_subscriber(self.dispatcher.clone())
    }
}
impl Drop for RequestTrace {
    fn drop(&mut self) {
        if let Some(started) = self.started {
            self.span.record(
                "retirement_ns",
                started.elapsed().as_nanos().min(u64::MAX as u128) as u64,
            );
        }
    }
}
struct Hex<'a>(&'a [u8]);
impl std::fmt::Display for Hex<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        for b in self.0 {
            write!(f, "{b:02x}")?;
        }
        Ok(())
    }
}
#[cfg(test)]
mod tests;
