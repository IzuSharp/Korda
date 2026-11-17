use crate::{Error, Result, control::Fields};
use nativeweave_data_contracts::{MAX_BYTES, MAX_NODES, Profile};
use std::time::Duration;

/// Immutable, bounded source settings in exact DatabaseLimits declaration order.
#[derive(Clone, Debug)]
pub struct Limits {
    pub connections: u32,
    pub waiters: usize,
    pub admission: Duration,
    pub buffered_rows: usize,
    pub buffered_bytes: usize,
    pub batch_rows: usize,
    pub batch_bytes: usize,
    pub field_bytes: usize,
    pub nodes: usize,
    pub depth: u32,
    pub parameters: usize,
    pub parameter_bytes: usize,
    pub prepared_plans: usize,
    pub prepared_bytes: usize,
    pub command: Duration,
}
impl Limits {
    pub fn parse(bytes: &[u8]) -> Result<Self> {
        let spec = (1..=15)
            .map(|id| (id, if matches!(id, 3 | 15) { 3 } else { 2 }, true))
            .collect::<Vec<_>>();
        let f = Fields::parse(bytes, 10, &spec)?;
        let checked = |id: u16, max: u64, zero: bool| -> Result<usize> {
            let n = f.u64(id)?;
            if n > max || n == 0 && !zero {
                return Err(Error::invalid("database limit outside native profile"));
            }
            usize::try_from(n).map_err(|_| Error::capacity())
        };
        let timeout = |id| -> Result<Duration> {
            let n = f.u64(id)?;
            let d = Duration::from_nanos(n);
            if n == 0 || d > Duration::from_secs(3600) {
                return Err(Error::invalid("database finite timeout"));
            }
            Ok(d)
        };
        Ok(Self {
            connections: checked(1, 1024, false)? as u32,
            waiters: checked(2, 4096, false)?,
            admission: timeout(3)?,
            buffered_rows: checked(4, 1_000_000, true)?,
            buffered_bytes: checked(5, 1 << 30, true)?,
            batch_rows: checked(6, MAX_NODES as u64, false)?,
            batch_bytes: checked(7, MAX_BYTES as u64, false)?,
            field_bytes: checked(8, 64 << 20, false)?,
            nodes: checked(9, MAX_NODES as u64, false)?,
            depth: checked(10, 256, false)? as u32,
            parameters: checked(11, MAX_NODES as u64, false)?,
            parameter_bytes: checked(12, MAX_BYTES as u64, false)?,
            prepared_plans: checked(13, MAX_NODES as u64, false)?,
            prepared_bytes: checked(14, 1 << 30, false)?,
            command: timeout(15)?,
        })
    }
    pub fn stream(&self, rows: usize, bytes: usize) -> nativeweave_data_streaming::Limits {
        nativeweave_data_streaming::Limits {
            max_rows: rows.min(self.batch_rows),
            max_batch_bytes: bytes.min(self.batch_bytes),
            max_field_bytes: self.field_bytes.min(MAX_BYTES),
        }
    }
    pub fn codec(&self) -> nativeweave_data_codecs::Limits {
        nativeweave_data_codecs::Limits {
            max_parameters: self.parameters,
            max_parameter_bytes: self.parameter_bytes,
            max_field_bytes: self.field_bytes.min(self.parameter_bytes),
        }
    }
    pub fn binding(&self) -> nativeweave_query_binding::Limits {
        nativeweave_query_binding::Limits {
            nodes: self.nodes,
            depth: self.depth,
            bytes: self.parameter_bytes,
            parameters: self.parameters,
        }
    }
    #[cfg(feature = "sqlite")]
    pub fn sqlite(&self) -> nativeweave_data_sqlite::Limits {
        nativeweave_data_sqlite::Limits {
            max_connections: self.connections,
            max_commands: self.connections as usize,
            max_input_bytes: self.parameter_bytes,
            max_sql_bytes: self.parameter_bytes.min(1024 * 1024),
            row_channel_capacity: 1,
            statement_cache_capacity: self.prepared_plans,
            acquire_timeout: self.admission,
            progress_operations: 1000,
        }
    }
}
pub enum ProviderConfig {
    #[cfg(feature = "postgres")]
    Postgres(nativeweave_data_postgres::ConnectConfig),
    #[cfg(feature = "sqlite")]
    Sqlite {
        path: String,
        options: nativeweave_data_sqlite::Options,
    },
}
pub struct Config {
    pub profile: Profile,
    pub provider: ProviderConfig,
    pub limits: Limits,
    pub name: String,
}
impl Config {
    pub fn parse(bytes: &[u8]) -> Result<Self> {
        let f = Fields::parse(
            bytes,
            1,
            &[
                (1, 1, true),
                (2, 4, true),
                (3, 5, true),
                (4, 5, true),
                (5, 4, false),
            ],
        )?;
        let limits = Limits::parse(f.bytes(3)?)?;
        let connection = f.text(2)?;
        if connection.is_empty() || connection.contains('\0') || connection.len() > 8192 {
            return Err(Error::invalid("connection identity"));
        }
        let name = f
            .get(5)
            .map(std::str::from_utf8)
            .transpose()
            .map_err(|_| Error::invalid("source name"))?
            .unwrap_or("default")
            .to_owned();
        if name.is_empty() || name.len() > 256 || name.contains('\0') {
            return Err(Error::invalid("source name"));
        }
        let (profile, provider) = match f.u32(1)? {
            #[cfg(feature = "postgres")]
            0 => {
                let p = Fields::parse(
                    f.bytes(4)?,
                    8,
                    &[(1, 1, true), (2, 4, false), (3, 4, true), (4, 3, true)],
                )?;
                let mut config =
                    nativeweave_data_postgres::ConnectConfig::new(connection.to_owned());
                config.tls = match p.u32(1)? {
                    0 => nativeweave_data_postgres::TlsMode::VerifyFull,
                    1 => nativeweave_data_postgres::TlsMode::DisableForLocalDevelopment,
                    _ => return Err(Error::invalid("TLS profile")),
                };
                config.root_certificate = p
                    .get(2)
                    .map(std::str::from_utf8)
                    .transpose()
                    .map_err(|_| Error::invalid("root path"))?
                    .map(Into::into);
                config.application_name = p.text(3)?.to_owned();
                config.connect_timeout = Duration::from_nanos(p.u64(4)?);
                config.statement_cache_capacity = limits.prepared_plans;
                config.options()?;
                (Profile::PostgreSql, ProviderConfig::Postgres(config))
            }
            #[cfg(feature = "sqlite")]
            1 => {
                let options = nativeweave_data_sqlite::Options::from_record(f.bytes(4)?)?;
                options.connect_options(std::path::Path::new(connection), limits.sqlite())?;
                (
                    Profile::Sqlite,
                    ProviderConfig::Sqlite {
                        path: connection.to_owned(),
                        options,
                    },
                )
            }
            _ => return Err(Error::unsupported()),
        };
        Ok(Self {
            profile,
            provider,
            limits,
            name,
        })
    }
}
