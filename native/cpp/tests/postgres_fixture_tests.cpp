// SPDX-License-Identifier: MIT
#include <cstdint>
extern "C" std::uint32_t nw_postgres_fixture_run_v1() noexcept;
int main()
{
    return static_cast<int>(nw_postgres_fixture_run_v1());
}
