#include "support.hpp"

namespace
{
std::atomic<std::uint64_t> live{0}, releases{0}, states{0}, state_releases{0}, next_id{1}, calls{0};
nw::interop::EngineBoundary boundary;
struct Parent
{
    std::uint64_t value;
    explicit Parent(std::uint64_t input) : value(input)
    {
        ++states;
    }
    ~Parent()
    {
        --states;
        ++state_releases;
    }
};
struct Owner
{
    std::shared_ptr<Parent> state;
    std::uint64_t id;
    explicit Owner(std::shared_ptr<Parent> value) : state(std::move(value)), id(next_id++)
    {
        ++live;
    }
    ~Owner()
    {
        --live;
        ++releases;
    }
};
} // namespace

NW_FIXTURE_API nw_status NW_CDECL nw_lifetime_fixture_create_v1(std::uint64_t value, Owner** output) noexcept
{
    return nw::interop::create_owner(boundary, output,
                                     [&] { return std::make_unique<Owner>(std::make_shared<Parent>(value)); });
}
NW_FIXTURE_API nw_status NW_CDECL nw_lifetime_fixture_child_v1(const Owner* parent, std::uint32_t fail,
                                                               Owner** output) noexcept
{
    if (parent == nullptr)
    {
        return NW_INVALID_ARGUMENT;
    }
    const auto state = parent->state;
    return nw::interop::create_owner(boundary, output,
                                     [&]
                                     {
                                         if (fail != 0)
                                         {
                                             throw nw::interop::Failure(NW_OUT_OF_MEMORY);
                                         }
                                         return std::make_unique<Owner>(state);
                                     });
}
NW_FIXTURE_API nw_status NW_CDECL nw_lifetime_fixture_read_v1(const Owner* owner, std::uint64_t* value,
                                                              std::uint64_t* id) noexcept
{
    if (owner == nullptr || value == nullptr || id == nullptr)
    {
        return NW_INVALID_ARGUMENT;
    }
    *value = owner->state->value;
    *id = owner->id;
    ++calls;
    return NW_OK;
}
NW_FIXTURE_API void NW_CDECL nw_lifetime_fixture_release_v1(Owner* owner) noexcept
{
    delete owner;
}
NW_FIXTURE_API std::uint64_t NW_CDECL nw_lifetime_fixture_counter_v1(std::uint32_t kind) noexcept
{
    switch (kind)
    {
        case 0:
            return live;
        case 1:
            return releases;
        case 2:
            return states;
        case 3:
            return state_releases;
        case 4:
            return calls;
        default:
            return UINT64_MAX;
    }
}
