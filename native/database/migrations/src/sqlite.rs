use crate::*;
use futures_util::TryStreamExt;
use sqlx::{
    Connection, Row,
    migrate::Migrate,
    sqlite::{SqliteConnectOptions, SqliteConnection},
};
use std::{collections::BTreeMap, time::Instant};

pub async fn run(
    options: SqliteConnectOptions,
    request: Request,
    limits: Limits,
    cancel: Arc<Cancellation>,
) -> Result<Output> {
    let options = options.busy_timeout(request.lock_timeout);
    let mut connection = tokio::time::timeout(
        limits.connect_timeout,
        SqliteConnection::connect_with(&options),
    )
    .await
    .map_err(|_| Error::new(ErrorKind::Timeout, "administrative connection timed out"))??;
    LIVE_CONNECTIONS.fetch_add(1, Ordering::AcqRel);
    let result = execute(&mut connection, &request, &limits, cancel).await;
    let close = connection.close().await;
    finish(result, close)
}
async fn execute(
    connection: &mut SqliteConnection,
    request: &Request,
    limits: &Limits,
    cancel: Arc<Cancellation>,
) -> Result<Output> {
    let deadline = Instant::now() + limits.command_timeout + request.lock_timeout;
    let stop = cancel.clone();
    connection
        .lock_handle()
        .await?
        .set_progress_handler(1000, move || !stop.requested() && Instant::now() < deadline);
    if request.action == 3 {
        return tables(connection, limits, &cancel).await;
    }
    cancel.check()?;
    // SQLx's SQLite Migrate::lock is a no-op. This depth-tracked transaction
    // acquires the actual writer lock before history reads and nested migrations.
    let mut tx = connection.begin_with("BEGIN IMMEDIATE").await?;
    let result = execute_locked(&mut tx, request, limits, &cancel).await;
    // Disable cancellation only after the real migration future has completed;
    // explicit rollback/commit must itself run to completion on the same worker.
    let reset = tx
        .lock_handle()
        .await
        .map(|mut handle| handle.remove_progress_handler());
    let result = match (result, reset) {
        (result, Ok(())) => result,
        (Err(original), Err(cleanup)) => Err(original.with_cleanup(cleanup.into())),
        (Ok(_), Err(cleanup)) => Err(cleanup.into()),
    };
    match result {
        Ok(output) => {
            tx.commit().await?;
            Ok(output)
        }
        Err(mut error) => {
            if error.kind == ErrorKind::Provider && error.provider.as_deref() == Some("9") {
                if cancel.requested() {
                    error.kind = ErrorKind::Canceled;
                } else if Instant::now() >= deadline {
                    error.kind = ErrorKind::Timeout;
                }
            }
            match tx.rollback().await {
                Ok(()) => Err(error),
                Err(cleanup) => Err(error.with_cleanup(cleanup.into())),
            }
        }
    }
}
async fn execute_locked(
    connection: &mut SqliteConnection,
    request: &Request,
    limits: &Limits,
    cancel: &Cancellation,
) -> Result<Output> {
    let bundle = request
        .bundle
        .as_ref()
        .ok_or_else(|| Error::invalid("migration bundle required"))?;
    if request.action == 0 {
        connection.ensure_migrations_table(LEDGER).await?;
        sqlx::query("CREATE TABLE IF NOT EXISTS _nativeweave_migration_phases (version BIGINT PRIMARY KEY, provider INTEGER NOT NULL, sql_hash BLOB NOT NULL, bundle_hash BLOB NOT NULL, description TEXT NOT NULL, non_transactional BOOLEAN NOT NULL, phase TEXT NOT NULL)").execute(&mut *connection).await?;
    }
    let applied = ledger(connection, limits).await?;
    validate_ledger(bundle, &applied)?;
    phases(connection, bundle, &applied, limits).await?;
    if request.action == 1 {
        return Ok(Output::Status(statuses(bundle, &applied, limits)?));
    }
    if request.action == 2 {
        return Ok(Output::Validated);
    }
    if request.target.is_some_and(|target| {
        applied
            .last_key_value()
            .is_some_and(|(current, _)| *current > target)
    }) {
        return Err(Error::invalid(
            "migrations cannot downgrade the current version",
        ));
    }
    let selected = bundle
        .migrations
        .iter()
        .filter(|x| request.target.is_none_or(|target| x.version <= target))
        .collect::<Vec<_>>();
    let mut count = 0;
    for entry in &selected {
        if !applied.contains_key(&entry.version) {
            phase(connection, entry, bundle, "applying").await?;
            count += 1;
        }
    }
    cancel.check()?;
    // Migrator obtains nested SQLx savepoints, never raw nested BEGIN.
    bundle
        .migrator(request.target)
        .run_direct(None, &mut *connection, false)
        .await?;
    cancel.check()?;
    for entry in &selected {
        if !applied.contains_key(&entry.version) {
            phase(connection, entry, bundle, "applied").await?;
        }
    }
    Ok(Output::Report {
        applied: count,
        current: selected
            .last()
            .map(|entry| entry.version)
            .or_else(|| applied.last_key_value().map(|(v, _)| *v)),
    })
}
async fn exists(connection: &mut SqliteConnection, name: &str) -> Result<bool> {
    Ok(sqlx::query_scalar::<_, i64>(
        "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name=?1",
    )
    .bind(name)
    .fetch_one(connection)
    .await?
        != 0)
}
async fn ledger(
    connection: &mut SqliteConnection,
    limits: &Limits,
) -> Result<BTreeMap<i64, Vec<u8>>> {
    let mut result = BTreeMap::new();
    if !exists(connection, LEDGER).await? {
        return Ok(result);
    }
    let mut budget = Budget::new(limits);
    let mut stream =
        sqlx::query("SELECT version, checksum, success FROM _sqlx_migrations ORDER BY version")
            .fetch(connection);
    while let Some(row) = stream.try_next().await? {
        budget.row(96)?;
        if !row.try_get::<bool, _>("success")? {
            return Err(Error::new(
                ErrorKind::Dirty,
                "SQLx ledger contains an unsuccessful migration",
            ));
        }
        let version: i64 = row.try_get("version")?;
        let checksum: Vec<u8> = row.try_get("checksum")?;
        if version <= 0 || checksum.len() != 48 || result.insert(version, checksum).is_some() {
            return Err(Error::new(
                ErrorKind::Checksum,
                "invalid SQLx migration ledger",
            ));
        }
    }
    Ok(result)
}
async fn phases(
    connection: &mut SqliteConnection,
    bundle: &Bundle,
    applied: &BTreeMap<i64, Vec<u8>>,
    limits: &Limits,
) -> Result<()> {
    if !exists(connection, PHASES).await? {
        return Ok(());
    }
    let mut stream = sqlx::query("SELECT version, provider, sql_hash, bundle_hash, description, non_transactional, phase FROM _nativeweave_migration_phases ORDER BY version").fetch(connection);
    let mut budget = Budget::new(limits);
    while let Some(row) = stream.try_next().await? {
        budget.row(128)?;
        let version: i64 = row.try_get("version")?;
        let entry = bundle
            .migrations
            .iter()
            .find(|x| x.version == version)
            .ok_or_else(|| {
                Error::new(
                    ErrorKind::Checksum,
                    "migration phase is absent from the supplied bundle",
                )
            })?;
        let hash: Vec<u8> = row.try_get("sql_hash")?;
        let bundle_hash: Vec<u8> = row.try_get("bundle_hash")?;
        let description: &str = row.try_get("description")?;
        budget.text(description)?;
        if row.try_get::<i64, _>("provider")? != 1
            || hash.as_slice() != entry.sql_hash
            || bundle_hash.len() != 32
            || description != entry.description
            || row.try_get::<bool, _>("non_transactional")?
        {
            return Err(Error::new(
                ErrorKind::Checksum,
                "persisted migration phase identity differs",
            ));
        }
        if row.try_get::<&str, _>("phase")? != "applied" || !applied.contains_key(&version) {
            return Err(Error::new(
                ErrorKind::Dirty,
                "migration has an unfinished execution phase",
            ));
        }
    }
    Ok(())
}
async fn phase(
    connection: &mut SqliteConnection,
    entry: &Migration,
    bundle: &Bundle,
    phase: &str,
) -> Result<()> {
    sqlx::query("INSERT INTO _nativeweave_migration_phases (version, provider, sql_hash, bundle_hash, description, non_transactional, phase) VALUES (?1,1,?2,?3,?4,0,?5) ON CONFLICT (version) DO UPDATE SET phase=excluded.phase")
        .bind(entry.version).bind(entry.sql_hash.as_slice()).bind(bundle.content_hash.as_slice()).bind(&entry.description).bind(phase).execute(connection).await?;
    Ok(())
}
async fn tables(
    connection: &mut SqliteConnection,
    limits: &Limits,
    cancel: &Cancellation,
) -> Result<Output> {
    let mut budget = Budget::new(limits);
    let mut names = Vec::<String>::new();
    {
        let mut stream = sqlx::query("SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name").fetch(&mut *connection);
        while let Some(row) = stream.try_next().await? {
            cancel.check()?;
            let name: &str = row.try_get("name")?;
            budget.row(80)?;
            budget.text("main")?;
            budget.text(name)?;
            names.push(name.to_owned());
        }
    }
    let mut result = Vec::new();
    for name in names {
        let mut table = Table {
            schema: Some("main".into()),
            name,
            columns: Vec::new(),
        };
        let primary_index: i64 =
            sqlx::query_scalar("SELECT count(*) FROM pragma_index_list(?1) WHERE origin='pk'")
                .bind(&table.name)
                .fetch_one(&mut *connection)
                .await?;
        let mut stream =
            sqlx::query("SELECT name,type,\"notnull\",pk FROM pragma_table_info(?1) ORDER BY cid")
                .bind(&table.name)
                .fetch(&mut *connection);
        while let Some(row) = stream.try_next().await? {
            cancel.check()?;
            let name: &str = row.try_get("name")?;
            let store: &str = row.try_get("type")?;
            budget.row(92)?;
            budget.text(name)?;
            budget.text(store)?;
            let primary_key = row.try_get::<i64, _>("pk")? > 0;
            // SQLite rowid INTEGER PRIMARY KEY is non-null despite PRAGMA's notnull=0.
            // Non-integer PRIMARY KEY columns retain SQLite's actual nullable semantics.
            let nullable = row.try_get::<i64, _>("notnull")? == 0
                && !(primary_key && primary_index == 0 && store.eq_ignore_ascii_case("INTEGER"));
            table.columns.push(Column {
                name: name.to_owned(),
                store_type: store.to_owned(),
                nullable,
                primary_key,
            });
        }
        result.push(table);
    }
    Ok(Output::Tables(result))
}
