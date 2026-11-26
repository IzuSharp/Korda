#ifndef NW_SCALAR_TEST_SUPPORT_HPP
#define NW_SCALAR_TEST_SUPPORT_HPP

#include "nw/scalar.hpp"

// Owning convenience results belong only to the test harness. Production
// decoding writes through the same synchronous scalar sink used by append_next.
namespace nw::test
{

inline Bytes sqlite_decode(const Codec& codec, int sqlite_type, const StorageValue& value)
{
    StorageView borrowed;
    switch (value.index())
    {
        case 0:
            borrowed = std::get<std::int64_t>(value);
            break;
        case 1:
            borrowed = std::get<double>(value);
            break;
        case 2:
            borrowed = std::string_view(std::get<std::string>(value));
            break;
        case 3:
            borrowed = ByteView(std::get<Bytes>(value));
            break;
        default:
            throw Error(NW_DOMAIN_ERROR, 4, "SQLite runtime storage type mismatch");
    }
    Bytes result;
    sqlite_decode_into(codec, sqlite_type, borrowed,
                       {&result, [](void* target, ByteView bytes)
                        { static_cast<Bytes*>(target)->assign(bytes.begin(), bytes.end()); }});
    return result;
}

inline Bytes postgres_decode(const Codec& codec, std::uint32_t oid, ByteView bytes)
{
    Bytes result;
    postgres_decode_into(codec, oid, bytes,
                         {&result, [](void* target, ByteView value)
                          { static_cast<Bytes*>(target)->assign(value.begin(), value.end()); }});
    return result;
}

} // namespace nw::test

#endif
