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
    (void)release;
}
