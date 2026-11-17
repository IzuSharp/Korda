// Generated from exact pinned nativeweave_server_api_v1.h; do not edit.
#![allow(dead_code)]
use crate::exports::*;
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
const _: unsafe extern "C" fn(u32, u32, *mut NwAbiInfoV1) -> u32 =
    crate::exports::nw_server_get_abi_v1;
const _: unsafe extern "C" fn(
    *const NwServerConfigV1,
    *const NwServerDispatchV1,
    *mut *mut NwServer,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_create;
const _: unsafe extern "C" fn(
    *mut NwServer,
    *const NwServerRouteV1,
    u32,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_routes_set;
const _: unsafe extern "C" fn(
    *mut NwServer,
    *mut *mut NwServerOperation,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_start_create;
const _: unsafe extern "C" fn(
    *mut NwServer,
    *mut *mut NwServerMetadata,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_bound_addresses;
const _: unsafe extern "C" fn(*mut NwServer, *mut NwServerSnapshotV1, *mut NwErrorInfoV1) -> u32 =
    crate::exports::nw_server_get_snapshot;
const _: unsafe extern "C" fn(
    *mut NwServer,
    u64,
    *mut *mut NwServerOperation,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_close_create;
const _: unsafe extern "C" fn(
    *mut NwServer,
    NwBytesViewV1,
    *const NwServerListenerV1,
    *mut *mut NwServerOperation,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_tls_reload_create;
const _: unsafe extern "C" fn(*mut NwServer) -> u32 = crate::exports::nw_server_try_retire;
const _: unsafe extern "C" fn(*mut NwServer) -> () = crate::exports::nw_server_release;
const _: unsafe extern "C" fn(
    *mut NwServerOperation,
    *const NwOperationCallbacksV1,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_operation_start;
const _: unsafe extern "C" fn(*mut NwServerOperation) -> u32 =
    crate::exports::nw_server_operation_request_cancel;
const _: unsafe extern "C" fn(*mut NwServerOperation, *mut NwErrorInfoV1) -> u32 =
    crate::exports::nw_server_operation_take_void;
const _: unsafe extern "C" fn(
    *mut NwServerOperation,
    *mut *mut NwServerWriteReservation,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_operation_take_write_reservation;
const _: unsafe extern "C" fn(
    *mut NwServerOperation,
    *mut NwServerCloseResultV1,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_operation_take_close;
const _: unsafe extern "C" fn(*mut NwServerOperation) -> u32 =
    crate::exports::nw_server_operation_try_retire;
const _: unsafe extern "C" fn(*mut NwServerOperation) -> () =
    crate::exports::nw_server_operation_release;
const _: unsafe extern "C" fn(
    *mut NwServer,
    u64,
    *mut *mut NwServerRequest,
    *mut *mut NwServerMetadata,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_request_take;
const _: unsafe extern "C" fn(
    *mut NwServerMetadata,
    *mut NwServerRequestInfoV1,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_metadata_read;
const _: unsafe extern "C" fn(*mut NwServerRequest, u32, *mut NwErrorInfoV1) -> u32 =
    crate::exports::nw_server_dispatch_complete;
const _: unsafe extern "C" fn(*mut NwServerRequest) -> u32 =
    crate::exports::nw_server_request_abort;
const _: unsafe extern "C" fn(*mut NwServerRequest) -> u32 =
    crate::exports::nw_server_request_try_retire;
const _: unsafe extern "C" fn(*mut NwServerMetadata) -> () =
    crate::exports::nw_server_metadata_release;
const _: unsafe extern "C" fn(*mut NwServerRequest) -> () =
    crate::exports::nw_server_request_release;
const _: unsafe extern "C" fn(
    *mut NwServerRequest,
    u64,
    *mut *mut NwServerManagedCredit,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_managed_credit_acquire;
const _: unsafe extern "C" fn(*mut NwServerManagedCredit) -> () =
    crate::exports::nw_server_managed_credit_release;
const _: unsafe extern "C" fn(
    *mut NwServerRequest,
    u32,
    u64,
    *mut *mut NwServerOperation,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_write_reserve_create;
const _: unsafe extern "C" fn(*mut NwServerWriteReservation) -> () =
    crate::exports::nw_server_write_reservation_release;
const _: unsafe extern "C" fn(
    *mut NwServerRequest,
    u32,
    *mut *mut NwServerOperation,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_body_read_create;
const _: unsafe extern "C" fn(
    *mut NwServerOperation,
    *mut u32,
    *mut *mut NwServerBuffer,
    *mut *mut NwServerMetadata,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_operation_take_chunk;
const _: unsafe extern "C" fn(*mut NwServerBuffer, *mut u64, *mut NwErrorInfoV1) -> u32 =
    crate::exports::nw_server_buffer_length;
const _: unsafe extern "C" fn(
    *mut NwServerBuffer,
    *mut u8,
    u64,
    *mut u64,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_buffer_copy;
const _: unsafe extern "C" fn(
    *mut NwServerBuffer,
    u64,
    *mut *mut NwServerManagedCredit,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_buffer_managed_credit_acquire;
const _: unsafe extern "C" fn(*mut NwServerBuffer) -> () = crate::exports::nw_server_buffer_release;
const _: unsafe extern "C" fn(
    *mut NwServerRequest,
    u32,
    NwBytesViewV1,
    *mut *mut NwServerOperation,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_response_begin_create;
const _: unsafe extern "C" fn(
    *mut NwServerRequest,
    *mut NwServerWriteReservation,
    NwBytesViewV1,
    *mut *mut NwServerOperation,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_response_write_create;
const _: unsafe extern "C" fn(
    *mut NwServerRequest,
    *mut *mut NwServerOperation,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_response_flush_create;
const _: unsafe extern "C" fn(
    *mut NwServerRequest,
    NwBytesViewV1,
    *mut *mut NwServerOperation,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_response_finish_create;
const _: unsafe extern "C" fn(
    *mut NwServerRequest,
    *mut NwServerWriteReservation,
    *const NwServerSseEventV1,
    *mut *mut NwServerOperation,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_sse_write_create;
const _: unsafe extern "C" fn(
    *mut NwServerRequest,
    *mut *mut NwServerOperation,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_grpc_read_create;
const _: unsafe extern "C" fn(
    *mut NwServerRequest,
    *mut NwServerWriteReservation,
    NwBytesViewV1,
    *mut *mut NwServerOperation,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_grpc_write_create;
const _: unsafe extern "C" fn(
    *mut NwServerRequest,
    NwBytesViewV1,
    *mut *mut NwServerOperation,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_grpc_headers_create;
const _: unsafe extern "C" fn(
    *mut NwServerRequest,
    u32,
    NwBytesViewV1,
    NwBytesViewV1,
    *mut *mut NwServerOperation,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_grpc_status_create;
const _: unsafe extern "C" fn(
    *mut NwServerRequest,
    u32,
    *mut *mut NwServerOperation,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_mcp_gate_resume_create;
const _: unsafe extern "C" fn(
    *mut NwServerRequest,
    *mut u32,
    *mut *mut NwServerBuffer,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_mcp_call_take;
const _: unsafe extern "C" fn(
    *mut NwServerRequest,
    *mut NwServerWriteReservation,
    u32,
    NwBytesViewV1,
    *mut *mut NwServerOperation,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_mcp_reply_create;
const _: unsafe extern "C" fn(
    *mut NwServerMetadata,
    *mut NwBytesViewV1,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_metadata_headers;
const _: unsafe extern "C" fn(
    *mut NwServerMetadata,
    *mut NwBytesViewV1,
    *mut NwErrorInfoV1,
) -> u32 = crate::exports::nw_server_metadata_grpc_headers;
include!("../generated/pinned_layout_assertions.rs");
