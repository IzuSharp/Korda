//! Feature-matched direct public Axum/Hyper baseline; no FFI or C# application work.
use axum::{Router, body::Body, routing::get};
use hyper_util::{
    rt::{TokioExecutor, TokioIo, TokioTimer},
    server::conn::auto::Builder,
};
use std::{net::Ipv4Addr, sync::Arc, time::Duration};
use tokio::sync::{Semaphore, oneshot};
use tower::ServiceExt;
#[tokio::main(flavor = "current_thread")]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    let listener = tokio::net::TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).await?;
    println!("http://{}/", listener.local_addr()?);
    let router = Router::new().route(
        "/bench",
        get(|| async { ([(http::header::CONTENT_LENGTH, "16")], "0123456789abcdef") }),
    );
    let permits = Arc::new(Semaphore::new(128));
    let (done, mut finished) = oneshot::channel();
    std::thread::spawn(move || {
        let mut line = String::new();
        let _ = std::io::stdin().read_line(&mut line);
        let _ = done.send(());
    });
    loop {
        tokio::select! {_=&mut finished=>break,result=listener.accept()=>{let(stream,_)=result?;stream.set_nodelay(true)?;let Ok(permit)=permits.clone().try_acquire_owned()else{continue;};let router=router.clone();tokio::spawn(async move{let _permit=permit;let mut builder=Builder::new(TokioExecutor::new()).http1_only();builder.http1().timer(TokioTimer::new()).header_read_timeout(Duration::from_secs(15)).max_headers(200).max_buf_size(32768);let service=hyper::service::service_fn(move|request:hyper::Request<hyper::body::Incoming>|router.clone().oneshot(request.map(Body::new)));let _=builder.serve_connection(TokioIo::new(stream),service).await;});}}
    }
    Ok(())
}
