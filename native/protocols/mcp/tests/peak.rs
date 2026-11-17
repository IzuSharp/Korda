//! Allocation witness for the enabled parser/tree/bridge terms. This counts
//! requested live allocation capacities, not RSS or allocator fragmentation.
use nativeweave_protocol_mcp::preflight::{self, Limits};
use std::{
    alloc::{GlobalAlloc, Layout, System},
    sync::atomic::{AtomicBool, AtomicUsize, Ordering},
    time::{Duration, Instant},
};
struct Tracking;
static ENABLED: AtomicBool = AtomicBool::new(false);
static GENERATION: AtomicUsize = AtomicUsize::new(1);
static LIVE: AtomicUsize = AtomicUsize::new(0);
static PEAK: AtomicUsize = AtomicUsize::new(0);
// Each allocation keeps a test-only generation header outside the counted user
// Layout. It prevents unrelated frees and cached previous samples from corrupting
// the live-capacity counter; the product never uses this allocator.
// SAFETY: every allocation preserves the requested alignment and stores its
// matching system layout in a private prefix read only by this allocator.
unsafe impl GlobalAlloc for Tracking {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        let prefix = layout.align().max(16);
        let Some(size) = layout.size().checked_add(prefix) else {
            return std::ptr::null_mut();
        };
        let Ok(total) = Layout::from_size_align(size, layout.align().max(16)) else {
            return std::ptr::null_mut();
        };
        // SAFETY: validated System layout and extra header space precede the aligned payload.
        let base = unsafe { System.alloc(total) };
        if base.is_null() {
            return base;
        }
        let generation = if ENABLED.load(Ordering::Acquire) {
            GENERATION.load(Ordering::Acquire)
        } else {
            0
        };
        // SAFETY: the first machine word is naturally aligned and inside this allocation.
        unsafe { base.cast::<usize>().write(generation) };
        if generation != 0 {
            let live = LIVE.fetch_add(layout.size(), Ordering::AcqRel) + layout.size();
            PEAK.fetch_max(live, Ordering::AcqRel);
        }
        // SAFETY: prefix preserves the original requested alignment and payload length.
        unsafe { base.add(prefix) }
    }
    unsafe fn dealloc(&self, pointer: *mut u8, layout: Layout) {
        let prefix = layout.align().max(16);
        // SAFETY: pointer came from this allocator under the same original Layout.
        let base = unsafe { pointer.sub(prefix) };
        // SAFETY: the header remains valid until the System deallocation below.
        let generation = unsafe { base.cast::<usize>().read() };
        if generation != 0 && generation == GENERATION.load(Ordering::Acquire) {
            LIVE.fetch_sub(layout.size(), Ordering::AcqRel);
        }
        let total =
            Layout::from_size_align(layout.size() + prefix, layout.align().max(16)).unwrap();
        // SAFETY: total exactly reconstructs the allocation's original System layout.
        unsafe { System.dealloc(base, total) };
    }
}
#[global_allocator]
static ALLOCATOR: Tracking = Tracking;
fn measure(action: impl FnOnce()) -> (usize, usize) {
    GENERATION.fetch_add(1, Ordering::AcqRel);
    LIVE.store(0, Ordering::Release);
    PEAK.store(0, Ordering::Release);
    ENABLED.store(true, Ordering::Release);
    action();
    ENABLED.store(false, Ordering::Release);
    (PEAK.load(Ordering::Acquire), LIVE.load(Ordering::Acquire))
}
#[test]
fn admitted_native_layout_and_peak_terms_bound_actual_serde_overlap() {
    let limits = Limits {
        wire: 1048576,
        depth: 32,
        nodes: 4096,
        strings: 262144,
    };
    assert!(size_of::<serde_json::Value>() <= 64);
    assert!(size_of::<serde_json::Map<String, serde_json::Value>>() <= 128);
    assert!(size_of::<String>() <= 32);
    println!(
        "layout value={} map={} string={} vec={}",
        size_of::<serde_json::Value>(),
        size_of::<serde_json::Map<String, serde_json::Value>>(),
        size_of::<String>(),
        size_of::<Vec<serde_json::Value>>()
    );
    let cases = [
        ("tiny-elements", format!("[{}]", vec!["0"; 4095].join(","))),
        (
            "object-slots",
            format!(
                "{{{}}}",
                (0..1365)
                    .map(|i| format!("\"{i}\":0"))
                    .collect::<Vec<_>>()
                    .join(",")
            ),
        ),
        ("deep", format!("{}0{}", "[".repeat(31), "]".repeat(31))),
        ("escaped", format!("\"{}\"", "\\u0061".repeat(131072))),
        ("utf8", format!("\"{}\"", "é".repeat(131072))),
    ];
    for (name, text) in cases {
        let shape = preflight::scan(
            text.as_bytes(),
            limits,
            Instant::now() + Duration::from_secs(5),
        )
        .unwrap();
        let (scan, live) = measure(|| {
            preflight::scan(
                text.as_bytes(),
                limits,
                Instant::now() + Duration::from_secs(5),
            )
            .unwrap();
        });
        assert_eq!(live, 0);
        assert!((scan as u64) <= preflight::scan_scratch(text.len(), 32).unwrap());
        let (peak, live) = measure(|| {
            // SDK typed JSON contains serde Value argument maps; this overlap
            // keeps native source, copied bridge JSON, a second decoded result
            // and compatibility JSON text alive together before freeing them.
            let tree: serde_json::Value = serde_json::from_slice(text.as_bytes()).unwrap();
            let bridge = serde_json::to_vec(&tree).unwrap();
            let result: serde_json::Value = serde_json::from_slice(&bridge).unwrap();
            let compatibility = serde_json::to_string(&result).unwrap();
            std::hint::black_box((&tree, &bridge, &result, &compatibility));
        });
        let bound = preflight::native_peak(text.len(), shape).unwrap();
        assert_eq!(live, 0);
        assert!(
            (peak as u64) <= bound,
            "{name}: measured{peak}>bound{bound}"
        );
        println!(
            "peak case={name} wire={} nodes={} members={} strings={} scan={} measured={} bound={} released={live}",
            text.len(),
            shape.nodes,
            shape.members,
            shape.strings,
            scan,
            peak,
            bound
        );
    }
    actual_public_rmcp_peak(limits);
}

struct MeasuredCall;
impl nativeweave_protocol_mcp::runtime::ForeignCall for MeasuredCall {
    fn invoke(
        &self,
        _kind: u32,
        _handler: u64,
        arguments: serde_json::Value,
    ) -> std::pin::Pin<
        Box<
            dyn std::future::Future<
                    Output = Result<serde_json::Value, nativeweave_protocol_mcp::rmcp::ErrorData>,
                > + Send
                + '_,
        >,
    > {
        Box::pin(async move {
            let bridge = serde_json::to_vec(&arguments).unwrap();
            let owned_copy = bridge.clone();
            std::hint::black_box((&arguments, &bridge, &owned_copy));
            Ok(
                serde_json::json!({"content":[{"type":"text","text":"Measured fixed response"}],"isError":true}),
            )
        })
    }
}
fn actual_public_rmcp_peak(limits: Limits) {
    use http_body_util::BodyExt;
    use nativeweave_protocol_mcp::{profile::Profile, runtime};
    use std::sync::Arc;
    let schema = serde_json::json!({"type":"object","properties":{"text":{"type":"string"},"items":{"type":"array","items":{"type":"integer"}}},"x-nativeweave-managed-peak-v1":{"nodeBytes":4096,"replyBytes":2097152}});
    let descriptor = serde_json::to_vec(&serde_json::json!({"version":1,"kind":"mcp","handler_id":1,"protocol_version":"2026-07-28","legacy_sessions":false,"event_store":false,"strict_metadata":true,"allowed_hosts":["localhost:80"],"allowed_origins":[],"max_request_bytes":1048576,"max_json_depth":32,"max_json_nodes":4096,"max_json_string_bytes":262144,
        "tools":[{"name":"measure","description":"measured native SDK","handler_id":4294967297u64,"input_codec_id":8589934594u64,"output_codec_id":8589934595u64,"input_schema":schema,"output_schema":schema}],"resources":[],"prompts":[]})).unwrap();
    let profile = Profile::parse(&descriptor, 1).unwrap();
    for (name, args) in [
        (
            "sdk-tiny",
            format!("{{\"items\":[{}]}}", vec!["0"; 3000].join(",")),
        ),
        (
            "sdk-escaped",
            format!("{{\"text\":\"{}\"}}", "\\u0061".repeat(100000)),
        ),
        (
            "sdk-utf8",
            format!("{{\"text\":\"{}\"}}", "é".repeat(100000)),
        ),
    ] {
        let text = format!(
            r#"{{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{{"name":"measure","arguments":{args},"_meta":{{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{{}}}}}}}}"#
        );
        let shape = preflight::scan(
            text.as_bytes(),
            limits,
            Instant::now() + Duration::from_secs(5),
        )
        .unwrap();
        let execute = || {
            let executor = tokio::runtime::Builder::new_current_thread()
                .enable_all()
                .build()
                .unwrap();
            executor.block_on(async {
                let request = http::Request::builder()
                    .method("POST")
                    .uri("http://localhost:80/mcp")
                    .header("host", "localhost:80")
                    .header("accept", "application/json, text/event-stream")
                    .header("content-type", "application/json")
                    .header("mcp-protocol-version", "2026-07-28")
                    .header("mcp-method", "tools/call")
                    .header("mcp-name", "measure")
                    .body(http_body_util::Full::new(bytes::Bytes::copy_from_slice(
                        text.as_bytes(),
                    )))
                    .unwrap();
                let response = runtime::serve(
                    profile.clone(),
                    vec!["localhost:80".into()],
                    request,
                    Arc::new(MeasuredCall),
                    tokio_util::sync::CancellationToken::new(),
                )
                .await;
                assert_eq!(response.status(), http::StatusCode::OK);
                let bytes = response.into_body().collect().await.unwrap().to_bytes();
                assert!(
                    std::str::from_utf8(&bytes)
                        .unwrap()
                        .contains("Measured fixed response")
                );
                tokio::task::yield_now().await;
            });
        };
        execute(); // Initialize upstream process/thread metadata separately from request ownership.
        let (peak, live) = measure(execute);
        let bound = preflight::native_peak(text.len(), shape).unwrap() + 65536;
        assert_eq!(live, 0);
        assert!((peak as u64) <= bound, "actual SDK {name}: {peak}>{bound}");
        println!("sdk_peak case={name} measured={peak} bound={bound} released={live}");
    }
}
