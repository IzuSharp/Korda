//! B19 native MCP protocol and bounded preflight; composed by the existing Server Host.
//! Ordinary tools are runtime descriptors, never per-tool Rust implementations.
pub mod preflight;
pub mod profile;
pub mod runtime;
// Host composition uses the exact pinned protocol types without introducing a
// second dependency selection or an application-visible managed protocol model.
pub use rmcp;
pub use serde_json;
pub use tokio_util;
