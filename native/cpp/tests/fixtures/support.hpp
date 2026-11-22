#ifndef NW_FIXTURE_SUPPORT_HPP
#define NW_FIXTURE_SUPPORT_HPP

#include "nw/interop.hpp"

#include <cstring>
#include <stdexcept>

#if defined(_WIN32)
#define NW_FIXTURE_API extern "C" __declspec(dllexport)
#else
#define NW_FIXTURE_API extern "C" __attribute__((visibility("default")))
#endif

namespace nw::fixtures
{
template <class T> bool aligned(const T* pointer) noexcept
{
    return pointer != nullptr && reinterpret_cast<std::uintptr_t>(pointer) % alignof(T) == 0;
}
template <class T> bool callback_table(const T* table) noexcept
{
    if (!aligned(table))
    {
        return false;
    }
    std::uint32_t capacity = 0;
    std::memcpy(&capacity, table, sizeof(capacity));
    return capacity >= sizeof(T);
}

inline std::atomic<std::uint64_t> memory_owners{0};
inline std::atomic<std::uint64_t> memory_releases{0};
inline std::atomic<std::uint64_t> memory_backings{0};
inline std::atomic<std::uint64_t> memory_backing_releases{0};
inline std::atomic<std::uint64_t> memory_charged{0};

struct MemoryBacking
{
    Bytes bytes;
    explicit MemoryBacking(Bytes value) : bytes(std::move(value))
    {
        ++memory_backings;
        memory_charged += bytes.capacity();
    }
    ~MemoryBacking()
    {
        memory_charged -= bytes.capacity();
        --memory_backings;
        ++memory_backing_releases;
    }
};
struct MemoryBuffer
{
    std::shared_ptr<MemoryBacking> backing;
    std::size_t offset;
    std::size_t length;
    MemoryBuffer(std::shared_ptr<MemoryBacking> value, std::size_t start, std::size_t size)
        : backing(std::move(value)), offset(start), length(size)
    {
        ++memory_owners;
    }
    ~MemoryBuffer()
    {
        --memory_owners;
        ++memory_releases;
    }
};

inline std::unique_ptr<MemoryBuffer> copy_buffer(const std::uint8_t* data, std::uint64_t length, std::uint64_t reserve,
                                                 std::uint32_t fail)
{
    constexpr std::uint64_t MAX = 8 * 1024 * 1024;
    if (!interop::checked_extent(reinterpret_cast<std::uintptr_t>(data), length, length, 1, 0, length, MAX, 1) ||
        reserve > MAX || length > MAX - reserve)
    {
        throw interop::Failure(NW_INVALID_ARGUMENT);
    }
    Bytes bytes;
    try
    {
        bytes.reserve(fail == 1 ? std::numeric_limits<std::size_t>::max() : static_cast<std::size_t>(length + reserve));
    }
    catch (const std::length_error&)
    {
        throw interop::Failure(NW_OUT_OF_MEMORY);
    }
    if (length != 0)
    {
        bytes.insert(bytes.end(), data, data + static_cast<std::size_t>(length));
    }
    auto backing = std::make_shared<MemoryBacking>(std::move(bytes));
    auto owner = std::make_unique<MemoryBuffer>(std::move(backing), 0, static_cast<std::size_t>(length));
    if (fail == 2)
    {
        throw interop::Failure(NW_OUT_OF_MEMORY);
    }
    return owner;
}
inline nw_status view_buffer(const MemoryBuffer* owner, const std::uint8_t** address, std::uint64_t* length,
                             std::uint64_t* capacity) noexcept
{
    if (owner == nullptr || address == nullptr || length == nullptr || capacity == nullptr)
    {
        return NW_INVALID_ARGUMENT;
    }
    *address = owner->length == 0 ? nullptr : owner->backing->bytes.data() + owner->offset;
    *length = owner->length;
    *capacity = owner->backing->bytes.capacity();
    return NW_OK;
}
inline std::uint64_t memory_counter(std::uint32_t kind) noexcept
{
    switch (kind)
    {
        case 0:
            return memory_owners;
        case 1:
            return memory_releases;
        case 2:
            return memory_backings;
        case 3:
            return memory_backing_releases;
        case 4:
            return memory_charged;
        default:
            return UINT64_MAX;
    }
}
} // namespace nw::fixtures

namespace nw::interop
{
struct FixtureAccess
{
    template <class R> static nw_status duplicate_notice(Operation<R>& operation)
    {
        nw_operation_callbacks_v1 callbacks{};
        nw_status status = NW_INVALID_STATE;
        nw_error_info_v1 error{};
        {
            std::lock_guard lock(operation.mutex_);
            if (operation.state_.phase != OperationPhase::TerminalReady || !operation.state_.callbacks)
            {
                return NW_INVALID_STATE;
            }
            callbacks = *operation.state_.callbacks;
            status = *operation.state_.terminal_status;
            error = operation.state_.error;
            ++operation.state_.callbacks_in_flight;
        }
        const typename Operation<R>::CallbackLease lease{operation};
        try
        {
            return callbacks.on_terminal(callbacks.cookie, status, &error);
        }
        catch (...)
        {
            return NW_CALLBACK_FAILED;
        }
    }
};
} // namespace nw::interop
#endif
