//! NativeWeave Server: explicit instance-local Tokio, Hyper, Axum and rustls ownership.
mod config;
mod engine;
mod exports;
mod metadata;
mod wire;
pub use exports::*;

use nativeweave_interop_abi::*;
use nativeweave_interop_errors::ErrorFields;
use nativeweave_server_streaming::StreamError;
use std::sync::{Mutex, MutexGuard};
type Result<T> = std::result::Result<T, ApiError>;
#[derive(Debug, Clone, Copy)]
struct ApiError {
    status: u32,
    code: i32,
}
impl ApiError {
    const fn status(status: u32) -> Self {
        Self { status, code: 0 }
    }
    const fn domain(code: i32) -> Self {
        Self {
            status: NW_DOMAIN_ERROR,
            code,
        }
    }
    fn fields(self) -> ErrorFields {
        ErrorFields {
            domain: 0x53525652,
            code: self.code,
            correlation_id: 0,
        }
    }
}
impl From<StreamError> for ApiError {
    fn from(error: StreamError) -> Self {
        match error {
            StreamError::InvalidArgument => invalid(),
            StreamError::ResourceLimit => Self::domain(1),
            StreamError::Canceled => Self::status(NW_CANCELED),
            StreamError::Closed => Self::status(NW_INVALID_STATE),
            StreamError::Transport => Self::domain(4),
        }
    }
}
fn invalid() -> ApiError {
    ApiError::status(NW_INVALID_ARGUMENT)
}
fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|p| p.into_inner())
}

#[cfg(test)]
mod tests;
