//! B23 direct mature-Rust application route. No NativeWeave ABI or managed host.
mod blobs;
mod db;
mod protocol;
mod service;

use axum::{
    Router,
    body::Body,
    extract::{Request, State},
    response::Response,
};
use futures_util::StreamExt;
use http::{HeaderValue, StatusCode};
use hyper::service::service_fn;
use hyper_util::{
    rt::{TokioExecutor, TokioIo, TokioTimer},
    server::conn::auto::Builder,
};
use protocol::{Authentication, Config, CreateItem, Error, Identity, Replay, Result};
use std::{convert::Infallible, io::BufReader, path::Path, sync::Arc, time::Duration};
use tokio::{net::TcpListener, sync::Semaphore, task::JoinSet};
use tokio_util::sync::CancellationToken;
use tower::ServiceExt;

struct Application {
    service: Arc<service::Service>,
    authentication: Authentication,
    requests: Arc<Semaphore>,
    waiters: Arc<Semaphore>,
    timeout: Duration,
}

#[tokio::main(flavor = "multi_thread", worker_threads = 2)]
async fn main() {
    eprintln!("B23_RUNTIME direct-rust=true tokio-workers=2");
    if let Err(error) = run().await {
        eprintln!("B23_COMPARATOR_FAILED {error}");
        std::process::exit(1);
    }
}
async fn run() -> Result<()> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args == ["--golden-vectors"] {
        println!("{}", protocol::golden());
        return Ok(());
    }
    if !(2..=3).contains(&args.len())
        || args[0] != "--config"
        || args.len() == 3 && !matches!(args[2].as_str(), "--migrate-only" | "--outbox-once")
    {
        return Err(Error(400, "invalid_command"));
    }
    if std::fs::metadata(&args[1])?.len() > 65536 {
        return Err(Error(400, "configuration_limit"));
    }
    let config: Config = serde_json::from_slice(&std::fs::read(&args[1])?)?;
    config.validate()?;
    let source = db::Database::new(&config, false).await?;
    let consumer = db::Database::new(&config, true).await?;
    if args.len() == 3 && args[2] == "--migrate-only" {
        source.close().await?;
        consumer.close().await?;
        println!("MIGRATED");
        return Ok(());
    }
    let blobs = blobs::Blobs::new(&config)?;
    let service = Arc::new(service::Service::new(
        source.clone(),
        consumer.clone(),
        blobs,
        protocol::Cursor::new(&config),
    ));
    let outcome = if args.len() == 3 {
        match service.outbox_once().await {
            Ok(delivered) => {
                println!("{{\"delivered\":{delivered}}}");
                Ok(())
            }
            Err(error) => Err(error),
        }
    } else {
        serve(&config, service.clone()).await
    };
    // Join app-owned retirements before releasing the filesystem owner lock,
    // including an exceptional HTTP/server exit.
    let database = source.close().await;
    let consumer_result = consumer.close().await;
    outcome?;
    database?;
    consumer_result?;
    if args.len() == 2 {
        println!(
            "CLOSED {{\"serverState\":\"Stopped\",\"databaseStatus\":\"Succeeded\",\"databaseResourcesReleased\":true,\"consumerStatus\":\"Succeeded\",\"consumerResourcesReleased\":true}}"
        );
    }
    Ok(())
}

#[cfg(not(target_os = "linux"))]
async fn serve(_config: &Config, _service: Arc<service::Service>) -> Result<()> {
    Err(Error(500, "linux_comparator_profile_not_implemented_here"))
}

#[cfg(target_os = "linux")]
async fn serve(config: &Config, service: Arc<service::Service>) -> Result<()> {
    let state = Arc::new(Application {
        service: service.clone(),
        authentication: Authentication::new(config),
        requests: Arc::new(Semaphore::new(256)),
        waiters: Arc::new(Semaphore::new(128)),
        timeout: Duration::from_secs(config.request_timeout_seconds),
    });
    let router = Router::new().fallback(dispatch).with_state(state);
    let listener = TcpListener::bind((config.listen_address.as_str(), config.port)).await?;
    let tls = match (&config.tls_certificate_pem, &config.tls_private_key_pem) {
        (Some(certificate), Some(key)) => Some(tls(certificate, key)?),
        _ => None,
    };
    let stop = CancellationToken::new();
    let slots = Arc::new(Semaphore::new(512));
    let mut connections = JoinSet::new();
    let mut signal = tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate())?;
    let mut worker = if config.run_workers {
        let service = service.clone();
        let stop = stop.clone();
        Some(tokio::spawn(async move {
            loop {
                tokio::select! {_ =stop.cancelled()=>return Ok::<(),Error>(()),result=service.outbox_once()=>{result?;}}
                tokio::select! {_=stop.cancelled()=>return Ok(()),_=tokio::time::sleep(Duration::from_secs(1))=>{}}
            }
        }))
    } else {
        None
    };
    println!(
        "READY {}://{}",
        if tls.is_some() { "https" } else { "http" },
        listener.local_addr()?
    );
    let mut failure = None;
    loop {
        while let Some(result) = connections.try_join_next() {
            if result.is_err() {
                failure = Some(Error(500, "connection_task_failed"));
            }
        }
        if failure.is_some() {
            break;
        }
        if worker.as_ref().is_some_and(|task| task.is_finished()) {
            failure = Some(Error(500, "worker_stopped"));
            break;
        }
        tokio::select! {
            _=signal.recv()=>break,
            result=tokio::signal::ctrl_c()=>{if result.is_err(){failure=Some(Error(500,"signal_failure"));}break;},
            accepted=listener.accept()=>{
                let (socket,_)=match accepted{Ok(value)=>value,Err(_)=>{failure=Some(Error(500,"accept_failure"));break;}};let permit=match slots.clone().try_acquire_owned(){Ok(value)=>value,Err(_)=>{drop(socket);continue;}};
                let router=router.clone();let stop=stop.clone();let tls=tls.clone();
                connections.spawn(async move{let _permit=permit;if let Some(acceptor)=tls{if let Ok(Ok(stream))=tokio::time::timeout(Duration::from_secs(10),acceptor.accept(socket)).await{connection(stream,router,stop,true).await;}}else{connection(socket,router,stop,false).await;}});
            }
        }
    }
    stop.cancel();
    drop(listener);
    let joining = async { while connections.join_next().await.is_some() {} };
    if tokio::time::timeout(Duration::from_secs(30), joining)
        .await
        .is_err()
    {
        connections.abort_all();
        while connections.join_next().await.is_some() {}
    }
    if let Some(worker) = worker.take() {
        match worker.await {
            Ok(Ok(())) => {}
            _ => failure = Some(Error(500, "worker_failed")),
        }
    }
    failure.map_or(Ok(()), Err)
}
fn tls(certificate: &str, key: &str) -> Result<tokio_rustls::TlsAcceptor> {
    let _ = rustls::crypto::ring::default_provider().install_default();
    let certificates = rustls_pemfile::certs(&mut BufReader::new(std::fs::File::open(Path::new(
        certificate,
    ))?))
    .collect::<std::result::Result<Vec<_>, _>>()?;
    let key =
        rustls_pemfile::private_key(&mut BufReader::new(std::fs::File::open(Path::new(key))?))?
            .ok_or(Error(400, "invalid_tls_key"))?;
    let mut config = rustls::ServerConfig::builder()
        .with_no_client_auth()
        .with_single_cert(certificates, key)
        .map_err(|_| Error(400, "invalid_tls_configuration"))?;
    config.alpn_protocols = vec![b"h2".to_vec(), b"http/1.1".to_vec()];
    Ok(tokio_rustls::TlsAcceptor::from(Arc::new(config)))
}
async fn connection<T: tokio::io::AsyncRead + tokio::io::AsyncWrite + Unpin + Send + 'static>(
    stream: T,
    router: Router,
    stop: CancellationToken,
    tls: bool,
) {
    let mut builder = Builder::new(TokioExecutor::new());
    builder
        .http1()
        .timer(TokioTimer::new())
        .header_read_timeout(Duration::from_secs(10))
        .max_headers(200)
        .max_buf_size(32768);
    builder
        .http2()
        .timer(TokioTimer::new())
        .max_concurrent_streams(32)
        .max_header_list_size(32768)
        .initial_stream_window_size(65536)
        .initial_connection_window_size(65536)
        .max_frame_size(32768);
    let builder = if tls { builder } else { builder.http1_only() };
    let service = service_fn(move |request: hyper::Request<hyper::body::Incoming>| {
        let router = router.clone();
        async move {
            Ok::<_, Infallible>(
                router
                    .oneshot(request.map(Body::new))
                    .await
                    .expect("infallible router"),
            )
        }
    });
    let connected = builder.serve_connection(TokioIo::new(stream), service);
    tokio::pin!(connected);
    tokio::select! {_= &mut connected=>{},_=stop.cancelled()=>{connected.as_mut().graceful_shutdown();let _=connected.await;}}
}

async fn dispatch(State(app): State<Arc<Application>>, request: Request) -> Response {
    let permit = match app.requests.clone().try_acquire_owned() {
        Ok(value) => value,
        Err(_) => {
            let Ok(_waiter) = app.waiters.clone().try_acquire_owned() else {
                return error_response(Error(503, "request_capacity"));
            };
            match tokio::time::timeout(Duration::from_secs(5), app.requests.clone().acquire_owned())
                .await
            {
                Ok(Ok(value)) => value,
                _ => return error_response(Error(503, "request_admission_timeout")),
            }
        }
    };
    let result = async {
        let identity = app.authentication.authenticate(request.headers())?;
        if matches!(request.method().as_str(), "POST" | "PUT") && !identity.can_write {
            return Err(Error(403, "forbidden"));
        }
        handle(&app.service, &identity, request).await
    };
    let response = match tokio::time::timeout(app.timeout, result).await {
        Ok(Ok(response)) => response,
        Ok(Err(error)) => error_response(error),
        Err(_) => error_response(Error(504, "request_timeout")),
    };
    let (parts, body) = response.into_parts();
    // Hold the request reservation through response-body consumption, including
    // asynchronous file download or disconnect; no per-body unbounded queue.
    let stream = body.into_data_stream().map(move |value| {
        let _keep = &permit;
        value
    });
    Response::from_parts(parts, Body::from_stream(stream))
}
async fn handle(
    service: &service::Service,
    identity: &Identity,
    request: Request,
) -> Result<Response> {
    let path = request.uri().path().to_owned();
    let method = request.method().as_str();
    if path == "/v1/items" && method == "POST" {
        let key = protocol::key(protocol::header(request.headers(), "idempotency-key")?)?;
        if content_length(request.headers())?.is_some_and(|x| x > 1048576) {
            return Err(Error(413, "json_limit"));
        }
        let bytes = axum::body::to_bytes(request.into_body(), 1048576)
            .await
            .map_err(|_| Error(413, "json_limit"))?;
        let input: CreateItem = serde_json::from_slice(&bytes)?;
        return Ok(replay_response(service.create(identity, key, input).await?));
    }
    if path == "/v1/items" && method == "GET" {
        let mut limit = None;
        let mut after = None;
        for (key, value) in form_urlencoded::parse(request.uri().query().unwrap_or("").as_bytes()) {
            match key.as_ref() {
                "limit" => {
                    if limit.is_some() {
                        return Err(Error(400, "ambiguous_query"));
                    }
                    if value.is_empty() || !value.bytes().all(|x| x.is_ascii_digit()) {
                        return Err(Error(400, "invalid_limit"));
                    }
                    limit = Some(
                        value
                            .parse::<u64>()
                            .map_err(|_| Error(400, "invalid_limit"))?,
                    );
                }
                "after" => {
                    if after.is_some() {
                        return Err(Error(400, "ambiguous_query"));
                    }
                    after = Some(value.into_owned());
                }
                _ => {}
            }
        }
        return Ok(json_response(
            200,
            protocol::json(
                &service
                    .list(identity, limit.unwrap_or(100), after.as_deref())
                    .await?,
            ),
        ));
    }
    if let Some(operation) = path.strip_prefix("/v1/operations/") {
        if method == "GET" {
            return Ok(replay_response(service.recover(identity, operation).await?));
        }
    }
    if let Some(item) = path
        .strip_prefix("/v1/items/")
        .and_then(|x| x.strip_suffix("/attachment"))
    {
        protocol::id(item)?;
        if method == "GET" {
            let attachment = service.attachment(identity, item).await?;
            let file = service.blobs.open(&attachment).await?;
            let stream = tokio_util::io::ReaderStream::with_capacity(file, 32768);
            return Ok(Response::builder()
                .status(200)
                .header("content-type", "application/octet-stream")
                .header("content-length", attachment.length.to_string())
                .header("etag", format!("\"{}\"", attachment.version))
                .header("content-sha256", attachment.hash)
                .body(Body::from_stream(stream))
                .expect("fixed response headers"));
        }
        if method == "PUT" {
            let key = protocol::key(protocol::header(request.headers(), "idempotency-key")?)?;
            let expected = protocol::header(request.headers(), "if-match")?
                .ok_or(Error(400, "invalid_expected_version"))?
                .trim_matches('"');
            if expected.is_empty() || !expected.bytes().all(|x| x.is_ascii_digit()) {
                return Err(Error(400, "invalid_expected_version"));
            }
            let version = expected
                .parse::<i64>()
                .ok()
                .filter(|x| *x > 0 && *x < i64::MAX)
                .ok_or(Error(400, "invalid_expected_version"))?;
            let length =
                content_length(request.headers())?.ok_or(Error(411, "content_length_required"))?;
            if protocol::header(request.headers(), "content-encoding")?
                .is_some_and(|x| !x.eq_ignore_ascii_case("identity"))
            {
                return Err(Error(415, "unsupported_content_encoding"));
            }
            service.authorize_item(identity, item).await?;
            let mut upload = service.blobs.begin(identity, item, &key, version, length)?;
            upload.stage(request.into_body()).await?;
            let replay = service
                .bind(identity, key, item, version, &mut upload)
                .await?;
            upload.complete(replay.status);
            return Ok(replay_response(replay));
        }
    }
    Err(Error(404, "not_found"))
}
fn content_length(headers: &http::HeaderMap) -> Result<Option<i64>> {
    protocol::header(headers, "content-length")?
        .map(|x| {
            x.parse::<i64>()
                .ok()
                .filter(|x| *x >= 0)
                .ok_or(Error(400, "invalid_content_length"))
        })
        .transpose()
}
fn json_response(status: u16, bytes: Vec<u8>) -> Response {
    Response::builder()
        .status(status)
        .header("content-type", "application/json")
        .header("content-length", bytes.len())
        .body(Body::from(bytes))
        .expect("fixed JSON response")
}
fn error_response(error: Error) -> Response {
    json_response(error.0, protocol::problem(error.1, None))
}
fn replay_response(replay: Replay) -> Response {
    let mut response = json_response(replay.status, replay.bytes);
    let headers = response.headers_mut();
    headers.insert(
        "idempotency-retention-seconds",
        HeaderValue::from_static("604800"),
    );
    headers.insert(
        "location",
        HeaderValue::from_str(&format!("/v1/operations/{}", replay.operation_id))
            .expect("canonical operation id"),
    );
    if response.status() == StatusCode::SERVICE_UNAVAILABLE {
        response
            .headers_mut()
            .insert("retry-after", HeaderValue::from_static("1"));
    }
    response
}
