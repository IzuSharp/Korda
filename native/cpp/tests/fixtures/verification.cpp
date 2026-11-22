#include "support.hpp"

NW_FIXTURE_API std::uint64_t nw_a20_layout_v1(std::uint32_t index) noexcept
{
    const std::size_t values[] = {sizeof(nw_abi_info_v1),
                                  alignof(nw_abi_info_v1),
                                  offsetof(nw_abi_info_v1, struct_size),
                                  offsetof(nw_abi_info_v1, abi_major),
                                  offsetof(nw_abi_info_v1, abi_minor),
                                  offsetof(nw_abi_info_v1, pointer_width_bits),
                                  offsetof(nw_abi_info_v1, capabilities),
                                  offsetof(nw_abi_info_v1, reserved),
                                  sizeof(nw_error_info_v1),
                                  alignof(nw_error_info_v1),
                                  offsetof(nw_error_info_v1, struct_size),
                                  offsetof(nw_error_info_v1, domain),
                                  offsetof(nw_error_info_v1, code),
                                  offsetof(nw_error_info_v1, flags),
                                  offsetof(nw_error_info_v1, correlation_id),
                                  sizeof(nw_bytes_view_v1),
                                  alignof(nw_bytes_view_v1),
                                  offsetof(nw_bytes_view_v1, data),
                                  offsetof(nw_bytes_view_v1, length),
                                  sizeof(nw_operation_callbacks_v1),
                                  alignof(nw_operation_callbacks_v1),
                                  offsetof(nw_operation_callbacks_v1, struct_size),
                                  offsetof(nw_operation_callbacks_v1, flags),
                                  offsetof(nw_operation_callbacks_v1, cookie),
                                  offsetof(nw_operation_callbacks_v1, on_terminal),
                                  sizeof(nw_callbacks_v1),
                                  alignof(nw_callbacks_v1),
                                  offsetof(nw_callbacks_v1, struct_size),
                                  offsetof(nw_callbacks_v1, flags),
                                  offsetof(nw_callbacks_v1, cookie),
                                  offsetof(nw_callbacks_v1, on_event)};
    return index < std::size(values) ? values[index] : UINT64_MAX;
}
NW_FIXTURE_API nw_status nw_a20_extent_v1(std::uint64_t address, std::uint64_t length, std::uint64_t count,
                                          std::uint64_t width, std::uint64_t offset, std::uint64_t capacity,
                                          std::uint64_t limit, std::uint32_t alignment, std::uint64_t* start) noexcept
{
    if (start == nullptr)
    {
        return NW_INVALID_ARGUMENT;
    }
    *start = 0;
    const auto extent = nw::interop::checked_extent(address, length, count, width, offset, capacity, limit, alignment);
    if (!extent)
    {
        return NW_INVALID_ARGUMENT;
    }
    *start = *extent;
    return NW_OK;
}
NW_FIXTURE_API nw_status nw_a20_utf8_v1(const std::uint8_t* input, std::uint64_t length, std::uint32_t limit,
                                        std::uint8_t* output, std::uint32_t capacity, std::uint64_t* written) noexcept
{
    if (written == nullptr)
    {
        return NW_INVALID_ARGUMENT;
    }
    *written = 0;
    if (length > 4096 || (length != 0 && input == nullptr))
    {
        return NW_INVALID_ARGUMENT;
    }
    const nw::ByteView bytes{input, static_cast<std::size_t>(length)};
    if (!nw::valid_utf8(bytes))
    {
        return NW_INVALID_ARGUMENT;
    }
    const auto text = length == 0 ? std::string_view{} : std::string_view(reinterpret_cast<const char*>(input), length);
    const auto detail = nw::interop::BoundedDetail::from_redacted(text, limit);
    if (detail.bytes().size() > capacity || (!detail.bytes().empty() && output == nullptr))
    {
        return NW_BUFFER_TOO_SMALL;
    }
    if (!detail.bytes().empty())
    {
        std::memcpy(output, detail.bytes().data(), detail.bytes().size());
    }
    *written = detail.bytes().size();
    return NW_OK;
}
NW_FIXTURE_API nw_status nw_a20_error_v1(nw_status status, nw_error_info_v1* output, std::uint32_t domain,
                                         std::int32_t code, std::uint64_t correlation) noexcept
{
    return nw::interop::write_error(status, output, {domain, code, correlation});
}
NW_FIXTURE_API nw_status nw_a20_notify_v1(const nw_operation_callbacks_v1* table) noexcept
{
    if (table == nullptr || table->on_terminal == nullptr)
    {
        return NW_INVALID_ARGUMENT;
    }
    return table->on_terminal(table->cookie, NW_OK, nullptr);
}
