//! Direct Hyper/rustls/Tonic control for the Perf-01 synthetic protocol service.
//! No NativeWeave product crate is linked; input, TLS, budgets and client are shared.
mod fixture;
use bytes::Bytes;
use fixture::{FixtureCodec, Payload};
use futures_core::Stream;
use futures_util::{StreamExt, stream};
use http::{Request, Response, StatusCode};
use http_body::{Body, Frame, SizeHint};
use http_body_util::{BodyExt, Full};
use hyper::body::Incoming;
use hyper_util::{rt::{TokioExecutor, TokioIo, TokioTimer}, server::conn::auto::Builder};
use serde::Deserialize;
use serde_json::json;
use sha2::{Digest, Sha256};
use std::{collections::BTreeMap, convert::Infallible, future::Future, io::{BufRead, Write}, net::Ipv4Addr,
    path::{Path, PathBuf}, pin::Pin, sync::{Arc, Mutex, atomic::{AtomicUsize, Ordering}}, task::{Context, Poll}, time::{Duration, SystemTime, UNIX_EPOCH}};
use tokio::{net::TcpListener, sync::{Notify, OwnedSemaphorePermit, Semaphore, watch}, task::JoinSet, time::{Instant, Sleep}};
use tokio_rustls::TlsAcceptor;
use tonic::{Status, Streaming, body::Body as TonicBody, server::{ClientStreamingService, Grpc, ServerStreamingService, StreamingService, UnaryService}};

#[derive(Deserialize)]
struct Config {
    certificate: String, private_key: String, chunk_bytes: usize, max_connections: usize,
    max_http2_streams_per_connection: u32, max_active_requests: usize, max_queued_requests: usize,
    native_payload_bytes: usize, retained_bytes_per_request: usize, max_message_bytes: usize,
    max_stream_bytes: usize, request_timeout_seconds: u64, shutdown_timeout_seconds: u64,
}
struct State {
    config: Config, connections: Arc<Semaphore>, active: Arc<Semaphore>, queued: Arc<Semaphore>,
    pending: AtomicUsize, connection_count: AtomicUsize, bytes: AtomicUsize, owners: AtomicUsize,
    downloads: BTreeMap<usize, (Bytes, String)>,
}
struct Life { active: AtomicUsize, last: Mutex<Instant>, changed: Notify }
impl Life {
    async fn idle(&self) {
        loop {
            let notified = self.changed.notified();
            let expires = *self.last.lock().unwrap() + Duration::from_secs(60);
            if self.active.load(Ordering::Acquire) == 0 {
                tokio::select! { _ = notified => {}, _ = tokio::time::sleep_until(expires) => {
                    if self.active.load(Ordering::Acquire) == 0 && Instant::now() >= *self.last.lock().unwrap() + Duration::from_secs(60) { return; }
                } }
            } else { notified.await; }
        }
    }
}
struct Lease { state: Arc<State>, life: Arc<Life>, local_bytes: AtomicUsize, _permit: OwnedSemaphorePermit, codec: AtomicUsize }
impl Drop for Lease {
    fn drop(&mut self) {
        let codec = self.codec.load(Ordering::Relaxed);
        self.state.bytes.fetch_sub(codec, Ordering::AcqRel);
        self.state.pending.fetch_sub(1, Ordering::AcqRel);
        self.life.active.fetch_sub(1, Ordering::AcqRel);
        *self.life.last.lock().unwrap() = Instant::now(); self.life.changed.notify_one();
    }
}
struct Grant { state: Arc<State>, local: Arc<Lease>, size: usize }
impl Drop for Grant {
    fn drop(&mut self) {
        self.local.local_bytes.fetch_sub(self.size, Ordering::AcqRel);
        self.state.bytes.fetch_sub(self.size, Ordering::AcqRel);
        self.state.owners.fetch_sub(1, Ordering::AcqRel);
    }
}
fn acquire(counter: &AtomicUsize, maximum: usize, amount: usize) -> bool {
    counter.try_update(Ordering::AcqRel, Ordering::Acquire, |value| value.checked_add(amount).filter(|next| *next <= maximum)).is_ok()
}
impl Lease {
    fn reserve(self: &Arc<Self>, size: usize) -> Result<Grant, Status> {
        if !acquire(&self.local_bytes, self.state.config.retained_bytes_per_request, size) { return Err(Status::resource_exhausted("request payload quota")); }
        if !acquire(&self.state.bytes, self.state.config.native_payload_bytes, size) {
            self.local_bytes.fetch_sub(size, Ordering::AcqRel); return Err(Status::resource_exhausted("native payload quota"));
        }
        self.state.owners.fetch_add(1, Ordering::AcqRel);
        Ok(Grant { state: self.state.clone(), local: self.clone(), size })
    }
    fn reserve_codec(&self) -> Result<(), Status> {
        // The same conservative NativeWeave maximum codec allowance is reserved,
        // even though direct Tonic grows its 8 KiB buffers lazily. One message per
        // frame keeps the encoder bound equal to max_message_bytes + 5.
        // Also reserve the product's fixed gRPC request-metadata floor. Fixture
        // requests contain no binary metadata, so conditional decode scratch is 0.
        let metadata = 2 * 32768 + 200 * 48 + 2048;
        let bytes = self.state.config.max_message_bytes * 2 + self.state.config.chunk_bytes * 2 + 10 + metadata;
        if !acquire(&self.local_bytes, self.state.config.retained_bytes_per_request, bytes) { return Err(Status::resource_exhausted("codec request quota")); }
        if !acquire(&self.state.bytes, self.state.config.native_payload_bytes, bytes) {
            self.local_bytes.fetch_sub(bytes, Ordering::AcqRel); return Err(Status::resource_exhausted("codec global quota"));
        }
        self.codec.store(bytes, Ordering::Release); Ok(())
    }
}
pub(crate) struct Retained { data: Bytes, _grant: Grant, _lease: Arc<Lease> }
impl AsRef<[u8]> for Retained { fn as_ref(&self) -> &[u8] { &self.data } }

#[derive(Default)]
struct Counts { inputs: AtomicUsize, outputs: AtomicUsize }
struct GuardBody {
    inner: TonicBody, lease: Arc<Lease>, counts: Arc<Counts>, grpc: bool,
    deadline: Pin<Box<Sleep>>, finished: bool,
}
impl Body for GuardBody {
    type Data = Bytes;
    type Error = Status;
    fn poll_frame(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Option<Result<Frame<Bytes>, Status>>> {
        if self.finished { return Poll::Ready(None); }
        if self.deadline.as_mut().poll(cx).is_ready() {
            self.finished = true; self.inner = TonicBody::empty();
            return if self.grpc {
                let mut headers = http::HeaderMap::new();
                Poll::Ready(Some(Status::deadline_exceeded("deadline exceeded").add_header(&mut headers).map(|()| Frame::trailers(headers))))
            }
                else { Poll::Ready(Some(Err(Status::deadline_exceeded("request timeout")))) };
        }
        match Pin::new(&mut self.inner).poll_frame(cx) {
            Poll::Ready(Some(Ok(frame))) => match frame.into_data() {
                Ok(data) => {
                    let grant = match self.lease.reserve(data.len().max(1)) { Ok(grant) => grant, Err(error) => return Poll::Ready(Some(Err(error))) };
                    Poll::Ready(Some(Ok(Frame::data(Bytes::from_owner(Retained { data, _grant: grant, _lease: self.lease.clone() })))))
                }
                Err(frame) => {
                    let mut trailers = frame.into_trailers().expect("HTTP frame data or trailers");
                    if self.grpc && trailers.get("grpc-status").is_some_and(|status| status == "0") {
                        trailers.insert("perf-completed", "yes".parse().unwrap());
                        trailers.insert("perf-inputs", self.counts.inputs.load(Ordering::Acquire).to_string().parse().unwrap());
                        trailers.insert("perf-outputs", self.counts.outputs.load(Ordering::Acquire).to_string().parse().unwrap());
                    }
                    Poll::Ready(Some(Ok(Frame::trailers(trailers))))
                }
            },
            Poll::Ready(None) => { self.finished = true; Poll::Ready(None) }
            other => other,
        }
    }
    fn is_end_stream(&self) -> bool { self.finished || self.inner.is_end_stream() }
    fn size_hint(&self) -> SizeHint { self.inner.size_hint() }
}
struct Chunks { data: Bytes, position: usize, maximum: usize }
impl Body for Chunks {
    type Data = Bytes;
    type Error = Infallible;
    fn poll_frame(mut self: Pin<&mut Self>, _: &mut Context<'_>) -> Poll<Option<Result<Frame<Bytes>, Infallible>>> {
        if self.position == self.data.len() { return Poll::Ready(None); }
        let end = (self.position + self.maximum).min(self.data.len());
        let data = self.data.slice(self.position..end); self.position = end;
        Poll::Ready(Some(Ok(Frame::data(data))))
    }
    fn is_end_stream(&self) -> bool { self.position == self.data.len() }
    fn size_hint(&self) -> SizeHint { SizeHint::with_exact((self.data.len() - self.position) as u64) }
}

type Reply<T> = Pin<Box<dyn Future<Output = Result<tonic::Response<T>, Status>> + Send>>;
type Messages = Pin<Box<dyn Stream<Item = Result<Payload, Status>> + Send>>;
struct Unary(Arc<Counts>);
impl UnaryService<Payload> for Unary {
    type Response = Payload;
    type Future = Reply<Payload>;
    fn call(&mut self, request: tonic::Request<Payload>) -> Self::Future {
        self.0.inputs.store(1, Ordering::Release); self.0.outputs.store(1, Ordering::Release);
        Box::pin(async { Ok(tonic::Response::new(request.into_inner())) })
    }
}
struct Server { counts: Arc<Counts>, wait: bool }
impl ServerStreamingService<Payload> for Server {
    type Response = Payload;
    type ResponseStream = Messages;
    type Future = Reply<Messages>;
    fn call(&mut self, request: tonic::Request<Payload>) -> Self::Future {
        let value = request.into_inner(); let counts = self.counts.clone(); let wait = self.wait;
        Box::pin(async move {
            if value.count < 1 || value.count > 32 { return Err(Status::invalid_argument("fixture count")); }
            counts.inputs.store(1, Ordering::Release);
            if wait {
                counts.outputs.store(1, Ordering::Release);
                let messages: Messages = Box::pin(stream::once(async { Ok(value) }).chain(stream::pending()));
                let mut response = tonic::Response::new(messages);
                response.metadata_mut().insert("perf-started", "yes".parse().unwrap()); return Ok(response);
            }
            let messages: Messages = Box::pin(stream::iter((0..value.count).map(move |index| {
                counts.outputs.fetch_add(1, Ordering::AcqRel);
                Ok(Payload { sequence: index as i64, data: value.data.clone(), count: value.count, ..Default::default() })
            })));
            Ok(tonic::Response::new(messages))
        })
    }
}
struct Client(Arc<Counts>);
impl ClientStreamingService<Payload> for Client {
    type Response = Payload;
    type Future = Reply<Payload>;
    fn call(&mut self, request: tonic::Request<Streaming<Payload>>) -> Self::Future {
        let counts = self.0.clone(); let mut input = request.into_inner();
        Box::pin(async move {
            let mut hash = Sha256::new(); let mut count = 0; let mut total = 0;
            while let Some(value) = input.message().await? {
                if value.sequence != count || count >= 32 { return Err(Status::invalid_argument("fixture order/count")); }
                count += 1; total += value.data.len() as i64; hash.update(&value.data);
            }
            counts.inputs.store(count as usize, Ordering::Release); counts.outputs.store(1, Ordering::Release);
            Ok(tonic::Response::new(Payload { count: count as i32, total_bytes: total, digest: hex(&hash.finalize()), ..Default::default() }))
        })
    }
}
struct Duplex(Arc<Counts>);
impl StreamingService<Payload> for Duplex {
    type Response = Payload;
    type ResponseStream = Messages;
    type Future = Reply<Messages>;
    fn call(&mut self, request: tonic::Request<Streaming<Payload>>) -> Self::Future {
        let input = request.into_inner(); let counts = self.0.clone();
        Box::pin(async move {
            let messages: Messages = Box::pin(stream::try_unfold((input, counts, 0_i64), |(mut input, counts, count)| async move {
                match input.message().await? {
                    Some(value) => {
                        if value.sequence != count || count >= 32 { return Err(Status::invalid_argument("fixture order/count")); }
                        counts.inputs.fetch_add(1, Ordering::AcqRel); counts.outputs.fetch_add(1, Ordering::AcqRel);
                        Ok(Some((value, (input, counts, count + 1))))
                    }
                    None => Ok(None),
                }
            }));
            Ok(tonic::Response::new(messages))
        })
    }
}
fn hex(bytes: &[u8]) -> String { bytes.iter().map(|byte| format!("{byte:02x}")).collect() }
fn full(status: StatusCode, bytes: impl Into<Bytes>) -> Response<TonicBody> {
    Response::builder().status(status).body(TonicBody::new(Full::new(bytes.into()))).unwrap()
}
fn deadline(headers: &http::HeaderMap, configured: u64) -> Result<Duration, Status> {
    let default = Duration::from_secs(configured);
    let Some(value) = headers.get("grpc-timeout") else { return Ok(default); };
    let value = value.to_str().map_err(|_| Status::invalid_argument("grpc timeout"))?;
    if !(2..=9).contains(&value.len()) { return Err(Status::invalid_argument("grpc timeout")); }
    let number: u64 = value[..value.len() - 1].parse().map_err(|_| Status::invalid_argument("grpc timeout"))?;
    let multiplier: u64 = match value.as_bytes()[value.len() - 1] { b'H' => 3_600_000_000_000, b'M' => 60_000_000_000, b'S' => 1_000_000_000, b'm' => 1_000_000, b'u' => 1000, b'n' => 1, _ => return Err(Status::invalid_argument("grpc timeout")) };
    Ok(default.min(Duration::from_nanos(number.saturating_mul(multiplier))))
}
async fn handle(request: Request<Incoming>, state: Arc<State>, life: Arc<Life>) -> Result<Response<TonicBody>, Infallible> {
    *life.last.lock().unwrap() = Instant::now(); life.changed.notify_one();
    let grpc = request.uri().path().starts_with("/nativeweave.perf01.Benchmark/");
    let expected = b"Bearer Perf01-synthetic-local-test-token";
    let authorized = request.headers().get("authorization").map(|value| value.as_bytes()).is_some_and(|value|
        value.len() == expected.len() && std::hint::black_box(value.iter().zip(expected).fold(0_u8, |difference, (a, b)| difference | (a ^ b))) == 0);
    if !authorized { return Ok(if grpc { Status::unauthenticated("authentication required").into_http() } else { full(StatusCode::UNAUTHORIZED, Bytes::new()) }); }
    let timeout = match deadline(request.headers(), state.config.request_timeout_seconds) { Ok(timeout) => timeout, Err(error) => return Ok(error.into_http()) };
    let expires = Instant::now() + timeout;
    let permit = match state.active.clone().try_acquire_owned() {
        Ok(permit) => permit,
        Err(_) => {
            let Ok(_queued) = state.queued.clone().try_acquire_owned() else {
                return Ok(if grpc { Status::resource_exhausted("request queue full").into_http() } else { full(StatusCode::SERVICE_UNAVAILABLE, Bytes::new()) });
            };
            match tokio::time::timeout_at(expires, state.active.clone().acquire_owned()).await {
                Ok(Ok(permit)) => permit,
                _ => return Ok(if grpc { Status::deadline_exceeded("queue deadline").into_http() } else { full(StatusCode::REQUEST_TIMEOUT, Bytes::new()) }),
            }
        }
    };
    state.pending.fetch_add(1, Ordering::AcqRel);
    life.active.fetch_add(1, Ordering::AcqRel); life.changed.notify_one();
    let lease = Arc::new(Lease { state: state.clone(), life, local_bytes: AtomicUsize::new(0), _permit: permit, codec: AtomicUsize::new(0) });
    let counts = Arc::new(Counts::default());
    let result = tokio::time::timeout_at(expires, response(request, state, lease.clone(), counts.clone(), grpc)).await;
    let response = match result {
        Ok(Ok(value)) => value,
        Ok(Err(error)) if grpc => error.into_http(),
        Ok(Err(_)) => full(StatusCode::BAD_REQUEST, Bytes::new()),
        Err(_) if grpc => Status::deadline_exceeded("deadline exceeded").into_http(),
        Err(_) => full(StatusCode::REQUEST_TIMEOUT, Bytes::new()),
    };
    let (parts, inner) = response.into_parts();
    Ok(Response::from_parts(parts, TonicBody::new(GuardBody { inner, lease, counts, grpc, deadline: Box::pin(tokio::time::sleep_until(expires)), finished: false })))
}
async fn response(request: Request<Incoming>, state: Arc<State>, lease: Arc<Lease>, counts: Arc<Counts>, grpc: bool) -> Result<Response<TonicBody>, Status> {
    let path = request.uri().path().to_owned();
    if grpc {
        lease.reserve_codec()?;
        let single = matches!(path.rsplit('/').next(), Some("Unary" | "Server" | "Wait"));
        let codec = FixtureCodec { lease, maximum: state.config.max_message_bytes, stream_maximum: state.config.max_stream_bytes, single_input: single };
        let mut server = Grpc::new(codec).max_decoding_message_size(state.config.max_message_bytes).max_encoding_message_size(state.config.max_message_bytes);
        return Ok(match path.rsplit('/').next().unwrap_or("") {
            "Unary" => server.unary(Unary(counts), request).await,
            "Server" => server.server_streaming(Server { counts, wait: false }, request).await,
            "Wait" => server.server_streaming(Server { counts, wait: true }, request).await,
            "Client" => server.client_streaming(Client(counts), request).await,
            "Duplex" => server.streaming(Duplex(counts), request).await,
            _ => Status::unimplemented("method not found").into_http(),
        });
    }
    if request.method() == http::Method::GET {
        if path == "/tiny" { return Ok(full(StatusCode::OK, Bytes::from_static(b"0123456789abcdef"))); }
        if path == "/json" {
            let bytes = serde_json::to_vec(&json!({"message": "perf01", "value": 42})).unwrap();
            let mut response = full(StatusCode::OK, bytes); response.headers_mut().insert("content-type", "application/json; charset=utf-8".parse().unwrap()); return Ok(response);
        }
        if let Some(size) = path.strip_prefix("/download/").and_then(|value| value.parse::<usize>().ok())
            && let Some((data, hash)) = state.downloads.get(&size) {
                return Ok(Response::builder().status(200).header("content-length", data.len()).header("x-content-sha256", hash)
                    .body(TonicBody::new(Chunks { data: data.clone(), position: 0, maximum: state.config.chunk_bytes })).unwrap());
            }
    }
    if path == "/upload" && request.method() == http::Method::PUT {
        let expected = request.headers().get("content-length").and_then(|value| value.to_str().ok()).and_then(|value| value.parse::<usize>().ok())
            .filter(|value| *value <= state.config.max_stream_bytes).ok_or_else(|| Status::invalid_argument("content length"))?;
        let mut input = request.into_body(); let mut hash = Sha256::new(); let mut total = 0;
        // Bound receive staging by the same chunk size. Hyper owns its protocol buffers.
        while let Some(frame) = input.frame().await {
            let frame = frame.map_err(|_| Status::cancelled("upload disconnected"))?;
            if let Ok(data) = frame.into_data() {
                total += data.len(); if total > expected { return Err(Status::invalid_argument("upload overflow")); }
                for chunk in data.chunks(state.config.chunk_bytes) {
                    let _grant = lease.reserve(chunk.len())?;
                    hash.update(chunk);
                }
            }
        }
        if total != expected { return Err(Status::invalid_argument("upload truncated")); }
        let bytes = serde_json::to_vec(&json!({"bytes": total, "sha256": hex(&hash.finalize())})).unwrap();
        let mut response = full(StatusCode::OK, bytes); response.headers_mut().insert("content-type", "application/json; charset=utf-8".parse().unwrap()); return Ok(response);
    }
    Ok(full(StatusCode::NOT_FOUND, Bytes::new()))
}

struct Connection { state: Arc<State>, _permit: OwnedSemaphorePermit }
impl Drop for Connection { fn drop(&mut self) { self.state.connection_count.fetch_sub(1, Ordering::AcqRel); } }
async fn serve_socket(socket: tokio::net::TcpStream, tls: TlsAcceptor, handshakes: Arc<Semaphore>, state: Arc<State>, mut stop: watch::Receiver<bool>, h2: bool, permit: OwnedSemaphorePermit) {
    let _connection = Connection { state: state.clone(), _permit: permit };
    let Ok(handshake) = handshakes.try_acquire_owned() else { return; };
    let stream = tokio::select! { _ = stop.changed() => return, result = tokio::time::timeout(Duration::from_secs(10), tls.accept(socket)) => match result { Ok(Ok(stream)) => stream, _ => return } };
    drop(handshake);
    let life = Arc::new(Life { active: AtomicUsize::new(0), last: Mutex::new(Instant::now()), changed: Notify::new() });
    let mut builder = Builder::new(TokioExecutor::new());
    builder.http1().timer(TokioTimer::new()).header_read_timeout(Duration::from_secs(10)).max_headers(200).max_buf_size(state.config.chunk_bytes.max(32768));
    builder.http2().timer(TokioTimer::new()).max_concurrent_streams(state.config.max_http2_streams_per_connection).max_header_list_size(32768)
        .initial_stream_window_size((state.config.chunk_bytes * 2) as u32).initial_connection_window_size((state.config.chunk_bytes * 2) as u32)
        .max_frame_size((state.config.chunk_bytes as u32).clamp(16384, 16_777_215));
    let builder = if h2 { builder.http2_only() } else { builder.http1_only() };
    let service_life = life.clone();
    let service = hyper::service::service_fn(move |request| handle(request, state.clone(), service_life.clone()));
    let connection = builder.serve_connection(TokioIo::new(stream), service); tokio::pin!(connection);
    tokio::select! { _ = &mut connection => {}, _ = life.idle() => {}, _ = stop.changed() => { connection.as_mut().graceful_shutdown(); let _ = connection.await; } }
}
fn tls(config: &Config, h2: bool) -> Result<TlsAcceptor, Box<dyn std::error::Error>> {
    let certificates = rustls_pemfile::certs(&mut std::io::BufReader::new(std::fs::File::open(&config.certificate)?)).collect::<Result<Vec<_>, _>>()?;
    let key = rustls_pemfile::private_key(&mut std::io::BufReader::new(std::fs::File::open(&config.private_key)?))?.ok_or("TLS private key missing")?;
    let mut server = rustls::ServerConfig::builder().with_no_client_auth().with_single_cert(certificates, key)?;
    server.alpn_protocols = vec![if h2 { b"h2".to_vec() } else { b"http/1.1".to_vec() }];
    Ok(TlsAcceptor::from(Arc::new(server)))
}
fn argument(name: &str) -> Result<PathBuf, Box<dyn std::error::Error>> {
    let mut args = std::env::args(); while let Some(arg) = args.next() { if arg == name { return Ok(args.next().ok_or("missing argument value")?.into()); } } Err("missing argument".into())
}
fn snapshot(state: &State, phase: &str, log: &mut std::fs::File) -> std::io::Result<()> {
    writeln!(log, "{}", json!({"utc_ns": SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos(), "phase": phase,
        "pending_requests": state.pending.load(Ordering::Acquire), "connections": state.connection_count.load(Ordering::Acquire),
        "retained_native_bytes": state.bytes.load(Ordering::Acquire), "byte_owners": state.owners.load(Ordering::Acquire),
        "active_permits_available": state.active.available_permits(), "queued_permits_available": state.queued.available_permits()}))?;
    log.flush()
}
#[tokio::main(flavor = "current_thread")]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    let config: Config = serde_json::from_slice(&std::fs::read(argument("--config")?)?)?;
    let out = argument("--output")?; std::fs::create_dir_all(&out)?;
    let tls1 = tls(&config, false)?; let tls2 = tls(&config, true)?;
    let handshakes1 = Arc::new(Semaphore::new(64)); let handshakes2 = Arc::new(Semaphore::new(64));
    let first = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).await?;
    let second = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).await?;
    let mut downloads = BTreeMap::new();
    for size in [1048576, 8388608] {
        let bytes: Vec<u8> = (0..size).map(|i| ((i * 31 + (i >> 8) * 17 + 20261119) & 255) as u8).collect();
        let hash = hex(&Sha256::digest(&bytes)); downloads.insert(size, (Bytes::from(bytes), hash));
    }
    let state = Arc::new(State { connections: Arc::new(Semaphore::new(config.max_connections)), active: Arc::new(Semaphore::new(config.max_active_requests)),
        queued: Arc::new(Semaphore::new(config.max_queued_requests)), pending: AtomicUsize::new(0), connection_count: AtomicUsize::new(0),
        bytes: AtomicUsize::new(0), owners: AtomicUsize::new(0), config, downloads });
    let (commands, mut receiver) = tokio::sync::mpsc::unbounded_channel::<String>();
    std::thread::spawn(move || { for line in std::io::stdin().lock().lines() { match line { Ok(line) => { if commands.send(line).is_err() { return; } }, Err(_) => return } } });
    let (stop, stopped) = watch::channel(false); let mut connections = JoinSet::new();
    let mut observations = std::fs::File::create(out.join("native-resources.jsonl"))?;
    let mut ticker = tokio::time::interval(Duration::from_secs(1)); let mut phase = "ready".to_owned();
    println!("READY {}", json!({"addresses": [format!("https://{}/", first.local_addr()?), format!("https://{}/", second.local_addr()?)],
        "dynamic_code_supported": false, "loaded_native_sha256": {}})); std::io::stdout().flush()?;
    loop {
        tokio::select! {
            command = receiver.recv() => match command {
                Some(command) if command != "stop" => { phase = command; snapshot(&state, &phase, &mut observations)?; }
                _ => break,
            },
            _ = ticker.tick() => snapshot(&state, &phase, &mut observations)?,
            result = first.accept() => {
                let (socket, _) = result?; socket.set_nodelay(true)?;
                if let Ok(permit) = state.connections.clone().try_acquire_owned() {
                    state.connection_count.fetch_add(1, Ordering::AcqRel);
                    connections.spawn(serve_socket(socket, tls1.clone(), handshakes1.clone(), state.clone(), stopped.clone(), false, permit));
                }
            },
            result = second.accept() => {
                let (socket, _) = result?; socket.set_nodelay(true)?;
                if let Ok(permit) = state.connections.clone().try_acquire_owned() {
                    state.connection_count.fetch_add(1, Ordering::AcqRel);
                    connections.spawn(serve_socket(socket, tls2.clone(), handshakes2.clone(), state.clone(), stopped.clone(), true, permit));
                }
            },
            _ = connections.join_next(), if !connections.is_empty() => {},
        }
    }
    drop(first); drop(second); let _ = stop.send(true);
    let drained = tokio::time::timeout(Duration::from_secs(state.config.shutdown_timeout_seconds), async { while connections.join_next().await.is_some() {} }).await.is_ok();
    if !drained { connections.abort_all(); while connections.join_next().await.is_some() {} }
    snapshot(&state, "disposed", &mut observations)?;
    let pending = state.pending.load(Ordering::Acquire); let bytes = state.bytes.load(Ordering::Acquire);
    let owners = state.owners.load(Ordering::Acquire); let sockets = state.connection_count.load(Ordering::Acquire);
    let complete = drained && pending == 0 && bytes == 0 && owners == 0 && sockets == 0;
    let closed = json!({"status": if complete { "Completed" } else { "Failed" }, "pending_requests": pending,
        "retained_managed_bytes": 0, "retained_native_bytes": bytes, "state": "Disposed", "byte_owners": owners, "connections": sockets});
    std::fs::write(Path::new(&out).join("closed.json"), serde_json::to_vec_pretty(&closed)?)?;
    println!("CLOSED {closed}"); std::io::stdout().flush()?;
    if !complete { return Err("direct Rust drain/owner retirement failed".into()); }
    Ok(())
}
