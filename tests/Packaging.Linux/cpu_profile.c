#include <cpuid.h>
#include <stdbool.h>
#include <stdio.h>

#if !defined(__x86_64__)
#error This probe implements the declared Linux x64 CPU profile only.
#endif

static const char* json_bool(bool value)
{
    return value ? "true" : "false";
}

int main(void)
{
    unsigned int eax = 0, ebx = 0, ecx = 0, edx = 0;
    unsigned int leaf1_ecx = 0, leaf1_edx = 0, leaf7_ebx = 0;
    unsigned int extended_ecx = 0, extended_edx = 0;
    if (__get_cpuid_max(0, NULL) >= 1)
    {
        __cpuid_count(1, 0, eax, ebx, ecx, edx);
        leaf1_ecx = ecx;
        leaf1_edx = edx;
    }
    if (__get_cpuid_max(0, NULL) >= 7)
    {
        __cpuid_count(7, 0, eax, ebx, ecx, edx);
        leaf7_ebx = ebx;
    }
    if (__get_cpuid_max(0x80000000u, NULL) >= 0x80000001u)
    {
        __cpuid_count(0x80000001u, 0, eax, ebx, ecx, edx);
        extended_ecx = ecx;
        extended_edx = edx;
    }
    /* x86-64-v2 additions: SSE3, SSSE3, CX16, SSE4.1, SSE4.2,
       POPCNT and LAHF/SAHF in long mode, on the x86-64 baseline. */
    const unsigned int baseline =
        (1u << 0) | (1u << 8) | (1u << 15) | (1u << 23) | (1u << 24) | (1u << 25) | (1u << 26);
    const unsigned int v2 = (1u << 0) | (1u << 9) | (1u << 13) | (1u << 19) | (1u << 20) | (1u << 23);
    bool x64 = (leaf1_edx & baseline) == baseline && (extended_edx & (1u << 29)) != 0;
    bool accepted_v2 = x64 && (leaf1_ecx & v2) == v2 && (extended_ecx & 1u) != 0;
    bool avx = (leaf1_ecx & (1u << 28)) != 0;
    bool avx2 = (leaf7_ebx & (1u << 5)) != 0;
    bool avx512f = (leaf7_ebx & (1u << 16)) != 0;
    bool fma = (leaf1_ecx & (1u << 12)) != 0;
    printf("{\"x86_64\":%s,\"x86_64_v2\":%s,\"sse3\":%s,"
           "\"ssse3\":%s,\"cx16\":%s,\"sse41\":%s,\"sse42\":%s,"
           "\"popcnt\":%s,\"lahf_sahf_long_mode\":%s,\"avx\":%s,"
           "\"avx2\":%s,\"avx512f\":%s,\"fma\":%s,"
           "\"leaf1_ecx\":\"%08x\",\"leaf1_edx\":\"%08x\","
           "\"leaf7_ebx\":\"%08x\",\"extended_ecx\":\"%08x\","
           "\"extended_edx\":\"%08x\"}\n",
           json_bool(x64), json_bool(accepted_v2), json_bool((leaf1_ecx & 1u) != 0),
           json_bool((leaf1_ecx & (1u << 9)) != 0), json_bool((leaf1_ecx & (1u << 13)) != 0),
           json_bool((leaf1_ecx & (1u << 19)) != 0), json_bool((leaf1_ecx & (1u << 20)) != 0),
           json_bool((leaf1_ecx & (1u << 23)) != 0), json_bool((extended_ecx & 1u) != 0), json_bool(avx),
           json_bool(avx2), json_bool(avx512f), json_bool(fma), leaf1_ecx, leaf1_edx, leaf7_ebx, extended_ecx,
           extended_edx);
    return accepted_v2 && !avx && !avx2 && !avx512f && !fma ? 0 : 77;
}
