/* Diagnostic glibc interposition, never part of timing/package binaries.
 * Reference: https://sourceware.org/glibc/manual/latest/html_node/Replacing-malloc.html
 * Calls glibc's exported implementation entry points, preserving its pointers
 * and allocator. The bounded table is separate mmap storage, not hidden heap.
 */
#define _GNU_SOURCE
#include "allocation_profile.h"
#include <errno.h>
#include <limits.h>
#include <malloc.h>
#include <pthread.h>
#include <stdint.h>
#include <stdlib.h>
#include <sys/mman.h>
#include <unistd.h>

extern void* __libc_malloc(size_t);
extern void* __libc_calloc(size_t, size_t);
extern void* __libc_realloc(void*, size_t);
extern void __libc_free(void*);
extern void* __libc_memalign(size_t, size_t);
extern void* __libc_valloc(size_t);
extern void* __libc_pvalloc(size_t);
typedef struct { uintptr_t address; size_t requested, usable; } entry;
static pthread_mutex_t gate = PTHREAD_MUTEX_INITIALIZER;
static entry* entries;
static size_t capacity;
static int initialized;
static db_perf_alloc_snapshot_v1 totals = {.version = 1};

static void initialize_locked(void)
{
    if (initialized) return;
    initialized = 1; capacity = 1u << 20;
    const char* setting = getenv("DB_PERF_ALLOC_CAPACITY");
    if (setting && *setting)
    {
        size_t value = 0; int valid = 1;
        for (const char* c = setting; *c; ++c)
        { if (*c < '0' || *c > '9' || value > (1u << 22)) { valid = 0; break; } value = value * 10 + (unsigned)(*c - '0'); }
        if (valid && value >= 256 && value <= (1u << 22) && !(value & (value - 1))) capacity = value;
        else { ++totals.initialization_failures; return; }
    }
    totals.table_capacity = capacity; totals.table_bytes = capacity * sizeof(entry);
    void* memory = mmap(NULL, totals.table_bytes, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (memory == MAP_FAILED) { ++totals.initialization_failures; return; }
    entries = memory;
}
static size_t index_for(uintptr_t address)
{
    uint64_t value = address >> 4;
    value ^= value >> 33; value *= UINT64_C(0xff51afd7ed558ccd); value ^= value >> 33;
    return (size_t)value & (capacity - 1);
}
static entry* find_locked(void* pointer)
{
    if (!entries) return NULL;
    size_t index = index_for((uintptr_t)pointer);
    for (size_t count = 0; count < capacity; ++count, index = (index + 1) & (capacity - 1))
    {
        entry* item = entries + index;
        if (!item->address) return NULL;
        if (item->address == (uintptr_t)pointer) return item;
    }
    return NULL;
}
static void retire_locked(entry* item)
{
    ++totals.deallocations;
    totals.released_requested_bytes += item->requested; totals.released_usable_bytes += item->usable;
    --totals.live_allocations; totals.live_requested_bytes -= item->requested; totals.live_usable_bytes -= item->usable;
    item->address = 1; item->requested = item->usable = 0;
}
static void record_locked(void* pointer, size_t size)
{
    if (!pointer) { ++totals.failed_allocations; return; }
    ++totals.successful_allocations;
    if (!entries) { ++totals.dropped_allocations; return; }
    size_t index = index_for((uintptr_t)pointer); entry* vacant = NULL;
    for (size_t count = 0; count < capacity; ++count, index = (index + 1) & (capacity - 1))
    {
        entry* item = entries + index;
        if (item->address == (uintptr_t)pointer) { ++totals.duplicate_addresses; return; }
        if (item->address <= 1 && !vacant) vacant = item;
        if (!item->address) break;
    }
    if (!vacant) { ++totals.dropped_allocations; return; }
    *vacant = (entry){(uintptr_t)pointer, size, malloc_usable_size(pointer)};
    totals.allocated_requested_bytes += size; totals.allocated_usable_bytes += vacant->usable;
    ++totals.live_allocations; totals.live_requested_bytes += size; totals.live_usable_bytes += vacant->usable;
    if (totals.live_allocations > totals.peak_live_allocations) totals.peak_live_allocations = totals.live_allocations;
    if (totals.live_requested_bytes > totals.peak_requested_bytes) totals.peak_requested_bytes = totals.live_requested_bytes;
    if (totals.live_usable_bytes > totals.peak_usable_bytes) totals.peak_usable_bytes = totals.live_usable_bytes;
}
static void record(void* pointer, size_t size, uint64_t* calls)
{
    int saved = errno; pthread_mutex_lock(&gate); initialize_locked(); ++*calls; record_locked(pointer, size);
    pthread_mutex_unlock(&gate); errno = saved;
}
void* malloc(size_t size) { void* p = __libc_malloc(size); record(p, size, &totals.malloc_calls); return p; }
void* calloc(size_t count, size_t size)
{
    void* p = __libc_calloc(count, size); size_t total = count && size > SIZE_MAX / count ? 0 : count * size;
    record(p, total, &totals.calloc_calls); return p;
}
void free(void* pointer)
{
    int saved = errno; pthread_mutex_lock(&gate); initialize_locked(); ++totals.free_calls;
    if (!pointer) ++totals.null_frees;
    else { entry* item = find_locked(pointer); if (item) retire_locked(item); else ++totals.unknown_frees; }
    pthread_mutex_unlock(&gate); __libc_free(pointer); errno = saved;
}
void* realloc(void* pointer, size_t size)
{
    int prior_errno = errno; pthread_mutex_lock(&gate); initialize_locked(); ++totals.realloc_calls;
    entry* previous = pointer ? find_locked(pointer) : NULL;
    if (pointer && !previous) ++totals.unknown_reallocs;
    errno = prior_errno;
    // Keep retirement and address replacement atomic against another thread
    // acquiring an address released by glibc's realloc implementation.
    void* replacement = __libc_realloc(pointer, size); int saved = errno;
    if (replacement || (pointer && !size)) { if (previous) retire_locked(previous); }
    if (replacement || size || !pointer) record_locked(replacement, size);
    pthread_mutex_unlock(&gate); errno = saved; return replacement;
}
void* memalign(size_t alignment, size_t size)
{ void* p = __libc_memalign(alignment, size); record(p, size, &totals.aligned_calls); return p; }
void* aligned_alloc(size_t alignment, size_t size)
{
    void* p;
    if (!alignment || (alignment & (alignment - 1))) { errno = EINVAL; p = NULL; }
    else p = __libc_memalign(alignment, size);
    record(p, size, &totals.aligned_calls); return p;
}
int posix_memalign(void** result, size_t alignment, size_t size)
{
    int saved = errno, error = 0; void* p = NULL;
    if (alignment < sizeof(void*) || alignment % sizeof(void*) || (alignment & (alignment - 1))) error = EINVAL;
    else { p = __libc_memalign(alignment, size); if (!p) error = ENOMEM; }
    record(p, size, &totals.aligned_calls); if (!error) *result = p; errno = saved; return error;
}
void* valloc(size_t size) { void* p = __libc_valloc(size); record(p, size, &totals.aligned_calls); return p; }
void* pvalloc(size_t size)
{
    void* p = __libc_pvalloc(size); size_t page = (size_t)sysconf(_SC_PAGESIZE);
    size_t rounded = size > SIZE_MAX - (page - 1) ? 0 : (size + page - 1) / page * page;
    record(p, rounded, &totals.aligned_calls); return p;
}
void cfree(void* pointer) { free(pointer); }
void free_sized(void* pointer, size_t size) { (void)size; free(pointer); }
void free_aligned_sized(void* pointer, size_t alignment, size_t size) { (void)alignment; (void)size; free(pointer); }
int db_perf_alloc_snapshot(db_perf_alloc_snapshot_v1* result, uint64_t bytes, int reset_peak)
{
    if (!result || bytes != sizeof(*result)) return -1;
    int saved = errno; pthread_mutex_lock(&gate); initialize_locked();
    if (reset_peak)
    { totals.peak_live_allocations = totals.live_allocations; totals.peak_requested_bytes = totals.live_requested_bytes; totals.peak_usable_bytes = totals.live_usable_bytes; }
    *result = totals; pthread_mutex_unlock(&gate); errno = saved; return entries ? 0 : -2;
}
