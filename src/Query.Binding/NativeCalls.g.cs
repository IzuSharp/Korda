// Generated from exact pinned signatures-v1.json; do not edit.
using NativeWeave.Interop.Abi;
namespace NativeWeave.Query.Internal;
/// <summary>Exact module-local direct calls; the creating engine supplies process-pinned exports.</summary>
internal sealed unsafe class NativeCalls
{
    internal readonly string EngineId;
    /// <summary>Exact Cdecl nw_db_operation_start_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, NwOperationCallbacksV1*, NwErrorInfoV1*, uint> nw_db_operation_start_v1;
    /// <summary>Exact Cdecl nw_db_operation_request_cancel_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, uint> nw_db_operation_request_cancel_v1;
    /// <summary>Exact Cdecl nw_db_operation_take_owner_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, uint, nint*, NwErrorInfoV1*, uint> nw_db_operation_take_owner_v1;
    /// <summary>Exact Cdecl nw_db_operation_take_buffer_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint> nw_db_operation_take_buffer_v1;
    /// <summary>Exact Cdecl nw_db_operation_take_count_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, long*, NwErrorInfoV1*, uint> nw_db_operation_take_count_v1;
    /// <summary>Exact Cdecl nw_db_operation_take_error_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint> nw_db_operation_take_error_v1;
    /// <summary>Exact Cdecl nw_db_operation_try_retire_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, uint> nw_db_operation_try_retire_v1;
    /// <summary>Exact Cdecl nw_db_operation_release_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, void> nw_db_operation_release_v1;
    /// <summary>Exact Cdecl nw_db_source_create_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<NwBytesViewV1, nint*, NwErrorInfoV1*, uint> nw_db_source_create_v1;
    /// <summary>Exact Cdecl nw_db_session_open_create_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint> nw_db_session_open_create_v1;
    /// <summary>Exact Cdecl nw_db_codec_register_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, uint, NwBytesViewV1, uint*, NwErrorInfoV1*, uint> nw_db_codec_register_v1;
    /// <summary>Exact Cdecl nw_db_metadata_register_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint> nw_db_metadata_register_v1;
    /// <summary>Exact Cdecl nw_db_projection_register_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, uint*, NwErrorInfoV1*, uint> nw_db_projection_register_v1;
    /// <summary>Exact Cdecl nw_db_relation_register_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, nint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint> nw_db_relation_register_v1;
    /// <summary>Exact Cdecl nw_db_relation_load_create_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, NwBytesViewV1, uint, nint*, NwErrorInfoV1*, uint> nw_db_relation_load_create_v1;
    /// <summary>Exact Cdecl nw_db_query_create_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, uint, nint*, NwErrorInfoV1*, uint> nw_db_query_create_v1;
    /// <summary>Exact Cdecl nw_db_expr_column_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, ulong, uint, ulong*, NwErrorInfoV1*, uint> nw_db_expr_column_v1;
    /// <summary>Exact Cdecl nw_db_expr_value_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, ulong*, NwErrorInfoV1*, uint> nw_db_expr_value_v1;
    /// <summary>Exact Cdecl nw_db_expr_parameter_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, uint, NwBytesViewV1, uint, uint, ulong*, NwErrorInfoV1*, uint> nw_db_expr_parameter_v1;
    /// <summary>Exact Cdecl nw_db_expr_binary_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, uint, ulong, ulong, ulong*, NwErrorInfoV1*, uint> nw_db_expr_binary_v1;
    /// <summary>Exact Cdecl nw_db_expr_unary_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, uint, ulong, ulong*, NwErrorInfoV1*, uint> nw_db_expr_unary_v1;
    /// <summary>Exact Cdecl nw_db_expr_list_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, uint, ulong, NwBytesViewV1, ulong*, NwErrorInfoV1*, uint> nw_db_expr_list_v1;
    /// <summary>Exact Cdecl nw_db_query_import_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, uint, ulong*, NwErrorInfoV1*, uint> nw_db_query_import_v1;
    /// <summary>Exact Cdecl nw_db_query_clause_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, uint, NwBytesViewV1, ulong*, NwErrorInfoV1*, uint> nw_db_query_clause_v1;
    /// <summary>Exact Cdecl nw_db_query_seal_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, uint, nint*, NwErrorInfoV1*, uint> nw_db_query_seal_v1;
    /// <summary>Exact Cdecl nw_db_query_clone_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint> nw_db_query_clone_v1;
    /// <summary>Exact Cdecl nw_db_query_render_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint> nw_db_query_render_v1;
    /// <summary>Exact Cdecl nw_db_cte_create_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, uint, nint, uint, nint*, NwErrorInfoV1*, uint> nw_db_cte_create_v1;
    /// <summary>Exact Cdecl nw_db_execute_create_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint> nw_db_execute_create_v1;
    /// <summary>Exact Cdecl nw_db_sql_create_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, NwBytesViewV1, NwBytesViewV1, nint*, NwErrorInfoV1*, uint> nw_db_sql_create_v1;
    /// <summary>Exact Cdecl nw_db_cursor_fetch_create_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, uint, ulong, nint*, NwErrorInfoV1*, uint> nw_db_cursor_fetch_create_v1;
    /// <summary>Exact Cdecl nw_db_tx_begin_create_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint> nw_db_tx_begin_create_v1;
    /// <summary>Exact Cdecl nw_db_tx_finish_create_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, uint, nint*, NwErrorInfoV1*, uint> nw_db_tx_finish_create_v1;
    /// <summary>Exact Cdecl nw_db_savepoint_create_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint> nw_db_savepoint_create_v1;
    /// <summary>Exact Cdecl nw_db_savepoint_action_create_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, uint, nint*, NwErrorInfoV1*, uint> nw_db_savepoint_action_create_v1;
    /// <summary>Exact Cdecl nw_db_mutation_create_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, uint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint> nw_db_mutation_create_v1;
    /// <summary>Exact Cdecl nw_db_admin_create_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, uint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint> nw_db_admin_create_v1;
    /// <summary>Exact Cdecl nw_db_close_create_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint> nw_db_close_create_v1;
    /// <summary>Exact Cdecl nw_db_snapshot_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint> nw_db_snapshot_v1;
    /// <summary>Exact Cdecl nw_db_owner_release_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, void> nw_db_owner_release_v1;
    /// <summary>Exact Cdecl nw_db_buffer_view_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, NwBytesViewV1*, NwErrorInfoV1*, uint> nw_db_buffer_view_v1;
    /// <summary>Exact Cdecl nw_db_buffer_release_v1; borrowed input spans end at return and outputs transfer only on OK.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, void> nw_db_buffer_release_v1;
    /// <summary>Captures already negotiated creating-module exports; zero means unsupported until required by a call.</summary>
    internal NativeCalls(string engineId, Func<string, nint> resolve)
    {
        EngineId = engineId;
        ArgumentNullException.ThrowIfNull(resolve);
        nw_db_operation_start_v1 = (delegate* unmanaged[Cdecl]<nint, NwOperationCallbacksV1*, NwErrorInfoV1*, uint>)resolve("nw_db_operation_start_v1");
        nw_db_operation_request_cancel_v1 = (delegate* unmanaged[Cdecl]<nint, uint>)resolve("nw_db_operation_request_cancel_v1");
        nw_db_operation_take_owner_v1 = (delegate* unmanaged[Cdecl]<nint, uint, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_operation_take_owner_v1");
        nw_db_operation_take_buffer_v1 = (delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_operation_take_buffer_v1");
        nw_db_operation_take_count_v1 = (delegate* unmanaged[Cdecl]<nint, long*, NwErrorInfoV1*, uint>)resolve("nw_db_operation_take_count_v1");
        nw_db_operation_take_error_v1 = (delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_operation_take_error_v1");
        nw_db_operation_try_retire_v1 = (delegate* unmanaged[Cdecl]<nint, uint>)resolve("nw_db_operation_try_retire_v1");
        nw_db_operation_release_v1 = (delegate* unmanaged[Cdecl]<nint, void>)resolve("nw_db_operation_release_v1");
        nw_db_source_create_v1 = (delegate* unmanaged[Cdecl]<NwBytesViewV1, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_source_create_v1");
        nw_db_session_open_create_v1 = (delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_session_open_create_v1");
        nw_db_codec_register_v1 = (delegate* unmanaged[Cdecl]<nint, uint, NwBytesViewV1, uint*, NwErrorInfoV1*, uint>)resolve("nw_db_codec_register_v1");
        nw_db_metadata_register_v1 = (delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_metadata_register_v1");
        nw_db_projection_register_v1 = (delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, uint*, NwErrorInfoV1*, uint>)resolve("nw_db_projection_register_v1");
        nw_db_relation_register_v1 = (delegate* unmanaged[Cdecl]<nint, nint, nint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_relation_register_v1");
        nw_db_relation_load_create_v1 = (delegate* unmanaged[Cdecl]<nint, nint, NwBytesViewV1, uint, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_relation_load_create_v1");
        nw_db_query_create_v1 = (delegate* unmanaged[Cdecl]<nint, nint, uint, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_query_create_v1");
        nw_db_expr_column_v1 = (delegate* unmanaged[Cdecl]<nint, ulong, uint, ulong*, NwErrorInfoV1*, uint>)resolve("nw_db_expr_column_v1");
        nw_db_expr_value_v1 = (delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, ulong*, NwErrorInfoV1*, uint>)resolve("nw_db_expr_value_v1");
        nw_db_expr_parameter_v1 = (delegate* unmanaged[Cdecl]<nint, uint, NwBytesViewV1, uint, uint, ulong*, NwErrorInfoV1*, uint>)resolve("nw_db_expr_parameter_v1");
        nw_db_expr_binary_v1 = (delegate* unmanaged[Cdecl]<nint, uint, ulong, ulong, ulong*, NwErrorInfoV1*, uint>)resolve("nw_db_expr_binary_v1");
        nw_db_expr_unary_v1 = (delegate* unmanaged[Cdecl]<nint, uint, ulong, ulong*, NwErrorInfoV1*, uint>)resolve("nw_db_expr_unary_v1");
        nw_db_expr_list_v1 = (delegate* unmanaged[Cdecl]<nint, uint, ulong, NwBytesViewV1, ulong*, NwErrorInfoV1*, uint>)resolve("nw_db_expr_list_v1");
        nw_db_query_import_v1 = (delegate* unmanaged[Cdecl]<nint, nint, uint, ulong*, NwErrorInfoV1*, uint>)resolve("nw_db_query_import_v1");
        nw_db_query_clause_v1 = (delegate* unmanaged[Cdecl]<nint, uint, NwBytesViewV1, ulong*, NwErrorInfoV1*, uint>)resolve("nw_db_query_clause_v1");
        nw_db_query_seal_v1 = (delegate* unmanaged[Cdecl]<nint, uint, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_query_seal_v1");
        nw_db_query_clone_v1 = (delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_query_clone_v1");
        nw_db_query_render_v1 = (delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_query_render_v1");
        nw_db_cte_create_v1 = (delegate* unmanaged[Cdecl]<nint, uint, nint, uint, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_cte_create_v1");
        nw_db_execute_create_v1 = (delegate* unmanaged[Cdecl]<nint, nint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_execute_create_v1");
        nw_db_sql_create_v1 = (delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, NwBytesViewV1, NwBytesViewV1, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_sql_create_v1");
        nw_db_cursor_fetch_create_v1 = (delegate* unmanaged[Cdecl]<nint, uint, ulong, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_cursor_fetch_create_v1");
        nw_db_tx_begin_create_v1 = (delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_tx_begin_create_v1");
        nw_db_tx_finish_create_v1 = (delegate* unmanaged[Cdecl]<nint, uint, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_tx_finish_create_v1");
        nw_db_savepoint_create_v1 = (delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_savepoint_create_v1");
        nw_db_savepoint_action_create_v1 = (delegate* unmanaged[Cdecl]<nint, uint, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_savepoint_action_create_v1");
        nw_db_mutation_create_v1 = (delegate* unmanaged[Cdecl]<nint, nint, uint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_mutation_create_v1");
        nw_db_admin_create_v1 = (delegate* unmanaged[Cdecl]<nint, uint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_admin_create_v1");
        nw_db_close_create_v1 = (delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_close_create_v1");
        nw_db_snapshot_v1 = (delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_snapshot_v1");
        nw_db_owner_release_v1 = (delegate* unmanaged[Cdecl]<nint, void>)resolve("nw_db_owner_release_v1");
        nw_db_buffer_view_v1 = (delegate* unmanaged[Cdecl]<nint, NwBytesViewV1*, NwErrorInfoV1*, uint>)resolve("nw_db_buffer_view_v1");
        nw_db_buffer_release_v1 = (delegate* unmanaged[Cdecl]<nint, void>)resolve("nw_db_buffer_release_v1");
    }
}
