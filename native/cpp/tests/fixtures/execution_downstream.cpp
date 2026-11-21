#include "execution_runtime.hpp"
#include "support.hpp"

NW_FIXTURE_API std::uint64_t nw_execution_link_probe_v1()
{
    nw::fixtures::execution::Executor executor({1, 1, 0, 0, 0, 0});
    return executor.join();
}
