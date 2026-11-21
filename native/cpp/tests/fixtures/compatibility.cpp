// SPDX-License-Identifier: MIT
// Controlled old/new ABI profiles share the actual Interop implementation.
// These test-only generations are neither released Engines nor a Rust baseline.
#include "support.hpp"

#ifndef NW_COMPAT_PROFILE
#define NW_COMPAT_PROFILE 0
#endif
namespace
{
std::atomic<std::uint32_t> calls{0};
constexpr std::uint32_t transform(std::uint32_t value) noexcept
{
    return value + (NW_COMPAT_PROFILE == 4 ? 8U : 7U);
}
} // namespace
NW_FIXTURE_API nw_status NW_CDECL nw_compat_get_abi_v1(std::uint32_t major, std::uint32_t capacity,
                                                       nw_abi_info_v1* output) noexcept
{
    constexpr bool additive = NW_COMPAT_PROFILE == 1;
    const auto status = nw::interop::write_abi(major, capacity, output, additive ? 1 : 0, additive ? 3 : 1);
    if (status == NW_OK)
    {
        if constexpr (NW_COMPAT_PROFILE == 2)
            output->abi_major = 2;
        if constexpr (NW_COMPAT_PROFILE == 3)
            output->struct_size = 24;
    }
    return status;
}
NW_FIXTURE_API std::uint32_t NW_CDECL nw_compat_transform(std::uint32_t value) noexcept
{
    calls.fetch_add(1, std::memory_order_relaxed);
    return transform(value);
}
#if NW_COMPAT_PROFILE == 1
NW_FIXTURE_API std::uint32_t NW_CDECL nw_compat_optional(std::uint32_t value) noexcept
{
    calls.fetch_add(1, std::memory_order_relaxed);
    return value * 2U;
}
#endif
NW_FIXTURE_API std::uint32_t NW_CDECL nw_compat_calls() noexcept
{
    return calls.load(std::memory_order_relaxed);
}
