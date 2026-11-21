//! Targeted B17 ABI and actual-network retirement tests. No managed transport substitute.
use super::*;
use crate::{
    config::{Config, Listener, Route},
    engine::{Host, Job, OpState},
    exports::{NwServer, NwServerOperation},
    wire::*,
};
use std::{
    collections::HashSet,
    io::{Read, Write},
    net::{Ipv4Addr, SocketAddr, TcpStream},
    sync::{
        Arc, RwLock,
        atomic::{AtomicBool, AtomicU32, AtomicU64, Ordering},
    },
    time::{Duration, Instant},
};

#[derive(Default)]
struct Notifications {
    pause_terminal: AtomicBool,
    ticket: AtomicU64,
    cancel: AtomicU32,
    fault: AtomicU32,
    terminal: AtomicU32,
    code: AtomicU32,
}
unsafe extern "C" fn requested(cookie: usize, ticket: u64, _handler: u64, _kind: u32) -> u32 {
    // SAFETY: the test retains its boxed callback state until the host retirement gate.
    unsafe { &*(cookie as *const Notifications) }
        .ticket
        .store(ticket, Ordering::Release);
    NW_OK
}
unsafe extern "C" fn controlled(
    cookie: usize,
    kind: u32,
    status: u32,
    _ticket: u64,
    _error: *const NwErrorInfoV1,
) -> u32 {
    // SAFETY: the same boxed notification state outlives all native callback entries.
    let state = unsafe { &*(cookie as *const Notifications) };
    if kind == 1 {
        state.cancel.store(status, Ordering::Release);
    } else {
        state.fault.store(status, Ordering::Release);
    }
    NW_OK
}
unsafe extern "C" fn terminal(cookie: usize, status: u32, error: *const NwErrorInfoV1) -> u32 {
    // SAFETY: operation callback storage is retained until operation_try_retire; error is a fixed callback borrow.
    let state = unsafe { &*(cookie as *const Notifications) };
    if !error.is_null() {
        // SAFETY: a nonnull terminal diagnostic borrows a complete fixed record until this callback returns.
        let code = unsafe { (*error).code };
        state.code.store(code as u32, Ordering::Release);
    }
    state.terminal.store(status + 1, Ordering::Release);
    while state.pause_terminal.load(Ordering::Acquire) {
        std::thread::sleep(Duration::from_millis(1));
    }
    NW_OK
}
fn until(mut condition: impl FnMut() -> bool) {
    let end = Instant::now() + Duration::from_secs(5);
    while !condition() {
        assert!(
            Instant::now() < end,
            "native retirement/test condition timed out"
        );
        std::thread::sleep(Duration::from_millis(1));
    }
}
fn make(state: &Notifications) -> Arc<Host> {
    let config = Config {
        _request_timeout: Duration::from_millis(40),
        _shutdown_timeout: Duration::from_millis(40),
        header_timeout: Duration::from_secs(1),
        keepalive: Duration::from_secs(1),
        _max_request_bytes: 1024,
        _max_json_bytes: 64,
        max_header_bytes: 8192,
        max_chunk_bytes: 256,
        max_connections: 4,
        max_h2_streams: 4,
        active_requests: 2,
        queued_requests: 0,
        native_payload_bytes: 1024,
        managed_payload_bytes: 1024,
        request_retained_bytes: 1024,
        listeners: vec![Listener {
            name: "test".into(),
            address: SocketAddr::from((Ipv4Addr::LOCALHOST, 0)),
            protocols: 1,
            tls: RwLock::new(None),
        }],
        trusted_proxies: HashSet::new(),
        forward_limit: 1,
    };
    let host = Host::new(
        config,
        NwServerDispatchV1 {
            struct_size: 32,
            version: 1,
            cookie: state as *const _ as usize,
            on_request: requested as *const () as usize,
            on_control: controlled as *const () as usize,
        },
    )
    .expect("valid host");
    *lock(&host.routes) = Some(vec![Route {
        id: 1,
        kind: 0,
        canonical: "/test".into(),
        parameters: vec![],
        methods: vec![http::Method::GET],
        timeout: Duration::from_millis(40),
        max_request_bytes: 1024,
        max_json_bytes: 64,
        mcp: None,
        grpc: None,
    }]);
    host
}
fn start_op(host: &Arc<Host>, job: Job, state: &Notifications) -> *mut NwServerOperation {
    state.terminal.store(0, Ordering::Release);
    let op = OpState::new(host.clone(), None, job).expect("operation slot");
    let raw = Box::into_raw(Box::new(NwServerOperation { inner: op }));
    let callbacks = NwOperationCallbacksV1 {
        struct_size: 24,
        flags: 0,
        cookie: state as *const _ as usize,
        on_terminal: Some(terminal),
    };
    // SAFETY: live creator operation and callback table; caller retains state through retire.
    assert_eq!(
        // SAFETY: this test holds the live creator owner and truthful fixed storage through the synchronous ABI call.
        unsafe { nw_server_operation_start(raw, &callbacks, std::ptr::null_mut()) },
        NW_OK
    );
    until(|| state.terminal.load(Ordering::Acquire) != 0);
    raw
}
fn retire_op(op: *mut NwServerOperation) {
    // SAFETY: the unique raw owner is retained until quiescence, then released exactly once.
    until(|| unsafe { nw_server_operation_try_retire(op) } == NW_OK);
    // SAFETY: this test holds the live creator owner and truthful fixed storage through the synchronous ABI call.
    unsafe { nw_server_operation_release(op) };
}
fn close(host: &Arc<Host>, state: &Notifications) {
    let op = start_op(host, Job::Close(Duration::from_millis(100)), state);
    let mut result = NwServerCloseResultV1 {
        struct_size: 32,
        version: 1,
        close_status: 99,
        pending_requests: 99,
        retained_managed_bytes: 99,
        retained_native_bytes: 99,
    };
    // SAFETY: complete naturally aligned output record, live typed close operation.
    assert_eq!(
        // SAFETY: this test holds the live creator owner and truthful fixed storage through the synchronous ABI call.
        unsafe { nw_server_operation_take_close(op, &mut result, std::ptr::null_mut()) },
        NW_OK
    );
    assert_eq!(result.close_status, 0);
    retire_op(op);
    assert!(host.native_joined.load(Ordering::Acquire));
    assert_eq!(host.live_ops.load(Ordering::Acquire), 0);
}
#[test]
fn fixed_prefix_and_tail_are_exact() {
    #[repr(C)]
    struct Output {
        info: NwAbiInfoV1,
        tail: [u8; 16],
    }
    let mut output = Output {
        info: NwAbiInfoV1 {
            struct_size: 48,
            abi_major: 0,
            abi_minor: 0,
            pointer_width_bits: 0,
            capabilities: 0,
            reserved: 0,
        },
        tail: [0xa5; 16],
    };
    // SAFETY: output provides its truthful full allocation and naturally aligned known prefix.
    assert_eq!(
        // SAFETY: this test holds the live creator owner and truthful fixed storage through the synchronous ABI call.
        unsafe { nw_server_get_abi_v1(1, 48, &mut output.info) },
        NW_OK
    );
    assert_eq!(output.info.struct_size, 32);
    assert_eq!(output.tail, [0xa5; 16]);
    let mut small = [0xdeadbeefu64; 4];
    // SAFETY: capacity is truthful; no 32-byte output is written for this four-byte offered prefix.
    assert_eq!(
        // SAFETY: this test holds the live creator owner and truthful fixed storage through the synchronous ABI call.
        unsafe { nw_server_get_abi_v1(1, 4, small.as_mut_ptr().cast()) },
        NW_BUFFER_TOO_SMALL
    );
    assert_eq!(small, [0xdeadbeef; 4]);
}
#[test]
fn never_started_release_has_no_runtime_and_reclaims_slot() {
    let state = Box::<Notifications>::default();
    let host = make(&state);
    let op = OpState::new(host.clone(), None, Job::Start).unwrap();
    assert_eq!(host.live_ops.load(Ordering::Acquire), 1);
    let ptr = Box::into_raw(Box::new(NwServerOperation { inner: op }));
    // SAFETY: documented never-started path, unique creator owner.
    unsafe { nw_server_operation_release(ptr) };
    assert_eq!(host.live_ops.load(Ordering::Acquire), 0);
    assert!(lock(&host.runtime).is_none());
    assert!(lock(&host.worker).is_none());
}
#[test]
fn wrong_typed_take_does_not_consume_and_double_take_rejects() {
    let state = Box::<Notifications>::default();
    let host = make(&state);
    let op = start_op(&host, Job::Start, &state);
    let mut close_result = NwServerCloseResultV1 {
        struct_size: 32,
        version: 1,
        close_status: 99,
        pending_requests: 0,
        retained_managed_bytes: 0,
        retained_native_bytes: 0,
    };
    // SAFETY: leased live operation, correctly initialized output; wrong-kind take intentionally exercises the rejecting gate.
    unsafe {
        assert_eq!(
            nw_server_operation_take_close(op, &mut close_result, std::ptr::null_mut()),
            NW_INVALID_STATE
        );
        assert_eq!(
            nw_server_operation_take_void(op, std::ptr::null_mut()),
            NW_OK
        );
        assert_eq!(
            nw_server_operation_take_void(op, std::ptr::null_mut()),
            NW_INVALID_STATE
        );
    }
    assert_eq!(close_result.close_status, 99);
    retire_op(op);
    close(&host, &state);
}
#[test]
fn actual_network_sticky_cancellation_before_take_and_control_retirement() {
    let state = Box::<Notifications>::default();
    let host = make(&state);
    let op = start_op(&host, Job::Start, &state);
    retire_op(op);
    let address = lock(&host.addresses)[0]
        .1
        .trim_start_matches("http://")
        .trim_end_matches('/')
        .to_owned();
    let mut peer = TcpStream::connect(address).unwrap();
    peer.write_all(b"GET /test HTTP/1.1\r\nHost: localhost\r\n\r\n")
        .unwrap();
    until(|| state.ticket.load(Ordering::Acquire) != 0);
    until(|| state.cancel.load(Ordering::Acquire) == NW_TIMEOUT);
    let server = Box::into_raw(Box::new(NwServer {
        inner: host.clone(),
    }));
    let (mut request, mut metadata) = (std::ptr::null_mut(), std::ptr::null_mut());
    // SAFETY: ticket belongs to this retained host; output owners transfer together exactly once.
    unsafe {
        assert_eq!(
            nw_server_request_take(
                server,
                state.ticket.load(Ordering::Acquire),
                &mut request,
                &mut metadata,
                std::ptr::null_mut()
            ),
            NW_OK
        );
        let mut info: NwServerRequestInfoV1 = std::mem::zeroed();
        info.struct_size = size_of::<NwServerRequestInfoV1>() as u32;
        assert_eq!(
            nw_server_metadata_read(metadata, &mut info, std::ptr::null_mut()),
            NW_OK
        );
        assert_eq!(info.cancellation_status, NW_TIMEOUT);
        assert_eq!(nw_server_request_try_retire(request), NW_PENDING);
        assert_eq!(
            nw_server_dispatch_complete(request, NW_TIMEOUT, std::ptr::null_mut()),
            NW_OK
        );
        until(|| nw_server_request_try_retire(request) == NW_OK);
        nw_server_request_release(request);
        nw_server_metadata_release(metadata);
    }
    drop(peer);
    until(|| lock(&host.requests).is_empty());
    close(&host, &state);
    // SAFETY: successful native join and no request/operation/callback/byte owners remain.
    unsafe {
        assert_eq!(nw_server_try_retire(server), NW_OK);
        nw_server_release(server)
    };
}
#[test]
fn completed_acceptor_outcomes_preserve_stop_and_fault_semantics() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .build()
        .unwrap();
    for (stopping, panic, expected_state, expected_fault) in [
        (true, false, 4, NW_OK),
        (false, false, 5, NW_DOMAIN_ERROR),
        (true, true, 5, NW_DOMAIN_ERROR),
        (false, true, 5, NW_DOMAIN_ERROR),
    ] {
        let state = Box::<Notifications>::default();
        let host = make(&state);
        if stopping {
            host.initiate_close();
        }
        let joined = runtime.block_on(async {
            let mut tasks = tokio::task::JoinSet::new();
            tasks.spawn(async move {
                assert!(!panic, "deliberate acceptor panic");
            });
            tasks
                .join_next()
                .await
                .expect("one real completed acceptor")
        });
        // Exercise the selected completion branch with a real Tokio outcome;
        // the simultaneously-ready stop branch must not hide this ordering.
        host.acceptor_finished(joined);
        assert_eq!(host.state.load(Ordering::Acquire), expected_state);
        assert_eq!(state.fault.load(Ordering::Acquire), expected_fault);
        assert_eq!(host.callbacks.load(Ordering::Acquire), 0);
    }
}
#[test]
fn actual_network_normal_shutdown_keeps_success_state_under_acceptor_race() {
    for iteration in 0..64 {
        let state = Box::<Notifications>::default();
        let host = make(&state);
        let op = start_op(&host, Job::Start, &state);
        retire_op(op);
        let address = lock(&host.addresses)[0]
            .1
            .trim_start_matches("http://")
            .trim_end_matches('/')
            .to_owned();
        let peer = TcpStream::connect(address).unwrap();
        // The stop notification and the acceptor's successful completion may
        // both be ready when the real native host polls its supervisory select.
        host.initiate_close();
        drop(peer);
        until(|| host.worker_exited.load(Ordering::Acquire));
        close(&host, &state);
        assert_eq!(
            state.fault.load(Ordering::Acquire),
            0,
            "iteration {iteration}"
        );
        assert_eq!(
            host.state.load(Ordering::Acquire),
            4,
            "iteration {iteration}"
        );
        assert!(lock(&host.requests).is_empty());
        assert_eq!(host.budget.snapshot(), (0, 0));
        assert_eq!(host.callbacks.load(Ordering::Acquire), 0);
    }
}

fn late_finish_after_network_join(payload: &[u8]) {
    use crate::engine::{OpResult, ShutdownTailGate, SideGuard};
    let state = Box::<Notifications>::default();
    let host = make(&state);
    // The test controls close ordering, not the unrelated 40 ms route deadline.
    lock(&host.routes).as_mut().unwrap()[0].timeout = Duration::from_secs(20);
    let operation = start_op(&host, Job::Start, &state);
    retire_op(operation);
    let address = lock(&host.addresses)[0]
        .1
        .trim_start_matches("http://")
        .trim_end_matches('/')
        .to_owned();
    let mut peer = TcpStream::connect(address).unwrap();
    peer.set_read_timeout(Some(Duration::from_secs(5))).unwrap();
    peer.write_all(b"GET /test HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n")
        .unwrap();
    until(|| state.ticket.load(Ordering::Acquire) != 0);
    let server = Box::into_raw(Box::new(NwServer {
        inner: host.clone(),
    }));
    let (mut raw_request, mut metadata) = (std::ptr::null_mut(), std::ptr::null_mut());
    // SAFETY: the host and callback state remain live; both transferred owners
    // are retained until dispatch, operation and native retirement below.
    unsafe {
        assert_eq!(
            nw_server_request_take(
                server,
                state.ticket.load(Ordering::Acquire),
                &mut raw_request,
                &mut metadata,
                std::ptr::null_mut()
            ),
            NW_OK
        );
    }
    // SAFETY: raw_request is the transferred, live creator-owned request.
    let request = unsafe { &*raw_request }.inner.clone();
    let submit = |job| {
        state.terminal.store(0, Ordering::Release);
        let operation = OpState::new(host.clone(), Some(request.clone()), job).unwrap();
        let raw = Box::into_raw(Box::new(NwServerOperation { inner: operation }));
        let callbacks = NwOperationCallbacksV1 {
            struct_size: 24,
            flags: 0,
            cookie: &*state as *const _ as usize,
            on_terminal: Some(terminal),
        };
        // SAFETY: live operation plus callback state retained through retire;
        // submission alone deliberately does not wait for terminal delivery.
        assert_eq!(
            // SAFETY: the callback root and live operation outlive this start.
            unsafe { nw_server_operation_start(raw, &callbacks, std::ptr::null_mut()) },
            NW_OK
        );
        raw
    };
    let wait_ok = |raw| {
        until(|| state.terminal.load(Ordering::Acquire) != 0);
        assert_eq!(state.terminal.load(Ordering::Acquire), NW_OK + 1);
        raw
    };
    let mut headers = http::HeaderMap::new();
    headers.insert(http::header::CONTENT_LENGTH, payload.len().into());
    let begin = wait_ok(submit(Job::Begin(
        request.clone(),
        200,
        headers,
        SideGuard::acquire(&request.write_active).unwrap(),
    )));
    retire_op(begin);
    if !payload.is_empty() {
        let reserved = wait_ok(submit(Job::Reserve(
            request.clone(),
            0,
            payload.len(),
            SideGuard::acquire(&request.write_active).unwrap(),
        )));
        // SAFETY: completed live operation; this is the one typed result take.
        let OpResult::Reservation(reservation) =
            unsafe { &*reserved }.inner.operation.take_result().unwrap()
        else {
            panic!("body write reservation required");
        };
        retire_op(reserved);
        let crate::engine::grpc::WriteToken::Body(token) = reservation.reservation else {
            panic!("HTTP body token required");
        };
        let write = wait_ok(submit(Job::Write(
            request.clone(),
            token.prepare(payload).unwrap(),
            payload.len(),
            reservation.side,
        )));
        retire_op(write);
    }
    let mut response = Vec::new();
    peer.read_to_end(&mut response).unwrap();
    let split = response
        .windows(4)
        .position(|part| part == b"\r\n\r\n")
        .unwrap()
        + 4;
    assert!(response.starts_with(b"HTTP/1.1 200"));
    assert_eq!(&response[split..], payload);
    until(|| request.body_done.load(Ordering::Acquire));
    assert!(!request.dispatch_done.load(Ordering::Acquire));
    assert_eq!(request.head.cancellation.load(Ordering::Acquire), 0);

    let (ready_tx, ready_rx) = std::sync::mpsc::sync_channel(1);
    let (release_tx, release_rx) = std::sync::mpsc::sync_channel(1);
    *lock(&host.shutdown_tail_gate) = Some(ShutdownTailGate {
        ready: ready_tx,
        release: release_rx,
    });
    host.initiate_close();
    ready_rx.recv_timeout(Duration::from_secs(5)).unwrap();
    // The current-thread runtime is held immediately after its network joins.
    // Queue Finish before releasing it. Dropping the runtime here loses the
    // operation's terminal; the runtime must keep polling request tails.
    state.terminal.store(0, Ordering::Release);
    let mut finish = std::ptr::null_mut();
    let callbacks = NwOperationCallbacksV1 {
        struct_size: 24,
        flags: 0,
        cookie: &*state as *const _ as usize,
        on_terminal: Some(terminal),
    };
    let empty_trailers = [0u8; 4];
    // SAFETY: live taken request, empty synchronous trailer view, initialized
    // owner output and callback root retained through operation retirement.
    unsafe {
        assert_eq!(
            nw_server_response_finish_create(
                raw_request,
                NwBytesViewV1 {
                    data: empty_trailers.as_ptr(),
                    length: empty_trailers.len() as u64
                },
                &mut finish,
                std::ptr::null_mut()
            ),
            NW_OK
        );
        assert_eq!(
            nw_server_operation_start(finish, &callbacks, std::ptr::null_mut()),
            NW_OK
        );
    }
    assert_eq!(state.terminal.load(Ordering::Acquire), 0);
    release_tx.send(()).unwrap();
    until(|| {
        state.terminal.load(Ordering::Acquire) != 0 || host.worker_exited.load(Ordering::Acquire)
    });
    let observed_terminal = state.terminal.load(Ordering::Acquire);
    let observed_worker_exited = host.worker_exited.load(Ordering::Acquire);
    if observed_terminal == 0 {
        assert!(observed_worker_exited);
        // This branch is test-only failure cleanup. The worker has exited, so
        // no runtime job can publish the lost terminal. Preserve its absence
        // above, then complete the owned operation so the failing regression
        // can still release every callback/request owner before asserting.
        // SAFETY: the live creator-owned operation and callback state remain
        // retained; worker exit proves there is no executing native publisher.
        unsafe { &*finish }
            .inner
            .complete(Err(ApiError::status(NW_CANCELED)));
    } else if observed_terminal == NW_OK + 1 {
        // SAFETY: the retained Finish owner published one successful void result.
        assert_eq!(
            // SAFETY: live completed typed operation and fixed optional error.
            unsafe { nw_server_operation_take_void(finish, std::ptr::null_mut()) },
            NW_OK
        );
    }
    retire_op(finish);
    let observed_cancellation = request.head.cancellation.load(Ordering::Acquire);
    // SAFETY: completed dispatch and no active operation; creator owners are
    // retired and released exactly once before joining the native worker.
    unsafe {
        assert_eq!(
            nw_server_dispatch_complete(raw_request, NW_OK, std::ptr::null_mut()),
            NW_OK
        );
        until(|| nw_server_request_try_retire(raw_request) == NW_OK);
        nw_server_request_release(raw_request);
        nw_server_metadata_release(metadata);
    }
    drop(request);
    drop(peer);
    close(&host, &state);
    assert!(lock(&host.requests).is_empty());
    assert_eq!(host.budget.snapshot(), (0, 0));
    // SAFETY: worker, callbacks, operations, requests and byte owners joined.
    unsafe {
        assert_eq!(nw_server_try_retire(server), NW_OK);
        nw_server_release(server);
    }
    assert_eq!(
        observed_terminal,
        NW_OK + 1,
        "closing runtime lost a Finish operation accepted after the network joined; worker_exited={observed_worker_exited}"
    );
    assert_eq!(observed_cancellation, 0);
}

#[test]
fn actual_network_empty_body_finish_survives_close_after_network_join() {
    late_finish_after_network_join(b"");
}

#[test]
fn actual_network_fixed_body_finish_survives_close_after_network_join() {
    late_finish_after_network_join(b"body");
}
#[test]
fn actual_network_host_fault_signals_and_joins_without_unload() {
    let state = Box::<Notifications>::default();
    let host = make(&state);
    let op = start_op(&host, Job::Start, &state);
    retire_op(op);
    let address = lock(&host.addresses)[0]
        .1
        .trim_start_matches("http://")
        .trim_end_matches('/')
        .to_owned();
    let peer = TcpStream::connect(address).unwrap();
    host.fault(NW_DOMAIN_ERROR);
    until(|| state.fault.load(Ordering::Acquire) == NW_DOMAIN_ERROR);
    drop(peer);
    close(&host, &state);
    assert_eq!(host.state.load(Ordering::Acquire), 5);
}
#[test]
fn strict_metadata_and_native_protocol_gate_reject_before_dispatch() {
    assert!(config::decode(&[0, 0, 0, 0, 1], 200, 256, 8192).is_err());
    assert!(config::headers(&config::encode([("connection", "close")]), 8192, false).is_err());
    assert!(config::headers(&config::encode([("content-length", "1")]), 8192, true).is_err());
    let state = Box::<Notifications>::default();
    let host = make(&state);
    let request = http::Request::builder()
        .uri("/test?q=%zz")
        .header("host", "localhost")
        .body(())
        .unwrap();
    let route = lock(&host.routes).as_ref().unwrap()[0].clone();
    assert!(
        crate::metadata::head(
            &request,
            &route,
            &host.config,
            1,
            SocketAddr::from((Ipv4Addr::LOCALHOST, 1234)),
            false,
            vec![],
            Instant::now() + Duration::from_secs(1)
        )
        .is_err()
    );
    assert_eq!(state.ticket.load(Ordering::Acquire), 0);
}

#[test]
fn terminal_before_start_return_retains_callback_and_source_until_release() {
    let state = Box::<Notifications>::default();
    state.pause_terminal.store(true, Ordering::Release);
    let host = make(&state);
    let operation = OpState::new(host.clone(), None, Job::Start).unwrap();
    let raw = Box::into_raw(Box::new(NwServerOperation { inner: operation }));
    let callbacks = NwOperationCallbacksV1 {
        struct_size: 24,
        flags: 0,
        cookie: &*state as *const _ as usize,
        on_terminal: Some(terminal),
    };
    // Hold publication of the native worker handle so the completion is forced
    // to overlap the synchronous start entry, rather than relying on timing.
    let worker_publication = lock(&host.worker);
    let address = raw as usize;
    let returned = Arc::new(AtomicBool::new(false));
    let observed = returned.clone();
    let entry = std::thread::spawn(move || {
        // SAFETY: caller retains the raw operation and boxed root until this start entry returns and later retirement succeeds.
        let status = unsafe {
            nw_server_operation_start(
                address as *mut NwServerOperation,
                &callbacks,
                std::ptr::null_mut(),
            )
        };
        observed.store(true, Ordering::Release);
        status
    });
    until(|| state.terminal.load(Ordering::Acquire) != 0);
    assert!(!returned.load(Ordering::Acquire));
    // SAFETY: live operation owner; the blocked callback prevents retirement even after terminal publication.
    unsafe {
        assert_eq!(nw_server_operation_try_retire(raw), NW_PENDING);
        assert_eq!(
            nw_server_operation_take_void(raw, std::ptr::null_mut()),
            NW_OK
        );
    }
    state.pause_terminal.store(false, Ordering::Release);
    drop(worker_publication);
    assert_eq!(entry.join().unwrap(), NW_OK);
    retire_op(raw);
    close(&host, &state);
}
#[test]
fn cancel_before_start_copies_status_and_still_joins_worker() {
    let state = Box::<Notifications>::default();
    let host = make(&state);
    let operation = OpState::new(host.clone(), None, Job::Start).unwrap();
    let raw = Box::into_raw(Box::new(NwServerOperation { inner: operation }));
    let callbacks = NwOperationCallbacksV1 {
        struct_size: 24,
        flags: 0,
        cookie: &*state as *const _ as usize,
        on_terminal: Some(terminal),
    };
    // SAFETY: cooperative pre-start cancellation is part of A07; callback storage remains rooted through retire.
    unsafe {
        assert_eq!(nw_server_operation_request_cancel(raw), NW_OK);
        assert_eq!(
            nw_server_operation_start(raw, &callbacks, std::ptr::null_mut()),
            NW_OK
        );
    }
    until(|| state.terminal.load(Ordering::Acquire) != 0);
    assert_eq!(state.terminal.load(Ordering::Acquire), NW_CANCELED + 1);
    assert!(lock(&host.addresses).is_empty());
    retire_op(raw);
    close(&host, &state);
}

#[path = "../../../../tests/Server.Diagnostics/native_host.rs"]
mod diagnostics;
