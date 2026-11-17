use crate::{
    ApiError, Result,
    config::{Config, Route, TlsSnapshot},
    invalid, lock,
    metadata::{self, Head},
    wire::*,
};
use axum::{
    Router,
    body::Body as AxumBody,
    extract::{RawPathParams, Request as AxumRequest},
    routing::any,
};
use bytes::Bytes;
use futures_util::{FutureExt, task::AtomicWaker};
use http::{HeaderMap, Method, Request, Response, StatusCode};
use http_body::{Body, Frame, SizeHint};
use hyper::service::service_fn;
use hyper_util::{
    rt::{TokioExecutor, TokioIo, TokioTimer},
    server::conn::auto::Builder,
};
use nativeweave_interop_abi::*;
use nativeweave_interop_async::{Operation, WorkLease};
use nativeweave_server_diagnostics::{Diagnostics, RequestTrace};
use nativeweave_server_streaming::{
    BodyReader, BodySender, BoundedBody, Budget, Cancellation, OwnedBuffer, PreparedWrite,
    ReadFrame, RequestBudget, StreamError, WriteReservation, body_channel,
};
use std::{
    collections::{HashMap, HashSet},
    convert::Infallible,
    net::SocketAddr,
    pin::Pin,
    sync::{
        Arc, Mutex, Weak,
        atomic::{AtomicBool, AtomicU32, AtomicU64, AtomicUsize, Ordering},
    },
    task::{Context, Poll},
    thread::JoinHandle,
    time::{Duration, Instant},
};
use tokio::{
    net::TcpListener,
    runtime::Handle,
    sync::{Notify, OwnedSemaphorePermit, Semaphore, oneshot},
    task::JoinSet,
};
use tower::ServiceExt;

#[path = "../../../protocols/grpc/host.rs"]
pub mod grpc;
#[path = "../../../protocols/mcp/host.rs"]
pub mod mcp;

pub struct Host {
    pub diagnostics: Diagnostics,
    pub config: Config,
    pub dispatch: NwServerDispatchV1,
    pub state: AtomicU32,
    pub budget: Arc<Budget>,
    pub routes: Mutex<Option<Vec<Route>>>,
    pub requests: Mutex<HashMap<u64, Arc<RequestState>>>,
    pub addresses: Mutex<Vec<(String, String)>>,
    pub worker: Mutex<Option<JoinHandle<()>>>,
    pub runtime: Mutex<Option<Handle>>,
    pub stop: Cancellation,
    pub abort: Cancellation,
    pub active: Arc<Semaphore>,
    pub queued: Arc<Semaphore>,
    pub connections: Arc<Semaphore>,
    pub operation_slots: Arc<Semaphore>,
    pub live_ops: AtomicUsize,
    pub retiring: AtomicUsize,
    pub callbacks: AtomicUsize,
    pub rejected: AtomicU64,
    pub callback_failures: AtomicU64,
    pub next_id: AtomicU64,
    pub worker_exited: AtomicBool,
    pub native_joined: AtomicBool,
    pub changed: Notify,
}
impl Host {
    pub fn new(config: Config, dispatch: NwServerDispatchV1) -> Result<Arc<Self>> {
        let budget = Budget::new(config.native_payload_bytes, config.managed_payload_bytes)?;
        let active = Arc::new(Semaphore::new(config.active_requests));
        let queued = Arc::new(Semaphore::new(config.queued_requests));
        let connections = Arc::new(Semaphore::new(config.max_connections));
        let slots = config
            .active_requests
            .checked_mul(8)
            .and_then(|x| x.checked_add(32))
            .filter(|x| *x <= Semaphore::MAX_PERMITS)
            .ok_or(invalid())?;
        Ok(Arc::new(Self {
            diagnostics: Diagnostics::new(),
            config,
            dispatch,
            state: AtomicU32::new(0),
            budget,
            routes: Mutex::new(None),
            requests: Mutex::new(HashMap::new()),
            addresses: Mutex::new(Vec::new()),
            worker: Mutex::new(None),
            runtime: Mutex::new(None),
            stop: Cancellation::default(),
            abort: Cancellation::default(),
            active,
            queued,
            connections,
            operation_slots: Arc::new(Semaphore::new(slots)),
            live_ops: AtomicUsize::new(0),
            retiring: AtomicUsize::new(0),
            callbacks: AtomicUsize::new(0),
            rejected: AtomicU64::new(0),
            callback_failures: AtomicU64::new(0),
            next_id: AtomicU64::new(1),
            worker_exited: AtomicBool::new(false),
            native_joined: AtomicBool::new(false),
            changed: Notify::new(),
        }))
    }
    pub fn reap(&self) {
        lock(&self.requests).retain(|_, r| !r.fully_retired());
        self.changed.notify_waiters();
    }
    pub fn notify_request(&self, request: &RequestState) -> u32 {
        self.callbacks.fetch_add(1, Ordering::AcqRel);
        // SAFETY: create validated the nonzero exact C ABI entry. Host/root survive until native retirement.
        let callback: unsafe extern "C" fn(usize, u64, u64, u32) -> u32 =
            unsafe { std::mem::transmute(self.dispatch.on_request) };
        // SAFETY: only fixed ticket metadata is delivered, and the managed root remains registered.
        let result = unsafe {
            callback(
                self.dispatch.cookie,
                request.head.id,
                request.head.handler,
                request.head.kind,
            )
        };
        self.callbacks.fetch_sub(1, Ordering::AcqRel);
        if result != NW_OK {
            self.callback_failures.fetch_add(1, Ordering::Relaxed);
        }
        result
    }
    pub fn control(&self, kind: u32, status: u32, ticket: u64) {
        self.callbacks.fetch_add(1, Ordering::AcqRel);
        // SAFETY: exact callback table was validated and copied synchronously at create.
        let callback: unsafe extern "C" fn(usize, u32, u32, u64, *const NwErrorInfoV1) -> u32 =
            unsafe { std::mem::transmute(self.dispatch.on_control) };
        let error = NwErrorInfoV1 {
            struct_size: 24,
            domain: 0x53525652,
            code: 4,
            flags: 0,
            correlation_id: 0,
        };
        // SAFETY: borrowed fixed error stays on this stack; callbacks cannot outlive the retained host.
        let result = unsafe { callback(self.dispatch.cookie, kind, status, ticket, &error) };
        self.callbacks.fetch_sub(1, Ordering::AcqRel);
        if result != NW_OK {
            self.callback_failures.fetch_add(1, Ordering::Relaxed);
        }
    }
    pub fn fault(&self, status: u32) {
        let previous = self.state.swap(5, Ordering::AcqRel);
        self.stop.cancel();
        self.abort.cancel();
        if previous != 5 {
            self.control(2, status, 0);
        }
    }
    pub fn start(
        self: &Arc<Self>,
        operation: Arc<OpState>,
        source: WorkLease<OpResult>,
    ) -> Result<()> {
        if self
            .state
            .compare_exchange(0, 1, Ordering::AcqRel, Ordering::Acquire)
            .is_err()
        {
            return Err(ApiError::status(NW_INVALID_STATE));
        }
        let host = self.clone();
        let fallback = Arc::downgrade(&operation);
        let worker = std::thread::Builder::new()
            .name("nativeweave-server".into())
            .spawn(move || {
                let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                    let runtime = tokio::runtime::Builder::new_current_thread()
                        .enable_all()
                        .build()
                        .map_err(|_| ApiError::domain(3))?;
                    *lock(&host.runtime) = Some(runtime.handle().clone());
                    runtime.block_on(
                        host.diagnostics
                            .instrument(host.clone().run(operation, source)),
                    );
                    drop(runtime);
                    Ok::<(), ApiError>(())
                }));
                *lock(&host.runtime) = None;
                if !matches!(result, Ok(Ok(()))) {
                    host.fault(NW_NATIVE_PANIC);
                    if let Some(operation) = fallback.upgrade() {
                        operation.complete(Err(ApiError::status(NW_NATIVE_PANIC)));
                    }
                }
                host.worker_exited.store(true, Ordering::Release);
                host.changed.notify_waiters();
            })
            .map_err(|_| {
                self.state.store(5, Ordering::Release);
                self.worker_exited.store(true, Ordering::Release);
                self.native_joined.store(true, Ordering::Release);
                ApiError::domain(3)
            })?;
        *lock(&self.worker) = Some(worker);
        Ok(())
    }
    async fn run(self: Arc<Self>, operation: Arc<OpState>, source: WorkLease<OpResult>) {
        let mut listeners = Vec::new();
        for (index, listener) in self.config.listeners.iter().enumerate() {
            if operation.cancel.is_canceled() || self.stop.is_canceled() {
                drop(listeners);
                self.state.store(5, Ordering::Release);
                operation.complete(Err(ApiError::status(NW_CANCELED)));
                return;
            }
            match TcpListener::bind(listener.address).await {
                Ok(socket) => {
                    let address = socket.local_addr().expect("bound listener address");
                    listeners.push((index, socket, address));
                }
                Err(_) => {
                    drop(listeners);
                    self.state.store(5, Ordering::Release);
                    operation.complete(Err(ApiError::domain(3)));
                    return;
                }
            }
        }
        let router = match self.router() {
            Ok(router) => router,
            Err(e) => {
                drop(listeners);
                self.state.store(5, Ordering::Release);
                operation.complete(Err(e));
                return;
            }
        };
        *lock(&self.addresses) = listeners
            .iter()
            .map(|(i, _, address)| {
                let l = &self.config.listeners[*i];
                let tls = l.tls.read().unwrap_or_else(|p| p.into_inner()).is_some();
                (
                    l.name.clone(),
                    format!("{}://{address}/", if tls { "https" } else { "http" }),
                )
            })
            .collect();
        let mut acceptors = JoinSet::new();
        for (index, listener, _) in listeners {
            let host = self.clone();
            let router = router.clone();
            acceptors.spawn(
                self.diagnostics
                    .instrument(async move { host.accept(index, listener, router).await }),
            );
        }
        self.state.store(2, Ordering::Release);
        operation.complete(Ok(OpResult::Void));
        drop(source);
        drop(operation);
        loop {
            tokio::select! {
                ()=self.stop.cancelled()=>break,
                result=acceptors.join_next()=>{if result.is_some(){self.fault(NW_DOMAIN_ERROR);}break;},
                ()=tokio::time::sleep(Duration::from_millis(10))=>self.reap(),
            }
        }
        self.stop.cancel();
        while acceptors.join_next().await.is_some() {}
        self.reap();
    }
    fn router(self: &Arc<Self>) -> Result<Router> {
        let routes = lock(&self.routes)
            .clone()
            .ok_or(ApiError::status(NW_INVALID_STATE))?;
        let mut groups: HashMap<String, Vec<Route>> = HashMap::new();
        for route in routes {
            groups
                .entry(route.canonical.clone())
                .or_default()
                .push(route);
        }
        let mut router = Router::new();
        for (path, routes) in groups {
            let host = self.clone();
            let routes = Arc::new(routes);
            router = router.route(
                &path,
                any(move |params: RawPathParams, request: AxumRequest| {
                    let host = host.clone();
                    let routes = routes.clone();
                    async move { host.dispatch(routes, params, request).await }
                }),
            );
        }
        Ok(router.fallback(grpc::fallback))
    }
    async fn accept(self: Arc<Self>, index: usize, listener: TcpListener, router: Router) {
        let mut connections = JoinSet::new();
        loop {
            tokio::select! {
                biased;()=self.stop.cancelled()=>break,
                result=listener.accept()=>{
                    let Ok((stream,peer))=result else{self.fault(NW_DOMAIN_ERROR);break;};
                    if stream.set_nodelay(true).is_err() { drop(stream); continue; }
                    let Ok(permit)=self.connections.clone().try_acquire_owned()else{self.rejected.fetch_add(1,Ordering::Relaxed);drop(stream);continue;};
                    let tls=self.config.listeners[index].tls.read().unwrap_or_else(|p|p.into_inner()).clone();let host=self.clone();let router=router.clone();
                    connections.spawn(self.diagnostics.instrument(async move{
                        let _permit=permit;
                        if let Some(tls)=tls{
                            let Ok(_handshake)=tls.handshakes.clone().try_acquire_owned()else{return;};
                            let accept=tokio_rustls::TlsAcceptor::from(tls.config.clone()).accept(stream);
                            let stream=tokio::select!{()=host.stop.cancelled()=>return,result=tokio::time::timeout(tls.handshake_timeout,accept)=>match result{Ok(Ok(v))=>v,_=>return}};
                            drop(_handshake);let negotiated=stream.get_ref().1.alpn_protocol();let protocols=host.config.listeners[index].protocols;
                            if protocols==2 && negotiated!=Some(b"h2".as_slice()){return;}
                            host.serve(stream,peer,index,true,router).await;
                        }else{host.serve(stream,peer,index,false,router).await;}
                    }));
                },
                _=connections.join_next(),if !connections.is_empty()=>{},
            }
        }
        drop(listener);
        while connections.join_next().await.is_some() {}
    }
    async fn serve<T>(
        self: Arc<Self>,
        stream: T,
        peer: SocketAddr,
        index: usize,
        tls: bool,
        router: Router,
    ) where
        T: tokio::io::AsyncRead + tokio::io::AsyncWrite + Unpin + Send + 'static,
    {
        let life = Arc::new(ConnectionLife {
            active: AtomicUsize::new(0),
            last: Mutex::new(Instant::now()),
            changed: Notify::new(),
        });
        let mut builder = Builder::new(TokioExecutor::new());
        builder
            .http1()
            .timer(TokioTimer::new())
            .header_read_timeout(self.config.header_timeout)
            .max_headers(200)
            .max_buf_size(
                self.config
                    .max_header_bytes
                    .max(self.config.max_chunk_bytes)
                    .max(8192),
            );
        builder
            .http2()
            .timer(TokioTimer::new())
            .max_concurrent_streams(self.config.max_h2_streams)
            .max_header_list_size(self.config.max_header_bytes as u32)
            .initial_stream_window_size(
                (self.config.max_chunk_bytes as u32)
                    .saturating_mul(2)
                    .max(1),
            )
            .initial_connection_window_size(
                (self.config.max_chunk_bytes as u32)
                    .saturating_mul(2)
                    .max(1),
            )
            .max_frame_size((self.config.max_chunk_bytes as u32).clamp(16384, 16_777_215));
        let builder = match self.config.listeners[index].protocols {
            1 => builder.http1_only(),
            2 => builder.http2_only(),
            _ => builder,
        };
        let host = self.clone();
        let callback_life = life.clone();
        let service = service_fn(move |mut request: Request<hyper::body::Incoming>| {
            let host = host.clone();
            let router = router.clone();
            let life = callback_life.clone();
            async move {
                *lock(&life.last) = Instant::now();
                if host.stop.is_canceled() {
                    return Ok::<_, Infallible>(simple(StatusCode::SERVICE_UNAVAILABLE));
                }
                if metadata::validate_request(&request, &host.config).is_err() {
                    return Ok(simple(StatusCode::BAD_REQUEST));
                }
                request.extensions_mut().insert(Peer {
                    address: peer,
                    tls,
                    life,
                });
                let response = router
                    .oneshot(request.map(AxumBody::new))
                    .await
                    .unwrap_or_else(|never| match never {});
                Ok(response)
            }
        });
        let connection = builder.serve_connection(TokioIo::new(stream), service);
        tokio::pin!(connection);
        tokio::select! {
            _=&mut connection=>{},
            ()=self.abort.cancelled()=>{},
            ()=life.idle(self.config.keepalive)=>{},
            ()=self.stop.cancelled()=>{connection.as_mut().graceful_shutdown();tokio::select!{_=&mut connection=>{},()=self.abort.cancelled()=>{}}},
        }
    }
    async fn dispatch(
        self: Arc<Self>,
        routes: Arc<Vec<Route>>,
        parameters: RawPathParams,
        request: AxumRequest,
    ) -> Response<AxumBody> {
        let method = request.method();
        let route = routes
            .iter()
            .find(|r| r.methods.contains(method))
            .or_else(|| {
                if *method == Method::HEAD {
                    routes.iter().find(|r| r.methods.contains(&Method::GET))
                } else {
                    None
                }
            });
        let Some(route) = route else {
            let mut response = simple(StatusCode::METHOD_NOT_ALLOWED);
            let mut methods: HashSet<String> = routes
                .iter()
                .flat_map(|r| r.methods.iter().map(ToString::to_string))
                .collect();
            if methods.contains("GET") {
                methods.insert("HEAD".into());
            }
            let mut methods: Vec<_> = methods.into_iter().collect();
            methods.sort();
            if let Ok(value) = methods.join(", ").parse() {
                response.headers_mut().insert(http::header::ALLOW, value);
            }
            return response;
        };
        if route.kind == 1 {
            return grpc::dispatch(self, route.clone(), parameters, request).await;
        }
        let deadline = Instant::now() + route.timeout;
        if let Some(profile) = &route.mcp
            && let Err(status) = mcp::headers(&self, profile, &request)
        {
            return simple(status);
        }
        let peer = request
            .extensions()
            .get::<Peer>()
            .expect("native peer extension")
            .clone();
        let params = parameters
            .iter()
            .map(|(a, b)| (a.to_owned(), b.to_owned()))
            .collect();
        let id = self.next_id.fetch_add(1, Ordering::Relaxed);
        if id == 0 {
            self.fault(NW_CONTRACT_VIOLATION);
            return simple(StatusCode::SERVICE_UNAVAILABLE);
        }
        let mut head = match metadata::head(
            &request,
            route,
            &self.config,
            id,
            peer.address,
            peer.tls,
            params,
            deadline,
        ) {
            Ok(v) => v,
            Err(e) => {
                return simple(if e.code == 2 {
                    StatusCode::PAYLOAD_TOO_LARGE
                } else {
                    StatusCode::BAD_REQUEST
                });
            }
        };
        let active = match self.active.clone().try_acquire_owned() {
            Ok(v) => v,
            Err(_) => {
                let Ok(_queued) = self.queued.clone().try_acquire_owned() else {
                    self.rejected.fetch_add(1, Ordering::Relaxed);
                    return simple(StatusCode::SERVICE_UNAVAILABLE);
                };
                tokio::select! {()=self.stop.cancelled()=>return simple(StatusCode::SERVICE_UNAVAILABLE),result=tokio::time::timeout_at(deadline.into(),self.active.clone().acquire_owned())=>match result{Ok(Ok(v))=>v,_=>return simple(StatusCode::REQUEST_TIMEOUT)}}
            }
        };
        if self.stop.is_canceled() {
            return simple(StatusCode::SERVICE_UNAVAILABLE);
        }
        let budget = match self.budget.request(self.config.request_retained_bytes) {
            Ok(v) => v,
            Err(_) => return simple(StatusCode::SERVICE_UNAVAILABLE),
        };
        // SSE formatter backing/growth has a conservative peak above its visible
        // chunk length. Raw operations independently enforce MaxChunkBytes.
        let write_capacity = if route.kind == 2 {
            self.config.request_retained_bytes as usize
        } else {
            self.config
                .max_chunk_bytes
                .saturating_mul(3)
                .min(self.config.request_retained_bytes as usize)
        };
        let (sender, body) = match body_channel(budget.clone(), write_capacity, 1) {
            Ok(v) => v,
            Err(_) => return simple(StatusCode::SERVICE_UNAVAILABLE),
        };
        let captured = self.diagnostics.capture(request.headers());
        let diagnostics = self.diagnostics.request(
            &route.canonical,
            if head.protocol == 2 {
                "HTTP/2"
            } else {
                "HTTP/1.1"
            },
            captured.context,
        );
        let mutable_head = Arc::get_mut(&mut head).expect("unshared admitted request metadata");
        mutable_head.trace = captured.encoded();
        mutable_head.trace_state = captured.state;
        let (parts, input_body) = request.into_parts();
        let mcp = route
            .mcp
            .as_ref()
            .map(|profile| mcp::State::new(profile.clone(), parts));
        let body_limit = Arc::new(AtomicBool::new(false));
        let reader = BodyReader::new(
            ChunkedInput {
                body: input_body,
                pending: None,
                maximum: self.config.max_chunk_bytes,
                received: 0,
                limit: route.max_request_bytes,
                exceeded: body_limit.clone(),
            },
            budget.clone(),
            self.config.max_chunk_bytes,
        )
        .expect("validated request budget");
        let (tx, rx) = oneshot::channel();
        peer.life.active.fetch_add(1, Ordering::AcqRel);
        peer.life.changed.notify_waiters();
        let request = Arc::new(RequestState {
            host: Arc::downgrade(&self),
            diagnostics,
            body_limit,
            head,
            reader: tokio::sync::Mutex::new(Some(reader)),
            sender,
            budget,
            response: Mutex::new(ResponseState {
                phase: 0,
                status: 200,
                length: None,
                written: 0,
                sse: false,
                head: Some(tx),
            }),
            cancel: Cancellation::default(),
            input_cancel: Cancellation::default(),
            output_waker: AtomicWaker::new(),
            finished: Arc::new(Cancellation::default()),
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
            mcp,
            grpc: None,
        });
        lock(&self.requests).insert(id, request.clone());
        if request
            .diagnostics
            .in_scope(|| self.notify_request(&request))
            != NW_OK
        {
            request.mark_dispatch_done();
            request.retired.store(true, Ordering::Release);
            request.cancel.cancel();
            request.body_ended();
            self.reap();
            return simple(StatusCode::INTERNAL_SERVER_ERROR);
        }
        let timer_request = Arc::downgrade(&request);
        let timer_host = self.clone();
        let finished = request.finished.clone();
        tokio::spawn(request.diagnostics.instrument(async move {
            tokio::select! {()=tokio::time::sleep_until(deadline.into())=>{if let Some(r)=timer_request.upgrade(){r.abort(NW_TIMEOUT);}},()=timer_host.abort.cancelled()=>{if let Some(r)=timer_request.upgrade(){r.abort(NW_CANCELED);}},()=finished.cancelled()=>{}}
        }));
        let mut waiting = Waiting {
            request: request.clone(),
            committed: false,
        };
        let head = request
            .diagnostics
            .instrument(async {
                tokio::select! {result=rx=>result.ok(),()=request.cancel.cancelled()=>None}
            })
            .await;
        let Some((status, headers)) = head else {
            request.body_ended();
            return simple(
                if request.head.cancellation.load(Ordering::Acquire) == NW_TIMEOUT {
                    StatusCode::REQUEST_TIMEOUT
                } else {
                    StatusCode::INTERNAL_SERVER_ERROR
                },
            );
        };
        waiting.committed = true;
        let state = lock(&request.response);
        let suppress = request.head.method == "HEAD" || matches!(status, 204 | 205 | 304);
        let expected = if suppress { None } else { state.length };
        drop(state);
        let output = if suppress {
            request.body_ended();
            drop(body);
            AxumBody::empty()
        } else {
            AxumBody::new(OutputBody {
                body,
                request,
                expected,
                sent: 0,
                suppress,
                ended: false,
            })
        };
        let mut response = Response::new(output);
        *response.status_mut() = StatusCode::from_u16(status).expect("validated final status");
        *response.headers_mut() = headers;
        response
    }
    pub fn initiate_close(&self) {
        let old = self.state.load(Ordering::Acquire);
        if old == 0 {
            self.state.store(4, Ordering::Release);
            self.native_joined.store(true, Ordering::Release);
        } else if old != 4 && old != 5 {
            self.state.store(3, Ordering::Release);
        }
        self.stop.cancel();
    }
    pub fn join_if_finished(&self) -> bool {
        let mut worker = lock(&self.worker);
        if worker.as_ref().is_some_and(JoinHandle::is_finished) {
            if let Some(w) = worker.take() {
                let _ = w.join();
            }
            self.native_joined.store(true, Ordering::Release);
        }
        self.native_joined.load(Ordering::Acquire)
    }
    pub fn close_result(&self) -> NwServerCloseResultV1 {
        self.reap();
        let requests = lock(&self.requests).len();
        let (native, managed) = self.budget.snapshot();
        let completed = self.join_if_finished()
            && requests == 0
            && native == 0
            && managed == 0
            && self.callbacks.load(Ordering::Acquire) == 0
            && self.live_ops.load(Ordering::Acquire) <= 1;
        if completed && self.state.load(Ordering::Acquire) != 5 {
            self.state.store(4, Ordering::Release);
        }
        NwServerCloseResultV1 {
            struct_size: size_of::<NwServerCloseResultV1>() as u32,
            version: 1,
            close_status: u32::from(!completed),
            pending_requests: requests.min(u32::MAX as usize) as u32,
            retained_managed_bytes: managed,
            retained_native_bytes: native,
        }
    }
}

#[derive(Clone)]
struct Peer {
    address: SocketAddr,
    tls: bool,
    life: Arc<ConnectionLife>,
}
struct ConnectionLife {
    active: AtomicUsize,
    last: Mutex<Instant>,
    changed: Notify,
}
impl ConnectionLife {
    async fn idle(&self, timeout: Duration) {
        loop {
            let notified = self.changed.notified();
            tokio::pin!(notified);
            notified.as_mut().enable();
            if self.active.load(Ordering::Acquire) == 0 {
                let deadline = *lock(&self.last) + timeout;
                tokio::select! {()=tokio::time::sleep_until(deadline.into())=>{if self.active.load(Ordering::Acquire)==0{return;}},()=&mut notified=>{}}
            } else {
                notified.await;
            }
        }
    }
}
fn simple(status: StatusCode) -> Response<AxumBody> {
    let mut response = Response::new(AxumBody::empty());
    *response.status_mut() = status;
    response
}
struct Waiting {
    request: Arc<RequestState>,
    committed: bool,
}
impl Drop for Waiting {
    fn drop(&mut self) {
        if !self.committed {
            self.request.abort(NW_CANCELED);
            self.request.body_ended();
        }
    }
}
pub struct RequestState {
    pub mcp: Option<mcp::State>,
    pub grpc: Option<Arc<nativeweave_protocols_grpc::Call>>,
    pub diagnostics: RequestTrace,
    body_limit: Arc<AtomicBool>,
    pub host: Weak<Host>,
    pub head: Arc<Head>,
    pub reader: tokio::sync::Mutex<Option<BodyReader<ChunkedInput>>>,
    pub sender: BodySender,
    pub budget: Arc<RequestBudget>,
    pub response: Mutex<ResponseState>,
    pub cancel: Cancellation,
    pub input_cancel: Cancellation,
    output_waker: AtomicWaker,
    pub finished: Arc<Cancellation>,
    pub dispatch_done: AtomicBool,
    pub taken: AtomicBool,
    pub retired: AtomicBool,
    pub body_done: AtomicBool,
    connection_done: AtomicBool,
    pub read_active: Arc<AtomicBool>,
    pub write_active: Arc<AtomicBool>,
    pub live_ops: AtomicUsize,
    pub controls: Mutex<ControlGate>,
    _active: OwnedSemaphorePermit,
    life: Arc<ConnectionLife>,
}
// A panic permanently closes the host before any affected Body is reused. ABI
// entries touch the reader only through the async mutex; trusted cleanup drops
// the Body and never resumes a panicked future. This explicit instance marker
// does not grant unwind safety to arbitrary user or upstream callback state.
impl std::panic::RefUnwindSafe for RequestState {}
impl RequestState {
    pub fn mark_dispatch_done(&self) -> bool {
        if self.dispatch_done.swap(true, Ordering::AcqRel) {
            return false;
        }
        if let Some(call) = &self.grpc {
            call.stop_input();
        }
        grpc::release_metadata_if_done(self);
        if let Some(mcp) = &self.mcp {
            mcp.close();
        }
        if let Some(host) = self.host.upgrade() {
            host.retiring.fetch_add(1, Ordering::AcqRel);
        }
        true
    }
    pub fn business(&self) -> Result<()> {
        if self.dispatch_done.load(Ordering::Acquire) || self.retired.load(Ordering::Acquire) {
            Err(ApiError::status(NW_INVALID_STATE))
        } else if self.cancel.is_canceled() {
            Err(ApiError::status(
                self.head
                    .cancellation
                    .load(Ordering::Acquire)
                    .max(NW_CANCELED),
            ))
        } else {
            Ok(())
        }
    }
    pub fn abort(&self, status: u32) {
        let status = if status == NW_TIMEOUT {
            NW_TIMEOUT
        } else {
            NW_CANCELED
        };
        if self
            .head
            .cancellation
            .compare_exchange(0, status, Ordering::AcqRel, Ordering::Acquire)
            .is_ok()
        {
            let mut gate = lock(&self.controls);
            if gate.open {
                gate.active += 1;
                drop(gate);
                if let Some(host) = self.host.upgrade() {
                    host.control(1, status, self.head.id);
                }
                lock(&self.controls).active -= 1;
            }
        }
        self.cancel.cancel();
        self.input_cancel.cancel();
        self.output_waker.wake();
    }
    fn release_connection(&self) {
        if !self.connection_done.swap(true, Ordering::AcqRel) {
            self.life.active.fetch_sub(1, Ordering::AcqRel);
            *lock(&self.life.last) = Instant::now();
            self.life.changed.notify_waiters();
        }
    }
    pub fn body_ended(&self) {
        self.body_done.store(true, Ordering::Release);
        grpc::release_metadata_if_done(self);
        self.release_connection();
        if self.dispatch_done.load(Ordering::Acquire) {
            self.finished.cancel();
        }
    }
    pub fn discard_input(self: &Arc<Self>) {
        if self.grpc.is_some() {
            grpc::discard(self);
            return;
        }
        self.input_cancel.cancel();
        if let Ok(mut reader) = self.reader.try_lock() {
            reader.take();
            return;
        }
        if let Some(host) = self.host.upgrade()
            && let Some(runtime) = lock(&host.runtime).clone()
        {
            self.live_ops.fetch_add(1, Ordering::AcqRel);
            let guard = InputDiscard(self.clone());
            runtime.spawn(self.diagnostics.instrument(async move {
                guard.0.reader.lock().await.take();
                drop(guard);
            }));
        }
    }
    pub fn try_retire(&self) -> u32 {
        if !self.dispatch_done.load(Ordering::Acquire) || self.live_ops.load(Ordering::Acquire) != 0
        {
            return NW_PENDING;
        }
        let mut gate = lock(&self.controls);
        gate.open = false;
        if gate.active != 0 {
            return NW_PENDING;
        }
        self.retired.store(true, Ordering::Release);
        NW_OK
    }
    fn fully_retired(&self) -> bool {
        self.retired.load(Ordering::Acquire)
            && self.body_done.load(Ordering::Acquire)
            && self.live_ops.load(Ordering::Acquire) == 0
            && self.budget.snapshot() == (grpc::remaining_anchor(self), 0)
            && grpc::metadata_released(self)
    }
    pub fn host(&self) -> Result<Arc<Host>> {
        self.host
            .upgrade()
            .ok_or(ApiError::status(NW_INVALID_STATE))
    }
}
impl Drop for RequestState {
    fn drop(&mut self) {
        if self.dispatch_done.load(Ordering::Acquire)
            && let Some(host) = self.host.upgrade()
        {
            host.retiring.fetch_sub(1, Ordering::AcqRel);
        }
        self.release_connection();
    }
}
struct InputDiscard(Arc<RequestState>);
impl Drop for InputDiscard {
    fn drop(&mut self) {
        self.0.live_ops.fetch_sub(1, Ordering::AcqRel);
        if let Some(host) = self.0.host.upgrade() {
            host.reap();
        }
    }
}
pub struct ControlGate {
    open: bool,
    active: usize,
}
pub struct ResponseState {
    pub phase: u32,
    pub status: u16,
    pub length: Option<u64>,
    pub written: u64,
    pub sse: bool,
    pub head: Option<oneshot::Sender<(u16, HeaderMap)>>,
}
pub struct SideGuard(Arc<AtomicBool>);
impl SideGuard {
    pub fn acquire(flag: &Arc<AtomicBool>) -> Result<Self> {
        if flag
            .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
            .is_err()
        {
            Err(ApiError::status(NW_INVALID_STATE))
        } else {
            Ok(Self(flag.clone()))
        }
    }
}
impl Drop for SideGuard {
    fn drop(&mut self) {
        self.0.store(false, Ordering::Release);
    }
}
pub struct Reservation {
    pub request: Arc<RequestState>,
    pub kind: u32,
    pub reservation: grpc::WriteToken,
    pub side: SideGuard,
}
pub enum OpResult {
    Void,
    Close(NwServerCloseResultV1),
    Chunk(ChunkResult),
    Reservation(Reservation),
}
pub enum ChunkResult {
    Data(OwnedBuffer),
    GrpcData(nativeweave_protocols_grpc::MessageBuffer),
    End(Option<Vec<u8>>),
}
pub enum Job {
    GrpcRead(Arc<RequestState>, SideGuard),
    GrpcReserve(Arc<RequestState>, usize, SideGuard),
    GrpcWrite(
        Arc<RequestState>,
        nativeweave_protocols_grpc::Prepared,
        SideGuard,
    ),
    GrpcHeaders(
        Arc<RequestState>,
        nativeweave_protocols_grpc::ResponseMetadata,
        SideGuard,
    ),
    GrpcStatus(
        Arc<RequestState>,
        nativeweave_protocols_grpc::tonic::Status,
        nativeweave_protocols_grpc::ResponseMetadata,
        SideGuard,
    ),
    Start,
    Close(Duration),
    Reload(usize, Arc<TlsSnapshot>),
    Read(Arc<RequestState>, usize, SideGuard),
    Reserve(Arc<RequestState>, u32, usize, SideGuard),
    Begin(Arc<RequestState>, u16, HeaderMap, SideGuard),
    Write(Arc<RequestState>, PreparedWrite, usize, SideGuard),
    Flush(Arc<RequestState>, SideGuard),
    Finish(Arc<RequestState>, HeaderMap, SideGuard),
    McpGate(Arc<RequestState>, u32),
    McpReply(
        Arc<RequestState>,
        u32,
        nativeweave_protocol_mcp::serde_json::Value,
        WriteReservation,
        SideGuard,
    ),
}
pub struct OpState {
    pub operation: Arc<Operation<OpResult>>,
    pub host: Arc<Host>,
    pub request: Option<Arc<RequestState>>,
    pub job: Mutex<Option<Job>>,
    pub cancel: Cancellation,
    pub result_kind: u32,
    _slot: OwnedSemaphorePermit,
}
impl OpState {
    pub fn new(host: Arc<Host>, request: Option<Arc<RequestState>>, job: Job) -> Result<Arc<Self>> {
        let slot = host
            .operation_slots
            .clone()
            .try_acquire_owned()
            .map_err(|_| ApiError::domain(1))?;
        Ok(Self::with_slot(host, request, job, slot))
    }
    pub fn with_slot(
        host: Arc<Host>,
        request: Option<Arc<RequestState>>,
        job: Job,
        slot: OwnedSemaphorePermit,
    ) -> Arc<Self> {
        let result_kind = match &job {
            Job::Close(_) => 1,
            Job::Read(..) | Job::GrpcRead(..) => 2,
            Job::Reserve(..) | Job::GrpcReserve(..) => 3,
            _ => 0,
        };
        host.live_ops.fetch_add(1, Ordering::AcqRel);
        if let Some(r) = &request {
            r.live_ops.fetch_add(1, Ordering::AcqRel);
        }
        Arc::new(Self {
            operation: Arc::new(Operation::new()),
            host,
            request,
            job: Mutex::new(Some(job)),
            cancel: Cancellation::default(),
            result_kind,
            _slot: slot,
        })
    }
    pub fn complete(&self, result: Result<OpResult>) {
        let (status, result, fields) = match result {
            Ok(value) => (NW_OK, Some(value), Default::default()),
            Err(e) => (e.status, None, e.fields()),
        };
        // SAFETY: Core start owns the exact callback/root until Core retirement, and this live source retains the operation.
        let _ = unsafe { self.operation.complete(status, result, fields) };
    }
    pub fn execute(self: &Arc<Self>, job: Job) -> Result<()> {
        let source = self.operation.reserve_source().map_err(ApiError::status)?;
        if let Job::McpGate(request, decision) = &job {
            if self.cancel.is_canceled() {
                return Err(ApiError::status(NW_CANCELED));
            }
            mcp::commit(request, *decision)?;
        }
        if matches!(job, Job::Start) {
            self.host.start(self.clone(), source)?;
            return Ok(());
        }
        if let Job::Close(timeout) = job {
            let state = self.clone();
            std::thread::Builder::new()
                .name("nativeweave-server-close".into())
                .spawn(move || {
                    let _source = source;
                    state.host.initiate_close();
                    let end = Instant::now() + timeout;
                    loop {
                        let result = state.host.close_result();
                        if result.close_status == 0 {
                            state.complete(Ok(OpResult::Close(result)));
                            break;
                        }
                        if state.cancel.is_canceled() {
                            state.complete(Err(ApiError::status(NW_CANCELED)));
                            break;
                        }
                        if Instant::now() >= end {
                            state.host.abort.cancel();
                            for r in lock(&state.host.requests).values() {
                                r.abort(NW_CANCELED);
                            }
                            state.complete(Ok(OpResult::Close(state.host.close_result())));
                            break;
                        }
                        std::thread::sleep(Duration::from_millis(1));
                    }
                })
                .map_err(|_| ApiError::domain(3))?;
            return Ok(());
        }
        let handle = lock(&self.host.runtime)
            .clone()
            .ok_or(ApiError::status(NW_INVALID_STATE))?;
        let state = self.clone();
        handle.spawn(async move {
            let _source = source;
            let future = state.run_job(job);
            let result = if let Some(request) = &state.request {
                std::panic::AssertUnwindSafe(request.diagnostics.instrument(future))
                    .catch_unwind()
                    .await
            } else {
                std::panic::AssertUnwindSafe(state.host.diagnostics.instrument(future))
                    .catch_unwind()
                    .await
            };
            match result {
                Ok(value) => state.complete(value),
                Err(payload) => {
                    std::mem::forget(payload);
                    state.host.fault(NW_NATIVE_PANIC);
                    state.complete(Err(ApiError::status(NW_NATIVE_PANIC)));
                }
            }
        });
        Ok(())
    }
    async fn run_job(&self, job: Job) -> Result<OpResult> {
        if self.cancel.is_canceled() {
            return Err(ApiError::status(NW_CANCELED));
        }
        let work = async {
            match job {
                Job::GrpcRead(request, side) => grpc::read(request, side).await,
                Job::GrpcReserve(request, capacity, side) => {
                    grpc::reserve(request, capacity, side).await
                }
                Job::GrpcWrite(request, prepared, side) => grpc::write(request, prepared, side),
                Job::GrpcHeaders(request, metadata, side) => grpc::headers(request, metadata, side),
                Job::GrpcStatus(request, status, metadata, side) => {
                    grpc::status(request, status, metadata, side).await
                }
                Job::McpGate(request, decision) => mcp::gate(request, decision).await,
                Job::McpReply(request, kind, value, reservation, _side) => {
                    mcp::reply(&request, kind, value, reservation)
                }
                Job::Reload(index, tls) => {
                    if self.host.state.load(Ordering::Acquire) != 2 {
                        return Err(ApiError::status(NW_INVALID_STATE));
                    }
                    *self.host.config.listeners[index]
                        .tls
                        .write()
                        .unwrap_or_else(|p| p.into_inner()) = Some(tls);
                    Ok(OpResult::Void)
                }
                Job::Read(request, max, _side) => {
                    request.business()?;
                    let mut guard = request.reader.lock().await;
                    let reader = guard.as_mut().ok_or(ApiError::status(NW_INVALID_STATE))?;
                    let frame = reader
                        .read(max, &request.input_cancel)
                        .await
                        .map_err(|error| {
                            if request.body_limit.load(Ordering::Acquire) {
                                ApiError::domain(2)
                            } else {
                                error.into()
                            }
                        })?;
                    let result = match frame {
                        ReadFrame::Data(buffer) => ChunkResult::Data(buffer),
                        ReadFrame::End(trailers) => ChunkResult::End(
                            trailers
                                .as_ref()
                                .map(crate::config::encode_headers)
                                .transpose()?,
                        ),
                    };
                    Ok(OpResult::Chunk(result))
                }
                Job::Reserve(request, kind, capacity, side) => {
                    request.business()?;
                    let reservation = if kind == 3 {
                        mcp::reserve(&request, capacity)?
                    } else {
                        request
                            .sender
                            .reserve(capacity, &request.cancel)
                            .await
                            .map_err(|error| {
                                request.abort(NW_CANCELED);
                                ApiError::from(error)
                            })?
                    };
                    Ok(OpResult::Reservation(Reservation {
                        request,
                        kind,
                        reservation: grpc::WriteToken::Body(reservation),
                        side,
                    }))
                }
                Job::Begin(request, status, headers, _side) => {
                    request.business()?;
                    if !mcp::raw_output(&request) {
                        return Err(ApiError::status(NW_INVALID_STATE));
                    }
                    let length = content_length(&headers)?;
                    if status == 204 && length.is_some()
                        || status == 205 && length.is_some_and(|x| x != 0)
                    {
                        return Err(invalid());
                    }
                    let mut response = lock(&request.response);
                    if response.phase != 0 {
                        return Err(ApiError::status(NW_INVALID_STATE));
                    }
                    response.sse = headers
                        .get(http::header::CONTENT_TYPE)
                        .and_then(|value| value.to_str().ok())
                        .is_some_and(|value| {
                            value.split(';').next().is_some_and(|part| {
                                part.trim().eq_ignore_ascii_case("text/event-stream")
                            })
                        });
                    let tx = response
                        .head
                        .take()
                        .ok_or(ApiError::status(NW_INVALID_STATE))?;
                    tx.send((status, headers))
                        .map_err(|_| ApiError::domain(4))?;
                    response.status = status;
                    response.length = length;
                    response.phase = 1;
                    Ok(OpResult::Void)
                }
                Job::Write(request, prepared, length, _side) => {
                    request.business()?;
                    {
                        let mut response = lock(&request.response);
                        if response.phase != 1 || matches!(response.status, 204 | 205 | 304) {
                            return Err(ApiError::status(NW_INVALID_STATE));
                        }
                        response.written = response
                            .written
                            .checked_add(length as u64)
                            .ok_or(invalid())?;
                        if request.head.method != "HEAD"
                            && response.length.is_some_and(|x| response.written > x)
                        {
                            request.abort(NW_DOMAIN_ERROR);
                            return Err(ApiError::domain(6));
                        }
                    }
                    prepared.commit();
                    Ok(OpResult::Void)
                }
                Job::Flush(request, _side) => {
                    request.business()?;
                    if !mcp::raw_output(&request) {
                        return Err(ApiError::status(NW_INVALID_STATE));
                    }
                    if lock(&request.response).phase != 1 {
                        return Err(ApiError::status(NW_INVALID_STATE));
                    }
                    request.sender.flush(&request.cancel).await?;
                    Ok(OpResult::Void)
                }
                Job::Finish(request, trailers, _side) => {
                    request.business()?;
                    if !mcp::raw_output(&request) {
                        return Err(ApiError::status(NW_INVALID_STATE));
                    }
                    let no_trailers = trailers.is_empty();
                    let declared_complete = {
                        let mut response = lock(&request.response);
                        if response.phase != 1 {
                            return Err(ApiError::status(NW_INVALID_STATE));
                        }
                        if request.head.method != "HEAD"
                            && !matches!(response.status, 204 | 205 | 304)
                            && response.length.is_some_and(|x| x != response.written)
                        {
                            request.abort(NW_DOMAIN_ERROR);
                            return Err(ApiError::domain(6));
                        }
                        if !trailers.is_empty()
                            && (request.head.method == "HEAD"
                                || matches!(response.status, 204 | 205 | 304))
                        {
                            return Err(invalid());
                        }
                        response.phase = 2;
                        if request.head.method == "HEAD"
                            || matches!(response.status, 204 | 205 | 304)
                        {
                            response.phase = 3;
                            return Ok(OpResult::Void);
                        }
                        response
                            .length
                            .is_some_and(|length| length == response.written)
                    };
                    let completed = request
                        .sender
                        .finish(
                            if no_trailers { None } else { Some(trailers) },
                            &request.cancel,
                        )
                        .await;
                    if let Err(error) = completed {
                        // Hyper may finish a Content-Length body without polling
                        // an additional End frame. Drop marks body_done before
                        // closing its receiver. Accept only that already-complete
                        // local framing case, never a reset, a short body, or
                        // discarded trailers. This is not peer acknowledgement.
                        if !matches!(error, StreamError::Closed)
                            || !no_trailers
                            || !declared_complete
                            || !request.body_done.load(Ordering::Acquire)
                            || request.cancel.is_canceled()
                        {
                            return Err(error.into());
                        }
                    }
                    lock(&request.response).phase = 3;
                    Ok(OpResult::Void)
                }
                Job::Start | Job::Close(_) => Err(ApiError::status(NW_INVALID_STATE)),
            }
        };
        tokio::select! {biased;()=self.cancel.cancelled()=>Err(ApiError::status(NW_CANCELED)),result=work=>result}
    }
}
impl Drop for OpState {
    fn drop(&mut self) {
        self.host.live_ops.fetch_sub(1, Ordering::AcqRel);
        if let Some(r) = &self.request {
            r.live_ops.fetch_sub(1, Ordering::AcqRel);
        }
        self.host.reap();
    }
}
fn content_length(headers: &HeaderMap) -> Result<Option<u64>> {
    let values: Vec<_> = headers
        .get_all(http::header::CONTENT_LENGTH)
        .iter()
        .collect();
    if values.len() > 1 {
        return Err(invalid());
    }
    values
        .first()
        .map(|x| {
            x.to_str()
                .map_err(|_| invalid())?
                .parse::<u64>()
                .map_err(|_| invalid())
        })
        .transpose()
}
pub struct ChunkedInput {
    body: AxumBody,
    pending: Option<Bytes>,
    maximum: usize,
    received: u64,
    limit: u64,
    exceeded: Arc<AtomicBool>,
}
impl Body for ChunkedInput {
    type Data = Bytes;
    type Error = StreamError;
    fn poll_frame(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
    ) -> Poll<Option<std::result::Result<Frame<Bytes>, StreamError>>> {
        if let Some(mut pending) = self.pending.take() {
            let part = pending.split_to(pending.len().min(self.maximum));
            if !pending.is_empty() {
                self.pending = Some(pending);
            }
            return Poll::Ready(Some(Ok(Frame::data(part))));
        }
        match Pin::new(&mut self.body).poll_frame(cx) {
            Poll::Ready(Some(Ok(frame))) => match frame.into_data() {
                Ok(mut bytes) => {
                    self.received = self.received.saturating_add(bytes.len() as u64);
                    if self.received > self.limit {
                        self.exceeded.store(true, Ordering::Release);
                        return Poll::Ready(Some(Err(StreamError::ResourceLimit)));
                    }
                    let part = bytes.split_to(bytes.len().min(self.maximum));
                    if !bytes.is_empty() {
                        self.pending = Some(bytes);
                    }
                    Poll::Ready(Some(Ok(Frame::data(part))))
                }
                Err(frame) => Poll::Ready(Some(Ok(frame))),
            },
            Poll::Ready(Some(Err(_))) => Poll::Ready(Some(Err(StreamError::Transport))),
            Poll::Ready(None) => Poll::Ready(None),
            Poll::Pending => Poll::Pending,
        }
    }
}
struct OutputBody {
    body: BoundedBody,
    request: Arc<RequestState>,
    expected: Option<u64>,
    sent: u64,
    suppress: bool,
    ended: bool,
}
impl OutputBody {
    fn end(&mut self) {
        if !self.ended {
            self.ended = true;
            self.request.output_waker.take();
            self.request.body_ended();
            if let Some(host) = self.request.host.upgrade() {
                host.reap();
            }
        }
    }
}
impl Body for OutputBody {
    type Data = Bytes;
    type Error = StreamError;
    fn poll_frame(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
    ) -> Poll<Option<std::result::Result<Frame<Bytes>, StreamError>>> {
        let request = self.request.clone();
        request.diagnostics.in_scope(|| {
            if self.ended {
                return Poll::Ready(None);
            }
            // Register before checking the sticky flag: abort must wake a pending
            // empty Body queue, including failure after headers have been committed.
            self.request.output_waker.register(cx.waker());
            if self.request.cancel.is_canceled() {
                self.end();
                return Poll::Ready(Some(Err(StreamError::Canceled)));
            }
            loop {
                match Pin::new(&mut self.body).poll_frame(cx) {
                    Poll::Ready(Some(Ok(frame))) => {
                        if let Some(data) = frame.data_ref() {
                            self.sent = self.sent.saturating_add(data.len() as u64);
                            if self.expected.is_some_and(|x| self.sent > x) {
                                self.request.abort(NW_DOMAIN_ERROR);
                                self.end();
                                return Poll::Ready(Some(Err(StreamError::Transport)));
                            }
                            if self.suppress {
                                continue;
                            }
                        }
                        return Poll::Ready(Some(Ok(frame)));
                    }
                    Poll::Ready(None) => {
                        self.end();
                        return if self.expected.is_some_and(|x| x != self.sent) {
                            Poll::Ready(Some(Err(StreamError::Transport)))
                        } else {
                            Poll::Ready(None)
                        };
                    }
                    Poll::Ready(Some(Err(error))) => {
                        self.end();
                        return Poll::Ready(Some(Err(error)));
                    }
                    Poll::Pending => return Poll::Pending,
                }
            }
        })
    }
    fn is_end_stream(&self) -> bool {
        self.ended
    }
    fn size_hint(&self) -> SizeHint {
        SizeHint::default()
    }
}
impl Drop for OutputBody {
    fn drop(&mut self) {
        if !self.ended {
            if lock(&self.request.response).phase != 3
                && !self.expected.is_some_and(|length| length == self.sent)
            {
                self.request.abort(NW_CANCELED);
            }
            self.end();
        }
    }
}
