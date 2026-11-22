// Generated from native/database/interfaces/schema-v1.json; do not edit.
#pragma once

#include <array>
#include <cstddef>
#include <string_view>

namespace nw::schema
{
inline constexpr std::size_t SCHEMA_VERSION = 1;
inline constexpr std::size_t WIRE_VERSION = 1;
inline constexpr std::size_t HEADER_BYTES = 96;
inline constexpr std::size_t DESCRIPTOR_BYTES = 16;
inline constexpr std::size_t CELL_BYTES = 24;
inline constexpr std::size_t CONTROL_HEADER_BYTES = 16;
inline constexpr std::size_t CONTROL_ENTRY_BYTES = 16;
inline constexpr std::size_t RELATION_CONTROL_KIND = 23;
inline constexpr std::array<std::string_view, 8> MODEL_KEYS{"version", "logical_id", "table_id", "schema", "table", "columns", "projection", "relations"};
inline constexpr std::array<std::string_view, 10> COLUMN_KEYS{"id", "name", "codec", "codec_config", "nullable", "key_ordinal", "generated", "concurrency", "collation", "max_length"};
inline constexpr std::array<std::string_view, 2> PROJECTION_KEYS{"version", "fields"};
inline constexpr std::array<std::string_view, 5> PROJECTION_FIELD_KEYS{"ordinal", "name", "codec", "codec_config", "nullable"};
inline constexpr std::array<std::string_view, 8> RELATION_KEYS{"version", "parent_model", "parent_table", "child_model", "child_table", "parent_columns", "child_columns", "child_nullable"};
} // namespace nw::schema
