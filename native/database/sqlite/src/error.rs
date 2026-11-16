use nativeweave_data_transactions::{ProtocolState, ProviderFailure};

/// Stable native classification, independent of SQLite's extended result number.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ErrorKind {
    Configuration,
    Capacity,
    CanceledBeforeDispatch,
    Interrupted,
    Busy,
    Database,
    Codec,
    Closed,
    Protocol,
}

/// Owned bounded diagnostic; SQL text, file paths and parameter values are omitted.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ProviderError {
    pub kind: ErrorKind,
    pub provider_code: Option<String>,
    pub message: &'static str,
}

pub type Result<T> = std::result::Result<T, ProviderError>;

impl ProviderError {
    pub(crate) fn new(kind: ErrorKind, message: &'static str) -> Self {
        Self {
            kind,
            provider_code: None,
            message,
        }
    }
    /// Converts to the transaction coordinator's bounded diagnostic contract.
    pub fn transaction_failure(&self, correlation_id: u64) -> ProviderFailure {
        ProviderFailure {
            code: self.provider_code.clone(),
            correlation_id,
        }
    }
    /// Errors do not independently establish reusable protocol state.
    pub fn protocol(&self) -> ProtocolState {
        ProtocolState::Untrusted
    }
}
impl std::fmt::Display for ProviderError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{:?}: {}", self.kind, self.message)
    }
}
impl std::error::Error for ProviderError {}
impl From<sqlx::Error> for ProviderError {
    fn from(error: sqlx::Error) -> Self {
        let code = error
            .as_database_error()
            .and_then(|e| e.code())
            .map(|s| s.chars().take(64).collect::<String>());
        let kind = match code
            .as_deref()
            .and_then(|s| s.parse::<i32>().ok())
            .map(|n| n & 0xff)
        {
            Some(9) => ErrorKind::Interrupted,
            Some(5 | 6) => ErrorKind::Busy,
            _ => match error {
                sqlx::Error::PoolClosed | sqlx::Error::WorkerCrashed => ErrorKind::Closed,
                sqlx::Error::PoolTimedOut => ErrorKind::Capacity,
                _ => ErrorKind::Database,
            },
        };
        Self {
            kind,
            provider_code: code,
            message: "SQLite operation failed",
        }
    }
}
impl From<nativeweave_data_codecs::CodecError> for ProviderError {
    fn from(_: nativeweave_data_codecs::CodecError) -> Self {
        Self::new(ErrorKind::Codec, "SQLite parameter conversion failed")
    }
}
