use super::*;
#[test]
fn actual_owner_thread_and_deferred_release() {
    fn is_send_sync<T: Send + Sync>() {}
    is_send_sync::<Control>();
    assert_eq!(size_of::<NwAffinitySnapshot>(), 80);
    let (mut handle, mut observer) = (std::ptr::null_mut(), std::ptr::null_mut());
    // SAFETY: independent live output slots and unique handle ownership throughout.
    unsafe {
        assert_eq!(nw_extension_create_v1(1, &mut handle, &mut observer), NW_OK);
        let mut value = 123;
        assert_eq!(nw_extension_read_v1(observer, &mut value), NW_OK);
        assert_eq!(value, 0);
        let observer_address = observer as usize;
        thread::scope(|scope| {
            scope
                .spawn(move || {
                    let mut value = 123;
                    assert_eq!(
                        nw_extension_read_v1(observer_address as *const _, &mut value),
                        NW_NOT_SUPPORTED
                    );
                    assert_eq!(value, 123); // No owner state or output was touched on the wrong thread.
                })
                .join()
                .unwrap();
        });
        assert_eq!(nw_extension_stop_v1(handle), NW_OK);
        let invalid = NwCallbacksV1 {
            struct_size: 32,
            flags: 0,
            cookie: 1,
            on_event: None,
        };
        assert_eq!(nw_extension_start_v1(handle, &invalid), NW_INVALID_STATE);
        let address = handle as usize;
        thread::scope(|scope| {
            scope
                .spawn(move || nw_extension_release_v1(address as *mut _))
                .join()
                .unwrap();
        });
        let mut pending = NwAffinitySnapshot::default();
        assert_eq!(nw_extension_snapshot_v1(observer, &mut pending), NW_OK);
        assert_eq!((pending.release_requested, pending.retired), (1, 0));
        assert_eq!(nw_extension_count_v1(0), 1);
        assert_eq!(nw_extension_pump_v1(observer, 1), NW_OK);
        assert_eq!(nw_extension_snapshot_v1(observer, &mut pending), NW_OK);
        assert_eq!(pending.retired, 1);
        nw_extension_observer_release_v1(observer);
        assert_eq!(nw_extension_count_v1(0), 0);
        assert_eq!(nw_extension_count_v1(2), 0);
        assert_eq!(nw_extension_count_v1(3), 0);
    }
    println!(
        "PASS actual Rc owner access, wrong-thread rejection, closed start and deferred foreign-thread release"
    );
}
