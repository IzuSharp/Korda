//! Test-only ABI composition of the actual B02/B03 providers and B06 cursor.
//! Blocking fixture entrypoints run on managed test workers. The product adapter
//! awaits its transport seam; this harness does not implement B04 or distribute a
//! new engine/package. Its one explicit test runtime stays resident for the run.
use futures_util::TryStreamExt;
use nativeweave_data_contracts::{ConnectionDisposition, Profile};
use nativeweave_data_postgres as pg;
use nativeweave_data_sqlite as sq;
use nativeweave_data_streaming::{
    Budget, ColumnMetadata, Cursor, ErrorKind, Fetch, Limits, OwnedBatch, Result, Retirement,
    Shape, Source, StreamError,
};
use sqlx::{
    Column, Row, Statement, TypeInfo,
    postgres::{PgPoolOptions, PgRow},
    sqlite::SqliteRow,
};
use std::{
    ffi::c_void,
    panic::{AssertUnwindSafe, catch_unwind},
    path::Path,
    sync::{
        Arc, Mutex, OnceLock,
        atomic::{AtomicUsize, Ordering},
    },
    time::Duration,
};

static OWNERS: AtomicUsize = AtomicUsize::new(0);
fn runtime() -> &'static tokio::runtime::Runtime {
    static VALUE: OnceLock<tokio::runtime::Runtime> = OnceLock::new();
    VALUE.get_or_init(|| {
        tokio::runtime::Builder::new_multi_thread()
            .worker_threads(2)
            .enable_all()
            .build()
            .expect("component test runtime")
    })
}
fn error(kind: ErrorKind, message: &'static str) -> StreamError {
    StreamError { kind, message }
}
fn pg_error(e: pg::ProviderError) -> StreamError {
    error(
        if e.kind == pg::ErrorKind::Canceled {
            ErrorKind::Canceled
        } else {
            ErrorKind::Provider
        },
        "PostgreSQL fixture operation failed",
    )
}
fn sq_error(e: sq::ProviderError) -> StreamError {
    error(
        if matches!(
            e.kind,
            sq::ErrorKind::Interrupted | sq::ErrorKind::CanceledBeforeDispatch
        ) {
            ErrorKind::Canceled
        } else {
            ErrorKind::Provider
        },
        "SQLite fixture operation failed",
    )
}
fn sql_error(_: sqlx::Error) -> StreamError {
    error(ErrorKind::Provider, "fixture database access failed")
}
fn status(error: StreamError) -> u32 {
    match error.kind {
        ErrorKind::InvalidState => 1,
        ErrorKind::Shape => 2,
        ErrorKind::LargeValueRequiresStreaming => 3,
        ErrorKind::Canceled => 4,
        ErrorKind::Capacity => 6,
        ErrorKind::CleanupPending => 7,
        ErrorKind::Conversion => 9,
        _ => 5,
    }
}
fn command(sql: String) -> pg::Command {
    pg::Command::expert(sql, None, pg::CommandKind::Ordinary)
}
fn query(provider: u32, scenario: u32) -> Result<String> {
    let pg = provider == 2;
    Ok(match scenario {
        0 => "SELECT CAST(1 AS BIGINT) AS id, CAST(NULL AS TEXT) AS label WHERE 1=0".into(),
        1 => "SELECT CAST(1 AS BIGINT) AS id, CAST('row-1' AS TEXT) AS label".into(),
        2 | 9 => {
            if pg {
                "SELECT i::bigint AS id, ('row-' || i::text)::text AS label FROM generate_series(1, 17) AS i".into()
            } else {
                "WITH RECURSIVE t(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM t WHERE i<17) SELECT i AS id, 'row-' || i AS label FROM t".into()
            }
        }
        3 => {
            if pg {
                "SELECT 1::bigint AS id, repeat('x', 4096)::text AS label".into()
            } else {
                "SELECT 1 AS id, replace(hex(zeroblob(2048)), '0', 'x') AS label".into()
            }
        }
        4 => "SELECT CAST(1 AS BIGINT) AS id".into(),
        5 => "SELECT CAST('x' AS TEXT) AS label, CAST(1 AS BIGINT) AS id".into(),
        6 => {
            if pg {
                "SELECT CAST(1 AS BIGINT) AS id, CAST(NULL AS BIGINT) AS label".into()
            } else {
                "SELECT id, label FROM b06_null_type".into()
            }
        }
        7 => {
            if pg {
                "SELECT 1::bigint AS id, NULL::text AS label".into()
            } else {
                "SELECT 1 AS id, NULL AS label".into()
            }
        }
        8 => {
            if pg {
                "SELECT 1::bigint AS id, 'blocked'::text AS label FROM pg_sleep(3)".into()
            } else {
                "WITH RECURSIVE t(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM t WHERE i<1000000000) SELECT sum(i) AS id, 'blocked' AS label FROM t".into()
            }
        }
        20 => "SELECT CAST(1 AS BIGINT) WHERE 1=0".into(),
        21 => "SELECT CAST(1 AS BIGINT)".into(),
        22 => "SELECT CAST(1 AS BIGINT) UNION ALL SELECT CAST(2 AS BIGINT)".into(),
        _ => return Err(error(ErrorKind::InvalidState, "unknown fixture scenario")),
    })
}
enum NativeCursor {
    Pg(Cursor<PgRow>),
    Sq(Cursor<SqliteRow>),
}
impl NativeCursor {
    async fn fetch(&mut self) -> Result<Fetch> {
        match self {
            Self::Pg(c) => c.fetch().await,
            Self::Sq(c) => c.fetch().await,
        }
    }
    fn take(&mut self) -> Result<OwnedBatch> {
        match self {
            Self::Pg(c) => c.take_batch(),
            Self::Sq(c) => c.take_batch(),
        }
    }
    async fn close(&mut self) -> Result<Retirement> {
        match self {
            Self::Pg(c) => c.close().await,
            Self::Sq(c) => c.close().await,
        }
    }
}
enum Provider {
    Pg(pg::Session, sqlx::PgPool),
    Sq(sq::Session, sqlx::SqlitePool, sq::Admission),
}
impl Provider {
    async fn close(&self, stats: &mut [u64; 20], reusable: bool) -> Result<()> {
        match self {
            Self::Pg(session, pool) => {
                if reusable {
                    let mut probe = session
                        .stream(command("SELECT 42::bigint AS probe".into()))
                        .map_err(pg_error)?;
                    let control = probe.control();
                    let row = probe
                        .try_next()
                        .await
                        .map_err(pg_error)?
                        .ok_or_else(|| error(ErrorKind::Provider, "cleanup probe empty"))?;
                    if row.try_get::<i64, _>(0).map_err(sql_error)? != 42 {
                        return Err(error(ErrorKind::Provider, "cleanup probe differs"));
                    }
                    if probe.try_next().await.map_err(pg_error)?.is_some() {
                        return Err(error(ErrorKind::Provider, "cleanup probe extra row"));
                    }
                    drop(probe);
                    let receipt = control.join().await;
                    stats[14] = u64::from(receipt.retired && receipt.protocol_clean);
                }
                let receipt = session.close().await;
                stats[11] = u64::from(receipt.retired);
                stats[12] = u64::from(receipt.returned_to_pool || receipt.physical_closed);
                stats[13] = u64::from(receipt.returned_to_pool);
                pool.close().await;
            }
            Self::Sq(session, pool, admission) => {
                if reusable {
                    let mut probe = session
                        .stream("SELECT 42 AS probe", None, sq::Cancellation::default())
                        .map_err(sq_error)?;
                    let receipt = probe.receipt();
                    let row = probe
                        .try_next()
                        .await
                        .map_err(sq_error)?
                        .ok_or_else(|| error(ErrorKind::Provider, "cleanup probe empty"))?;
                    if row.try_get::<i64, _>(0).map_err(sql_error)? != 42 {
                        return Err(error(ErrorKind::Provider, "cleanup probe differs"));
                    }
                    if probe.try_next().await.map_err(sq_error)?.is_some() {
                        return Err(error(ErrorKind::Provider, "cleanup probe extra row"));
                    }
                    drop(probe);
                    let receipt = receipt.join().await.map_err(sq_error)?;
                    stats[14] =
                        u64::from(receipt.native_retired && receipt.progress_handler_removed);
                }
                let receipt = session.close().await.map_err(sq_error)?;
                stats[11] =
                    u64::from(receipt.active_operations == 0 && receipt.physical_lease_released);
                stats[12] = u64::from(receipt.physical_lease_released);
                stats[13] = u64::from(receipt.pool_return_completed);
                stats[15] = u64::from(receipt.worker_shutdown_acknowledged);
                stats[16] = u64::from(receipt.os_thread_joined);
                stats[17] = admission.snapshot().active_commands as u64;
                pool.close().await;
            }
        }
        Ok(())
    }
}
struct State {
    cursor: Option<NativeCursor>,
    provider: Provider,
    stats: [u64; 20],
}
struct Fixture {
    state: Mutex<State>,
    stop: Arc<dyn Fn() + Send + Sync>,
    budget: Budget,
}
impl Drop for Fixture {
    fn drop(&mut self) {
        (self.stop)();
        OWNERS.fetch_sub(1, Ordering::SeqCst);
    }
}
async fn create(
    provider: u32,
    scenario: u32,
    projection: &[u8],
    max_rows: usize,
    max_bytes: usize,
) -> Result<Fixture> {
    let profile = if provider == 1 {
        Profile::Sqlite
    } else if provider == 2 {
        Profile::PostgreSql
    } else {
        return Err(error(ErrorKind::Capability, "unknown provider"));
    };
    let scalar = scenario >= 20;
    let shape = Shape::parse(
        projection,
        profile,
        7,
        if scalar { &[3] } else { &[3, 5] },
        scalar,
    )?;
    let limits = Limits {
        max_rows,
        max_batch_bytes: max_bytes,
        max_field_bytes: max_bytes,
    };
    shape.validate_limits(limits)?;
    let budget = Budget::new(1, 64 * 1024 * 1024)?;
    let sql = query(provider, scenario)?;
    let mut stats = [0; 20];
    let (cursor, provider, stop) = if provider == 2 {
        let url = std::env::var("NATIVEWEAVE_TEST_POSTGRES_URL")
            .map_err(|_| error(ErrorKind::Provider, "explicit PostgreSQL test URL required"))?;
        let mut config = pg::ConnectConfig::new(url);
        config.tls = pg::TlsMode::DisableForLocalDevelopment;
        config.application_name = "NativeWeave-B06-fixture".into();
        let pool = PgPoolOptions::new()
            .max_connections(1)
            .acquire_timeout(Duration::from_secs(5))
            .connect_with(config.options().map_err(pg_error)?)
            .await
            .map_err(sql_error)?;
        let mut connection = pool.acquire().await.map_err(sql_error)?;
        stats[18] =
            sqlx::query_scalar::<_, i32>("SELECT current_setting('server_version_num')::int")
                .fetch_one(&mut *connection)
                .await
                .map_err(sql_error)? as u64;
        let session = pg::Session::from_lease(
            connection,
            pg::SessionConfig {
                statement_timeout: Duration::from_millis(300),
                cleanup_timeout: Duration::from_secs(2),
                ..Default::default()
            },
        )
        .await
        .map_err(pg_error)?;
        let mut stop = None;
        let cursor = Cursor::open(
            shape,
            limits,
            &budget,
            runtime().handle().clone(),
            || async { pg_source(&session, sql, &mut stop).await },
        )
        .await;
        match cursor {
            Ok(c) => (
                NativeCursor::Pg(c),
                Provider::Pg(session, pool),
                stop.expect("source supplies stop control"),
            ),
            Err(e) => {
                session.close().await;
                pool.close().await;
                return Err(e);
            }
        }
    } else {
        let provider_limits = sq::Limits {
            max_connections: 1,
            max_commands: 1,
            ..Default::default()
        };
        let options = sq::Options {
            mode: sq::OpenMode::Memory,
            journal: sq::JournalMode::Memory,
            busy_timeout: Duration::from_millis(100),
            ..Default::default()
        };
        let pool = provider_limits
            .pool_options()
            .map_err(sq_error)?
            .connect_with(
                options
                    .connect_options(Path::new(":memory:"), provider_limits)
                    .map_err(sq_error)?,
            )
            .await
            .map_err(sql_error)?;
        let admission = sq::Admission::new(provider_limits).map_err(sq_error)?;
        let mut connection = pool.acquire().await.map_err(sql_error)?;
        let version = sqlx::query_scalar::<_, String>("SELECT sqlite_version()")
            .fetch_one(&mut *connection)
            .await
            .map_err(sql_error)?;
        if version != "3.51.3" {
            return Err(error(
                ErrorKind::Provider,
                "unexpected pinned SQLite engine version",
            ));
        }
        let session =
            sq::Session::start(connection, options, admission.clone(), runtime().handle())
                .map_err(sq_error)?;
        stats[18] = 3_051_003; // Actual sqlite_version() was checked above before adapter ownership.
        let mut stop = None;
        let cursor = Cursor::open(
            shape,
            limits,
            &budget,
            runtime().handle().clone(),
            || async { sq_source(&session, sql, scenario, &mut stop).await },
        )
        .await;
        match cursor {
            Ok(c) => (
                NativeCursor::Sq(c),
                Provider::Sq(session, pool, admission),
                stop.expect("source supplies stop control"),
            ),
            Err(e) => {
                let _ = session.close().await;
                pool.close().await;
                return Err(e);
            }
        }
    };
    OWNERS.fetch_add(1, Ordering::SeqCst);
    Ok(Fixture {
        state: Mutex::new(State {
            cursor: Some(cursor),
            provider,
            stats,
        }),
        stop,
        budget,
    })
}
type Stop = Arc<dyn Fn() + Send + Sync>;
async fn pg_source(
    session: &pg::Session,
    sql: String,
    stop_out: &mut Option<Stop>,
) -> Result<Source<PgRow>> {
    let columns = session
        .describe(command(sql.clone()))
        .await
        .map_err(pg_error)?
        .into_iter()
        .map(|c| ColumnMetadata {
            ordinal: c.ordinal,
            name: c.name,
            type_name: Some(c.type_name),
        })
        .collect();
    let rows = session.stream(command(sql)).map_err(pg_error)?;
    let control = rows.control();
    let join = control.clone();
    let stop: Stop = Arc::new(move || control.request_stop());
    *stop_out = Some(stop.clone());
    Ok(Source {
        columns,
        rows: Box::pin(rows.map_err(pg_error)),
        request_stop: stop,
        join: Box::pin(async move {
            let r = join.join().await;
            Ok(Retirement {
                native_retired: r.retired,
                canceled: r.stopped,
                disposition: if r.protocol_clean {
                    ConnectionDisposition::Usable
                } else {
                    ConnectionDisposition::Discarded
                },
                provider_rows_peak: r.retained_rows_peak,
            })
        }),
    })
}
async fn sq_source(
    session: &sq::Session,
    sql: String,
    scenario: u32,
    stop_out: &mut Option<Stop>,
) -> Result<Source<SqliteRow>> {
    if scenario == 6 {
        session
            .execute(
                "CREATE TEMP TABLE b06_null_type(id INTEGER NOT NULL, label INTEGER)",
                None,
                sq::Cancellation::default(),
            )
            .await
            .map_err(sq_error)?;
        session
            .execute(
                "INSERT INTO b06_null_type VALUES(1, NULL)",
                None,
                sq::Cancellation::default(),
            )
            .await
            .map_err(sq_error)?;
    }
    let metadata = session
        .describe(&sql, sq::Cancellation::default())
        .await
        .map_err(sq_error)?;
    let columns = metadata
        .columns()
        .iter()
        .map(|c| ColumnMetadata {
            ordinal: c.ordinal(),
            name: c.name().to_owned(),
            type_name: Some(c.type_info().name().to_owned()),
        })
        .collect();
    let rows = session
        .stream(&sql, None, sq::Cancellation::default())
        .map_err(sq_error)?;
    let control = rows.stop_control();
    let join = rows.receipt();
    let stop: Stop = Arc::new(move || control.request_stop());
    *stop_out = Some(stop.clone());
    Ok(Source {
        columns,
        rows: Box::pin(rows.map_err(sq_error)),
        request_stop: stop,
        join: Box::pin(async move {
            let r = join.join().await.map_err(sq_error)?;
            Ok(Retirement {
                native_retired: r.native_retired && r.progress_handler_removed,
                canceled: r.stopped,
                disposition: r.disposition,
                provider_rows_peak: r.provider_rows_peak,
            })
        }),
    })
}
fn protected(action: impl FnOnce() -> Result<()>) -> u32 {
    match catch_unwind(AssertUnwindSafe(action)) {
        Ok(Ok(())) => 0,
        Ok(Err(e)) => status(e),
        Err(_) => 90,
    }
}

/// # Safety
/// Projection points to an initialized, readable bounded span; output is writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_stream_fixture_open(
    provider: u32,
    scenario: u32,
    projection: *const u8,
    length: usize,
    rows: usize,
    bytes: usize,
    output: *mut *mut c_void,
) -> u32 {
    if output.is_null() {
        return 1;
    }
    // SAFETY: output is a live writable out pointer by the fixture caller contract.
    unsafe {
        *output = std::ptr::null_mut();
    }
    protected(|| {
        if projection.is_null() || length == 0 || length > 4 * 1024 * 1024 {
            return Err(error(ErrorKind::Shape, "invalid projection span"));
        }
        // SAFETY: caller retains initialized projection bytes for this synchronous call.
        let projection = unsafe { std::slice::from_raw_parts(projection, length) };
        let fixture = runtime().block_on(create(provider, scenario, projection, rows, bytes))?;
        // SAFETY: publication transfers exactly one Box to the writable output.
        unsafe {
            *output = Box::into_raw(Box::new(fixture)).cast();
        }
        Ok(())
    })
}
/// # Safety
/// Owner is this fixture's live Box protected by a managed call lease.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_stream_fixture_fetch(owner: *mut c_void) -> u32 {
    protected(|| {
        // SAFETY: exact creating Box and live call lease are the fixture ABI contract.
        let fixture = unsafe { &*owner.cast::<Fixture>() };
        let mut state = fixture
            .state
            .try_lock()
            .map_err(|_| error(ErrorKind::InvalidState, "concurrent native fetch"))?;
        let cursor = state
            .cursor
            .as_mut()
            .ok_or_else(|| error(ErrorKind::InvalidState, "cursor already released"))?;
        match runtime().block_on(cursor.fetch())? {
            Fetch::Ready { .. } => Ok(()),
            Fetch::End => Err(error(ErrorKind::InvalidState, "fetch after terminal batch")),
        }
    })
}
/// # Safety
/// Live owner and writable output are retained through the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_stream_fixture_take(
    owner: *mut c_void,
    output: *mut *mut c_void,
) -> u32 {
    if output.is_null() {
        return 1;
    }
    // SAFETY: writable out pointer supplied by the fixture caller.
    unsafe {
        *output = std::ptr::null_mut();
    }
    protected(|| {
        // SAFETY: a live managed owner lease covers this exact Box.
        let fixture = unsafe { &*owner.cast::<Fixture>() };
        let mut state = fixture
            .state
            .try_lock()
            .map_err(|_| error(ErrorKind::InvalidState, "concurrent native take"))?;
        let batch = state
            .cursor
            .as_mut()
            .ok_or_else(|| error(ErrorKind::InvalidState, "cursor released"))?
            .take()?;
        // SAFETY: one result owner is transferred after all fallible checks.
        unsafe {
            *output = Box::into_raw(Box::new(batch)).cast();
        }
        Ok(())
    })
}
/// # Safety
/// Owner remains live under a call lease; stop never takes the cursor lock.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_stream_fixture_stop(owner: *mut c_void) {
    // SAFETY: the owner is valid for this bounded call.
    let fixture = unsafe { &*owner.cast::<Fixture>() };
    (fixture.stop)();
}
/// # Safety
/// Owner remains live while the test worker joins provider cleanup.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_stream_fixture_join(owner: *mut c_void) -> u32 {
    protected(|| {
        // SAFETY: a call lease retains the fixture throughout the blocking test-only join.
        let fixture = unsafe { &*owner.cast::<Fixture>() };
        let mut state = fixture
            .state
            .try_lock()
            .map_err(|_| error(ErrorKind::InvalidState, "concurrent native join"))?;
        if let Some(mut cursor) = state.cursor.take() {
            let receipt = runtime().block_on(cursor.close())?;
            state.stats[7] = receipt.provider_rows_peak as u64;
            state.stats[8] = u64::from(receipt.native_retired);
            state.stats[9] = u64::from(receipt.disposition == ConnectionDisposition::Usable);
            state.stats[10] = u64::from(receipt.canceled);
            drop(cursor);
            let mut stats = state.stats;
            runtime().block_on(state.provider.close(
                &mut stats,
                receipt.disposition == ConnectionDisposition::Usable,
            ))?;
            state.stats = stats;
        }
        Ok(())
    })
}
/// # Safety
/// Owner is live and output addresses 20 writable u64 elements.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_stream_fixture_stats(owner: *mut c_void, output: *mut u64) -> u32 {
    protected(|| {
        // SAFETY: the retained fixture and writable output satisfy the test ABI contract.
        let fixture = unsafe { &*owner.cast::<Fixture>() };
        let mut values = fixture
            .state
            .lock()
            .map_err(|_| error(ErrorKind::InvalidState, "fixture lock poisoned"))?
            .stats;
        let b = fixture.budget.snapshot();
        values[0] = b.live_cursors as u64;
        values[1] = b.reserved_bytes as u64;
        values[2] = b.peak_reserved_bytes as u64;
        values[3] = b.live_batches as u64;
        values[4] = b.live_batch_bytes as u64;
        values[5] = b.peak_batch_bytes as u64;
        values[6] = b.batches_taken as u64;
        values[19] = OWNERS.load(Ordering::SeqCst) as u64;
        // SAFETY: caller supplies space for exactly the fixed observation record.
        unsafe {
            std::ptr::copy_nonoverlapping(values.as_ptr(), output, values.len());
        }
        Ok(())
    })
}
/// # Safety
/// Batch is live; output addresses three writable u64 values.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_stream_fixture_batch_info(batch: *mut c_void, output: *mut u64) {
    // SAFETY: the managed batch call lease retains this creating-module Box.
    let batch = unsafe { &*batch.cast::<OwnedBatch>() };
    let values = [
        batch.bytes().len() as u64,
        batch.rows() as u64,
        u64::from(batch.end()),
    ];
    // SAFETY: caller provides the exact writable observation extent.
    unsafe {
        std::ptr::copy_nonoverlapping(values.as_ptr(), output, 3);
    }
}
/// # Safety
/// Batch is live, destination has writable capacity, and spans do not overlap.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_stream_fixture_batch_copy(
    batch: *mut c_void,
    destination: *mut u8,
    capacity: usize,
) -> u32 {
    // SAFETY: the retained exact batch Box is the fixture ABI's input contract.
    let batch = unsafe { &*batch.cast::<OwnedBatch>() };
    if capacity < batch.bytes().len() || destination.is_null() {
        return 6;
    }
    // SAFETY: complete capacity was checked and the two allocations are disjoint.
    unsafe {
        std::ptr::copy_nonoverlapping(batch.bytes().as_ptr(), destination, batch.bytes().len());
    }
    0
}
/// # Safety
/// The sole batch owner is surrendered exactly once after all call leases end.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_stream_fixture_batch_release(batch: *mut c_void) {
    // SAFETY: only the SafeHandle's unique creating-module release invokes this destructor.
    drop(unsafe { Box::from_raw(batch.cast::<OwnedBatch>()) });
}
/// # Safety
/// The sole fixture owner is surrendered exactly once, after active calls finish.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_stream_fixture_release(owner: *mut c_void) {
    // SAFETY: only the unique SafeHandle release owns this Box. Cursor Drop transfers
    // unfinished cleanup to the resident test runtime instead of joining here.
    drop(unsafe { Box::from_raw(owner.cast::<Fixture>()) });
}
/// Returns test fixture wrapper owners, independently from native cleanup receipts.
#[unsafe(no_mangle)]
pub extern "C" fn nw_stream_fixture_owners() -> usize {
    OWNERS.load(Ordering::SeqCst)
}
