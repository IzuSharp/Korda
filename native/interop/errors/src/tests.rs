use super::*;
use std::sync::{Arc, Barrier, atomic::AtomicUsize};

#[repr(C, align(8))]
struct Storage([u8; 64]);

#[test]
fn every_status_and_capacity_preserves_the_original_outcome() {
    for status in (0..=14).chain([u32::MAX]) {
        for capacity in (0_u32..=32).chain([64]) {
            let mut memory = Storage([0xa5; 64]);
            memory.0[..4].copy_from_slice(&capacity.to_ne_bytes());
            let original = memory.0;
            let fields = ErrorFields {
                domain: u32::MAX,
                code: i32::MIN,
                correlation_id: u64::MAX,
            };
            // SAFETY: aligned live storage holds the complete truthful declared range.
            let actual = unsafe { write_error(status, memory.0.as_mut_ptr().cast(), fields) };
            assert_eq!(actual, status);
            if status <= NW_PENDING || capacity < 24 {
                assert_eq!(memory.0, original, "status={status}, capacity={capacity}");
            } else {
                assert_eq!(u32::from_ne_bytes(memory.0[0..4].try_into().unwrap()), 24);
                assert_eq!(
                    u32::from_ne_bytes(memory.0[4..8].try_into().unwrap()),
                    u32::MAX
                );
                assert_eq!(
                    i32::from_ne_bytes(memory.0[8..12].try_into().unwrap()),
                    i32::MIN
                );
                assert_eq!(&memory.0[12..16], &[0; 4]);
                assert_eq!(&memory.0[16..24], &[0xff; 8]);
                assert_eq!(&memory.0[24..], &original[24..]);
            }
            // SAFETY: absent diagnostic output is supported for every status.
            assert_eq!(
                // SAFETY: a null output is explicitly supported.
                unsafe { write_error(status, std::ptr::null_mut(), fields) },
                status
            );
        }
    }
}

#[test]
fn utf8_is_owned_bounded_and_never_split() {
    for input in ["", "ASCII", "Aé水🦀Z", "embedded\0nul"] {
        for capacity in 0..=input.len() + 1 {
            let detail = BoundedDetail::from_redacted(input, capacity);
            let text = std::str::from_utf8(detail.bytes()).unwrap();
            assert!(input.starts_with(text));
            assert!(detail.bytes().len() <= capacity);
            assert_eq!(detail.is_truncated(), detail.bytes().len() < input.len());
            assert!(
                input[detail.bytes().len()..]
                    .chars()
                    .next()
                    .is_none_or(|c| detail.bytes().len() + c.len_utf8() > capacity)
            );
        }
    }
    let huge = "🦀".repeat(5000);
    let detail = BoundedDetail::from_redacted(&huge, usize::MAX);
    assert_eq!(detail.bytes().len(), MAX_DETAIL_BYTES);
    assert!(detail.is_truncated());
}

#[test]
fn ordinary_failure_keeps_admission_and_panic_quarantines_poison() {
    let boundary = EngineBoundary::new();
    assert_eq!(
        boundary
            .business(|| Err::<(), _>(Failure::base(NW_INVALID_ARGUMENT)))
            .unwrap_err()
            .status,
        NW_INVALID_ARGUMENT
    );
    assert!(!boundary.is_poisoned());
    assert_eq!(boundary.business(|| Ok(42)), Ok(42));
    struct Dangerous;
    impl Drop for Dangerous {
        fn drop(&mut self) {
            panic!("payload destructor must never run");
        }
    }
    let result = boundary.business(|| -> Result<(), Failure> { std::panic::panic_any(Dangerous) });
    assert_eq!(result.unwrap_err().status, NW_NATIVE_PANIC);
    assert_eq!(boundary.quarantined_payloads(), 1);
    assert_eq!(boundary.active_calls(), 0);
    assert_eq!(
        boundary.business(|| Ok(())).unwrap_err().status,
        NW_ENGINE_POISONED
    );
    assert_eq!(
        boundary.cleanup(|| Ok("known-safe cleanup")),
        Ok("known-safe cleanup")
    );
    assert!(boundary.is_poisoned());
}

#[test]
fn poison_race_preserves_admitted_calls_and_counts_each_payload() {
    let boundary = Arc::new(EngineBoundary::new());
    let admitted = Arc::new(Barrier::new(9));
    let proceed = Arc::new(Barrier::new(9));
    let handles: Vec<_> = (0..8)
        .map(|i| {
            let engine = boundary.clone();
            let entered = admitted.clone();
            let finish = proceed.clone();
            std::thread::spawn(move || {
                engine.business(|| {
                    entered.wait();
                    finish.wait();
                    if i < 2 {
                        panic!("accepted operation panic");
                    }
                    Ok(i)
                })
            })
        })
        .collect();
    admitted.wait();
    assert_eq!(boundary.active_calls(), 8);
    boundary.poison();
    assert_eq!(
        boundary.business(|| Ok(())).unwrap_err().status,
        NW_ENGINE_POISONED
    );
    assert_eq!(boundary.cleanup(|| Ok(())), Ok(()));
    proceed.wait();
    let mut successes = 0;
    for handle in handles {
        match handle.join().unwrap() {
            Ok(_) => successes += 1,
            Err(e) => assert_eq!(e.status, NW_NATIVE_PANIC),
        }
    }
    assert_eq!(successes, 6);
    assert_eq!(boundary.active_calls(), 0);
    assert_eq!(boundary.quarantined_payloads(), 2);
}

#[test]
fn owner_publication_is_failure_atomic_and_partial_temporaries_drop() {
    static DROPS: AtomicUsize = AtomicUsize::new(0);
    struct Owner;
    impl Drop for Owner {
        fn drop(&mut self) {
            DROPS.fetch_add(1, Ordering::SeqCst);
        }
    }
    let boundary = EngineBoundary::new();
    let mut output = std::ptr::dangling_mut::<Owner>();
    // SAFETY: valid output slot, no owner is transferred on failure.
    let status = unsafe {
        create_owner(&boundary, &mut output, || {
            let _temporary = Box::new(Owner);
            Err(Failure::base(NW_INVALID_ARGUMENT))
        })
    };
    assert_eq!(status, NW_INVALID_ARGUMENT);
    assert!(output.is_null());
    assert_eq!(DROPS.load(Ordering::SeqCst), 1);
    // SAFETY: valid output slot and the accepted owner is reclaimed exactly once below.
    assert_eq!(
        // SAFETY: valid slot; the sole successful owner is reclaimed below.
        unsafe { create_owner(&boundary, &mut output, || Ok(Box::new(Owner))) },
        NW_OK
    );
    assert!(!output.is_null());
    // SAFETY: sole owner received above; matching Rust allocator and type.
    unsafe { drop(Box::from_raw(output)) };
    assert_eq!(DROPS.load(Ordering::SeqCst), 2);
}

#[test]
fn an_invalid_failure_never_becomes_success() {
    for status in [NW_OK, NW_PENDING, NW_CONTRACT_VIOLATION] {
        let boundary = EngineBoundary::new();
        let result = boundary.business(|| {
            Err::<(), _>(Failure {
                status,
                fields: ErrorFields::default(),
            })
        });
        assert_eq!(result.unwrap_err().status, NW_CONTRACT_VIOLATION);
        assert!(boundary.is_poisoned());
    }
}
