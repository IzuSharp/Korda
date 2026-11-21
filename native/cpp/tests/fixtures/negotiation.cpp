#include "support.hpp"

namespace
{
std::atomic<std::uint32_t> bootstrap_calls{0};
nw_status bootstrap(std::uint32_t major, std::uint32_t capacity, nw_abi_info_v1* output) noexcept
{
    ++bootstrap_calls;
#if defined(minor_zero)
    constexpr std::uint32_t minor = 0;
#else
    constexpr std::uint32_t minor = 3;
#endif
#if defined(fixture_beta)
    constexpr std::uint64_t capabilities = 2;
#else
    constexpr std::uint64_t capabilities = UINT64_C(0x8000000000000001);
#endif
    const auto status = nw::interop::write_abi(major, capacity, output, minor, capabilities);
    if (status != NW_OK)
    {
        return status;
    }
#if defined(bad_size)
    output->struct_size = 24;
#endif
#if defined(future_size)
    output->struct_size = 48;
#endif
#if defined(bad_major)
    output->abi_major = 2;
#endif
#if defined(bad_pointer)
    output->pointer_width_bits = 32;
#endif
#if defined(bad_reserved)
    output->reserved = 1;
#endif
#if defined(bad_pending)
    return NW_PENDING;
#elif defined(bad_unknown)
    return UINT32_MAX;
#else
    return status;
#endif
}
} // namespace
#if defined(fixture_beta)
NW_FIXTURE_API nw_status NW_CDECL nw_a02_beta_get_abi_v1(std::uint32_t major, std::uint32_t capacity,
                                                         nw_abi_info_v1* output) noexcept
{
    return bootstrap(major, capacity, output);
}
NW_FIXTURE_API std::uint64_t NW_CDECL nw_a02_beta_probe(std::uint64_t input) noexcept
{
    return input ^ UINT64_C(0xb203);
}
#else
NW_FIXTURE_API nw_status NW_CDECL nw_a02_alpha_get_abi_v1(std::uint32_t major, std::uint32_t capacity,
                                                          nw_abi_info_v1* output) noexcept
{
    return bootstrap(major, capacity, output);
}
NW_FIXTURE_API std::uint32_t NW_CDECL nw_a02_alpha_bootstrap_calls() noexcept
{
    return bootstrap_calls;
}
NW_FIXTURE_API std::uint64_t NW_CDECL nw_a02_alpha_probe(std::uint64_t input) noexcept
{
    return input ^ UINT64_C(0xa102);
}
#endif
