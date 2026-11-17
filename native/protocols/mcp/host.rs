//! Included as a child of the existing Server engine; no second runtime or ABI.
use super::*;
use http_body_util::{BodyExt, Full};
use nativeweave_protocol_mcp::{
    preflight,
    profile::Profile,
    rmcp::ErrorData,
    runtime::{self, ForeignCall},
    serde_json::{self, Value},
    tokio_util::sync::CancellationToken,
};
use nativeweave_server_streaming::Side;
use std::future::Future;

pub struct State {
    profile: Arc<Profile>,
    parts: Mutex<Option<http::request::Parts>>,
    decision: AtomicU32,
    call_seen: AtomicBool,
    pending: Mutex<Option<Pending>>,
    peak: Mutex<Option<Arc<Peak>>>,
}
struct Pending {
    kind: u32,
    handler: u64,
    arguments: Option<OwnedBuffer>,
    result: Option<oneshot::Sender<std::result::Result<Value, ErrorData>>>,
}
struct Peak {
    _admission: preflight::Admission,
    reply: Mutex<Option<WriteReservation>>,
}
impl State {
    pub fn new(profile: Arc<Profile>, parts: http::request::Parts) -> Self {
        Self {
            profile,
            parts: Mutex::new(Some(parts)),
            decision: AtomicU32::new(0),
            call_seen: AtomicBool::new(false),
            pending: Mutex::new(None),
            peak: Mutex::new(None),
        }
    }
    pub fn denied(&self) -> bool {
        self.decision.load(Ordering::Acquire) == 2
    }
    pub fn allowed(&self) -> bool {
        self.decision.load(Ordering::Acquire) == 1
    }
    pub fn awaiting(&self) -> bool {
        self.decision.load(Ordering::Acquire) == 0
    }
    pub fn close(&self) {
        lock(&self.pending).take();
        lock(&self.peak).take();
        lock(&self.parts).take();
    }
    pub fn can_reply(&self) -> bool {
        self.allowed()
            && lock(&self.pending)
                .as_ref()
                .is_some_and(|p| p.arguments.is_none() && p.result.is_some())
    }
    pub fn take(&self) -> Result<(u32, OwnedBuffer)> {
        if !self.allowed() {
            return Err(ApiError::status(NW_INVALID_STATE));
        }
        let mut pending = lock(&self.pending);
        let pending = pending.as_mut().ok_or(ApiError::status(NW_INVALID_STATE))?;
        let buffer = pending
            .arguments
            .take()
            .ok_or(ApiError::status(NW_INVALID_STATE))?;
        Ok((pending.kind, buffer))
    }
}
pub fn state(request: &RequestState) -> Result<&State> {
    request
        .mcp
        .as_ref()
        .ok_or(ApiError::status(NW_INVALID_STATE))
}
pub fn raw_output(request: &RequestState) -> bool {
    request.head.kind == 0 || request.mcp.as_ref().is_some_and(State::denied)
}
pub fn bound_hosts(host: &Host) -> Vec<String> {
    lock(&host.addresses)
        .iter()
        .filter_map(|(_, url)| url.parse::<http::Uri>().ok())
        .filter(|uri| {
            uri.host().is_some_and(|host| {
                host.trim_matches(['[', ']'])
                    .parse::<std::net::IpAddr>()
                    .is_ok_and(|ip| ip.is_loopback())
            })
        })
        .filter_map(|uri| uri.authority().map(ToString::to_string))
        .collect()
}
pub fn headers(
    host: &Host,
    profile: &Profile,
    request: &AxumRequest,
) -> std::result::Result<(), StatusCode> {
    profile.headers(
        request.method(),
        request.headers(),
        request.uri().authority().map(|a| a.as_str()),
        &bound_hosts(host),
    )
}

struct Bridge {
    request: Arc<RequestState>,
    _peak: Arc<Peak>,
}
impl Bridge {
    fn new(request: Arc<RequestState>, peak: Arc<Peak>) -> Arc<Self> {
        request.live_ops.fetch_add(1, Ordering::AcqRel);
        Arc::new(Self {
            request,
            _peak: peak,
        })
    }
}
impl Drop for Bridge {
    fn drop(&mut self) {
        self.request.live_ops.fetch_sub(1, Ordering::AcqRel);
        if let Some(host) = self.request.host.upgrade() {
            host.reap();
        }
    }
}
impl ForeignCall for Bridge {
    fn invoke(
        &self,
        kind: u32,
        handler: u64,
        arguments: Value,
    ) -> Pin<Box<dyn Future<Output = std::result::Result<Value, ErrorData>> + Send + '_>> {
        Box::pin(async move {
            let internal = || ErrorData::internal_error("Native MCP request unavailable", None);
            self.request.business().map_err(|_| internal())?;
            let state = state(&self.request).map_err(|_| internal())?;
            if !state.allowed() || state.call_seen.swap(true, Ordering::AcqRel) {
                return Err(internal());
            }
            let encoded = serde_json::to_vec(&arguments).map_err(|_| internal())?;
            if encoded.is_empty() || encoded.len() > state.profile.limits.wire {
                return Err(internal());
            }
            let buffer = self
                .request
                .budget
                .reserve_payload(encoded.len())
                .and_then(|r| r.copy(&encoded))
                .map_err(|_| internal())?;
            let (send, receive) = oneshot::channel();
            *lock(&state.pending) = Some(Pending {
                kind,
                handler,
                arguments: Some(buffer),
                result: Some(send),
            });
            // The current-thread Host runtime cannot complete a canceled gate
            // between this admission and the synchronous fixed notification.
            // No await, native lock, or application callback occurs in between.
            let host = self.request.host().map_err(|_| internal())?;
            host.callbacks.fetch_add(1, Ordering::AcqRel);
            // SAFETY: the request, host, and its Core callback root remain leased
            // by this Bridge until the SDK service worker releases it.
            let callback: unsafe extern "C" fn(usize, u64, u64, u32) -> u32 =
                unsafe { std::mem::transmute(host.dispatch.on_request) };
            // SAFETY: kind3 names the already-taken ticket and immutable registry ID.
            let notified =
                unsafe { callback(host.dispatch.cookie, self.request.head.id, handler, 3) };
            host.callbacks.fetch_sub(1, Ordering::AcqRel);
            if notified != NW_OK {
                host.callback_failures.fetch_add(1, Ordering::Relaxed);
                self.request.abort(NW_CONTRACT_VIOLATION);
                return Err(internal());
            }
            tokio::select! { result=receive=>result.unwrap_or_else(|_|Err(internal())),()=self.request.cancel.cancelled()=>Err(internal()) }
        })
    }
}
struct GateGuard {
    request: Arc<RequestState>,
    success: bool,
}
impl Drop for GateGuard {
    fn drop(&mut self) {
        if !self.success {
            self.request.abort(NW_CANCELED);
        }
    }
}

pub fn commit(request: &Arc<RequestState>, decision: u32) -> Result<()> {
    request.business()?;
    let s = state(&request)?;
    if decision > 1 {
        return Err(invalid());
    }
    if lock(&request.response).phase != 0
        || s.decision
            .compare_exchange(
                0,
                if decision == 1 { 1 } else { 2 },
                Ordering::AcqRel,
                Ordering::Acquire,
            )
            .is_err()
    {
        return Err(ApiError::status(NW_INVALID_STATE));
    }
    Ok(())
}
pub async fn gate(request: Arc<RequestState>, decision: u32) -> Result<OpResult> {
    request.business()?;
    let s = state(&request)?;
    if decision == 0 {
        request.discard_input();
        lock(&s.parts).take();
        return Ok(OpResult::Void);
    }
    let mut guard = GateGuard {
        request: request.clone(),
        success: false,
    };
    let result = allowed(request.clone()).await;
    if result.is_ok() {
        guard.success = true;
    }
    result
}

async fn allowed(request: Arc<RequestState>) -> Result<OpResult> {
    let s = state(&request)?;
    let host = request.host()?;
    let parts = lock(&s.parts)
        .take()
        .ok_or(ApiError::status(NW_INVALID_STATE))?;
    let capacity = parts
        .headers
        .get(http::header::CONTENT_LENGTH)
        .and_then(|v| v.to_str().ok())
        .and_then(|v| v.parse::<usize>().ok())
        .unwrap_or(s.profile.limits.wire)
        .min(s.profile.limits.wire)
        .max(1);
    let wire_credit = request.budget.try_acquire(Side::Native, capacity as u64)?;
    let mut wire = Vec::with_capacity(capacity);
    {
        let mut reader = request.reader.lock().await;
        let input = reader.as_mut().ok_or(ApiError::status(NW_INVALID_STATE))?;
        loop {
            let frame = tokio::select! { result=input.read(host.config.max_chunk_bytes,&request.input_cancel)=>result?,()=request.cancel.cancelled()=>return Err(ApiError::status(NW_CANCELED)) };
            match frame {
                ReadFrame::Data(buffer) => {
                    if buffer.as_slice().len() > capacity.saturating_sub(wire.len()) {
                        return response_error(&request, StatusCode::PAYLOAD_TOO_LARGE).await;
                    }
                    wire.extend_from_slice(buffer.as_slice());
                }
                ReadFrame::End(_) => break,
            }
        }
        reader.take();
    }
    if wire.is_empty() {
        return response_error(&request, StatusCode::BAD_REQUEST).await;
    }
    // Compact the unknown-length collector under a second exact backing grant;
    // only then discard its maximum-capacity allocation before parsing.
    let input = request.budget.reserve_payload(wire.len())?.copy(&wire)?;
    drop(wire);
    drop(wire_credit);
    let scratch = request.budget.try_acquire(
        Side::Native,
        preflight::scan_scratch(input.backing_capacity(), s.profile.limits.depth)
            .ok_or(invalid())?,
    )?;
    let shape = match preflight::scan(input.as_slice(), s.profile.limits, request.head.deadline) {
        Ok(shape) => shape,
        Err(preflight::Error::InvalidJson) => {
            drop(scratch);
            return response_error(&request, StatusCode::BAD_REQUEST).await;
        }
        Err(preflight::Error::ResourceLimit) => {
            drop(scratch);
            return response_error(&request, StatusCode::PAYLOAD_TOO_LARGE).await;
        }
        Err(preflight::Error::Timeout) => return Err(ApiError::status(NW_TIMEOUT)),
    };
    drop(scratch);
    let native_peak = preflight::native_peak(input.backing_capacity(), shape).ok_or(invalid())?;
    let managed_peak = preflight::managed_peak(input.len(), shape).ok_or(invalid())?;
    let automatic = if parts
        .headers
        .get("mcp-method")
        .and_then(|v| v.to_str().ok())
        .is_some_and(|m| {
            matches!(
                m,
                "tools/list" | "resources/list" | "prompts/list" | "server/discover"
            )
        }) {
        (s.profile.metadata_bytes as u64)
            .checked_mul(8)
            .and_then(|n| n.checked_add(65536))
            .ok_or(invalid())?
    } else {
        65536
    };
    let admission = match preflight::admit(
        &request.budget,
        native_peak.saturating_sub(input.backing_capacity() as u64),
        managed_peak,
        automatic,
    ) {
        Ok(value) => value,
        Err(_) => return response_error(&request, StatusCode::SERVICE_UNAVAILABLE).await,
    };
    let peak = Arc::new(Peak {
        _admission: admission,
        reply: Mutex::new(None),
    });
    *lock(&s.peak) = Some(peak.clone());
    let bridge = Bridge::new(request.clone(), peak.clone());
    let cancel = CancellationToken::new();
    let _cancel = cancel.clone().drop_guard();
    let hosts = if s.profile.descriptor.allowed_hosts.is_empty() {
        bound_hosts(&host)
    } else {
        s.profile.descriptor.allowed_hosts.clone()
    };
    let call = runtime::serve(
        s.profile.clone(),
        hosts,
        Request::from_parts(parts, Full::new(input.into_bytes())),
        bridge,
        cancel,
    );
    let response = tokio::select! { response=call=>response,()=request.cancel.cancelled()=>return Err(ApiError::status(NW_CANCELED)) };
    let (parts, body) = response.into_parts();
    let bytes = match body.collect().await {
        Ok(body) => body.to_bytes(),
        Err(never) => match never {},
    };
    let reservation = lock(&peak.reply).take();
    send_response(
        &request,
        parts.status.as_u16(),
        parts.headers,
        bytes,
        reservation,
    )
    .await?;
    Ok(OpResult::Void)
}
async fn response_error(request: &Arc<RequestState>, status: StatusCode) -> Result<OpResult> {
    request.discard_input();
    send_response(
        request,
        status.as_u16(),
        HeaderMap::new(),
        Bytes::new(),
        None,
    )
    .await?;
    Ok(OpResult::Void)
}
async fn send_response(
    request: &Arc<RequestState>,
    status: u16,
    headers: HeaderMap,
    bytes: Bytes,
    reservation: Option<WriteReservation>,
) -> Result<()> {
    {
        let mut response = lock(&request.response);
        if response.phase != 0 {
            return Err(ApiError::status(NW_INVALID_STATE));
        }
        response
            .head
            .take()
            .ok_or(ApiError::status(NW_INVALID_STATE))?
            .send((status, headers))
            .map_err(|_| ApiError::domain(4))?;
        response.phase = 1;
        response.status = status;
        response.written = bytes.len() as u64;
    }
    if !bytes.is_empty() {
        if let Some(reservation) = reservation {
            if bytes.len() > reservation.capacity() {
                return Err(ApiError::domain(1));
            }
            // SAFETY: the reply's exact parsed shape and copied/output allocations
            // were admitted under this retained reservation before create. No
            // alias escapes; this is the sole complete SDK JSON response Bytes.
            unsafe { reservation.prepare_admitted(bytes)? }.commit();
        } else {
            request
                .sender
                .reserve(bytes.len(), &request.cancel)
                .await?
                .commit(&bytes)?;
        }
    }
    request.sender.finish(None, &request.cancel).await?;
    lock(&request.response).phase = 3;
    Ok(())
}

pub fn validate_reply(
    request: &RequestState,
    kind: u32,
    bytes: &[u8],
    capacity: usize,
) -> Result<Value> {
    let s = state(request)?;
    if !s.can_reply() || kind > 2 || bytes.is_empty() || bytes.len() > 262144 {
        return Err(invalid());
    }
    let limits = preflight::Limits {
        wire: 262144,
        depth: 32,
        nodes: 4096,
        strings: 262144,
    };
    let shape = preflight::scan(bytes, limits, request.head.deadline).map_err(|_| invalid())?;
    if preflight::native_peak(bytes.len(), shape).is_none_or(|n| n > capacity as u64) {
        return Err(ApiError::domain(1));
    }
    let value: Value = serde_json::from_slice(bytes).map_err(|_| invalid())?;
    let pending = lock(&s.pending);
    let pending = pending.as_ref().ok_or(ApiError::status(NW_INVALID_STATE))?;
    let object = value.as_object().ok_or(invalid())?;
    if kind == 2 {
        if object.len() != 2
            || !object
                .get("code")
                .and_then(Value::as_i64)
                .is_some_and(|c| matches!(c, -32602 | -32603 | -32601))
            || !object
                .get("message")
                .and_then(Value::as_str)
                .is_some_and(|v| v.len() <= 4096)
        {
            return Err(invalid());
        }
    } else if pending.kind == 0 {
        let tool = s
            .profile
            .descriptor
            .tools
            .iter()
            .find(|t| t.handler_id == pending.handler)
            .ok_or(invalid())?;
        let content = object
            .get("content")
            .and_then(Value::as_array)
            .filter(|v| v.len() == 1)
            .ok_or(invalid())?;
        let text = content[0]
            .as_object()
            .filter(|v| v.len() == 2 && v.get("type").and_then(Value::as_str) == Some("text"))
            .and_then(|v| v.get("text"))
            .and_then(Value::as_str)
            .ok_or(invalid())?;
        if kind == 1 {
            if object.len() != 2 || object.get("isError") != Some(&Value::Bool(true)) {
                return Err(invalid());
            }
        } else {
            let structured = object.get("structuredContent").ok_or(invalid())?;
            if object.len() != 3
                || object.get("isError") != Some(&Value::Bool(false))
                || !nativeweave_protocol_mcp::profile::conforms(structured, &tool.output_schema)
            {
                return Err(invalid());
            }
            let copy: Value = serde_json::from_str(text).map_err(|_| invalid())?;
            if &copy != structured {
                return Err(invalid());
            }
        }
    } else if pending.kind == 1 && kind == 0 {
        let resource = s
            .profile
            .descriptor
            .resources
            .iter()
            .find(|r| r.handler_id == pending.handler)
            .ok_or(invalid())?;
        let contents = object
            .get("contents")
            .and_then(Value::as_array)
            .filter(|v| v.len() == 1 && object.len() == 1)
            .ok_or(invalid())?;
        let item = contents[0].as_object().ok_or(invalid())?;
        if item.len() != 3
            || item.get("uri").and_then(Value::as_str) != Some(&resource.uri)
            || item.get("mimeType").and_then(Value::as_str) != Some(&resource.mime_type)
            || !item.get("text").is_some_and(Value::is_string)
        {
            return Err(invalid());
        }
    } else if pending.kind == 2 && kind == 0 {
        let messages = object
            .get("messages")
            .and_then(Value::as_array)
            .filter(|v| v.len() <= 128 && object.len() == 1)
            .ok_or(invalid())?;
        for message in messages {
            let msg = message.as_object().ok_or(invalid())?;
            let content = msg
                .get("content")
                .and_then(Value::as_object)
                .ok_or(invalid())?;
            if msg.len() != 2
                || !matches!(
                    msg.get("role").and_then(Value::as_str),
                    Some("user" | "assistant")
                )
                || content.len() != 2
                || content.get("type").and_then(Value::as_str) != Some("text")
                || !content.get("text").is_some_and(Value::is_string)
            {
                return Err(invalid());
            }
        }
    } else {
        return Err(invalid());
    }
    Ok(value)
}
pub fn reply(
    request: &Arc<RequestState>,
    kind: u32,
    value: Value,
    reservation: WriteReservation,
) -> Result<OpResult> {
    request.business()?;
    let s = state(request)?;
    let mut pending = lock(&s.pending);
    let pending = pending.as_mut().ok_or(ApiError::status(NW_INVALID_STATE))?;
    if pending.arguments.is_some() {
        return Err(ApiError::status(NW_INVALID_STATE));
    }
    let sender = pending
        .result
        .take()
        .ok_or(ApiError::status(NW_INVALID_STATE))?;
    let peak = lock(&s.peak)
        .clone()
        .ok_or(ApiError::status(NW_INVALID_STATE))?;
    *lock(&peak.reply) = Some(reservation);
    let result = if kind == 2 {
        Err(ErrorData::new(
            rmcp_code(value["code"].as_i64().ok_or(invalid())? as i32),
            value["message"].as_str().ok_or(invalid())?.to_owned(),
            None,
        ))
    } else {
        Ok(value)
    };
    sender.send(result).map_err(|_| ApiError::domain(4))?;
    Ok(OpResult::Void)
}
fn rmcp_code(code: i32) -> nativeweave_protocol_mcp::rmcp::model::ErrorCode {
    nativeweave_protocol_mcp::rmcp::model::ErrorCode(code)
}
