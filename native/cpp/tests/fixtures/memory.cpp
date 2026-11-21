#include "support.hpp"

using nw::fixtures::MemoryBuffer;
namespace
{
nw::interop::EngineBoundary boundary;
}

NW_FIXTURE_API nw_status NW_CDECL nw_memory_fixture_copy_v1(const std::uint8_t* data, std::uint64_t length,
                                                            std::uint64_t reserve, std::uint32_t fail,
                                                            MemoryBuffer** output) noexcept
{
    return nw::interop::create_owner(boundary, output,
                                     [&] { return nw::fixtures::copy_buffer(data, length, reserve, fail); });
}
NW_FIXTURE_API nw_status NW_CDECL nw_memory_fixture_slice_v1(const MemoryBuffer* source, std::uint64_t offset,
                                                             std::uint64_t length, MemoryBuffer** output) noexcept
{
    return nw::interop::create_owner(
        boundary, output,
        [&]
        {
            if (source == nullptr || offset > source->length || length > source->length - offset)
            {
                throw nw::interop::Failure(NW_INVALID_ARGUMENT);
            }
            return std::make_unique<MemoryBuffer>(source->backing, source->offset + static_cast<std::size_t>(offset),
                                                  static_cast<std::size_t>(length));
        });
}
NW_FIXTURE_API nw_status NW_CDECL nw_memory_fixture_view_v1(const MemoryBuffer* owner, const std::uint8_t** address,
                                                            std::uint64_t* length, std::uint64_t* capacity) noexcept
{
    return nw::fixtures::view_buffer(owner, address, length, capacity);
}
NW_FIXTURE_API void NW_CDECL nw_memory_fixture_release_v1(MemoryBuffer* owner) noexcept
{
    delete owner;
}
NW_FIXTURE_API std::uint64_t NW_CDECL nw_memory_fixture_counter_v1(std::uint32_t kind) noexcept
{
    return nw::fixtures::memory_counter(kind);
}
NW_FIXTURE_API nw_status NW_CDECL nw_memory_fixture_validate_v1(std::uint64_t address, std::uint64_t length,
                                                                std::uint64_t count, std::uint64_t width,
                                                                std::uint64_t offset, std::uint64_t capacity,
                                                                std::uint64_t limit, std::uint32_t alignment,
                                                                std::uint64_t* start) noexcept
{
    if (start == nullptr)
    {
        return NW_INVALID_ARGUMENT;
    }
    *start = 0;
    const auto value = nw::interop::checked_extent(address, length, count, width, offset, capacity, limit, alignment);
    if (!value)
    {
        return NW_INVALID_ARGUMENT;
    }
    *start = *value;
    return NW_OK;
}
