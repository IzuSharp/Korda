#include "../../native/database/binding/nativeweave_database_binding_v1.h"
_Static_assert(sizeof(nw_db_token) == 8, "query token is u64, not a pointer");
_Static_assert(sizeof(nw_db_ref) == sizeof(void*), "opaque owner pointer");
_Static_assert(sizeof(nw_bytes_view_v1) == 16, "canonical A01 byte view on Linux x64");
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
    (void)relation_register;
    (void)relation_load;
    (void)take_cursor;
    (void)cancel;
    (void)retire;
    (void)release;
}
