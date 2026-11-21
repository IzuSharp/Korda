/* SPDX-License-Identifier: MIT
 * Compile the unmodified normative database header as C17 and call the real
 * C++ module. C++ definitions also compile against its private declaration view.
 */
#include "../../database/binding/nativeweave_database_binding_v1.h"
#include <assert.h>
#include <stdint.h>

_Static_assert(sizeof(nw_db_ref) == 8, "Linux x64 opaque owner size");
_Static_assert(sizeof(nw_db_token) == 8, "opaque generation token size");

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
    nw_db_operation_release_v1(NULL);
    nw_db_owner_release_v1(NULL);
    nw_db_buffer_release_v1(NULL);
    return 0;
}
