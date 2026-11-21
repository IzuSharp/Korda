//! Exact native Core/Server owner tests; real HTTP2/TLS consumers are B18's managed suite.
use super::*;
use crate::exports::*;
use http_body_util::{BodyExt, StreamBody};
use nativeweave_protocols_grpc::MethodKind;
use std::{collections::HashSet, net::Ipv4Addr, sync::RwLock};

unsafe extern "C" fn notify(_cookie: usize, _ticket: u64, _handler: u64, _kind: u32) -> u32 {
    NW_OK
}
unsafe extern "C" fn control(
    _cookie: usize,
    _kind: u32,
    _status: u32,
    _ticket: u64,
    _error: *const NwErrorInfoV1,
) -> u32 {
    NW_OK
}
unsafe extern "C" fn terminal(cookie: usize, status: u32, _error: *const NwErrorInfoV1) -> u32 {
    // SAFETY: each test retains the boxed atomic until Core callback retirement.
    unsafe { &*(cookie as *const AtomicU32) }.store(status + 1, Ordering::Release);
    NW_OK
}
fn fixture() -> (Arc<Host>, Arc<RequestState>, *mut NwServerRequest) {
    let host = Host::new(
        Config {
            _request_timeout: Duration::from_secs(30),
            _shutdown_timeout: Duration::from_secs(1),
            header_timeout: Duration::from_secs(1),
            keepalive: Duration::from_secs(1),
            _max_request_bytes: 1024,
            _max_json_bytes: 64,
            max_header_bytes: 8192,
            max_chunk_bytes: 64,
            max_connections: 1,
            max_h2_streams: 2,
            active_requests: 2,
            queued_requests: 0,
            native_payload_bytes: 8_388_608,
            managed_payload_bytes: 8_388_608,
            request_retained_bytes: 4_194_304,
            listeners: vec![crate::config::Listener {
                name: "native-test".into(),
                address: (Ipv4Addr::LOCALHOST, 0).into(),
                protocols: 2,
                tls: RwLock::new(None),
            }],
            trusted_proxies: HashSet::new(),
            forward_limit: 1,
        },
        NwServerDispatchV1 {
            struct_size: 32,
            version: 1,
            cookie: 1,
            on_request: notify as *const () as usize,
            on_control: control as *const () as usize,
        },
    )
    .unwrap();
    *lock(&host.runtime) = Some(Handle::current());
    host.state.store(2, Ordering::Release);
    let descriptor = Arc::new(protocol::Descriptor {
        version: 1,
        kind: "grpc".into(),
        handler_id: 1,
        method: "/test.Call/Run".into(),
        method_kind: MethodKind::Duplex,
        request_codec_id: 1,
        response_codec_id: 2,
        max_encoded_message_bytes: 1024,
        max_decoded_message_bytes: 1024,
        max_stream_bytes: 65_536,
        timeout_ns: 30_000_000_000,
        compression: "identity".into(),
    });
    let route = Route {
        id: 1,
        kind: 1,
        canonical: descriptor.method.clone(),
        parameters: vec![],
        methods: vec![Method::POST],
        timeout: Duration::from_secs(30),
        max_request_bytes: 65_536,
        max_json_bytes: 0,
        mcp: None,
        grpc: Some(descriptor.clone()),
    };
    let input = Request::builder()
        .version(http::Version::HTTP_2)
        .method("POST")
        .uri("http://localhost/test.Call/Run")
        .header("content-type", "application/grpc")
        .body(())
        .unwrap();
    let budget = host.budget.request(4_194_304).unwrap();
    let projection = protocol::project_metadata(input.headers(), 8192, &budget).unwrap();
    let mut head = metadata::head(
        &input,
        &route,
        &host.config,
        1,
        (Ipv4Addr::LOCALHOST, 1234).into(),
        false,
        vec![],
        Instant::now() + Duration::from_secs(30),
    )
    .unwrap();
    Arc::get_mut(&mut head).unwrap().grpc = Some(projection);
    let call = Call::new(descriptor, budget.clone());
    let (sender, unused) = body_channel(budget.clone(), 64, 1).unwrap();
    drop(unused);
    let request = Arc::new(RequestState {
        diagnostics: host
            .diagnostics
            .request(&route.canonical, "gRPC", Default::default()),
        body_limit: Arc::new(AtomicBool::new(false)),
        host: Arc::downgrade(&host),
        head,
        reader: tokio::sync::Mutex::new(None),
        sender,
        budget,
        response: Mutex::new(ResponseState {
            phase: 0,
            status: 200,
            length: None,
            written: 0,
            sse: false,
            head: None,
        }),
        cancel: Cancellation::default(),
        input_cancel: Cancellation::default(),
        output_waker: AtomicWaker::new(),
        finished: Arc::default(),
        dispatch_done: AtomicBool::new(false),
        taken: AtomicBool::new(true),
        retired: AtomicBool::new(false),
        body_done: AtomicBool::new(false),
        connection_done: AtomicBool::new(false),
        read_active: Arc::new(AtomicBool::new(false)),
        write_active: Arc::new(AtomicBool::new(false)),
        live_ops: AtomicUsize::new(0),
        controls: Mutex::new(ControlGate {
            open: true,
            active: 0,
        }),
        _active: host.active.clone().try_acquire_owned().unwrap(),
        life: Arc::new(ConnectionLife {
            active: AtomicUsize::new(1),
            last: Mutex::new(Instant::now()),
            changed: Notify::new(),
        }),
        mcp: None,
        grpc: Some(call),
    });
    lock(&host.requests).insert(1, request.clone());
    let pointer = Box::into_raw(Box::new(NwServerRequest {
        inner: request.clone(),
    }));
    (host, request, pointer)
}
async fn run(pointer: *mut NwServerOperation) -> u32 {
    let state = Box::new(AtomicU32::new(0));
    let callbacks = NwOperationCallbacksV1 {
        struct_size: 24,
        flags: 0,
        cookie: &*state as *const AtomicU32 as usize,
        on_terminal: Some(terminal),
    };
    // SAFETY: creator operation, fixed callbacks and boxed state remain live until retirement below.
    assert_eq!(
        // SAFETY: pointer and callback cookie are retained until run observes successful Core retirement.
        unsafe { nw_server_operation_start(pointer, &callbacks, std::ptr::null_mut()) },
        NW_OK
    );
    tokio::time::timeout(Duration::from_secs(2), async {
        while state.load(Ordering::Acquire) == 0 {
            tokio::task::yield_now().await;
        }
    })
    .await
    .unwrap();
    // SAFETY: the exact started operation remains owned; callback/source quiescence is checked before returning and dropping cookie storage.
    while unsafe { nw_server_operation_try_retire(pointer) } != NW_OK {
        tokio::task::yield_now().await;
    }
    state.load(Ordering::Acquire) - 1
}
async fn cleanup(host: Arc<Host>, request: Arc<RequestState>, pointer: *mut NwServerRequest) {
    request.body_ended();
    // SAFETY: live request owner is completed once, retires every spawned input cleanup lease, then releases exactly once.
    unsafe {
        assert_eq!(
            nw_server_dispatch_complete(pointer, NW_OK, std::ptr::null_mut()),
            NW_OK
        );
    }
    // SAFETY: query remains within the retained request owner lifetime.
    while unsafe { nw_server_request_try_retire(pointer) } != NW_OK {
        tokio::task::yield_now().await;
    }
    // SAFETY: successful request retirement above transfers its unique raw creator allocation to release.
    unsafe { nw_server_request_release(pointer) };
    host.reap();
    assert!(lock(&host.requests).is_empty());
    drop(request);
    assert_eq!(host.budget.snapshot(), (0, 0));
}
#[tokio::test]
async fn grpc_created_cancel_stops_ready_input_and_preserves_output() {
    let (host, request, pointer) = fixture();
    let call = request.grpc.as_ref().unwrap();
    let bytes = request
        .budget
        .reserve_payload(5)
        .unwrap()
        .copy(b"ready")
        .unwrap()
        .into_bytes();
    call.set_input(protocol::Inbound::One(Some(bytes))).await;
    let mut operation = std::ptr::null_mut();
    // SAFETY: live request and exclusive output; cancellation is intentionally requested before Core start.
    unsafe {
        assert_eq!(
            nw_server_grpc_read_create(pointer, &mut operation, std::ptr::null_mut()),
            NW_OK
        );
        assert_eq!(nw_server_operation_request_cancel(operation), NW_OK);
    }
    assert_eq!(run(operation).await, NW_CANCELED);
    // SAFETY: run joined Core retirement, so this unique operation allocation can be released.
    unsafe { nw_server_operation_release(operation) };
    assert!(!request.cancel.is_canceled());
    assert_eq!(
        call.read().await.err().unwrap().code(),
        protocol::tonic::Code::Cancelled
    );
    // SAFETY: raw-body admission must reject the gRPC request without creating an operation.
    unsafe {
        assert_eq!(
            nw_server_body_read_create(pointer, 1, &mut operation, std::ptr::null_mut()),
            NW_INVALID_STATE
        );
        assert!(operation.is_null());
        assert_eq!(
            nw_server_write_reserve_create(pointer, 0, 1, &mut operation, std::ptr::null_mut()),
            NW_INVALID_STATE
        );
        assert!(operation.is_null());
        let empty = NwBytesViewV1 {
            data: std::ptr::null(),
            length: 0,
        };
        assert_eq!(
            nw_server_response_begin_create(
                pointer,
                200,
                empty,
                &mut operation,
                std::ptr::null_mut()
            ),
            NW_INVALID_STATE
        );
        assert!(operation.is_null());
        assert_eq!(
            nw_server_response_flush_create(pointer, &mut operation, std::ptr::null_mut()),
            NW_INVALID_STATE
        );
        assert!(operation.is_null());
        assert_eq!(
            nw_server_response_finish_create(pointer, empty, &mut operation, std::ptr::null_mut()),
            NW_INVALID_STATE
        );
        assert!(operation.is_null());
        assert_eq!(
            nw_server_response_write_create(
                pointer,
                std::ptr::null_mut(),
                empty,
                &mut operation,
                std::ptr::null_mut()
            ),
            NW_INVALID_STATE
        );
        assert!(operation.is_null());
        assert_eq!(
            nw_server_sse_write_create(
                pointer,
                std::ptr::null_mut(),
                std::ptr::null(),
                &mut operation,
                std::ptr::null_mut()
            ),
            NW_INVALID_STATE
        );
        assert!(operation.is_null());
        assert_eq!(lock(&request.response).phase, 0);
        assert!(!request.write_active.load(Ordering::Acquire));
        assert_eq!(
            nw_server_write_reserve_create(pointer, 1, 1024, &mut operation, std::ptr::null_mut()),
            NW_OK
        );
    }
    assert_eq!(run(operation).await, NW_OK);
    let mut token = std::ptr::null_mut();
    // SAFETY: successful reserve typed take transfers one token; operation then releases after its gate.
    unsafe {
        assert_eq!(
            nw_server_operation_take_write_reservation(operation, &mut token, std::ptr::null_mut()),
            NW_OK
        );
        nw_server_operation_release(operation);
        assert_eq!(
            nw_server_grpc_write_create(
                pointer,
                token,
                NwBytesViewV1 {
                    data: std::ptr::null(),
                    length: 0
                },
                &mut operation,
                std::ptr::null_mut()
            ),
            NW_OK
        );
    }
    assert_eq!(run(operation).await, NW_OK);
    // SAFETY: void result is consumed once and its quiescent operation released.
    unsafe {
        assert_eq!(
            nw_server_operation_take_void(operation, std::ptr::null_mut()),
            NW_OK
        );
        nw_server_operation_release(operation)
    };
    cleanup(host, request, pointer).await;
}
#[tokio::test]
async fn grpc_cleanup_cancel_preserves_success_and_explicit_created_cancel_stops_next_read() {
    let (host, request, pointer) = fixture();
    let call = request.grpc.as_ref().unwrap();
    let bytes = request
        .budget
        .reserve_payload(5)
        .unwrap()
        .copy(b"ready")
        .unwrap()
        .into_bytes();
    call.set_input(protocol::Inbound::One(Some(bytes))).await;
    let mut operation = std::ptr::null_mut();
    // SAFETY: one complete-message operation is created on the retained request.
    unsafe {
        assert_eq!(
            nw_server_grpc_read_create(pointer, &mut operation, std::ptr::null_mut()),
            NW_OK
        );
    }
    assert_eq!(run(operation).await, NW_OK);
    let (mut kind, mut buffer, mut trailers) = (99, std::ptr::null_mut(), std::ptr::null_mut());
    // SAFETY: A07 sends cancellation during normal terminal cleanup. It must not stop the next read or revoke a valid typed result.
    unsafe {
        assert_eq!(nw_server_operation_request_cancel(operation), NW_OK);
        assert_eq!(
            nw_server_operation_take_chunk(
                operation,
                &mut kind,
                &mut buffer,
                &mut trailers,
                std::ptr::null_mut()
            ),
            NW_OK
        );
        nw_server_operation_release(operation);
    }
    assert_eq!(kind, 0);
    assert!(trailers.is_null());
    assert!(call.read().await.unwrap().is_none());
    assert!(!request.cancel.is_canceled());
    // A dedicated Created-canceled operation expresses deterministic unread stop.
    // SAFETY: the new read owner is explicitly canceled before its first start.
    unsafe {
        assert_eq!(
            nw_server_grpc_read_create(pointer, &mut operation, std::ptr::null_mut()),
            NW_OK
        );
        assert_eq!(nw_server_operation_request_cancel(operation), NW_OK);
    }
    assert_eq!(run(operation).await, NW_CANCELED);
    // SAFETY: run joined the operation's callback/source retirement gate.
    unsafe {
        nw_server_operation_release(operation);
    }
    assert_eq!(
        call.read().await.err().unwrap().code(),
        protocol::tonic::Code::Cancelled
    );
    let mut bytes = [0u8; 5];
    let mut written = 0;
    // SAFETY: retained buffer is immutable and the fixed destination is exclusive and large enough.
    unsafe {
        assert_eq!(
            nw_server_buffer_copy(
                buffer,
                bytes.as_mut_ptr(),
                5,
                &mut written,
                std::ptr::null_mut()
            ),
            NW_OK
        );
        assert_eq!(&bytes, b"ready");
        assert_eq!(written, 5);
        nw_server_buffer_release(buffer);
    }
    cleanup(host, request, pointer).await;
}
#[tokio::test]
async fn grpc_trailer_frame_and_clones_keep_request_anchor_until_last_release() {
    let (host, request, pointer) = fixture();
    request.mark_dispatch_done();
    request.retired.store(true, Ordering::Release);
    let mut trailers = HeaderMap::new();
    trailers.insert("grpc-status", http::HeaderValue::from_static("4"));
    let stream = futures_util::stream::iter([Ok::<_, Status>(Frame::trailers(trailers))]);
    let inner = protocol::tonic::body::Body::new(StreamBody::new(stream));
    let mut body = GrpcBody {
        terminal_head: false,
        inner: Some(inner),
        request: request.clone(),
        ended: false,
    };
    let frame = body.frame().await.unwrap().unwrap();
    assert!(request.body_done.load(Ordering::Acquire));
    drop(body);
    let cloned = frame
        .trailers_ref()
        .unwrap()
        .get("grpc-status")
        .unwrap()
        .clone();
    drop(frame);
    host.reap();
    assert_eq!(lock(&host.requests).len(), 1);
    assert_eq!(host.budget.snapshot().0, remaining_anchor(&request));
    drop(cloned);
    host.reap();
    assert!(lock(&host.requests).is_empty());
    // SAFETY: test marked dispatch complete and retired with no native operations; raw owner now releases once.
    unsafe { nw_server_request_release(pointer) };
    drop(request);
    assert_eq!(host.budget.snapshot(), (0, 0));
}

#[tokio::test]
async fn grpc_committed_terminal_precedes_late_deadline_before_response_poll() {
    let (host, request, pointer) = fixture();
    let call = request.grpc.as_ref().unwrap().clone();
    let incoming = Request::builder()
        .version(http::Version::HTTP_2)
        .method("POST")
        .uri("http://localhost/test.Call/Run")
        .header("content-type", "application/grpc")
        .body(http_body_util::Full::new(Bytes::new()))
        .unwrap();
    let serving = protocol::serve(incoming, call.clone(), 64, Arc::new(|| Ok(())));
    tokio::pin!(serving);
    assert!(futures_util::poll!(&mut serving).is_pending());
    call.finish(
        Status::permission_denied("earlier terminal"),
        ResponseMetadata::empty(&request.budget).unwrap(),
        &request.cancel,
    )
    .await
    .unwrap();
    request.abort(NW_TIMEOUT);
    let response = response_or_cancel(&request, serving).await;
    let mut body = GrpcBody {
        terminal_head: response.headers().contains_key("grpc-status"),
        inner: Some(response.into_body()),
        request: request.clone(),
        ended: false,
    };
    let frame = body.frame().await.unwrap().unwrap();
    assert_eq!(
        frame.trailers_ref().unwrap().get("grpc-status").unwrap(),
        "7"
    );
    drop(frame);
    drop(body);
    drop(call);
    cleanup(host, request, pointer).await;
}

#[tokio::test]
async fn grpc_unresolved_response_deadline_terminates_without_waiting_for_handler() {
    let (host, request, pointer) = fixture();
    request.abort(NW_TIMEOUT);
    let response = response_or_cancel(&request, std::future::pending()).await;
    assert_eq!(response.headers().get("grpc-status").unwrap(), "4");
    drop(response);
    cleanup(host, request, pointer).await;
}

#[tokio::test]
async fn grpc_dropped_response_future_cancels_pending_read_and_retires_body() {
    let (host, request, pointer) = fixture();
    let call = request.grpc.as_ref().unwrap().clone();
    let incoming = Request::builder()
        .version(http::Version::HTTP_2)
        .method("POST")
        .uri("http://localhost/test.Call/Run")
        .header("content-type", "application/grpc")
        .body(StreamBody::new(futures_util::stream::pending::<
            std::result::Result<Frame<Bytes>, Status>,
        >()))
        .unwrap();
    let mut serving = Box::pin(protocol::serve(
        incoming,
        call.clone(),
        64,
        Arc::new(|| Ok(())),
    ));
    assert!(futures_util::poll!(&mut serving).is_pending());
    let pending = PendingResponse {
        request: request.clone(),
        dispatched: Arc::new(AtomicBool::new(true)),
        transferred: false,
    };
    let read = read(
        request.clone(),
        SideGuard::acquire(&request.read_active).unwrap(),
    );
    tokio::pin!(read);
    assert!(futures_util::poll!(&mut read).is_pending());
    drop(pending);
    assert_eq!(read.await.err().unwrap().status, NW_CANCELED);
    assert!(request.body_done.load(Ordering::Acquire));
    assert!(!request.dispatch_done.load(Ordering::Acquire));
    // The canceled serving future still owns codec state until its own drop.
    drop(serving);
    drop(call);
    cleanup(host, request, pointer).await;
}

#[tokio::test]
async fn grpc_dropped_undispatched_response_retires_its_ticket() {
    let (host, request, pointer) = fixture();
    let pending = PendingResponse {
        request: request.clone(),
        dispatched: Arc::new(AtomicBool::new(false)),
        transferred: false,
    };
    drop(pending);
    assert!(request.body_done.load(Ordering::Acquire));
    assert!(request.dispatch_done.load(Ordering::Acquire));
    assert!(request.retired.load(Ordering::Acquire));
    // SAFETY: fixed request owner remains live while every input cleanup source retires.
    while unsafe { nw_server_request_try_retire(pointer) } != NW_OK {
        tokio::task::yield_now().await;
    }
    // SAFETY: successful retirement above authorizes the creator's unique release.
    unsafe { nw_server_request_release(pointer) };
    host.reap();
    assert!(lock(&host.requests).is_empty());
    drop(request);
    assert_eq!(host.budget.snapshot(), (0, 0));
}

#[tokio::test]
async fn grpc_reaper_finishes_metadata_cleanup_after_both_completion_flags() {
    let (host, request, pointer) = fixture();
    let call = request.grpc.as_ref().unwrap().clone();
    call.headers(ResponseMetadata::empty(&request.budget).unwrap())
        .unwrap();
    call.finish(
        Status::ok(""),
        ResponseMetadata::empty(&request.budget).unwrap(),
        &request.cancel,
    )
    .await
    .unwrap();
    assert!(request.mark_dispatch_done());
    assert_eq!(request.try_retire(), NW_OK);
    // Construct the quiescent postcondition of a missed completion notification.
    // This deterministic state test does not claim to reproduce its scheduling.
    request.body_done.store(true, Ordering::Release);
    host.reap();
    let initial_cleared = call.initial_credit().is_none();
    let terminal_cleared = call.terminal_credit().is_none();
    let removed = lock(&host.requests).is_empty();
    // Observe the required postcondition before explicit test-only teardown, so
    // an unmodified implementation fails without leaving the raw fixture owner.
    call.release_metadata();
    // SAFETY: the real retirement gate succeeded and no native operation exists.
    unsafe { nw_server_request_release(pointer) };
    host.reap();
    drop(call);
    drop(request);
    assert_eq!(host.budget.snapshot(), (0, 0));
    assert_eq!(host.retiring.load(Ordering::Acquire), 0);
    assert!(initial_cleared, "reaper retained initial metadata");
    assert!(terminal_cleared, "reaper retained terminal metadata");
    assert!(removed, "reaper retained the completed request");
}

#[tokio::test]
async fn grpc_reaper_waits_for_input_cleanup_before_releasing_metadata() {
    let (host, request, pointer) = fixture();
    let call = request.grpc.as_ref().unwrap().clone();
    call.headers(ResponseMetadata::empty(&request.budget).unwrap())
        .unwrap();
    call.finish(
        Status::ok(""),
        ResponseMetadata::empty(&request.budget).unwrap(),
        &request.cancel,
    )
    .await
    .unwrap();
    assert!(request.mark_dispatch_done());
    assert_eq!(request.try_retire(), NW_OK);
    // A late response/body drop can own this existing input-cleanup guard after
    // request retirement. Keep that real guard live while constructing the same
    // missed-notification postcondition; metadata must not be cleared yet.
    request.live_ops.fetch_add(1, Ordering::AcqRel);
    let input_cleanup = InputDiscard(request.clone());
    request.body_done.store(true, Ordering::Release);
    host.reap();
    let preserved_while_live = call.initial_credit().is_some()
        && call.terminal_credit().is_some()
        && lock(&host.requests).len() == 1;
    drop(input_cleanup);
    let initial_cleared = call.initial_credit().is_none();
    let terminal_cleared = call.terminal_credit().is_none();
    let removed = lock(&host.requests).is_empty();
    call.release_metadata();
    // SAFETY: the retired request's last input-cleanup guard has been dropped.
    unsafe { nw_server_request_release(pointer) };
    host.reap();
    drop(call);
    drop(request);
    assert_eq!(host.budget.snapshot(), (0, 0));
    assert_eq!(host.retiring.load(Ordering::Acquire), 0);
    assert!(
        preserved_while_live,
        "reaper cleared live-operation metadata"
    );
    assert!(
        initial_cleared,
        "last input cleanup retained initial metadata"
    );
    assert!(
        terminal_cleared,
        "last input cleanup retained terminal metadata"
    );
    assert!(removed, "last input cleanup retained the completed request");
}
