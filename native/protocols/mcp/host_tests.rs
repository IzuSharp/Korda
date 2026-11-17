//! Actual Host/C ABI tests for the pre-parser gate and retained native ownership.
use super::*;
use crate::{
    config::{Config, Listener, Route},
    exports::*,
    *,
};
use std::{
    collections::HashSet,
    io::{Read, Write},
    net::{Ipv4Addr, SocketAddr, TcpStream},
    sync::RwLock,
};

#[derive(Default)]
struct Events {
    ticket: AtomicU64,
    gates: AtomicUsize,
    calls: AtomicUsize,
    terminal: AtomicU32,
}
unsafe extern "C" fn notify(cookie: usize, ticket: u64, _handler: u64, kind: u32) -> u32 {
    // SAFETY: test owns this callback state until Host retirement.
    let events = unsafe { &*(cookie as *const Events) };
    if kind == 2 {
        events.gates.fetch_add(1, Ordering::AcqRel);
        events.ticket.store(ticket, Ordering::Release);
    } else if kind == 3 {
        events.calls.fetch_add(1, Ordering::AcqRel);
    } else {
        return NW_CONTRACT_VIOLATION;
    }
    NW_OK
}
unsafe extern "C" fn control(_: usize, _: u32, _: u32, _: u64, _: *const NwErrorInfoV1) -> u32 {
    NW_OK
}
unsafe extern "C" fn terminal(cookie: usize, status: u32, _: *const NwErrorInfoV1) -> u32 {
    // SAFETY: one synchronous test terminal state outlives its operation.
    unsafe { &*(cookie as *const Events) }
        .terminal
        .store(status + 1, Ordering::Release);
    NW_OK
}
fn until(mut predicate: impl FnMut() -> bool) {
    let end = Instant::now() + Duration::from_secs(8);
    while !predicate() {
        assert!(Instant::now() < end, "MCP native test wait timed out");
        std::thread::sleep(Duration::from_millis(1));
    }
}
fn profile(node_bytes: u64, reply_bytes: u64) -> Arc<Profile> {
    let schema = serde_json::json!({"type":"object","properties":{"text":{"type":"string"},"items":{"type":"array","items":{"type":"object","properties":{}}}},"x-nativeweave-managed-peak-v1":{"nodeBytes":node_bytes,"replyBytes":reply_bytes}});
    Profile::parse(&serde_json::to_vec(&serde_json::json!({"version":1,"kind":"mcp","handler_id":1,"protocol_version":"2026-07-28","legacy_sessions":false,"event_store":false,"strict_metadata":true,"allowed_hosts":[],"allowed_origins":[],"max_request_bytes":1048576,"max_json_depth":32,"max_json_nodes":4096,"max_json_string_bytes":262144,
    "tools":[{"name":"echo","description":"native gate","handler_id":4294967297u64,"input_codec_id":8589934594u64,"output_codec_id":8589934595u64,"input_schema":schema,"output_schema":schema}],"resources":[],"prompts":[]})).unwrap(),1).unwrap()
}
fn host(events: &Events, node_bytes: u64, reply_bytes: u64) -> Arc<Host> {
    let host = Host::new(
        Config {
            _request_timeout: Duration::from_secs(5),
            _shutdown_timeout: Duration::from_secs(2),
            header_timeout: Duration::from_secs(2),
            keepalive: Duration::from_secs(2),
            _max_request_bytes: 1048576,
            _max_json_bytes: 262144,
            max_header_bytes: 16384,
            max_chunk_bytes: 65536,
            max_connections: 4,
            max_h2_streams: 4,
            active_requests: 2,
            queued_requests: 0,
            native_payload_bytes: 8 * 1024 * 1024,
            managed_payload_bytes: 8 * 1024 * 1024,
            request_retained_bytes: 4 * 1024 * 1024,
            listeners: vec![Listener {
                name: "mcp-test".into(),
                address: SocketAddr::from((Ipv4Addr::LOCALHOST, 0)),
                protocols: 1,
                tls: RwLock::new(None),
            }],
            trusted_proxies: HashSet::new(),
            forward_limit: 1,
        },
        NwServerDispatchV1 {
            struct_size: 32,
            version: 1,
            cookie: events as *const _ as usize,
            on_request: notify as *const () as usize,
            on_control: control as *const () as usize,
        },
    )
    .unwrap();
    *lock(&host.routes) = Some(vec![Route {
        id: 1,
        kind: 2,
        canonical: "/mcp".into(),
        parameters: vec![],
        methods: vec![Method::POST],
        timeout: Duration::from_secs(5),
        max_request_bytes: 1048576,
        max_json_bytes: 0,
        mcp: Some(profile(node_bytes, reply_bytes)),
        grpc: None,
    }]);
    host
}
fn start(pointer: *mut NwServerOperation, events: &Events) {
    events.terminal.store(0, Ordering::Release);
    let callbacks = NwOperationCallbacksV1 {
        struct_size: 24,
        flags: 0,
        cookie: events as *const _ as usize,
        on_terminal: Some(terminal),
    };
    assert_eq!(
        // SAFETY: exact test callback and unstarted creating-module operation owner.
        unsafe { nw_server_operation_start(pointer, &callbacks, std::ptr::null_mut()) },
        NW_OK
    );
}
fn finish(pointer: *mut NwServerOperation, events: &Events) {
    until(|| events.terminal.load(Ordering::Acquire) != 0);
    assert_eq!(events.terminal.load(Ordering::Acquire), NW_OK + 1);
    assert_eq!(
        // SAFETY: terminal operation owns one unconsumed void result.
        unsafe { nw_server_operation_take_void(pointer, std::ptr::null_mut()) },
        NW_OK
    );
    retire(pointer);
}
fn retire(pointer: *mut NwServerOperation) {
    // SAFETY: owner remains live until actual native callback/source quiescence.
    until(|| unsafe { nw_server_operation_try_retire(pointer) } == NW_OK);
    // SAFETY: the single retired test owner is released exactly once.
    unsafe { nw_server_operation_release(pointer) };
}
fn begin(host: &Arc<Host>, job: Job, events: &Events) -> *mut NwServerOperation {
    let operation = OpState::new(host.clone(), None, job).unwrap();
    let pointer = Box::into_raw(Box::new(NwServerOperation { inner: operation }));
    start(pointer, events);
    pointer
}
fn rpc(method: &str, args: &str) -> String {
    format!(
        r#"{{"jsonrpc":"2.0","id":1,"method":"{method}","params":{{{args}"_meta":{{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{{}}}}}}}}"#
    )
}
fn network(
    host: &Arc<Host>,
    method: &str,
    body: String,
    extra: &str,
) -> std::thread::JoinHandle<String> {
    let url = lock(&host.addresses)[0].1.clone();
    let authority = url
        .split("://")
        .nth(1)
        .unwrap()
        .trim_end_matches('/')
        .to_owned();
    let method = method.to_owned();
    let extra = extra.to_owned();
    std::thread::spawn(move || {
        let mut socket = TcpStream::connect(&authority).unwrap();
        socket
            .set_read_timeout(Some(Duration::from_secs(8)))
            .unwrap();
        write!(socket,"POST /mcp HTTP/1.1\r\nHost: {authority}\r\nContent-Type: application/json\r\nAccept: application/json, text/event-stream\r\nMCP-Protocol-Version: 2026-07-28\r\nMcp-Method: {method}\r\nContent-Length: {}\r\nConnection: close\r\n{extra}\r\n{body}",body.len()).unwrap();
        let mut response = String::new();
        socket.read_to_string(&mut response).unwrap();
        response
    })
}
fn scenario(body: String, method: &str, deny: bool, expected_status: u16, expected_sdk: usize) {
    scenario_pressure(
        body,
        method,
        deny,
        expected_status,
        expected_sdk,
        None,
        (4096, 2097152),
    );
}
fn scenario_pressure(
    body: String,
    method: &str,
    deny: bool,
    expected_status: u16,
    expected_sdk: usize,
    pressure: Option<Side>,
    layout: (u64, u64),
) {
    let events = Box::<Events>::default();
    let host = host(&events, layout.0, layout.1);
    let pressure_owner1 = host.budget.request(4 * 1024 * 1024).unwrap();
    let pressure_owner2 = host.budget.request(4 * 1024 * 1024).unwrap();
    let pressure1 =
        pressure.map(|side| pressure_owner1.try_acquire(side, 3 * 1024 * 1024).unwrap());
    let pressure2 =
        pressure.map(|side| pressure_owner2.try_acquire(side, 3 * 1024 * 1024).unwrap());
    let started = begin(&host, Job::Start, &events);
    finish(started, &events);
    let network = network(
        &host,
        method,
        body,
        if method == "tools/call" {
            "Mcp-Name: echo\r\n"
        } else {
            ""
        },
    );
    until(|| events.ticket.load(Ordering::Acquire) != 0);
    let request = lock(&host.requests)
        .get(&events.ticket.load(Ordering::Acquire))
        .unwrap()
        .clone();
    let server = NwServer {
        inner: host.clone(),
    };
    let mut server = Box::new(server);
    let mut owner = std::ptr::null_mut();
    let mut meta = std::ptr::null_mut();
    assert_eq!(
        // SAFETY: all creating-module handles and outputs remain live and aligned.
        unsafe {
            nw_server_request_take(
                &mut *server,
                request.head.id,
                &mut owner,
                &mut meta,
                std::ptr::null_mut(),
            )
        },
        NW_OK
    );
    // SAFETY: metadata ownership is independent and moved exactly once by take.
    unsafe { nw_server_metadata_release(meta) };
    let mut duplicate = std::ptr::null_mut();
    let mut duplicate_meta = std::ptr::null_mut();
    assert_eq!(
        // SAFETY: second take must reject without transferring either owner.
        unsafe {
            nw_server_request_take(
                &mut *server,
                request.head.id,
                &mut duplicate,
                &mut duplicate_meta,
                std::ptr::null_mut(),
            )
        },
        NW_INVALID_STATE
    );
    assert!(duplicate.is_null() && duplicate_meta.is_null());
    let mut unstarted = std::ptr::null_mut();
    assert_eq!(
        // SAFETY: create alone may never poll input or commit a decision.
        unsafe { nw_server_mcp_gate_resume_create(owner, 1, &mut unstarted, std::ptr::null_mut()) },
        NW_OK
    );
    // SAFETY: never-started operation has no callback/source admission.
    unsafe { nw_server_operation_release(unstarted) };
    assert!(request.mcp.as_ref().unwrap().awaiting());
    assert_eq!(
        request
            .mcp
            .as_ref()
            .unwrap()
            .sdk_admissions
            .load(Ordering::Acquire),
        0
    );
    let mut gate = std::ptr::null_mut();
    assert_eq!(
        // SAFETY: typed gate operation retains the same request until its retirement.
        unsafe {
            nw_server_mcp_gate_resume_create(
                owner,
                if deny { 0 } else { 1 },
                &mut gate,
                std::ptr::null_mut(),
            )
        },
        NW_OK
    );
    start(gate, &events);
    finish(gate, &events);
    if deny {
        let bytes = config::encode(std::iter::empty());
        let mut begin_op = std::ptr::null_mut();
        assert_eq!(
            // SAFETY: denial keeps the raw HTTP output path available on this owner.
            unsafe {
                nw_server_response_begin_create(
                    owner,
                    401,
                    crate::metadata::view(&bytes),
                    &mut begin_op,
                    std::ptr::null_mut(),
                )
            },
            NW_OK
        );
        start(begin_op, &events);
        finish(begin_op, &events);
        let mut end = std::ptr::null_mut();
        assert_eq!(
            // SAFETY: the empty bounded trailer block is borrowed until create returns.
            unsafe {
                nw_server_response_finish_create(
                    owner,
                    crate::metadata::view(&bytes),
                    &mut end,
                    std::ptr::null_mut(),
                )
            },
            NW_OK
        );
        start(end, &events);
        finish(end, &events);
    }
    assert_eq!(
        request
            .mcp
            .as_ref()
            .unwrap()
            .sdk_admissions
            .load(Ordering::Acquire),
        expected_sdk
    );
    assert_eq!(events.calls.load(Ordering::Acquire), 0);
    assert_eq!(
        // SAFETY: exactly one managed-dispatch completion ends control admission.
        unsafe { nw_server_dispatch_complete(owner, NW_OK, std::ptr::null_mut()) },
        NW_OK
    );
    // SAFETY: request is retained until SDK worker callback/source retirement.
    until(|| unsafe { nw_server_request_try_retire(owner) } == NW_OK);
    // SAFETY: this is the unique retired request owner.
    unsafe { nw_server_request_release(owner) };
    let response = network.join().unwrap();
    assert!(
        response.starts_with(&format!("HTTP/1.1 {expected_status}")),
        "{response}"
    );
    drop(request);
    drop(pressure1);
    drop(pressure2);
    until(|| host.budget.snapshot() == (0, 0));
    assert_eq!(events.gates.load(Ordering::Acquire), 1);
    println!(
        "preparse status={expected_status} deny={deny} sdk_admissions={expected_sdk} handlers=0 request_take=1 gates=1 final_native=0 final_managed=0 node_bytes={} reply_bytes={}",
        layout.0, layout.1
    );
    let closing = begin(&host, Job::Close(Duration::from_secs(2)), &events);
    until(|| events.terminal.load(Ordering::Acquire) != 0);
    let mut result = NwServerCloseResultV1 {
        struct_size: 32,
        version: 1,
        close_status: 99,
        pending_requests: 99,
        retained_managed_bytes: 99,
        retained_native_bytes: 99,
    };
    assert_eq!(
        // SAFETY: initialized truthful-capacity output and terminal close operation.
        unsafe { nw_server_operation_take_close(closing, &mut result, std::ptr::null_mut()) },
        NW_OK
    );
    retire(closing);
    assert_eq!(
        (
            result.close_status,
            result.pending_requests,
            result.retained_managed_bytes,
            result.retained_native_bytes
        ),
        (0, 0, 0, 0)
    );
}
#[test]
fn actual_host_denial_and_full_peak_failures_never_enter_public_sdk_or_handler() {
    scenario("{malformed and unread".into(), "tools/list", true, 401, 0);
    scenario(rpc("tools/list", ""), "tools/list", false, 200, 1);
    scenario(
        rpc(
            "tools/call",
            &format!(
                r#""name":"echo","arguments":{{"text":"x","many":[{}]}},"#,
                vec!["0"; 4096].join(",")
            ),
        ),
        "tools/call",
        false,
        413,
        0,
    );
    scenario(
        rpc(
            "tools/call",
            &format!(
                r#""name":"echo","arguments":{{"text":"x","deep":{}0{}}},"#,
                "[".repeat(33),
                "]".repeat(33)
            ),
        ),
        "tools/call",
        false,
        413,
        0,
    );
    scenario(
        rpc(
            "tools/call",
            &format!(
                r#""name":"echo","arguments":{{"text":"{}"}},"#,
                "\\u0061".repeat(131072)
            ),
        ),
        "tools/call",
        false,
        503,
        0,
    );
    scenario(
        rpc(
            "tools/call",
            &format!(
                r#""name":"echo","arguments":{{"text":"x","many":[{}]}},"#,
                vec!["0"; 3000].join(",")
            ),
        ),
        "tools/call",
        false,
        503,
        0,
    );
    for side in [Side::Native, Side::Managed] {
        scenario_pressure(
            rpc("tools/call", r#""name":"echo","arguments":{"text":"x"},"#),
            "tools/call",
            false,
            503,
            0,
            Some(side),
            (4096, 2097152),
        );
    }
    // Real managed witnesses prove these declared inline/boxing widths. The
    // native half separately observes zero public SDK and continuation entries.
    scenario_pressure(
        rpc(
            "tools/call",
            &format!(
                r#""name":"echo","arguments":{{"items":[{}]}},"#,
                vec!["{}"; 128].join(",")
            ),
        ),
        "tools/call",
        false,
        503,
        0,
        None,
        (524288, 2097152),
    );
    scenario_pressure(
        rpc("tools/call", r#""name":"echo","arguments":{"text":"x"},"#),
        "tools/call",
        false,
        503,
        0,
        None,
        (4096, 8 * 1024 * 1024),
    );
}
