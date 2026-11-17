//! Owning database runtime and physical-session/operation lifetimes.
//!
//! Providers, transaction state and streaming remain the existing B02/B03/B05/B06
//! components. No source creates connections until explicit session admission.
pub mod abi;
pub mod config;
pub mod control;
pub mod error;
pub mod operation;
pub mod runtime;
pub mod session;
pub mod sql;

pub use error::{Error, Result};
