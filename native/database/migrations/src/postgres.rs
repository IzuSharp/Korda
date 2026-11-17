use crate::*;
use futures_util::TryStreamExt;
use sqlx::{
    Connection, Row,
    migrate::Migrate,
    postgres::{PgConnectOptions, PgConnection},
};
use std::collections::BTreeMap;

pub async fn run(
    options: PgConnectOptions,
    request: Request,
    limits: Limits,
    cancel: Arc<Cancellation>,
) -> Result<Output> {
    let mut connection =
        tokio::time::timeout(limits.connect_timeout, PgConnection::connect_with(&options))
            .await
            .map_err(|_| Error::new(ErrorKind::Timeout, "administrative connection timed out"))??;
    LIVE_CONNECTIONS.fetch_add(1, Ordering::AcqRel);
    let result = execute(&mut connection, &request, &limits, &cancel).await;
    // SQLx can leave its session advisory lock held on early return. This
    // connection is never pooled, even after a successful administrative action.
    let close = connection.close().await;
    finish(result, close)
}
async fn execute(
    connection: &mut PgConnection,
    request: &Request,
    limits: &Limits,
    cancel: &Cancellation,
) -> Result<Output> {
    sqlx::query(
        "SELECT set_config('statement_timeout',$1,false), set_config('lock_timeout',$2,false)",
    )
    .bind(format!("{}ms", limits.command_timeout.as_millis().max(1)))
    .bind(format!("{}ms", request.lock_timeout.as_millis().max(1)))
    .execute(&mut *connection)
    .await?;
    cancel.check()?;
    if request.action == 3 {
        return tables(connection, request.schema.as_deref(), limits, cancel).await;
    }
    connection.lock().await?;
    cancel.check()?;
    let bundle = request
        .bundle
        .as_ref()
        .ok_or_else(|| Error::invalid("migration bundle required"))?;
    if request.action == 0 {
        connection.ensure_migrations_table(LEDGER).await?;
        sqlx::query("CREATE TABLE IF NOT EXISTS _nativeweave_migration_phases (version BIGINT PRIMARY KEY, provider INTEGER NOT NULL, sql_hash BYTEA NOT NULL, bundle_hash BYTEA NOT NULL, description TEXT NOT NULL, non_transactional BOOLEAN NOT NULL, phase TEXT NOT NULL)").execute(&mut *connection).await?;
    }
    let mut applied = ledger(connection, limits).await?;
    validate_ledger(bundle, &applied)?;
    phases(connection, bundle, &applied, request, limits).await?;
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
    let mut count = 0;
    for entry in &bundle.migrations {
        if request.target.is_some_and(|target| entry.version > target) {
            break;
        }
        if applied.contains_key(&entry.version) {
            continue;
        }
        cancel.check()?;
        let migrator = bundle.migrator(Some(entry.version));
        if entry.non_transactional {
            phase(connection, entry, bundle, "applying").await?;
            // Persist intent before dispatch. SQLx preserves its own SHA-384 ledger.
            // Failure/crash leaves applying, requiring a new explicit identical
            // AllowNonTransactional call; scripts must be safe for that replay.
            migrator.run_direct(None, &mut *connection, false).await?;
            phase(connection, entry, bundle, "applied").await?;
        } else {
            let mut tx = connection.begin().await?;
            let result: Result<()> = async {
                phase(&mut tx, entry, bundle, "applying").await?;
                migrator.run_direct(None, &mut *tx, false).await?;
                cancel.check()?;
                phase(&mut tx, entry, bundle, "applied").await?;
                Ok(())
            }
            .await;
            match result {
                Ok(()) => tx.commit().await?,
                Err(error) => {
                    return match tx.rollback().await {
                        Ok(()) => Err(error),
                        Err(cleanup) => Err(error.with_cleanup(cleanup.into())),
                    };
                }
            }
        }
        applied.insert(entry.version, entry.native().checksum.into_owned());
        count += 1;
    }
    cancel.check()?;
    Ok(Output::Report {
        applied: count,
        current: applied.last_key_value().map(|(v, _)| *v),
    })
}
async fn exists(connection: &mut PgConnection, table: &str) -> Result<bool> {
    Ok(
        sqlx::query_scalar::<_, bool>("SELECT to_regclass($1) IS NOT NULL")
            .bind(table)
            .fetch_one(connection)
            .await?,
    )
}
async fn ledger(connection: &mut PgConnection, limits: &Limits) -> Result<BTreeMap<i64, Vec<u8>>> {
    let mut result = BTreeMap::new();
    if !exists(connection, LEDGER).await? {
        return Ok(result);
    }
    let mut stream =
        sqlx::query("SELECT version, checksum, success FROM _sqlx_migrations ORDER BY version")
            .fetch(connection);
    let mut budget = Budget::new(limits);
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
    connection: &mut PgConnection,
    bundle: &Bundle,
    applied: &BTreeMap<i64, Vec<u8>>,
    request: &Request,
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
        let phase: &str = row.try_get("phase")?;
        budget.text(description)?;
        if row.try_get::<i32, _>("provider")? != 0
            || hash.as_slice() != entry.sql_hash
            || bundle_hash.len() != 32
            || description != entry.description
            || row.try_get::<bool, _>("non_transactional")? != entry.non_transactional
            || !matches!(phase, "applying" | "applied")
        {
            return Err(Error::new(
                ErrorKind::Checksum,
                "persisted migration phase identity differs",
            ));
        }
        if !applied.contains_key(&version)
            && (phase == "applied" || request.action == 2 || !entry.non_transactional)
        {
            return Err(Error::new(
                ErrorKind::Dirty,
                "migration has an unfinished execution phase",
            ));
        }
    }
    Ok(())
}
async fn phase(
    connection: &mut PgConnection,
    entry: &Migration,
    bundle: &Bundle,
    phase: &str,
) -> Result<()> {
    sqlx::query("INSERT INTO _nativeweave_migration_phases (version, provider, sql_hash, bundle_hash, description, non_transactional, phase) VALUES ($1,0,$2,$3,$4,$5,$6) ON CONFLICT (version) DO UPDATE SET phase=EXCLUDED.phase")
        .bind(entry.version).bind(entry.sql_hash.as_slice()).bind(bundle.content_hash.as_slice()).bind(&entry.description).bind(entry.non_transactional).bind(phase).execute(connection).await?;
    Ok(())
}
async fn tables(
    connection: &mut PgConnection,
    schema: Option<&str>,
    limits: &Limits,
    cancel: &Cancellation,
) -> Result<Output> {
    let selected: String = match schema {
        Some(schema) => schema.to_owned(),
        None => {
            sqlx::query_scalar("SELECT current_schema()")
                .fetch_one(&mut *connection)
                .await?
        }
    };
    if selected.starts_with("pg_") || selected == "information_schema" {
        return Err(Error::invalid("system schema scope is not admitted"));
    }
    let mut budget = Budget::new(limits);
    let mut tables = Vec::<Table>::new();
    let mut stream = sqlx::query("SELECT c.relname AS table_name, a.attname AS column_name, format_type(a.atttypid,a.atttypmod) AS store_type, NOT a.attnotnull AS nullable, EXISTS (SELECT 1 FROM pg_index i WHERE i.indrelid=c.oid AND i.indisprimary AND a.attnum=ANY(i.indkey)) AS primary_key FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace LEFT JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped WHERE n.nspname=$1 AND c.relkind IN ('r','p') ORDER BY c.relname,a.attnum")
        .bind(&selected).fetch(connection);
    while let Some(row) = stream.try_next().await? {
        cancel.check()?;
        let name: &str = row.try_get("table_name")?;
        if tables.last().is_none_or(|x| x.name != name) {
            budget.row(80)?;
            budget.text(&selected)?;
            budget.text(name)?;
            tables.push(Table {
                schema: Some(selected.clone()),
                name: name.to_owned(),
                columns: Vec::new(),
            });
        }
        let Some(column) = row.try_get::<Option<&str>, _>("column_name")? else {
            continue;
        };
        let store: &str = row.try_get("store_type")?;
        budget.row(92)?;
        budget.text(column)?;
        budget.text(store)?;
        tables.last_mut().unwrap().columns.push(Column {
            name: column.to_owned(),
            store_type: store.to_owned(),
            nullable: row.try_get("nullable")?,
            primary_key: row.try_get("primary_key")?,
        });
    }
    Ok(Output::Tables(tables))
}
