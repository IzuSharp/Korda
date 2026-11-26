#define _GNU_SOURCE
#include "allocation_profile.h"
#include <assert.h>
#include <dlfcn.h>
#include <errno.h>
#include <malloc.h>
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static db_perf_alloc_snapshot_fn snapshot;
static pthread_barrier_t ready, start;
static void* concurrent(void* argument)
{
    size_t salt = (size_t)argument; pthread_barrier_wait(&ready); pthread_barrier_wait(&start);
    for (size_t i = 0; i < 10000; ++i)
    { size_t size = 1 + (i + salt) % 251; volatile unsigned char* p = malloc(size); assert(p); p[0] = (unsigned char)i; free((void*)p); }
    return NULL;
}
int main(int argc, char** argv)
{
    snapshot = (db_perf_alloc_snapshot_fn)dlsym(RTLD_DEFAULT, "db_perf_alloc_snapshot"); assert(snapshot);
    db_perf_alloc_snapshot_v1 a, b;
    if (argc == 2 && !strcmp(argv[1], "--exhaust-table"))
    {
        assert(!snapshot(&a, sizeof(a), 1)); void* pointers[8192];
        for (size_t i = 0; i < 8192; ++i) { pointers[i] = malloc(1); assert(pointers[i]); }
        assert(!snapshot(&b, sizeof(b), 0)); assert(b.dropped_allocations > a.dropped_allocations);
        for (size_t i = 0; i < 8192; ++i) free(pointers[i]);
        assert(!snapshot(&b, sizeof(b), 0)); assert(b.unknown_frees > a.unknown_frees);
        printf("ALLOCATION_PROFILE_EXHAUSTION_PASS capacity=%llu dropped=%llu unknown_frees=%llu\n",
            (unsigned long long)b.table_capacity, (unsigned long long)b.dropped_allocations, (unsigned long long)b.unknown_frees); return 0;
    }
    assert(!snapshot(&a, sizeof(a), 1));
    void* p = malloc(17); assert(p && malloc_usable_size(p) >= 17); memset(p, 0x5a, 17);
    unsigned char* c = calloc(3, 11); assert(c); for (size_t i = 0; i < 33; ++i) assert(c[i] == 0);
    p = realloc(p, 55); assert(p); for (size_t i = 0; i < 17; ++i) assert(((unsigned char*)p)[i] == 0x5a);
    void* aligned = aligned_alloc(64, 128); assert(aligned && !((uintptr_t)aligned % 64));
    void* posix = NULL; errno = E2BIG; assert(posix_memalign(&posix, 128, 96) == 0 && errno == E2BIG && !((uintptr_t)posix % 128));
    void* m = memalign(32, 77); assert(m && !((uintptr_t)m % 32));
    size_t page = (size_t)sysconf(_SC_PAGESIZE), rounded = (4111 + page - 1) / page * page;
    void* v = valloc(19); assert(v && !((uintptr_t)v % page));
    void* pv = pvalloc(4111); assert(pv && !((uintptr_t)pv % page) && malloc_usable_size(pv) >= rounded);
    volatile size_t huge = SIZE_MAX / 2;
    errno = 0; assert(realloc(p, huge) == NULL && errno == ENOMEM);
    volatile size_t overflow = SIZE_MAX;
    errno = 0; assert(calloc(overflow, 2) == NULL && errno == ENOMEM);
    void* unchanged = (void*)1; errno = E2BIG;
    assert(posix_memalign(&unchanged, 3, 25) == EINVAL && unchanged == (void*)1 && errno == E2BIG);
    assert(((unsigned char*)p)[0] == 0x5a);
    assert(!snapshot(&b, sizeof(b), 0));
    assert(b.malloc_calls - a.malloc_calls == 1 && b.calloc_calls - a.calloc_calls == 2);
    assert(b.realloc_calls - a.realloc_calls == 2 && b.aligned_calls - a.aligned_calls == 6);
    assert(b.successful_allocations - a.successful_allocations == 8 && b.failed_allocations - a.failed_allocations == 3);
    assert(b.allocated_requested_bytes - a.allocated_requested_bytes == 17 + 33 + 55 + 128 + 96 + 77 + 19 + rounded);
    assert(b.released_requested_bytes - a.released_requested_bytes == 17 && b.deallocations - a.deallocations == 1);
    assert(b.live_allocations - a.live_allocations == 7 && b.live_requested_bytes - a.live_requested_bytes == 55 + 33 + 128 + 96 + 77 + 19 + rounded);
    errno = E2BIG; free(p); free(c); free(aligned); free(posix); free(m); free(v); free(pv); assert(errno == E2BIG);
    assert(!snapshot(&b, sizeof(b), 0)); assert(b.live_requested_bytes == a.live_requested_bytes && b.live_allocations == a.live_allocations);
    assert(b.allocated_requested_bytes - a.allocated_requested_bytes == b.released_requested_bytes - a.released_requested_bytes);
    void* zero = realloc(NULL, 0); assert(zero); free(zero);
    zero = calloc(0, 123); assert(zero); free(zero);
    zero = malloc(23); assert(zero); assert(realloc(zero, 0) == NULL);
    typedef void (*sized_free_fn)(void*, size_t);
    typedef void (*aligned_free_fn)(void*, size_t, size_t);
    typedef void (*plain_free_fn)(void*);
    sized_free_fn sized_free = (sized_free_fn)dlsym(RTLD_DEFAULT, "free_sized");
    aligned_free_fn aligned_free = (aligned_free_fn)dlsym(RTLD_DEFAULT, "free_aligned_sized");
    plain_free_fn old_free = (plain_free_fn)dlsym(RTLD_DEFAULT, "cfree");
    assert(sized_free && aligned_free && old_free);
    sized_free(malloc(27), 27); aligned_free(aligned_alloc(64, 64), 64, 64); old_free(malloc(29));
    assert(!snapshot(&b, sizeof(b), 0)); assert(b.live_requested_bytes == a.live_requested_bytes && b.live_allocations == a.live_allocations);
    assert(!b.dropped_allocations && !b.duplicate_addresses && !b.initialization_failures && !b.unknown_frees && !b.unknown_reallocs);
    pthread_t workers[4]; pthread_barrier_init(&ready, NULL, 5); pthread_barrier_init(&start, NULL, 5);
    for (size_t i = 0; i < 4; ++i) assert(!pthread_create(&workers[i], NULL, concurrent, (void*)i));
    pthread_barrier_wait(&ready); assert(!snapshot(&a, sizeof(a), 1)); pthread_barrier_wait(&start);
    for (size_t i = 0; i < 4; ++i) assert(!pthread_join(workers[i], NULL));
    assert(!snapshot(&b, sizeof(b), 0)); assert(b.malloc_calls - a.malloc_calls >= 40000 && b.deallocations - a.deallocations >= 40000);
    assert(!b.dropped_allocations && !b.duplicate_addresses && !b.initialization_failures && !b.unknown_frees && !b.unknown_reallocs);
    printf("ALLOCATION_PROFILE_SELF_TEST_PASS known-size accounting, realloc failure/zero, calloc overflow/zero, all alignment/free variants, errno, 40000 threaded allocations; capacity=%llu metadata_bytes=%llu\n",
        (unsigned long long)b.table_capacity, (unsigned long long)b.table_bytes);
    return 0;
}
