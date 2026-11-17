// Generated from exact pinned nativeweave_server_api_v1.h; do not edit.
using System.Runtime.InteropServices;
using NativeWeave.Interop.Abi;
namespace NativeWeave.Server.Native;
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NwServerDispatchV1
{
    internal uint struct_size;
    internal uint version;
    internal nuint cookie;
    internal nint on_request;
    internal nint on_control;
}
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NwServerListenerV1
{
    internal uint struct_size;
    internal uint version;
    internal uint address_family;
    internal uint port;
    internal fixed byte address[16];
    internal uint protocols;
    internal uint flags;
    internal NwBytesViewV1 name;
    internal NwBytesViewV1 certificate_chain_file;
    internal NwBytesViewV1 private_key_file;
    internal NwBytesViewV1 client_ca_file;
    internal ulong handshake_timeout_ns;
    internal uint max_handshakes;
    internal uint reserved;
}
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NwServerConfigV1
{
    internal uint struct_size;
    internal uint version;
    internal ulong request_timeout_ns;
    internal ulong shutdown_timeout_ns;
    internal ulong header_timeout_ns;
    internal ulong keepalive_ns;
    internal ulong max_request_bytes;
    internal ulong managed_payload_bytes;
    internal ulong native_payload_bytes;
    internal ulong request_retained_bytes;
    internal uint max_json_bytes;
    internal uint max_header_bytes;
    internal uint max_chunk_bytes;
    internal uint max_connections;
    internal uint max_h2_streams;
    internal uint active_requests;
    internal uint queued_requests;
    internal uint listener_count;
    internal NwServerListenerV1* listeners;
    internal NwBytesViewV1 trusted_proxies;
    internal uint forward_limit;
    internal uint reserved;
}
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NwServerRouteV1
{
    internal uint struct_size;
    internal uint version;
    internal ulong handler_id;
    internal NwBytesViewV1 pattern;
    internal NwBytesViewV1 methods;
    internal NwBytesViewV1 endpoint_name;
    internal uint kind;
    internal uint flags;
    internal ulong timeout_ns;
    internal ulong max_request_bytes;
    internal uint max_json_bytes;
    internal uint reserved;
    internal NwBytesViewV1 protocol_descriptor;
}
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NwServerRequestInfoV1
{
    internal uint struct_size;
    internal uint version;
    internal ulong request_id;
    internal ulong handler_id;
    internal ulong remaining_timeout_ns;
    internal uint kind;
    internal uint flags;
    internal ulong content_length;
    internal NwBytesViewV1 method;
    internal NwBytesViewV1 scheme;
    internal NwBytesViewV1 authority;
    internal NwBytesViewV1 path;
    internal NwBytesViewV1 raw_query;
    internal NwBytesViewV1 headers;
    internal NwBytesViewV1 route_values;
    internal NwBytesViewV1 query_values;
    internal NwBytesViewV1 trace_context;
    internal NwBytesViewV1 trace_state;
    internal uint protocol;
    internal uint peer_family;
    internal fixed byte peer_address[16];
    internal uint peer_port;
    internal uint cancellation_status;
}
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NwServerSnapshotV1
{
    internal uint struct_size;
    internal uint version;
    internal uint state;
    internal uint reserved;
    internal ulong active_requests;
    internal ulong queued_requests;
    internal ulong retiring_requests;
    internal ulong retained_managed_bytes;
    internal ulong retained_native_bytes;
    internal ulong rejected_requests;
    internal ulong callback_failures;
    internal ulong diagnostic_drops;
}
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NwServerCloseResultV1
{
    internal uint struct_size;
    internal uint version;
    internal uint close_status;
    internal uint pending_requests;
    internal ulong retained_managed_bytes;
    internal ulong retained_native_bytes;
}
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NwServerSseEventV1
{
    internal uint struct_size;
    internal uint version;
    internal uint present_flags;
    internal uint reserved;
    internal NwBytesViewV1 data;
    internal NwBytesViewV1 event_name;
    internal NwBytesViewV1 id;
    internal ulong retry_milliseconds;
}
internal sealed unsafe class ServerNativeCalls
{
    internal readonly delegate* unmanaged[Cdecl]<uint, uint, NwAbiInfoV1*, uint> nw_server_get_abi_v1;
    internal readonly delegate* unmanaged[Cdecl]<NwServerConfigV1*, NwServerDispatchV1*, nint*, NwErrorInfoV1*, uint> nw_server_create;
    internal readonly delegate* unmanaged[Cdecl]<nint, NwServerRouteV1*, uint, NwErrorInfoV1*, uint> nw_server_routes_set;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint> nw_server_start_create;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint> nw_server_bound_addresses;
    internal readonly delegate* unmanaged[Cdecl]<nint, NwServerSnapshotV1*, NwErrorInfoV1*, uint> nw_server_get_snapshot;
    internal readonly delegate* unmanaged[Cdecl]<nint, ulong, nint*, NwErrorInfoV1*, uint> nw_server_close_create;
    internal readonly delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, NwServerListenerV1*, nint*, NwErrorInfoV1*, uint> nw_server_tls_reload_create;
    internal readonly delegate* unmanaged[Cdecl]<nint, uint> nw_server_try_retire;
    internal readonly delegate* unmanaged[Cdecl]<nint, void> nw_server_release;
    internal readonly delegate* unmanaged[Cdecl]<nint, NwOperationCallbacksV1*, NwErrorInfoV1*, uint> nw_server_operation_start;
    internal readonly delegate* unmanaged[Cdecl]<nint, uint> nw_server_operation_request_cancel;
    internal readonly delegate* unmanaged[Cdecl]<nint, NwErrorInfoV1*, uint> nw_server_operation_take_void;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint> nw_server_operation_take_write_reservation;
    internal readonly delegate* unmanaged[Cdecl]<nint, NwServerCloseResultV1*, NwErrorInfoV1*, uint> nw_server_operation_take_close;
    internal readonly delegate* unmanaged[Cdecl]<nint, uint> nw_server_operation_try_retire;
    internal readonly delegate* unmanaged[Cdecl]<nint, void> nw_server_operation_release;
    internal readonly delegate* unmanaged[Cdecl]<nint, ulong, nint*, nint*, NwErrorInfoV1*, uint> nw_server_request_take;
    internal readonly delegate* unmanaged[Cdecl]<nint, NwServerRequestInfoV1*, NwErrorInfoV1*, uint> nw_server_metadata_read;
    internal readonly delegate* unmanaged[Cdecl]<nint, uint, NwErrorInfoV1*, uint> nw_server_dispatch_complete;
    internal readonly delegate* unmanaged[Cdecl]<nint, uint> nw_server_request_abort;
    internal readonly delegate* unmanaged[Cdecl]<nint, uint> nw_server_request_try_retire;
    internal readonly delegate* unmanaged[Cdecl]<nint, void> nw_server_metadata_release;
    internal readonly delegate* unmanaged[Cdecl]<nint, void> nw_server_request_release;
    internal readonly delegate* unmanaged[Cdecl]<nint, ulong, nint*, NwErrorInfoV1*, uint> nw_server_managed_credit_acquire;
    internal readonly delegate* unmanaged[Cdecl]<nint, void> nw_server_managed_credit_release;
    internal readonly delegate* unmanaged[Cdecl]<nint, uint, ulong, nint*, NwErrorInfoV1*, uint> nw_server_write_reserve_create;
    internal readonly delegate* unmanaged[Cdecl]<nint, void> nw_server_write_reservation_release;
    internal readonly delegate* unmanaged[Cdecl]<nint, uint, nint*, NwErrorInfoV1*, uint> nw_server_body_read_create;
    internal readonly delegate* unmanaged[Cdecl]<nint, uint*, nint*, nint*, NwErrorInfoV1*, uint> nw_server_operation_take_chunk;
    internal readonly delegate* unmanaged[Cdecl]<nint, ulong*, NwErrorInfoV1*, uint> nw_server_buffer_length;
    internal readonly delegate* unmanaged[Cdecl]<nint, byte*, ulong, ulong*, NwErrorInfoV1*, uint> nw_server_buffer_copy;
    internal readonly delegate* unmanaged[Cdecl]<nint, ulong, nint*, NwErrorInfoV1*, uint> nw_server_buffer_managed_credit_acquire;
    internal readonly delegate* unmanaged[Cdecl]<nint, void> nw_server_buffer_release;
    internal readonly delegate* unmanaged[Cdecl]<nint, uint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint> nw_server_response_begin_create;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint> nw_server_response_write_create;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint> nw_server_response_flush_create;
    internal readonly delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint> nw_server_response_finish_create;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, NwServerSseEventV1*, nint*, NwErrorInfoV1*, uint> nw_server_sse_write_create;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint> nw_server_grpc_read_create;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint> nw_server_grpc_write_create;
    internal readonly delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint> nw_server_grpc_headers_create;
    internal readonly delegate* unmanaged[Cdecl]<nint, uint, NwBytesViewV1, NwBytesViewV1, nint*, NwErrorInfoV1*, uint> nw_server_grpc_status_create;
    internal readonly delegate* unmanaged[Cdecl]<nint, uint, nint*, NwErrorInfoV1*, uint> nw_server_mcp_gate_resume_create;
    internal readonly delegate* unmanaged[Cdecl]<nint, uint*, nint*, NwErrorInfoV1*, uint> nw_server_mcp_call_take;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, uint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint> nw_server_mcp_reply_create;
    internal readonly delegate* unmanaged[Cdecl]<nint, NwBytesViewV1*, NwErrorInfoV1*, uint> nw_server_metadata_headers;
    internal readonly delegate* unmanaged[Cdecl]<nint, NwBytesViewV1*, NwErrorInfoV1*, uint> nw_server_metadata_grpc_headers;
    internal ServerNativeCalls(Func<string,nint> resolve)
    {
        nw_server_get_abi_v1 = (delegate* unmanaged[Cdecl]<uint, uint, NwAbiInfoV1*, uint>)resolve("nw_server_get_abi_v1");
        nw_server_create = (delegate* unmanaged[Cdecl]<NwServerConfigV1*, NwServerDispatchV1*, nint*, NwErrorInfoV1*, uint>)resolve("nw_server_create");
        nw_server_routes_set = (delegate* unmanaged[Cdecl]<nint, NwServerRouteV1*, uint, NwErrorInfoV1*, uint>)resolve("nw_server_routes_set");
        nw_server_start_create = (delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint>)resolve("nw_server_start_create");
        nw_server_bound_addresses = (delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint>)resolve("nw_server_bound_addresses");
        nw_server_get_snapshot = (delegate* unmanaged[Cdecl]<nint, NwServerSnapshotV1*, NwErrorInfoV1*, uint>)resolve("nw_server_get_snapshot");
        nw_server_close_create = (delegate* unmanaged[Cdecl]<nint, ulong, nint*, NwErrorInfoV1*, uint>)resolve("nw_server_close_create");
        nw_server_tls_reload_create = (delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, NwServerListenerV1*, nint*, NwErrorInfoV1*, uint>)resolve("nw_server_tls_reload_create");
        nw_server_try_retire = (delegate* unmanaged[Cdecl]<nint, uint>)resolve("nw_server_try_retire");
        nw_server_release = (delegate* unmanaged[Cdecl]<nint, void>)resolve("nw_server_release");
        nw_server_operation_start = (delegate* unmanaged[Cdecl]<nint, NwOperationCallbacksV1*, NwErrorInfoV1*, uint>)resolve("nw_server_operation_start");
        nw_server_operation_request_cancel = (delegate* unmanaged[Cdecl]<nint, uint>)resolve("nw_server_operation_request_cancel");
        nw_server_operation_take_void = (delegate* unmanaged[Cdecl]<nint, NwErrorInfoV1*, uint>)resolve("nw_server_operation_take_void");
        nw_server_operation_take_write_reservation = (delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint>)resolve("nw_server_operation_take_write_reservation");
        nw_server_operation_take_close = (delegate* unmanaged[Cdecl]<nint, NwServerCloseResultV1*, NwErrorInfoV1*, uint>)resolve("nw_server_operation_take_close");
        nw_server_operation_try_retire = (delegate* unmanaged[Cdecl]<nint, uint>)resolve("nw_server_operation_try_retire");
        nw_server_operation_release = (delegate* unmanaged[Cdecl]<nint, void>)resolve("nw_server_operation_release");
        nw_server_request_take = (delegate* unmanaged[Cdecl]<nint, ulong, nint*, nint*, NwErrorInfoV1*, uint>)resolve("nw_server_request_take");
        nw_server_metadata_read = (delegate* unmanaged[Cdecl]<nint, NwServerRequestInfoV1*, NwErrorInfoV1*, uint>)resolve("nw_server_metadata_read");
        nw_server_dispatch_complete = (delegate* unmanaged[Cdecl]<nint, uint, NwErrorInfoV1*, uint>)resolve("nw_server_dispatch_complete");
        nw_server_request_abort = (delegate* unmanaged[Cdecl]<nint, uint>)resolve("nw_server_request_abort");
        nw_server_request_try_retire = (delegate* unmanaged[Cdecl]<nint, uint>)resolve("nw_server_request_try_retire");
        nw_server_metadata_release = (delegate* unmanaged[Cdecl]<nint, void>)resolve("nw_server_metadata_release");
        nw_server_request_release = (delegate* unmanaged[Cdecl]<nint, void>)resolve("nw_server_request_release");
        nw_server_managed_credit_acquire = (delegate* unmanaged[Cdecl]<nint, ulong, nint*, NwErrorInfoV1*, uint>)resolve("nw_server_managed_credit_acquire");
        nw_server_managed_credit_release = (delegate* unmanaged[Cdecl]<nint, void>)resolve("nw_server_managed_credit_release");
        nw_server_write_reserve_create = (delegate* unmanaged[Cdecl]<nint, uint, ulong, nint*, NwErrorInfoV1*, uint>)resolve("nw_server_write_reserve_create");
        nw_server_write_reservation_release = (delegate* unmanaged[Cdecl]<nint, void>)resolve("nw_server_write_reservation_release");
        nw_server_body_read_create = (delegate* unmanaged[Cdecl]<nint, uint, nint*, NwErrorInfoV1*, uint>)resolve("nw_server_body_read_create");
        nw_server_operation_take_chunk = (delegate* unmanaged[Cdecl]<nint, uint*, nint*, nint*, NwErrorInfoV1*, uint>)resolve("nw_server_operation_take_chunk");
        nw_server_buffer_length = (delegate* unmanaged[Cdecl]<nint, ulong*, NwErrorInfoV1*, uint>)resolve("nw_server_buffer_length");
        nw_server_buffer_copy = (delegate* unmanaged[Cdecl]<nint, byte*, ulong, ulong*, NwErrorInfoV1*, uint>)resolve("nw_server_buffer_copy");
        nw_server_buffer_managed_credit_acquire = (delegate* unmanaged[Cdecl]<nint, ulong, nint*, NwErrorInfoV1*, uint>)resolve("nw_server_buffer_managed_credit_acquire");
        nw_server_buffer_release = (delegate* unmanaged[Cdecl]<nint, void>)resolve("nw_server_buffer_release");
        nw_server_response_begin_create = (delegate* unmanaged[Cdecl]<nint, uint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint>)resolve("nw_server_response_begin_create");
        nw_server_response_write_create = (delegate* unmanaged[Cdecl]<nint, nint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint>)resolve("nw_server_response_write_create");
        nw_server_response_flush_create = (delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint>)resolve("nw_server_response_flush_create");
        nw_server_response_finish_create = (delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint>)resolve("nw_server_response_finish_create");
        nw_server_sse_write_create = (delegate* unmanaged[Cdecl]<nint, nint, NwServerSseEventV1*, nint*, NwErrorInfoV1*, uint>)resolve("nw_server_sse_write_create");
        nw_server_grpc_read_create = (delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint>)resolve("nw_server_grpc_read_create");
        nw_server_grpc_write_create = (delegate* unmanaged[Cdecl]<nint, nint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint>)resolve("nw_server_grpc_write_create");
        nw_server_grpc_headers_create = (delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint>)resolve("nw_server_grpc_headers_create");
        nw_server_grpc_status_create = (delegate* unmanaged[Cdecl]<nint, uint, NwBytesViewV1, NwBytesViewV1, nint*, NwErrorInfoV1*, uint>)resolve("nw_server_grpc_status_create");
        nw_server_mcp_gate_resume_create = (delegate* unmanaged[Cdecl]<nint, uint, nint*, NwErrorInfoV1*, uint>)resolve("nw_server_mcp_gate_resume_create");
        nw_server_mcp_call_take = (delegate* unmanaged[Cdecl]<nint, uint*, nint*, NwErrorInfoV1*, uint>)resolve("nw_server_mcp_call_take");
        nw_server_mcp_reply_create = (delegate* unmanaged[Cdecl]<nint, nint, uint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint>)resolve("nw_server_mcp_reply_create");
        nw_server_metadata_headers = (delegate* unmanaged[Cdecl]<nint, NwBytesViewV1*, NwErrorInfoV1*, uint>)resolve("nw_server_metadata_headers");
        nw_server_metadata_grpc_headers = (delegate* unmanaged[Cdecl]<nint, NwBytesViewV1*, NwErrorInfoV1*, uint>)resolve("nw_server_metadata_grpc_headers");
    }
}
