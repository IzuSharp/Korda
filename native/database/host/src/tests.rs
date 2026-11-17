//! Tests for this Host's composition of the existing Core operation, not a replacement executor.
use crate::{
    config::{Config, Limits, ProviderConfig},
    operation::{DbOperation, Value},
    runtime::Source,
};
use futures_util::FutureExt;
use nativeweave_interop_abi::*;
use std::sync::{
    Arc, Condvar, Mutex,
    atomic::{AtomicBool, AtomicUsize, Ordering},
};
use std::time::{Duration, Instant};

fn source() -> Arc<Source> {
    let limits = Limits {
        connections: 1,
        waiters: 2,
        admission: Duration::from_secs(1),
        buffered_rows: 8,
        buffered_bytes: 65536,
        batch_rows: 2,
        batch_bytes: 8192,
        field_bytes: 4096,
        nodes: 64,
        depth: 16,
        parameters: 8,
        parameter_bytes: 65536,
        prepared_plans: 4,
        prepared_bytes: 65536,
        command: Duration::from_secs(1),
    };
    #[cfg(feature = "sqlite")]
    let (profile, provider) = (
        nativeweave_data_contracts::Profile::Sqlite,
        ProviderConfig::Sqlite {
            path: ":memory:".into(),
            options: nativeweave_data_sqlite::Options {
                mode: nativeweave_data_sqlite::OpenMode::Memory,
                journal: nativeweave_data_sqlite::JournalMode::Memory,
                ..Default::default()
            },
        },
    );
    #[cfg(all(feature = "postgres", not(feature = "sqlite")))]
    let (profile, provider) = (
        nativeweave_data_contracts::Profile::PostgreSql,
        ProviderConfig::Postgres(nativeweave_data_postgres::ConnectConfig::new(
            "postgresql://unused@127.0.0.1/unused".into(),
        )),
    );
    Source::create(Config {
        profile,
        provider,
        limits,
        name: "host-operation-tests".into(),
    })
    .unwrap()
}
struct Probe {
    calls: AtomicUsize,
    status: AtomicUsize,
    entered: Mutex<bool>,
    released: Mutex<bool>,
    wake: Condvar,
    block: bool,
    result: u32,
    start_returned: AtomicBool,
    during_start: AtomicBool,
}
impl Probe {
    fn new(block: bool, result: u32) -> Self {
        Self {
            calls: AtomicUsize::new(0),
            status: AtomicUsize::new(usize::MAX),
            entered: Mutex::new(false),
            released: Mutex::new(false),
            wake: Condvar::new(),
            block,
            result,
            start_returned: AtomicBool::new(false),
            during_start: AtomicBool::new(false),
        }
    }
    fn callbacks(&self) -> NwOperationCallbacksV1 {
        NwOperationCallbacksV1 {
            struct_size: size_of::<NwOperationCallbacksV1>() as u32,
            flags: 0,
            cookie: self as *const Self as usize,
            on_terminal: Some(callback),
        }
    }
    fn wait(&self) {
        let state = self.entered.lock().unwrap();
        let (state, timeout) = self
            .wake
            .wait_timeout_while(state, Duration::from_secs(5), |entered| !*entered)
            .unwrap();
        assert!(
            *state && !timeout.timed_out(),
            "Host callback did not arrive"
        );
    }
    fn release(&self) {
        *self.released.lock().unwrap() = true;
        self.wake.notify_all();
    }
}
unsafe extern "C" fn callback(cookie: usize, status: u32, _: *const NwErrorInfoV1) -> u32 {
    // SAFETY: every test retains this pinned Box until operation retirement.
    let probe = unsafe { &*(cookie as *const Probe) };
    probe.calls.fetch_add(1, Ordering::AcqRel);
    probe.status.store(status as usize, Ordering::Release);
    probe.during_start.store(
        !probe.start_returned.load(Ordering::Acquire),
        Ordering::Release,
    );
    *probe.entered.lock().unwrap() = true;
    probe.wake.notify_all();
    if probe.block {
        let state = probe.released.lock().unwrap();
        let (state, _) = probe
            .wake
            .wait_timeout_while(state, Duration::from_secs(5), |released| !*released)
            .unwrap();
        if !*state {
            return NW_CONTRACT_VIOLATION;
        }
    }
    probe.result
}
fn retire(operation: &Arc<DbOperation>) {
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        match operation.core.try_retire() {
            NW_OK => break,
            NW_PENDING => {}
            other => panic!("unexpected retirement {other}"),
        }
        assert!(Instant::now() < deadline, "Host operation did not retire");
        std::thread::yield_now();
    }
    let (_, _, _, callbacks, workers, _) = operation.core.snapshot();
    assert_eq!((callbacks, workers), (0, 0));
    assert!(operation.core.can_release());
}
fn close(source: Arc<Source>) {
    assert_eq!(source.pending.load(Ordering::Acquire), 0);
    source.request_close();
    source.runtime.join();
    assert!(source.runtime.is_joined());
    let weak = Arc::downgrade(&source);
    drop(source);
    assert!(weak.upgrade().is_none());
}
#[test]
fn rejected_start_rolls_back_created_host_owner_without_callback() {
    let source = source();
    let probe = Box::new(Probe::new(false, NW_OK));
    let operation = DbOperation::create(
        source.clone(),
        100,
        false,
        Box::new(|_| async { Ok(Value::Count(4)) }.boxed()),
    )
    .unwrap();
    let mut callbacks = probe.callbacks();
    callbacks.flags = 1;
    // SAFETY: invalid registration remains rooted through the synchronous rejecting call.
    assert_eq!(unsafe { operation.start(callbacks) }, NW_INVALID_ARGUMENT);
    assert_eq!(probe.calls.load(Ordering::Acquire), 0);
    assert!(operation.core.can_release());
    drop(operation);
    close(source);
}
#[test]
fn callback_and_control_races_cannot_retire_or_transfer_twice() {
    let source = source();
    let probe = Box::new(Probe::new(true, NW_OK));
    let operation = DbOperation::create(
        source.clone(),
        100,
        false,
        Box::new(|_| async { Ok(Value::Count(41)) }.boxed()),
    )
    .unwrap();
    // SAFETY: callback's pinned state stays alive through successful retirement.
    assert_eq!(unsafe { operation.start(probe.callbacks()) }, NW_OK);
    probe.start_returned.store(true, Ordering::Release);
    probe.wait();
    assert_eq!(operation.core.try_retire(), NW_PENDING);
    assert!(!operation.core.can_release());
    // SAFETY: the duplicate start uses the same still-rooted callback, and must reject.
    assert_eq!(
        // SAFETY: duplicate registration uses the retained callback Box.
        unsafe { operation.start(probe.callbacks()) },
        NW_INVALID_STATE
    );
    // SAFETY: terminal has already been delivered; the duplicate attempt must be rejected without a second callback.
    assert_eq!(
        // SAFETY: the callback Box remains alive while the duplicate terminal is rejected.
        unsafe {
            operation
                .core
                .complete(NW_OK, Some(Value::Count(99)), Default::default())
        },
        NW_INVALID_STATE
    );
    let canceler = operation.clone();
    let thread = std::thread::spawn(move || {
        for _ in 0..32 {
            canceler.cancel.request();
            canceler.core.request_cancel();
            assert_eq!(canceler.core.try_retire(), NW_PENDING);
        }
    });
    thread.join().unwrap();
    assert_eq!(probe.calls.load(Ordering::Acquire), 1);
    let Value::Count(value) = operation.core.take_result().unwrap() else {
        panic!("wrong transfer")
    };
    assert_eq!(value, 41);
    assert!(operation.core.take_result().is_err());
    probe.release();
    retire(&operation);
    drop(operation);
    close(source);
}
#[test]
fn actual_retired_source_control_completes_inside_start() {
    let source = source();
    source.request_close();
    source.runtime.join();
    let probe = Box::new(Probe::new(false, NW_OK));
    let operation = DbOperation::create(
        source.clone(),
        100,
        true,
        Box::new(|_| async { Ok(Value::Count(1)) }.boxed()),
    )
    .unwrap();
    // SAFETY: synchronous callback's Box outlives start and retirement.
    assert_eq!(unsafe { operation.start(probe.callbacks()) }, NW_OK);
    probe.start_returned.store(true, Ordering::Release);
    assert!(probe.during_start.load(Ordering::Acquire));
    assert_eq!(probe.calls.load(Ordering::Acquire), 1);
    retire(&operation);
    drop(operation);
    close(source);
}
#[test]
fn accepted_callback_failure_poison_is_separate_from_retirement() {
    let source = source();
    let probe = Box::new(Probe::new(false, NW_CONTRACT_VIOLATION));
    let operation = DbOperation::create(
        source.clone(),
        100,
        false,
        Box::new(|_| async { Ok(Value::Count(2)) }.boxed()),
    )
    .unwrap();
    // SAFETY: the failure-returning callback remains rooted through native retirement.
    assert_eq!(unsafe { operation.start(probe.callbacks()) }, NW_OK);
    probe.wait();
    retire(&operation);
    assert!(operation.core.snapshot().5);
    assert_eq!(source.admit().unwrap_err().status, NW_ENGINE_POISONED);
    assert_eq!(probe.calls.load(Ordering::Acquire), 1);
    drop(operation);
    close(source);
}
#[test]
fn cancel_before_start_retains_source_until_native_canceled_terminal() {
    let source = source();
    let probe = Box::new(Probe::new(false, NW_OK));
    let operation = DbOperation::create(
        source.clone(),
        100,
        false,
        Box::new(|cancel| {
            async move {
                if cancel.requested() {
                    Err(crate::Error::canceled())
                } else {
                    Ok(Value::Count(1))
                }
            }
            .boxed()
        }),
    )
    .unwrap();
    operation.cancel.request();
    // SAFETY: callback state remains live through the actual terminal and native retirement.
    assert_eq!(unsafe { operation.start(probe.callbacks()) }, NW_OK);
    probe.wait();
    retire(&operation);
    assert_eq!(probe.status.load(Ordering::Acquire), NW_CANCELED as usize);
    assert!(operation.core.take_result().is_err());
    assert_eq!(probe.calls.load(Ordering::Acquire), 1);
    drop(operation);
    close(source);
}

#[cfg(feature = "sqlite")]
mod relation_retention {
    use super::*;
    use crate::abi::{self, OwnerValue};
    use nativeweave_data_contracts::{Model, Projection, stable_id, wire::Batch};
    use nativeweave_query_binding::{ModelBinding, ProjectionBinding};
    use sha2::{Digest, Sha256};
    use std::{ffi::c_void, ptr};

    fn model(source: &Source, table: &str, names: &[&str]) -> ModelBinding {
        let logical = format!("B14.Retention:{table}");
        let table_id = stable_id(&[Some("NWDB-TABLE-ID-v1"), Some(&logical), None, Some(table)]);
        let ids: Vec<_> = names
            .iter()
            .map(|name| {
                stable_id(&[
                    Some("NWDB-COLUMN-ID-v1"),
                    Some(&logical),
                    None,
                    Some(table),
                    Some(name),
                ])
            })
            .collect();
        let mut columns: Vec<_> = names.iter().enumerate().map(|(index, name)| (ids[index], format!(
            "{{\"id\":{},\"name\":\"{name}\",\"codec\":3,\"codec_config\":null,\"nullable\":false,\"key_ordinal\":{},\"generated\":false,\"concurrency\":false,\"collation\":null,\"max_length\":null}}",
            ids[index], if index == 0 { "0" } else { "null" }
        ))).collect();
        columns.sort_by_key(|column| column.0);
        let canonical = format!(
            "{{\"version\":1,\"logical_id\":\"{logical}\",\"table_id\":{table_id},\"schema\":null,\"table\":\"{table}\",\"columns\":[{}],\"projection\":[{}],\"relations\":[]}}",
            columns.iter().map(|column| column.1.as_str()).collect::<Vec<_>>().join(","),
            ids.iter().map(u32::to_string).collect::<Vec<_>>().join(",")
        ).into_bytes();
        let definition = crate::control::encode(
            2,
            &[
                (1, 5, canonical.clone()),
                (2, 5, Sha256::digest(&canonical).to_vec()),
            ],
        );
        source.register_model(&definition).unwrap()
    }
    fn hex(bytes: &[u8]) -> String {
        bytes.iter().map(|byte| format!("{byte:02x}")).collect()
    }
    fn relation(
        source: &Source,
        parent: ModelBinding,
        child: ModelBinding,
    ) -> Arc<nativeweave_query_binding::RelationBinding> {
        let parent_column = parent.model.keys()[0].id;
        let child_column = child
            .model
            .columns()
            .iter()
            .find(|column| column.name == "Parent")
            .unwrap()
            .id;
        let parent_id = parent.model.table_id();
        let child_id = child.model.table_id();
        let canonical = format!(
            "{{\"version\":1,\"parent_model\":\"{}\",\"parent_table\":{parent_id},\"child_model\":\"{}\",\"child_table\":{child_id},\"parent_columns\":[{parent_column}],\"child_columns\":[{child_column}],\"child_nullable\":[false]}}",
            hex(&parent.model.hash()),
            hex(&child.model.hash())
        );
        let definition = crate::control::encode(
            23,
            &[
                (1, 1, parent_id.to_le_bytes().to_vec()),
                (2, 5, parent.model.hash().to_vec()),
                (3, 1, child_id.to_le_bytes().to_vec()),
                (4, 5, child.model.hash().to_vec()),
                (5, 7, parent_column.to_le_bytes().to_vec()),
                (6, 7, child_column.to_le_bytes().to_vec()),
                (7, 7, 0u32.to_le_bytes().to_vec()),
                (8, 5, Sha256::digest(canonical.as_bytes()).to_vec()),
            ],
        );
        source
            .register_relation(parent, child, &definition)
            .unwrap()
    }
    fn run_owner(operation: *mut c_void, kind: u32) -> *mut c_void {
        let probe = Box::new(Probe::new(false, NW_OK));
        let mut error = NwErrorInfoV1 {
            struct_size: size_of::<NwErrorInfoV1>() as u32,
            domain: 0,
            code: 0,
            flags: 0,
            correlation_id: 0,
        };
        assert_eq!(
            // SAFETY: the callback Box and writable error record survive start and retirement.
            unsafe { abi::nw_db_operation_start_v1(operation, &probe.callbacks(), &mut error) },
            NW_OK
        );
        probe.wait();
        assert_eq!(probe.status.load(Ordering::Acquire), NW_OK as usize);
        let mut result = ptr::null_mut();
        // SAFETY: one successful result is transferred into an independent writable slot.
        let taken = unsafe {
            if kind == 101 {
                abi::nw_db_operation_take_buffer_v1(operation, &mut result, &mut error)
            } else {
                abi::nw_db_operation_take_owner_v1(operation, kind, &mut result, &mut error)
            }
        };
        assert_eq!(taken, NW_OK);
        let deadline = Instant::now() + Duration::from_secs(5);
        loop {
            // SAFETY: this test still owns the operation and its callback state.
            let status = unsafe { abi::nw_db_operation_try_retire_v1(operation) };
            if status == NW_OK {
                break;
            }
            assert_eq!(status, NW_PENDING);
            assert!(Instant::now() < deadline);
            std::thread::yield_now();
        }
        // SAFETY: native quiet is confirmed and the owner is released once.
        unsafe {
            abi::nw_db_operation_release_v1(operation);
        }
        result
    }

    #[test]
    fn transferred_relation_cursor_keeps_exact_dependencies_until_quiet() {
        let source = source();
        let opening = source.clone();
        let operation = DbOperation::create(
            source.clone(),
            1,
            false,
            Box::new(move |cancel| {
                async move {
                    Ok(Value::Owner(OwnerValue::Session(
                        crate::session::Session::open(opening, cancel).await?,
                    )))
                }
                .boxed()
            }),
        )
        .unwrap();
        let session_handle = run_owner(abi::publish_operation(operation).unwrap(), 1);
        let session_owner = abi::owner(session_handle).unwrap();
        let OwnerValue::Session(session) = &*session_owner else {
            panic!("session owner");
        };
        #[cfg(feature = "postgres")]
        let crate::session::ProviderSession::Sqlite(provider) = &session.provider else {
            panic!("SQLite profile");
        };
        #[cfg(not(feature = "postgres"))]
        let crate::session::ProviderSession::Sqlite(provider) = &session.provider;
        source.runtime.handle.block_on(async {
            provider.execute("CREATE TABLE b14_retention_child (\"Id\" BIGINT PRIMARY KEY, \"Parent\" BIGINT NOT NULL)", None, Default::default()).await.unwrap();
            provider.execute("INSERT INTO b14_retention_child VALUES (1,7),(2,7),(3,7)", None, Default::default()).await.unwrap();
        });
        let parent = model(&source, "b14_retention_parent", &["Id"]);
        let child = model(&source, "b14_retention_child", &["Id", "Parent"]);
        let parent_weak = Arc::<Model>::downgrade(&parent.model);
        let child_weak = Arc::<Model>::downgrade(&child.model);
        let projection = source.resolve_projection(ProjectionBinding::new(source.identity, 1, Arc::new(Projection::parse(
            br#"{"version":1,"fields":[{"ordinal":0,"name":"Id","codec":3,"codec_config":null,"nullable":false},{"ordinal":1,"name":"Parent","codec":3,"codec_config":null,"nullable":false}]}"#,
            source.config.profile).unwrap()), source.codecs().unwrap()).unwrap()).unwrap();
        let projection_id = projection.id;
        let projection_weak = Arc::downgrade(&projection.projection);
        let relation = relation(&source, parent, child);
        let relation_weak = Arc::downgrade(&relation);
        let expected = relation
            .relation
            .parent_key_expectation(source.config.profile, &[3])
            .unwrap();
        let key = Batch::encode(&expected, 1, &[Some(&7i64.to_le_bytes())]).unwrap();
        let relation_handle =
            abi::publish_owner(OwnerValue::Relation(source.clone(), relation)).unwrap();
        drop(projection);
        let mut operation = ptr::null_mut();
        let mut error = NwErrorInfoV1 {
            struct_size: size_of::<NwErrorInfoV1>() as u32,
            domain: 0,
            code: 0,
            flags: 0,
            correlation_id: 0,
        };
        assert_eq!(
            // SAFETY: both creating-source handles are owned, and key bytes are borrowed only by this call.
            unsafe {
                crate::relations::nw_db_relation_load_create_v1(
                    session_handle,
                    relation_handle,
                    NwBytesViewV1 {
                        data: key.as_ptr(),
                        length: key.len() as u64,
                    },
                    projection_id,
                    &mut operation,
                    &mut error,
                )
            },
            NW_OK
        );
        // SAFETY: no further call uses the relation wrapper; the admitted operation owns its dependency.
        unsafe {
            abi::nw_db_owner_release_v1(relation_handle);
        }
        {
            // Apply the same registry-root removal as source close without closing
            // execution admission: an admitted cursor must own these roots itself.
            let mut registry = source.registry.lock().unwrap();
            registry.relations.clear();
            registry.models.clear();
            registry.projections.clear();
            registry.bytes = 0;
        }
        assert!(relation_weak.upgrade().is_some());
        let cursor_handle = run_owner(operation, 2);
        let cursor_owner = abi::owner(cursor_handle).unwrap();
        let OwnerValue::Cursor(cursor) = &*cursor_owner else {
            panic!("cursor owner");
        };
        assert!(
            relation_weak.upgrade().is_some(),
            "transferred cursor lost its relation"
        );
        assert!(
            parent_weak.upgrade().is_some(),
            "transferred cursor lost its parent model"
        );
        assert!(
            child_weak.upgrade().is_some(),
            "transferred cursor lost its child model"
        );
        assert!(
            projection_weak.upgrade().is_some(),
            "transferred cursor lost its exact projection"
        );
        let mut fetch = ptr::null_mut();
        assert_eq!(
            // SAFETY: a live cursor and bounded capacities produce one independently owned fetch operation.
            unsafe {
                abi::nw_db_cursor_fetch_create_v1(cursor_handle, 1, 8192, &mut fetch, &mut error)
            },
            NW_OK
        );
        let batch = run_owner(fetch, 101);
        let mut view = NwBytesViewV1 {
            data: ptr::null(),
            length: 0,
        };
        assert_eq!(
            // SAFETY: the buffer is retained through this view and the following bounded byte read.
            unsafe { abi::nw_db_buffer_view_v1(batch, &mut view, &mut error) },
            NW_OK
        );
        assert!(view.length >= 96);
        // SAFETY: the successful view remains covered by the live buffer owner.
        let wire = unsafe { std::slice::from_raw_parts(view.data, view.length as usize) };
        assert_eq!(u32::from_le_bytes(wire[56..60].try_into().unwrap()), 1);
        // SAFETY: the borrowed wire is no longer used and this is its only release.
        unsafe {
            abi::nw_db_buffer_release_v1(batch);
        }
        cursor.request_close();
        source.runtime.handle.block_on(cursor.join());
        assert!(
            relation_weak.upgrade().is_none(),
            "quiet cursor retained relation roots"
        );
        assert!(parent_weak.upgrade().is_none());
        assert!(child_weak.upgrade().is_none());
        assert!(projection_weak.upgrade().is_none());
        drop(cursor_owner);
        // SAFETY: retirement was joined and the remaining cursor wrapper is released once.
        unsafe {
            abi::nw_db_owner_release_v1(cursor_handle);
        }
        session.request_close();
        source.runtime.handle.block_on(session.join());
        drop(session_owner);
        // SAFETY: this is the original live session wrapper, released after actual session retirement.
        unsafe {
            abi::nw_db_owner_release_v1(session_handle);
        }
        close(source);
    }
}
