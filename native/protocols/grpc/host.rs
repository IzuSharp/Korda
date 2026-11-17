//! B18 child of the existing B17 Host. One Tokio runtime and exact Core operations.
use super::*;
use nativeweave_protocols_grpc::{
    self as protocol, Call, MessageBuffer, ResponseMetadata, tonic::Status,
};
use nativeweave_server_streaming::Credit;

pub enum Buffer {
    Body(OwnedBuffer),
    Grpc(MessageBuffer),
}
impl From<OwnedBuffer> for Buffer {
    fn from(value: OwnedBuffer) -> Self {
        Self::Body(value)
    }
}
impl From<MessageBuffer> for Buffer {
    fn from(value: MessageBuffer) -> Self {
        Self::Grpc(value)
    }
}
impl Buffer {
    pub fn len(&self) -> usize {
        match self {
            Self::Body(v) => v.len(),
            Self::Grpc(v) => v.len(),
        }
    }
    pub fn as_slice(&self) -> &[u8] {
        match self {
            Self::Body(v) => v.as_slice(),
            Self::Grpc(v) => v.as_slice(),
        }
    }
    pub fn acquire_managed(&self, capacity: usize) -> Result<Credit> {
        match self {
            Self::Body(v) => Ok(v.acquire_managed(capacity)?),
            Self::Grpc(v) => v.acquire_managed(capacity).map_err(error),
        }
    }
}
pub enum WriteToken {
    Body(WriteReservation),
    Grpc(protocol::Reservation),
}
impl WriteToken {
    pub fn capacity(&self) -> usize {
        match self {
            Self::Body(v) => v.capacity(),
            Self::Grpc(v) => v.capacity(),
        }
    }
    pub fn prepare(self, data: &[u8]) -> std::result::Result<PreparedWrite, StreamError> {
        match self {
            Self::Body(v) => v.prepare(data),
            Self::Grpc(_) => Err(StreamError::Closed),
        }
    }
    /// # Safety
    /// The B16 formatter's whole-backing/growth/no-alias pregrant contract applies.
    pub unsafe fn prepare_admitted(
        self,
        data: Bytes,
    ) -> std::result::Result<PreparedWrite, StreamError> {
        match self {
            // SAFETY: the caller proves B16's formatter-specific pregrant and exclusive adoption requirements.
            Self::Body(v) => unsafe { v.prepare_admitted(data) },
            Self::Grpc(_) => Err(StreamError::Closed),
        }
    }
    pub fn into_body(self) -> WriteReservation {
        match self {
            Self::Body(v) => v,
            Self::Grpc(_) => unreachable!("validated raw reservation kind"),
        }
    }
    pub fn into_grpc(self) -> protocol::Reservation {
        match self {
            Self::Grpc(v) => v,
            Self::Body(_) => unreachable!("validated grpc reservation kind"),
        }
    }
}
pub fn validate_route(host: &Host, route: &Route) -> Result<()> {
    if let Some(descriptor) = &route.grpc {
        let floor = protocol::admission_floor(
            descriptor.max_encoded_message_bytes,
            host.config.max_chunk_bytes,
            host.config.max_header_bytes,
        )
        .ok_or(invalid())?;
        if floor
            > host
                .config
                .native_payload_bytes
                .min(host.config.request_retained_bytes)
        {
            return Err(invalid());
        }
    }
    Ok(())
}
pub fn error(status: Status) -> ApiError {
    ApiError {
        status: NW_DOMAIN_ERROR,
        code: status.code() as i32,
        error_domain: 0x47525043,
    }
}
pub fn state(request: &RequestState) -> Result<&Arc<Call>> {
    request
        .grpc
        .as_ref()
        .ok_or(ApiError::status(NW_INVALID_STATE))
}
pub fn cancel_read(operation: &OpState) {
    if operation.result_kind == 2
        && matches!(
            operation.operation.snapshot().0,
            nativeweave_interop_async::Phase::Created | nativeweave_interop_async::Phase::Running
        )
        && let Some(request) = &operation.request
        && let Some(call) = &request.grpc
    {
        call.stop_input();
    }
}
// Actual retirement helper below uses the existing InputDiscard guard rather than
// introducing another callback or native owner protocol.
pub fn discard(request: &Arc<RequestState>) {
    let Some(call) = &request.grpc else {
        return;
    };
    call.stop_input();
    if let Some(host) = request.host.upgrade()
        && let Some(runtime) = lock(&host.runtime).clone()
    {
        request.live_ops.fetch_add(1, Ordering::AcqRel);
        let guard = InputDiscard(request.clone());
        let call = call.clone();
        runtime.spawn(request.diagnostics.instrument(async move {
            call.retire_input().await;
            drop(guard);
        }));
    }
}
pub fn release_metadata_if_done(request: &RequestState) {
    if request.dispatch_done.load(Ordering::Acquire)
        && request.body_done.load(Ordering::Acquire)
        && let Some(call) = &request.grpc
    {
        call.release_metadata();
    }
}
pub fn remaining_anchor(request: &RequestState) -> u64 {
    request
        .head
        .grpc
        .as_ref()
        .map(|m| m.reserved_capacity())
        .unwrap_or(0)
}
pub fn metadata_released(request: &RequestState) -> bool {
    request
        .head
        .grpc
        .as_ref()
        .is_none_or(|m| m.unshared() && Arc::strong_count(&request.head) == 1)
}

pub async fn read(request: Arc<RequestState>, _side: SideGuard) -> Result<OpResult> {
    request.business()?;
    let result = tokio::select! {biased;()=request.cancel.cancelled()=>return Err(canceled(&request)),value=state(&request)?.read()=>value.map_err(|status| if status.code()==protocol::tonic::Code::Cancelled {ApiError::status(NW_CANCELED)} else {error(status)})?};
    Ok(OpResult::Chunk(match result {
        Some(value) => ChunkResult::GrpcData(value),
        None => ChunkResult::End(None),
    }))
}
pub async fn reserve(
    request: Arc<RequestState>,
    capacity: usize,
    side: SideGuard,
) -> Result<OpResult> {
    request.business()?;
    let reservation = state(&request)?
        .reserve(capacity, &request.cancel)
        .await
        .map_err(error)?;
    Ok(OpResult::Reservation(Reservation {
        request,
        kind: 1,
        reservation: WriteToken::Grpc(reservation),
        side,
    }))
}
pub fn write(
    request: Arc<RequestState>,
    prepared: protocol::Prepared,
    _side: SideGuard,
) -> Result<OpResult> {
    request.business()?;
    state(&request)?.write(prepared).map_err(error)?;
    Ok(OpResult::Void)
}
pub fn headers(
    request: Arc<RequestState>,
    metadata: ResponseMetadata,
    _side: SideGuard,
) -> Result<OpResult> {
    request.business()?;
    state(&request)?.headers(metadata).map_err(error)?;
    Ok(OpResult::Void)
}
pub async fn status(
    request: Arc<RequestState>,
    status: Status,
    metadata: ResponseMetadata,
    _side: SideGuard,
) -> Result<OpResult> {
    request.business()?;
    state(&request)?
        .finish(status, metadata, &request.cancel)
        .await
        .map_err(error)?;
    Ok(OpResult::Void)
}
fn canceled(request: &RequestState) -> ApiError {
    ApiError::status(
        if request.head.cancellation.load(Ordering::Acquire) == NW_TIMEOUT {
            NW_TIMEOUT
        } else {
            NW_CANCELED
        },
    )
}
fn cancellation_status(request: &RequestState) -> Status {
    if request.head.cancellation.load(Ordering::Acquire) == NW_TIMEOUT {
        Status::deadline_exceeded("call deadline expired")
    } else {
        Status::cancelled("call canceled")
    }
}
fn response(status: Status) -> Response<AxumBody> {
    status.into_http()
}
pub async fn fallback(request: AxumRequest) -> Response<AxumBody> {
    if request
        .headers()
        .get(http::header::CONTENT_TYPE)
        .is_some_and(|v| v.as_bytes().starts_with(b"application/grpc"))
    {
        response(Status::unimplemented("unknown method"))
    } else {
        simple(StatusCode::NOT_FOUND)
    }
}

pub async fn dispatch(
    host: Arc<Host>,
    route: Route,
    parameters: RawPathParams,
    request: AxumRequest,
) -> Response<AxumBody> {
    if let Err(status) = protocol::validate_http(&request) {
        return response(status);
    }
    let deadline = match protocol::deadline(request.headers(), route.timeout, Instant::now()) {
        Ok(v) => v,
        Err(status) => return response(status),
    };
    let profile = route.grpc.clone().expect("validated grpc route");
    let peer = request
        .extensions()
        .get::<Peer>()
        .expect("native peer extension")
        .clone();
    let active = match host.active.clone().try_acquire_owned() {
        Ok(value) => value,
        Err(_) => {
            let Ok(_queued) = host.queued.clone().try_acquire_owned() else {
                host.rejected.fetch_add(1, Ordering::Relaxed);
                return response(Status::resource_exhausted("admission queue"));
            };
            tokio::select! {()=host.stop.cancelled()=>return response(Status::unavailable("host closing")),value=tokio::time::timeout_at(deadline.into(),host.active.clone().acquire_owned())=>match value{Ok(Ok(v))=>v,_=>return response(Status::deadline_exceeded("admission deadline"))}}
        }
    };
    if host.stop.is_canceled() {
        return response(Status::unavailable("host closing"));
    }
    let budget = match host.budget.request(host.config.request_retained_bytes) {
        Ok(v) => v,
        Err(_) => return response(Status::resource_exhausted("request budget")),
    };
    let metadata = match protocol::project_metadata(
        request.headers(),
        host.config.max_header_bytes.min(32_768),
        &budget,
    ) {
        Ok(v) => v,
        Err(s) => return response(s),
    };
    let id = host.next_id.fetch_add(1, Ordering::Relaxed);
    if id == 0 {
        host.fault(NW_CONTRACT_VIOLATION);
        return response(Status::unavailable("request identity"));
    }
    let params = parameters
        .iter()
        .map(|(a, b)| (a.to_owned(), b.to_owned()))
        .collect();
    let mut head = match metadata::head(
        &request,
        &route,
        &host.config,
        id,
        peer.address,
        peer.tls,
        params,
        deadline,
    ) {
        Ok(v) => v,
        Err(_) => return response(Status::invalid_argument("request metadata")),
    };
    let captured = host.diagnostics.capture(request.headers());
    let diagnostics = host
        .diagnostics
        .request(&route.canonical, "gRPC", captured.context);
    let mutable = Arc::get_mut(&mut head).expect("unpublished metadata");
    mutable.trace = captured.encoded();
    mutable.trace_state = captured.state;
    mutable.grpc = Some(metadata);
    let call = Call::new(profile, budget.clone());
    let (sender, unused) = body_channel(budget.clone(), host.config.max_chunk_bytes, 1)
        .expect("validated body limits");
    drop(unused);
    peer.life.active.fetch_add(1, Ordering::AcqRel);
    peer.life.changed.notify_waiters();
    let request_state = Arc::new(RequestState {
        host: Arc::downgrade(&host),
        diagnostics,
        body_limit: Arc::new(AtomicBool::new(false)),
        head,
        reader: tokio::sync::Mutex::new(None),
        sender,
        budget,
        response: Mutex::new(ResponseState {
            phase: 0,
            status: 200,
            length: None,
            written: 0,
            sse: false,
            head: None,
        }),
        cancel: Cancellation::default(),
        input_cancel: Cancellation::default(),
        output_waker: AtomicWaker::new(),
        finished: Arc::default(),
        dispatch_done: AtomicBool::new(false),
        taken: AtomicBool::new(false),
        retired: AtomicBool::new(false),
        body_done: AtomicBool::new(false),
        connection_done: AtomicBool::new(false),
        read_active: Arc::new(AtomicBool::new(false)),
        write_active: Arc::new(AtomicBool::new(false)),
        live_ops: AtomicUsize::new(0),
        controls: Mutex::new(ControlGate {
            open: true,
            active: 0,
        }),
        _active: active,
        life: peer.life,
        mcp: None,
        grpc: Some(call.clone()),
    });
    lock(&host.requests).insert(id, request_state.clone());
    let dispatched = Arc::new(AtomicBool::new(false));
    let mut pending = PendingResponse {
        request: request_state.clone(),
        dispatched: dispatched.clone(),
        transferred: false,
    };
    let timer = Arc::downgrade(&request_state);
    let timer_host = host.clone();
    let finished = request_state.finished.clone();
    tokio::spawn(request_state.diagnostics.instrument(async move{
        tokio::select!{()=tokio::time::sleep_until(deadline.into())=>{if let Some(r)=timer.upgrade(){r.abort(NW_TIMEOUT);}},()=timer_host.abort.cancelled()=>{if let Some(r)=timer.upgrade(){r.abort(NW_CANCELED);}},()=finished.cancelled()=>{}}
    }));
    let dispatch_state = request_state.clone();
    let dispatch_host = host.clone();
    let dispatch_seen = dispatched.clone();
    let dispatch = Arc::new(move || {
        dispatch_seen.store(true, Ordering::Release);
        if dispatch_state.cancel.is_canceled() {
            dispatch_state.mark_dispatch_done();
            dispatch_state.retired.store(true, Ordering::Release);
            return Err(cancellation_status(&dispatch_state));
        }
        if dispatch_host.notify_request(&dispatch_state) != NW_OK {
            dispatch_state.mark_dispatch_done();
            dispatch_state.retired.store(true, Ordering::Release);
            return Err(Status::internal("managed dispatch unavailable"));
        }
        Ok(())
    });
    let serving = protocol::serve(request, call, host.config.max_chunk_bytes, dispatch);
    let response = request_state
        .diagnostics
        .instrument(response_or_cancel(&request_state, serving))
        .await;
    if !dispatched.load(Ordering::Acquire) {
        request_state.mark_dispatch_done();
        request_state.retired.store(true, Ordering::Release);
    }
    let terminal_head = response.headers().contains_key("grpc-status");
    let (mut parts, body) = response.into_parts();
    let header_credit = request_state
        .grpc
        .as_ref()
        .and_then(|c| c.initial_credit())
        .unwrap_or_else(|| {
            request_state
                .head
                .grpc
                .as_ref()
                .expect("grpc metadata anchor")
                .credit()
        });
    protocol::retain_headers(&mut parts.headers, header_credit);
    let response = Response::from_parts(
        parts,
        AxumBody::new(GrpcBody {
            inner: Some(body),
            request: request_state,
            ended: false,
            terminal_head,
        }),
    );
    pending.transferred = true;
    response
}
// Hyper can drop an HTTP/2 response future on peer reset before any Body exists.
// Retain the same request owner across that gap; every exit either transfers it
// to GrpcBody or establishes cancellation/body retirement here.
struct PendingResponse {
    request: Arc<RequestState>,
    dispatched: Arc<AtomicBool>,
    transferred: bool,
}
impl Drop for PendingResponse {
    fn drop(&mut self) {
        if !self.transferred {
            self.request.abort(NW_CANCELED);
            if !self.dispatched.load(Ordering::Acquire) {
                self.request.mark_dispatch_done();
                self.request.retired.store(true, Ordering::Release);
            }
            self.request.discard_input();
            self.request.body_ended();
            if let Some(host) = self.request.host.upgrade() {
                host.reap();
            }
        }
    }
}
// A committed terminal already owns its queue slot and has no remaining wait
// before publication. Cancellation may stop an unresolved call, but must not
// replace that earlier winner while the ready Tonic response is being scheduled.
async fn response_or_cancel<F>(
    request: &RequestState,
    serving: F,
) -> Response<protocol::tonic::body::Body>
where
    F: Future<Output = Response<protocol::tonic::body::Body>>,
{
    tokio::pin!(serving);
    tokio::select! {
        biased;
        () = request.cancel.cancelled() => {
            if request.grpc.as_ref().is_some_and(|call| call.is_terminal()) {
                serving.await
            } else {
                cancellation_status(request).into_http()
            }
        },
        response = &mut serving => response,
    }
}
struct GrpcBody {
    terminal_head: bool,
    inner: Option<protocol::tonic::body::Body>,
    request: Arc<RequestState>,
    ended: bool,
}
impl GrpcBody {
    fn end(&mut self) {
        if !self.ended {
            self.ended = true;
            self.inner.take();
            self.request.body_ended();
            if let Some(host) = self.request.host.upgrade() {
                host.reap();
            }
        }
    }
}
impl Body for GrpcBody {
    type Data = Bytes;
    type Error = Status;
    fn poll_frame(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
    ) -> Poll<Option<std::result::Result<Frame<Bytes>, Status>>> {
        if self.ended {
            return Poll::Ready(None);
        }
        self.request.output_waker.register(cx.waker());
        if self.request.cancel.is_canceled()
            && !self.terminal_head
            && !self
                .request
                .grpc
                .as_ref()
                .is_some_and(|call| call.is_terminal())
        {
            let status = cancellation_status(&self.request);
            let mut trailers = HeaderMap::new();
            let result = status.add_header(&mut trailers).map(|()| {
                protocol::retain_headers(
                    &mut trailers,
                    self.request
                        .head
                        .grpc
                        .as_ref()
                        .expect("grpc metadata anchor")
                        .credit(),
                );
                Frame::trailers(trailers)
            });
            self.end();
            return Poll::Ready(Some(result));
        }
        let request = self.request.clone();
        request.diagnostics.in_scope(|| {
            match Pin::new(self.inner.as_mut().expect("live grpc body")).poll_frame(cx) {
                Poll::Ready(Some(Ok(mut frame))) => {
                    if let Some(trailers) = frame.trailers_mut() {
                        // Unary Tonic responses have an implicit OK trailer. Merge
                        // the admitted terminal metadata using Tonic's public Status writer.
                        if let Some(status) = request
                            .grpc
                            .as_ref()
                            .filter(|c| c.descriptor.method_kind.single_output())
                            .and_then(|c| c.terminal_status())
                            && let Err(error) = status.add_header(trailers)
                        {
                            self.end();
                            return Poll::Ready(Some(Err(error)));
                        }
                        let credit = request
                            .grpc
                            .as_ref()
                            .and_then(|c| c.terminal_credit())
                            .unwrap_or_else(|| {
                                request
                                    .head
                                    .grpc
                                    .as_ref()
                                    .expect("grpc metadata anchor")
                                    .credit()
                            });
                        protocol::retain_headers(trailers, credit);
                        self.end();
                    }
                    Poll::Ready(Some(Ok(frame)))
                }
                Poll::Ready(None) => {
                    self.end();
                    Poll::Ready(None)
                }
                Poll::Ready(Some(Err(error))) => {
                    self.end();
                    Poll::Ready(Some(Err(error)))
                }
                Poll::Pending => Poll::Pending,
            }
        })
    }
    fn is_end_stream(&self) -> bool {
        self.ended
    }
}
impl Drop for GrpcBody {
    fn drop(&mut self) {
        if !self.ended {
            self.request.abort(NW_CANCELED);
            self.end();
        }
    }
}

#[cfg(test)]
#[path = "host_tests.rs"]
mod tests;
