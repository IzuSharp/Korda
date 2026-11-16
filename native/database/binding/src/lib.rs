//! Direct query binding contracts and source/arena validation. B01 owns canonical
//! models, projections, values and relations; B11 owns SeaQuery state/rendering;
//! B04 owns runtime/session/operation scheduling. No new shared runtime is created.
mod clauses;
mod control;
#[rustfmt::skip]
pub mod signatures;
mod model;
pub use clauses::CteReference;
mod execute;
mod relation;
pub use control::projection_record;
pub use execute::ExecutionArguments;
pub use relation::*;
mod arena;
pub use arena::{ExpressionType, PlanReference, QueryArena, QueryState};
pub use model::*;
use nativeweave_data_contracts::ContractError;
use nativeweave_interop_abi::{
    NW_DOMAIN_ERROR, NW_ENGINE_POISONED, NW_INVALID_ARGUMENT, NW_INVALID_STATE, NW_NOT_SUPPORTED,
    NwStatus,
};

/// Bounded domain detail over the existing Core status inventory. No SQL or values.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct BindingError {
    pub status: NwStatus,
    pub detail: &'static str,
}
impl std::fmt::Display for BindingError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "binding status {}: {}", self.status, self.detail)
    }
}
impl std::error::Error for BindingError {}
impl From<ContractError> for BindingError {
    fn from(value: ContractError) -> Self {
        invalid(value.0)
    }
}
pub type Result<T> = std::result::Result<T, BindingError>;
pub(crate) fn invalid(detail: &'static str) -> BindingError {
    BindingError {
        status: NW_INVALID_ARGUMENT,
        detail,
    }
}
pub(crate) fn state(detail: &'static str) -> BindingError {
    BindingError {
        status: NW_INVALID_STATE,
        detail,
    }
}
pub(crate) fn unsupported(detail: &'static str) -> BindingError {
    BindingError {
        status: NW_NOT_SUPPORTED,
        detail,
    }
}
pub(crate) fn capacity(detail: &'static str) -> BindingError {
    BindingError {
        status: NW_DOMAIN_ERROR,
        detail,
    }
}
pub(crate) fn poisoned() -> BindingError {
    BindingError {
        status: NW_ENGINE_POISONED,
        detail: "query binding poisoned",
    }
}
