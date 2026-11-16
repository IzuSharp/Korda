use crate::{ErrorKind, ProviderError, Result};
use nativeweave_data_contracts::MAX_BYTES;
use sqlx::sqlite::{SqliteConnectOptions, SqliteJournalMode, SqlitePoolOptions, SqliteSynchronous};
use std::{path::Path, time::Duration};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum OpenMode {
    ReadWriteCreate,
    ReadWrite,
    ReadOnly,
    Memory,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum JournalMode {
    Wal,
    Delete,
    Memory,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SynchronousMode {
    Normal,
    Full,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BeginMode {
    Deferred,
    Immediate,
}

/// Provider profile copied before opening each physical SQLx connection.
#[derive(Clone, Debug)]
pub struct Options {
    pub mode: OpenMode,
    pub journal: JournalMode,
    pub synchronous: SynchronousMode,
    pub begin: BeginMode,
    pub busy_timeout: Duration,
    pub foreign_keys: bool,
}
impl Default for Options {
    fn default() -> Self {
        Self {
            mode: OpenMode::ReadWriteCreate,
            journal: JournalMode::Wal,
            synchronous: SynchronousMode::Full,
            begin: BeginMode::Deferred,
            busy_timeout: Duration::from_secs(5),
            foreign_keys: true,
        }
    }
}

/// Finite adapter capacities supplied by the owning engine, not new CLR options.
#[derive(Clone, Copy, Debug)]
pub struct Limits {
    pub max_connections: u32,
    pub max_commands: usize,
    pub max_input_bytes: usize,
    pub max_sql_bytes: usize,
    pub row_channel_capacity: usize,
    pub statement_cache_capacity: usize,
    pub acquire_timeout: Duration,
    pub progress_operations: i32,
}
impl Default for Limits {
    fn default() -> Self {
        Self {
            max_connections: 4,
            max_commands: 32,
            max_input_bytes: MAX_BYTES,
            max_sql_bytes: 64 * 1024,
            row_channel_capacity: 1,
            statement_cache_capacity: 32,
            acquire_timeout: Duration::from_secs(5),
            progress_operations: 1000,
        }
    }
}
impl Limits {
    pub fn validate(self) -> Result<()> {
        if self.max_connections == 0
            || self.max_connections > 1024
            || self.max_commands == 0
            || self.max_commands > 4096
            || self.max_input_bytes == 0
            || self.max_input_bytes > MAX_BYTES
            || self.max_sql_bytes == 0
            || self.max_sql_bytes > self.max_input_bytes
            || self.row_channel_capacity == 0
            || self.row_channel_capacity > 4096
            || self.statement_cache_capacity > 4096
            || self.acquire_timeout.is_zero()
            || self.progress_operations < 1
        {
            return Err(ProviderError::new(
                ErrorKind::Configuration,
                "invalid SQLite capacity",
            ));
        }
        Ok(())
    }
    /// Configures the existing SQLx pool; B04 retains its source/runtime ownership.
    pub fn pool_options(self) -> Result<SqlitePoolOptions> {
        self.validate()?;
        Ok(SqlitePoolOptions::new()
            .max_connections(self.max_connections)
            .min_connections(0)
            .acquire_timeout(self.acquire_timeout)
            .max_lifetime(None)
            .idle_timeout(None))
    }
}
impl Options {
    /// Decodes the exact B01 control-record envelope for provider options (kind 9).
    /// Unknown enums, noncanonical Boolean and field widths fail before opening.
    pub fn from_record(bytes: &[u8]) -> Result<Self> {
        let invalid =
            || ProviderError::new(ErrorKind::Configuration, "invalid SQLite options record");
        let fields = nativeweave_data_contracts::wire::control_fields(
            bytes,
            9,
            &[(1, 1), (2, 1), (3, 1), (4, 1), (5, 2), (6, 1)],
        )
        .map_err(|_| invalid())?;
        let word = |index: usize| -> Result<u32> {
            Ok(u32::from_le_bytes(
                fields[index].try_into().map_err(|_| invalid())?,
            ))
        };
        let mode = match word(0)? {
            0 => OpenMode::ReadWriteCreate,
            1 => OpenMode::ReadWrite,
            2 => OpenMode::ReadOnly,
            3 => OpenMode::Memory,
            _ => return Err(invalid()),
        };
        let journal = match word(1)? {
            0 => JournalMode::Wal,
            1 => JournalMode::Delete,
            2 => JournalMode::Memory,
            _ => return Err(invalid()),
        };
        let synchronous = match word(2)? {
            0 => SynchronousMode::Normal,
            1 => SynchronousMode::Full,
            _ => return Err(invalid()),
        };
        let begin = match word(3)? {
            0 => BeginMode::Deferred,
            1 => BeginMode::Immediate,
            _ => return Err(invalid()),
        };
        let busy_timeout = Duration::from_nanos(u64::from_le_bytes(
            fields[4].try_into().map_err(|_| invalid())?,
        ));
        let foreign_keys = match word(5)? {
            0 => false,
            1 => true,
            _ => return Err(invalid()),
        };
        Ok(Self {
            mode,
            journal,
            synchronous,
            begin,
            busy_timeout,
            foreign_keys,
        })
    }
    /// Validates before any file I/O and applies the profile to every physical connection.
    /// Private in-memory profiles require one connection and MEMORY journal explicitly.
    pub fn connect_options(&self, path: &Path, limits: Limits) -> Result<SqliteConnectOptions> {
        limits.validate()?;
        let name = path.to_str().ok_or_else(|| {
            ProviderError::new(ErrorKind::Configuration, "SQLite path must be UTF-8")
        })?;
        if name.is_empty()
            || name.len() > 4096
            || name.contains('\0')
            || name.starts_with("file:")
            || self.busy_timeout.as_millis() > i32::MAX as u128
            || !self.busy_timeout.subsec_nanos().is_multiple_of(1_000_000)
        {
            return Err(ProviderError::new(
                ErrorKind::Configuration,
                "invalid SQLite path or timeout",
            ));
        }
        let memory = self.mode == OpenMode::Memory || name == ":memory:";
        if memory
            && (self.mode != OpenMode::Memory
                || self.journal != JournalMode::Memory
                || limits.max_connections != 1)
        {
            return Err(ProviderError::new(
                ErrorKind::Configuration,
                "private memory requires Memory mode/journal and one connection",
            ));
        }
        let journal = match self.journal {
            JournalMode::Wal => SqliteJournalMode::Wal,
            JournalMode::Delete => SqliteJournalMode::Delete,
            JournalMode::Memory => SqliteJournalMode::Memory,
        };
        let synchronous = match self.synchronous {
            SynchronousMode::Normal => SqliteSynchronous::Normal,
            SynchronousMode::Full => SqliteSynchronous::Full,
        };
        Ok(SqliteConnectOptions::new()
            .filename(path)
            .in_memory(memory)
            .shared_cache(false)
            .read_only(self.mode == OpenMode::ReadOnly)
            .create_if_missing(self.mode == OpenMode::ReadWriteCreate)
            .foreign_keys(self.foreign_keys)
            .journal_mode(journal)
            .synchronous(synchronous)
            .busy_timeout(self.busy_timeout)
            .statement_cache_capacity(limits.statement_cache_capacity)
            .command_buffer_size(1)
            .row_buffer_size(1))
    }
}
