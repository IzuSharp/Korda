//! Actual provider scenarios shared by native tests and the test-only JIT/AOT ABI fixture.
use futures_util::StreamExt;
use nativeweave_data_codecs::read_sqlite;
use nativeweave_data_contracts::{
    Codec, ConnectionDisposition, LocalLifetime, Profile, TransactionOutcome,
    wire::{Batch, Descriptor, Expectation},
};
use nativeweave_data_sqlite::{
    Admission, BeginMode, Cancellation, ErrorKind, JournalMode, Limits, OpenMode, Options,
    ParameterInput, SQLITE_VERSION, Session, SqliteTransactionDriver,
};
use nativeweave_data_transactions::{
    AdmissionToken, CleanupStatus, Error as TransactionError, Isolation, Lifecycle,
    Options as TransactionOptions, ProtocolState, Transaction,
};
use sqlx::{
    Column, Connection, Row, SqlSafeStr, SqlitePool, Statement, TypeInfo,
    migrate::{MigrateError, Migration, MigrationType, Migrator},
};
use std::{
    path::Path,
    sync::{
        Arc,
        atomic::{AtomicUsize, Ordering},
    },
    time::Duration,
};

pub type TestResult<T = ()> = std::result::Result<T, Box<dyn std::error::Error + Send + Sync>>;
pub const CASES: u32 = 14;
const WAIT: Duration = Duration::from_secs(15);

fn limits() -> Limits {
    Limits {
        max_connections: 1,
        ..Limits::default()
    }
}
async fn pool(path: &Path, options: &Options, limits: Limits) -> TestResult<SqlitePool> {
    Ok(limits
        .pool_options()?
        .connect_with(options.connect_options(path, limits)?)
        .await?)
}
async fn session(
    pool: &SqlitePool,
    options: Options,
    limits: Limits,
) -> TestResult<(Session, Admission)> {
    let admission = Admission::new(limits)?;
    let session = Session::start(
        pool.acquire().await?,
        options,
        admission.clone(),
        &tokio::runtime::Handle::current(),
    )?;
    Ok((session, admission))
}
async fn execute(session: &Session, sql: &str) -> TestResult<u64> {
    Ok(tokio::time::timeout(WAIT, session.execute(sql, None, Cancellation::default())).await??)
}
async fn scalar(session: &Session, sql: &str) -> TestResult<i64> {
    let mut rows = session.stream(sql, None, Cancellation::default())?;
    let receipt = rows.receipt();
    let value = tokio::time::timeout(WAIT, rows.next())
        .await?
        .ok_or("missing row")??
        .try_get::<i64, _>(0)?;
    assert!(tokio::time::timeout(WAIT, rows.next()).await?.is_none());
    rows.stop_control().request_stop();
    let final_state = receipt.join().await?;
    assert!(
        final_state.native_retired && !final_state.stopped,
        "natural EOF wins a late cancellation request"
    );
    Ok(value)
}
async fn mark(session: &Session) -> TestResult {
    execute(
        session,
        "CREATE TEMP TABLE physical_marker(value INTEGER NOT NULL)",
    )
    .await?;
    execute(session, "INSERT INTO physical_marker VALUES (731)").await?;
    Ok(())
}
async fn same(session: &Session) -> TestResult {
    assert_eq!(
        scalar(session, "SELECT value FROM physical_marker").await?,
        731
    );
    Ok(())
}
async fn close(session: Session, pool: SqlitePool) -> TestResult {
    let closed = tokio::time::timeout(WAIT, session.close()).await??;
    assert!(closed.worker_shutdown_acknowledged && closed.physical_lease_released);
    assert!(!closed.os_thread_joined && !closed.pool_return_completed);
    drop(session);
    tokio::time::timeout(WAIT, pool.close()).await?;
    Ok(())
}
async fn transaction(
    session: &Session,
    options: TransactionOptions,
) -> TestResult<Transaction<SqliteTransactionDriver>> {
    Ok(Transaction::begin(
        SqliteTransactionDriver::new(session.clone()),
        options,
        &AdmissionToken::default(),
    )
    .await?)
}
async fn tx_execute(
    tx: &Transaction<SqliteTransactionDriver>,
    session: &Session,
    sql: &str,
) -> TestResult<u64> {
    let lease = tx.admit_command(false, &AdmissionToken::default())?;
    let result = execute(session, sql).await;
    lease.complete(result.is_err(), ProtocolState::Clean);
    result
}

pub async fn run(case: u32, root: &Path) -> TestResult {
    std::fs::create_dir_all(root)?;
    let file = root.join(format!("case-{case}.db"));
    match case {
        1 => profile(&file).await?,
        2 => memory().await?,
        3 => codecs(&file).await?,
        4 => savepoints(&file).await?,
        5 => cancellation(&file).await?,
        6 => early_stream(&file).await?,
        7 => pool_retirement(&file).await?,
        8 => lost_commit(&file).await?,
        9 => admission(&file).await?,
        10 => concurrency(&file).await?,
        11 => migrations(&file).await?,
        12 => read_only(&file).await?,
        13 => last_parent(&file).await?,
        14 => row_handoff(&file).await?,
        _ => return Err("unknown B03 scenario".into()),
    }
    println!("PASS B03 native scenario {case}");
    Ok(())
}

async fn profile(file: &Path) -> TestResult {
    let options = Options {
        busy_timeout: Duration::from_millis(40),
        ..Options::default()
    };
    let limits = Limits {
        max_connections: 2,
        ..limits()
    };
    let pool = pool(file, &options, limits).await?;
    let mut a = pool.acquire().await?;
    let mut b = pool.acquire().await?;
    for connection in [&mut *a, &mut *b] {
        assert_eq!(
            sqlx::query_scalar::<_, String>("SELECT sqlite_version()")
                .fetch_one(&mut *connection)
                .await?,
            SQLITE_VERSION
        );
        assert_eq!(
            sqlx::query_scalar::<_, i64>("PRAGMA foreign_keys")
                .fetch_one(&mut *connection)
                .await?,
            1
        );
        assert_eq!(
            sqlx::query_scalar::<_, String>("PRAGMA journal_mode")
                .fetch_one(&mut *connection)
                .await?,
            "wal"
        );
        assert_eq!(
            sqlx::query_scalar::<_, i64>("PRAGMA synchronous")
                .fetch_one(&mut *connection)
                .await?,
            2
        );
        assert_eq!(
            sqlx::query_scalar::<_, i64>("PRAGMA busy_timeout")
                .fetch_one(&mut *connection)
                .await?,
            40
        );
        assert_eq!(
            sqlx::query_scalar::<_, i64>("SELECT sqlite_compileoption_used('THREADSAFE=1')")
                .fetch_one(&mut *connection)
                .await?,
            1
        );
    }
    a.close().await?;
    b.close().await?;
    pool.close().await;
    assert!(
        Options::default()
            .connect_options(Path::new(""), limits)
            .is_err()
    );
    assert!(
        Options::default()
            .connect_options(Path::new("file:unbounded?cache=shared"), limits)
            .is_err()
    );
    Ok(())
}

async fn memory() -> TestResult {
    let options = Options {
        mode: OpenMode::Memory,
        journal: JournalMode::Memory,
        ..Options::default()
    };
    assert!(
        options
            .connect_options(Path::new(":memory:"), Limits::default())
            .is_err()
    );
    assert!(
        Options {
            journal: JournalMode::Wal,
            ..options.clone()
        }
        .connect_options(Path::new(":memory:"), limits())
        .is_err()
    );
    assert!(
        Options::default()
            .connect_options(Path::new(":memory:"), limits())
            .is_err()
    );
    let first = pool(Path::new(":memory:"), &options, limits()).await?;
    let second = pool(Path::new(":memory:"), &options, limits()).await?;
    let (s, _) = session(&first, options.clone(), limits()).await?;
    mark(&s).await?;
    execute(&s, "CREATE TABLE private_value(value INTEGER)").await?;
    let returned = s.return_to_pool().await?;
    assert!(returned.pool_return_completed && !returned.worker_shutdown_acknowledged);
    drop(s);
    let (reused, _) = session(&first, options.clone(), limits()).await?;
    same(&reused).await?;
    let (isolated, _) = session(&second, options, limits()).await?;
    assert_eq!(
        scalar(
            &isolated,
            "SELECT count(*) FROM sqlite_master WHERE name='private_value'"
        )
        .await?,
        0
    );
    close(reused, first).await?;
    close(isolated, second).await
}

async fn codecs(file: &Path) -> TestResult {
    let pool = pool(file, &Options::default(), limits()).await?;
    let (s, budget) = session(&pool, Options::default(), limits()).await?;
    let shape = Expectation {
        kind: 1,
        profile: Profile::Sqlite,
        hash: [7; 32],
        entity_id: 0,
        projection_id: 0,
        exact_rows: Some(1),
        descriptors: vec![
            Descriptor {
                id: 1,
                codec: Codec {
                    id: 3,
                    configuration: None,
                    nullable: false,
                },
                bound_codec: 3,
            },
            Descriptor {
                id: 2,
                codec: Codec {
                    id: 5,
                    configuration: None,
                    nullable: true,
                },
                bound_codec: 5,
            },
        ],
    };
    let value = 9_223_372_036_854_775_000i64.to_le_bytes();
    let bytes = Batch::encode(&shape, 1, &[Some(&value), None])
        .map_err(nativeweave_data_codecs::CodecError::from)?;
    let mut rows = s.stream(
        "SELECT ?1 AS number, ?2 AS optional",
        Some(ParameterInput {
            bytes: &bytes,
            expected: shape.clone(),
        }),
        Cancellation::default(),
    )?;
    let receipt = rows.receipt();
    let row = rows.next().await.ok_or("typed row missing")??;
    assert_eq!(
        read_sqlite(&row, 0, &shape.descriptors[0].codec, 1024)?,
        Some(value.to_vec())
    );
    assert_eq!(
        read_sqlite(&row, 1, &shape.descriptors[1].codec, 1024)?,
        None
    );
    assert!(read_sqlite(&row, 1, &shape.descriptors[0].codec, 1024).is_err());
    assert!(rows.next().await.is_none());
    receipt.join().await?;
    assert_eq!(budget.snapshot().retained_input_bytes, 0);
    let mut wrong = shape.clone();
    wrong.hash[0] ^= 1;
    assert!(
        s.stream(
            "SELECT ?1, ?2",
            Some(ParameterInput {
                bytes: &bytes,
                expected: wrong
            }),
            Cancellation::default()
        )
        .is_err()
    );
    close(s, pool).await
}

async fn savepoints(file: &Path) -> TestResult {
    let pool = pool(file, &Options::default(), limits()).await?;
    let (s, _) = session(&pool, Options::default(), limits()).await?;
    mark(&s).await?;
    execute(&s, "CREATE TABLE parent(id INTEGER PRIMARY KEY)").await?;
    execute(&s, "CREATE TABLE child(id INTEGER REFERENCES parent(id))").await?;
    let tx = transaction(&s, TransactionOptions::default()).await?;
    let token = AdmissionToken::default();
    let savepoint = tx.create_savepoint(&token).await?;
    let error = tx_execute(&tx, &s, "INSERT INTO child VALUES (9)").await;
    assert!(error.is_err());
    assert_eq!(tx.snapshot().state, Lifecycle::Failed);
    assert!(matches!(
        tx.commit(&token).await,
        Err(TransactionError::InvalidState)
    ));
    tx.rollback_savepoint(savepoint, &token).await?;
    assert_eq!(tx.snapshot().state, Lifecycle::Active);
    // Repeated rollback-to preserves exactly this tracked scope; no nested re-BEGIN.
    for n in 0..3 {
        tx_execute(&tx, &s, &format!("INSERT INTO parent VALUES ({n})")).await?;
        tx.rollback_savepoint(savepoint, &token).await?;
    }
    tx_execute(&tx, &s, "INSERT INTO parent VALUES (1)").await?;
    tx_execute(&tx, &s, "INSERT INTO child VALUES (1)").await?;
    tx.release_savepoint(savepoint, &token).await?;
    assert!(tx.rollback_savepoint(savepoint, &token).await.is_err());
    tx.commit(&token).await?;
    let final_state = tx.join().await;
    assert_eq!(final_state.outcome, TransactionOutcome::Committed);
    assert_eq!(final_state.disposition, ConnectionDisposition::Usable);
    assert_eq!(final_state.lifetime, LocalLifetime::NativeRetired);
    assert_eq!(final_state.cleanup, CleanupStatus::Succeeded);
    assert!(final_state.local_resources_released);
    same(&s).await?;
    assert_eq!(scalar(&s, "SELECT count(*) FROM parent").await?, 1);
    close(s, pool).await
}

async fn cancellation(file: &Path) -> TestResult {
    let pool = pool(file, &Options::default(), limits()).await?;
    let mut connection = pool.acquire().await?;
    let (entered_tx, entered_rx) = tokio::sync::oneshot::channel();
    let (release_tx, release_rx) = std::sync::mpsc::channel();
    let gate = std::sync::Mutex::new(Some((entered_tx, release_rx)));
    let released = Arc::new(AtomicUsize::new(0));
    let callback_released = released.clone();
    connection
        .lock_handle()
        .await?
        .create_collation("b03_worker_gate", move |left, right| {
            if let Some((entered, release)) = gate.lock().ok().and_then(|mut gate| gate.take()) {
                // TEST ONLY: this comparison runs inside SQLite's actual worker.
                // A bounded wait cannot strand it if the caller errors or times out.
                let _ = entered.send(());
                callback_released.store(
                    usize::from(release.recv_timeout(WAIT).is_ok()),
                    Ordering::Release,
                );
            }
            left.cmp(right)
        })?;
    let budget = Admission::new(limits())?;
    let s = Session::start(
        connection,
        Options::default(),
        budget.clone(),
        &tokio::runtime::Handle::current(),
    )?;
    mark(&s).await?;

    // A full adapter channel prevents the next fetch. Stop can close the row
    // stream without manufacturing a SQLx error; its retirement receipt is required.
    let before_fetch = Cancellation::default();
    let mut rows = s.stream("SELECT 1 UNION ALL SELECT 2", None, before_fetch.clone())?;
    let receipt = rows.receipt();
    tokio::time::timeout(WAIT, async {
        while rows.buffered_rows() == 0 {
            tokio::task::yield_now().await;
        }
    })
    .await?;
    before_fetch.cancel();
    let retired = tokio::time::timeout(WAIT, receipt.join()).await??;
    assert!(
        retired.native_retired
            && retired.progress_handler_removed
            && retired.stop_requested
            && retired.stopped
    );
    assert_eq!(retired.disposition, ConnectionDisposition::Usable);
    assert_eq!(retired.provider_rows_peak, 1);
    assert_eq!(
        rows.next()
            .await
            .ok_or("retained row missing")??
            .try_get::<i64, _>(0)?,
        1
    );
    assert!(
        rows.next().await.is_none(),
        "stop before the next fetch ends with EOF"
    );
    assert_eq!(rows.buffered_rows(), 0);
    drop(rows);
    same(&s).await?;
    assert_eq!(budget.snapshot().active_commands, 0);
    assert_eq!(budget.snapshot().retained_input_bytes, 0);
    println!(
        "B03 cancellation before next fetch: one retained row, EOF, stopped=true, worker retired"
    );

    let old = Cancellation::default();
    let mut rows = s.stream("WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<100000000 AND CAST(x AS TEXT) COLLATE b03_worker_gate != '') SELECT sum(x) FROM n", None, old.clone())?;
    let receipt = rows.receipt();
    tokio::time::timeout(WAIT, entered_rx).await??;
    assert!(
        receipt.snapshot().is_none(),
        "the worker is still inside the comparison"
    );
    old.cancel();
    release_tx.send(())?;
    let error = tokio::time::timeout(WAIT, rows.next())
        .await?
        .ok_or("in-worker interruption result missing")?
        .expect_err("in-worker progress cancellation must interrupt");
    assert_eq!(
        released.load(Ordering::Acquire),
        1,
        "worker barrier did not time out"
    );
    assert_eq!(error.kind, ErrorKind::Interrupted);
    let retired = tokio::time::timeout(WAIT, receipt.join()).await??;
    assert!(
        retired.native_retired
            && retired.progress_handler_removed
            && retired.stop_requested
            && retired.stopped
    );
    assert_eq!(retired.disposition, ConnectionDisposition::Usable);
    println!(
        "B03 cancellation inside worker: comparison observed, explicit release, Interrupted, worker retired"
    );
    drop(rows);
    old.cancel();
    // Exceed the installed 1,000-op progress interval with a fresh generation;
    // a constant SELECT could succeed without ever exercising its progress hook.
    assert_eq!(
        scalar(&s, "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<5000) SELECT sum(x) FROM n").await?,
        12_502_500
    );
    println!("B03 stale cancellation generation: fresh 5000-row recursive query completed");
    same(&s).await?;
    assert!(s.stream("SELECT 44", None, old).is_err());
    let before = Cancellation::default();
    before.cancel();
    assert!(matches!(
        s.stream("SELECT 45", None, before).err().map(|e| e.kind),
        Some(ErrorKind::CanceledBeforeDispatch)
    ));
    assert_eq!(budget.snapshot().active_commands, 0);
    assert_eq!(budget.snapshot().queued_commands, 0);
    assert_eq!(budget.snapshot().retained_input_bytes, 0);
    assert_eq!(s.observer().snapshot().active_operations, 0);
    close(s, pool).await
}

async fn early_stream(file: &Path) -> TestResult {
    let pool = pool(file, &Options::default(), limits()).await?;
    let (s, budget) = session(&pool, Options::default(), limits()).await?;
    mark(&s).await?;
    let metadata = s
        .describe(
            "SELECT value AS exact_name FROM physical_marker WHERE 0",
            Cancellation::default(),
        )
        .await?;
    assert_eq!(metadata.columns().len(), 1);
    assert_eq!(metadata.columns()[0].name(), "exact_name");
    assert_eq!(metadata.columns()[0].type_info().name(), "INTEGER");
    let mut rows = s.stream("WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<10000) SELECT x FROM n", None, Cancellation::default())?;
    let receipt = rows.receipt();
    assert_eq!(
        rows.next()
            .await
            .ok_or("stream first row missing")??
            .try_get::<i64, _>(0)?,
        1
    );
    let stop = rows.stop_control();
    stop.request_stop();
    // Stop must wake a full row channel without requiring the consumer to drain it.
    let retired = tokio::time::timeout(WAIT, receipt.join()).await??;
    assert!(retired.native_retired && retired.progress_handler_removed && retired.stopped);
    assert!(retired.provider_rows_peak <= 2);
    assert_eq!(retired.disposition, ConnectionDisposition::Usable);
    drop(rows);
    same(&s).await?;
    assert_eq!(budget.snapshot().retained_input_bytes, 0);
    close(s, pool).await
}

async fn pool_retirement(file: &Path) -> TestResult {
    let pool = pool(file, &Options::default(), limits()).await?;
    let (s, _) = session(&pool, Options::default(), limits()).await?;
    mark(&s).await?;
    execute(&s, "CREATE TABLE items(id INTEGER)").await?;
    let tx = transaction(&s, TransactionOptions::default()).await?;
    tx_execute(&tx, &s, "INSERT INTO items VALUES (1)").await?;
    tx.request_close();
    let snapshot = tx.join().await;
    assert_eq!(snapshot.outcome, TransactionOutcome::RolledBack);
    assert!(!snapshot.commit_attempted && snapshot.local_resources_released);
    assert_eq!(snapshot.resources.physical_lease, 0);
    let held_pool = pool.clone();
    let waiter = tokio::spawn(async move { held_pool.acquire().await });
    tokio::task::yield_now().await;
    waiter.abort();
    let _ = waiter.await;
    assert!(s.return_to_pool().await?.pool_return_completed);
    drop(s);
    let (next, single) = session(
        &pool,
        Options::default(),
        Limits {
            max_commands: 1,
            ..limits()
        },
    )
    .await?;
    same(&next).await?;
    assert_eq!(scalar(&next, "SELECT count(*) FROM items").await?, 0);
    for _ in 0..8 {
        execute(&next, "SELECT 1").await?;
        assert_eq!(
            single.snapshot().active_commands,
            0,
            "one-slot reservation retires before sequential waiter resumes"
        );
        let transaction = transaction(&next, TransactionOptions::default()).await?;
        transaction.rollback(&AdmissionToken::default()).await?;
        assert!(transaction.join().await.local_resources_released);
    }
    let pool_clone = pool.clone();
    let closing = tokio::spawn(async move { pool_clone.close().await });
    tokio::time::timeout(WAIT, async {
        while !pool.is_closed() {
            tokio::task::yield_now().await;
        }
    })
    .await?;
    assert!(!closing.is_finished());
    assert!(pool.acquire().await.is_err());
    next.close().await?;
    tokio::time::timeout(WAIT, closing).await??;
    assert_eq!(pool.size(), 0);
    assert!(!next.observer().snapshot().os_thread_joined);
    Ok(())
}

async fn lost_commit(file: &Path) -> TestResult {
    let pool = pool(file, &Options::default(), limits()).await?;
    let mut connection = pool.acquire().await?;
    let (entered_tx, entered_rx) = tokio::sync::oneshot::channel();
    let (release_tx, release_rx) = std::sync::mpsc::channel();
    let armed = Arc::new(AtomicUsize::new(0));
    let flag = armed.clone();
    let mut entered = Some(entered_tx);
    connection.lock_handle().await?.set_commit_hook(move || {
        if flag.load(Ordering::Acquire) != 0
            && let Some(signal) = entered.take()
        {
            let _ = signal.send(());
            // TEST ONLY: deterministic real SQLite COMMIT suspension; no product callback blocks.
            let _ = release_rx.recv();
        }
        true
    });
    let s = Session::start(
        connection,
        Options::default(),
        Admission::new(limits())?,
        &tokio::runtime::Handle::current(),
    )?;
    mark(&s).await?;
    execute(&s, "CREATE TABLE durable_items(id INTEGER)").await?;
    let tx = transaction(
        &s,
        TransactionOptions {
            commit_timeout: Duration::from_millis(20),
            ..TransactionOptions::default()
        },
    )
    .await?;
    tx_execute(&tx, &s, "INSERT INTO durable_items VALUES (99)").await?;
    armed.store(1, Ordering::Release);
    let committing = tx.clone();
    let waiter = tokio::spawn(async move { committing.commit(&AdmissionToken::default()).await });
    tokio::time::timeout(WAIT, entered_rx).await??;
    // Lose the public waiting future. The retained B05/actor futures keep the COMMIT owned.
    waiter.abort();
    let _ = waiter.await;
    tokio::time::timeout(WAIT, async {
        while tx.snapshot().outcome != TransactionOutcome::Unknown {
            tokio::task::yield_now().await;
        }
    })
    .await?;
    let pending = tx.close(Duration::ZERO).await;
    assert_eq!(pending.outcome, TransactionOutcome::Unknown);
    assert!(pending.commit_attempted && !pending.local_resources_released);
    assert_eq!(pending.disposition, ConnectionDisposition::Quarantined);
    assert_eq!(pending.cleanup, CleanupStatus::Pending);
    assert_eq!(pending.resources.physical_lease, 1);
    assert_eq!(s.observer().snapshot().active_operations, 1);
    release_tx.send(())?;
    let retired = tokio::time::timeout(WAIT, tx.join()).await?;
    assert_eq!(retired.outcome, TransactionOutcome::Unknown);
    assert_eq!(retired.disposition, ConnectionDisposition::Usable);
    assert!(retired.local_resources_released);
    same(&s).await?;
    assert_eq!(
        scalar(&s, "SELECT count(*) FROM durable_items WHERE id=99").await?,
        1
    );
    close(s, pool).await
}

async fn admission(file: &Path) -> TestResult {
    for acquire_timeout in [Duration::ZERO, Duration::MAX] {
        let invalid = Limits {
            acquire_timeout,
            ..limits()
        };
        assert_eq!(
            invalid.pool_options().unwrap_err().kind,
            ErrorKind::Configuration
        );
        assert!(Admission::new(invalid).is_err());
        assert!(Options::default().connect_options(file, invalid).is_err());
        assert!(
            !file.exists(),
            "invalid acquisition deadlines fail before SQLite I/O"
        );
    }
    let limits = Limits {
        max_commands: 2,
        max_input_bytes: 1024,
        max_sql_bytes: 1000,
        ..limits()
    };
    let pool = pool(file, &Options::default(), limits).await?;
    let (s, budget) = session(&pool, Options::default(), limits).await?;
    let mut first = s.stream("WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<10000) SELECT x FROM n", None, Cancellation::default())?;
    let first_receipt = first.receipt();
    first.next().await.ok_or("first admission row")??;
    let second = s.stream("SELECT 2", None, Cancellation::default())?;
    let error = s
        .stream("SELECT 3", None, Cancellation::default())
        .err()
        .ok_or("third command must reject")?;
    assert_eq!(error.kind, ErrorKind::Capacity);
    assert_eq!(
        budget.snapshot().queued_commands + budget.snapshot().active_commands,
        2
    );
    drop(first);
    drop(second);
    tokio::time::timeout(WAIT, first_receipt.join()).await??;
    tokio::time::timeout(WAIT, async {
        while budget.snapshot().retained_input_bytes != 0 {
            tokio::task::yield_now().await;
        }
    })
    .await?;
    // Separate byte budget: two valid SQL inputs fit count capacity but cannot fit bytes.
    let large = format!("SELECT 1{}", " ".repeat(700));
    let rows = s.stream(&large, None, Cancellation::default())?;
    let byte_error = s
        .stream(&large, None, Cancellation::default())
        .err()
        .ok_or("input byte overflow must reject")?;
    assert_eq!(byte_error.kind, ErrorKind::Capacity);
    drop(rows);
    close(s, pool).await
}

async fn concurrency(file: &Path) -> TestResult {
    let options = Options {
        begin: BeginMode::Immediate,
        busy_timeout: Duration::from_millis(25),
        ..Options::default()
    };
    let limits = Limits {
        max_connections: 2,
        ..limits()
    };
    let pool = pool(file, &options, limits).await?;
    let (a, _) = session(&pool, options.clone(), limits).await?;
    let (b, _) = session(&pool, options.clone(), limits).await?;
    mark(&a).await?;
    mark(&b).await?;
    execute(
        &a,
        "CREATE TABLE once_only(scope TEXT,key TEXT,hash TEXT,PRIMARY KEY(scope,key))",
    )
    .await?;
    let first = transaction(&a, TransactionOptions::default()).await?;
    tx_execute(&first, &a, "INSERT INTO once_only VALUES ('s','k','h')").await?;
    assert_eq!(
        scalar(&b, "SELECT count(*) FROM once_only").await?,
        0,
        "WAL reader sees committed snapshot"
    );
    let blocked = transaction(&b, TransactionOptions::default()).await;
    assert!(
        blocked.is_err(),
        "second IMMEDIATE writer must respect busy timeout"
    );
    assert!(b.observer().snapshot().worker_shutdown_acknowledged);
    first.commit(&AdmissionToken::default()).await?;
    first.join().await;
    // B05 conservatively discards an unsuccessfully started transaction's lease.
    // A new physical connection is explicitly a replacement, never reuse evidence.
    drop(b);
    let (b, _) = session(&pool, options, limits).await?;
    assert_eq!(
        scalar(
            &b,
            "SELECT count(*) FROM sqlite_temp_master WHERE name='physical_marker'"
        )
        .await?,
        0
    );
    mark(&b).await?;
    let contender = transaction(&b, TransactionOptions::default()).await?;
    assert_eq!(
        tx_execute(
            &contender,
            &b,
            "INSERT INTO once_only VALUES ('s','k','h') ON CONFLICT(scope,key) DO NOTHING"
        )
        .await?,
        0
    );
    assert_eq!(
        scalar(
            &b,
            "SELECT count(*) FROM once_only WHERE scope='s' AND key='k' AND hash='h'"
        )
        .await?,
        1
    );
    contender.commit(&AdmissionToken::default()).await?;
    contender.join().await;
    same(&a).await?;
    same(&b).await?;
    a.close().await?;
    b.close().await?;
    pool.close().await;
    Ok(())
}

async fn migrations(file: &Path) -> TestResult {
    let pool = pool(file, &Options::default(), limits()).await?;
    let mut connection = pool.acquire().await?;
    let migration = |version, text: &'static str| {
        Migration::new(
            version,
            "B03 native substrate probe".into(),
            MigrationType::Simple,
            text.into_sql_str(),
            false,
        )
    };
    let good = Migrator::with_migrations(vec![migration(
        1,
        "CREATE TABLE migrated(id INTEGER PRIMARY KEY)",
    )]);
    assert!(!good.iter().any(|m| m.no_tx));
    let mut tx = connection.begin_with("BEGIN IMMEDIATE").await?;
    good.run(&mut *tx).await?;
    tx.commit().await?;
    let mismatch = Migrator::with_migrations(vec![migration(
        1,
        "CREATE TABLE migrated(id INTEGER PRIMARY KEY, changed TEXT)",
    )]);
    let mut tx = connection.begin_with("BEGIN IMMEDIATE").await?;
    assert!(matches!(
        mismatch.run(&mut *tx).await,
        Err(MigrateError::VersionMismatch(1))
    ));
    tx.rollback().await?;
    let failing = Migrator::with_migrations(vec![
        migration(1, "CREATE TABLE migrated(id INTEGER PRIMARY KEY)"),
        migration(
            2,
            "CREATE TABLE must_rollback(id INTEGER); INSERT INTO table_that_does_not_exist VALUES (1)",
        ),
    ]);
    let mut tx = connection.begin_with("BEGIN IMMEDIATE").await?;
    assert!(failing.run(&mut *tx).await.is_err());
    tx.rollback().await?;
    assert_eq!(
        sqlx::query_scalar::<_, i64>(
            "SELECT count(*) FROM sqlite_master WHERE name='must_rollback'"
        )
        .fetch_one(&mut *connection)
        .await?,
        0
    );
    assert_eq!(
        sqlx::query_scalar::<_, i64>("SELECT count(*) FROM _sqlx_migrations")
            .fetch_one(&mut *connection)
            .await?,
        1
    );
    connection.close().await?;
    pool.close().await;
    Ok(())
}

async fn read_only(file: &Path) -> TestResult {
    let pool = pool(file, &Options::default(), limits()).await?;
    let (s, _) = session(&pool, Options::default(), limits()).await?;
    execute(&s, "CREATE TABLE readonly_probe(id INTEGER)").await?;
    let unsupported = transaction(
        &s,
        TransactionOptions {
            isolation: Some(Isolation::ReadCommitted),
            ..TransactionOptions::default()
        },
    )
    .await;
    assert!(unsupported.is_err());
    let tx = transaction(
        &s,
        TransactionOptions {
            read_only: true,
            ..TransactionOptions::default()
        },
    )
    .await?;
    assert!(
        tx_execute(&tx, &s, "INSERT INTO readonly_probe VALUES (1)")
            .await
            .is_err()
    );
    tx.rollback(&AdmissionToken::default()).await?;
    tx.join().await;
    let next = transaction(&s, TransactionOptions::default()).await?;
    tx_execute(&next, &s, "INSERT INTO readonly_probe VALUES (2)").await?;
    next.commit(&AdmissionToken::default()).await?;
    next.join().await;
    assert_eq!(scalar(&s, "SELECT count(*) FROM readonly_probe").await?, 1);
    close(s, pool).await
}

async fn last_parent(file: &Path) -> TestResult {
    struct CloseGate {
        entered: Option<tokio::sync::oneshot::Sender<()>>,
        release: std::sync::mpsc::Receiver<()>,
    }
    impl Drop for CloseGate {
        fn drop(&mut self) {
            if let Some(entered) = self.entered.take() {
                let _ = entered.send(());
                // TEST ONLY: SQLx drops this callback capture while destroying the
                // actual ConnectionState, before its worker shutdown ACK is sent.
                let _ = self.release.recv();
            }
        }
    }
    let pool = pool(file, &Options::default(), limits()).await?;
    let mut connection = pool.acquire().await?;
    let (entered_tx, entered_rx) = tokio::sync::oneshot::channel();
    let (release_tx, release_rx) = std::sync::mpsc::channel();
    let gate = CloseGate {
        entered: Some(entered_tx),
        release: release_rx,
    };
    connection.lock_handle().await?.set_commit_hook(move || {
        std::hint::black_box(&gate);
        true
    });
    let s = Session::start(
        connection,
        Options::default(),
        Admission::new(limits())?,
        &tokio::runtime::Handle::current(),
    )?;
    execute(&s, "CREATE TABLE final_parent(id INTEGER)").await?;
    let tx = transaction(&s, TransactionOptions::default()).await?;
    tx_execute(&tx, &s, "INSERT INTO final_parent VALUES (1)").await?;
    let observer = s.observer();
    drop(s);
    let committing = tx.clone();
    let joining = tokio::spawn(async move {
        committing.commit(&AdmissionToken::default()).await?;
        Ok::<_, TransactionError>(committing.join().await)
    });
    tokio::time::timeout(WAIT, entered_rx).await??;
    let premature = tx.snapshot().local_resources_released;
    let ack = observer.snapshot().worker_shutdown_acknowledged;
    let size = pool.size();
    println!(
        "B03 last-parent barrier: local_released={premature}, worker_ack={ack}, physical_pool_size={size}"
    );
    release_tx.send(())?;
    let final_state = tokio::time::timeout(WAIT, joining).await???;
    let worker = tokio::time::timeout(WAIT, observer.join()).await??;
    assert!(
        !premature,
        "transaction released its last parent lease before actual worker closure"
    );
    assert!(
        !ack && size == 1,
        "the test must observe the retained physical worker"
    );
    assert!(worker.worker_shutdown_acknowledged && worker.physical_lease_released);
    assert_eq!(pool.size(), 0);
    assert!(final_state.local_resources_released);
    assert_eq!(final_state.outcome, TransactionOutcome::Committed);
    assert_eq!(final_state.resources.physical_lease, 0);
    assert!(!worker.os_thread_joined);
    pool.close().await;
    Ok(())
}

async fn row_handoff(file: &Path) -> TestResult {
    let pool = pool(file, &Options::default(), limits()).await?;
    let (s, budget) = session(&pool, Options::default(), limits()).await?;
    mark(&s).await?;
    for round in 0..4 {
        let mut rows = s.stream(
            "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<10000) SELECT x FROM n",
            None,
            Cancellation::default(),
        )?;
        let receipt = rows.receipt();
        let mut count = 0;
        let mut observed_peak = 0;
        while let Some(value) = tokio::time::timeout(WAIT, rows.next()).await? {
            observed_peak = observed_peak.max(rows.buffered_rows());
            count += 1;
            assert_eq!(value?.try_get::<i64, _>(0)?, count);
            if count % 7 == 0 {
                tokio::task::yield_now().await;
            }
        }
        let retired = tokio::time::timeout(WAIT, receipt.join()).await??;
        println!(
            "B03 row handoff round={round} rows={count} provider_peak={} sampled_peak={observed_peak}",
            retired.provider_rows_peak
        );
        assert_eq!(count, 10000);
        assert!(
            retired.provider_rows_peak <= 2 && observed_peak <= 2,
            "provider row budget includes the receiver handoff"
        );
        assert!(retired.native_retired && !retired.stopped);
        assert_eq!(retired.disposition, ConnectionDisposition::Usable);
        assert_eq!(rows.buffered_rows(), 0);
        assert_eq!(budget.snapshot().active_commands, 0);
        assert_eq!(budget.snapshot().retained_input_bytes, 0);
    }
    same(&s).await?;
    close(s, pool).await
}
