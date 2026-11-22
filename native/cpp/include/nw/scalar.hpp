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

[[nodiscard]] StorageValue sqlite_encode(const Codec& codec, ByteView bytes);
[[nodiscard]] Bytes sqlite_decode(const Codec& codec, int sqlite_type, const StorageValue& value);

// PostgreSQL parameters use text format with an explicit OID; returned columns
// use binary format. All provider buffers are copied before returning.
[[nodiscard]] std::string postgres_encode(const Codec& codec, ByteView bytes);
[[nodiscard]] Bytes postgres_decode(const Codec& codec, std::uint32_t oid, ByteView bytes);
[[nodiscard]] std::uint32_t postgres_oid(const Codec& codec);
[[nodiscard]] std::string postgres_type_name(std::uint32_t oid);

} // namespace nw

#endif
