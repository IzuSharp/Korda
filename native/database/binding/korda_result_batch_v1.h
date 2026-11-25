/* SPDX-License-Identifier: MIT
 * Additive owned-result extension. The frozen database v1 declarations,
 * fingerprint and NWDB bytes are unchanged. Require capability bit 27.
 */
#ifndef KORDA_RESULT_BATCH_V1_H
#define KORDA_RESULT_BATCH_V1_H
#include "../../interop/abi/include/korda_abi_v1.h"

#ifndef KORDA_DATABASE_BINDING_V1_H
typedef struct nw_db_owner* nw_db_ref;
typedef struct nw_db_buffer nw_db_buffer;
#endif

#define NW_DB_RESULT_BATCH_CAPABILITY_V1 (UINT64_C(1) << 27)

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
    nw_status NW_CDECL nw_db_operation_take_batch_v1(nw_db_ref operation, nw_db_batch_result_v1* result,
                                                     nw_error_info_v1* error);
    nw_status NW_CDECL nw_db_cursor_check_read_admission_v1(nw_db_ref cursor, nw_error_info_v1* error);
#ifdef __cplusplus
}
#endif
#endif
