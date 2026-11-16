//! Owned values; no pool, transport or dynamic runtime is started by these types.

use nativeweave_data_contracts::{
    ConnectionDisposition, LocalLifetime, Profile, TransactionOutcome,
};
use std::sync::{
    Arc,
    atomic::{AtomicBool, Ordering},
};
use std::time::Duration;

/// Transaction admission, independent from durable outcome and local safety.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Lifecycle {
    Active,
    Failed,
    CommitInFlight,
    RollbackInFlight,
    Closed,
}
/// Result of the separate cleanup path.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CleanupStatus {
    NotStarted,
    Pending,
    Succeeded,
    RollbackUnconfirmed,
    Failed,
}
/// Exact requested database isolation. Unsupported values are rejected before BEGIN.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Isolation {
    ReadUncommitted,
    ReadCommitted,
    RepeatableRead,
    Serializable,
    Snapshot,
}
/// Provider-advertised capabilities; SQLite BEGIN mode remains a provider setting.
#[derive(Debug, Clone, Copy)]
pub struct Capabilities {
    pub profile: Profile,
    pub read_only: bool,
}
/// Recovery metadata survives native retirement; it never triggers business replay.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RecoveryIdentity {
    pub key: String,
    pub scope: Option<String>,
    pub request_hash: Option<[u8; 32]>,
}
/// Independent finite operation deadlines and exact requested transaction options.
#[derive(Debug, Clone)]
pub struct Options {
    pub isolation: Option<Isolation>,
    pub recovery: Option<RecoveryIdentity>,
    pub commit_timeout: Duration,
    pub cleanup_timeout: Duration,
    pub read_only: bool,
}
impl Default for Options {
    fn default() -> Self {
        Self {
            isolation: None,
            recovery: None,
            commit_timeout: Duration::from_secs(30),
            cleanup_timeout: Duration::from_secs(5),
            read_only: false,
        }
    }
}
impl Options {
    /// Validates without invoking the provider or publishing an owner.
    pub fn validate(&self, caps: Capabilities) -> Result<(), Error> {
        if std::time::Instant::now()
            .checked_add(self.commit_timeout)
            .is_none()
            || std::time::Instant::now()
                .checked_add(self.cleanup_timeout)
                .is_none()
        {
            return Err(Error::InvalidOptions);
        }
        if self.read_only && !caps.read_only {
            return Err(Error::UnsupportedCapability);
        }
        if let Some(isolation) = self.isolation {
            let supported = match caps.profile {
                Profile::PostgreSql => matches!(
                    isolation,
                    Isolation::ReadCommitted | Isolation::RepeatableRead | Isolation::Serializable
                ),
                Profile::Sqlite => isolation == Isolation::Serializable,
            };
            if !supported {
                return Err(Error::UnsupportedCapability);
            }
        }
        if self.recovery.as_ref().is_some_and(|r| {
            r.key.len() > nativeweave_data_contracts::MAX_BYTES
                || r.scope
                    .as_ref()
                    .is_some_and(|s| s.len() > nativeweave_data_contracts::MAX_BYTES)
        }) {
            return Err(Error::InvalidOptions);
        }
        Ok(())
    }
}
/// A one-generation admission signal. Cancellation never reaches an admitted driver call.
#[derive(Debug, Clone, Default)]
pub struct AdmissionToken(Arc<AtomicBool>);
impl AdmissionToken {
    pub fn cancel(&self) {
        self.0.store(true, Ordering::Release);
    }
    pub fn is_canceled(&self) -> bool {
        self.0.load(Ordering::Acquire)
    }
}
/// An opaque transaction-generation-bound stack entry, never a caller SQL identifier.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SavepointToken {
    pub(crate) generation: u64,
    pub(crate) sequence: u64,
}
impl SavepointToken {
    pub fn generation(self) -> u64 {
        self.generation
    }
    pub fn sequence(self) -> u64 {
        self.sequence
    }
}
/// Error categories retain business and cleanup failures separately.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Error {
    InvalidState,
    InvalidOptions,
    UnsupportedCapability,
    CanceledBeforeDispatch,
    CommitUnknown {
        provider_code: Option<String>,
        correlation_id: u64,
    },
    Provider {
        provider_code: Option<String>,
        correlation_id: u64,
    },
    Cleanup {
        provider_code: Option<String>,
        correlation_id: u64,
    },
    SavepointOrder,
    GenerationExhausted,
}
impl std::fmt::Display for Error {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{self:?}")
    }
}
impl std::error::Error for Error {}
/// Local obligations counted until observation of provider retirement.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Resources {
    pub physical_lease: u32,
    pub driver_operations: u32,
    pub cleanup_owners: u32,
}
/// Immutable observation; outcome and cleanup survive disposal of native resources.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Snapshot {
    pub state: Lifecycle,
    pub outcome: TransactionOutcome,
    pub disposition: ConnectionDisposition,
    /// B01 Open/Closing/NativeRetired/Released maps to D15 Live/Closing/Quiescent/Reclaimed.
    pub lifetime: LocalLifetime,
    pub commit_attempted: bool,
    pub cleanup: CleanupStatus,
    pub correlation_id: u64,
    pub provider_code: Option<String>,
    pub deadline_exceeded: bool,
    pub local_resources_released: bool,
    pub generation: u64,
    pub session_id: u64,
    pub resources: Resources,
}
