#include "nw/interop.hpp"

#include <cstdlib>
#include <iostream>
#include <stdexcept>

namespace
{
struct Dangerous
{
    ~Dangerous() noexcept
    {
        std::abort();
    }
};
void throwing_body()
{
    throw std::runtime_error("exception escaped a noexcept ABI boundary");
}
void illegal_escape() noexcept
{
    throwing_body();
}
} // namespace

int main(int argc, char** argv)
{
    const std::string_view mode = argc > 1 ? argv[1] : "";
    if (mode == "drop")
    {
        try
        {
            throw Dangerous{};
        }
        catch (...)
        {
        }
        return 2;
    }
    if (mode == "double")
    {
        try
        {
            const Dangerous dangerous;
            throw std::runtime_error("unwind while destructor terminates");
        }
        catch (...)
        {
            return 2;
        }
    }
    if (mode == "ffi")
    {
        illegal_escape();
        return 2;
    }
    if (mode == "hook")
    {
        std::set_terminate([] { throw std::runtime_error("termination handler cannot recover"); });
        illegal_escape();
        return 2;
    }
    if (mode == "abort")
    {
        std::abort();
    }
    if (mode != "ordinary" && mode != "quarantine")
    {
        return 2;
    }
    nw::interop::EngineBoundary boundary;
    const auto status = boundary.business(
        [&]() -> nw_status
        {
            if (mode == "quarantine")
            {
                throw Dangerous{};
            }
            throw std::runtime_error("controlled native exception");
        });
    std::cout << "status=" << status << " poisoned=" << std::boolalpha << boundary.is_poisoned()
              << " quarantined=" << boundary.quarantined_payloads() << " active=" << boundary.active_calls() << '\n';
}
