use futures_util::TryStreamExt;
use nativeweave_data_codecs::{Limits, Parameters, read_postgres};
use nativeweave_data_contracts::{
    Codec, Profile, TransactionOutcome,
    wire::{Batch, Descriptor, Expectation},
};
use nativeweave_data_postgres::{
    Command, CommandKind, ConnectConfig, ErrorKind, Session, SessionConfig, TlsMode,
};
use nativeweave_data_transactions::{
    AdmissionToken, Confirmation, Options, ProtocolState, Transaction, TransactionDriver,
};
use serde_json::{Value, json};
use sqlx::{PgPool, Row, postgres::PgPoolOptions};
use std::{
    path::PathBuf,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    time::Duration,
};
use tokio::{net::TcpListener, time::timeout};
type Result<T = ()> = std::result::Result<T, Box<dyn std::error::Error + Send + Sync>>;
fn env(name: &str) -> Result<String> {
    std::env::var(name).map_err(|_| format!("required test input {name} missing").into())
}
fn command(sql: &str) -> Command {
    Command::generated(sql.into(), None)
}
fn config() -> Result<ConnectConfig> {
    let mut config = if let Ok(path) = std::env::var("NW_PG_OPTION_RECORD") {
        ConnectConfig::from_record(env("NW_PG_URL")?, &std::fs::read(path)?)?
    } else {
        ConnectConfig::new(env("NW_PG_URL")?)
    };
    config.root_certificate = Some(PathBuf::from(env("NW_PG_ROOT")?));
    config.connect_timeout = Duration::from_secs(2);
    Ok(config)
}
async fn connect_pool(config: &ConnectConfig, max: u32) -> Result<PgPool> {
    Ok(PgPoolOptions::new()
        .max_connections(max)
        .min_connections(0)
        .acquire_timeout(config.connect_timeout)
        .connect_with(config.options()?)
        .await?)
}
async fn open_session(pool: &PgPool) -> Result<Session> {
    Ok(Session::from_lease(pool.acquire().await?, SessionConfig::default()).await?)
}
async fn rows(session: &Session, sql: &str) -> Result<Vec<sqlx::postgres::PgRow>> {
    let mut stream = session.stream(command(sql))?;
    let control = stream.control();
    let mut rows = Vec::new();
    while let Some(row) = stream.try_next().await? {
        rows.push(row);
    }
    let receipt = control.join().await;
    assert!(receipt.retired && receipt.protocol_clean);
    assert!(receipt.retained_rows_peak <= 2);
    Ok(rows)
}
async fn scalar_i64(session: &Session, sql: &str) -> Result<i64> {
    Ok(rows(session, sql).await?[0].try_get(0)?)
}

async fn timeout_rollback() -> Result<Value> {
    let pool = connect_pool(&config()?, 1).await?;
    let session = Session::from_lease(
        pool.acquire().await?,
        SessionConfig {
            statement_timeout: Duration::from_millis(60),
            ..Default::default()
        },
    )
    .await?;
    let pid = session.backend_pid();
    let mut driver = session.transaction_driver();
    driver
        .begin(&Options::default())
        .await
        .map_err(|_| "begin")?;
    let timeout_error = session
        .execute(command("SELECT pg_sleep(2)"))
        .await
        .unwrap_err();
    assert_eq!(timeout_error.sqlstate.as_deref(), Some("57014"));
    let failed = session.execute(command("SELECT 1")).await.unwrap_err();
    assert_eq!(failed.sqlstate.as_deref(), Some("25P02"));
    assert_eq!(
        driver.rollback().await.map_err(|_| "rollback")?,
        ProtocolState::Clean
    );
    assert_eq!(
        scalar_i64(&session, "SELECT pg_backend_pid()::bigint").await?,
        pid as i64
    );
    assert_eq!(scalar_i64(&session, "SELECT 7::bigint").await?, 7);
    // An unreported command failure must still not turn COMMIT's server-side
    // ROLLBACK tag into a false committed outcome.
    driver
        .begin(&Options::default())
        .await
        .map_err(|_| "begin failed tx")?;
    let _ = session.execute(command("SELECT 1/0")).await.unwrap_err();
    assert!(matches!(
        driver.commit().await.map_err(|_| "failed commit")?,
        Confirmation::RolledBack(_)
    ));
    drop(driver);
    let retired = session.close().await;
    assert!(retired.returned_to_pool);
    let reused = session_from(&pool).await?;
    assert_eq!(reused.backend_pid(), pid);
    reused.close().await;
    pool.close().await;
    Ok(
        json!({"case":"timeout-rollback","timeout_sqlstate":"57014","failed_sqlstate":"25P02","original_pid":pid,"reused_pid":pid,"failed_commit":"RolledBack"}),
    )
}
async fn session_from(pool: &PgPool) -> Result<Session> {
    open_session(pool).await
}
async fn stream_and_admission() -> Result<Value> {
    let pool = connect_pool(&config()?, 1).await?;
    let session = open_session(&pool).await?;
    let pid = session.backend_pid();
    let mut stream = session.stream(command("SELECT n FROM generate_series(1,20000) n"))?;
    let control = stream.control();
    assert!(stream.try_next().await?.is_some());
    assert_eq!(
        session.execute(command("SELECT 1")).await.unwrap_err().kind,
        ErrorKind::Busy
    );
    let waiting = pool.acquire();
    assert!(timeout(Duration::from_millis(20), waiting).await.is_err());
    drop(stream);
    let receipt = timeout(Duration::from_secs(10), control.join()).await?;
    assert!(receipt.retired && receipt.protocol_clean && receipt.stopped);
    assert!(receipt.retained_rows_peak <= 2);
    assert_eq!(
        scalar_i64(&session, "SELECT pg_backend_pid()::bigint").await?,
        pid as i64
    );
    let metadata = session
        .describe(command("SELECT 1::bigint AS value WHERE false"))
        .await?;
    assert_eq!(metadata.len(), 1);
    assert_eq!(metadata[0].name, "value");
    assert_eq!(metadata[0].type_name, "INT8");
    session.close().await;
    let reused = session_from(&pool).await?;
    assert_eq!(reused.backend_pid(), pid);
    reused.close().await;
    pool.close().await;
    Ok(
        json!({"case":"early-stream-drop","same_backend_pid":pid,"provider_rows_peak":receipt.retained_rows_peak,"acquire_cancel_permit_restored":true,"empty_result_metadata":true}),
    )
}
fn parameter(codec: Codec, value: Option<&[u8]>) -> Result<Parameters> {
    let expected = Expectation {
        kind: 3,
        profile: Profile::PostgreSql,
        hash: [0; 32],
        entity_id: 0,
        projection_id: 0,
        exact_rows: Some(1),
        descriptors: vec![Descriptor {
            id: 1,
            bound_codec: if codec.configuration.is_some() {
                1024
            } else {
                codec.id
            },
            codec,
        }],
    };
    let wire = Batch::encode(&expected, 1, &[value]).map_err(|e| e.0)?;
    Ok(Parameters::decode(&wire, expected, Limits::default())?)
}
async fn codecs() -> Result<Value> {
    let pool = connect_pool(&config()?, 1).await?;
    let session = open_session(&pool).await?;
    let cases: Vec<(Codec, Vec<u8>, &str)> = vec![
        (
            Codec {
                id: 7,
                configuration: None,
                nullable: false,
            },
            (0..16).collect(),
            "SELECT $1::uuid AS value",
        ),
        (
            Codec {
                id: 13,
                configuration: Some("{\"precision\":12,\"scale\":3}".into()),
                nullable: false,
            },
            {
                let mut bytes = vec![0; 16];
                bytes[..4].copy_from_slice(&123456125u32.to_le_bytes());
                bytes[12] = 3;
                bytes
            },
            "SELECT $1::numeric AS value",
        ),
        (
            Codec {
                id: 10,
                configuration: None,
                nullable: false,
            },
            1_700_000_000_123_456i64.to_le_bytes().to_vec(),
            "SELECT $1::timestamptz AS value",
        ),
        (
            Codec {
                id: 5,
                configuration: None,
                nullable: false,
            },
            "PostgreSQL λ".as_bytes().to_vec(),
            "SELECT $1::text AS value",
        ),
    ];
    for (codec, expected, sql) in &cases {
        let mut stream = session.stream(Command::generated(
            (*sql).into(),
            Some(parameter(codec.clone(), Some(expected))?),
        ))?;
        let control = stream.control();
        let row = stream.try_next().await?.ok_or("missing roundtrip")?;
        assert_eq!(read_postgres(&row, 0, codec, 4096)?, Some(expected.clone()));
        drop(stream);
        assert!(control.join().await.protocol_clean);
    }
    let null = Codec {
        id: 3,
        configuration: None,
        nullable: true,
    };
    let mut stream = session.stream(Command::generated(
        "SELECT $1::bigint".into(),
        Some(parameter(null.clone(), None)?),
    ))?;
    let control = stream.control();
    assert_eq!(
        read_postgres(&stream.try_next().await?.unwrap(), 0, &null, 4096)?,
        None
    );
    drop(stream);
    control.join().await;
    let row = rows(&session, "SELECT 1e100::numeric").await?.remove(0);
    assert!(read_postgres(&row, 0, &cases[1].0, 4096).is_err());
    session.close().await;
    pool.close().await;
    Ok(json!({"case":"codecs","actual_b07_roundtrips":5,"wide_numeric_rejected":true}))
}
async fn savepoints_and_b05() -> Result<Value> {
    let pool = connect_pool(&config()?, 1).await?;
    let session = open_session(&pool).await?;
    session
        .execute(command(
            "CREATE TEMP TABLE b02_savepoint(value int) ON COMMIT PRESERVE ROWS",
        ))
        .await?;
    let admission = AdmissionToken::default();
    let tx =
        Transaction::begin(session.transaction_driver(), Options::default(), &admission).await?;
    let lease = tx.admit_command(false, &admission)?;
    session
        .execute(command("INSERT INTO b02_savepoint VALUES(1)"))
        .await?;
    lease.complete(false, ProtocolState::Clean);
    let savepoint = tx.create_savepoint(&admission).await?;
    let lease = tx.admit_command(false, &admission)?;
    session
        .execute(command("INSERT INTO b02_savepoint VALUES(2)"))
        .await?;
    lease.complete(false, ProtocolState::Clean);
    tx.rollback_savepoint(savepoint, &admission).await?;
    tx.release_savepoint(savepoint, &admission).await?;
    tx.commit(&admission).await?;
    let end = tx.join().await;
    assert_eq!(end.outcome, TransactionOutcome::Committed);
    assert!(end.local_resources_released);
    assert_eq!(
        scalar_i64(&session, "SELECT count(*) FROM b02_savepoint").await?,
        1
    );
    let tx =
        Transaction::begin(session.transaction_driver(), Options::default(), &admission).await?;
    let canceled = AdmissionToken::default();
    canceled.cancel();
    assert!(matches!(
        tx.commit(&canceled).await,
        Err(nativeweave_data_transactions::Error::CanceledBeforeDispatch)
    ));
    assert!(!tx.snapshot().commit_attempted);
    tx.rollback(&admission).await?;
    assert_eq!(tx.join().await.outcome, TransactionOutcome::RolledBack);
    // Unsupported isolation is rejected without a server BEGIN.
    let unsupported = Options {
        isolation: Some(nativeweave_data_transactions::Isolation::Snapshot),
        ..Default::default()
    };
    assert!(
        Transaction::begin(session.transaction_driver(), unsupported, &admission)
            .await
            .is_err()
    );
    session.close().await;
    pool.close().await;
    Ok(
        json!({"case":"b05-savepoints","rollback_to_keeps_tracked_depth":true,"durable_rows":1,"pre_dispatch_cancel_no_commit":true,"b05_native_retired":true}),
    )
}
async fn lost_commit() -> Result<Value> {
    let mut plain = ConnectConfig::new(env("NW_PG_PLAIN_URL")?);
    plain.tls = TlsMode::DisableForLocalDevelopment;
    let observer_pool = connect_pool(&plain, 1).await?;
    let observer = open_session(&observer_pool).await?;
    observer
        .execute(command("DROP TABLE IF EXISTS b02_commit_loss"))
        .await?;
    observer
        .execute(command(
            "CREATE TABLE b02_commit_loss(id int PRIMARY KEY, result text NOT NULL)",
        ))
        .await?;
    let options = plain.options()?;
    let listener = TcpListener::bind("127.0.0.1:0").await?;
    let port = listener.local_addr()?.port();
    let observed = Arc::new(AtomicBool::new(false));
    let proxy = tokio::spawn(crate::proxy::lose_commit_ack(
        listener,
        (options.get_host().to_owned(), options.get_port()),
        Arc::clone(&observed),
    ));
    let proxied = options.host("127.0.0.1").port(port);
    let client_pool = PgPoolOptions::new()
        .max_connections(1)
        .connect_with(proxied)
        .await?;
    let client = open_session(&client_pool).await?;
    let admission = AdmissionToken::default();
    let tx =
        Transaction::begin(client.transaction_driver(), Options::default(), &admission).await?;
    let lease = tx.admit_command(false, &admission)?;
    client
        .execute(command("INSERT INTO b02_commit_loss VALUES(1,'durable')"))
        .await?;
    lease.complete(false, ProtocolState::Clean);
    assert!(matches!(
        timeout(Duration::from_secs(10), tx.commit(&admission)).await?,
        Err(nativeweave_data_transactions::Error::CommitUnknown { .. })
    ));
    timeout(Duration::from_secs(10), proxy).await???;
    assert!(observed.load(Ordering::SeqCst));
    let snapshot = timeout(Duration::from_secs(10), tx.join()).await?;
    assert_eq!(snapshot.outcome, TransactionOutcome::Unknown);
    assert!(snapshot.local_resources_released);
    assert_eq!(
        scalar_i64(
            &observer,
            "SELECT count(*) FROM b02_commit_loss WHERE result='durable'"
        )
        .await?,
        1
    );
    assert_eq!(tx.snapshot().outcome, TransactionOutcome::Unknown);
    client.close().await;
    client_pool.close().await;
    observer.close().await;
    observer_pool.close().await;
    Ok(
        json!({"case":"commit-response-loss","proxy_saw_commit_and_idle":true,"client_outcome":"Unknown","durable_rows":1,"cleanup_preserved_unknown":true,"native_retired":snapshot.local_resources_released}),
    )
}
async fn conflict_snapshot() -> Result<Value> {
    let pool = connect_pool(&config()?, 3).await?;
    let observer = open_session(&pool).await?;
    observer
        .execute(command("DROP TABLE IF EXISTS b02_conflict_snapshot"))
        .await?;
    observer
        .execute(command(
            "CREATE TABLE b02_conflict_snapshot(key text PRIMARY KEY, operation_id bigint)",
        ))
        .await?;
    let holder = open_session(&pool).await?;
    let contender = open_session(&pool).await?;
    let pid = contender.backend_pid();
    let mut driver = holder.transaction_driver();
    driver
        .begin(&Options::default())
        .await
        .map_err(|_| "begin holder")?;
    holder
        .execute(command("INSERT INTO b02_conflict_snapshot VALUES('key',1)"))
        .await?;
    let c = contender.clone();
    let pending = tokio::spawn(async move {
        rows(&c,"WITH inserted AS (INSERT INTO b02_conflict_snapshot VALUES('key',2) ON CONFLICT(key) DO NOTHING RETURNING operation_id) SELECT operation_id FROM inserted UNION ALL SELECT operation_id FROM b02_conflict_snapshot WHERE key='key' LIMIT 1").await
    });
    timeout(Duration::from_secs(10), async {
        loop {
            let waiting = scalar_i64(
                &observer,
                &format!("SELECT count(*) FROM pg_locks WHERE pid={pid} AND NOT granted"),
            )
            .await?;
            if waiting > 0 {
                break;
            }
            tokio::task::yield_now().await;
        }
        Ok::<_, Box<dyn std::error::Error + Send + Sync>>(())
    })
    .await??;
    assert!(matches!(
        driver.commit().await.map_err(|_| "holder commit")?,
        Confirmation::Committed
    ));
    assert!(pending.await??.is_empty());
    assert_eq!(
        scalar_i64(
            &contender,
            "SELECT operation_id FROM b02_conflict_snapshot WHERE key='key'"
        )
        .await?,
        1
    );
    drop(driver);
    holder.close().await;
    contender.close().await;
    observer.close().await;
    pool.close().await;
    Ok(
        json!({"case":"conflict-cte-snapshot","synchronized_on_server_lock":true,"same_statement_rows":0,"separate_statement_operation":1}),
    )
}
async fn tls() -> Result<Value> {
    let good = config()?;
    let pool = connect_pool(&good, 1).await?;
    let session = open_session(&pool).await?;
    let identity = rows(
        &session,
        "SELECT version(),ssl,version,cipher FROM pg_stat_ssl WHERE pid=pg_backend_pid()",
    )
    .await?
    .remove(0);
    let server: String = identity.try_get(0)?;
    let actual_name: String =
        rows(&session, "SELECT current_setting('application_name')").await?[0].try_get(0)?;
    assert_eq!(actual_name, good.application_name);
    assert!(server.contains("PostgreSQL 18"));
    assert!(identity.try_get::<bool, _>(1)?);
    let tls_version: String = identity.try_get(2)?;
    let cipher: String = identity.try_get(3)?;
    session.close().await;
    pool.close().await;
    let mut wrong = good.clone();
    wrong.connection_string = wrong.connection_string.replace("localhost", "127.0.0.1");
    assert!(pool_fail(&wrong).await);
    let mut untrusted = good.clone();
    untrusted.root_certificate = Some(PathBuf::from(env("NW_PG_UNTRUSTED_ROOT")?));
    assert!(pool_fail(&untrusted).await);
    let mut expired = good.clone();
    expired.connection_string = env("NW_PG_EXPIRED_URL")?;
    assert!(pool_fail(&expired).await);
    let mut plain = good.clone();
    plain.connection_string = env("NW_PG_PLAIN_URL")?;
    assert!(pool_fail(&plain).await);
    plain.tls = TlsMode::DisableForLocalDevelopment;
    let pool = connect_pool(&plain, 1).await?;
    let session = open_session(&pool).await?;
    assert!(
        !rows(
            &session,
            "SELECT ssl FROM pg_stat_ssl WHERE pid=pg_backend_pid()"
        )
        .await?[0]
            .try_get::<bool, _>(0)?
    );
    session.close().await;
    pool.close().await;
    let mut override_tls = good;
    override_tls.connection_string.push_str("?sslmode=disable");
    assert!(override_tls.options().is_err());
    Ok(
        json!({"case":"tls","server_identity":server,"application_name":actual_name,"tls_version":tls_version,"cipher":cipher,"wrong_hostname_rejected":true,"untrusted_root_rejected":true,"expired_certificate_rejected":true,"plaintext_requires_explicit_opt_in":true,"connection_string_tls_override_rejected":true}),
    )
}
async fn pool_fail(config: &ConnectConfig) -> bool {
    match connect_pool(config, 1).await {
        Ok(pool) => {
            pool.close().await;
            false
        }
        Err(_) => true,
    }
}
async fn quarantine() -> Result<Value> {
    let pool = connect_pool(&config()?, 2).await?;
    let observer = open_session(&pool).await?;
    let session = Session::from_lease(
        pool.acquire().await?,
        SessionConfig {
            statement_timeout: Duration::from_secs(2),
            cleanup_timeout: Duration::from_millis(20),
            ..Default::default()
        },
    )
    .await?;
    let pid = session.backend_pid();
    let stream = session.stream(command("SELECT pg_sleep(1), 1"))?;
    let control = stream.control();
    // Wait for the actual backend to enter pg_sleep before requesting stop.
    timeout(Duration::from_secs(5), async {
        loop {
            let n = scalar_i64(
                &observer,
                &format!(
                    "SELECT count(*) FROM pg_stat_activity WHERE pid={pid} AND wait_event='PgSleep'"
                ),
            )
            .await?;
            if n == 1 {
                break;
            }
            tokio::task::yield_now().await;
        }
        Ok::<_, Box<dyn std::error::Error + Send + Sync>>(())
    })
    .await??;
    control.request_stop();
    drop(stream);
    let receipt = timeout(Duration::from_secs(5), control.join()).await?;
    assert!(receipt.retired && receipt.quarantined && !receipt.protocol_clean);
    let closed = session.control().join().await;
    assert!(closed.physical_closed && closed.retired);
    assert!(session.execute(command("SELECT 1")).await.is_err());
    let replacement = session_from(&pool).await?;
    assert_ne!(replacement.backend_pid(), pid);
    replacement.close().await;
    // Broken transport cannot return the old backend session to another tenant.
    let victim = session_from(&pool).await?;
    let victim_pid = victim.backend_pid();
    observer
        .execute(command(&format!(
            "SELECT pg_terminate_backend({victim_pid})"
        )))
        .await?;
    assert!(victim.execute(command("SELECT 1")).await.is_err());
    let retired = victim.close().await;
    assert!(retired.physical_closed);
    observer.close().await;
    pool.close().await;
    Ok(
        json!({"case":"quarantine","drain_timeout_pid":pid,"replacement_different_pid":true,"broken_pid":victim_pid,"native_owners_joined":true,"pool_permits_restored":true}),
    )
}
async fn close_with_admitted_operation() -> Result<Value> {
    let pool = connect_pool(&config()?, 1).await?;
    let session = open_session(&pool).await?;
    let pid = session.backend_pid();
    let operation = session.execute(command("SELECT pg_sleep(0.15)"));
    tokio::pin!(operation);
    std::future::poll_fn(|cx| {
        assert!(operation.as_mut().poll(cx).is_pending());
        std::task::Poll::Ready(())
    })
    .await;
    let control = session.control();
    control.request_close();
    assert!(
        timeout(Duration::from_millis(10), control.join())
            .await
            .is_err()
    );
    assert!(!control.snapshot().retired);
    assert_eq!(pool.size(), 1);
    operation.await?;
    let retired = timeout(Duration::from_secs(5), control.join()).await?;
    assert!(retired.retired && retired.returned_to_pool);
    let replacement = session_from(&pool).await?;
    assert_eq!(replacement.backend_pid(), pid);
    replacement.close().await;
    pool.close().await;
    Ok(
        json!({"case":"admitted-operation-close","admitted_before_close":true,"pending_close_keeps_pool_permit":true,"retired_after_operation":true,"same_backend_pid":pid}),
    )
}

async fn transaction_parent_retirement() -> Result<Value> {
    let pool = connect_pool(&config()?, 1).await?;
    // The retirement hook must release its own parent lease before returning.
    // Keeping the driver value alive must not prevent a later last-parent close.
    let session = open_session(&pool).await?;
    let control = session.control();
    let mut driver = session.transaction_driver();
    driver
        .retire(nativeweave_data_contracts::ConnectionDisposition::Usable)
        .await;
    assert!(!control.snapshot().retired);
    drop(session);
    let receipt = timeout(Duration::from_secs(2), control.join()).await?;
    assert!(receipt.retired && receipt.returned_to_pool);
    assert!(driver.begin(&Options::default()).await.is_err());
    drop(driver);

    // When the independent parent has already gone, retirement itself joins
    // the last lease. The observation is checked before dropping the driver.
    let session = open_session(&pool).await?;
    let control = session.control();
    let mut driver = session.transaction_driver();
    drop(session);
    driver
        .retire(nativeweave_data_contracts::ConnectionDisposition::Usable)
        .await;
    assert!(control.snapshot().retired);
    drop(driver);
    let replacement = open_session(&pool).await?;
    assert_eq!(scalar_i64(&replacement, "SELECT 9::bigint").await?, 9);
    replacement.close().await;
    pool.close().await;
    Ok(
        json!({"case":"transaction-parent-retirement","driver_parent_released_before_return":true,
        "already_last_parent_joined":true,"retired_driver_rejects_operations":true,"pool_permits_restored":true}),
    )
}

async fn migration_lock() -> Result<Value> {
    let pool = connect_pool(&config()?, 2).await?;
    let observer = open_session(&pool).await?;
    let dir = PathBuf::from(env("NW_PG_REPORT")?).with_extension("migrations");
    std::fs::create_dir_all(&dir)?;
    let file = dir.join("1_initial.sql");
    std::fs::write(
        &file,
        "CREATE TABLE b02_migration_probe(id int PRIMARY KEY);",
    )?;
    observer
        .execute(command("DROP TABLE IF EXISTS b02_migration_probe"))
        .await?;
    observer
        .execute(command("DROP TABLE IF EXISTS _b02_migration_lock"))
        .await?;
    let mut admin = pool.acquire().await?;
    let pid: i32 = sqlx::query_scalar("SELECT pg_backend_pid()")
        .fetch_one(&mut *admin)
        .await?;
    let mut first = sqlx::migrate::Migrator::new(dir.as_path()).await?;
    first.table_name = std::borrow::Cow::Borrowed("_b02_migration_lock");
    first.run_direct(None, &mut *admin, false).await?;
    assert_eq!(
        scalar_i64(
            &observer,
            &format!("SELECT count(*) FROM pg_locks WHERE pid={pid} AND locktype='advisory'")
        )
        .await?,
        0
    );
    std::fs::write(
        &file,
        "CREATE TABLE b02_changed_migration(id int PRIMARY KEY);",
    )?;
    let mut changed = sqlx::migrate::Migrator::new(dir.as_path()).await?;
    changed.table_name = std::borrow::Cow::Borrowed("_b02_migration_lock");
    assert!(matches!(
        changed.run_direct(None, &mut *admin, false).await,
        Err(sqlx::migrate::MigrateError::VersionMismatch(1))
    ));
    assert_eq!(
        scalar_i64(
            &observer,
            &format!("SELECT count(*) FROM pg_locks WHERE pid={pid} AND locktype='advisory'")
        )
        .await?,
        1
    );
    // Transfer the dirty dedicated lease to the product's retained retirement
    // owner, mark the audited session-mutating category and join physical close.
    let admin = Session::from_lease(admin, SessionConfig::default()).await?;
    admin
        .execute(Command::expert(
            "SELECT 1".into(),
            None,
            CommandKind::SessionMutating,
        ))
        .await?;
    let retired = admin.close().await;
    assert!(retired.physical_closed && retired.retired);
    timeout(Duration::from_secs(5), async {
        loop {
            if scalar_i64(
                &observer,
                &format!("SELECT count(*) FROM pg_locks WHERE pid={pid} AND locktype='advisory'"),
            )
            .await?
                == 0
            {
                break;
            }
            tokio::task::yield_now().await;
        }
        Ok::<_, Box<dyn std::error::Error + Send + Sync>>(())
    })
    .await??;
    observer.close().await;
    pool.close().await;
    Ok(
        json!({"case":"migration-lock-cleanup","negative_control_locks":1,"after_product_dedicated_retirement_locks":0,"dedicated_pid":pid,"native_retired":true}),
    )
}
pub async fn run() -> Result {
    let mut results = Vec::new();
    macro_rules! case {
        ($function:ident) => {{
            let result = timeout(Duration::from_secs(40), $function()).await??;
            println!("PASS {}", result["case"]);
            results.push(result);
        }};
    }
    case!(transaction_parent_retirement);
    case!(tls);
    case!(timeout_rollback);
    case!(stream_and_admission);
    case!(codecs);
    case!(savepoints_and_b05);
    case!(lost_commit);
    case!(conflict_snapshot);
    case!(quarantine);
    case!(migration_lock);
    case!(close_with_admitted_operation);
    std::fs::write(
        env("NW_PG_REPORT")?,
        serde_json::to_vec_pretty(
            &json!({"schema_version":1,"platform":"linux-x64","cases":results,"component_only":true,"full_b04_host":false}),
        )?,
    )?;
    Ok(())
}
