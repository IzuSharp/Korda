/* SPDX-License-Identifier: MIT
 * Private one-shot command completion extension. The frozen database v1 ABI
 * is unchanged. Require capability bit 28 (db.command.completion.v1).
 */
#ifndef KORDA_COMMAND_COMPLETION_V1_H
#define KORDA_COMMAND_COMPLETION_V1_H
#include "korda_cursor_pull_v1.h"

#define NW_DB_COMMAND_COMPLETION_CAPABILITY_V1 (UINT64_C(1) << 28)

typedef struct nw_db_command_request_args_v1
{
    uint32_t struct_size;
    uint32_t kind;
    nw_db_ref target;
    nw_db_ref input;
    nw_bytes_view_v1 payload;
    uint32_t action;
    uint32_t reserved;
} nw_db_command_request_args_v1;

typedef struct nw_db_command_result_v1
{
    uint32_t struct_size;
    uint32_t kind;
    int64_t count;
    nw_db_ref owner;
    nw_db_buffer* buffer;
} nw_db_command_result_v1;

#ifdef __cplusplus
extern "C"
{
#endif
    nw_status NW_CDECL nw_db_command_request_v1(const nw_db_command_request_args_v1* request,
                                                const nw_db_cursor_callbacks_v1* callbacks, nw_db_ref* command,
                                                nw_error_info_v1* error);
    nw_status NW_CDECL nw_db_command_take_v1(nw_db_ref command, uint32_t expected_kind, nw_db_command_result_v1* result,
                                             nw_error_info_v1* error);
    nw_status NW_CDECL nw_db_command_take_error_v1(nw_db_ref command, nw_db_buffer** result, nw_error_info_v1* error);
    nw_status NW_CDECL nw_db_command_cancel_v1(nw_db_ref command);
#ifdef __cplusplus
}
#endif
#endif
