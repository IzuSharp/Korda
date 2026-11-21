// SPDX-License-Identifier: MIT
#include <cstdint>
#include <iostream>
#include <limits>
extern "C" std::uint32_t nw_compat_transform(std::uint32_t value) noexcept;
int main()
{
    if (nw_compat_transform(35) != 42 || nw_compat_transform(std::numeric_limits<std::uint32_t>::max()) != 6)
    {
        std::cerr << "FAILED: controlled native semantics changed\n";
        return 1;
    }
    std::cout << "C++ direct oracle passed\n";
    return 0;
}
