#include "support.hpp"

NW_FIXTURE_API std::uint32_t nw_async_link_probe_v1()
{
    nw::Operation<std::uint32_t> operation;
    (void)operation.request_cancel();
    return static_cast<std::uint32_t>(operation.snapshot().phase);
}
