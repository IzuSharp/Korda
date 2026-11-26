/* SPDX-License-Identifier: MIT
 * Private cursor pull extension. The frozen database v1 ABI is unchanged.
 * Require capability bit 27 (db.cursor.pull.v1).
 */
#ifndef KORDA_CURSOR_PULL_V1_H
#define KORDA_CURSOR_PULL_V1_H
#include "../../interop/abi/include/korda_abi_v1.h"

#ifndef KORDA_DATABASE_BINDING_V1_H
typedef struct nw_db_owner* nw_db_ref;
typedef struct nw_db_buffer nw_db_buffer;
#endif

#define NW_DB_CURSOR_PULL_CAPABILITY_V1 (UINT64_C(1) << 27)

typedef void(NW_CDECL* nw_db_cursor_ready_v1)(void* context, uint64_t generation);
typedef struct nw_db_cursor_callbacks_v1
{
    uint32_t struct_size;
    uint32_t reserved;
    void* context;
    nw_db_cursor_ready_v1 ready;
} nw_db_cursor_callbacks_v1;

typedef struct nw_db_batch_result_v1
{
    nw_db_buffer* owner;
    nw_bytes_view_v1 bytes;
    uint32_t known_end;
    uint32_t reserved;
} nw_db_batch_result_v1;

#ifdef __cplusplus
extern "C"
{
#endif
    nw_status NW_CDECL nw_db_cursor_pull_create_v1(nw_db_ref session, nw_db_ref plan, nw_bytes_view_v1 arguments,
                                                   nw_db_ref* channel, nw_error_info_v1* error);
    nw_status NW_CDECL nw_db_cursor_pull_relation_create_v1(nw_db_ref session, nw_db_ref relation, nw_bytes_view_v1 key,
                                                            uint32_t projection, nw_db_ref* channel,
                                                            nw_error_info_v1* error);
    nw_status NW_CDECL nw_db_cursor_pull_mutation_create_v1(nw_db_ref session, nw_db_ref model, uint32_t kind,
                                                            nw_bytes_view_v1 payload, nw_db_ref* channel,
                                                            nw_error_info_v1* error);
    nw_status NW_CDECL nw_db_cursor_pull_request_v1(nw_db_ref channel, uint64_t generation, uint32_t rows,
                                                    uint64_t bytes, const nw_db_cursor_callbacks_v1* callbacks,
                                                    nw_error_info_v1* error);
    nw_status NW_CDECL nw_db_cursor_pull_take_v1(nw_db_ref channel, uint64_t generation, nw_db_batch_result_v1* result,
                                                 nw_error_info_v1* error);
    nw_status NW_CDECL nw_db_cursor_pull_take_error_v1(nw_db_ref channel, uint64_t generation, nw_db_buffer** result,
                                                       nw_error_info_v1* error);
    nw_status NW_CDECL nw_db_cursor_pull_check_read_v1(nw_db_ref channel, nw_error_info_v1* error);
    nw_status NW_CDECL nw_db_cursor_pull_stop_v1(nw_db_ref channel);
    nw_status NW_CDECL nw_db_cursor_pull_close_request_v1(nw_db_ref channel, const nw_db_cursor_callbacks_v1* callbacks,
                                                          nw_error_info_v1* error);
    nw_status NW_CDECL nw_db_cursor_pull_close_take_v1(nw_db_ref channel, nw_db_buffer** result,
                                                       nw_error_info_v1* error);
#ifdef __cplusplus
}
#endif
#endif
