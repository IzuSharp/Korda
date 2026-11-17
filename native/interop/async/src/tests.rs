use super::*;
use nativeweave_interop_abi::{NW_CALLBACK_FAILED, NW_CANCELED};
use std::sync::atomic::{AtomicUsize, Ordering};

unsafe extern "C" fn notify(cookie: usize, _: u32, _: *const NwErrorInfoV1) -> u32 {
    // SAFETY: tests keep their AtomicUsize cookie alive through retirement.
    unsafe { &*(cookie as *const AtomicUsize) }.fetch_add(1, Ordering::SeqCst);
    NW_OK
}
fn table(counter: &AtomicUsize) -> NwOperationCallbacksV1 {
    NwOperationCallbacksV1 {
        struct_size: size_of::<NwOperationCallbacksV1>() as u32,
        flags: 0,
        cookie: counter as *const _ as usize,
        on_terminal: Some(notify),
    }
}

#[test]
fn cancellation_does_not_choose_terminal_and_retired_result_can_be_taken_once() {
    let counter = AtomicUsize::new(0);
    let operation = Operation::new();
    assert_eq!(operation.request_cancel(), NW_OK);
    assert_eq!(operation.start(table(&counter)), NW_OK);
    assert!(operation.cancel_requested());
    assert_eq!(
        // SAFETY: live counter and exact callback ABI.
        unsafe { operation.complete(NW_OK, Some(42), ErrorFields::default()) },
        NW_OK
    );
    assert_eq!(counter.load(Ordering::SeqCst), 1);
    assert_eq!(operation.try_retire(), NW_OK);
    assert_eq!(operation.take_result(), Ok(42));
    assert_eq!(operation.take_result(), Err(NW_INVALID_STATE));
    assert_eq!(
        // SAFETY: owner still live; rejected duplicate cannot invoke callback.
        unsafe { operation.complete(NW_CANCELED, None, ErrorFields::default()) },
        NW_INVALID_STATE
    );
    assert_eq!(counter.load(Ordering::SeqCst), 1);
}

unsafe extern "C" fn reject(_: usize, _: u32, _: *const NwErrorInfoV1) -> u32 {
    NW_CALLBACK_FAILED
}
#[test]
fn callback_failure_keeps_native_success_and_untaken_owner() {
    let operation = Operation::new();
    assert_eq!(
        operation.start(NwOperationCallbacksV1 {
            struct_size: size_of::<NwOperationCallbacksV1>() as u32,
            flags: 0,
            cookie: 1,
            on_terminal: Some(reject)
        }),
        NW_OK
    );
    assert_eq!(
        // SAFETY: rejecting callback ignores its opaque cookie.
        unsafe { operation.complete(NW_OK, Some(vec![1, 2]), ErrorFields::default()) },
        NW_OK
    );
    assert_eq!(operation.snapshot().1, Some(NW_OK));
    assert!(operation.snapshot().5);
    assert_eq!(operation.try_retire(), NW_OK);
    assert_eq!(operation.take_result(), Ok(vec![1, 2]));
}

#[test]
fn native_source_retirement_is_not_terminal_completion() {
    let counter = AtomicUsize::new(0);
    let operation = Arc::new(Operation::new());
    operation.start(table(&counter));
    let source = operation.reserve_source().unwrap();
    // SAFETY: counter stays live through source/operation retirement.
    unsafe { operation.complete(NW_OK, Some(7), ErrorFields::default()) };
    assert_eq!(operation.take_result(), Ok(7));
    assert_eq!(operation.try_retire(), NW_PENDING);
    assert!(!operation.can_release());
    drop(source);
    assert_eq!(operation.try_retire(), NW_OK);
}

#[test]
fn concurrent_terminal_commit_publishes_one_notice_and_one_result() {
    for _ in 0..32 {
        let counter = AtomicUsize::new(0);
        let operation = Operation::new();
        operation.start(table(&counter));
        std::thread::scope(|scope| {
            for value in 0..4 {
                let operation = &operation;
                scope.spawn(move || {
                    // SAFETY: scoped joins precede counter destruction.
                    let status =
                        unsafe { operation.complete(NW_OK, Some(value), ErrorFields::default()) };
                    assert!(status == NW_OK || status == NW_INVALID_STATE);
                });
            }
        });
        assert_eq!(counter.load(Ordering::SeqCst), 1);
        assert_eq!(operation.try_retire(), NW_OK);
        assert!(operation.take_result().is_ok());
        assert_eq!(operation.take_result(), Err(NW_INVALID_STATE));
    }
}
