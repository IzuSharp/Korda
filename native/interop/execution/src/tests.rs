use super::*;
use std::pin::Pin;
use std::sync::{Barrier, mpsc};
use std::task::{Context, Poll, Waker};
use std::time::Duration;

fn limits() -> Limits {
    Limits {
        workers: 1,
        blocking_workers: 1,
        admitted: 2,
        waiters: 2,
        bytes: 8,
        cleanup: 2,
    }
}
fn poll<F: Future>(future: Pin<&mut F>) -> Poll<F::Output> {
    future.poll(&mut Context::from_waker(Waker::noop()))
}
fn ready<F: Future>(future: Pin<&mut F>) -> F::Output {
    match poll(future) {
        Poll::Ready(value) => value,
        Poll::Pending => panic!("expected ready"),
    }
}
fn settle(executor: &EngineExecutor) {
    let end = Instant::now() + Duration::from_secs(5);
    while executor.snapshot().tracked_tasks != 0 {
        assert!(Instant::now() < end, "completion supervisor did not finish");
        std::thread::sleep(Duration::from_millis(1));
    }
}
fn closed(executor: &EngineExecutor) {
    assert!(executor.join());
    let state = executor.snapshot();
    assert!(state.closing && state.joined);
    assert_eq!(
        (
            state.admitted,
            state.reserved_bytes,
            state.issued,
            state.running,
            state.retiring,
            state.tracked_tasks
        ),
        (0, 0, 0, 0, 0, 0)
    );
    assert_eq!(state.workers_started, state.workers_stopped);
    assert!(state.workers_started >= 1);
}

#[test]
fn zero_and_impossible_limits_reject_before_waiting() {
    let mut invalid = limits();
    invalid.workers = 0;
    assert!(matches!(
        EngineExecutor::new(invalid),
        Err(AdmissionError::InvalidLimits)
    ));
    for field in 0..3 {
        let mut value = limits();
        match field {
            0 => value.admitted = 0,
            1 => value.cleanup = 0,
            _ => value.bytes = 0,
        }
        let executor = EngineExecutor::new(value).unwrap();
        assert!(matches!(
            executor.try_acquire(1),
            Err(AdmissionError::Impossible)
        ));
        let mut waiting = Box::pin(executor.acquire(1));
        assert!(matches!(
            ready(waiting.as_mut()),
            Err(AdmissionError::Impossible)
        ));
        assert_eq!(executor.snapshot().waiters, 0);
        drop(waiting);
        closed(&executor);
    }
}

#[test]
fn all_budgets_are_reserved_atomically_and_released_once() {
    let executor = EngineExecutor::new(limits()).unwrap();
    let first = executor.try_acquire(6).unwrap();
    assert_eq!(first.bytes(), 6);
    assert!(matches!(executor.try_acquire(3), Err(AdmissionError::Full)));
    assert!(matches!(
        executor.try_acquire(9),
        Err(AdmissionError::Impossible)
    ));
    let second = executor.try_acquire(2).unwrap();
    assert!(matches!(executor.try_acquire(0), Err(AdmissionError::Full)));
    let clone = Arc::clone(&first);
    drop(first);
    assert_eq!(
        (
            executor.snapshot().admitted,
            executor.snapshot().reserved_bytes
        ),
        (2, 8)
    );
    drop(clone);
    drop(second);
    closed(&executor);
}

#[test]
fn cleanup_limit_is_independent_of_operation_and_byte_limits() {
    let executor = EngineExecutor::new(Limits {
        admitted: 8,
        cleanup: 1,
        ..limits()
    })
    .unwrap();
    let first = executor.try_acquire(1).unwrap();
    assert!(matches!(executor.try_acquire(1), Err(AdmissionError::Full)));
    assert_eq!(
        (
            executor.snapshot().admitted,
            executor.snapshot().reserved_bytes
        ),
        (1, 1)
    );
    drop(first);
    closed(&executor);
}

#[test]
fn fifo_does_not_bypass_head_and_canceled_head_wakes_next() {
    let executor = EngineExecutor::new(limits()).unwrap();
    let first = executor.try_acquire(4).unwrap();
    let mut big = Box::pin(executor.acquire(8));
    let mut small = Box::pin(executor.acquire(1));
    assert!(poll(big.as_mut()).is_pending());
    assert!(poll(small.as_mut()).is_pending());
    assert!(matches!(executor.try_acquire(1), Err(AdmissionError::Full)));
    let mut excess = Box::pin(executor.acquire(0));
    assert!(matches!(ready(excess.as_mut()), Err(AdmissionError::Full)));
    drop(excess);
    drop(big);
    let second = ready(small.as_mut()).unwrap();
    assert_eq!(
        (executor.snapshot().admitted, executor.snapshot().waiters),
        (2, 0)
    );
    drop(small);
    drop(second);
    drop(first);
    closed(&executor);
}

#[test]
fn fifo_issues_in_declared_order() {
    let executor = EngineExecutor::new(Limits {
        admitted: 1,
        cleanup: 1,
        ..limits()
    })
    .unwrap();
    let first = executor.try_acquire(8).unwrap();
    let mut second = Box::pin(executor.acquire(8));
    let mut third = Box::pin(executor.acquire(1));
    assert!(poll(second.as_mut()).is_pending());
    assert!(poll(third.as_mut()).is_pending());
    drop(first);
    assert!(poll(third.as_mut()).is_pending());
    let second_owner = ready(second.as_mut()).unwrap();
    drop(second_owner);
    let third_owner = ready(third.as_mut()).unwrap();
    drop(second);
    drop(third);
    drop(third_owner);
    closed(&executor);
}

#[test]
fn close_wakes_waiters_and_retains_issued_permits() {
    let executor = EngineExecutor::new(limits()).unwrap();
    let issued = executor.try_acquire(8).unwrap();
    let mut waiting = Box::pin(executor.acquire(1));
    assert!(poll(waiting.as_mut()).is_pending());
    executor.request_close();
    assert!(matches!(
        ready(waiting.as_mut()),
        Err(AdmissionError::Closed)
    ));
    assert!(matches!(
        executor.try_acquire(0),
        Err(AdmissionError::Closed)
    ));
    assert!(matches!(
        executor.spawn(&issued, async { Ok(7u64) }, |_| panic!(
            "rejected work cannot run"
        )),
        Err(AdmissionError::Closed)
    ));
    assert_eq!(
        (
            executor.snapshot().issued,
            executor.snapshot().reserved_bytes
        ),
        (1, 8)
    );
    assert!(!executor.try_join());
    drop(waiting);
    drop(issued);
    closed(&executor);
}

#[test]
fn waiter_zero_is_explicit_try_only_profile() {
    let executor = EngineExecutor::new(Limits {
        waiters: 0,
        ..limits()
    })
    .unwrap();
    let issued = executor.try_acquire(8).unwrap();
    let mut waiting = Box::pin(executor.acquire(1));
    assert!(matches!(ready(waiting.as_mut()), Err(AdmissionError::Full)));
    drop(waiting);
    drop(issued);
    closed(&executor);
}

#[test]
fn business_terminal_keeps_bytes_until_actual_owner_retirement() {
    let executor = EngineExecutor::new(limits()).unwrap();
    let issued = executor.try_acquire(8).unwrap();
    let (send, receive) = mpsc::channel();
    executor
        .spawn(&issued, async { Ok(123u64) }, move |outcome| {
            send.send(outcome.unwrap()).unwrap();
        })
        .unwrap();
    assert_eq!(receive.recv_timeout(Duration::from_secs(5)).unwrap(), 123);
    settle(&executor);
    std::thread::sleep(Duration::from_millis(2));
    let state = executor.snapshot();
    assert_eq!(
        (state.running, state.retiring, state.reserved_bytes),
        (0, 1, 8)
    );
    assert!(state.oldest_cleanup_millis >= 1);
    executor.request_close();
    assert!(!executor.try_join());
    drop(issued);
    closed(&executor);
    assert_eq!(executor.snapshot().oldest_cleanup_millis, 0);
}

#[test]
fn started_blocking_work_survives_close_timeout_and_is_really_joined() {
    let executor = EngineExecutor::new(limits()).unwrap();
    let issued = executor.try_acquire(8).unwrap();
    let (entered_send, entered) = mpsc::channel();
    let (release, blocked) = mpsc::channel();
    let (done_send, done) = mpsc::channel();
    executor
        .spawn_blocking(
            &issued,
            move || {
                entered_send.send(()).unwrap();
                blocked.recv().unwrap();
                Ok(7u64)
            },
            move |result| {
                done_send.send(result.unwrap()).unwrap();
            },
        )
        .unwrap();
    entered.recv_timeout(Duration::from_secs(5)).unwrap();
    executor.request_close();
    assert!(!executor.try_join());
    assert!(done.recv_timeout(Duration::from_millis(20)).is_err());
    let state = executor.snapshot();
    assert_eq!(
        (state.admitted, state.reserved_bytes, state.running),
        (1, 8, 1)
    );
    assert!(!state.joined);
    release.send(()).unwrap();
    assert_eq!(done.recv_timeout(Duration::from_secs(5)).unwrap(), 7);
    settle(&executor);
    drop(issued);
    closed(&executor);
    assert_eq!(executor.snapshot().workers_started, 2);
}

#[test]
fn cooperative_close_is_observed_by_actual_native_future() {
    let executor = EngineExecutor::new(limits()).unwrap();
    let issued = executor.try_acquire(1).unwrap();
    let native = Arc::clone(&executor);
    let (send, receive) = mpsc::channel();
    executor
        .spawn(
            &issued,
            async move {
                native.cancelled().await;
                Ok(17u64)
            },
            move |value| {
                send.send(value.unwrap()).unwrap();
            },
        )
        .unwrap();
    executor.request_close();
    assert_eq!(receive.recv_timeout(Duration::from_secs(5)).unwrap(), 17);
    settle(&executor);
    drop(issued);
    closed(&executor);
}

#[test]
fn wrong_instance_and_duplicate_start_cannot_spend_a_permit_twice() {
    let first = EngineExecutor::new(limits()).unwrap();
    let second = EngineExecutor::new(limits()).unwrap();
    let issued = first.try_acquire(1).unwrap();
    assert!(matches!(
        second.spawn(&issued, async { Ok(0u64) }, |_| {}),
        Err(AdmissionError::WrongOwner)
    ));
    let (send, receive) = mpsc::channel();
    first
        .spawn(&issued, async { Ok(0u64) }, move |_| {
            send.send(()).unwrap();
        })
        .unwrap();
    assert!(matches!(
        first.spawn(&issued, async { Ok(0u64) }, |_| {}),
        Err(AdmissionError::AlreadyStarted)
    ));
    receive.recv_timeout(Duration::from_secs(5)).unwrap();
    settle(&first);
    drop(issued);
    closed(&first);
    closed(&second);
}

struct PanicPayload(Arc<AtomicU64>);
impl Drop for PanicPayload {
    fn drop(&mut self) {
        self.0.fetch_add(1, Ordering::AcqRel);
        panic!("must quarantine unknown panic payload");
    }
}

#[test]
fn actual_future_poll_panic_is_observed_and_poison_closes_admission() {
    let executor = EngineExecutor::new(limits()).unwrap();
    let issued = executor.try_acquire(1).unwrap();
    let destroyed = Arc::new(AtomicU64::new(0));
    let payload = Arc::clone(&destroyed);
    let (send, receive) = mpsc::channel();
    executor
        .spawn::<u64, _, _>(
            &issued,
            async move {
                std::panic::panic_any(PanicPayload(payload));
            },
            move |value| {
                send.send(value.err().unwrap().status).unwrap();
            },
        )
        .unwrap();
    assert_eq!(
        receive.recv_timeout(Duration::from_secs(5)).unwrap(),
        NW_NATIVE_PANIC
    );
    settle(&executor);
    assert!(executor.snapshot().poisoned && executor.snapshot().closing);
    assert_eq!(
        (
            executor.snapshot().panic_payloads,
            destroyed.load(Ordering::Acquire)
        ),
        (1, 0)
    );
    drop(issued);
    closed(&executor);
}

#[test]
fn actual_blocking_panic_is_observed() {
    let executor = EngineExecutor::new(limits()).unwrap();
    let issued = executor.try_acquire(1).unwrap();
    let (send, receive) = mpsc::channel();
    executor
        .spawn_blocking::<u64, _, _>(
            &issued,
            || panic!("actual blocking body"),
            move |value| {
                send.send(value.err().unwrap().status).unwrap();
            },
        )
        .unwrap();
    assert_eq!(
        receive.recv_timeout(Duration::from_secs(5)).unwrap(),
        NW_NATIVE_PANIC
    );
    settle(&executor);
    assert_eq!(executor.snapshot().panic_payloads, 1);
    drop(issued);
    closed(&executor);
}

#[test]
fn completion_panic_retires_supervisor_and_quarantines_unknown_payload() {
    let executor = EngineExecutor::new(limits()).unwrap();
    let issued = executor.try_acquire(1).unwrap();
    let destroyed = Arc::new(AtomicU64::new(0));
    let payload = Arc::clone(&destroyed);
    executor
        .spawn(&issued, async { Ok(1u64) }, move |_| {
            std::panic::panic_any(PanicPayload(payload));
        })
        .unwrap();
    settle(&executor);
    assert!(executor.snapshot().poisoned);
    assert_eq!(
        (
            executor.snapshot().retiring,
            executor.snapshot().panic_payloads,
            destroyed.load(Ordering::Acquire)
        ),
        (1, 1, 0)
    );
    drop(issued);
    closed(&executor);
}

#[test]
fn close_with_issued_permit_race_never_loses_an_obligation() {
    for _ in 0..64 {
        let executor = EngineExecutor::new(limits()).unwrap();
        let issued = executor.try_acquire(8).unwrap();
        let gate = Arc::new(Barrier::new(2));
        let worker_executor = Arc::clone(&executor);
        let worker_gate = Arc::clone(&gate);
        let worker_issued = Arc::clone(&issued);
        let (send, receive) = mpsc::channel();
        let starter = std::thread::spawn(move || {
            worker_gate.wait();
            worker_executor.spawn(&worker_issued, async { Ok(1u64) }, move |result| {
                send.send(result.unwrap()).unwrap();
            })
        });
        gate.wait();
        executor.request_close();
        match starter.join().unwrap() {
            Ok(()) => {
                assert_eq!(receive.recv_timeout(Duration::from_secs(5)).unwrap(), 1);
                settle(&executor);
            }
            Err(AdmissionError::Closed) => assert_eq!(executor.snapshot().issued, 1),
            Err(error) => panic!("unexpected race outcome {error:?}"),
        }
        drop(issued);
        closed(&executor);
    }
}

#[test]
fn independent_instances_have_explicit_worker_ownership() {
    let first = EngineExecutor::new(Limits {
        workers: 2,
        ..limits()
    })
    .unwrap();
    let second = EngineExecutor::new(limits()).unwrap();
    closed(&first);
    assert_eq!(first.snapshot().workers_started, 2);
    assert!(!second.snapshot().closing && !second.snapshot().joined);
    closed(&second);
    assert_eq!(second.snapshot().workers_started, 1);
}

#[test]
fn waiter_wakeup_can_reenter_diagnostics_outside_the_admission_mutex() {
    struct Reenter {
        executor: std::sync::Weak<EngineExecutor>,
        calls: AtomicU64,
    }
    impl std::task::Wake for Reenter {
        fn wake(self: Arc<Self>) {
            self.wake_by_ref();
        }
        fn wake_by_ref(self: &Arc<Self>) {
            let executor = self.executor.upgrade().unwrap();
            assert_eq!(executor.snapshot().admitted, 1);
            self.calls.fetch_add(1, Ordering::AcqRel);
        }
    }
    let executor = EngineExecutor::new(Limits {
        admitted: 1,
        cleanup: 1,
        ..limits()
    })
    .unwrap();
    let issued = executor.try_acquire(8).unwrap();
    let wake = Arc::new(Reenter {
        executor: Arc::downgrade(&executor),
        calls: AtomicU64::new(0),
    });
    let waker = Waker::from(Arc::clone(&wake));
    let mut waiting = Box::pin(executor.acquire(8));
    assert!(
        waiting
            .as_mut()
            .poll(&mut Context::from_waker(&waker))
            .is_pending()
    );
    drop(issued);
    let next = ready(waiting.as_mut()).unwrap();
    assert_eq!(wake.calls.load(Ordering::Acquire), 1);
    drop(waiting);
    drop(next);
    closed(&executor);
}

#[test]
fn native_worker_requests_close_without_waiting_for_its_own_join() {
    let executor = EngineExecutor::new(limits()).unwrap();
    let issued = executor.try_acquire(1).unwrap();
    let native = Arc::clone(&executor);
    let (send, receive) = mpsc::channel();
    executor
        .spawn(&issued, async move { Ok(native.join()) }, move |value| {
            send.send(value.unwrap()).unwrap();
        })
        .unwrap();
    assert!(!receive.recv_timeout(Duration::from_secs(5)).unwrap());
    assert!(executor.snapshot().closing);
    settle(&executor);
    drop(issued);
    closed(&executor);
}
