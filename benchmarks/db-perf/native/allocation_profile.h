#ifndef DB_PERF_ALLOCATION_PROFILE_H
#define DB_PERF_ALLOCATION_PROFILE_H
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
typedef struct db_perf_alloc_snapshot_v1 {
    uint64_t version, table_capacity, table_bytes;
    uint64_t malloc_calls, calloc_calls, realloc_calls, aligned_calls, free_calls, null_frees;
    uint64_t successful_allocations, failed_allocations, deallocations;
    uint64_t allocated_requested_bytes, released_requested_bytes, allocated_usable_bytes, released_usable_bytes;
    uint64_t live_allocations, live_requested_bytes, live_usable_bytes;
    uint64_t peak_live_allocations, peak_requested_bytes, peak_usable_bytes;
    uint64_t unknown_frees, unknown_reallocs, dropped_allocations, duplicate_addresses, initialization_failures;
} db_perf_alloc_snapshot_v1;
typedef int (*db_perf_alloc_snapshot_fn)(db_perf_alloc_snapshot_v1*, uint64_t, int);
int db_perf_alloc_snapshot(db_perf_alloc_snapshot_v1*, uint64_t, int);
#ifdef __cplusplus
}
#endif
#endif
