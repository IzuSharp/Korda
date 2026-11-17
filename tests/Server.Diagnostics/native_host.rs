//! B20 owned actual native TCP dispatcher/context integration, nested in B17 tests.
use super::*;
use nativeweave_server_diagnostics::{TraceContextV1, tracing};
#[derive(Default)]
struct Observations {
    fields: std::sync::Mutex<Vec<String>>,
    native_polls: AtomicU64,
    enters: AtomicU64,
    exits: AtomicU64,
}
struct Subscriber(Arc<Observations>);
struct Visitor<'a>(&'a Observations);
impl tracing::field::Visit for Visitor<'_> {
    fn record_debug(&mut self, field: &tracing::field::Field, value: &dyn std::fmt::Debug) {
        lock(&self.0.fields).push(format!("{}={value:?}", field.name()));
    }
}
impl tracing::Subscriber for Subscriber {
    fn enabled(&self, _: &tracing::Metadata<'_>) -> bool {
        true
    }
    fn new_span(&self, attributes: &tracing::span::Attributes<'_>) -> tracing::span::Id {
        attributes.record(&mut Visitor(&self.0));
        tracing::span::Id::from_u64(1)
    }
    fn record(&self, _: &tracing::span::Id, values: &tracing::span::Record<'_>) {
        values.record(&mut Visitor(&self.0));
    }
    fn record_follows_from(&self, _: &tracing::span::Id, _: &tracing::span::Id) {}
    fn event(&self, event: &tracing::Event<'_>) {
        event.record(&mut Visitor(&self.0));
    }
    fn enter(&self, _: &tracing::span::Id) {
        self.0.enters.fetch_add(1, Ordering::Relaxed);
        if std::thread::current().name() == Some("nativeweave-server") {
            self.0.native_polls.fetch_add(1, Ordering::Relaxed);
        }
    }
    fn exit(&self, _: &tracing::span::Id) {
        self.0.exits.fetch_add(1, Ordering::Relaxed);
    }
}
#[test]
fn owned_subscriber_follows_actual_tcp_metadata_and_callback_retirement() {
    let seen = Arc::new(Observations::default());
    let dispatcher = tracing::Dispatch::new(Subscriber(seen.clone()));
    let state = Box::<Notifications>::default();
    let host = tracing::dispatcher::with_default(&dispatcher, || make(&state));
    let operation = start_op(&host, Job::Start, &state);
    retire_op(operation);
    let address = lock(&host.addresses)[0]
        .1
        .trim_start_matches("http://")
        .trim_end_matches('/')
        .to_owned();
    let mut peer = TcpStream::connect(address).unwrap();
    peer.write_all(b"GET /test HTTP/1.1\r\nHost: localhost\r\ntraceparent: 00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01\r\ntracestate: vendor=value\r\nbaggage: password=SECRET\r\n\r\n").unwrap();
    until(|| state.ticket.load(Ordering::Acquire) != 0);
    until(|| state.cancel.load(Ordering::Acquire) == NW_TIMEOUT);
    let server = Box::into_raw(Box::new(NwServer {
        inner: host.clone(),
    }));
    let (mut request, mut metadata) = (std::ptr::null_mut(), std::ptr::null_mut());
    // SAFETY: live creator host/ticket, initialized exclusive output storage, and
    // transferred owners remain retained through metadata access and retirement.
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
        assert_eq!(info.trace_context.length, 32);
        let context =
            TraceContextV1::decode(std::slice::from_raw_parts(info.trace_context.data, 32))
                .unwrap();
        assert_eq!(context.trace_id[0], 0x4b);
        assert_eq!(
            context.parent_span_id,
            [0, 0xf0, 0x67, 0xaa, 0x0b, 0xa9, 2, 0xb7]
        );
        assert_eq!(
            std::slice::from_raw_parts(info.trace_state.data, info.trace_state.length as usize),
            b"vendor=value"
        );
        assert_eq!(info.cancellation_status, NW_TIMEOUT);
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
    // SAFETY: the host joined its OS worker with every request/callback owner retired.
    unsafe {
        assert_eq!(nw_server_try_retire(server), NW_OK);
        nw_server_release(server);
    }
    let fields = lock(&seen.fields).join(";");
    assert!(fields.contains("parent_trace_id=4bf92f3577b34da6a3ce929d0e0e4736"));
    assert!(fields.contains("parent_span_id=00f067aa0ba902b7"));
    assert!(fields.contains("retirement_ns="));
    assert!(!fields.contains("SECRET"));
    assert!(seen.native_polls.load(Ordering::Relaxed) > 0);
    assert_eq!(
        seen.enters.load(Ordering::Relaxed),
        seen.exits.load(Ordering::Relaxed)
    );
    tracing::dispatcher::get_default(|active| {
        assert!(active.downcast_ref::<Subscriber>().is_none())
    });
}
