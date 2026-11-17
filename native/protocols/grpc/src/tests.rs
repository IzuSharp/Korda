use super::*;
use bytes::{BufMut, Bytes};
use http::{HeaderMap, HeaderValue, Request, Version};
use http_body_util::{BodyExt, Full};
use nativeweave_server_streaming::{Budget, Cancellation, Side};
use std::{
    sync::{
        Arc,
        atomic::{AtomicUsize, Ordering},
    },
    time::{Duration, Instant},
};
use tonic::{Code, Status};
fn descriptor(kind: MethodKind) -> Arc<Descriptor> {
    Arc::new(Descriptor {
        version: 1,
        kind: "grpc".into(),
        handler_id: 7,
        method: "/test.Echo/Call".into(),
        method_kind: kind,
        request_codec_id: 11,
        response_codec_id: 12,
        max_encoded_message_bytes: 1024,
        max_decoded_message_bytes: 1024,
        max_stream_bytes: 65_536,
        timeout_ns: 1_000_000_000,
        compression: "identity".into(),
    })
}
fn frame(messages: &[&[u8]]) -> Bytes {
    let mut bytes = Vec::new();
    for message in messages {
        bytes.push(0);
        bytes.put_u32(message.len() as u32);
        bytes.extend_from_slice(message);
    }
    bytes.into()
}
fn request(bytes: Bytes) -> Request<Full<Bytes>> {
    Request::builder()
        .version(Version::HTTP_2)
        .method("POST")
        .uri("/test.Echo/Call")
        .header("content-type", "application/grpc")
        .body(Full::new(bytes))
        .unwrap()
}
async fn collect(
    mut response: http::Response<tonic::body::Body>,
) -> (u32, Vec<Vec<u8>>, HeaderMap) {
    let mut status = response
        .headers()
        .get("grpc-status")
        .map(|v| v.to_str().unwrap().parse().unwrap())
        .unwrap_or(999);
    let mut payload = Vec::new();
    let mut trailers = HeaderMap::new();
    while let Some(value) = response.body_mut().frame().await {
        let value = value.unwrap();
        match value.into_data() {
            Ok(bytes) => payload.extend_from_slice(&bytes),
            Err(frame) => {
                trailers = frame.into_trailers().unwrap();
                status = trailers
                    .get("grpc-status")
                    .unwrap()
                    .to_str()
                    .unwrap()
                    .parse()
                    .unwrap();
            }
        }
    }
    let mut items = Vec::new();
    let mut cursor = 0;
    while cursor < payload.len() {
        assert_eq!(payload[cursor], 0);
        let n = u32::from_be_bytes(payload[cursor + 1..cursor + 5].try_into().unwrap()) as usize;
        cursor += 5;
        items.push(payload[cursor..cursor + n].to_vec());
        cursor += n;
    }
    (status, items, trailers)
}
async fn execute(kind: MethodKind, wire: Bytes) -> (u32, Vec<Vec<u8>>, usize) {
    let budget = Budget::new(1_048_576, 1_048_576).unwrap();
    let request_budget = budget.request(1_048_576).unwrap();
    let call = Call::new(descriptor(kind), request_budget);
    let count = Arc::new(AtomicUsize::new(0));
    let observed = count.clone();
    let dispatched = call.clone();
    let dispatch = Arc::new(move || {
        observed.fetch_add(1, Ordering::AcqRel);
        let call = dispatched.clone();
        tokio::spawn(async move {
            let cancel = Cancellation::default();
            let mut messages = Vec::new();
            loop {
                match call.read().await {
                    Ok(Some(value)) => messages.push(value.as_slice().to_vec()),
                    Ok(None) => break,
                    Err(status) => {
                        call.finish(
                            status,
                            ResponseMetadata::empty(&call.budget).unwrap(),
                            &cancel,
                        )
                        .await
                        .unwrap();
                        return;
                    }
                }
            }
            let replies = match kind {
                MethodKind::Unary => vec![messages.first().cloned().unwrap()],
                MethodKind::ServerStreaming => {
                    vec![messages[0].clone(), b"second".to_vec(), vec![]]
                }
                MethodKind::ClientStreaming => vec![messages.concat()],
                MethodKind::Duplex => messages,
            };
            for value in replies {
                let reservation = call.reserve(1024, &cancel).await.unwrap();
                call.write(reservation.prepare(&value)).unwrap();
            }
            call.finish(
                Status::ok(""),
                ResponseMetadata::empty(&call.budget).unwrap(),
                &cancel,
            )
            .await
            .unwrap();
        });
        Ok(())
    });
    let result = tokio::time::timeout(Duration::from_secs(2), async {
        collect(serve(request(wire), call.clone(), 64, dispatch).await).await
    })
    .await
    .unwrap();
    call.retire_input().await;
    call.release_metadata();
    let count = count.load(Ordering::Acquire);
    drop(call);
    tokio::task::yield_now().await;
    assert_eq!(budget.snapshot(), (0, 0));
    (result.0, result.1, count)
}
#[tokio::test]
async fn four_public_tonic_modes_and_empty_messages() {
    for kind in [
        MethodKind::Unary,
        MethodKind::ServerStreaming,
        MethodKind::ClientStreaming,
        MethodKind::Duplex,
    ] {
        let input = if kind.single_input() {
            frame(&[b"one"])
        } else {
            frame(&[b"one", b"", b"three"])
        };
        let (status, values, count) = execute(kind, input).await;
        assert_eq!(status, 0);
        assert_eq!(count, 1);
        match kind {
            MethodKind::Unary => assert_eq!(values, vec![b"one".to_vec()]),
            MethodKind::ServerStreaming => {
                assert_eq!(values, vec![b"one".to_vec(), b"second".to_vec(), vec![]])
            }
            MethodKind::ClientStreaming => assert_eq!(values, vec![b"onethree".to_vec()]),
            MethodKind::Duplex => {
                assert_eq!(values, vec![b"one".to_vec(), vec![], b"three".to_vec()])
            }
        }
    }
    assert_eq!(
        execute(MethodKind::Unary, frame(&[b""])).await,
        (0, vec![vec![]], 1)
    );
}
#[tokio::test]
async fn unary_allowance_is_fresh_and_precedes_business_dispatch() {
    for kind in [MethodKind::Unary, MethodKind::ServerStreaming] {
        assert_eq!(execute(kind, frame(&[])).await.0, 13);
        let (status, _, calls) = execute(kind, frame(&[b"a", b"b"])).await;
        assert_eq!((status, calls), (3, 0));
        let (status, _, calls) = execute(kind, frame(&[b"a"])).await;
        assert_eq!((status, calls), (0, 1));
        let (status, _, calls) = execute(kind, frame(&[b"", b""])).await;
        assert_eq!((status, calls), (3, 0));
    }
}
#[tokio::test]
async fn tonic_retains_framed_length_truncation_and_compression_errors() {
    let mut large = vec![0];
    large.put_u32(1025);
    assert_eq!(execute(MethodKind::Unary, large.into()).await.0, 11);
    assert_eq!(
        execute(MethodKind::Unary, Bytes::from_static(&[0, 0, 0, 0, 2, 8]))
            .await
            .0,
        13
    );
    assert_eq!(
        execute(MethodKind::Unary, Bytes::from_static(&[1, 0, 0, 0, 0]))
            .await
            .0,
        13
    );
    assert_eq!(
        execute(MethodKind::Unary, Bytes::from_static(&[2, 0, 0, 0, 0]))
            .await
            .0,
        13
    );
}
#[test]
fn strict_timeout_and_descriptor_integer_contract() {
    let now = Instant::now();
    let route = Duration::from_secs(30);
    let mut headers = HeaderMap::new();
    for valid in ["1H", "99999999H", "1M", "30S", "12m", "3u", "1n"] {
        headers.insert("grpc-timeout", valid.parse().unwrap());
        assert!(deadline(&headers, route, now).unwrap() <= now + route);
    }
    for invalid in ["", "1", "123456789S", "1s", "-1S", "1.0S", " 1S", "1S "] {
        headers.insert("grpc-timeout", HeaderValue::from_str(invalid).unwrap());
        assert_eq!(
            deadline(&headers, route, now).unwrap_err().code(),
            Code::InvalidArgument
        );
    }
    headers.insert("grpc-timeout", HeaderValue::from_static("0n"));
    assert_eq!(
        deadline(&headers, route, now).unwrap_err().code(),
        Code::DeadlineExceeded
    );
    headers.insert("grpc-timeout", HeaderValue::from_static("1S"));
    headers.append("grpc-timeout", HeaderValue::from_static("2S"));
    assert_eq!(
        deadline(&headers, route, now).unwrap_err().code(),
        Code::InvalidArgument
    );
    let json = r#"{"version":1,"kind":"grpc","handler_id":18446744073709551615,"method":"/test.Echo/Call","method_kind":"unary","request_codec_id":1,"response_codec_id":2,"max_encoded_message_bytes":1024,"max_decoded_message_bytes":1024,"max_stream_bytes":65536,"timeout_ns":1000000000,"compression":"identity"}"#;
    assert!(
        Descriptor::parse(
            json.as_bytes(),
            "/test.Echo/Call",
            u64::MAX,
            Duration::from_secs(1),
            65536
        )
        .is_ok()
    );
    for invalid in [
        json.replace("18446744073709551615", "18446744073709551616"),
        json.replace("18446744073709551615", "1.0"),
        json.replace("\"version\":1", "\"version\":1,\"version\":1"),
        json.replace("\"version\":1", "\"version\":1,\"unknown\":0"),
    ] {
        assert!(
            Descriptor::parse(
                invalid.as_bytes(),
                "/test.Echo/Call",
                u64::MAX,
                Duration::from_secs(1),
                65536
            )
            .is_err()
        );
    }
}
fn entries(bytes: &[u8]) -> Vec<(String, Vec<u8>)> {
    let count = u32::from_le_bytes(bytes[..4].try_into().unwrap()) as usize;
    let mut out = vec![];
    let mut offset = 4;
    for _ in 0..count {
        let n = u32::from_le_bytes(bytes[offset..offset + 4].try_into().unwrap()) as usize;
        let v = u32::from_le_bytes(bytes[offset + 4..offset + 8].try_into().unwrap()) as usize;
        offset += 8;
        out.push((
            String::from_utf8(bytes[offset..offset + n].to_vec()).unwrap(),
            bytes[offset + n..offset + n + v].to_vec(),
        ));
        offset += n + v;
    }
    assert_eq!(offset, bytes.len());
    out
}
#[test]
fn binary_metadata_projection_preserves_wire_and_split_order() {
    let global = Budget::new(1_048_576, 1_048_576).unwrap();
    let budget = global.request(1_048_576).unwrap();
    let mut headers = HeaderMap::new();
    headers.append("x-data-bin", HeaderValue::from_static("YQ==, \t, Yg"));
    headers.append("x-data-bin", HeaderValue::from_static("Yw=="));
    headers.insert("text", HeaderValue::from_static("ascii"));
    let value = project_metadata(&headers, 32768, &budget).unwrap();
    assert_eq!(
        headers.get_all("x-data-bin").iter().next().unwrap(),
        "YQ==, \t, Yg"
    );
    assert_eq!(
        entries(&value.decoded)
            .into_iter()
            .filter(|(n, _)| n == "x-data-bin")
            .map(|(_, v)| v)
            .collect::<Vec<_>>(),
        vec![b"a".to_vec(), vec![], b"b".to_vec(), b"c".to_vec()]
    );
    assert!(global.snapshot().0 > value.decoded.len() as u64);
    drop(value);
    assert_eq!(global.snapshot(), (0, 0));
    for invalid in ["Y Q==", "YQ=", "YQ===", "YQ==a", "YQ==,***"] {
        headers.insert("x-data-bin", HeaderValue::from_str(invalid).unwrap());
        assert!(project_metadata(&headers, 32768, &budget).is_err());
        assert_eq!(global.snapshot(), (0, 0));
    }
}
#[test]
fn metadata_count_byte_and_pregrant_failure_are_atomic() {
    let global = Budget::new(1_048_576, 1_048_576).unwrap();
    let budget = global.request(1_048_576).unwrap();
    let mut headers = HeaderMap::new();
    headers.insert("x-bin", HeaderValue::from_str(&",".repeat(199)).unwrap());
    let block = project_metadata(&headers, 32768, &budget).unwrap();
    assert_eq!(entries(&block.decoded).len(), 200);
    drop(block);
    headers.insert("x-bin", HeaderValue::from_str(&",".repeat(200)).unwrap());
    assert_eq!(
        project_metadata(&headers, 32768, &budget)
            .err()
            .unwrap()
            .code(),
        Code::ResourceExhausted
    );
    assert_eq!(global.snapshot(), (0, 0));
    headers.insert("x-bin", HeaderValue::from_static("YQ=="));
    assert!(project_metadata(&headers, 12, &budget).is_err());
    let held = budget.try_acquire(Side::Native, 1_048_500).unwrap();
    let before = global.snapshot();
    assert!(project_metadata(&headers, 32768, &budget).is_err());
    assert_eq!(global.snapshot(), before);
    drop(held);
    assert_eq!(global.snapshot(), (0, 0));
}
#[tokio::test]
async fn read_stop_is_sticky_and_preserves_output_and_unstarted_rollback() {
    let global = Budget::new(1_048_576, 1_048_576).unwrap();
    let budget = global.request(1_048_576).unwrap();
    let call = Call::new(descriptor(MethodKind::Duplex), budget);
    call.set_input(Inbound::One(Some(Bytes::from_static(b"ready"))))
        .await;
    call.stop_input();
    assert_eq!(call.read().await.err().unwrap().code(), Code::Cancelled);
    let cancel = Cancellation::default();
    let token = call.reserve(1024, &cancel).await.unwrap();
    assert_eq!(global.snapshot().0, 1029);
    let prepared = token.prepare(b"copied");
    drop(prepared);
    assert_eq!(global.snapshot(), (0, 0));
    assert!(call.reserve(1024, &cancel).await.is_ok());
    call.retire_input().await;
    call.release_metadata();
    drop(call);
    assert_eq!(global.snapshot(), (0, 0));
}

#[test]
fn escaping_header_and_trailer_values_hold_whole_metadata_credit() {
    let global = Budget::new(1_048_576, 1_048_576).unwrap();
    let budget = global.request(1_048_576).unwrap();
    let mut wire = HeaderMap::new();
    wire.insert("x-bin", HeaderValue::from_static("YQ=="));
    let request = project_metadata(&wire, 32768, &budget).unwrap();
    let anchor = request.reserved_capacity();
    let mut trailers = HeaderMap::new();
    trailers.insert("grpc-status", HeaderValue::from_static("4"));
    retain_headers(&mut trailers, request.credit());
    assert!(!request.unshared());
    let clone = trailers.clone();
    drop(trailers);
    drop(request);
    assert_eq!(global.snapshot(), (anchor, 0));
    assert_eq!(clone.get("grpc-status").unwrap(), "4");
    drop(clone);
    assert_eq!(global.snapshot(), (0, 0));
    let response = ResponseMetadata::empty(&budget).unwrap();
    let credit = response.credit().capacity();
    let mut headers = HeaderMap::new();
    headers.insert("content-type", HeaderValue::from_static("application/grpc"));
    retain_headers(&mut headers, response.credit());
    drop(response);
    assert_eq!(global.snapshot(), (credit, 0));
    drop(headers);
    assert_eq!(global.snapshot(), (0, 0));
}
#[tokio::test]
async fn payload_credit_never_waits_and_queue_wait_has_no_payload_grant() {
    let global = Budget::new(1_048_576, 1_048_576).unwrap();
    let budget = global.request(1_048_576).unwrap();
    let call = Call::new(descriptor(MethodKind::Duplex), budget.clone());
    let cancel = Cancellation::default();
    let held = call.reserve(1024, &cancel).await.unwrap();
    let before = global.snapshot();
    let other = Cancellation::default();
    let pending = call.reserve(1024, &other);
    tokio::pin!(pending);
    assert!(
        tokio::time::timeout(Duration::from_millis(5), &mut pending)
            .await
            .is_err()
    );
    assert_eq!(global.snapshot(), before);
    other.cancel();
    assert_eq!(pending.await.err().unwrap().code(), Code::Cancelled);
    assert_eq!(global.snapshot(), before);
    drop(held);
    let full = budget.try_acquire(Side::Native, 1_048_576).unwrap();
    assert_eq!(
        tokio::time::timeout(Duration::from_millis(20), call.reserve(1024, &cancel))
            .await
            .unwrap()
            .err()
            .unwrap()
            .code(),
        Code::ResourceExhausted
    );
    assert_eq!(global.snapshot(), (1_048_576, 0));
    drop(full);
    assert_eq!(global.snapshot(), (0, 0));
}

#[test]
fn admission_floor_includes_both_codec_buffers_and_fixed_header_projection() {
    assert_eq!(admission_floor(1024, 64, 8192), Some(30_218));
    assert_eq!(
        admission_floor(1024, 64, 65_536),
        admission_floor(1024, 64, 32_768)
    );
    assert_eq!(admission_floor(1, usize::MAX, 8192), None);
}
#[test]
fn response_metadata_reserves_actual_payload_and_detail_before_copy() {
    let global = Budget::new(4096, 4096).unwrap();
    let budget = global.request(4096).unwrap();
    let empty = 0u32.to_le_bytes();
    let metadata = ResponseMetadata::parse(&empty, 32_768, &budget, true, 0).unwrap();
    assert_eq!(global.snapshot(), (1056, 0));
    drop(metadata);
    assert_eq!(global.snapshot(), (0, 0));
    assert_eq!(
        ResponseMetadata::parse(&empty, 32_768, &budget, true, 4096)
            .err()
            .unwrap()
            .code(),
        Code::ResourceExhausted
    );
    assert_eq!(global.snapshot(), (0, 0));
}

#[tokio::test]
async fn escaping_framed_slice_holds_full_grant_after_response_and_call_retire() {
    let global = Budget::new(1_048_576, 1_048_576).unwrap();
    let budget = global.request(1_048_576).unwrap();
    let call = Call::new(descriptor(MethodKind::Unary), budget.clone());
    let dispatched = call.clone();
    let dispatch = Arc::new(move || {
        let call = dispatched.clone();
        tokio::spawn(async move {
            let cancel = Cancellation::default();
            let message = call.read().await.unwrap().unwrap();
            let reserved = call.reserve(1024, &cancel).await.unwrap();
            call.write(reserved.prepare(message.as_slice())).unwrap();
            drop(message);
            call.finish(
                Status::ok(""),
                ResponseMetadata::empty(&call.budget).unwrap(),
                &cancel,
            )
            .await
            .unwrap();
        });
        Ok(())
    });
    let mut response = serve(request(frame(&[b"small"])), call.clone(), 64, dispatch).await;
    let data = response
        .body_mut()
        .frame()
        .await
        .unwrap()
        .unwrap()
        .into_data()
        .unwrap();
    assert_eq!(
        data.as_ref(),
        &[0, 0, 0, 0, 5, b's', b'm', b'a', b'l', b'l']
    );
    let slice = data.slice(5..6);
    drop(data);
    drop(response);
    call.retire_input().await;
    call.release_metadata();
    budget.close();
    drop(call);
    assert_eq!(global.snapshot(), (1029, 0));
    assert_eq!(&slice[..], b"s");
    drop(slice);
    assert_eq!(global.snapshot(), (0, 0));
}
