#ifndef NW_SCALAR_HPP
#define NW_SCALAR_HPP

#include "nw/contracts.hpp"

#include <cstdint>
#include <string>
#include <variant>

namespace nw
{

// The caller owns SQL NULL, column shape, and field-byte admission. These
// conversions preserve the declared logical codec without storage coercion.
using StorageValue = std::variant<std::int64_t, double, std::string, Bytes>;
using StorageView = std::variant<std::int64_t, double, std::string_view, ByteView>;

// Called synchronously with one validated canonical scalar. The view is either
// borrowed provider memory or at most sixteen bytes of conversion scratch, and
// must not escape write(). No owning intermediate scalar is required.
struct ScalarSink
{
    void* state;
    void (*write)(void*, ByteView);
};

[[nodiscard]] StorageValue sqlite_encode(const Codec& codec, ByteView bytes);
void sqlite_decode_into(const Codec& codec, int sqlite_type, const StorageView& value, ScalarSink sink);

// PostgreSQL parameters use text format with an explicit OID; returned columns
// use binary format. The synchronous sink owns any retained output; borrowed
// provider or scalar scratch views never escape a decode_into call.
[[nodiscard]] std::string postgres_encode(const Codec& codec, ByteView bytes);
void postgres_decode_into(const Codec& codec, std::uint32_t oid, ByteView bytes, ScalarSink sink);
[[nodiscard]] std::uint32_t postgres_oid(const Codec& codec);
[[nodiscard]] std::string postgres_type_name(std::uint32_t oid);

} // namespace nw

#endif
