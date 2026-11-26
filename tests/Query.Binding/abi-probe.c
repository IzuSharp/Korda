#include "../../native/database/binding/korda_command_completion_v1.h"
#include "../../native/database/binding/korda_cursor_pull_v1.h"
#include "../../native/database/binding/korda_database_binding_v1.h"
#include <stddef.h>
_Static_assert(sizeof(nw_db_token) == 8, "query token is u64, not a pointer");
_Static_assert(sizeof(nw_db_ref) == sizeof(void*), "opaque owner pointer");
_Static_assert(sizeof(nw_bytes_view_v1) == 16, "canonical A01 byte view on Linux x64");
_Static_assert(sizeof(nw_db_batch_result_v1) == 32, "additive owned result batch x64 layout");
_Static_assert(offsetof(nw_db_batch_result_v1, bytes) == 8, "owned result byte view offset");
_Static_assert(offsetof(nw_db_batch_result_v1, known_end) == 24, "owned result EOF offset");
_Static_assert(offsetof(nw_db_batch_result_v1, reserved) == 28, "owned result reserved offset");
_Static_assert(sizeof(nw_db_cursor_callbacks_v1) == 24, "cursor ready callback x64 layout");
_Static_assert(offsetof(nw_db_cursor_callbacks_v1, context) == 8, "cursor callback context offset");
_Static_assert(offsetof(nw_db_cursor_callbacks_v1, ready) == 16, "cursor callback entry offset");
_Static_assert(sizeof(nw_db_command_request_args_v1) == 48, "one-shot command request x64 layout");
_Static_assert(offsetof(nw_db_command_request_args_v1, kind) == 4, "command request kind offset");
_Static_assert(offsetof(nw_db_command_request_args_v1, target) == 8, "command target offset");
_Static_assert(offsetof(nw_db_command_request_args_v1, input) == 16, "command input offset");
_Static_assert(offsetof(nw_db_command_request_args_v1, payload) == 24, "command payload offset");
_Static_assert(offsetof(nw_db_command_request_args_v1, action) == 40, "command action offset");
_Static_assert(offsetof(nw_db_command_request_args_v1, reserved) == 44, "command reserved offset");
_Static_assert(sizeof(nw_db_command_result_v1) == 32, "one-shot command result x64 layout");
_Static_assert(offsetof(nw_db_command_result_v1, kind) == 4, "command result kind offset");
_Static_assert(offsetof(nw_db_command_result_v1, count) == 8, "command count offset");
_Static_assert(offsetof(nw_db_command_result_v1, owner) == 16, "command owner offset");
_Static_assert(offsetof(nw_db_command_result_v1, buffer) == 24, "command buffer offset");
static nw_status(NW_CDECL* const command_request)(const nw_db_command_request_args_v1*,
                                                  const nw_db_cursor_callbacks_v1*, nw_db_ref*,
                                                  nw_error_info_v1*) = nw_db_command_request_v1;
static nw_status(NW_CDECL* const command_take)(nw_db_ref, uint32_t, nw_db_command_result_v1*,
                                               nw_error_info_v1*) = nw_db_command_take_v1;
static nw_status(NW_CDECL* const command_error)(nw_db_ref, nw_db_buffer**,
                                                nw_error_info_v1*) = nw_db_command_take_error_v1;
static nw_status(NW_CDECL* const command_cancel)(nw_db_ref) = nw_db_command_cancel_v1;
static nw_status(NW_CDECL* const take_batch)(nw_db_ref, uint64_t, nw_db_batch_result_v1*,
                                             nw_error_info_v1*) = nw_db_cursor_pull_take_v1;
static nw_status(NW_CDECL* const check_read_admission)(nw_db_ref, nw_error_info_v1*) = nw_db_cursor_pull_check_read_v1;
static nw_status(NW_CDECL* const create_query)(nw_db_ref, nw_db_ref, uint32_t, nw_db_ref*,
                                               nw_error_info_v1*) = nw_db_query_create_v1;
static nw_status(NW_CDECL* const relation_register)(nw_db_ref, nw_db_ref, nw_db_ref, nw_bytes_view_v1, nw_db_ref*,
                                                    nw_error_info_v1*) = nw_db_relation_register_v1;
static nw_status(NW_CDECL* const relation_load)(nw_db_ref, nw_db_ref, nw_bytes_view_v1, uint32_t, nw_db_ref*,
                                                nw_error_info_v1*) = nw_db_relation_load_create_v1;
static nw_status(NW_CDECL* const take_cursor)(nw_db_ref, uint32_t, nw_db_ref*,
                                              nw_error_info_v1*) = nw_db_operation_take_owner_v1;
static nw_status(NW_CDECL* const cancel)(nw_db_ref) = nw_db_operation_request_cancel_v1;
static nw_status(NW_CDECL* const retire)(nw_db_ref) = nw_db_operation_try_retire_v1;
static void(NW_CDECL* const release)(nw_db_ref) = nw_db_operation_release_v1;
void binding_signature_probe(void)
{
    (void)create_query;
    (void)take_batch;
    (void)check_read_admission;
    (void)relation_register;
    (void)relation_load;
    (void)take_cursor;
    (void)cancel;
    (void)retire;
    (void)command_request;
    (void)command_take;
    (void)command_error;
    (void)command_cancel;
    (void)release;
}
