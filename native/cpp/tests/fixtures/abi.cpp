#include "support.hpp"

NW_FIXTURE_API nw_status NW_CDECL nw_abi_conformance_get_abi_v1(std::uint32_t major, std::uint32_t capacity,
                                                                nw_abi_info_v1* output) noexcept
{
    return nw::interop::write_abi(major, capacity, output, 0, UINT64_C(0x8000000000000001));
}
