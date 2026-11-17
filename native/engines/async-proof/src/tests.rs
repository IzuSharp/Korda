static CASE_GATE: std::sync::Mutex<()> = std::sync::Mutex::new(());

#[test]
fn actual_tcp_service_body_and_retained_bytes_native_oracle() {
    let _case = CASE_GATE.lock().unwrap();
    let (status, bytes, chunks) = crate::oracle::run(7);
    assert_eq!(status, 200);
    assert_eq!(bytes, b"route:7:native-http-body");
    assert!(chunks >= 2);
    let (status, bytes, _) = crate::oracle::run(99);
    assert_eq!(status, 500);
    assert!(bytes.is_empty());
}

#[test]
fn actual_headers_close_publication_race() {
    let _case = CASE_GATE.lock().unwrap();
    crate::oracle::headers_close_race();
}

// Execute this filter in its own process: quarantine intentionally retains an
// unknown panic payload and its host. It is not a normal cleanup success row.
#[test]
fn worker_panic_payload_retained_without_foreign_unwind() {
    if std::env::var_os("NATIVEWEAVE_HTTP_PANIC_CHILD").is_none() {
        let output = std::process::Command::new(std::env::current_exe().unwrap())
            .args([
                "--exact",
                "tests::worker_panic_payload_retained_without_foreign_unwind",
                "--nocapture",
            ])
            .env("NATIVEWEAVE_HTTP_PANIC_CHILD", "1")
            .output()
            .unwrap();
        assert!(
            output.status.success(),
            "isolated panic probe failed: {}",
            String::from_utf8_lossy(&output.stderr)
        );
        println!("{}", String::from_utf8_lossy(&output.stdout));
        return;
    }
    use crate::*;
    struct PanickingDrop;
    impl Drop for PanickingDrop {
        fn drop(&mut self) {
            panic!("unknown payload Drop must never execute");
        }
    }
    let limits = NwHttpLimitsV1 {
        struct_size: 24,
        flags: 0,
        streams: 1,
        operations: 1,
        retained_bytes: 4096,
        chunk_bytes: 4096,
    };
    let mut host = std::ptr::null_mut();
    assert_eq!(
        // SAFETY: complete fixed limits and exclusive output.
        unsafe { nw_asyncproof_host_create_v1(&limits, &mut host) },
        NW_OK
    );
    // SAFETY: this isolated test uniquely owns the host; no real networking source was started.
    let value = unsafe { &*host };
    *lock(&value.worker) = Some(std::thread::spawn(|| std::panic::panic_any(PanickingDrop)));
    value.state.close();
    while !lock(&value.worker).as_ref().unwrap().is_finished() {
        std::thread::yield_now();
    }
    assert_eq!(
        // SAFETY: exact live host and finished worker, exercising the actual C cleanup export.
        unsafe { nw_asyncproof_host_retire_v1(host) },
        NW_NATIVE_PANIC
    );
    assert!(value.state.boundary.is_poisoned());
    assert_eq!(value.state.boundary.quarantined_payloads(), 1);
    assert_eq!(
        // SAFETY: repeated cleanup keeps the explicit quarantine outcome sticky.
        unsafe { nw_asyncproof_host_retire_v1(host) },
        NW_NATIVE_PANIC
    );
    // SAFETY: deliberately attempted early release must refuse this quarantined host.
    unsafe { nw_asyncproof_host_release_v1(host) };
    assert_eq!(count(0), 1);
    assert_eq!(count(15), 1);
    println!(
        "worker panic: one retained host, one unknown-size retained payload, no foreign unwind"
    );
}
