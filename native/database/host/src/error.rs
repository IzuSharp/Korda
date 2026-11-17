use nativeweave_interop_abi::*;
use std::sync::atomic::{AtomicU64, Ordering};

/// Domain failures retain only bounded classification and provider code.
#[derive(Clone, Debug)]
pub struct Error {
    pub status: u32,
    pub code: u32,
    pub message: &'static str,
    pub provider: Option<String>,
    pub correlation: u64,
}
static NEXT_ERROR: AtomicU64 = AtomicU64::new(1);
impl Error {
    pub fn new(status: u32, code: u32, message: &'static str) -> Self {
        Self {
            status,
            code,
            message,
            provider: None,
            correlation: NEXT_ERROR.fetch_add(1, Ordering::Relaxed),
        }
    }
    pub fn invalid(message: &'static str) -> Self {
        Self::new(NW_INVALID_ARGUMENT, 0, message)
    }
    pub fn state(message: &'static str) -> Self {
        Self::new(NW_INVALID_STATE, 8, message)
    }
    pub fn closed() -> Self {
        Self::new(NW_INVALID_STATE, 8, "database admission is closed")
    }
    pub fn capacity() -> Self {
        Self::new(NW_DOMAIN_ERROR, 6, "finite database capacity exhausted")
    }
    pub fn canceled() -> Self {
        Self::new(NW_CANCELED, 8, "database operation canceled")
    }
    pub fn unsupported() -> Self {
        Self::new(NW_NOT_SUPPORTED, 5, "database capability unavailable")
    }
    pub fn fields(&self) -> nativeweave_interop_errors::ErrorFields {
        nativeweave_interop_errors::ErrorFields {
            domain: 0x4442,
            code: self.code as i32,
            correlation_id: self.correlation,
        }
    }
}
impl std::fmt::Display for Error {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{} [{}]", self.message, self.correlation)
    }
}
impl std::error::Error for Error {}
pub type Result<T> = std::result::Result<T, Error>;
impl From<nativeweave_data_contracts::ContractError> for Error {
    fn from(e: nativeweave_data_contracts::ContractError) -> Self {
        Self::invalid(e.0)
    }
}
impl From<nativeweave_query_binding::BindingError> for Error {
    fn from(e: nativeweave_query_binding::BindingError) -> Self {
        Self::new(e.status, 11, e.detail)
    }
}
impl From<sqlx::Error> for Error {
    fn from(e: sqlx::Error) -> Self {
        let mut value = Self::new(NW_DOMAIN_ERROR, 1, "native database operation failed");
        value.provider = e
            .as_database_error()
            .and_then(|e| e.code())
            .map(|c| c.into_owned())
            .filter(|c| c.len() <= 16 && c.bytes().all(|b| b.is_ascii_alphanumeric()));
        value
    }
}
impl From<nativeweave_data_codecs::CodecError> for Error {
    fn from(_: nativeweave_data_codecs::CodecError) -> Self {
        Self::new(NW_DOMAIN_ERROR, 3, "database scalar conversion failed")
    }
}
impl From<nativeweave_data_streaming::StreamError> for Error {
    fn from(e: nativeweave_data_streaming::StreamError) -> Self {
        use nativeweave_data_streaming::ErrorKind as K;
        let code = match e.kind {
            K::Shape => 4,
            K::Conversion => 3,
            K::Capability => 5,
            K::Capacity => 6,
            K::LargeValueRequiresStreaming => 14,
            K::InvalidState => 8,
            _ => 1,
        };
        Self::new(NW_DOMAIN_ERROR, code, e.message)
    }
}
#[cfg(feature = "postgres")]
impl From<nativeweave_data_postgres::ProviderError> for Error {
    fn from(e: nativeweave_data_postgres::ProviderError) -> Self {
        use nativeweave_data_postgres::ErrorKind as K;
        let code = match e.kind {
            K::Codec => 3,
            K::Shape => 4,
            K::Busy => 7,
            K::Closed => 8,
            K::Configuration => 8,
            _ => {
                if e.sqlstate.as_ref().is_some_and(|s| s.starts_with("23")) {
                    2
                } else {
                    1
                }
            }
        };
        let mut v = Self::new(
            if e.kind == K::Canceled {
                NW_CANCELED
            } else {
                NW_DOMAIN_ERROR
            },
            code,
            e.message,
        );
        v.provider = e.sqlstate;
        v.correlation = e.correlation_id;
        v
    }
}
#[cfg(feature = "sqlite")]
impl From<nativeweave_data_sqlite::ProviderError> for Error {
    fn from(e: nativeweave_data_sqlite::ProviderError) -> Self {
        use nativeweave_data_sqlite::ErrorKind as K;
        let code = match e.kind {
            K::Capacity => 6,
            K::Codec => 3,
            K::Closed | K::Configuration | K::Protocol => 8,
            _ => {
                if e.provider_code
                    .as_ref()
                    .and_then(|s| s.parse::<u32>().ok())
                    .is_some_and(|c| c & 255 == 19)
                {
                    2
                } else {
                    1
                }
            }
        };
        let mut value = Self::new(
            if matches!(e.kind, K::CanceledBeforeDispatch | K::Interrupted) {
                NW_CANCELED
            } else {
                NW_DOMAIN_ERROR
            },
            code,
            e.message,
        );
        value.provider = e.provider_code;
        value
    }
}
impl From<nativeweave_data_transactions::Error> for Error {
    fn from(e: nativeweave_data_transactions::Error) -> Self {
        use nativeweave_data_transactions::Error as T;
        match e {
            T::CanceledBeforeDispatch => Self::canceled(),
            T::UnsupportedCapability => Self::unsupported(),
            T::InvalidOptions => Self::invalid("invalid transaction options"),
            T::CommitUnknown {
                provider_code,
                correlation_id,
            } => Self {
                status: NW_DOMAIN_ERROR,
                code: 9,
                message: "commit outcome is unknown",
                provider: provider_code,
                correlation: correlation_id,
            },
            T::Provider {
                provider_code,
                correlation_id,
            }
            | T::Cleanup {
                provider_code,
                correlation_id,
            } => Self {
                status: NW_DOMAIN_ERROR,
                code: 1,
                message: "transaction operation failed",
                provider: provider_code,
                correlation: correlation_id,
            },
            _ => Self::state("transaction admission rejected"),
        }
    }
}
