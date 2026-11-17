use crate::{
    Error, Result,
    runtime::{OPERATIONS, Source},
};
use futures_util::{FutureExt, future::BoxFuture};
use nativeweave_interop_abi::*;
use nativeweave_interop_async::Operation;
use std::{
    panic::AssertUnwindSafe,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
};
use tokio::sync::Notify;

/// The source-specific cancellation hook is installed before provider dispatch.
pub struct Cancel {
    requested: AtomicBool,
    changed: Notify,
    hook: Mutex<Option<Arc<dyn Fn() + Send + Sync>>>,
}
impl Cancel {
    fn new() -> Self {
        Self {
            requested: AtomicBool::new(false),
            changed: Notify::new(),
            hook: Mutex::new(None),
        }
    }
    pub fn request(&self) {
        self.requested.store(true, Ordering::Release);
        self.changed.notify_waiters();
        let hook = self.hook.lock().unwrap_or_else(|e| e.into_inner()).clone();
        if let Some(hook) = hook {
            hook();
        }
    }
    pub fn detach(&self) {
        self.hook.lock().unwrap_or_else(|e| e.into_inner()).take();
    }
    pub fn requested(&self) -> bool {
        self.requested.load(Ordering::Acquire)
    }
    pub fn install(&self, hook: Arc<dyn Fn() + Send + Sync>) {
        *self.hook.lock().unwrap_or_else(|e| e.into_inner()) = Some(hook.clone());
        if self.requested() {
            hook();
        }
    }
    pub async fn notified(&self) {
        loop {
            let ready = self.changed.notified();
            tokio::pin!(ready);
            ready.as_mut().enable();
            if self.requested() {
                return;
            }
            ready.await;
        }
    }
}
pub enum Value {
    Owner(crate::abi::OwnerValue),
    Count(i64),
    Buffer(crate::abi::Buffer),
}
pub type Action = Box<dyn FnOnce(Arc<Cancel>) -> BoxFuture<'static, Result<Value>> + Send>;
enum Work {
    Async(Action),
    Immediate(Action),
    SourceClose,
}
/// An exact Core operation; setup/callback and worker sources retire independently.
pub struct DbOperation {
    pub source: Arc<Source>,
    pub core: Arc<Operation<Value>>,
    pub cancel: Arc<Cancel>,
    work: Mutex<Option<Work>>,
    pub error: Mutex<Option<Error>>,
    pub error_taken: AtomicBool,
    pub result_kind: u32,
    counted: bool,
    source_close: bool,
}
impl DbOperation {
    pub fn create(
        source: Arc<Source>,
        result_kind: u32,
        control: bool,
        action: Action,
    ) -> Result<Arc<Self>> {
        let counted = source.reserve_operation(control)?;
        OPERATIONS.fetch_add(1, Ordering::Relaxed);
        Ok(Arc::new(Self {
            source,
            core: Arc::new(Operation::new()),
            cancel: Arc::new(Cancel::new()),
            work: Mutex::new(Some(if counted {
                Work::Async(action)
            } else {
                Work::Immediate(action)
            })),
            error: Mutex::new(None),
            error_taken: AtomicBool::new(false),
            result_kind,
            counted,
            source_close: false,
        }))
    }
    pub fn close_source(source: Arc<Source>) -> Result<Arc<Self>> {
        // A distinct finite cleanup reserve avoids waiting on the runtime that is being joined.
        source
            .close_waiters
            .try_update(Ordering::AcqRel, Ordering::Acquire, |n| {
                (n < source.config.limits.connections as usize + 1).then_some(n + 1)
            })
            .map_err(|_| Error::capacity())?;
        OPERATIONS.fetch_add(1, Ordering::Relaxed);
        Ok(Arc::new(Self {
            source,
            core: Arc::new(Operation::new()),
            cancel: Arc::new(Cancel::new()),
            work: Mutex::new(Some(Work::SourceClose)),
            error: Mutex::new(None),
            error_taken: AtomicBool::new(false),
            result_kind: 101,
            counted: false,
            source_close: true,
        }))
    }
    /// # Safety
    /// Caller keeps its callback cookie rooted through successful Core retirement.
    pub unsafe fn start(self: &Arc<Self>, callbacks: NwOperationCallbacksV1) -> u32 {
        let status = self.core.start(callbacks);
        if status != NW_OK {
            return status;
        }
        let work = self.work.lock().unwrap_or_else(|e| e.into_inner()).take();
        let Some(work) = work else {
            return NW_CONTRACT_VIOLATION;
        };
        let lease = match self.core.reserve_source() {
            Ok(v) => v,
            Err(s) => return s,
        };
        let operation = self.clone();
        match work {
            Work::Immediate(action) => {
                let result = std::panic::catch_unwind(AssertUnwindSafe(|| {
                    action(operation.cancel.clone()).now_or_never()
                }));
                let result = match result {
                    Ok(Some(value)) => value,
                    Ok(None) => Err(Error::state(
                        "cleanup attempted I/O after engine retirement",
                    )),
                    Err(_) => Err(Error::new(
                        NW_NATIVE_PANIC,
                        8,
                        "retired cleanup panic contained",
                    )),
                };
                // SAFETY: the live Core source lease covers synchronous terminal delivery inside start.
                unsafe {
                    operation.complete(result);
                }
                drop(lease);
            }
            Work::Async(action) => {
                self.source.runtime.handle.spawn(async move {
                    let result = AssertUnwindSafe(async { action(operation.cancel.clone()).await })
                        .catch_unwind()
                        .await;
                    let result = match result {
                        Ok(value) => value,
                        Err(_) => {
                            operation.source.poisoned.store(true, Ordering::Release);
                            Err(Error::new(
                                NW_NATIVE_PANIC,
                                8,
                                "native database panic contained",
                            ))
                        }
                    };
                    // SAFETY: Core accepted the retained callback record and this WorkLease covers its use.
                    unsafe { operation.complete(result) };
                    drop(lease);
                });
            }
            Work::SourceClose => {
                self.source.request_close();
                let waiter = std::thread::Builder::new()
                    .name("nw-db-close".into())
                    .spawn(move || {
                        operation.source.runtime.join();
                        let buffer = crate::abi::Buffer::bytes(
                            operation.source.clone(),
                            crate::abi::source_snapshot(&operation.source),
                        );
                        // SAFETY: this reserved WorkLease retains Core callback admission through notification.
                        unsafe { operation.complete(Ok(Value::Buffer(buffer))) };
                        drop(lease);
                    });
                if waiter.is_err() {
                    // Thread creation failure occurs after start admission; preserve a terminal error, never fake release.
                    // SAFETY: Core callback ownership was accepted and this Arc remains live for notification.
                    unsafe { self.complete(Err(Error::capacity())) };
                }
            }
        }
        NW_OK
    }
    unsafe fn complete(&self, result: Result<Value>) {
        // Terminal work has transferred its independent cursor/buffer owner. Core may
        // request cancellation while draining the old callback; that cannot stop the result.
        self.cancel.detach();
        let (status, value, fields) = match result {
            Ok(v) => (NW_OK, Some(v), Default::default()),
            Err(error) => {
                let fields = error.fields();
                let status = error.status;
                *self.error.lock().unwrap_or_else(|e| e.into_inner()) = Some(error);
                (status, None, fields)
            }
        };
        // SAFETY: the caller holds the corresponding Core WorkLease and registered callback root.
        let _ = unsafe { self.core.complete(status, value, fields) };
    }
}
impl Drop for DbOperation {
    fn drop(&mut self) {
        if self.counted {
            self.source.release_operation();
        }
        if self.source_close {
            self.source.close_waiters.fetch_sub(1, Ordering::AcqRel);
        }
        OPERATIONS.fetch_sub(1, Ordering::Relaxed);
    }
}
