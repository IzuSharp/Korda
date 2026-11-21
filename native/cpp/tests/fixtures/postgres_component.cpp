// SPDX-License-Identifier: MIT
#include "support.hpp"
NW_FIXTURE_API nw_status NW_CDECL nw_postgres_component_get_abi_v1(std::uint32_t major, std::uint32_t capacity,
                                                                   nw_abi_info_v1* output) noexcept
{
    return nw::interop::write_abi(major, capacity, output, 0, 1);
}
