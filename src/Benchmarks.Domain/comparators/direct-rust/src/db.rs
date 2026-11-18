use crate::protocol::{Config, Error, Result, hash};
use sqlx::{
    AssertSqlSafe, ConnectOptions, Row,
    pool::PoolConnection,
    postgres::{PgConnectOptions, PgPoolOptions, PgRow, PgSslMode},
    sqlite::{
        SqliteConnectOptions, SqliteJournalMode, SqlitePoolOptions, SqliteRow, SqliteSynchronous,
    },
};
use std::{
    str::FromStr,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
    time::Duration,
};
use tokio::{
    sync::{OwnedSemaphorePermit, Semaphore},
    task::JoinHandle,
};

pub enum Value {
    Text(String),
    Integer(i64),
    Bytes(Vec<u8>),
}
impl From<&str> for Value {
    fn from(x: &str) -> Self {
        Self::Text(x.into())
    }
}
impl From<String> for Value {
    fn from(x: String) -> Self {
        Self::Text(x)
    }
}
impl From<i64> for Value {
    fn from(x: i64) -> Self {
        Self::Integer(x)
    }
}
impl From<&[u8]> for Value {
    fn from(x: &[u8]) -> Self {
        Self::Bytes(x.into())
    }
}
pub enum DataRow {
    Sqlite(SqliteRow),
    Postgres(PgRow),
}
impl DataRow {
    pub fn text(&self, n: usize) -> Result<String> {
        Ok(match self {
            Self::Sqlite(x) => x.try_get(n)?,
            Self::Postgres(x) => x.try_get(n)?,
        })
    }
    pub fn integer(&self, n: usize) -> Result<i64> {
        Ok(match self {
            Self::Sqlite(x) => x.try_get(n)?,
            Self::Postgres(x) => x.try_get(n)?,
        })
    }
    pub fn bytes(&self, n: usize) -> Result<Vec<u8>> {
        Ok(match self {
            Self::Sqlite(x) => x.try_get(n)?,
            Self::Postgres(x) => x.try_get(n)?,
        })
    }
}
enum Pool {
    Sqlite(sqlx::SqlitePool),
    Postgres(sqlx::PgPool),
}
enum Connection {
    Sqlite(PoolConnection<sqlx::Sqlite>),
    Postgres(PoolConnection<sqlx::Postgres>),
}
pub struct Database {
    pool: Pool,
    slots: Arc<Semaphore>,
    waiters: Arc<Semaphore>,
    retirements: Mutex<Vec<JoinHandle<()>>>,
    maximum: usize,
    cleanup_failed: AtomicBool,
}
pub struct Lease {
    owner: Arc<Database>,
    connection: Option<Connection>,
    permit: Option<OwnedSemaphorePermit>,
    transaction: bool,
    committed: bool,
    pub unknown: bool,
    confirmed_rollback: Option<Box<dyn FnOnce() + Send>>,
}

impl Database {
    pub async fn new(config: &Config, consumer: bool) -> Result<Arc<Self>> {
        let maximum = if consumer { 2 } else { 16 };
        let pool = if !consumer && config.provider == "postgresql" {
            if config.database.contains(['?', '#']) || !config.database.starts_with("postgres") {
                return Err(Error(400, "invalid_database_uri"));
            }
            let options = PgConnectOptions::from_str(&config.database)?
                .ssl_mode(if config.allow_local_database_tls_disable {
                    PgSslMode::Disable
                } else {
                    PgSslMode::VerifyFull
                })
                .application_name("NativeWeave.B23.DirectRust")
                .statement_cache_capacity(64)
                .disable_statement_logging();
            Pool::Postgres(
                PgPoolOptions::new()
                    .max_connections(maximum as u32)
                    .min_connections(0)
                    .acquire_timeout(Duration::from_secs(5))
                    .idle_timeout(None)
                    .max_lifetime(None)
                    .connect_lazy_with(options),
            )
        } else {
            let options = SqliteConnectOptions::new()
                .filename(if consumer {
                    &config.consumer_database
                } else {
                    &config.database
                })
                .create_if_missing(true)
                .shared_cache(false)
                .foreign_keys(true)
                .journal_mode(SqliteJournalMode::Wal)
                .synchronous(SqliteSynchronous::Full)
                .busy_timeout(Duration::from_secs(5))
                .statement_cache_capacity(32)
                .command_buffer_size(1)
                .row_buffer_size(1)
                .disable_statement_logging();
            Pool::Sqlite(
                SqlitePoolOptions::new()
                    .max_connections(maximum as u32)
                    .min_connections(0)
                    .acquire_timeout(Duration::from_secs(5))
                    .idle_timeout(None)
                    .max_lifetime(None)
                    .connect_lazy_with(options),
            )
        };
        let source = Arc::new(Self {
            pool,
            slots: Arc::new(Semaphore::new(maximum)),
            waiters: Arc::new(Semaphore::new(if consumer { 4 } else { 64 })),
            retirements: Mutex::new(Vec::new()),
            maximum,
            cleanup_failed: AtomicBool::new(false),
        });
        let resource = if consumer {
            "consumer"
        } else {
            config.provider.as_str()
        };
        source.migrate(resource).await?;
        if !source.is_postgres() {
            let mut lease = source.open().await?;
            let rows = lease.query("SELECT sqlite_version()", vec![]).await?;
            if rows.len() != 1 || rows[0].text(0)? != "3.51.3" {
                return Err(Error(500, "sqlite_version_mismatch"));
            }
            eprintln!("B23_SQLITE version=3.51.3 embedded=true");
        }
        Ok(source)
    }
    pub fn is_postgres(&self) -> bool {
        matches!(self.pool, Pool::Postgres(_))
    }
    pub async fn open(self: &Arc<Self>) -> Result<Lease> {
        let permit = match self.slots.clone().try_acquire_owned() {
            Ok(value) => value,
            Err(_) => {
                let _waiter = self
                    .waiters
                    .clone()
                    .try_acquire_owned()
                    .map_err(|_| Error(503, "database_capacity"))?;
                tokio::time::timeout(Duration::from_secs(5), self.slots.clone().acquire_owned())
                    .await
                    .map_err(|_| Error(503, "database_admission_timeout"))?
                    .map_err(|_| Error(503, "database_closed"))?
            }
        };
        let connection = match &self.pool {
            Pool::Sqlite(x) => Connection::Sqlite(x.acquire().await?),
            Pool::Postgres(x) => Connection::Postgres(x.acquire().await?),
        };
        Ok(Lease {
            owner: self.clone(),
            connection: Some(connection),
            permit: Some(permit),
            transaction: false,
            committed: false,
            unknown: false,
            confirmed_rollback: None,
        })
    }
    async fn migrate(self: &Arc<Self>, resource: &str) -> Result<()> {
        let sql = match resource {
            "sqlite" => include_str!("../../../../Reference.TaskService/Migrations/sqlite.sql"),
            "postgresql" => {
                include_str!("../../../../Reference.TaskService/Migrations/postgresql.sql")
            }
            "consumer" => include_str!("../../../../Reference.TaskService/Migrations/consumer.sql"),
            _ => return Err(Error(400, "invalid_schema")),
        };
        let mut lease = self.open().await?;
        lease.begin().await?;
        lease.execute("CREATE TABLE IF NOT EXISTS __b23_migrations(version BIGINT PRIMARY KEY,checksum TEXT NOT NULL)",vec![]).await?;
        let rows = lease
            .query(
                "SELECT checksum FROM __b23_migrations WHERE version=1",
                vec![],
            )
            .await?;
        let digest = hash(sql.as_bytes());
        if rows.is_empty() {
            lease.batch(sql).await?;
            lease
                .execute(
                    "INSERT INTO __b23_migrations(version,checksum) VALUES(1,$1)",
                    vec![digest.into()],
                )
                .await?;
        } else if rows.len() != 1 || rows[0].text(0)? != digest {
            return Err(Error(500, "migration_checksum_mismatch"));
        }
        lease.commit().await?;
        Ok(())
    }
    pub async fn close(&self) -> Result<()> {
        loop {
            let handles = std::mem::take(&mut *self.retirements.lock().expect("retirement gate"));
            if handles.is_empty() {
                break;
            }
            for handle in handles {
                handle
                    .await
                    .map_err(|_| Error(500, "cleanup_task_failed"))?;
            }
        }
        match &self.pool {
            Pool::Sqlite(x) => x.close().await,
            Pool::Postgres(x) => x.close().await,
        };
        // A connection may finish pool return while its short cleanup
        // continuation is still queued. Join that continuation as well.
        loop {
            let handles = std::mem::take(&mut *self.retirements.lock().expect("retirement gate"));
            if handles.is_empty() {
                break;
            }
            for handle in handles {
                handle
                    .await
                    .map_err(|_| Error(500, "cleanup_task_failed"))?;
            }
        }
        if self.slots.available_permits() != self.maximum
            || self.cleanup_failed.load(Ordering::Acquire)
        {
            return Err(Error(500, "database_owners_remain"));
        }
        Ok(())
    }
}

impl Connection {
    async fn execute(&mut self, sql: &str, values: Vec<Value>) -> Result<u64> {
        Ok(match self {
            Self::Sqlite(connection) => {
                let mut query = sqlx::query::<sqlx::Sqlite>(AssertSqlSafe(sql));
                for value in values {
                    query = match value {
                        Value::Text(x) => query.bind(x),
                        Value::Integer(x) => query.bind(x),
                        Value::Bytes(x) => query.bind(x),
                    }
                }
                query.execute(&mut **connection).await?.rows_affected()
            }
            Self::Postgres(connection) => {
                let mut query = sqlx::query::<sqlx::Postgres>(AssertSqlSafe(sql));
                for value in values {
                    query = match value {
                        Value::Text(x) => query.bind(x),
                        Value::Integer(x) => query.bind(x),
                        Value::Bytes(x) => query.bind(x),
                    }
                }
                query.execute(&mut **connection).await?.rows_affected()
            }
        })
    }
    async fn query(&mut self, sql: &str, values: Vec<Value>) -> Result<Vec<DataRow>> {
        use futures_util::TryStreamExt;
        let mut rows = Vec::new();
        match self {
            Self::Sqlite(connection) => {
                let mut query = sqlx::query::<sqlx::Sqlite>(AssertSqlSafe(sql));
                for value in values {
                    query = match value {
                        Value::Text(x) => query.bind(x),
                        Value::Integer(x) => query.bind(x),
                        Value::Bytes(x) => query.bind(x),
                    }
                }
                let mut stream = query.fetch(&mut **connection);
                while let Some(row) = stream.try_next().await? {
                    if rows.len() >= 128 {
                        return Err(Error(500, "row_capacity"));
                    }
                    rows.push(DataRow::Sqlite(row));
                }
            }
            Self::Postgres(connection) => {
                let mut query = sqlx::query::<sqlx::Postgres>(AssertSqlSafe(sql));
                for value in values {
                    query = match value {
                        Value::Text(x) => query.bind(x),
                        Value::Integer(x) => query.bind(x),
                        Value::Bytes(x) => query.bind(x),
                    }
                }
                let mut stream = query.fetch(&mut **connection);
                while let Some(row) = stream.try_next().await? {
                    if rows.len() >= 128 {
                        return Err(Error(500, "row_capacity"));
                    }
                    rows.push(DataRow::Postgres(row));
                }
            }
        }
        Ok(rows)
    }
    async fn batch(&mut self, sql: &str) -> Result<()> {
        match self {
            Self::Sqlite(x) => {
                sqlx::raw_sql(AssertSqlSafe(sql)).execute(&mut **x).await?;
            }
            Self::Postgres(x) => {
                sqlx::raw_sql(AssertSqlSafe(sql)).execute(&mut **x).await?;
            }
        }
        Ok(())
    }
    async fn discard(self) -> Result<()> {
        match self {
            Self::Sqlite(x) => x.close().await?,
            Self::Postgres(x) => x.close().await?,
        };
        Ok(())
    }
}
impl Lease {
    pub async fn begin(&mut self) -> Result<()> {
        let sql = if self.owner.is_postgres() {
            "BEGIN ISOLATION LEVEL READ COMMITTED"
        } else {
            "BEGIN IMMEDIATE"
        };
        self.transaction = true;
        self.execute(sql, vec![]).await?;
        Ok(())
    }
    pub fn on_rollback(&mut self, callback: impl FnOnce() + Send + 'static) {
        self.confirmed_rollback = Some(Box::new(callback));
    }
    pub async fn execute(&mut self, sql: &str, values: Vec<Value>) -> Result<u64> {
        tokio::time::timeout(
            Duration::from_secs(15),
            self.connection
                .as_mut()
                .expect("live lease")
                .execute(sql, values),
        )
        .await
        .map_err(|_| Error(504, "database_timeout"))?
    }
    pub async fn query(&mut self, sql: &str, values: Vec<Value>) -> Result<Vec<DataRow>> {
        tokio::time::timeout(
            Duration::from_secs(15),
            self.connection
                .as_mut()
                .expect("live lease")
                .query(sql, values),
        )
        .await
        .map_err(|_| Error(504, "database_timeout"))?
    }
    async fn batch(&mut self, sql: &str) -> Result<()> {
        self.connection
            .as_mut()
            .expect("live lease")
            .batch(sql)
            .await
    }
    pub async fn commit(&mut self) -> Result<()> {
        // Mark before dispatch. A canceled/dropped waiter cannot reinterpret an
        // in-flight COMMIT as rollback; the durable protection remains.
        self.unknown = true;
        let result = tokio::time::timeout(
            Duration::from_secs(15),
            self.connection
                .as_mut()
                .expect("live lease")
                .execute("COMMIT", vec![]),
        )
        .await;
        match result {
            Ok(Ok(_)) => {
                self.committed = true;
                self.unknown = false;
                Ok(())
            }
            _ => Err(Error(503, "outcome_unknown")),
        }
    }
}
impl Drop for Lease {
    fn drop(&mut self) {
        let Some(mut connection) = self.connection.take() else {
            return;
        };
        let permit = self.permit.take();
        let rollback = self.transaction && !self.committed && !self.unknown;
        let uncertain = self.unknown;
        let callback = self.confirmed_rollback.take();
        let owner = self.owner.clone();
        let task = tokio::spawn(async move {
            let clean = if rollback {
                matches!(
                    tokio::time::timeout(
                        Duration::from_secs(15),
                        connection.execute("ROLLBACK", vec![])
                    )
                    .await,
                    Ok(Ok(_))
                )
            } else {
                !uncertain
            };
            if clean && rollback {
                if let Some(callback) = callback {
                    callback();
                }
            }
            if !clean {
                if connection.discard().await.is_err() {
                    owner.cleanup_failed.store(true, Ordering::Release);
                    eprintln!("B23_CLEANUP_FAILURE close");
                }
            }
            // Dropping a clean pooled connection starts SQLx's pool return; the
            // explicit pool close joins actual connections at top-level shutdown.
            drop(permit);
        });
        let mut pending = self.owner.retirements.lock().expect("retirement gate");
        pending.retain(|task| !task.is_finished());
        pending.push(task);
    }
}
