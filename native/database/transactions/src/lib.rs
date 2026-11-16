//! SQLx-provider transaction coordination with separately observed effects and safety.
//!
//! The provider owns the actual physical-session lease. Its futures always run on
//! the engine's Tokio executor, including after a caller stops waiting. The
//! coordinator never retries COMMIT, drops an in-flight driver future on deadline,
//! or treats queued closure as observed retirement. No Rust owner crosses a DLL.

mod coordinator;
mod driver;
mod model;

pub use coordinator::{CommandLease, Transaction};
pub use driver::{Confirmation, ProtocolState, ProviderFailure, TransactionDriver};
pub use model::*;
