use super::*;
use std::sync::atomic::{AtomicUsize, Ordering};

struct Context {
    subscription: Arc<Subscription>,
    calls: AtomicUsize,
    bad: AtomicUsize,
    reentrant_stop: bool,
}

unsafe extern "C" fn callback(cookie: usize, _: u32, _: *const std::ffi::c_void, _: u64) -> u32 {
    // SAFETY: tests keep the boxed context live until native retirement and joins.
    let context = unsafe { &*(cookie as *const Context) };
    context.calls.fetch_add(1, Ordering::SeqCst);
    if context.subscription.snapshot().0 == Phase::Retired {
        context.bad.fetch_add(1, Ordering::SeqCst);
    }
    if context.reentrant_stop {
        assert_eq!(context.subscription.request_stop(), NW_OK);
        assert_eq!(context.subscription.try_retire(), NW_PENDING);
    }
    NW_OK
}

fn context(stop: bool) -> Box<Context> {
    Box::new(Context {
        subscription: Arc::new(Subscription::new()),
        calls: AtomicUsize::new(0),
        bad: AtomicUsize::new(0),
        reentrant_stop: stop,
    })
}

fn table(context: &Context) -> NwCallbacksV1 {
    NwCallbacksV1 {
        struct_size: size_of::<NwCallbacksV1>() as u32,
        flags: 0,
        cookie: context as *const _ as usize,
        on_event: Some(callback),
    }
}

#[test]
fn start_failure_keeps_created_and_copies_table() {
    let context = context(false);
    let mut value = table(&context);
    value.flags = 1;
    assert_eq!(context.subscription.start(value), NW_INVALID_ARGUMENT);
    assert!(context.subscription.can_release());
    value.flags = 0;
    value.struct_size = 4;
    assert_eq!(context.subscription.start(value), NW_INVALID_ARGUMENT);
    value.struct_size = size_of::<NwCallbacksV1>() as u32;
    assert_eq!(context.subscription.start(value), NW_OK);
    value.cookie = 0;
    value.on_event = None;
    // SAFETY: original context and registered function remain live.
    assert_eq!(unsafe { context.subscription.emit(1, b"copied") }, NW_OK);
    assert_eq!(context.calls.load(Ordering::SeqCst), 1);
    assert_eq!(context.subscription.start(value), NW_INVALID_ARGUMENT);
    assert_eq!(context.subscription.request_stop(), NW_OK);
    assert_eq!(context.subscription.try_retire(), NW_OK);
    assert!(context.subscription.can_release());
}

#[test]
fn reentrant_stop_does_not_wait_for_own_callback() {
    let context = context(true);
    assert_eq!(context.subscription.start(table(&context)), NW_OK);
    // SAFETY: context remains live through the complete protocol.
    assert_eq!(unsafe { context.subscription.emit(1, b"reentry") }, NW_OK);
    assert_eq!(context.subscription.try_retire(), NW_OK);
    assert_eq!(context.subscription.request_stop(), NW_OK);
    assert_eq!(context.subscription.try_retire(), NW_OK);
    assert_eq!(
        // SAFETY: owner remains live; admission must reject without callback.
        unsafe { context.subscription.emit(1, b"late") },
        NW_INVALID_STATE
    );
    assert_eq!(context.calls.load(Ordering::SeqCst), 1);
}

#[test]
fn source_lease_is_separate_from_callback_quiet() {
    let context = context(false);
    assert_eq!(context.subscription.start(table(&context)), NW_OK);
    let source = context.subscription.reserve_source().unwrap();
    context.subscription.request_stop();
    assert_eq!(context.subscription.snapshot().1, 0);
    assert_eq!(context.subscription.try_retire(), NW_PENDING);
    assert!(!context.subscription.can_release());
    drop(source);
    assert_eq!(context.subscription.try_retire(), NW_OK);
    assert!(context.subscription.reserve_source().is_err());
}

#[test]
fn concurrent_stop_and_callback_admission_never_enter_after_retirement() {
    for _ in 0..32 {
        let context = context(false);
        let subscription = &context.subscription;
        assert_eq!(subscription.start(table(&context)), NW_OK);
        std::thread::scope(|scope| {
            for _ in 0..4 {
                scope.spawn(|| {
                    for _ in 0..128 {
                        // SAFETY: scoped joins precede context destruction.
                        let status = unsafe { subscription.emit(5, b"race") };
                        assert!(status == NW_OK || status == NW_INVALID_STATE);
                    }
                });
            }
            subscription.request_stop();
            let status = subscription.try_retire();
            assert!(status == NW_OK || status == NW_PENDING);
        });
        assert_eq!(subscription.try_retire(), NW_OK);
        assert_eq!(subscription.snapshot(), (Phase::Retired, 0, 0));
        assert_eq!(context.bad.load(Ordering::SeqCst), 0);
    }
}

unsafe extern "C" fn rejected(_: usize, _: u32, _: *const std::ffi::c_void, _: u64) -> u32 {
    nativeweave_interop_abi::NW_CALLBACK_FAILED
}

#[test]
fn callback_failure_closes_native_production() {
    let subscription = Subscription::new();
    assert_eq!(
        subscription.start(NwCallbacksV1 {
            struct_size: size_of::<NwCallbacksV1>() as u32,
            flags: 0,
            cookie: 1,
            on_event: Some(rejected),
        }),
        NW_OK
    );
    assert_eq!(
        // SAFETY: rejected ignores its opaque non-dereferenced cookie.
        unsafe { subscription.emit(0, &[]) },
        nativeweave_interop_abi::NW_CALLBACK_FAILED
    );
    assert_eq!(subscription.try_retire(), NW_OK);
}
