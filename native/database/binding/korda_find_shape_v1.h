/* SPDX-License-Identifier: MIT
 * Private complete-key immutable shape extension. Frozen database v1 is unchanged.
 * Require capability bit 29 (db.find.shape.v1).
 */
#ifndef KORDA_FIND_SHAPE_V1_H
#define KORDA_FIND_SHAPE_V1_H
#include "korda_query_command_v1.h"
#define NW_DB_FIND_SHAPE_CAPABILITY_V1 (UINT64_C(1) << 29)
#ifdef __cplusplus
extern "C"
{
#endif
    nw_status NW_CDECL nw_db_find_plan_create_v1(nw_db_ref source, nw_db_ref model, uint32_t table, uint32_t projection,
                                                 nw_db_ref* result, uint64_t* cache_charge, uint64_t* logical_bytes,
                                                 nw_error_info_v1* error);
#ifdef __cplusplus
}
#endif
#endif
