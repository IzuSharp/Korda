/* A20 corpus schema v1. Coverage/ASan/UBSan instrument this C decoder and oracle.
 * Actual Rust Core libraries are stable-toolchain optimized, NOT sanitizer-instrumented.
 * Every address dereferenced by ordinary fuzzing belongs to a live local allocation.
 */
#include "nativeweave_abi_v1.h"
#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

extern uint32_t nw_a20_extent_v1(uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint32_t,
                                 uint64_t*);
extern uint32_t nw_a20_utf8_v1(const uint8_t*, uint64_t, uint32_t, uint8_t*, uint32_t, uint64_t*);
extern uint32_t nw_a20_error_v1(uint32_t, nw_error_info_v1*, uint32_t, int32_t, uint64_t);
extern uint32_t nw_memory_fixture_copy_v1(const uint8_t*, uint64_t, uint64_t, uint32_t, void**);
extern uint32_t nw_memory_fixture_view_v1(const void*, const uint8_t**, uint64_t*, uint64_t*);
extern void nw_memory_fixture_release_v1(void*);
extern uint64_t nw_memory_fixture_counter_v1(uint32_t);

static uint64_t le64(const uint8_t* p)
{
    uint64_t v = 0;
    for (unsigned i = 0; i < 8; i++)
        v |= (uint64_t)p[i] << (8 * i);
    return v;
}
static bool scalar(const uint64_t d[7], uint32_t a)
{
    __uint128_t first = (__uint128_t)d[0] + d[4];
    __uint128_t end = first + d[1];
    return d[3] != 0 && a != 0 && (a & (a - 1)) == 0 && d[1] <= INT64_MAX && d[1] <= d[6] &&
           (__uint128_t)d[2] * d[3] == d[1] && (__uint128_t)d[4] + d[1] <= d[5] && first <= UINT64_MAX &&
           end <= UINT64_MAX && first % a == 0 && (d[0] != 0 || (d[1] == 0 && d[4] == 0));
}
/* Independent Unicode scalar decoder, compared with Rust std::str and D08 truncation. */
static bool utf8(const uint8_t* p, size_t n, size_t limit, size_t* prefix)
{
    *prefix = 0;
    size_t i = 0;
    while (i < n)
    {
        uint32_t value = p[i++];
        unsigned need = 0;
        uint32_t minimum = 0;
        if (value < 0x80)
        {
        }
        else if (value >= 0xc2 && value <= 0xdf)
        {
            need = 1;
            minimum = 0x80;
            value &= 0x1f;
        }
        else if (value >= 0xe0 && value <= 0xef)
        {
            need = 2;
            minimum = 0x800;
            value &= 0x0f;
        }
        else if (value >= 0xf0 && value <= 0xf4)
        {
            need = 3;
            minimum = 0x10000;
            value &= 7;
        }
        else
            return false;
        if (n - i < need)
            return false;
        for (unsigned j = 0; j < need; j++)
        {
            if ((p[i] & 0xc0) != 0x80)
                return false;
            value = (value << 6) | (p[i++] & 0x3f);
        }
        if (value < minimum || value > 0x10ffff || (value >= 0xd800 && value <= 0xdfff))
            return false;
        if (i <= limit)
            *prefix = i;
    }
    return true;
}
int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size)
{
    if (size == 0 || size > 256)
        return 0;
    switch (data[0] % 4)
    {
        case 0:
        {
            if (size < 61)
                return 0;
            uint64_t d[7];
            for (unsigned i = 0; i < 7; i++)
                d[i] = le64(data + 1 + i * 8);
            uint32_t alignment =
                (uint32_t)data[57] | (uint32_t)data[58] << 8 | (uint32_t)data[59] << 16 | (uint32_t)data[60] << 24;
            bool expected = scalar(d, alignment);
#ifdef A20_ORACLE_NEGATIVE
            /* Deliberate oracle defect, only in a separate expected-failing binary.
             * Removing the address-end guard accepts a wrapped range. */
            if (d[0] == UINT64_MAX && d[1] == 1 && d[2] == 1 && d[3] == 1 && d[4] == 0 && d[5] == 1 && d[6] == 1 &&
                alignment == 1)
                expected = true;
#endif
            uint64_t start = UINT64_MAX;
            uint32_t status = nw_a20_extent_v1(d[0], d[1], d[2], d[3], d[4], d[5], d[6], alignment, &start);
            assert(status == (expected ? NW_OK : NW_INVALID_ARGUMENT));
            assert(start == (expected ? d[0] + d[4] : 0));
            break;
        }
        case 1:
        {
            if (size < 3)
                return 0;
            size_t n = size - 3, prefix = 0;
            uint32_t limit = data[1], capacity = data[2];
            bool valid = utf8(data + 3, n, limit, &prefix);
            uint8_t output[512];
            for (size_t i = 0; i < sizeof(output); i++)
                output[i] = 0xa5;
            uint64_t written = UINT64_MAX;
            uint32_t status = nw_a20_utf8_v1(data + 3, n, limit, output, capacity, &written);
            uint32_t expected = !valid ? NW_INVALID_ARGUMENT : prefix > capacity ? NW_BUFFER_TOO_SMALL : NW_OK;
            assert(status == expected);
            assert(written == (expected == NW_OK ? prefix : 0));
            if (expected == NW_OK)
                assert(memcmp(output, data + 3, prefix) == 0);
            for (size_t i = written; i < sizeof(output); i++)
                assert(output[i] == 0xa5);
            break;
        }
        case 2:
        {
            if (size < 10)
                return 0;
            struct error_buffer
            {
                nw_error_info_v1 record;
                uint8_t tail[40];
            } original;
            _Static_assert(sizeof(nw_error_info_v1) == 24 && sizeof(struct error_buffer) == 64, "v1 fixture layout");
            uint8_t* bytes = (uint8_t*)&original;
            for (size_t i = 0; i < sizeof(original); i++)
                bytes[i] = 0xa5;
            uint32_t capacity = data[1] % 65, status = (uint32_t)le64(data + 2);
            original.record.struct_size = capacity;
            struct error_buffer output = original;
            assert(nw_a20_error_v1(status, &output.record, 17, -71, 123) == status);
            if (status > 1 && capacity >= sizeof(output.record))
            {
                assert(output.record.struct_size == 24 && output.record.domain == 17 && output.record.code == -71 &&
                       output.record.flags == 0 && output.record.correlation_id == 123);
                assert(memcmp(output.tail, original.tail, sizeof(output.tail)) == 0);
            }
            else
                assert(memcmp(&output, &original, sizeof(output)) == 0);
            break;
        }
        case 3:
        {
            if (size < 3)
                return 0;
            uint32_t failure = data[1] % 3;
            uint64_t reserve = data[2];
            void* owner = (void*)(uintptr_t)1;
            uint32_t status = nw_memory_fixture_copy_v1(data + 3, size - 3, reserve, failure, &owner);
            assert(status == (failure ? NW_OUT_OF_MEMORY : NW_OK));
            if (failure)
                assert(owner == NULL);
            else
            {
                const uint8_t* view = NULL;
                uint64_t length = 0, capacity = 0;
                assert(nw_memory_fixture_view_v1(owner, &view, &length, &capacity) == NW_OK);
                assert(length == size - 3 && capacity >= length + reserve);
                if (length)
                    assert(memcmp(view, data + 3, (size_t)length) == 0);
                nw_memory_fixture_release_v1(owner);
            }
            assert(nw_memory_fixture_counter_v1(0) == 0 && nw_memory_fixture_counter_v1(2) == 0 &&
                   nw_memory_fixture_counter_v1(4) == 0);
            break;
        }
    }
    return 0;
}
