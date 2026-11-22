#include "support.hpp"

#include <system_error>
#include <thread>

namespace
{
using BufferOwner = std::unique_ptr<nw::fixtures::MemoryBuffer>;
using AsyncOperation = nw::Operation<BufferOwner>;
nw::interop::EngineBoundary boundary;
std::atomic<std::uint64_t> live{0}, created{0}, released{0}, bad_release{0}, input_bytes{0};
struct Fixture
{
    std::shared_ptr<AsyncOperation> operation = std::make_shared<AsyncOperation>();
    nw::Bytes input;
    std::uint32_t mode;
    std::atomic<bool> worker_started{false};
    Fixture(nw::Bytes bytes, std::uint32_t value) : input(std::move(bytes)), mode(value)
    {
        ++live;
        ++created;
        input_bytes += input.size();
    }
    ~Fixture()
    {
        input_bytes -= input.size();
        --live;
        ++released;
    }
};
nw_status complete(Fixture& fixture, nw_status status) noexcept
{
    return boundary.cleanup(
        [&]
        {
            std::optional<BufferOwner> result;
            if (status == NW_OK)
            {
                result.emplace(nw::fixtures::copy_buffer(fixture.input.data(), fixture.input.size(), 0, 0));
            }
            return fixture.operation->complete(status, std::move(result), {17, -71, 123});
        });
}
} // namespace

NW_FIXTURE_API nw_status NW_CDECL nw_async_fixture_create_v1(std::uint32_t mode, const std::uint8_t* data,
                                                             std::uint64_t length, std::uint32_t fail,
                                                             Fixture** output) noexcept
{
    return nw::interop::create_owner(boundary, output,
                                     [&]
                                     {
                                         if (length > 1024 || (length != 0 && data == nullptr) || fail == 1)
                                         {
                                             throw nw::interop::Failure(NW_INVALID_ARGUMENT);
                                         }
                                         nw::Bytes input;
                                         input.reserve(static_cast<std::size_t>(length));
                                         if (length != 0)
                                         {
                                             input.insert(input.end(), data, data + length);
                                         }
                                         auto fixture = std::make_unique<Fixture>(std::move(input), mode);
                                         if (fail == 2)
                                         {
                                             throw nw::interop::Failure(NW_OUT_OF_MEMORY);
                                         }
                                         return fixture;
                                     });
}
NW_FIXTURE_API nw_status NW_CDECL nw_async_fixture_start_v1(Fixture* fixture,
                                                            const nw_operation_callbacks_v1* table) noexcept
{
    if (fixture == nullptr || !nw::fixtures::callback_table(table))
    {
        return NW_INVALID_ARGUMENT;
    }
    if ((fixture->mode & 1U) != 0)
    {
        return NW_INVALID_ARGUMENT;
    }
    const auto status = fixture->operation->start(*table);
    if (status != NW_OK)
    {
        return status;
    }
    if ((fixture->mode & 2U) != 0)
    {
        static_cast<void>(complete(*fixture, NW_OK));
    }
    return (fixture->mode & 8U) != 0 ? NW_INVALID_ARGUMENT : NW_OK;
}
NW_FIXTURE_API nw_status NW_CDECL nw_async_fixture_cancel_v1(Fixture* fixture) noexcept
{
    if (fixture == nullptr)
    {
        return NW_INVALID_ARGUMENT;
    }
    static_cast<void>(fixture->operation->request_cancel());
    if ((fixture->mode & 4U) != 0 && fixture->operation->snapshot().phase == nw::OperationPhase::Running)
    {
        static_cast<void>(complete(*fixture, NW_CANCELED));
    }
    return NW_OK;
}
NW_FIXTURE_API nw_status NW_CDECL nw_async_fixture_complete_v1(Fixture* fixture, nw_status status) noexcept
{
    return fixture == nullptr ? NW_INVALID_ARGUMENT : complete(*fixture, status);
}
NW_FIXTURE_API nw_status NW_CDECL nw_async_fixture_spawn_v1(Fixture* fixture, nw_status terminal) noexcept
{
    if (fixture == nullptr)
    {
        return NW_INVALID_ARGUMENT;
    }
    if (fixture->worker_started.exchange(true))
    {
        return NW_INVALID_STATE;
    }
    auto [status, source] = fixture->operation->reserve_source();
    if (status != NW_OK)
    {
        return status;
    }
    try
    {
        std::thread(
            [fixture, terminal, lease = std::move(source)]() mutable
            {
                static_cast<void>(complete(*fixture, terminal));
                lease.reset();
            })
            .detach();
        return NW_OK;
    }
    catch (const std::system_error&)
    {
        return NW_OUT_OF_MEMORY;
    }
    catch (const std::bad_alloc&)
    {
        return NW_OUT_OF_MEMORY;
    }
}
NW_FIXTURE_API nw_status NW_CDECL nw_async_fixture_take_v1(Fixture* fixture,
                                                           nw::fixtures::MemoryBuffer** output) noexcept
{
    if (!nw::fixtures::aligned(output))
    {
        return NW_INVALID_ARGUMENT;
    }
    *output = nullptr;
    if (fixture == nullptr)
    {
        return NW_INVALID_ARGUMENT;
    }
    if ((fixture->mode & 16U) != 0)
    {
        return NW_OUT_OF_MEMORY;
    }
    auto [status, value] = fixture->operation->take_result();
    if (status == NW_OK)
    {
        *output = value->release();
    }
    return status;
}
NW_FIXTURE_API nw_status NW_CDECL nw_async_fixture_view_v1(const nw::fixtures::MemoryBuffer* buffer,
                                                           const std::uint8_t** address, std::uint64_t* length) noexcept
{
    std::uint64_t capacity = 0;
    return nw::fixtures::view_buffer(buffer, address, length, &capacity);
}
NW_FIXTURE_API void NW_CDECL nw_async_fixture_buffer_release_v1(nw::fixtures::MemoryBuffer* buffer) noexcept
{
    delete buffer;
}
NW_FIXTURE_API nw_status NW_CDECL nw_async_fixture_retire_v1(Fixture* fixture) noexcept
{
    if (fixture == nullptr)
    {
        return NW_INVALID_ARGUMENT;
    }
    if ((fixture->mode & 32U) != 0)
    {
        return NW_INVALID_STATE;
    }
    return fixture->operation->try_retire();
}
NW_FIXTURE_API nw_status NW_CDECL nw_async_fixture_source_create_v1(Fixture* fixture, void** output) noexcept
{
    if (!nw::fixtures::aligned(output))
    {
        return NW_INVALID_ARGUMENT;
    }
    *output = nullptr;
    if (fixture == nullptr)
    {
        return NW_INVALID_ARGUMENT;
    }
    return boundary.cleanup(
        [&]
        {
            auto [status, lease] = fixture->operation->reserve_source();
            if (status != NW_OK)
            {
                return status;
            }
            auto source = std::make_unique<nw::WorkLease<BufferOwner>>(std::move(lease));
            *output = source.release();
            return NW_OK;
        });
}
NW_FIXTURE_API void NW_CDECL nw_async_fixture_source_release_v1(void* source) noexcept
{
    delete static_cast<nw::WorkLease<BufferOwner>*>(source);
}
NW_FIXTURE_API nw_status NW_CDECL nw_async_fixture_duplicate_notice_v1(Fixture* fixture) noexcept
{
    return fixture == nullptr ? NW_INVALID_ARGUMENT : nw::interop::FixtureAccess::duplicate_notice(*fixture->operation);
}
NW_FIXTURE_API void NW_CDECL nw_async_fixture_release_v1(Fixture* fixture) noexcept
{
    if (fixture == nullptr)
    {
        return;
    }
    if (!fixture->operation->can_release())
    {
        ++bad_release;
        return;
    }
    delete fixture;
}
NW_FIXTURE_API std::uint64_t NW_CDECL nw_async_fixture_count_v1(std::uint32_t kind) noexcept
{
    switch (kind)
    {
        case 0:
            return live;
        case 1:
            return created;
        case 2:
            return released;
        case 3:
            return bad_release;
        case 4:
            return input_bytes;
        case 5:
            return nw::fixtures::memory_counter(0);
        case 6:
            return nw::fixtures::memory_counter(4);
        default:
            return UINT64_MAX;
    }
}
