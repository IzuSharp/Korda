use super::*;
const PARENT: &str = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
#[test]
fn fixed_context_golden_and_malformed_fields() {
    let c = trace_parent(PARENT).unwrap();
    let b = c.encode().unwrap();
    assert_eq!(&b[..8], &[32, 0, 0, 0, 1, 0, 1, 0]);
    assert_eq!(
        &b[8..24],
        &[
            0x4b, 0xf9, 0x2f, 0x35, 0x77, 0xb3, 0x4d, 0xa6, 0xa3, 0xce, 0x92, 0x9d, 0x0e, 0x0e,
            0x47, 0x36
        ]
    );
    assert_eq!(TraceContextV1::decode(&b), Some(c));
    assert_eq!(
        Hex(&b).to_string(),
        include_str!("../../../../tests/Server.Diagnostics/trace-golden.txt").trim()
    );
    for offset in [0, 4, 6, 7] {
        let mut malformed = b;
        malformed[offset] = 255;
        assert!(TraceContextV1::decode(&malformed).is_none());
    }
    assert!(TraceContextV1::decode(&b[..31]).is_none());
    let mut zero = b;
    zero[8..24].fill(0);
    assert!(TraceContextV1::decode(&zero).is_none());
}
#[test]
fn strict_w3c_values_and_no_baggage() {
    for p in [
        "ff-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
        "00-00000000000000000000000000000000-00f067aa0ba902b7-01",
        "00-4BF92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
    ] {
        assert!(trace_parent(p).is_none());
    }
    assert!(trace_state("vendor=value,tenant@system=other"));
    for value in [
        "vendor=secret,vendor=duplicate",
        "Upper=value",
        "vendor=a=b",
        "vendor=",
    ] {
        assert!(!trace_state(value));
    }
    let d = Diagnostics::new();
    let mut h = HeaderMap::new();
    h.insert("traceparent", PARENT.parse().unwrap());
    h.insert("baggage", "secret=credential".parse().unwrap());
    let captured = d.capture(&h);
    assert_eq!(captured.context, trace_parent(PARENT));
    assert!(captured.state.is_empty());
    h.append("traceparent", PARENT.parse().unwrap());
    assert!(d.capture(&h).context.is_none());
    assert_eq!(d.dropped(), 1);
}
#[test]
fn default_debug_and_release_install_nothing() {
    let d = Diagnostics::new();
    let request = d.request("/fixed/{id}", "http1", None);
    assert!(!request.enabled());
    assert!(request.context().is_none());
    assert_eq!(d.dropped(), 0);
}

#[test]
fn tracestate_field_combination_and_independent_validation() {
    let diagnostics = Diagnostics::new();
    let mut headers = HeaderMap::new();
    headers.insert("traceparent", PARENT.parse().unwrap());
    headers.append("tracestate", "one=first".parse().unwrap());
    headers.append("tracestate", " ,two=second".parse().unwrap());
    let captured = diagnostics.capture(&headers);
    assert_eq!(captured.context, trace_parent(PARENT));
    assert_eq!(captured.state, b"one=first, ,two=second");
    headers.insert("tracestate", "bad=one,bad=two".parse().unwrap());
    let captured = diagnostics.capture(&headers);
    assert_eq!(captured.context, trace_parent(PARENT));
    assert!(captured.state.is_empty());
    headers.insert(
        "tracestate",
        http::HeaderValue::from_bytes(b"bad=\xff").unwrap(),
    );
    assert!(diagnostics.capture(&headers).state.is_empty());
    assert_eq!(diagnostics.dropped(), 2);
    assert!(trace_state(",vendor=value,, "));
    assert!(!trace_state(&vec![" "; 33].join(",")));
    assert!(!trace_state(&format!("vendor={}", "x".repeat(257))));
    assert!(trace_parent("01-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-ff-").is_some());
}

thread_local! { static SPANS: std::cell::RefCell<Vec<u64>> = const { std::cell::RefCell::new(Vec::new()) }; }
#[derive(Default)]
struct Seen {
    threads: std::sync::Mutex<HashSet<std::thread::ThreadId>>,
    fields: std::sync::Mutex<Vec<String>>,
    entered: AtomicU64,
    exited: AtomicU64,
}
struct Observer(std::sync::Arc<Seen>);
struct Values<'a>(&'a Seen);
impl tracing::field::Visit for Values<'_> {
    fn record_debug(&mut self, field: &tracing::field::Field, value: &dyn std::fmt::Debug) {
        self.0
            .fields
            .lock()
            .unwrap()
            .push(format!("{}={value:?}", field.name()));
    }
}
impl tracing::Subscriber for Observer {
    fn enabled(&self, _: &tracing::Metadata<'_>) -> bool {
        true
    }
    fn new_span(&self, attributes: &tracing::span::Attributes<'_>) -> tracing::span::Id {
        attributes.record(&mut Values(&self.0));
        tracing::span::Id::from_u64(7)
    }
    fn record(&self, _: &tracing::span::Id, values: &tracing::span::Record<'_>) {
        values.record(&mut Values(&self.0));
    }
    fn record_follows_from(&self, _: &tracing::span::Id, _: &tracing::span::Id) {}
    fn event(&self, event: &tracing::Event<'_>) {
        event.record(&mut Values(&self.0));
    }
    fn enter(&self, id: &tracing::span::Id) {
        SPANS.with(|spans| spans.borrow_mut().push(id.into_u64()));
        self.0.entered.fetch_add(1, Ordering::Relaxed);
        self.0
            .threads
            .lock()
            .unwrap()
            .insert(std::thread::current().id());
    }
    fn exit(&self, id: &tracing::span::Id) {
        SPANS.with(|spans| assert_eq!(spans.borrow_mut().pop(), Some(id.into_u64())));
        self.0.exited.fetch_add(1, Ordering::Relaxed);
    }
}
#[test]
fn owned_dispatcher_and_span_follow_each_poll_across_workers() {
    use std::task::{Context, Poll, Waker};
    let seen = std::sync::Arc::new(Seen::default());
    let dispatcher = Dispatch::new(Observer(seen.clone()));
    let diagnostics = tracing::dispatcher::with_default(&dispatcher, Diagnostics::new);
    let request = diagnostics.request("/fixed/{id}", "HTTP/2", trace_parent(PARENT));
    assert!(request.enabled());
    let mut first = true;
    let future = RequestTrace::instrument(
        &request,
        std::future::poll_fn(move |_| {
            tracing::dispatcher::get_default(|active| {
                assert!(active.downcast_ref::<Observer>().is_some())
            });
            SPANS.with(|spans| assert_eq!(*spans.borrow(), [7]));
            if first {
                first = false;
                Poll::Pending
            } else {
                Poll::Ready(9)
            }
        }),
    );
    let mut future = Box::pin(future);
    let future = std::thread::spawn(move || {
        let mut context = Context::from_waker(Waker::noop());
        assert!(future.as_mut().poll(&mut context).is_pending());
        SPANS.with(|spans| assert!(spans.borrow().is_empty()));
        tracing::dispatcher::get_default(|active| {
            assert!(active.downcast_ref::<Observer>().is_none())
        });
        future
    })
    .join()
    .unwrap();
    std::thread::spawn(move || {
        let mut future = future;
        let mut context = Context::from_waker(Waker::noop());
        assert_eq!(future.as_mut().poll(&mut context), Poll::Ready(9));
        SPANS.with(|spans| assert!(spans.borrow().is_empty()));
        tracing::dispatcher::get_default(|active| {
            assert!(active.downcast_ref::<Observer>().is_none())
        });
    })
    .join()
    .unwrap();
    drop(request);
    assert_eq!(seen.threads.lock().unwrap().len(), 2);
    assert_eq!(
        seen.entered.load(Ordering::Relaxed),
        seen.exited.load(Ordering::Relaxed)
    );
    let fields = seen.fields.lock().unwrap().join(";");
    assert!(fields.contains("parent_trace_id=4bf92f3577b34da6a3ce929d0e0e4736"));
    assert!(fields.contains("parent_span_id=00f067aa0ba902b7"));
    assert!(fields.contains("retirement_ns="));
    assert!(!fields.contains("secret"));
}
