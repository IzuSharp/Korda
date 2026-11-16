use crate::{Command, ProviderError, Result, Session};
use futures_util::{Stream, TryStreamExt};
use sqlx::{AssertSqlSafe, postgres::PgRow};
use std::{
    pin::Pin,
    sync::{
        Arc,
        atomic::{AtomicBool, AtomicUsize, Ordering},
    },
    task::{Context, Poll},
};
use tokio::sync::{Notify, mpsc, watch};

#[derive(Clone, Debug, Default)]
pub struct StreamReceipt {
    pub backend_pid: i32,
    pub stopped: bool,
    /// Every SQLx stream accessor and cleanup operation has exited.
    pub retired: bool,
    pub protocol_clean: bool,
    pub quarantined: bool,
    /// Maximum charged rows in the single-item channel plus its delivery handoff.
    /// poll_recv frees channel capacity before the receiver subtracts its charge,
    /// so one queued row and one handoff may overlap: the finite bound is two.
    /// SQLx buffers and B06's decoded bytes are separate, not an RSS cap.
    pub retained_rows_peak: usize,
}
struct State {
    stop: AtomicBool,
    changed: Notify,
    rows: AtomicUsize,
    peak: AtomicUsize,
    receipt: watch::Sender<StreamReceipt>,
}
#[derive(Clone)]
pub struct StreamControl {
    state: Arc<State>,
}
impl StreamControl {
    /// Nonblocking and idempotent; never opens a cancel-key connection or waits on I/O.
    pub fn request_stop(&self) {
        self.state.stop.store(true, Ordering::Release);
        self.state.changed.notify_waiters();
    }
    async fn stopped(&self) {
        loop {
            let notified = self.state.changed.notified();
            tokio::pin!(notified);
            notified.as_mut().enable();
            if self.state.stop.load(Ordering::Acquire) {
                return;
            }
            notified.await;
        }
    }
    pub fn snapshot(&self) -> StreamReceipt {
        self.state.receipt.borrow().clone()
    }
    pub async fn join(&self) -> StreamReceipt {
        let mut changed = self.state.receipt.subscribe();
        loop {
            let receipt = changed.borrow_and_update().clone();
            if receipt.retired {
                return receipt;
            }
            if changed.changed().await.is_err() {
                return self.snapshot();
            }
        }
    }
}
/// A business receiver only. Its independent worker retains the physical lease,
/// drains pending protocol messages, and reports retirement through control().
pub struct RowStream {
    receiver: mpsc::Receiver<Result<PgRow>>,
    control: StreamControl,
}
impl RowStream {
    pub fn control(&self) -> StreamControl {
        self.control.clone()
    }
}
impl Stream for RowStream {
    type Item = Result<PgRow>;
    fn poll_next(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Option<Self::Item>> {
        // Receiving releases the slot before this accounting handoff completes.
        // The next producer may add one row in between; a single receiver must
        // subtract before another poll, bounding this conservative counter at two.
        let polled = self.receiver.poll_recv(cx);
        if matches!(polled, Poll::Ready(Some(Ok(_)))) {
            self.control.state.rows.fetch_sub(1, Ordering::AcqRel);
        }
        polled
    }
}
impl Drop for RowStream {
    fn drop(&mut self) {
        self.control.request_stop();
    }
}
impl Session {
    /// Starts one bounded stream. SQLx Execute limit=0 still requests all server
    /// results; this capacity is owned-row backpressure, not wire fetch batching.
    pub fn stream(&self, command: Command) -> Result<RowStream> {
        self.inner.validate(&command)?;
        let arguments = command.arguments()?;
        let operation = self.inner.admit()?;
        let (sender, receiver) = mpsc::channel(1);
        let (receipt, _) = watch::channel(StreamReceipt {
            backend_pid: self.inner.pid,
            ..Default::default()
        });
        let control = StreamControl {
            state: Arc::new(State {
                stop: AtomicBool::new(false),
                changed: Notify::new(),
                rows: AtomicUsize::new(0),
                peak: AtomicUsize::new(0),
                receipt,
            }),
        };
        let worker_control = control.clone();
        let inner = Arc::clone(&self.inner);
        self.inner.runtime.spawn(async move {
            let mut guard = inner.conn.lock().await;
            let mut stopped = false;
            let mut clean = false;
            if let Some(connection) = guard.as_mut() {
                {
                    let mut rows = sqlx::query_with(AssertSqlSafe(command.sql), arguments).fetch(&mut **connection);
                    loop {
                        // Reserve the only channel slot before asking SQLx for a row.
                        let permit = tokio::select! {
                            biased;
                            _ = worker_control.stopped() => { stopped = true; break; }
                            permit = sender.reserve() => match permit { Ok(p) => p, Err(_) => { stopped = true; break; } }
                        };
                        let next = tokio::select! {
                            biased;
                            _ = worker_control.stopped() => { stopped = true; break; }
                            row = rows.try_next() => row
                        };
                        match next {
                            Ok(Some(row)) => {
                                let retained = worker_control.state.rows.fetch_add(1, Ordering::AcqRel) + 1;
                                worker_control.state.peak.fetch_max(retained, Ordering::AcqRel);
                                permit.send(Ok(row));
                            }
                            Ok(None) => break,
                            Err(error) => { let error = ProviderError::from(error); inner.observe(&error); permit.send(Err(error)); break; }
                        }
                    }
                } // The SQLx cursor is dropped before ping drains pending ReadyForQuery.
                clean = inner.drain(connection).await.is_ok() && !inner.untrusted.load(Ordering::Acquire);
            }
            drop(sender); drop(guard); drop(operation);
            let quarantined = !clean;
            if quarantined {
                inner.request_close();
                // The receipt follows physical closure and pool permit retirement.
                crate::session::SessionControl::from_inner(Arc::clone(&inner)).join().await;
            }
            worker_control.state.receipt.send_replace(StreamReceipt { backend_pid: inner.pid, stopped,
                retired: true, protocol_clean: clean, quarantined,
                retained_rows_peak: worker_control.state.peak.load(Ordering::Acquire) });
        });
        Ok(RowStream { receiver, control })
    }
}
