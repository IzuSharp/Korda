#include "support.hpp"

#include <condition_variable>

namespace
{
nw::interop::EngineBoundary boundary;
std::atomic<std::uint64_t> created{0}, released{0}, bad_release{0};
struct Fixture
{
    std::shared_ptr<nw::Subscription> subscription = std::make_shared<nw::Subscription>();
    std::uint32_t mode;
    std::mutex source_mutex;
    nw::Subscription::SourceLease held_source;
    std::mutex gate_mutex;
    std::condition_variable gate_changed;
    bool entered = false;
    bool released = false;
    explicit Fixture(std::uint32_t value) : mode(value)
    {
        ++created;
    }
};
} // namespace

NW_FIXTURE_API nw_status NW_CDECL nw_callbacks_fixture_create_v1(std::uint32_t mode, Fixture** output) noexcept
{
    return nw::interop::create_owner(boundary, output, [&] { return std::make_unique<Fixture>(mode); });
}
NW_FIXTURE_API nw_status NW_CDECL nw_callbacks_fixture_start_v1(Fixture* fixture, const nw_callbacks_v1* table) noexcept
{
    if (fixture == nullptr || !nw::fixtures::callback_table(table))
    {
        return NW_INVALID_ARGUMENT;
    }
    if ((fixture->mode & 1U) != 0)
    {
        return NW_INVALID_ARGUMENT;
    }
    const auto status = fixture->subscription->start(*table);
    if (status != NW_OK)
    {
        return status;
    }
    if ((fixture->mode & 2U) != 0)
    {
        constexpr std::string_view text = "synchronous-start";
        static_cast<void>(
            fixture->subscription->emit(42, {reinterpret_cast<const std::uint8_t*>(text.data()), text.size()}));
    }
    return NW_OK;
}
NW_FIXTURE_API nw_status NW_CDECL nw_callbacks_fixture_emit_v1(Fixture* fixture, std::uint32_t kind,
                                                               const std::uint8_t* data, std::uint64_t length) noexcept
{
    if (fixture == nullptr || length > static_cast<std::uint64_t>(PTRDIFF_MAX) || (length != 0 && data == nullptr))
    {
        return NW_INVALID_ARGUMENT;
    }
    return fixture->subscription->emit(kind, {data, static_cast<std::size_t>(length)});
}
NW_FIXTURE_API nw_status NW_CDECL nw_callbacks_fixture_gated_emit_v1(Fixture* fixture) noexcept
{
    if (fixture == nullptr)
    {
        return NW_INVALID_ARGUMENT;
    }
    auto [status, lease] = fixture->subscription->admit_callback();
    if (status != NW_OK)
    {
        return status;
    }
    {
        std::unique_lock lock(fixture->gate_mutex);
        fixture->entered = true;
        fixture->gate_changed.notify_all();
        fixture->gate_changed.wait(lock, [&] { return fixture->released; });
    }
    constexpr std::string_view text = "gated";
    return lease.invoke(7, {reinterpret_cast<const std::uint8_t*>(text.data()), text.size()});
}
NW_FIXTURE_API nw_status NW_CDECL nw_callbacks_fixture_gate_v1(Fixture* fixture, std::uint32_t release) noexcept
{
    if (fixture == nullptr)
    {
        return NW_INVALID_ARGUMENT;
    }
    std::lock_guard lock(fixture->gate_mutex);
    if (release != 0)
    {
        fixture->released = true;
        fixture->gate_changed.notify_all();
    }
    return fixture->entered ? NW_OK : NW_PENDING;
}
NW_FIXTURE_API nw_status NW_CDECL nw_callbacks_fixture_stop_v1(Fixture* fixture) noexcept
{
    return fixture == nullptr ? NW_INVALID_ARGUMENT : fixture->subscription->request_stop();
}
NW_FIXTURE_API nw_status NW_CDECL nw_callbacks_fixture_retire_v1(Fixture* fixture) noexcept
{
    return fixture == nullptr ? NW_INVALID_ARGUMENT : fixture->subscription->try_retire();
}
NW_FIXTURE_API nw_status NW_CDECL nw_callbacks_fixture_source_v1(Fixture* fixture, std::uint32_t hold) noexcept
{
    if (fixture == nullptr)
    {
        return NW_INVALID_ARGUMENT;
    }
    std::lock_guard lock(fixture->source_mutex);
    if (hold == 0)
    {
        fixture->held_source.reset();
        return NW_OK;
    }
    if (fixture->held_source)
    {
        return NW_INVALID_STATE;
    }
    auto [status, lease] = fixture->subscription->reserve_source();
    if (status == NW_OK)
    {
        fixture->held_source = std::move(lease);
    }
    return status;
}
NW_FIXTURE_API void NW_CDECL nw_callbacks_fixture_release_v1(Fixture* fixture) noexcept
{
    if (fixture == nullptr)
    {
        return;
    }
    if (!fixture->subscription->can_release())
    {
        ++bad_release;
        return;
    }
    delete fixture;
    ++released;
}
NW_FIXTURE_API std::uint64_t NW_CDECL nw_callbacks_fixture_count_v1(std::uint32_t kind) noexcept
{
    return kind == 0 ? created.load() : (kind == 1 ? released.load() : bad_release.load());
}
NW_FIXTURE_API std::uint32_t NW_CDECL
nw_callbacks_fixture_invalid_uco_v1(std::uint8_t(NW_CDECL* callback)(std::uint16_t))
{
    return callback(120);
}
