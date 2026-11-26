/* SPDX-License-Identifier: MIT
 * Additive complete-query extension. The frozen database v1 declarations and
 * fingerprint remain unchanged. Require capability bit 26 and all three exports.
 */
#ifndef KORDA_QUERY_COMMAND_V1_H
#define KORDA_QUERY_COMMAND_V1_H
#include "../../interop/abi/include/korda_abi_v1.h"

#ifndef KORDA_DATABASE_BINDING_V1_H
typedef struct nw_db_owner* nw_db_ref;
typedef struct nw_db_buffer nw_db_buffer;
#endif

#define NW_DB_QUERY_COMMAND_CAPABILITY_V1 (UINT64_C(1) << 26)

typedef struct nw_db_query_dependency_v1
{
    nw_db_ref owner;
    uint32_t kind; /* 1 = model, 2 = immutable plan, 3 = immutable CTE */
    uint32_t reserved;
} nw_db_query_dependency_v1;

#ifdef __cplusplus
extern "C"
{
#endif
    nw_status NW_CDECL nw_db_query_command_compile_v1(nw_db_ref session, nw_bytes_view_v1 command,
                                                      const nw_db_query_dependency_v1* dependencies,
                                                      uint32_t dependency_count, uint32_t terminal, nw_db_ref* plan,
                                                      nw_error_info_v1* error);
    nw_status NW_CDECL nw_db_query_command_render_v1(nw_db_ref session, nw_bytes_view_v1 command,
                                                     const nw_db_query_dependency_v1* dependencies,
                                                     uint32_t dependency_count, nw_db_buffer** sql,
                                                     nw_error_info_v1* error);
    nw_status NW_CDECL nw_db_query_command_cte_v1(nw_db_ref session, nw_bytes_view_v1 command,
                                                  const nw_db_query_dependency_v1* dependencies,
                                                  uint32_t dependency_count, nw_db_ref declared_model,
                                                  uint32_t table_id, uint32_t recursive, nw_db_ref* cte,
                                                  nw_error_info_v1* error);
#ifdef __cplusplus
}
#endif
#endif
