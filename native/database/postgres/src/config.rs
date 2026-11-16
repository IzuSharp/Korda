use crate::{ErrorKind, ProviderError, Result};
use sqlx::{
    ConnectOptions,
    postgres::{PgConnectOptions, PgSslMode},
};
use std::{path::PathBuf, str::FromStr, time::Duration};

/// Explicit policy. Only the named development profile permits plaintext.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum TlsMode {
    #[default]
    VerifyFull,
    DisableForLocalDevelopment,
}
/// Owned connect settings; neither this type nor its errors print credentials.
#[derive(Clone)]
pub struct ConnectConfig {
    pub connection_string: String,
    pub tls: TlsMode,
    pub root_certificate: Option<PathBuf>,
    pub application_name: String,
    pub connect_timeout: Duration,
    pub statement_cache_capacity: usize,
}
impl ConnectConfig {
    pub fn new(connection_string: String) -> Self {
        Self {
            connection_string,
            tls: TlsMode::VerifyFull,
            root_certificate: None,
            application_name: "NativeWeave".into(),
            connect_timeout: Duration::from_secs(15),
            statement_cache_capacity: 64,
        }
    }
    /// Parse only a TCP PostgreSQL URI. Query-string overrides are rejected rather
    /// than silently weakening typed TLS, timeout, or session settings.
    pub fn options(&self) -> Result<PgConnectOptions> {
        let invalid =
            || ProviderError::new(ErrorKind::Configuration, "invalid PostgreSQL configuration");
        if self.connection_string.len() > 8192
            || self.application_name.is_empty()
            || self.application_name.len() > 256
            || self.application_name.contains('\0')
            || self.connect_timeout.is_zero()
            || self.connect_timeout.as_secs() > 3600
            || self.statement_cache_capacity > 4096
        {
            return Err(invalid());
        }
        let mut uri = url::Url::parse(&self.connection_string).map_err(|_| invalid())?;
        if !matches!(uri.scheme(), "postgres" | "postgresql")
            || uri.host_str().is_none()
            || uri.query().is_some()
            || uri.fragment().is_some()
            || uri.username().is_empty()
            || uri.path().trim_matches('/').is_empty()
            || uri.host_str().is_some_and(|h| h.contains('%'))
        {
            return Err(invalid());
        }
        // SQLx public options inherit PG* variables and pgpass. Reject ambient
        // policy inputs rather than mutating process environment or reading
        // private fields; an explicit empty password disables pgpass fallback.
        if [
            "PGHOSTADDR",
            "PGHOST",
            "PGPORT",
            "PGUSER",
            "PGDATABASE",
            "PGPASSWORD",
            "PGPASSFILE",
            "PGSSLMODE",
            "PGSSLROOTCERT",
            "PGSSLCERT",
            "PGSSLKEY",
            "PGAPPNAME",
            "PGOPTIONS",
        ]
        .iter()
        .any(|name| std::env::var_os(name).is_some())
        {
            return Err(invalid());
        }
        if uri.password().is_none() {
            uri.set_password(Some("")).map_err(|_| invalid())?;
        }
        let mut options = PgConnectOptions::from_str(uri.as_str())
            .map_err(|_| invalid())?
            .application_name(&self.application_name)
            .statement_cache_capacity(self.statement_cache_capacity)
            .ssl_mode(match self.tls {
                TlsMode::VerifyFull => PgSslMode::VerifyFull,
                TlsMode::DisableForLocalDevelopment => PgSslMode::Disable,
            })
            .disable_statement_logging();
        if let Some(path) = &self.root_certificate {
            if !path.is_absolute() || !path.is_file() {
                return Err(invalid());
            }
            options = options.ssl_root_cert(path);
        }
        Ok(options)
    }
}
/// Finite per-session provider budgets. Pool/session/waiter limits belong to B04.
#[derive(Clone, Copy, Debug)]
pub struct SessionConfig {
    pub statement_timeout: Duration,
    pub cleanup_timeout: Duration,
    pub max_sql_bytes: usize,
}
impl Default for SessionConfig {
    fn default() -> Self {
        Self {
            statement_timeout: Duration::from_secs(30),
            cleanup_timeout: Duration::from_secs(5),
            max_sql_bytes: 1024 * 1024,
        }
    }
}
impl SessionConfig {
    pub(crate) fn validate(self) -> Result<()> {
        if self.statement_timeout.as_millis() == 0
            || self.statement_timeout.as_millis() > i32::MAX as u128
            || self.cleanup_timeout.is_zero()
            || self.cleanup_timeout.as_secs() > 3600
            || self.max_sql_bytes == 0
            || self.max_sql_bytes > nativeweave_data_contracts::MAX_BYTES
        {
            Err(ProviderError::new(
                ErrorKind::Configuration,
                "invalid provider session limits",
            ))
        } else {
            Ok(())
        }
    }
}
impl ConnectConfig {
    /// Consume the exact pinned kind8 record using B01's shared envelope parser.
    /// Connection credentials arrive separately in B04's outer source record.
    pub fn from_record(connection_string: String, bytes: &[u8]) -> Result<Self> {
        let invalid =
            || ProviderError::new(ErrorKind::Configuration, "invalid PostgreSQL option record");
        let count = bytes
            .get(8..12)
            .and_then(|b| <[u8; 4]>::try_from(b).ok())
            .map(u32::from_le_bytes)
            .ok_or_else(invalid)?;
        let expected: &[(u16, u16)] = match count {
            3 => &[(1, 1), (3, 4), (4, 3)],
            4 => &[(1, 1), (2, 4), (3, 4), (4, 3)],
            _ => return Err(invalid()),
        };
        let fields = nativeweave_data_contracts::wire::control_fields(bytes, 8, expected)
            .map_err(|_| invalid())?;
        let tls = match fields[0] {
            [0, 0, 0, 0] => TlsMode::VerifyFull,
            [1, 0, 0, 0] => TlsMode::DisableForLocalDevelopment,
            _ => return Err(invalid()),
        };
        let text = |bytes: &[u8], max: usize| -> Result<String> {
            if bytes.is_empty() || bytes.len() > max || bytes.contains(&0) {
                return Err(invalid());
            }
            std::str::from_utf8(bytes)
                .map(str::to_owned)
                .map_err(|_| invalid())
        };
        let offset = usize::from(count == 4);
        let root_certificate = if offset == 1 {
            Some(PathBuf::from(text(fields[1], 4096)?))
        } else {
            None
        };
        let application_name = text(fields[1 + offset], 256)?;
        let ns = u64::from_le_bytes(fields[2 + offset].try_into().map_err(|_| invalid())?);
        let config = Self {
            connection_string,
            tls,
            root_certificate,
            application_name,
            connect_timeout: Duration::from_nanos(ns),
            statement_cache_capacity: 64,
        };
        config.options()?;
        Ok(config)
    }
}
