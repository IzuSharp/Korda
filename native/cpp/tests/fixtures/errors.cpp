#include "support.hpp"

namespace
{
struct Fixture
{
    nw::interop::EngineBoundary boundary;
};
} // namespace

NW_FIXTURE_API nw_status NW_CDECL nw_errors_create(Fixture** output) noexcept
{
    nw::interop::EngineBoundary boundary;
    return nw::interop::create_owner(boundary, output, [] { return std::make_unique<Fixture>(); });
}
NW_FIXTURE_API nw_status NW_CDECL nw_errors_run(const Fixture* pointer, std::uint32_t mode,
                                                nw_error_info_v1* error) noexcept
{
    if (pointer == nullptr)
    {
        return NW_INVALID_ARGUMENT;
    }
    auto* fixture = const_cast<Fixture*>(pointer);
    nw::ErrorFields fields{};
    const auto status = fixture->boundary.business(
        [&]
        {
            switch (mode)
            {
                case 0:
                    return NW_OK;
                case 1:
                    throw nw::interop::Failure(NW_DOMAIN_ERROR, {7, -42, 73});
                case 2:
                    throw std::runtime_error("controlled native exception");
                default:
                    throw nw::interop::Failure(NW_CONTRACT_VIOLATION);
            }
        },
        &fields);
    return nw::interop::write_error(status, error, fields);
}
NW_FIXTURE_API nw_status NW_CDECL nw_errors_release(Fixture* pointer) noexcept
{
    if (pointer == nullptr)
    {
        return NW_OK;
    }
    const std::unique_ptr<Fixture> fixture(pointer);
    return fixture->boundary.cleanup([] { return NW_OK; });
}
NW_FIXTURE_API std::uint32_t NW_CDECL nw_errors_notify(nw_status(NW_CDECL* callback)(std::uintptr_t, std::uint32_t),
                                                       std::uintptr_t cookie, std::uint32_t original,
                                                       std::uint32_t* bridge) noexcept
{
    if (bridge == nullptr)
    {
        return NW_INVALID_ARGUMENT;
    }
    *bridge = NW_OK;
    if (callback != nullptr)
    {
        try
        {
            *bridge = callback(cookie, original);
        }
        catch (...)
        {
            *bridge = NW_CALLBACK_FAILED;
        }
    }
    return original;
}
NW_FIXTURE_API std::uint32_t NW_CDECL nw_errors_invalid_uco(std::uint32_t(NW_CDECL* callback)(std::uint8_t))
{
    return callback(1);
}
