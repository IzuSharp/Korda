//! Explicit administration over SQLx; no request pool or implicit startup migration.
//!
//! PostgreSQL scripts are atomic per transactional entry. SQLite's tracked outer
//! IMMEDIATE transaction makes the entire selected batch atomic. Dedicated
//! connections are always closed, including checksum, cancellation and cleanup failures.
use sha2::{Digest, Sha256};
use sqlx::{
    AssertSqlSafe, SqlSafeStr,
    migrate::{MigrateError, Migration as NativeMigration, MigrationType, Migrator},
};
use std::{
    sync::{
        Arc,
        atomic::{AtomicBool, AtomicUsize, Ordering},
    },
    time::Duration,
};

#[cfg(feature = "postgres")]
mod postgres;
mod script;
#[cfg(feature = "sqlite")]
mod sqlite;
#[cfg(test)]
mod tests;

pub static LIVE_CONNECTIONS: AtomicUsize = AtomicUsize::new(0);
pub static CLOSED_CONNECTIONS: AtomicUsize = AtomicUsize::new(0);
pub const LEDGER: &str = "_sqlx_migrations";
pub const PHASES: &str = "_nativeweave_migration_phases";

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ErrorKind {
    Invalid,
    Checksum,
    Dirty,
    Capability,
    Capacity,
    Provider,
    Canceled,
    Timeout,
    Cleanup,
}
#[derive(Clone, Debug)]
pub struct Error {
    pub kind: ErrorKind,
    pub message: &'static str,
    pub provider: Option<String>,
    pub cleanup: Option<Box<Error>>,
}
pub type Result<T> = std::result::Result<T, Error>;
impl Error {
    pub fn new(kind: ErrorKind, message: &'static str) -> Self {
        Self {
            kind,
            message,
            provider: None,
            cleanup: None,
        }
    }
    pub fn invalid(message: &'static str) -> Self {
        Self::new(ErrorKind::Invalid, message)
    }
    pub fn capacity() -> Self {
        Self::new(
            ErrorKind::Capacity,
            "administrative result exceeds its finite budget",
        )
    }
    fn with_cleanup(mut self, cleanup: Error) -> Self {
        self.cleanup = Some(match self.cleanup.take() {
            Some(previous) => Box::new(previous.with_cleanup(cleanup)),
            None => Box::new(cleanup),
        });
        self
    }
}
impl From<sqlx::Error> for Error {
    fn from(value: sqlx::Error) -> Self {
        let mut error = Self::new(
            ErrorKind::Provider,
            "administrative provider command failed",
        );
        error.provider = value
            .as_database_error()
            .and_then(|x| x.code())
            .map(|s| s.into_owned())
            .filter(|s| s.len() <= 16 && s.bytes().all(|x| x.is_ascii_alphanumeric()));
        error
    }
}
impl From<MigrateError> for Error {
    fn from(value: MigrateError) -> Self {
        match value {
            MigrateError::VersionMismatch(_) | MigrateError::VersionMissing(_) => Self::new(
                ErrorKind::Checksum,
                "migration identity differs from the persisted ledger",
            ),
            MigrateError::Dirty(_) => {
                Self::new(ErrorKind::Dirty, "migration ledger has an unfinished phase")
            }
            MigrateError::Execute(error) | MigrateError::ExecuteMigration(error, _) => error.into(),
            _ => Self::new(ErrorKind::Provider, "SQLx migration failed"),
        }
    }
}
impl std::fmt::Display for Error {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{} ({:?})", self.message, self.kind)
    }
}
impl std::error::Error for Error {}

#[derive(Default)]
pub struct Cancellation {
    requested: AtomicBool,
}
impl Cancellation {
    pub fn cancel(&self) {
        self.requested.store(true, Ordering::Release);
    }
    pub fn requested(&self) -> bool {
        self.requested.load(Ordering::Acquire)
    }
    pub fn check(&self) -> Result<()> {
        if self.requested() {
            Err(Error::new(
                ErrorKind::Canceled,
                "administrative operation canceled",
            ))
        } else {
            Ok(())
        }
    }
}
#[derive(Clone, Debug)]
pub struct Limits {
    pub rows: usize,
    pub bytes: usize,
    pub field_bytes: usize,
    pub command_timeout: Duration,
    pub connect_timeout: Duration,
}
impl Limits {
    pub fn validate(&self) -> Result<()> {
        if self.rows > 1_000_000
            || self.bytes > 1 << 30
            || self.field_bytes == 0
            || self.field_bytes > 64 << 20
            || self.connect_timeout.is_zero()
            || self.connect_timeout > Duration::from_secs(3600)
            || self.command_timeout.is_zero()
            || self.command_timeout > Duration::from_secs(3600)
        {
            return Err(Error::invalid("invalid administrative limits"));
        }
        Ok(())
    }
}
pub(crate) struct Budget<'a> {
    limits: &'a Limits,
    rows: usize,
    bytes: usize,
}
impl<'a> Budget<'a> {
    fn new(limits: &'a Limits) -> Self {
        Self {
            limits,
            rows: 0,
            bytes: 48,
        }
    }
    fn row(&mut self, overhead: usize) -> Result<()> {
        self.rows = self.rows.checked_add(1).ok_or_else(Error::capacity)?;
        self.bytes = self
            .bytes
            .checked_add(overhead)
            .ok_or_else(Error::capacity)?;
        if self.rows > self.limits.rows
            || self.bytes > self.limits.bytes.min(nativeweave_data_contracts::MAX_BYTES)
        {
            return Err(Error::capacity());
        }
        Ok(())
    }
    fn text(&mut self, value: &str) -> Result<()> {
        if value.len() > self.limits.field_bytes || value.contains('\0') {
            return Err(Error::capacity());
        }
        self.bytes = self
            .bytes
            .checked_add(value.len())
            .ok_or_else(Error::capacity)?;
        if self.bytes > self.limits.bytes.min(nativeweave_data_contracts::MAX_BYTES) {
            return Err(Error::capacity());
        }
        Ok(())
    }
}

#[derive(Clone, Debug)]
pub struct Migration {
    pub version: i64,
    pub description: String,
    pub sql: String,
    pub non_transactional: bool,
    pub sql_hash: [u8; 32],
}
impl Migration {
    pub fn new(
        version: i64,
        description: String,
        sql: String,
        non_transactional: bool,
    ) -> Result<Self> {
        if version <= 0
            || description.is_empty()
            || description.contains('\0')
            || sql.is_empty()
            || sql.contains('\0')
            || description.len() > nativeweave_data_contracts::MAX_BYTES
            || sql.len() > nativeweave_data_contracts::MAX_BYTES
        {
            return Err(Error::invalid("invalid migration entry"));
        }
        script::validate(&sql)?;
        let sql_hash = Sha256::digest(sql.as_bytes()).into();
        Ok(Self {
            version,
            description,
            sql,
            non_transactional,
            sql_hash,
        })
    }
    pub fn native(&self) -> NativeMigration {
        NativeMigration::new(
            self.version,
            self.description.clone().into(),
            MigrationType::Simple,
            AssertSqlSafe(self.sql.clone()).into_sql_str(),
            self.non_transactional,
        )
    }
    pub fn checksum(&self) -> String {
        hex(&self.native().checksum)
    }
}
#[derive(Clone, Debug)]
pub struct Bundle {
    pub provider: u32,
    pub migrations: Vec<Migration>,
    pub content_hash: [u8; 32],
}
impl Bundle {
    pub fn new(provider: u32, migrations: Vec<Migration>, content_hash: [u8; 32]) -> Result<Self> {
        if provider > 1 || migrations.len() > nativeweave_data_contracts::MAX_NODES {
            return Err(Error::invalid("invalid migration bundle"));
        }
        let mut previous = 0;
        let mut size = 0usize;
        for entry in &migrations {
            if entry.version <= previous {
                return Err(Error::invalid(
                    "migration versions must be positive and strictly ascending",
                ));
            }
            previous = entry.version;
            size = size
                .checked_add(entry.sql.len())
                .and_then(|x| x.checked_add(entry.description.len()))
                .ok_or_else(Error::capacity)?;
            if size > nativeweave_data_contracts::MAX_BYTES {
                return Err(Error::capacity());
            }
        }
        Ok(Self {
            provider,
            migrations,
            content_hash,
        })
    }
    fn migrator(&self, target: Option<i64>) -> Migrator {
        let mut value = Migrator::with_migrations(
            self.migrations
                .iter()
                .filter(|x| target.is_none_or(|v| x.version <= v))
                .map(Migration::native)
                .collect(),
        );
        // This adapter acquires the actual provider lock before reading either ledger.
        value.set_locking(false);
        value
    }
}
#[derive(Clone, Debug)]
pub struct Request {
    pub action: u32,
    pub bundle: Option<Bundle>,
    pub target: Option<i64>,
    pub lock_timeout: Duration,
    pub allow_non_transactional: bool,
    pub schema: Option<String>,
}
impl Request {
    pub fn validate(&self, provider: u32, limits: &Limits) -> Result<()> {
        limits.validate()?;
        if self.action > 3
            || self.lock_timeout.is_zero()
            || self.lock_timeout > Duration::from_secs(3600)
            || self.target.is_some_and(|x| x <= 0)
            || (self.action == 3) != self.bundle.is_none()
            || self.action != 0 && (self.target.is_some() || self.allow_non_transactional)
            || self.action != 3 && self.schema.is_some()
        {
            return Err(Error::invalid("invalid administration action"));
        }
        if let Some(bundle) = &self.bundle {
            if bundle.provider != provider {
                return Err(Error::invalid("migration provider differs from source"));
            }
            if bundle.migrations.iter().any(|x| {
                x.description.len() > limits.field_bytes || x.sql.len() > limits.field_bytes
            }) {
                return Err(Error::capacity());
            }
            if self.action == 0
                && bundle
                    .migrations
                    .iter()
                    .filter(|entry| self.target.is_none_or(|target| entry.version <= target))
                    .count()
                    > limits.rows
            {
                return Err(Error::capacity());
            }
            if bundle.migrations.iter().any(|entry| {
                entry.non_transactional
                    && (provider == 1
                        || self.action == 0
                            && !self.allow_non_transactional
                            && self.target.is_none_or(|target| entry.version <= target))
            }) {
                return Err(Error::new(
                    ErrorKind::Capability,
                    "nontransactional migration requires an explicit PostgreSQL profile",
                ));
            }
        }
        if let Some(schema) = &self.schema
            && (schema.is_empty()
                || schema.len() > 63
                || !schema.bytes().enumerate().all(|(i, c)| {
                    c.is_ascii_alphabetic() || c == b'_' || i > 0 && c.is_ascii_digit()
                })
                || schema.starts_with("pg_")
                || schema == "information_schema"
                || provider == 1 && schema != "main")
        {
            return Err(Error::invalid(
                "schema scope is not an admitted user schema",
            ));
        }
        Ok(())
    }
}
#[derive(Clone, Debug)]
pub struct Status {
    pub version: i64,
    pub description: String,
    pub checksum: String,
    pub applied: bool,
}
#[derive(Clone, Debug)]
pub struct Column {
    pub name: String,
    pub store_type: String,
    pub nullable: bool,
    pub primary_key: bool,
}
#[derive(Clone, Debug)]
pub struct Table {
    pub schema: Option<String>,
    pub name: String,
    pub columns: Vec<Column>,
}
#[derive(Clone, Debug)]
pub enum Output {
    Report { applied: u32, current: Option<i64> },
    Status(Vec<Status>),
    Validated,
    Tables(Vec<Table>),
}
pub enum ConnectionOptions {
    #[cfg(feature = "postgres")]
    Postgres(sqlx::postgres::PgConnectOptions),
    #[cfg(feature = "sqlite")]
    Sqlite(sqlx::sqlite::SqliteConnectOptions),
}
pub async fn run(
    options: ConnectionOptions,
    request: Request,
    limits: Limits,
    cancel: Arc<Cancellation>,
) -> Result<Output> {
    let provider = match &options {
        #[cfg(feature = "postgres")]
        ConnectionOptions::Postgres(_) => 0,
        #[cfg(feature = "sqlite")]
        ConnectionOptions::Sqlite(_) => 1,
    };
    request.validate(provider, &limits)?;
    cancel.check()?;
    match options {
        #[cfg(feature = "postgres")]
        ConnectionOptions::Postgres(options) => {
            postgres::run(options, request, limits, cancel).await
        }
        #[cfg(feature = "sqlite")]
        ConnectionOptions::Sqlite(options) => sqlite::run(options, request, limits, cancel).await,
    }
}
pub fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}
pub(crate) fn finish<T>(
    result: Result<T>,
    close: std::result::Result<(), sqlx::Error>,
) -> Result<T> {
    LIVE_CONNECTIONS.fetch_sub(1, Ordering::AcqRel);
    CLOSED_CONNECTIONS.fetch_add(1, Ordering::AcqRel);
    match (result, close) {
        (result, Ok(())) => result,
        (Err(error), Err(cleanup)) => Err(error.with_cleanup(cleanup.into())),
        (Ok(_), Err(cleanup)) => {
            let mut error: Error = cleanup.into();
            error.kind = ErrorKind::Cleanup;
            Err(error)
        }
    }
}
pub(crate) fn statuses(
    bundle: &Bundle,
    applied: &std::collections::BTreeMap<i64, Vec<u8>>,
    limits: &Limits,
) -> Result<Vec<Status>> {
    let mut budget = Budget::new(limits);
    let mut result = Vec::new();
    for migration in &bundle.migrations {
        let checksum = migration.checksum();
        budget.row(112)?;
        budget.text(&migration.description)?;
        budget.text(&checksum)?;
        result.push(Status {
            version: migration.version,
            description: migration.description.clone(),
            checksum,
            applied: applied.contains_key(&migration.version),
        });
    }
    Ok(result)
}
pub(crate) fn validate_ledger(
    bundle: &Bundle,
    applied: &std::collections::BTreeMap<i64, Vec<u8>>,
) -> Result<()> {
    for (version, checksum) in applied {
        let entry = bundle
            .migrations
            .iter()
            .find(|x| x.version == *version)
            .ok_or_else(|| {
                Error::new(
                    ErrorKind::Checksum,
                    "applied migration is absent from this bundle",
                )
            })?;
        if entry.native().checksum.as_ref() != checksum {
            return Err(Error::new(
                ErrorKind::Checksum,
                "migration checksum differs from SQLx ledger",
            ));
        }
    }
    Ok(())
}
