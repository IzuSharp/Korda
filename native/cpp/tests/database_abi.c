/* SPDX-License-Identifier: MIT
 * Compile the unmodified normative database header as C17 and call the real
 * C++ module. C++ definitions also compile against its private declaration view.
 */
#include "../../database/binding/korda_cursor_pull_execution_v1.h"
#include "../../database/binding/korda_cursor_pull_v1.h"
#include "../../database/binding/korda_database_binding_v1.h"
#include "../../database/binding/korda_find_shape_v1.h"
#include "../../database/binding/korda_query_command_v1.h"
#include <assert.h>
#include <stddef.h>
#include <stdint.h>

_Static_assert(sizeof(nw_db_ref) == 8, "Linux x64 opaque owner size");
_Static_assert(sizeof(nw_db_token) == 8, "opaque generation token size");
_Static_assert(sizeof(nw_db_query_dependency_v1) == 16, "query dependency sidecar x64 layout");
_Static_assert(offsetof(nw_db_query_dependency_v1, kind) == 8, "query dependency kind offset");
_Static_assert(sizeof(nw_db_cursor_callbacks_v1) == 24, "ready callback x64 layout");
_Static_assert(offsetof(nw_db_cursor_callbacks_v1, context) == 8, "ready callback context offset");
_Static_assert(offsetof(nw_db_cursor_callbacks_v1, ready) == 16, "ready callback function offset");
_Static_assert(sizeof(nw_db_batch_result_v1) == 32, "owned batch x64 layout");
_Static_assert(offsetof(nw_db_batch_result_v1, owner) == 0, "owned batch owner offset");
_Static_assert(offsetof(nw_db_batch_result_v1, bytes) == 8, "owned batch byte view offset");
_Static_assert(offsetof(nw_db_batch_result_v1, known_end) == 24, "owned batch EOF offset");
_Static_assert(offsetof(nw_db_batch_result_v1, reserved) == 28, "owned batch reserved offset");
_Static_assert(NW_DB_CURSOR_PULL_EXECUTION_CAPABILITY_V1 == (UINT64_C(1) << 30),
               "admitted cursor execution capability is independent of frozen pull ABI");

int main(void)
{
    nw_error_info_v1 error = {0};
    nw_db_ref source = (nw_db_ref)(uintptr_t)1;
    nw_bytes_view_v1 empty = {0};
    error.struct_size = sizeof(error);
    assert(nw_db_source_create_v1(empty, &source, &error) != NW_OK);
    assert(source == NULL);
    nw_db_ref operation = (nw_db_ref)(uintptr_t)1;
    assert(nw_db_session_open_create_v1(NULL, &operation, &error) == NW_INVALID_STATE);
    assert(operation == NULL);
    nw_db_ref plan = (nw_db_ref)(uintptr_t)1;
    assert(nw_db_query_command_compile_v1(NULL, empty, NULL, 0, 8, &plan, &error) == NW_INVALID_STATE);
    assert(plan == NULL);
    plan = (nw_db_ref)(uintptr_t)1;
    uint64_t charge = 42;
    uint64_t logical_bytes = 42;
    assert(nw_db_find_plan_create_v1(NULL, NULL, 0, 0, &plan, &charge, &logical_bytes, &error) == NW_INVALID_STATE);
    assert(plan == NULL && charge == 0 && logical_bytes == 0);
    nw_db_buffer* sql = (nw_db_buffer*)(uintptr_t)1;
    assert(nw_db_query_command_render_v1(NULL, empty, NULL, 0, &sql, &error) == NW_INVALID_STATE);
    assert(sql == NULL);
    nw_db_ref cte = (nw_db_ref)(uintptr_t)1;
    assert(nw_db_query_command_cte_v1(NULL, empty, NULL, 0, NULL, 0, 0, &cte, &error) == NW_INVALID_STATE);
    assert(cte == NULL);
    nw_db_batch_result_v1 batch = {(nw_db_buffer*)(uintptr_t)1, {(const uint8_t*)(uintptr_t)1, 42}, 1, 1};
    assert(nw_db_cursor_pull_take_v1(NULL, 1, &batch, &error) == NW_INVALID_STATE);
    assert(batch.owner == NULL && batch.bytes.data == NULL && batch.bytes.length == 0 && batch.known_end == 0 &&
           batch.reserved == 0);
    assert(nw_db_cursor_pull_check_read_v1(NULL, &error) == NW_INVALID_STATE);
    assert(nw_db_cursor_pull_stop_v1(NULL) == NW_INVALID_STATE);
    assert(nw_db_cursor_pull_prepare_v1(NULL, 1, 1, 256, NULL, &error) == NW_INVALID_STATE);
    assert(nw_db_cursor_pull_run_v1(NULL, 1, &error) == NW_INVALID_STATE);
    assert(nw_db_cursor_pull_abandon_v1(NULL, 1, &error) == NW_INVALID_STATE);
    assert(nw_db_cursor_pull_close_request_v1(NULL, NULL, &error) == NW_INVALID_STATE);
    nw_db_buffer* closed = (nw_db_buffer*)(uintptr_t)1;
    assert(nw_db_cursor_pull_close_take_v1(NULL, &closed, &error) == NW_INVALID_STATE && closed == NULL);
    nw_db_ref pull = (nw_db_ref)(uintptr_t)1;
    assert(nw_db_cursor_pull_create_v1(NULL, NULL, empty, &pull, &error) == NW_INVALID_STATE && pull == NULL);
    nw_db_operation_release_v1(NULL);
    nw_db_owner_release_v1(NULL);
    nw_db_buffer_release_v1(NULL);
    return 0;
}
