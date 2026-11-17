use super::*;

// These product counters cover both providers in this test process. Isolate
// independent cases while retaining each case's actual concurrent operations.
#[cfg(any(feature = "postgres", feature = "sqlite"))]
static COUNTER_CASE: tokio::sync::Mutex<()> = tokio::sync::Mutex::const_new(());

#[cfg(feature = "postgres")]
#[tokio::test]
async fn postgres_connection_deadline_is_independent_of_command_deadline() {
    let _counters = COUNTER_CASE.lock().await;
    // A real TCP peer accepts PostgreSQL's startup packet but sends no greeting.
    // This isolates connect policy without a DNS/server failure racing the timer.
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let address = listener.local_addr().unwrap();
    let (stop, stopped) = tokio::sync::oneshot::channel::<()>();
    let peer = tokio::spawn(async move {
        let (socket, _) = listener.accept().await.unwrap();
        let _ = stopped.await;
        drop(socket);
    });
    let mut policy = limits();
    policy.connect_timeout = Duration::from_millis(100);
    policy.command_timeout = Duration::from_secs(3);
    let options = sqlx::postgres::PgConnectOptions::new()
        .host("127.0.0.1")
        .port(address.port())
        .username("deadline_probe")
        .database("deadline_probe")
        .ssl_mode(sqlx::postgres::PgSslMode::Disable);
    let began = std::time::Instant::now();
    let result = run(
        ConnectionOptions::Postgres(options),
        request(0, &["SELECT 1"]),
        policy,
        Arc::new(Cancellation::default()),
    )
    .await;
    let elapsed = began.elapsed();
    let _ = stop.send(());
    peer.await.unwrap();
    assert_eq!(result.unwrap_err().kind, ErrorKind::Timeout);
    assert!(
        elapsed < Duration::from_secs(1),
        "connect used command timeout: {elapsed:?}"
    );
}

#[test]
fn migration_contract_rejects_escape_and_immutable_identity_is_exact() {
    for sql in [
        "BEGIN; SELECT 1",
        "CREATE TABLE t(a); COMMIT;",
        "-- x\nSAVEPOINT bad",
        "/* nested /* comment */ */ ROLLBACK",
    ] {
        assert!(Migration::new(1, "test".into(), sql.into(), false).is_err());
    }
    let x = Migration::new(
        1,
        "a".into(),
        "DO $$ BEGIN RAISE NOTICE 'COMMIT'; END $$;".into(),
        false,
    )
    .unwrap();
    assert_eq!(x.native().checksum.len(), 48);
    assert_eq!(x.sql_hash.len(), 32);
    assert!(Bundle::new(0, vec![x.clone(), x], [0; 32]).is_err());
}

fn limits() -> Limits {
    Limits {
        rows: 1000,
        bytes: 1024 * 1024,
        field_bytes: 1024 * 1024,
        command_timeout: Duration::from_secs(10),
        connect_timeout: Duration::from_secs(2),
    }
}
fn request(provider: u32, sql: &[&str]) -> Request {
    let entries = sql
        .iter()
        .enumerate()
        .map(|(i, sql)| {
            Migration::new(
                (i + 1) as i64,
                format!("migration {}", i + 1),
                sql.to_string(),
                false,
            )
            .unwrap()
        })
        .collect();
    Request {
        action: 0,
        bundle: Some(Bundle::new(provider, entries, [1; 32]).unwrap()),
        target: None,
        lock_timeout: Duration::from_secs(2),
        allow_non_transactional: false,
        schema: None,
    }
}
#[cfg(feature = "sqlite")]
fn sqlite_path(label: &str) -> std::path::PathBuf {
    std::env::temp_dir().join(format!("nw-b08-{}-{label}.db", std::process::id()))
}
#[cfg(feature = "sqlite")]
fn sqlite_options(path: &std::path::Path) -> sqlx::sqlite::SqliteConnectOptions {
    sqlx::sqlite::SqliteConnectOptions::new()
        .filename(path)
        .create_if_missing(true)
        .journal_mode(sqlx::sqlite::SqliteJournalMode::Wal)
        .busy_timeout(Duration::from_secs(2))
        .foreign_keys(true)
}
#[cfg(feature = "sqlite")]
#[tokio::test]
async fn sqlite_three_independent_upstream_depth_oracles_and_atomic_adapter() {
    use sqlx::{Connection, Executor};
    let _counters = COUNTER_CASE.lock().await;
    let path = sqlite_path("depth");
    let _ = std::fs::remove_file(&path);
    let options = sqlite_options(&path);
    let manifest = request(1, &["CREATE TABLE native_depth(id INTEGER PRIMARY KEY)"]);
    let migrator = manifest.bundle.as_ref().unwrap().migrator(None);
    let mut connection = sqlx::SqliteConnection::connect_with(&options)
        .await
        .unwrap();
    connection.execute("BEGIN IMMEDIATE").await.unwrap();
    let raw = migrator.run(&mut connection).await.unwrap_err();
    assert!(raw.to_string().contains("transaction"));
    connection.execute("ROLLBACK").await.unwrap();
    {
        let mut tx = connection.begin_with("BEGIN IMMEDIATE").await.unwrap();
        migrator.run(&mut *tx).await.unwrap();
        tx.rollback().await.unwrap();
    }
    assert_eq!(
        sqlx::query_scalar::<_, i64>(
            "SELECT count(*) FROM sqlite_schema WHERE name IN ('native_depth','_sqlx_migrations')"
        )
        .fetch_one(&mut connection)
        .await
        .unwrap(),
        0
    );
    {
        let mut tx = connection.begin_with("BEGIN IMMEDIATE").await.unwrap();
        migrator.run(&mut *tx).await.unwrap();
        tx.commit().await.unwrap();
    }
    assert_eq!(
        sqlx::query_scalar::<_, i64>("SELECT count(*) FROM _sqlx_migrations")
            .fetch_one(&mut connection)
            .await
            .unwrap(),
        1
    );
    connection.close().await.unwrap();
    let broken = sqlite_path("atomic");
    let _ = std::fs::remove_file(&broken);
    let options = sqlite_options(&broken);
    let failed = request(
        1,
        &[
            "CREATE TABLE first_entry(id INTEGER PRIMARY KEY)",
            "INSERT INTO absent_table VALUES(1)",
        ],
    );
    assert!(
        run(
            ConnectionOptions::Sqlite(options.clone()),
            failed,
            limits(),
            Arc::default()
        )
        .await
        .is_err()
    );
    let mut observer = sqlx::SqliteConnection::connect_with(&options)
        .await
        .unwrap();
    assert_eq!(sqlx::query_scalar::<_,i64>("SELECT count(*) FROM sqlite_schema WHERE name IN ('first_entry','_sqlx_migrations','_nativeweave_migration_phases')").fetch_one(&mut observer).await.unwrap(),0);
    observer.close().await.unwrap();
    std::fs::remove_file(path).unwrap();
    std::fs::remove_file(broken).unwrap();
}
#[cfg(feature = "sqlite")]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn sqlite_contention_idempotency_checksum_cancellation_and_limits() {
    use sqlx::Connection;
    let _counters = COUNTER_CASE.lock().await;
    let path = sqlite_path("contention");
    let _ = std::fs::remove_file(&path);
    let options = sqlite_options(&path);
    let manifest = request(
        1,
        &[
            "CREATE TABLE contended(id INTEGER PRIMARY KEY, name TEXT NOT NULL)",
            "INSERT INTO contended VALUES(1,'one')",
        ],
    );
    // Configure WAL once; the contention oracle is the writer lock, not two
    // simultaneous journal-mode changes while creating an empty file.
    sqlx::SqliteConnection::connect_with(&options)
        .await
        .unwrap()
        .close()
        .await
        .unwrap();
    let (a, b) = tokio::join!(
        run(
            ConnectionOptions::Sqlite(options.clone()),
            manifest.clone(),
            limits(),
            Arc::default()
        ),
        run(
            ConnectionOptions::Sqlite(options.clone()),
            manifest.clone(),
            limits(),
            Arc::default()
        )
    );
    let applied = |result| match result {
        Output::Report {
            applied,
            current: Some(2),
        } => applied,
        _ => panic!("report"),
    };
    assert_eq!(applied(a.unwrap()) + applied(b.unwrap()), 2);
    let mut status = manifest.clone();
    status.action = 1;
    let Output::Status(values) = run(
        ConnectionOptions::Sqlite(options.clone()),
        status,
        limits(),
        Arc::default(),
    )
    .await
    .unwrap() else {
        panic!("status")
    };
    assert!(values.iter().all(|x| x.applied && x.checksum.len() == 96));
    let changed = request(
        1,
        &[
            "CREATE TABLE different(id INTEGER)",
            "INSERT INTO contended VALUES(1,'one')",
        ],
    );
    assert_eq!(
        run(
            ConnectionOptions::Sqlite(options.clone()),
            changed,
            limits(),
            Arc::default()
        )
        .await
        .unwrap_err()
        .kind,
        ErrorKind::Checksum
    );
    let mut catalog = manifest.clone();
    catalog.action = 3;
    catalog.bundle = None;
    let mut tiny = limits();
    tiny.rows = 2;
    assert_eq!(
        run(
            ConnectionOptions::Sqlite(options.clone()),
            catalog,
            tiny,
            Arc::default()
        )
        .await
        .unwrap_err()
        .kind,
        ErrorKind::Capacity
    );
    let mut nontransactional = manifest.clone();
    nontransactional.bundle.as_mut().unwrap().migrations[0].non_transactional = true;
    nontransactional.allow_non_transactional = true;
    let before = CLOSED_CONNECTIONS.load(Ordering::Acquire);
    assert_eq!(
        run(
            ConnectionOptions::Sqlite(options.clone()),
            nontransactional,
            limits(),
            Arc::default()
        )
        .await
        .unwrap_err()
        .kind,
        ErrorKind::Capability
    );
    assert_eq!(CLOSED_CONNECTIONS.load(Ordering::Acquire), before);
    let cancel_path = sqlite_path("cancel");
    let _ = std::fs::remove_file(&cancel_path);
    let cancel_options = sqlite_options(&cancel_path);
    let manifest = request(
        1,
        &[
            "CREATE TABLE canceled AS WITH RECURSIVE numbers(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM numbers WHERE n<100000000) SELECT n FROM numbers",
        ],
    );
    let token = Arc::new(Cancellation::default());
    let stop = token.clone();
    let work = tokio::spawn(async move {
        run(
            ConnectionOptions::Sqlite(cancel_options),
            manifest,
            limits(),
            token,
        )
        .await
    });
    tokio::time::sleep(Duration::from_millis(30)).await;
    stop.cancel();
    let canceled = work.await.unwrap().unwrap_err();
    assert_eq!(canceled.kind, ErrorKind::Canceled);
    assert!(
        canceled.cleanup.is_some(),
        "actual SQLite interruption also reports failed tracked rollback"
    );
    let mut observer = sqlx::SqliteConnection::connect_with(&sqlite_options(&cancel_path))
        .await
        .unwrap();
    assert_eq!(
        sqlx::query_scalar::<_, i64>("SELECT count(*) FROM sqlite_schema WHERE name='canceled'")
            .fetch_one(&mut observer)
            .await
            .unwrap(),
        0
    );
    observer.close().await.unwrap();
    assert_eq!(LIVE_CONNECTIONS.load(Ordering::Acquire), 0);
    std::fs::remove_file(path).unwrap();
    std::fs::remove_file(cancel_path).unwrap();
}
#[cfg(feature = "postgres")]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn postgres_actual_lock_cleanup_permissions_cancel_and_partial_recovery() {
    use sqlx::{Connection, Executor, Row};
    let _counters = COUNTER_CASE.lock().await;
    let uri = std::env::var("NW_TEST_POSTGRES")
        .expect("B08 PostgreSQL product test requires a real isolated server");
    let base: sqlx::postgres::PgConnectOptions = uri.parse().unwrap();
    let name = format!("b08_native_{}", std::process::id());
    let mut admin = sqlx::PgConnection::connect_with(&base).await.unwrap();
    admin
        .execute(AssertSqlSafe(format!("CREATE DATABASE {name}")))
        .await
        .unwrap();
    let options = base.clone().database(&name);
    let mut connection = sqlx::PgConnection::connect_with(&options).await.unwrap();
    let pid: i32 = sqlx::query_scalar("SELECT pg_backend_pid()")
        .fetch_one(&mut connection)
        .await
        .unwrap();
    let manifest = request(0, &["CREATE TABLE seed(id INTEGER PRIMARY KEY)"]);
    // Direct upstream oracle: SQLx itself leaves the advisory lock on mismatch.
    manifest
        .bundle
        .as_ref()
        .unwrap()
        .migrator(None)
        .run(&mut connection)
        .await
        .unwrap();
    let mut changed = request(0, &["CREATE TABLE other(id INTEGER)"])
        .bundle
        .unwrap()
        .migrator(None);
    changed.set_locking(true);
    assert!(changed.run(&mut connection).await.is_err());
    let held: i64 =
        sqlx::query_scalar("SELECT count(*) FROM pg_locks WHERE locktype='advisory' AND pid=$1")
            .bind(pid)
            .fetch_one(&mut admin)
            .await
            .unwrap();
    assert!(held > 0);
    connection.close().await.unwrap();
    for _ in 0..100 {
        let held: i64 = sqlx::query_scalar(
            "SELECT count(*) FROM pg_locks WHERE locktype='advisory' AND pid=$1",
        )
        .bind(pid)
        .fetch_one(&mut admin)
        .await
        .unwrap();
        if held == 0 {
            break;
        }
        tokio::time::sleep(Duration::from_millis(5)).await;
    }
    assert_eq!(
        sqlx::query_scalar::<_, i64>(
            "SELECT count(*) FROM pg_locks WHERE locktype='advisory' AND pid=$1"
        )
        .bind(pid)
        .fetch_one(&mut admin)
        .await
        .unwrap(),
        0
    );
    let failed = run(
        ConnectionOptions::Postgres(options.clone()),
        request(0, &["CREATE TABLE other(id INTEGER)"]),
        limits(),
        Arc::default(),
    )
    .await
    .unwrap_err();
    assert_eq!(failed.kind, ErrorKind::Checksum);
    assert_eq!(LIVE_CONNECTIONS.load(Ordering::Acquire), 0);
    let role = format!("b08_denied_{}", std::process::id());
    admin
        .execute(AssertSqlSafe(format!("CREATE ROLE {role} LOGIN")))
        .await
        .unwrap();
    let denied = run(
        ConnectionOptions::Postgres(options.clone().username(&role)),
        manifest.clone(),
        limits(),
        Arc::default(),
    )
    .await
    .unwrap_err();
    assert_eq!(denied.provider.as_deref(), Some("42501"));
    let mut partial = manifest.clone();
    partial.allow_non_transactional = true;
    partial.bundle.as_mut().unwrap().migrations.push(
        Migration::new(
            2,
            "concurrent index".into(),
            "CREATE INDEX CONCURRENTLY partial_index ON seed ((1/(id-1)))".into(),
            true,
        )
        .unwrap(),
    );
    let mut observer = sqlx::PgConnection::connect_with(&options).await.unwrap();
    observer
        .execute("INSERT INTO seed VALUES(1),(2)")
        .await
        .unwrap();
    let error = run(
        ConnectionOptions::Postgres(options.clone()),
        partial.clone(),
        limits(),
        Arc::default(),
    )
    .await
    .unwrap_err();
    assert_eq!(error.provider.as_deref(), Some("22012"));
    let phase: String =
        sqlx::query_scalar("SELECT phase FROM _nativeweave_migration_phases WHERE version=2")
            .fetch_one(&mut observer)
            .await
            .unwrap();
    assert_eq!(phase, "applying");
    let index =
        sqlx::query("SELECT indisvalid FROM pg_index WHERE indexrelid='partial_index'::regclass")
            .fetch_one(&mut observer)
            .await
            .unwrap();
    assert!(!index.get::<bool, _>(0));
    let mut validation = partial.clone();
    validation.action = 2;
    validation.allow_non_transactional = false;
    assert_eq!(
        run(
            ConnectionOptions::Postgres(options.clone()),
            validation,
            limits(),
            Arc::default()
        )
        .await
        .unwrap_err()
        .kind,
        ErrorKind::Dirty
    );
    let mut status = partial.clone();
    status.action = 1;
    status.allow_non_transactional = false;
    let Output::Status(status) = run(
        ConnectionOptions::Postgres(options.clone()),
        status,
        limits(),
        Arc::default(),
    )
    .await
    .unwrap() else {
        panic!("status")
    };
    assert!(!status[1].applied);
    observer
        .execute("DROP INDEX partial_index; DELETE FROM seed WHERE id=1")
        .await
        .unwrap();
    let report = run(
        ConnectionOptions::Postgres(options.clone()),
        partial.clone(),
        limits(),
        Arc::default(),
    )
    .await
    .unwrap();
    assert!(matches!(
        report,
        Output::Report {
            applied: 1,
            current: Some(2)
        }
    ));
    let mut cancel_manifest = partial.clone();
    cancel_manifest.bundle.as_mut().unwrap().migrations.push(
        Migration::new(
            3,
            "cancel".into(),
            "CREATE TABLE canceled(id INTEGER); SELECT pg_sleep(0.2)".into(),
            false,
        )
        .unwrap(),
    );
    let token = Arc::new(Cancellation::default());
    let stop = token.clone();
    let retained = options.clone();
    let started = std::time::Instant::now();
    let work = tokio::spawn(async move {
        run(
            ConnectionOptions::Postgres(retained),
            cancel_manifest,
            limits(),
            token,
        )
        .await
    });
    tokio::time::sleep(Duration::from_millis(30)).await;
    stop.cancel();
    assert_eq!(work.await.unwrap().unwrap_err().kind, ErrorKind::Canceled);
    assert!(started.elapsed() >= Duration::from_millis(180));
    assert!(
        !sqlx::query_scalar::<_, bool>("SELECT to_regclass('canceled') IS NOT NULL")
            .fetch_one(&mut observer)
            .await
            .unwrap()
    );
    let mut terminated = partial.clone();
    terminated.bundle.as_mut().unwrap().migrations.push(
        Migration::new(
            3,
            "terminate".into(),
            "SELECT pg_terminate_backend(pg_backend_pid())".into(),
            false,
        )
        .unwrap(),
    );
    let failure = run(
        ConnectionOptions::Postgres(options.clone()),
        terminated,
        limits(),
        Arc::default(),
    )
    .await
    .unwrap_err();
    assert_eq!(failure.kind, ErrorKind::Provider);
    assert_eq!(failure.provider.as_deref(), Some("57P01"));
    assert!(
        failure.cleanup.is_some(),
        "actual terminated backend retains failed rollback/close"
    );
    observer
        .execute("CREATE TABLE empty_table()")
        .await
        .unwrap();
    let mut catalog = partial.clone();
    catalog.action = 3;
    catalog.bundle = None;
    catalog.allow_non_transactional = false;
    let Output::Tables(tables) = run(
        ConnectionOptions::Postgres(options.clone()),
        catalog,
        limits(),
        Arc::default(),
    )
    .await
    .unwrap() else {
        panic!("tables")
    };
    assert!(
        tables
            .iter()
            .any(|table| table.name == "empty_table" && table.columns.is_empty())
    );
    observer.close().await.unwrap();
    assert_eq!(LIVE_CONNECTIONS.load(Ordering::Acquire), 0);
    admin
        .execute(AssertSqlSafe(format!("DROP DATABASE {name}")))
        .await
        .unwrap();
    admin
        .execute(AssertSqlSafe(format!("DROP ROLE {role}")))
        .await
        .unwrap();
    admin.close().await.unwrap();
}

#[cfg(feature = "sqlite")]
#[tokio::test]
async fn sqlite_catalog_distinguishes_rowid_and_composite_primary_keys() {
    use sqlx::{Connection, Executor};
    let _counters = COUNTER_CASE.lock().await;
    let path = sqlite_path("catalog");
    let _ = std::fs::remove_file(&path);
    let options = sqlite_options(&path);
    let mut connection = sqlx::SqliteConnection::connect_with(&options)
        .await
        .unwrap();
    connection.execute("CREATE TABLE single_key(id INTEGER PRIMARY KEY); CREATE TABLE composite_key(a INTEGER,b INTEGER,PRIMARY KEY(a,b)); CREATE TABLE descending_key(id INTEGER PRIMARY KEY DESC)").await.unwrap();
    connection.close().await.unwrap();
    let request = Request {
        action: 3,
        bundle: None,
        target: None,
        lock_timeout: Duration::from_secs(1),
        allow_non_transactional: false,
        schema: None,
    };
    let Output::Tables(tables) = run(
        ConnectionOptions::Sqlite(options),
        request,
        limits(),
        Arc::default(),
    )
    .await
    .unwrap() else {
        panic!("tables")
    };
    assert!(
        !tables
            .iter()
            .find(|t| t.name == "single_key")
            .unwrap()
            .columns[0]
            .nullable
    );
    assert!(
        tables
            .iter()
            .find(|t| t.name == "composite_key")
            .unwrap()
            .columns
            .iter()
            .all(|c| c.nullable)
    );
    assert!(
        tables
            .iter()
            .find(|t| t.name == "descending_key")
            .unwrap()
            .columns[0]
            .nullable
    );
    std::fs::remove_file(path).unwrap();
}

#[test]
fn target_admission_excludes_future_nontransactional_entries_and_caps_history() {
    let mut value = request(
        0,
        &[
            "CREATE TABLE one(id INTEGER)",
            "CREATE INDEX CONCURRENTLY idx ON one(id)",
        ],
    );
    value.bundle.as_mut().unwrap().migrations[1].non_transactional = true;
    value.target = Some(1);
    assert!(value.validate(0, &limits()).is_ok());
    value.target = Some(2);
    assert_eq!(
        value.validate(0, &limits()).unwrap_err().kind,
        ErrorKind::Capability
    );
    value.allow_non_transactional = true;
    let mut bounded = limits();
    bounded.rows = 1;
    assert_eq!(
        value.validate(0, &bounded).unwrap_err().kind,
        ErrorKind::Capacity
    );
}

#[test]
fn original_failure_retains_rollback_and_close_failures() {
    let primary = Error::new(ErrorKind::Checksum, "original");
    let error = primary
        .with_cleanup(Error::new(ErrorKind::Provider, "rollback"))
        .with_cleanup(Error::new(ErrorKind::Cleanup, "close"));
    assert_eq!(error.kind, ErrorKind::Checksum);
    let rollback = error.cleanup.unwrap();
    assert_eq!(rollback.message, "rollback");
    assert_eq!(rollback.cleanup.unwrap().message, "close");
}
