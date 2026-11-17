use super::*;
include!("../generated/layout_assertions.rs");

fn profile(raw: u64, encoded: u64, decoded: u64) -> NwCodecLimitsV1 {
    NwCodecLimitsV1 {
        struct_size: 32,
        flags: 0,
        raw_limit: raw,
        encoded_limit: encoded,
        decoded_limit: decoded,
    }
}
fn random(length: usize) -> Vec<u8> {
    let mut seed = 0x9e3779b9u32;
    (0..length)
        .map(|_| {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            seed as u8
        })
        .collect()
}
#[test]
fn real_direct_native_boundary_and_owner_cases() {
    let mut cases = 0;
    for size in [0, 1, 7, 31, 256, 4096, 65536, 4 * 1024 * 1024] {
        let bytes = random(size);
        let limits = profile(size as u64, size as u64 + 65536, size as u64);
        let encoded = run(&bytes, 0, limits).unwrap();
        assert_eq!(
            encoded,
            miniz_oxide::deflate::compress_to_vec_zlib(&bytes, 6)
        );
        assert_eq!(run(&encoded, 1, limits).unwrap(), bytes);
        assert_eq!(
            miniz_oxide::inflate::decompress_to_vec_zlib_with_limit(&encoded, size).unwrap(),
            bytes
        );
        let mut owner = std::ptr::null_mut();
        let mut address = std::ptr::null();
        let mut length = 0;
        // SAFETY: byte vectors and disjoint initialized slots stay live through these calls.
        unsafe {
            assert_eq!(
                nw_syncproof_run_v1(
                    bytes.as_ptr(),
                    size as u64,
                    0,
                    &limits,
                    &mut owner,
                    &mut address,
                    &mut length,
                    std::ptr::null_mut()
                ),
                NW_OK
            );
            assert!(!owner.is_null());
            assert_eq!(
                std::slice::from_raw_parts(address, length as usize),
                encoded
            );
            nw_syncproof_release_v1(owner);
        }
        cases += 1;
    }
    for cap in [
        0,
        1,
        7,
        8,
        31,
        256,
        4096,
        65536,
        4 * 1024 * 1024,
        16 * 1024 * 1024,
    ] {
        let bytes = random(cap);
        let limits = profile(cap as u64, cap as u64, cap as u64);
        let mut owner = std::ptr::dangling_mut();
        let mut address = std::ptr::dangling();
        let mut length = 17;
        let mut error = NwErrorInfoV1 {
            struct_size: 24,
            domain: 0,
            code: 0,
            flags: 0,
            correlation_id: 0,
        };
        // SAFETY: live vectors and disjoint aligned output slots; owner released exactly once on success.
        let status = unsafe {
            nw_syncproof_run_v1(
                bytes.as_ptr(),
                cap as u64,
                0,
                &limits,
                &mut owner,
                &mut address,
                &mut length,
                &mut error,
            )
        };
        if status == NW_OK {
            // SAFETY: this test owns the successful output until its matching release.
            unsafe {
                assert_eq!(
                    run(
                        std::slice::from_raw_parts(address, length as usize),
                        1,
                        limits
                    )
                    .unwrap(),
                    bytes
                );
                nw_syncproof_release_v1(owner);
            }
        } else {
            assert_eq!(
                (status, error.code),
                (NW_DOMAIN_ERROR, COMPRESSED_OUTPUT_LIMIT)
            );
            assert!(owner.is_null() && address.is_null() && length == 0);
        }
        cases += 1;
    }
    let limits = profile(16, 1024, 16);
    let encoded = miniz_oxide::deflate::compress_to_vec_zlib(&[42u8; 17], 6);
    assert_eq!(
        run(&encoded, 1, limits).unwrap_err().fields.code,
        DECODED_LIMIT
    );
    assert_eq!(
        run(&[42u8; 17], 0, limits).unwrap_err().fields.code,
        RAW_LIMIT
    );
    assert_eq!(
        run(&[0u8; 1025], 1, limits).unwrap_err().fields.code,
        ENCODED_LIMIT
    );
    let valid = miniz_oxide::deflate::compress_to_vec_zlib(b"payload", 6);
    for size in 0..valid.len() {
        assert_eq!(
            run(&valid[..size], 1, limits).unwrap_err().fields.code,
            MALFORMED
        );
    }
    let mut corrupt = valid.clone();
    *corrupt.last_mut().unwrap() ^= 1;
    assert_eq!(run(&corrupt, 1, limits).unwrap_err().fields.code, MALFORMED);
    let mut trailing = valid;
    trailing.push(0);
    assert_eq!(
        run(&trailing, 1, limits).unwrap_err().fields.code,
        MALFORMED
    );
    assert_eq!(buffer(usize::MAX).unwrap_err().status, NW_OUT_OF_MEMORY);
    assert_eq!(LIVE.load(Ordering::SeqCst), 0);
    assert_eq!(CAPACITY.load(Ordering::SeqCst), 0);
    assert_eq!(BOUNDARY.active_calls(), 0);
    println!(
        "PASS direct miniz_oxide parity, {cases} bounded profiles, malformed/limit/reserve/owner cases"
    );
}
