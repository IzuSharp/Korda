//! The bounded proof profile uses public Hyper Service/Body and Tokio APIs.
//! Native network tasks, live-stream permits and backing Bytes have distinct owners.
use bytes::Bytes;
use hyper::{
    Request, Response,
    body::{Body, Frame, Incoming},
};
use hyper_util::rt::TokioIo;
use nativeweave_interop_abi::*;
use nativeweave_interop_async::Operation;
use nativeweave_interop_callbacks::{Subscription, WorkLease as CallbackSource};
use nativeweave_interop_errors::{EngineBoundary, ErrorFields};
use std::{
    collections::HashMap,
    convert::Infallible,
    future::{Future, poll_fn},
    pin::Pin,
    sync::{
        Arc, Mutex, OnceLock, Weak,
        atomic::{AtomicU64, Ordering},
    },
    task::{Context, Poll},
    time::Duration,
};
use tokio::{
    net::{TcpListener, TcpStream},
    runtime::Handle,
    sync::{OwnedSemaphorePermit, Semaphore, mpsc, oneshot, watch},
};

pub(crate) static COUNTERS: [AtomicU64; 16] = [const { AtomicU64::new(0) }; 16];
pub(crate) fn count(index: usize) -> u64 {
    COUNTERS[index].load(Ordering::SeqCst)
}
fn add(index: usize) {
    COUNTERS[index].fetch_add(1, Ordering::SeqCst);
}
fn sub(index: usize) {
    COUNTERS[index].fetch_sub(1, Ordering::SeqCst);
}
pub(crate) fn lock<T>(mutex: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|p| p.into_inner())
}
pub(crate) async fn closed(mut receiver: watch::Receiver<bool>) {
    if *receiver.borrow() {
        return;
    }
    let _ = receiver.changed().await;
}

pub(crate) struct State {
    pub callbacks: Arc<Subscription>,
    pub handle: OnceLock<Handle>,
    pub port: u16,
    pub chunk_limit: usize,
    pub operations: Arc<Semaphore>,
    response_owners: Arc<Semaphore>,
    chunk_owners: Arc<Semaphore>,
    streams: Arc<Semaphore>,
    bytes: Arc<Semaphore>,
    connections: Arc<Semaphore>,
    control: Mutex<bool>, // ordinary admission and runtime shutdown share this gate
    pub stop: watch::Sender<bool>,
    requests: Mutex<HashMap<u64, PendingHandler>>,
    responses: Mutex<Vec<Weak<ResponseState>>>,
    next_request: AtomicU64,
    pub tasks: AtomicU64,
    pub(crate) boundary: EngineBoundary,
    #[cfg(test)]
    pub close_after_headers: std::sync::atomic::AtomicBool,
}

impl State {
    pub fn new(
        port: u16,
        streams: u32,
        operations: u32,
        bytes: u32,
        chunk_limit: u32,
    ) -> Arc<Self> {
        Arc::new(Self {
            callbacks: Arc::new(Subscription::new()),
            handle: OnceLock::new(),
            port,
            chunk_limit: chunk_limit as usize,
            operations: Arc::new(Semaphore::new(operations as usize)),
            response_owners: Arc::new(Semaphore::new(operations as usize)),
            chunk_owners: Arc::new(Semaphore::new(operations as usize)),
            streams: Arc::new(Semaphore::new(streams as usize)),
            bytes: Arc::new(Semaphore::new(bytes as usize)),
            connections: Arc::new(Semaphore::new(streams as usize * 4)),
            control: Mutex::new(false),
            stop: watch::channel(false).0,
            requests: Mutex::new(HashMap::new()),
            responses: Mutex::new(Vec::new()),
            next_request: AtomicU64::new(1),
            tasks: AtomicU64::new(0),
            boundary: EngineBoundary::new(),
            #[cfg(test)]
            close_after_headers: std::sync::atomic::AtomicBool::new(false),
        })
    }
    pub fn is_closing(&self) -> bool {
        *lock(&self.control)
    }
    pub fn close(&self) {
        *lock(&self.control) = true;
        self.stop.send_replace(true);
        self.callbacks.request_stop();
        // Queued/discarded managed notifications never claim these entries. A
        // claimed handler retains its source/stream slot until the managed ack.
        lock(&self.requests).retain(|_, request| request.claimed);
    }
    pub fn spawn(self: &Arc<Self>, future: impl Future<Output = ()> + Send + 'static) -> bool {
        let closing = lock(&self.control);
        if *closing {
            return false;
        }
        let Some(handle) = self.handle.get() else {
            return false;
        };
        self.tasks.fetch_add(1, Ordering::SeqCst);
        add(8);
        let guard = TaskGuard(self.clone());
        let protected = Guarded {
            future: Box::pin(future),
            state: self.clone(),
        };
        handle.spawn(async move {
            let _guard = guard;
            let _ = protected.await;
        });
        true
    }
    pub fn claim(&self, id: u64) -> u32 {
        let closing = lock(&self.control);
        if *closing {
            return NW_CANCELED;
        }
        let mut requests = lock(&self.requests);
        match requests.get_mut(&id) {
            Some(request) if !request.claimed => {
                request.claimed = true;
                NW_OK
            }
            _ => NW_INVALID_STATE,
        }
    }
    pub fn acknowledge(&self, id: u64) {
        lock(&self.requests).remove(&id);
    }
    pub fn reply(self: &Arc<Self>, id: u64, status: u16, payload: &[u8], delay_ms: u32) -> u32 {
        if self.is_closing() {
            return NW_CANCELED;
        }
        let (sender, slot) = {
            let mut requests = lock(&self.requests);
            let Some(request) = requests.get_mut(&id) else {
                return NW_INVALID_STATE;
            };
            if !request.claimed {
                return NW_INVALID_STATE;
            }
            let Some(sender) = request.reply.take() else {
                return NW_INVALID_STATE;
            };
            (sender, request.slot.clone())
        };
        let bytes = match self.owned_bytes(payload) {
            Ok(bytes) => bytes,
            Err(status) => return status,
        };
        let body = self.body(bytes, slot, delay_ms);
        let response = match Response::builder()
            .status(status)
            .header("connection", "close")
            .body(body)
        {
            Ok(response) => response,
            Err(_) => return NW_INVALID_ARGUMENT,
        };
        if sender.send(response).is_ok() {
            NW_OK
        } else {
            NW_INVALID_STATE
        }
    }
    fn owned_bytes(self: &Arc<Self>, input: &[u8]) -> Result<Bytes, u32> {
        if input.len() > self.chunk_limit {
            return Err(NW_INVALID_ARGUMENT);
        }
        if input.is_empty() {
            return Ok(Bytes::new());
        }
        // A whole maximum frame is reserved before allocation. This deliberately
        // conservative payload-capacity quota stays with the last Bytes clone.
        let permit = self
            .bytes
            .clone()
            .try_acquire_many_owned(self.chunk_limit as u32)
            .map_err(|_| NW_OUT_OF_MEMORY)?;
        let mut data = Vec::new();
        data.try_reserve_exact(input.len())
            .map_err(|_| NW_OUT_OF_MEMORY)?;
        if data.capacity() > self.chunk_limit {
            return Err(NW_OUT_OF_MEMORY);
        }
        data.extend_from_slice(input);
        COUNTERS[4].fetch_add(self.chunk_limit as u64, Ordering::SeqCst);
        Ok(Bytes::from_owner(ChargedBytes {
            data,
            _permit: permit,
            charged: self.chunk_limit as u64,
        }))
    }
    fn body(self: &Arc<Self>, bytes: Bytes, slot: Arc<StreamSlot>, delay_ms: u32) -> StreamBody {
        let (sender, receiver) = mpsc::channel(1);
        add(6);
        let body = StreamBody {
            receiver,
            slot: Some(slot.clone()),
            counted: true,
        };
        let stop = self.stop.subscribe();
        self.spawn(async move {
            let _producer_slot = slot;
            let produce = async {
                if bytes.is_empty() {
                    return;
                }
                let split = bytes.len().div_ceil(2);
                for chunk in [bytes.slice(..split), bytes.slice(split..)] {
                    if chunk.is_empty() {
                        continue;
                    }
                    if delay_ms != 0 {
                        tokio::time::sleep(Duration::from_millis(delay_ms as u64)).await;
                    }
                    let Ok(permit) = sender.reserve().await else {
                        break;
                    };
                    add(14);
                    permit.send(Frame::data(chunk));
                }
            };
            tokio::select! { _ = produce => {}, _ = closed(stop) => {} }
        });
        body
    }
    async fn service(
        self: Arc<Self>,
        request: Request<Incoming>,
    ) -> Result<Response<StreamBody>, Infallible> {
        let route = request
            .uri()
            .path()
            .trim_start_matches('/')
            .parse::<u32>()
            .unwrap_or(0);
        drop(request); // this smallest profile accepts GET routes; no request body API
        let permit = match self.streams.clone().try_acquire_owned() {
            Ok(permit) => permit,
            Err(_) => return Ok(StreamBody::empty_response(503)),
        };
        let source = match self.callbacks.reserve_source() {
            Ok(source) => source,
            Err(_) => return Ok(StreamBody::empty_response(503)),
        };
        add(5);
        let slot = Arc::new(StreamSlot { _permit: permit });
        let id = self.next_request.fetch_add(1, Ordering::SeqCst);
        let (sender, receiver) = oneshot::channel();
        {
            let closing = lock(&self.control);
            if *closing {
                return Ok(StreamBody::empty_response(503));
            }
            add(10);
            lock(&self.requests).insert(
                id,
                PendingHandler {
                    reply: Some(sender),
                    claimed: false,
                    slot,
                    _source: source,
                },
            );
        }
        let _request = RequestGuard {
            state: self.clone(),
            id,
        };
        let mut payload = [0u8; 12];
        payload[..8].copy_from_slice(&id.to_le_bytes());
        payload[8..].copy_from_slice(&route.to_le_bytes());
        // SAFETY: the A06 source and native invocation lease retain the valid
        // static callback/root; only this bounded stack payload is borrowed.
        let status = unsafe { self.callbacks.emit(1, &payload) };
        if status != NW_OK {
            self.close();
            return Ok(StreamBody::empty_response(503));
        }
        tokio::select! {
            response = receiver => Ok(response.unwrap_or_else(|_| StreamBody::empty_response(500))),
            _ = closed(self.stop.subscribe()) => Ok(StreamBody::empty_response(503)),
        }
    }
    pub async fn run(self: Arc<Self>, listener: std::net::TcpListener) {
        let listener = match TcpListener::from_std(listener) {
            Ok(listener) => listener,
            Err(_) => {
                self.close();
                return;
            }
        };
        loop {
            tokio::select! {
                _ = closed(self.stop.subscribe()) => break,
                accepted = listener.accept() => {
                    let Ok((socket, _)) = accepted else { add(11); continue; };
                    let Ok(permit) = self.connections.clone().try_acquire_owned() else { drop(socket); continue; };
                    let state = self.clone(); let stop = self.stop.subscribe();
                    self.spawn(async move {
                        let _permit = permit;
                        let service = hyper::service::service_fn(move |request| state.clone().service(request));
                        let connection = hyper::server::conn::http1::Builder::new().keep_alive(false).max_buf_size(8192)
                            .serve_connection(TokioIo::new(socket), service);
                        tokio::select! { result = connection => { if result.is_err() { add(11); } }, _ = closed(stop) => {} }
                    });
                }
            }
        }
        drop(listener);
        let responses: Vec<_> = lock(&self.responses)
            .iter()
            .filter_map(Weak::upgrade)
            .collect();
        for response in responses {
            response.close.send_replace(true);
            response.clear_body().await;
        }
        while self.tasks.load(Ordering::SeqCst) != 0 {
            tokio::time::sleep(Duration::from_millis(1)).await;
        }
    }
    pub async fn get(self: &Arc<Self>, route: u32) -> Result<NativeResult, u32> {
        let response_permit = self
            .response_owners
            .clone()
            .try_acquire_owned()
            .map_err(|_| NW_OUT_OF_MEMORY)?;
        let permit = self
            .connections
            .clone()
            .try_acquire_owned()
            .map_err(|_| NW_OUT_OF_MEMORY)?;
        let socket = TcpStream::connect((std::net::Ipv4Addr::LOCALHOST, self.port))
            .await
            .map_err(|_| NW_DOMAIN_ERROR)?;
        let (mut sender, connection) =
            hyper::client::conn::http1::handshake::<_, Empty>(TokioIo::new(socket))
                .await
                .map_err(|_| NW_DOMAIN_ERROR)?;
        let stop = self.stop.subscribe();
        if !self.spawn(async move {
            let _permit = permit;
            tokio::select! { _ = connection => {}, _ = closed(stop) => {} }
        }) {
            return Err(NW_CANCELED);
        }
        let request = Request::builder()
            .uri(format!("/{route}"))
            .header("host", "localhost")
            .header("connection", "close")
            .body(Empty)
            .map_err(|_| NW_INVALID_ARGUMENT)?;
        let response = sender
            .send_request(request)
            .await
            .map_err(|_| NW_DOMAIN_ERROR)?;
        let status = response.status().as_u16();
        #[cfg(test)]
        if self.close_after_headers.load(Ordering::Acquire) {
            self.close();
        }
        // Headers are not a publication right. Serialize the response registry
        // with close: a winner is registered before the close drain snapshots;
        // a loser drops Incoming immediately and transfers no live Body.
        let closing = lock(&self.control);
        if *closing {
            return Err(NW_CANCELED);
        }
        add(7);
        let body = Arc::new(ResponseState {
            state: self.clone(),
            status,
            body: tokio::sync::Mutex::new(Some(response.into_body())),
            close: watch::channel(false).0,
            counted: std::sync::atomic::AtomicBool::new(true),
            _permit: response_permit,
        });
        {
            let mut responses = lock(&self.responses);
            responses.retain(|entry| entry.strong_count() != 0);
            responses.push(Arc::downgrade(&body));
        }
        Ok(NativeResult::Response(body))
    }
}

struct TaskGuard(Arc<State>);
impl Drop for TaskGuard {
    fn drop(&mut self) {
        self.0.tasks.fetch_sub(1, Ordering::SeqCst);
        sub(8);
    }
}
struct Guarded<F> {
    future: Pin<Box<F>>,
    state: Arc<State>,
}
impl<F: Future> Future for Guarded<F> {
    type Output = Result<F::Output, u32>;
    fn poll(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Self::Output> {
        let this = self.get_mut();
        // Only this single poll is asserted unwind-safe. A failed native future
        // is never polled or inspected again: admission closes and it is dropped.
        // The shared A05 guard forgets unknown panic payloads and records poison.
        // These are already-admitted tasks. A different task's poison must still
        // let their close/cancel branch publish terminal state and drop owners.
        match this
            .state
            .boundary
            .cleanup(std::panic::AssertUnwindSafe(|| {
                Ok(this.future.as_mut().poll(cx))
            })) {
            Ok(Poll::Pending) => Poll::Pending,
            Ok(Poll::Ready(value)) => Poll::Ready(Ok(value)),
            Err(failure) => {
                this.state.close();
                Poll::Ready(Err(failure.status))
            }
        }
    }
}
struct StreamSlot {
    _permit: OwnedSemaphorePermit,
}
impl Drop for StreamSlot {
    fn drop(&mut self) {
        sub(5);
    }
}
struct ChargedBytes {
    data: Vec<u8>,
    _permit: OwnedSemaphorePermit,
    charged: u64,
}
impl AsRef<[u8]> for ChargedBytes {
    fn as_ref(&self) -> &[u8] {
        &self.data
    }
}
impl Drop for ChargedBytes {
    fn drop(&mut self) {
        COUNTERS[4].fetch_sub(self.charged, Ordering::SeqCst);
    }
}
struct PendingHandler {
    reply: Option<oneshot::Sender<Response<StreamBody>>>,
    claimed: bool,
    slot: Arc<StreamSlot>,
    _source: CallbackSource,
}
impl Drop for PendingHandler {
    fn drop(&mut self) {
        sub(10);
    }
}
struct RequestGuard {
    state: Arc<State>,
    id: u64,
}
impl Drop for RequestGuard {
    fn drop(&mut self) {
        let mut requests = lock(&self.state.requests);
        if requests
            .get(&self.id)
            .is_some_and(|request| !request.claimed)
        {
            requests.remove(&self.id);
        }
    }
}

struct Empty;
impl Body for Empty {
    type Data = Bytes;
    type Error = Infallible;
    fn poll_frame(
        self: Pin<&mut Self>,
        _: &mut Context<'_>,
    ) -> Poll<Option<Result<Frame<Bytes>, Infallible>>> {
        Poll::Ready(None)
    }
    fn is_end_stream(&self) -> bool {
        true
    }
}
struct StreamBody {
    receiver: mpsc::Receiver<Frame<Bytes>>,
    slot: Option<Arc<StreamSlot>>,
    counted: bool,
}
impl StreamBody {
    fn empty_response(status: u16) -> Response<Self> {
        let (sender, receiver) = mpsc::channel(1);
        drop(sender);
        let mut response = Response::new(Self {
            receiver,
            slot: None,
            counted: false,
        });
        *response.status_mut() = hyper::StatusCode::from_u16(status).expect("fixed valid status");
        response
    }
    fn retire(&mut self) {
        if self.counted {
            self.counted = false;
            sub(6);
        }
        self.slot.take();
    }
}
impl Body for StreamBody {
    type Data = Bytes;
    type Error = Infallible;
    fn poll_frame(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
    ) -> Poll<Option<Result<Frame<Bytes>, Infallible>>> {
        match self.receiver.poll_recv(cx) {
            Poll::Ready(Some(frame)) => {
                sub(14);
                Poll::Ready(Some(Ok(frame)))
            }
            Poll::Ready(None) => {
                self.retire();
                Poll::Ready(None)
            }
            Poll::Pending => Poll::Pending,
        }
    }
    fn is_end_stream(&self) -> bool {
        !self.counted
    }
}
impl Drop for StreamBody {
    fn drop(&mut self) {
        self.receiver.close();
        while self.receiver.try_recv().is_ok() {
            sub(14);
        }
        self.retire();
    }
}

pub(crate) struct ResponseState {
    pub state: Arc<State>,
    pub status: u16,
    body: tokio::sync::Mutex<Option<Incoming>>,
    pub close: watch::Sender<bool>,
    counted: std::sync::atomic::AtomicBool,
    _permit: OwnedSemaphorePermit,
}
impl ResponseState {
    fn uncount(&self) {
        if self.counted.swap(false, Ordering::SeqCst) {
            sub(7);
        }
    }
    pub async fn clear_body(&self) {
        self.body.lock().await.take();
        self.uncount();
    }
    pub async fn read(self: &Arc<Self>) -> Result<NativeResult, u32> {
        if *self.close.borrow() {
            return Err(NW_CANCELED);
        }
        let permit = self
            .state
            .chunk_owners
            .clone()
            .try_acquire_owned()
            .map_err(|_| NW_OUT_OF_MEMORY)?;
        let mut body = self.body.lock().await;
        let Some(incoming) = body.as_mut() else {
            return Ok(NativeResult::Eof);
        };
        let frame = poll_fn(|cx| Pin::new(&mut *incoming).poll_frame(cx)).await;
        match frame {
            None => {
                body.take();
                self.uncount();
                Ok(NativeResult::Eof)
            }
            Some(Err(_)) => {
                body.take();
                self.uncount();
                Err(NW_DOMAIN_ERROR)
            }
            Some(Ok(frame)) => {
                let data = frame.into_data().map_err(|_| NW_CONTRACT_VIOLATION)?;
                let bytes = self.state.owned_bytes(&data)?;
                Ok(NativeResult::Chunk(bytes, permit))
            }
        }
    }
}
impl Drop for ResponseState {
    fn drop(&mut self) {
        self.uncount();
    }
}

pub(crate) enum NativeResult {
    Response(Arc<ResponseState>),
    Chunk(Bytes, OwnedSemaphorePermit),
    Eof,
}
pub(crate) enum RequestKind {
    Get(u32),
    Read(Arc<ResponseState>),
}
pub(crate) struct OperationState {
    pub operation: Arc<Operation<NativeResult>>,
    pub state: Arc<State>,
    pub kind: RequestKind,
    pub cancel: watch::Sender<bool>,
    pub _permit: OwnedSemaphorePermit,
}
impl OperationState {
    pub fn start(self: &Arc<Self>, callbacks: NwOperationCallbacksV1) -> u32 {
        if self.state.is_closing() {
            return NW_CANCELED;
        }
        let status = self.operation.start(callbacks);
        if status != NW_OK {
            return status;
        }
        let source = match self.operation.reserve_source() {
            Ok(source) => source,
            Err(status) => return status,
        };
        let this = self.clone();
        let accepted = self.state.spawn(async move {
            let _source = source;
            let work = async {
                match &this.kind {
                    RequestKind::Get(route) => tokio::select! {
                        result = this.state.get(*route) => result,
                        _ = closed(this.cancel.subscribe()) => Err(NW_CANCELED),
                        _ = closed(this.state.stop.subscribe()) => Err(NW_CANCELED),
                    },
                    RequestKind::Read(response) => {
                        let result = tokio::select! {
                            result = response.read() => result,
                            _ = closed(this.cancel.subscribe()) => Err(NW_CANCELED),
                            _ = closed(this.state.stop.subscribe()) => Err(NW_CANCELED),
                            _ = closed(response.close.subscribe()) => Err(NW_CANCELED),
                        };
                        if result.is_err() {
                            response.close.send_replace(true);
                            response.clear_body().await;
                        }
                        result
                    }
                }
            };
            let result = match (Guarded {
                future: Box::pin(work),
                state: this.state.clone(),
            })
            .await
            {
                Ok(result) => result,
                Err(status) => Err(status),
            };
            let (status, result) = match result {
                Ok(result) => (NW_OK, Some(result)),
                Err(status) => (status, None),
            };
            // SAFETY: accepted native source and Operation's callback lease keep
            // its exact managed A07 cookie/table live through terminal return.
            unsafe {
                this.operation.complete(
                    status,
                    result,
                    ErrorFields {
                        domain: 0x48545450,
                        code: if status == NW_OK { 0 } else { 1 },
                        correlation_id: 0,
                    },
                )
            };
            add(12);
        });
        if !accepted {
            // Start already accepted native responsibility. Report cancellation
            // through terminal, even when host closure wins before task enqueue.
            // SAFETY: start-call control lease retains the A07 context/table.
            unsafe {
                self.operation
                    .complete(NW_CANCELED, None, ErrorFields::default())
            };
        }
        NW_OK
    }
}
