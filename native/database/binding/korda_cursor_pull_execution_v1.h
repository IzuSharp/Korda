/* SPDX-License-Identifier: MIT
 * Private admitted cursor execution extension. The frozen database v1 ABI and
 * cursor pull request semantics are unchanged. Require capability bit 30
 * (db.cursor.pull.execution.v1), in addition to db.cursor.pull.v1.
 */
#ifndef KORDA_CURSOR_PULL_EXECUTION_V1_H
#define KORDA_CURSOR_PULL_EXECUTION_V1_H
#include "korda_cursor_pull_v1.h"

#define NW_DB_CURSOR_PULL_EXECUTION_CAPABILITY_V1 (UINT64_C(1) << 30)

#ifdef __cplusplus
extern "C"
{
#endif
    /* Prepare validates and reserves one generation without provider I/O or
     * native queue submission. Its callback may be delivered by concurrent
     * close before prepare returns. The caller must retain its channel owner
     * until the accepted managed scheduling obligation is run or abandoned.
     */
    nw_status NW_CDECL nw_db_cursor_pull_prepare_v1(nw_db_ref channel, uint64_t generation, uint32_t rows,
                                                    uint64_t bytes, const nw_db_cursor_callbacks_v1* callbacks,
                                                    nw_error_info_v1* error);
    /* Exactly one run or abandon claims an admitted generation. Run blocks the
     * calling worker in the existing provider algorithm. Abandon performs no
     * new provider execution; existing cursor cleanup retains its contract.
     * Query failures remain available through the existing take
     * and take_error entries; NW_OK describes successful execution of this
     * control entry, not query success.
     *
     * Close settles unclaimed work itself. The first later run/abandon for that
     * exact generation succeeds without repeating work; another claim or an
     * old generation is rejected. Concurrent close settlement may still be
     * publishing readiness when this no-op claim returns: callers must observe
     * the original ready callback before taking the result.
     */
    nw_status NW_CDECL nw_db_cursor_pull_run_v1(nw_db_ref channel, uint64_t generation, nw_error_info_v1* error);
    nw_status NW_CDECL nw_db_cursor_pull_abandon_v1(nw_db_ref channel, uint64_t generation, nw_error_info_v1* error);
#ifdef __cplusplus
}
#endif
#endif
