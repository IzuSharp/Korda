use super::*;
use nativeweave_interop_abi::{NW_CANCELED, NW_OK, NwErrorInfoV1, NwOperationCallbacksV1};
use nativeweave_interop_async::Operation;
use nativeweave_interop_errors::ErrorFields;
use std::future::poll_fn;
use std::time::Duration;

fn request(bytes: u64) -> (Arc<Budget>, Arc<RequestBudget>) {
    let global = Budget::new(bytes, bytes).unwrap();
    let request = global.request(bytes).unwrap();
    (global, request)
}
async fn frame(body: &mut BoundedBody) -> Option<Result<Frame<Bytes>, StreamError>> {
    poll_fn(|cx| Pin::new(&mut *body).poll_frame(cx)).await
}

#[tokio::test(flavor = "current_thread")]
async fn prepared_formatter_credit_survives_last_nonempty_slice() {
    let (global, request) = request(128);
    let (sender, mut body) = body_channel(request.clone(), 64, 1).unwrap();
    let cancellation = Cancellation::default();
    let reservation = sender.reserve(64, &cancellation).await.unwrap();
    // The pregrant precedes a known 32-byte allocation with no growth or alias.
    let mut formatting = bytes::BytesMut::with_capacity(32);
    formatting.extend_from_slice(b"formatted");
    // SAFETY: the 64-byte grant covers the complete 32-byte formatter backing;
    // freeze allocates no second payload, and there are no pre-adoption aliases.
    let prepared = unsafe { reservation.prepare_admitted(formatting.freeze()) }.unwrap();
    request.close();
    body.close();
    assert_eq!(global.snapshot(), (64, 0));
    prepared.commit();
    let output = frame(&mut body)
        .await
        .unwrap()
        .unwrap()
        .into_data()
        .unwrap();
    let retained = output.slice(1..2);
    drop((body, output));
    assert_eq!(&retained[..], b"o");
    assert_eq!(global.snapshot(), (64, 0));
    drop(retained);
    assert_eq!(global.snapshot(), (0, 0));
}

#[tokio::test(flavor = "current_thread")]
async fn prepared_write_unstarted_drop_rolls_back_queue_and_payload() {
    let (global, request) = request(64);
    let (sender, mut body) = body_channel(request, 32, 1).unwrap();
    let cancel = Cancellation::default();
    let prepared = sender
        .reserve(32, &cancel)
        .await
        .unwrap()
        .prepare(b"copied")
        .unwrap();
    assert_eq!(sender.queue_capacity(), 0);
    assert_eq!(global.snapshot(), (32, 0));
    assert!(
        tokio::time::timeout(Duration::from_millis(2), frame(&mut body))
            .await
            .is_err()
    );
    drop(prepared);
    assert_eq!(sender.queue_capacity(), 1);
    assert_eq!(global.snapshot(), (0, 0));
}

#[tokio::test(flavor = "current_thread")]
async fn prepared_write_survives_close_and_preserves_owned_input() {
    let (global, request) = request(64);
    let (sender, mut body) = body_channel(request.clone(), 32, 1).unwrap();
    let cancel = Cancellation::default();
    let mut caller = b"copied".to_vec();
    let prepared = sender
        .reserve(32, &cancel)
        .await
        .unwrap()
        .prepare(&caller)
        .unwrap();
    caller.fill(b'x');
    request.close();
    body.close();
    prepared.commit();
    let retained = frame(&mut body)
        .await
        .unwrap()
        .unwrap()
        .into_data()
        .unwrap();
    assert_eq!(&retained[..], b"copied");
    drop(body);
    assert_eq!(global.snapshot(), (32, 0));
    drop(retained);
    assert_eq!(global.snapshot(), (0, 0));
}

#[test]
fn all_or_none_across_two_requests_and_sides() {
    let budget = Budget::new(16, 12).unwrap();
    let first = budget.request(12).unwrap();
    let second = budget.request(12).unwrap();
    let a = first.try_acquire(Side::Native, 12).unwrap();
    assert!(matches!(
        second.try_acquire(Side::Native, 5),
        Err(StreamError::ResourceLimit)
    ));
    assert_eq!(second.snapshot(), (0, 0));
    assert_eq!(budget.snapshot(), (12, 0));
    let b = second.try_acquire(Side::Native, 4).unwrap();
    let c = second.try_acquire(Side::Managed, 12).unwrap();
    assert!(matches!(
        first.try_acquire(Side::Managed, 1),
        Err(StreamError::ResourceLimit)
    ));
    assert_eq!(first.snapshot(), (12, 0));
    drop((a, b, c));
    assert_eq!(budget.snapshot(), (0, 0));
}

#[test]
fn slice_and_clone_hold_full_backing_until_last_drop() {
    let (global, request) = request(128);
    let original = request
        .reserve_payload(64)
        .unwrap()
        .copy(b"payload")
        .unwrap();
    let clone = original.clone();
    let slice = clone.slice(1, 1).unwrap();
    assert_eq!(slice.as_slice(), b"a");
    assert_eq!(slice.backing_capacity(), 64);
    drop((original, clone));
    assert_eq!(global.snapshot(), (64, 0));
    drop(slice);
    assert_eq!(global.snapshot(), (0, 0));
}

#[test]
fn taken_buffer_survives_closed_request_and_managed_copy_overlap() {
    let (global, request) = request(64);
    let buffer = request.reserve_payload(32).unwrap().copy(b"chunk").unwrap();
    request.close();
    assert!(matches!(
        request.reserve_payload(1),
        Err(StreamError::Closed)
    ));
    let copy_credit = buffer.acquire_managed(32).unwrap();
    assert_eq!(global.snapshot(), (32, 32));
    let output = buffer.as_slice().to_vec();
    drop(copy_credit);
    assert_eq!(global.snapshot(), (32, 0));
    drop((request, buffer));
    assert_eq!(global.snapshot(), (0, 0));
    assert_eq!(output, b"chunk");
}

#[test]
fn zero_huge_invalid_and_failed_copy_leave_quotas_balanced() {
    let (global, request) = request(32);
    assert!(matches!(
        request.reserve_payload(0),
        Err(StreamError::InvalidArgument)
    ));
    assert!(matches!(
        request.reserve_payload(usize::MAX),
        Err(StreamError::ResourceLimit)
    ));
    assert!(matches!(
        request.reserve_payload(4).unwrap().copy(b"longer"),
        Err(StreamError::InvalidArgument)
    ));
    assert!(matches!(
        request.reserve_payload(4).unwrap().copy(b""),
        Err(StreamError::InvalidArgument)
    ));
    assert_eq!(global.snapshot(), (0, 0));
}

#[tokio::test]
async fn full_retained_input_rejects_output_without_payload_waiting() {
    let cap = 4 * 1024 * 1024;
    let (global, request) = request(cap);
    let mut retained = Vec::with_capacity(128);
    for _ in 0..128 {
        retained.push(
            request
                .reserve_payload(32_768)
                .unwrap()
                .copy(&[7; 32_768])
                .unwrap(),
        );
    }
    let (sender, _body) = body_channel(Arc::clone(&request), 32_768, 2).unwrap();
    let cancel = Cancellation::default();
    let output = tokio::time::timeout(Duration::from_millis(100), sender.reserve(1, &cancel))
        .await
        .unwrap();
    assert!(matches!(output, Err(StreamError::ResourceLimit)));
    assert_eq!(sender.queue_capacity(), 2);
    assert_eq!(global.snapshot(), (cap, 0));
    drop(retained);
    let grant = sender.reserve(1, &cancel).await.unwrap();
    drop(grant);
    assert_eq!(global.snapshot(), (0, 0));
}

#[tokio::test]
async fn queue_wait_owns_no_payload_and_cancel_restores_metadata() {
    let (global, request) = request(64);
    let (sender, mut body) = body_channel(request, 32, 1).unwrap();
    let live = Cancellation::default();
    sender
        .reserve(8, &live)
        .await
        .unwrap()
        .commit(b"data")
        .unwrap();
    let cancel = Arc::new(Cancellation::default());
    let other = sender.clone();
    let signal = Arc::clone(&cancel);
    let wait = tokio::spawn(async move { other.reserve(32, &signal).await });
    tokio::task::yield_now().await;
    assert_eq!(global.snapshot(), (8, 0));
    cancel.cancel();
    assert!(matches!(wait.await.unwrap(), Err(StreamError::Canceled)));
    let bytes = frame(&mut body)
        .await
        .unwrap()
        .unwrap()
        .into_data()
        .unwrap();
    assert_eq!(&bytes[..], b"data");
    assert_eq!(sender.queue_capacity(), 1);
    assert_eq!(global.snapshot(), (8, 0));
    drop(bytes);
    assert_eq!(global.snapshot(), (0, 0));
}

#[tokio::test]
async fn native_credit_rejection_releases_issued_slot() {
    let (global, request) = request(8);
    let retained = request
        .reserve_payload(8)
        .unwrap()
        .copy(b"12345678")
        .unwrap();
    let (sender, _body) = body_channel(request, 8, 1).unwrap();
    assert!(matches!(
        sender.reserve(1, &Cancellation::default()).await,
        Err(StreamError::ResourceLimit)
    ));
    assert_eq!(sender.queue_capacity(), 1);
    drop(retained);
    assert_eq!(global.snapshot(), (0, 0));
}

#[tokio::test]
async fn issued_permit_survives_receiver_close_and_is_accounted() {
    let (global, request) = request(32);
    let (sender, mut body) = body_channel(request, 16, 1).unwrap();
    let reservation = sender.reserve(16, &Cancellation::default()).await.unwrap();
    body.close();
    reservation.commit(b"after close").unwrap();
    let bytes = frame(&mut body)
        .await
        .unwrap()
        .unwrap()
        .into_data()
        .unwrap();
    assert_eq!(&bytes[..], b"after close");
    assert!(frame(&mut body).await.is_none());
    assert_eq!(global.snapshot(), (16, 0));
    drop(bytes);
    assert_eq!(global.snapshot(), (0, 0));
}

#[tokio::test]
async fn receiver_drop_frees_queued_but_not_taken_bytes() {
    let (global, request) = request(64);
    let (sender, mut body) = body_channel(request, 16, 2).unwrap();
    let cancel = Cancellation::default();
    sender
        .reserve(16, &cancel)
        .await
        .unwrap()
        .commit(b"taken")
        .unwrap();
    sender
        .reserve(16, &cancel)
        .await
        .unwrap()
        .commit(b"queued")
        .unwrap();
    let retained = frame(&mut body)
        .await
        .unwrap()
        .unwrap()
        .into_data()
        .unwrap();
    drop(body);
    assert_eq!(global.snapshot(), (16, 0));
    drop(retained);
    assert_eq!(global.snapshot(), (0, 0));
}

#[tokio::test]
async fn body_end_is_not_last_owner_retirement() {
    let (global, request) = request(64);
    let (sender, mut body) = body_channel(request, 16, 3).unwrap();
    let cancel = Cancellation::default();
    sender
        .reserve(16, &cancel)
        .await
        .unwrap()
        .commit(b"retained")
        .unwrap();
    let mut trailers = HeaderMap::new();
    trailers.insert("x-trailer", "done".parse().unwrap());
    sender.finish(Some(trailers), &cancel).await.unwrap();
    let retained = frame(&mut body)
        .await
        .unwrap()
        .unwrap()
        .into_data()
        .unwrap();
    assert_eq!(
        frame(&mut body)
            .await
            .unwrap()
            .unwrap()
            .into_trailers()
            .unwrap()["x-trailer"],
        "done"
    );
    assert!(frame(&mut body).await.is_none());
    assert!(body.is_end_stream());
    drop((sender, body));
    assert_eq!(global.snapshot(), (16, 0));
    drop(retained);
    assert_eq!(global.snapshot(), (0, 0));
}

#[tokio::test]
async fn cancel_before_reserve_and_drop_before_commit_are_balanced() {
    let (global, request) = request(64);
    let (sender, _body) = body_channel(request, 16, 1).unwrap();
    let canceled = Cancellation::default();
    canceled.cancel();
    assert!(matches!(
        sender.reserve(16, &canceled).await,
        Err(StreamError::Canceled)
    ));
    assert_eq!(global.snapshot(), (0, 0));
    let grant = sender.reserve(16, &Cancellation::default()).await.unwrap();
    assert_eq!(global.snapshot(), (16, 0));
    drop(grant);
    assert_eq!(global.snapshot(), (0, 0));
    assert_eq!(sender.queue_capacity(), 1);
}

#[test]
fn concurrent_request_global_grants_never_exceed_limits() {
    let global = Budget::new(256, 256).unwrap();
    let workers: Vec<_> = (0..8)
        .map(|_| {
            let global = Arc::clone(&global);
            std::thread::spawn(move || {
                let request = global.request(64).unwrap();
                for _ in 0..1_000 {
                    if let Ok(grant) = request.try_acquire(Side::Native, 32) {
                        assert!(global.snapshot().0 <= 256);
                        assert!(request.snapshot().0 <= 64);
                        drop(grant);
                    }
                }
            })
        })
        .collect();
    for worker in workers {
        worker.join().unwrap();
    }
    assert_eq!(global.snapshot(), (0, 0));
}

#[tokio::test]
async fn managed_overlap_denial_returns_native_grant_and_queue_slot() {
    let global = Budget::new(64, 4).unwrap();
    let request = global.request(64).unwrap();
    let (sender, _body) = body_channel(Arc::clone(&request), 32, 1).unwrap();
    let native = sender.reserve(16, &Cancellation::default()).await.unwrap();
    assert!(matches!(
        request.try_acquire(Side::Managed, 16),
        Err(StreamError::ResourceLimit)
    ));
    drop(native);
    assert_eq!(sender.queue_capacity(), 1);
    assert_eq!(global.snapshot(), (0, 0));
}

#[tokio::test]
async fn real_body_reader_bounds_slices_and_preserves_retained_backing() {
    let (transport_budget, transport_request) = request(128);
    let (application_budget, application_request) = request(64);
    let (sender, body) = body_channel(transport_request, 32, 2).unwrap();
    let cancel = Cancellation::default();
    sender
        .reserve(16, &cancel)
        .await
        .unwrap()
        .commit(b"abcdefgh")
        .unwrap();
    sender.finish(None, &cancel).await.unwrap();
    assert!(matches!(
        sender.reserve(1, &cancel).await,
        Err(StreamError::Closed)
    ));
    assert!(matches!(
        sender.finish(None, &cancel).await,
        Err(StreamError::Closed)
    ));
    let mut reader = BodyReader::new(body, application_request, 32).unwrap();
    let ReadFrame::Data(first) = reader.read(3, &cancel).await.unwrap() else {
        panic!("Missing data")
    };
    assert_eq!(first.as_slice(), b"abc");
    assert_eq!(application_budget.snapshot(), (32, 0));
    assert_eq!(transport_budget.snapshot(), (0, 0));
    let ReadFrame::Data(second) = reader.read(32, &cancel).await.unwrap() else {
        panic!("Missing remainder")
    };
    assert_eq!(second.as_slice(), b"defgh");
    assert_eq!(application_budget.snapshot(), (32, 0));
    assert!(matches!(
        reader.read(32, &cancel).await.unwrap(),
        ReadFrame::End(None)
    ));
    assert!(reader.end_observed());
    drop((reader, first));
    assert_eq!(application_budget.snapshot(), (32, 0));
    drop(second);
    assert_eq!(application_budget.snapshot(), (0, 0));
}

#[tokio::test]
async fn pending_network_read_cancel_returns_reserved_capacity() {
    let (_, transport_request) = request(64);
    let (application_budget, application_request) = request(64);
    let (_sender, body) = body_channel(transport_request, 32, 1).unwrap();
    let mut reader = BodyReader::new(body, application_request, 32).unwrap();
    let cancel = Arc::new(Cancellation::default());
    let signal = Arc::clone(&cancel);
    let waiting = tokio::spawn(async move { reader.read(32, &signal).await });
    tokio::task::yield_now().await;
    assert_eq!(application_budget.snapshot(), (32, 0));
    cancel.cancel();
    assert!(matches!(waiting.await.unwrap(), Err(StreamError::Canceled)));
    assert_eq!(application_budget.snapshot(), (0, 0));
}

unsafe extern "C" fn terminal(_cookie: usize, _status: u32, _error: *const NwErrorInfoV1) -> u32 {
    NW_OK
}

#[test]
fn core_operation_cancel_never_started_untaken_and_taken_results() {
    let (global, request) = request(64);
    let callbacks = NwOperationCallbacksV1 {
        struct_size: size_of::<NwOperationCallbacksV1>() as u32,
        flags: 0,
        cookie: 1,
        on_terminal: Some(terminal),
    };
    let never_started = request.reserve_payload(16).unwrap();
    drop(never_started);
    assert_eq!(global.snapshot(), (0, 0));
    for status in [NW_OK, NW_CANCELED] {
        let operation = Operation::new();
        let buffer = request
            .reserve_payload(16)
            .unwrap()
            .copy(b"pending")
            .unwrap();
        assert_eq!(operation.start(callbacks), NW_OK);
        // SAFETY: the callback is fixed, nonthrowing and has no borrowed external state.
        assert_eq!(
            // SAFETY: the callback is fixed, nonthrowing and has no borrowed external state.
            unsafe { operation.complete(status, Some(buffer), ErrorFields::default()) },
            NW_OK
        );
        assert_eq!(operation.try_retire(), NW_OK);
        drop(operation); // an untaken owned result returns its credit on real destruction
        assert_eq!(global.snapshot(), (0, 0));
    }
    let operation = Operation::new();
    let buffer = request.reserve_payload(16).unwrap().copy(b"taken").unwrap();
    assert_eq!(operation.start(callbacks), NW_OK);
    // SAFETY: fixed callback has no external lifetime obligations.
    assert_eq!(
        // SAFETY: fixed callback has no external lifetime obligations.
        unsafe { operation.complete(NW_OK, Some(buffer), ErrorFields::default()) },
        NW_OK
    );
    let taken = operation.take_result().unwrap();
    assert_eq!(operation.try_retire(), NW_OK);
    drop(operation);
    request.close();
    assert_eq!(global.snapshot(), (16, 0));
    drop(taken);
    assert_eq!(global.snapshot(), (0, 0));
}

#[tokio::test(flavor = "current_thread")]
async fn flush_waits_for_queue_progress_without_releasing_retained_credit() {
    let (global, request) = request(32);
    let (sender, mut body) = body_channel(request, 32, 1).unwrap();
    let cancel = Cancellation::default();
    sender
        .reserve(32, &cancel)
        .await
        .unwrap()
        .commit(b"retained")
        .unwrap();
    let mut flushing = Box::pin(sender.flush(&cancel));
    assert!(
        tokio::time::timeout(Duration::from_millis(1), &mut flushing)
            .await
            .is_err()
    );
    assert_eq!(global.snapshot(), (32, 0));
    let retained = frame(&mut body)
        .await
        .unwrap()
        .unwrap()
        .into_data()
        .unwrap();
    flushing.await.unwrap();
    assert_eq!(global.snapshot(), (32, 0));
    assert_eq!(sender.queue_capacity(), 1);
    drop(retained);
    assert_eq!(global.snapshot(), (0, 0));
}
#[tokio::test(flavor = "current_thread")]
async fn flush_cancellation_rolls_back_metadata_wait_only() {
    let (global, request) = request(32);
    let (sender, mut body) = body_channel(request, 32, 1).unwrap();
    let cancel = Cancellation::default();
    sender
        .reserve(32, &cancel)
        .await
        .unwrap()
        .commit(b"retained")
        .unwrap();
    let mut flushing = Box::pin(sender.flush(&cancel));
    assert!(
        tokio::time::timeout(Duration::from_millis(1), &mut flushing)
            .await
            .is_err()
    );
    cancel.cancel();
    assert!(matches!(flushing.await, Err(StreamError::Canceled)));
    assert_eq!(global.snapshot(), (32, 0));
    drop(frame(&mut body).await);
    assert_eq!(global.snapshot(), (0, 0));
}
