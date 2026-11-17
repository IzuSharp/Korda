//! Optional Tonic protocol leaf on the existing NativeWeave Server runtime.
//! No executor, listener, TLS implementation or callback ABI is introduced here.
mod call;
mod codec;
mod metadata;
mod policy;
mod service;
pub use call::{Call, Inbound, Prepared, Reservation};
pub use codec::MessageBuffer;
pub use metadata::{
    RequestMetadata, ResponseMetadata, project as project_metadata, retain_headers,
};
pub use policy::{Descriptor, MethodKind, admission_floor, deadline, validate_http};
pub use service::serve;
use std::sync::{Mutex, MutexGuard};
pub use tonic;
fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|p| p.into_inner())
}
fn quota() -> tonic::Status {
    tonic::Status::resource_exhausted("native payload quota")
}

#[cfg(test)]
mod tests;
