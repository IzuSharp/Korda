//! Test-only framed PostgreSQL proxy, never a product network implementation.
use std::sync::{
    Arc,
    atomic::{AtomicBool, Ordering},
};
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    net::{TcpListener, TcpStream},
};
type TestResult<T> = Result<T, Box<dyn std::error::Error + Send + Sync>>;

/// Forward COMMIT then observe and discard CommandComplete(COMMIT) and
/// ReadyForQuery(Idle). The client cannot receive trustworthy commit confirmation.
pub async fn lose_commit_ack(
    listener: TcpListener,
    upstream: (String, u16),
    committed: Arc<AtomicBool>,
) -> TestResult<()> {
    let (client, _) = listener.accept().await?;
    let server = TcpStream::connect(upstream).await?;
    let (mut cr, mut cw) = client.into_split();
    let (mut sr, mut sw) = server.into_split();
    let armed = Arc::new(AtomicBool::new(false));
    let request_armed = Arc::clone(&armed);
    let request = tokio::spawn(async move {
        let size = cr.read_u32().await?;
        if !(8..=1_048_576).contains(&size) {
            return Err(std::io::Error::other("invalid startup frame"));
        }
        let mut bytes = vec![0; size as usize - 4];
        cr.read_exact(&mut bytes).await?;
        sw.write_u32(size).await?;
        sw.write_all(&bytes).await?;
        loop {
            let tag = match cr.read_u8().await {
                Ok(t) => t,
                Err(e) if e.kind() == std::io::ErrorKind::UnexpectedEof => break,
                Err(e) => return Err(e),
            };
            let size = cr.read_u32().await?;
            if !(4..=1_048_576).contains(&size) {
                return Err(std::io::Error::other("invalid request frame"));
            }
            let mut bytes = vec![0; size as usize - 4];
            cr.read_exact(&mut bytes).await?;
            if tag == b'Q' && bytes == b"COMMIT\0" {
                request_armed.store(true, Ordering::SeqCst);
            }
            sw.write_u8(tag).await?;
            sw.write_u32(size).await?;
            sw.write_all(&bytes).await?;
        }
        Ok::<(), std::io::Error>(())
    });
    let mut command_complete = false;
    let response = async {
        loop {
            let tag = sr.read_u8().await?;
            let size = sr.read_u32().await?;
            if !(4..=1_048_576).contains(&size) {
                return Err(std::io::Error::other("invalid response frame"));
            }
            let mut bytes = vec![0; size as usize - 4];
            sr.read_exact(&mut bytes).await?;
            if armed.load(Ordering::SeqCst) {
                if tag == b'C' {
                    command_complete = bytes == b"COMMIT\0";
                }
                if tag == b'Z' {
                    if !command_complete || bytes != b"I" {
                        return Err(std::io::Error::other("missing durable COMMIT/Idle oracle"));
                    }
                    committed.store(true, Ordering::SeqCst);
                    cw.shutdown().await?;
                    break;
                }
            } else {
                cw.write_u8(tag).await?;
                cw.write_u32(size).await?;
                cw.write_all(&bytes).await?;
            }
        }
        Ok::<(), std::io::Error>(())
    }
    .await;
    request.abort();
    let _ = request.await;
    response?;
    Ok(())
}
