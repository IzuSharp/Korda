//! Direct Rust TCP/Hyper behavioral oracle using the same explicit proof ABI.
use crate::*;
use std::{
    ffi::c_void,
    sync::atomic::{AtomicU32, AtomicUsize, Ordering},
    time::{Duration, Instant},
};

struct Handler {
    host: AtomicUsize,
}
unsafe extern "C" fn handler(cookie: usize, kind: u32, payload: *const c_void, length: u64) -> u32 {
    if kind != 1 || length != 12 {
        return NW_CALLBACK_FAILED;
    }
    // SAFETY: the caller keeps this Handler and 12-byte callback borrow live until return.
    let (context, data) = unsafe {
        (
            &*(cookie as *const Handler),
            std::slice::from_raw_parts(payload.cast::<u8>(), 12),
        )
    };
    let id = u64::from_le_bytes(data[..8].try_into().expect("fixed id"));
    let route = u32::from_le_bytes(data[8..].try_into().expect("fixed route"));
    let host = context.host.load(Ordering::SeqCst) as *const NwHttpHost;
    // SAFETY: the native subscription source and owning oracle retain the host.
    if unsafe { nw_asyncproof_handler_claim_v1(host, id) } != NW_OK {
        return NW_OK;
    }
    let text = if route == 99 {
        String::new()
    } else {
        format!("route:{route}:native-http-body")
    };
    let status = if route == 99 { 500 } else { 200 };
    // SAFETY: bounded call-borrowed UTF8 and active claimed handler control.
    let result = unsafe {
        nw_asyncproof_handler_reply_v1(host, id, status, text.as_ptr(), text.len() as u64, 5)
    };
    // SAFETY: exact claimed handler acknowledgement after its last host access.
    unsafe { nw_asyncproof_handler_ack_v1(host, id) };
    result
}
unsafe extern "C" fn terminal(cookie: usize, status: u32, _: *const NwErrorInfoV1) -> u32 {
    // SAFETY: boxed notice outlives the native retirement barrier below.
    unsafe { &*(cookie as *const AtomicU32) }.store(status, Ordering::SeqCst);
    NW_OK
}
fn wait(mut ready: impl FnMut() -> bool) {
    let deadline = Instant::now() + Duration::from_secs(10);
    while !ready() {
        assert!(Instant::now() < deadline, "native oracle timed out");
        std::thread::sleep(Duration::from_millis(1));
    }
}
unsafe fn completed(operation: *mut NwHttpOperation) {
    let notice = Box::new(AtomicU32::new(u32::MAX));
    let callbacks = NwOperationCallbacksV1 {
        struct_size: 24,
        flags: 0,
        cookie: &*notice as *const _ as usize,
        on_terminal: Some(terminal),
    };
    assert_eq!(
        // SAFETY: exact created operation; callback cookie remains boxed through native quiet.
        unsafe { nw_asyncproof_operation_start_v1(operation, &callbacks) },
        NW_OK
    );
    wait(|| notice.load(Ordering::SeqCst) != u32::MAX);
    assert_eq!(notice.load(Ordering::SeqCst), NW_OK);
    // SAFETY: unique live operation, retained until the caller takes/releases it.
    wait(|| unsafe { nw_asyncproof_operation_retire_v1(operation) } == NW_OK);
}

/// Executes actual loopback HTTP with a direct native Handler and retains every
/// received Bytes chunk across response/host retirement before comparing bytes.
pub fn run(route: u32) -> (u32, Vec<u8>, usize) {
    let limits = NwHttpLimitsV1 {
        struct_size: 24,
        flags: 0,
        streams: 2,
        operations: 8,
        retained_bytes: 16384,
        chunk_bytes: 4096,
    };
    let mut host = std::ptr::null_mut();
    assert_eq!(
        // SAFETY: valid fixed profile and exclusive owner output.
        unsafe { nw_asyncproof_host_create_v1(&limits, &mut host) },
        NW_OK
    );
    let context = Handler {
        host: AtomicUsize::new(host as usize),
    };
    let callbacks = NwCallbacksV1 {
        struct_size: 24,
        flags: 0,
        cookie: &context as *const _ as usize,
        on_event: Some(handler),
    };
    assert_eq!(
        // SAFETY: rooted direct Handler remains live through host retirement.
        unsafe { nw_asyncproof_host_start_v1(host, &callbacks) },
        NW_OK
    );
    let mut operation = std::ptr::null_mut();
    assert_eq!(
        // SAFETY: admitted live host and exclusive operation output.
        unsafe { nw_asyncproof_get_create_v1(host, route, &mut operation) },
        NW_OK
    );
    // SAFETY: exact created owner; callback/retirement controlled above.
    unsafe { completed(operation) };
    let mut response = std::ptr::null_mut();
    let mut status = 0;
    assert_eq!(
        // SAFETY: successful terminal operation and exclusive typed take outputs.
        unsafe { nw_asyncproof_take_response_v1(operation, &mut response, &mut status) },
        NW_OK
    );
    // SAFETY: native quiet and result ownership has moved independently.
    unsafe { nw_asyncproof_operation_release_v1(operation) };
    let mut chunks = Vec::new();
    loop {
        assert_eq!(
            // SAFETY: response owner remains live; output receives a distinct read owner.
            unsafe { nw_asyncproof_read_create_v1(response, &mut operation) },
            NW_OK
        );
        // SAFETY: exact created read owner retained through callback/native quiet.
        unsafe { completed(operation) };
        let mut chunk = std::ptr::null_mut();
        let mut data = std::ptr::null();
        let mut length = 0;
        let mut end = 0;
        assert_eq!(
            // SAFETY: complete read, mutually exclusive initialized output slots.
            unsafe {
                nw_asyncproof_take_chunk_v1(operation, &mut chunk, &mut data, &mut length, &mut end)
            },
            NW_OK
        );
        // SAFETY: result processing ended and native operation retired.
        unsafe { nw_asyncproof_operation_release_v1(operation) };
        if end != 0 {
            assert!(!chunk.is_null());
            // SAFETY: exact empty EOF owner transferred by the successful typed take.
            unsafe { nw_asyncproof_chunk_release_v1(chunk) };
            break;
        }
        chunks.push((chunk, data, length));
    }
    // SAFETY: no live read operations remain; chunk owners are independent.
    unsafe {
        nw_asyncproof_response_release_v1(response);
        nw_asyncproof_host_stop_v1(host)
    };
    // SAFETY: live host retained until its OS join and callback source quiet.
    wait(|| unsafe { nw_asyncproof_host_retire_v1(host) } == NW_OK);
    assert_eq!(count(5), 0);
    assert_eq!(count(6), 0);
    assert_eq!(count(7), 0);
    if !chunks.is_empty() {
        assert!(count(4) > 0, "held backing Bytes lost their retained quota");
    }
    // SAFETY: host/source/callback ownership has retired; chunks own their backing separately.
    unsafe { nw_asyncproof_host_release_v1(host) };
    let mut bytes = Vec::new();
    let chunk_count = chunks.len();
    for (owner, data, length) in chunks {
        // SAFETY: owner still holds this exact immutable backing range after host release.
        bytes.extend_from_slice(unsafe { std::slice::from_raw_parts(data, length as usize) });
        // SAFETY: unique creating-module chunk, no remaining borrowed read.
        unsafe { nw_asyncproof_chunk_release_v1(owner) };
    }
    for index in [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 14, 15] {
        assert_eq!(count(index), 0, "counter {index}");
    }
    (status, bytes, chunk_count)
}

#[cfg(test)]
pub(crate) fn headers_close_race() {
    let limits = NwHttpLimitsV1 {
        struct_size: 24,
        flags: 0,
        streams: 2,
        operations: 8,
        retained_bytes: 16384,
        chunk_bytes: 4096,
    };
    let mut host = std::ptr::null_mut();
    // SAFETY: fixed valid limits and exclusive owner output.
    let created = unsafe { nw_asyncproof_host_create_v1(&limits, &mut host) };
    assert_eq!(created, NW_OK);
    let context = Handler {
        host: AtomicUsize::new(host as usize),
    };
    let callbacks = NwCallbacksV1 {
        struct_size: 24,
        flags: 0,
        cookie: &context as *const _ as usize,
        on_event: Some(handler),
    };
    // SAFETY: exact live owner. A test-only barrier closes admission after real headers arrive.
    let value = unsafe { &*host };
    value
        .state
        .close_after_headers
        .store(true, Ordering::Release);
    assert_eq!(
        // SAFETY: context/table retained until OS-worker join and native quiet.
        unsafe { nw_asyncproof_host_start_v1(host, &callbacks) },
        NW_OK
    );
    let mut operation = std::ptr::null_mut();
    assert_eq!(
        // SAFETY: exact admitted host and exclusive created operation output.
        unsafe { nw_asyncproof_get_create_v1(host, 7, &mut operation) },
        NW_OK
    );
    let notice = Box::new(AtomicU32::new(u32::MAX));
    let terminal_table = NwOperationCallbacksV1 {
        struct_size: 24,
        flags: 0,
        cookie: &*notice as *const _ as usize,
        on_terminal: Some(terminal),
    };
    assert_eq!(
        // SAFETY: static callback and stable cookie kept until native operation retirement.
        unsafe { nw_asyncproof_operation_start_v1(operation, &terminal_table) },
        NW_OK
    );
    wait(|| notice.load(Ordering::Acquire) != u32::MAX);
    assert_eq!(
        notice.load(Ordering::Acquire),
        NW_CANCELED,
        "closed publication gate must reject the newly received Body"
    );
    // SAFETY: both native owners remain uniquely held through their retirement acknowledgements.
    wait(|| unsafe { nw_asyncproof_operation_retire_v1(operation) } == NW_OK);
    // SAFETY: result-free canceled operation has retired.
    unsafe { nw_asyncproof_operation_release_v1(operation) };
    // SAFETY: host close was committed at the header boundary and owner remains live.
    wait(|| unsafe { nw_asyncproof_host_retire_v1(host) } == NW_OK);
    assert_eq!(
        count(7),
        0,
        "no Body may be inserted behind the close drain"
    );
    // SAFETY: all sources/callbacks and actual native worker are joined.
    unsafe { nw_asyncproof_host_release_v1(host) };
    for index in [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 14, 15] {
        assert_eq!(count(index), 0);
    }
}
