// Generated from exact pinned nativeweave_server_api_v1.h; do not edit.
#![allow(dead_code)]
use nativeweave_interop_abi::*;
#[repr(C)]
#[derive(Clone, Copy)]
pub struct NwServerDispatchV1 {
    pub struct_size: u32,
    pub version: u32,
    pub cookie: usize,
    pub on_request: usize,
    pub on_control: usize,
}
#[repr(C)]
#[derive(Clone, Copy)]
pub struct NwServerListenerV1 {
    pub struct_size: u32,
    pub version: u32,
    pub address_family: u32,
    pub port: u32,
    pub address: [u8; 16],
    pub protocols: u32,
    pub flags: u32,
    pub name: NwBytesViewV1,
    pub certificate_chain_file: NwBytesViewV1,
    pub private_key_file: NwBytesViewV1,
    pub client_ca_file: NwBytesViewV1,
    pub handshake_timeout_ns: u64,
    pub max_handshakes: u32,
    pub reserved: u32,
}
#[repr(C)]
#[derive(Clone, Copy)]
pub struct NwServerConfigV1 {
    pub struct_size: u32,
    pub version: u32,
    pub request_timeout_ns: u64,
    pub shutdown_timeout_ns: u64,
    pub header_timeout_ns: u64,
    pub keepalive_ns: u64,
    pub max_request_bytes: u64,
    pub managed_payload_bytes: u64,
    pub native_payload_bytes: u64,
    pub request_retained_bytes: u64,
    pub max_json_bytes: u32,
    pub max_header_bytes: u32,
    pub max_chunk_bytes: u32,
    pub max_connections: u32,
    pub max_h2_streams: u32,
    pub active_requests: u32,
    pub queued_requests: u32,
    pub listener_count: u32,
    pub listeners: *const NwServerListenerV1,
    pub trusted_proxies: NwBytesViewV1,
    pub forward_limit: u32,
    pub reserved: u32,
}
#[repr(C)]
#[derive(Clone, Copy)]
pub struct NwServerRouteV1 {
    pub struct_size: u32,
    pub version: u32,
    pub handler_id: u64,
    pub pattern: NwBytesViewV1,
    pub methods: NwBytesViewV1,
    pub endpoint_name: NwBytesViewV1,
    pub kind: u32,
    pub flags: u32,
    pub timeout_ns: u64,
    pub max_request_bytes: u64,
    pub max_json_bytes: u32,
    pub reserved: u32,
    pub protocol_descriptor: NwBytesViewV1,
}
#[repr(C)]
#[derive(Clone, Copy)]
pub struct NwServerRequestInfoV1 {
    pub struct_size: u32,
    pub version: u32,
    pub request_id: u64,
    pub handler_id: u64,
    pub remaining_timeout_ns: u64,
    pub kind: u32,
    pub flags: u32,
    pub content_length: u64,
    pub method: NwBytesViewV1,
    pub scheme: NwBytesViewV1,
    pub authority: NwBytesViewV1,
    pub path: NwBytesViewV1,
    pub raw_query: NwBytesViewV1,
    pub headers: NwBytesViewV1,
    pub route_values: NwBytesViewV1,
    pub query_values: NwBytesViewV1,
    pub trace_context: NwBytesViewV1,
    pub trace_state: NwBytesViewV1,
    pub protocol: u32,
    pub peer_family: u32,
    pub peer_address: [u8; 16],
    pub peer_port: u32,
    pub cancellation_status: u32,
}
#[repr(C)]
#[derive(Clone, Copy)]
pub struct NwServerSnapshotV1 {
    pub struct_size: u32,
    pub version: u32,
    pub state: u32,
    pub reserved: u32,
    pub active_requests: u64,
    pub queued_requests: u64,
    pub retiring_requests: u64,
    pub retained_managed_bytes: u64,
    pub retained_native_bytes: u64,
    pub rejected_requests: u64,
    pub callback_failures: u64,
    pub diagnostic_drops: u64,
}
#[repr(C)]
#[derive(Clone, Copy)]
pub struct NwServerCloseResultV1 {
    pub struct_size: u32,
    pub version: u32,
    pub close_status: u32,
    pub pending_requests: u32,
    pub retained_managed_bytes: u64,
    pub retained_native_bytes: u64,
}
#[repr(C)]
#[derive(Clone, Copy)]
pub struct NwServerSseEventV1 {
    pub struct_size: u32,
    pub version: u32,
    pub present_flags: u32,
    pub reserved: u32,
    pub data: NwBytesViewV1,
    pub event_name: NwBytesViewV1,
    pub id: NwBytesViewV1,
    pub retry_milliseconds: u64,
}
