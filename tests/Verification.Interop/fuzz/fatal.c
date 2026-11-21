/* Deliberately invalid memory/ownership is never passed to the ordinary fuzzer. */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
extern uint32_t nw_memory_fixture_copy_v1(const uint8_t*, uint64_t, uint64_t, uint32_t, void**);
extern void nw_memory_fixture_release_v1(void*);
int main(int argc, char** argv)
{
    if (argc != 2)
        return 2;
    if (strcmp(argv[1], "invalid-pointer") == 0)
    {
        fputs("EXPECTED_FATAL actual-Core-copy-from-invalid-address\n", stderr);
        fflush(stderr);
        void* owner = NULL;
        (void)nw_memory_fixture_copy_v1((const uint8_t*)(uintptr_t)1, 16, 0, 0, &owner);
        return 3;
    }
    if (strcmp(argv[1], "wrong-release") == 0)
    {
        void* owner = NULL;
        const uint8_t bytes[] = {1, 2, 3};
        if (nw_memory_fixture_copy_v1(bytes, sizeof(bytes), 0, 0, &owner) != 0 || owner == NULL)
            return 4;
        nw_memory_fixture_release_v1(owner);
        fputs("EXPECTED_FATAL wrong-creator-free-after-owner-return\n", stderr);
        fflush(stderr);
        free(owner); /* The exact creator already consumed this ownership. */
        return 5;
    }
    return 6;
}
