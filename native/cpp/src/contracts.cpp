#include "nw/contracts.hpp"

#include <nlohmann/json.hpp>
#include <openssl/sha.h>

#include <algorithm>
#include <atomic>
#include <bit>
#include <cmath>
#include <cstring>
#include <limits>
#include <set>
#include <type_traits>

namespace nw
{
namespace
{

using Json = nlohmann::json;
std::atomic<std::uint64_t> next_error{1};

[[noreturn]] void invalid(std::string_view message)
{
    throw Error::invalid(std::string(message));
}

std::size_t add(std::size_t left, std::size_t right, std::string_view message)
{
    if (right > std::numeric_limits<std::size_t>::max() - left)
    {
        invalid(message);
    }
    return left + right;
}

std::size_t multiply(std::size_t left, std::size_t right, std::string_view message)
{
    if (left != 0 && right > std::numeric_limits<std::size_t>::max() / left)
    {
        invalid(message);
    }
    return left * right;
}

ByteView view(std::string_view value)
{
    return {reinterpret_cast<const std::uint8_t*>(value.data()), value.size()};
}

Bytes owned(std::string_view value)
{
    return {value.begin(), value.end()};
}

bool equal(ByteView left, ByteView right)
{
    return left.size() == right.size() && std::equal(left.begin(), left.end(), right.begin());
}

std::string bool_text(bool value)
{
    return value ? "true" : "false";
}

std::string optional_string(const std::optional<std::string>& value)
{
    return value ? json_quote(*value) : "null";
}

std::string optional_number(std::optional<std::uint32_t> value)
{
    return value ? std::to_string(*value) : "null";
}

std::string numbers(std::span<const std::uint32_t> values)
{
    std::string result;
    for (const auto number : values)
    {
        if (!result.empty())
        {
            result += ',';
        }
        result += std::to_string(number);
    }
    return result;
}

std::string identity(std::span<const std::optional<std::string_view>> parts)
{
    std::string result = "[";
    bool first = true;
    for (const auto& part : parts)
    {
        if (!first)
        {
            result += ',';
        }
        first = false;
        result += part ? json_quote(*part) : "null";
    }
    return result + ']';
}

std::optional<std::string_view> string_view_of(const std::optional<std::string>& text)
{
    return text ? std::optional<std::string_view>(*text) : std::nullopt;
}

void guard_json_depth(ByteView bytes, std::uint32_t maximum, std::string_view message)
{
    bool quoted = false;
    bool escaped = false;
    std::uint32_t level = 0;
    for (const auto byte : bytes)
    {
        if (quoted)
        {
            if (escaped)
            {
                escaped = false;
            }
            else if (byte == '\\')
            {
                escaped = true;
            }
            else if (byte == '"')
            {
                quoted = false;
            }
        }
        else if (byte == '"')
        {
            quoted = true;
        }
        else if (byte == '[' || byte == '{')
        {
            if (++level > maximum)
            {
                invalid(message);
            }
        }
        else if (byte == ']' || byte == '}')
        {
            if (level == 0)
            {
                invalid("unbalanced JSON container");
            }
            --level;
        }
    }
}

Json parse_json(ByteView bytes, std::string_view message)
{
    if (!valid_utf8(bytes))
    {
        invalid(message);
    }
    // serde_json's ordinary metadata parser has a finite recursion limit.
    // Valid v1 metadata is shallow; bound hostile input before entering a parser.
    guard_json_depth(bytes, 128, message);
    try
    {
        return Json::parse(bytes.begin(), bytes.end());
    }
    catch (const Json::exception&)
    {
        invalid(message);
    }
}

void require_keys(const Json& value, std::initializer_list<std::string_view> expected)
{
    if (!value.is_object() || value.size() != expected.size())
    {
        invalid("unknown or missing field");
    }
    for (const auto& item : value.items())
    {
        if (std::find(expected.begin(), expected.end(), item.key()) == expected.end())
        {
            invalid("unknown or missing field");
        }
    }
}

std::uint64_t unsigned_number(const Json& value)
{
    if (value.is_number_unsigned())
    {
        return value.get<std::uint64_t>();
    }
    if (value.is_number_integer() && value.get<std::int64_t>() >= 0)
    {
        return static_cast<std::uint64_t>(value.get<std::int64_t>());
    }
    invalid("unsigned integer required");
}

std::uint64_t number(const Json& value, std::string_view key)
{
    const auto found = value.find(std::string(key));
    if (found == value.end())
    {
        invalid("unsigned integer required");
    }
    return unsigned_number(*found);
}

std::uint32_t u32_number(const Json& value, std::string_view key)
{
    const auto result = number(value, key);
    if (result > std::numeric_limits<std::uint32_t>::max())
    {
        invalid("u32 overflow");
    }
    return static_cast<std::uint32_t>(result);
}

std::optional<std::uint32_t> optional_u32(const Json& value, std::string_view key)
{
    const auto found = value.find(std::string(key));
    if (found != value.end() && found->is_null())
    {
        return std::nullopt;
    }
    return u32_number(value, key);
}

bool boolean(const Json& value, std::string_view key)
{
    const auto found = value.find(std::string(key));
    if (found == value.end() || !found->is_boolean())
    {
        invalid("Boolean required");
    }
    return found->get<bool>();
}

std::string name(const Json& value, std::string_view key)
{
    const auto found = value.find(std::string(key));
    if (found == value.end() || !found->is_string() || found->get_ref<const std::string&>().empty())
    {
        invalid("nonempty name required");
    }
    return found->get<std::string>();
}

std::optional<std::string> optional_name(const Json& value, std::string_view key)
{
    const auto found = value.find(std::string(key));
    if (found != value.end() && found->is_null())
    {
        return std::nullopt;
    }
    return name(value, key);
}

const Json& array(const Json& value, std::string_view key)
{
    const auto found = value.find(std::string(key));
    if (found == value.end() || !found->is_array())
    {
        invalid("array required");
    }
    return *found;
}

std::vector<std::uint32_t> u32_array(const Json& value, std::string_view key)
{
    std::vector<std::uint32_t> result;
    for (const auto& item : array(value, key))
    {
        const auto scalar = unsigned_number(item);
        if (scalar > std::numeric_limits<std::uint32_t>::max())
        {
            invalid("u32 vector required");
        }
        result.push_back(static_cast<std::uint32_t>(scalar));
    }
    return result;
}

Codec parse_codec(const Json& value)
{
    Codec result{u32_number(value, "codec"), std::nullopt, boolean(value, "nullable")};
    const auto found = value.find("codec_config");
    if (found == value.end())
    {
        invalid("codec configuration missing");
    }
    if (found->is_null())
    {
        return result;
    }
    if (result.id == 13)
    {
        require_keys(*found, {"precision", "scale"});
        result.configuration = "{\"precision\":" + std::to_string(number(*found, "precision")) +
                               ",\"scale\":" + std::to_string(number(*found, "scale")) + '}';
    }
    else if (result.id == 15)
    {
        require_keys(*found, {"scale"});
        result.configuration = "{\"scale\":" + std::to_string(number(*found, "scale")) + '}';
    }
    else
    {
        invalid("irrelevant configuration");
    }
    return result;
}

std::string config_text(const Codec& codec)
{
    return codec.configuration.value_or("null");
}

// JSON scalar values are raw RFC8259 data. serde_json::RawValue does not convert
// numeric tokens into floating point, so 1e10000 remains valid. Normalize only
// validated number tokens in a temporary syntax-check buffer, retaining the
// caller's original bytes. RawValue and JsonDocument also accept individual
// escaped UTF-16 surrogate code units without interpreting strings. Validate
// each four-hex-digit escape and normalize only that temporary syntax token.
// nlohmann still validates JSON grammar, escaping, containers and adjacency.
void validate_raw_json(ByteView bytes)
{
    if (bytes.size() > MAX_BYTES || (bytes.size() >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf))
    {
        invalid("JSON size limit or BOM");
    }
    if (!valid_utf8(bytes))
    {
        invalid("invalid JSON UTF-8");
    }
    guard_json_depth(bytes, 64, "JSON depth limit");
    std::string normalized;
    normalized.reserve(bytes.size());
    bool quoted = false;
    bool escaped = false;
    const auto digit = [](std::uint8_t byte) { return byte >= '0' && byte <= '9'; };
    for (std::size_t at = 0; at < bytes.size();)
    {
        const auto byte = bytes[at];
        if (quoted)
        {
            if (!escaped && byte == '\\' && at + 1 < bytes.size() && bytes[at + 1] == 'u')
            {
                if (bytes.size() - at < 6)
                    invalid("invalid JSON Unicode escape");
                for (std::size_t index = at + 2; index < at + 6; ++index)
                {
                    const auto hex = bytes[index];
                    if (!digit(hex) && !(hex >= 'a' && hex <= 'f') && !(hex >= 'A' && hex <= 'F'))
                        invalid("invalid JSON Unicode escape");
                }
                normalized += "\\u0000";
                at += 6;
                continue;
            }
            normalized += static_cast<char>(byte);
            ++at;
            if (escaped)
            {
                escaped = false;
            }
            else if (byte == '\\')
            {
                escaped = true;
            }
            else if (byte == '"')
            {
                quoted = false;
            }
            continue;
        }
        if (byte == '"')
        {
            quoted = true;
        }
        if (byte != '-' && !digit(byte))
        {
            normalized += static_cast<char>(byte);
            ++at;
            continue;
        }
        if (bytes[at] == '-')
        {
            ++at;
        }
        if (at >= bytes.size() || !digit(bytes[at]))
        {
            invalid("invalid JSON");
        }
        if (bytes[at] == '0')
        {
            ++at;
            if (at < bytes.size() && digit(bytes[at]))
            {
                invalid("invalid JSON");
            }
        }
        else
        {
            while (at < bytes.size() && digit(bytes[at]))
            {
                ++at;
            }
        }
        if (at < bytes.size() && bytes[at] == '.')
        {
            ++at;
            if (at >= bytes.size() || !digit(bytes[at]))
            {
                invalid("invalid JSON");
            }
            while (at < bytes.size() && digit(bytes[at]))
            {
                ++at;
            }
        }
        if (at < bytes.size() && (bytes[at] == 'e' || bytes[at] == 'E'))
        {
            ++at;
            if (at < bytes.size() && (bytes[at] == '+' || bytes[at] == '-'))
            {
                ++at;
            }
            if (at >= bytes.size() || !digit(bytes[at]))
            {
                invalid("invalid JSON");
            }
            while (at < bytes.size() && digit(bytes[at]))
            {
                ++at;
            }
        }
        if (at < bytes.size() && bytes[at] != ' ' && bytes[at] != '\t' && bytes[at] != '\r' && bytes[at] != '\n' &&
            bytes[at] != ',' && bytes[at] != ']' && bytes[at] != '}')
        {
            invalid("invalid JSON");
        }
        normalized += '0';
    }
    if (!Json::accept(normalized))
    {
        invalid("invalid JSON");
    }
}

template <typename T> T read_unsigned(ByteView bytes, std::size_t offset)
{
    static_assert(std::is_unsigned_v<T>);
    if (offset > bytes.size() || sizeof(T) > bytes.size() - offset)
    {
        invalid("integer read bounds");
    }
    T result = 0;
    for (std::size_t index = 0; index < sizeof(T); ++index)
    {
        result = static_cast<T>(result | (static_cast<T>(bytes[offset + index]) << (index * 8)));
    }
    return result;
}

template <typename T> void write_unsigned(Bytes& bytes, std::size_t offset, T value)
{
    static_assert(std::is_unsigned_v<T>);
    if (offset > bytes.size() || sizeof(T) > bytes.size() - offset)
    {
        invalid("integer write bounds");
    }
    for (std::size_t index = 0; index < sizeof(T); ++index)
    {
        bytes[offset + index] = static_cast<std::uint8_t>(value >> (index * 8));
    }
}

} // namespace

Error::Error(std::uint32_t status_value, std::string message_value) : Error(status_value, 11, std::move(message_value))
{
}

Error::Error(std::uint32_t status_value, std::uint32_t code_value, std::string message_value)
    : status(status_value), code(code_value), message(std::move(message_value)),
      correlation(next_error.fetch_add(1, std::memory_order_relaxed))
{
}

const char* Error::what() const noexcept
{
    return message.c_str();
}

ErrorFields Error::fields() const noexcept
{
    return {0x4442, static_cast<std::int32_t>(code), correlation};
}

Error Error::invalid(std::string message)
{
    return {NW_INVALID_ARGUMENT, 0, std::move(message)};
}

Error Error::state(std::string message)
{
    return {NW_INVALID_STATE, 8, std::move(message)};
}

Error Error::closed()
{
    return state("database admission is closed");
}

Error Error::capacity()
{
    return {NW_DOMAIN_ERROR, 6, "finite database capacity exhausted"};
}

Error Error::canceled()
{
    return {NW_CANCELED, 8, "database operation canceled"};
}

Error Error::unsupported()
{
    return {NW_NOT_SUPPORTED, 5, "database capability unavailable"};
}

Profile profile_from(std::uint32_t value)
{
    if (value != 1 && value != 2)
    {
        invalid("unknown semantic profile");
    }
    return static_cast<Profile>(value);
}

void SourceIdentity::validate() const
{
    if (engine == 0 || source == 0 || generation == 0)
    {
        throw Error(NW_INVALID_ARGUMENT, "zero source identity");
    }
}

void Codec::validate(Profile profile) const
{
    if (id < 1 || id > 17)
    {
        invalid("unknown required codec");
    }
    if (id == 13)
    {
        if (profile == Profile::Sqlite)
        {
            invalid("numeric decimal unsupported on ordinary SQLite");
        }
        if (!configuration)
        {
            invalid("missing decimal configuration");
        }
        const auto value = parse_json(view(*configuration), "invalid decimal configuration");
        require_keys(value, {"precision", "scale"});
        const auto precision = number(value, "precision");
        const auto scale = number(value, "scale");
        if (precision < 1 || precision > 29 || scale > 28 || scale > precision ||
            *configuration !=
                "{\"precision\":" + std::to_string(precision) + ",\"scale\":" + std::to_string(scale) + '}')
        {
            invalid("invalid decimal configuration");
        }
    }
    else if (id == 15)
    {
        if (!configuration)
        {
            invalid("missing money configuration");
        }
        const auto value = parse_json(view(*configuration), "invalid money configuration");
        require_keys(value, {"scale"});
        const auto scale = number(value, "scale");
        if (scale > 18 || *configuration != "{\"scale\":" + std::to_string(scale) + '}')
        {
            invalid("invalid money configuration");
        }
    }
    else if (configuration)
    {
        invalid("irrelevant codec configuration");
    }
}

bool Codec::same_scalar(const Codec& other) const noexcept
{
    return id == other.id && configuration == other.configuration;
}

Model Model::parse(ByteView bytes, Profile profile)
{
    if (bytes.size() > MAX_BYTES)
    {
        invalid("metadata byte limit");
    }
    const auto value = parse_json(bytes, "invalid metadata JSON");
    require_keys(value, {"version", "logical_id", "table_id", "schema", "table", "columns", "projection", "relations"});
    if (number(value, "version") != 1)
    {
        invalid("unsupported metadata schema");
    }
    Model model;
    model.logical_id_ = name(value, "logical_id");
    model.table_ = name(value, "table");
    if (model.logical_id_.find(':') == std::string::npos)
    {
        invalid("invalid logical identity");
    }
    model.schema_ = optional_name(value, "schema");
    model.table_id_ = u32_number(value, "table_id");
    if (model.table_id_ == 0 || model.table_id_ != stable_id({"NWDB-TABLE-ID-v1", model.logical_id_,
                                                              string_view_of(model.schema_), model.table_}))
    {
        invalid("table identity mismatch");
    }
    const auto& columns = array(value, "columns");
    if (columns.empty() || columns.size() > MAX_NODES)
    {
        invalid("invalid column count");
    }
    std::set<std::string> names;
    std::set<std::uint32_t> ids;
    model.columns_.reserve(columns.size());
    for (const auto& item : columns)
    {
        require_keys(item, {"id", "name", "codec", "codec_config", "nullable", "key_ordinal", "generated",
                            "concurrency", "collation", "max_length"});
        Column column;
        column.name = name(item, "name");
        column.id = u32_number(item, "id");
        auto policy_name = column.name;
        if (profile == Profile::Sqlite)
        {
            for (auto& byte : policy_name)
            {
                if (byte >= 'A' && byte <= 'Z')
                {
                    byte = static_cast<char>(byte + ('a' - 'A'));
                }
            }
        }
        if (!names.insert(policy_name).second || !ids.insert(column.id).second || column.id == 0 ||
            column.id != stable_id({"NWDB-COLUMN-ID-v1", model.logical_id_, string_view_of(model.schema_), model.table_,
                                    column.name}))
        {
            invalid("duplicate or mismatched column identity");
        }
        column.codec = parse_codec(item);
        column.codec.validate(profile);
        column.key_ordinal = optional_u32(item, "key_ordinal");
        if (column.key_ordinal && column.codec.nullable)
        {
            invalid("nullable primary key");
        }
        column.max_length = optional_u32(item, "max_length");
        if (column.max_length == 0)
        {
            invalid("zero byte capacity");
        }
        column.generated = boolean(item, "generated");
        column.concurrency = boolean(item, "concurrency");
        column.collation = optional_name(item, "collation");
        model.columns_.push_back(std::move(column));
    }
    model.projection_ = u32_array(value, "projection");
    if (model.projection_.size() != model.columns_.size() ||
        std::set<std::uint32_t>(model.projection_.begin(), model.projection_.end()) != ids)
    {
        invalid("projection must cover every column exactly once");
    }
    if (!array(value, "relations").empty())
    {
        invalid("v1 model relations must remain empty");
    }
    std::uint32_t expected_key = 0;
    for (const auto id : model.projection_)
    {
        const auto& column = model.column(id);
        if (column.key_ordinal)
        {
            if (*column.key_ordinal != expected_key)
            {
                invalid("noncontiguous key declaration order");
            }
            ++expected_key;
        }
    }
    std::sort(model.columns_.begin(), model.columns_.end(),
              [](const Column& left, const Column& right) { return left.id < right.id; });
    model.canonical_ = model.encode();
    if (!equal(model.canonical_, bytes))
    {
        invalid("noncanonical or duplicate model property");
    }
    model.hash_ = sha256(model.canonical_);
    return model;
}

Bytes Model::encode() const
{
    std::string columns;
    for (const auto& column : columns_)
    {
        if (!columns.empty())
        {
            columns += ',';
        }
        columns += "{\"id\":" + std::to_string(column.id) + ",\"name\":" + json_quote(column.name) +
                   ",\"codec\":" + std::to_string(column.codec.id) + ",\"codec_config\":" + config_text(column.codec) +
                   ",\"nullable\":" + bool_text(column.codec.nullable) +
                   ",\"key_ordinal\":" + optional_number(column.key_ordinal) +
                   ",\"generated\":" + bool_text(column.generated) +
                   ",\"concurrency\":" + bool_text(column.concurrency) +
                   ",\"collation\":" + optional_string(column.collation) +
                   ",\"max_length\":" + optional_number(column.max_length) + '}';
    }
    return owned("{\"version\":1,\"logical_id\":" + json_quote(logical_id_) +
                 ",\"table_id\":" + std::to_string(table_id_) + ",\"schema\":" + optional_string(schema_) +
                 ",\"table\":" + json_quote(table_) + ",\"columns\":[" + columns + "],\"projection\":[" +
                 numbers(projection_) + "],\"relations\":[]}");
}

const Bytes& Model::bytes() const noexcept
{
    return canonical_;
}
Hash Model::hash() const noexcept
{
    return hash_;
}
std::uint32_t Model::table_id() const noexcept
{
    return table_id_;
}
const std::vector<Column>& Model::columns() const noexcept
{
    return columns_;
}
const std::string& Model::logical_id() const noexcept
{
    return logical_id_;
}
const std::optional<std::string>& Model::schema() const noexcept
{
    return schema_;
}
const std::string& Model::table() const noexcept
{
    return table_;
}
const std::vector<std::uint32_t>& Model::projection() const noexcept
{
    return projection_;
}

const Column& Model::column(std::uint32_t id) const
{
    const auto found =
        std::find_if(columns_.begin(), columns_.end(), [id](const Column& column) { return column.id == id; });
    if (found == columns_.end())
    {
        invalid("unknown column");
    }
    return *found;
}

std::vector<const Column*> Model::keys() const
{
    std::vector<const Column*> result;
    for (const auto& column : columns_)
    {
        if (column.key_ordinal)
        {
            result.push_back(&column);
        }
    }
    std::sort(result.begin(), result.end(),
              [](const Column* left, const Column* right) { return left->key_ordinal < right->key_ordinal; });
    return result;
}

Projection Projection::parse(ByteView bytes, Profile profile)
{
    return parse_context(bytes, profile, false);
}

Projection Projection::parse_scalar(ByteView bytes, Profile profile)
{
    return parse_context(bytes, profile, true);
}

Projection Projection::parse_context(ByteView bytes, Profile profile, bool scalar)
{
    if (bytes.size() > MAX_BYTES)
    {
        invalid("projection byte limit");
    }
    const auto value = parse_json(bytes, "invalid projection JSON");
    require_keys(value, {"version", "fields"});
    if (number(value, "version") != 1)
    {
        invalid("unsupported projection version");
    }
    const auto& fields = array(value, "fields");
    if (fields.empty() || fields.size() > MAX_NODES || (scalar && fields.size() != 1))
    {
        invalid("invalid projection count");
    }
    std::set<std::string> names;
    Projection result;
    result.fields_.reserve(fields.size());
    for (const auto& field : fields)
    {
        require_keys(field, {"ordinal", "name", "codec", "codec_config", "nullable"});
        if (number(field, "ordinal") != result.fields_.size())
        {
            invalid("projection ordinal mismatch");
        }
        std::string field_name;
        if (scalar)
        {
            if (!field["name"].is_string() || field["name"].get_ref<const std::string&>() != "")
            {
                invalid("scalar shape requires the empty-alias sentinel");
            }
        }
        else
        {
            field_name = name(field, "name");
        }
        if (!names.insert(field_name).second)
        {
            invalid("duplicate result alias");
        }
        auto codec = parse_codec(field);
        codec.validate(profile);
        result.fields_.push_back({std::move(field_name), std::move(codec)});
    }
    result.canonical_ = result.encode();
    if (!equal(result.canonical_, bytes))
    {
        invalid("noncanonical projection");
    }
    result.hash_ = sha256(bytes);
    return result;
}

Bytes Projection::encode() const
{
    std::string fields;
    for (std::size_t ordinal = 0; ordinal < fields_.size(); ++ordinal)
    {
        const auto& field = fields_[ordinal];
        if (ordinal != 0)
        {
            fields += ',';
        }
        fields += "{\"ordinal\":" + std::to_string(ordinal) + ",\"name\":" + json_quote(field.name) +
                  ",\"codec\":" + std::to_string(field.codec.id) + ",\"codec_config\":" + config_text(field.codec) +
                  ",\"nullable\":" + bool_text(field.codec.nullable) + '}';
    }
    return owned("{\"version\":1,\"fields\":[" + fields + "]}");
}

const Bytes& Projection::bytes() const noexcept
{
    return canonical_;
}
Hash Projection::hash() const noexcept
{
    return hash_;
}
const std::vector<ResultField>& Projection::fields() const noexcept
{
    return fields_;
}

MetadataRegistry::MetadataRegistry(Profile profile, std::size_t max_entries, std::size_t max_bytes)
    : profile_(profile), max_entries_(max_entries), max_bytes_(max_bytes)
{
    if (max_entries == 0 || max_bytes == 0 || max_entries > MAX_NODES || max_bytes > MAX_BYTES)
    {
        invalid("invalid registry limits");
    }
}

std::shared_ptr<const Model> MetadataRegistry::register_model(ByteView bytes, const Hash& expected_hash)
{
    auto model = std::make_shared<const Model>(Model::parse(bytes, profile_));
    if (model->hash() != expected_hash)
    {
        invalid("metadata content hash mismatch");
    }
    const auto existing = models_.find(model->hash());
    if (existing != models_.end())
    {
        if (!equal(existing->second->bytes(), bytes))
        {
            invalid("metadata hash collision");
        }
        return existing->second;
    }
    if (models_.size() >= max_entries_ || model->bytes().size() > max_bytes_ - bytes_)
    {
        invalid("metadata registry budget");
    }
    const std::array<std::optional<std::string_view>, 3> table_parts{model->logical_id(),
                                                                     string_view_of(model->schema()), model->table()};
    const auto table_identity = identity(table_parts);
    const auto table = table_ids_.find(model->table_id());
    if (table != table_ids_.end() && table->second != table_identity)
    {
        invalid("table ID collision");
    }
    std::vector<std::pair<std::uint32_t, std::string>> identities;
    for (const auto& column : model->columns())
    {
        const std::array<std::optional<std::string_view>, 4> parts{model->logical_id(), string_view_of(model->schema()),
                                                                   model->table(), column.name};
        auto column_identity = identity(parts);
        const auto found = column_ids_.find(column.id);
        if (found != column_ids_.end() && found->second != column_identity)
        {
            invalid("column ID collision");
        }
        identities.emplace_back(column.id, std::move(column_identity));
    }
    // Publish all three indexes together, including on allocation failure.
    auto next_models = models_;
    auto next_tables = table_ids_;
    auto next_columns = column_ids_;
    next_models.emplace(model->hash(), model);
    next_tables.insert_or_assign(model->table_id(), table_identity);
    for (auto& [id, column_identity] : identities)
    {
        next_columns.insert_or_assign(id, std::move(column_identity));
    }
    models_.swap(next_models);
    table_ids_.swap(next_tables);
    column_ids_.swap(next_columns);
    bytes_ += model->bytes().size();
    return model;
}

std::pair<std::size_t, std::size_t> MetadataRegistry::counts() const noexcept
{
    return {models_.size(), bytes_};
}

void Expectation::validate(std::uint32_t rows) const
{
    if (exact_rows && *exact_rows != rows)
    {
        invalid("consumer-specific exact row count mismatch");
    }
    if (kind == 2 && descriptors.empty())
    {
        invalid("row shape is required");
    }
    if (kind < 1 || kind > 4 || descriptors.size() > MAX_NODES)
    {
        invalid("unknown kind or descriptor limit");
    }
    if ((kind == 1 && rows != 1) || (kind == 4 && (rows == 0 || entity_id == 0)) ||
        (kind == 3 && (descriptors.size() != 1 || descriptors[0].id != 1 || hash != Hash{})) ||
        (kind != 4 && entity_id != 0) || ((kind == 2) != (projection_id > 0)))
    {
        invalid("invalid kind-specific identity/count");
    }
    std::set<std::uint32_t> ids;
    for (const auto& descriptor : descriptors)
    {
        descriptor.codec.validate(profile);
        if (!ids.insert(descriptor.id).second || (kind != 2 && descriptor.id == 0))
        {
            invalid("duplicate/reserved descriptor ID");
        }
        if ((!descriptor.codec.configuration && descriptor.bound_codec != descriptor.codec.id) ||
            (descriptor.codec.configuration && descriptor.bound_codec < 1024))
        {
            invalid("invalid source-bound codec");
        }
    }
}

BatchView BatchView::parse(ByteView bytes, const Expectation& expected)
{
    if (bytes.size() < 96 || bytes.size() > MAX_BYTES || !equal(bytes.first(4), view("NWDB")))
    {
        invalid("invalid wire header/size");
    }
    const auto rows = read_u32(bytes, 56);
    expected.validate(rows);
    if (read_u16(bytes, 4) != 1 || read_u16(bytes, 6) != expected.kind || read_u32(bytes, 8) != 96 ||
        read_u32(bytes, 12) != 0 || read_u64(bytes, 16) != bytes.size() ||
        !equal(bytes.subspan(24, 32), expected.hash) || read_u32(bytes, 60) != expected.descriptors.size() ||
        read_u32(bytes, 64) != expected.entity_id || read_u32(bytes, 68) != 0 ||
        read_u32(bytes, 72) != expected.projection_id || profile_from(read_u32(bytes, 76)) != expected.profile ||
        read_u64(bytes, 80) != 96)
    {
        invalid("version/profile/identity/header mismatch");
    }
    const auto count = multiply(rows, expected.descriptors.size(), "cell count overflow");
    const auto cells_offset =
        add(96, multiply(expected.descriptors.size(), 16, "descriptor overflow"), "descriptor overflow");
    const auto payload_offset = add(cells_offset, multiply(count, 24, "cell size overflow"), "cell size overflow");
    if (payload_offset > bytes.size() || read_u64(bytes, 88) != payload_offset)
    {
        invalid("truncated/overlapping tables");
    }
    for (std::size_t index = 0; index < expected.descriptors.size(); ++index)
    {
        const auto& descriptor = expected.descriptors[index];
        const auto offset = 96 + index * 16;
        if (read_u32(bytes, offset) != descriptor.id || read_u16(bytes, offset + 4) != descriptor.codec.id ||
            read_u16(bytes, offset + 6) != static_cast<std::uint16_t>(descriptor.codec.nullable) ||
            read_u32(bytes, offset + 8) != descriptor.bound_codec || read_u32(bytes, offset + 12) != 0)
        {
            invalid("descriptor shape mismatch");
        }
    }
    std::vector<std::pair<std::uint64_t, std::uint64_t>> ranges;
    ranges.reserve(count);
    for (std::size_t index = 0; index < count; ++index)
    {
        const auto& descriptor = expected.descriptors[index % expected.descriptors.size()];
        const auto offset = cells_offset + index * 24;
        const auto flags = read_u16(bytes, offset + 2);
        const auto start = read_u64(bytes, offset + 8);
        const auto length = read_u64(bytes, offset + 16);
        if (read_u16(bytes, offset) != descriptor.codec.id || read_u32(bytes, offset + 4) != descriptor.bound_codec ||
            flags > 1)
        {
            invalid("cell codec/flags mismatch");
        }
        if (flags == 1)
        {
            if (!descriptor.codec.nullable || start != 0 || length != 0)
            {
                invalid("invalid NULL cell");
            }
            continue;
        }
        if (start < payload_offset || start > bytes.size() || length > bytes.size() - start ||
            (length == 0 && start != payload_offset))
        {
            invalid("invalid cell range");
        }
        if (length != 0)
        {
            ranges.emplace_back(start, start + length);
        }
        validate_scalar(descriptor.codec,
                        bytes.subspan(static_cast<std::size_t>(start), static_cast<std::size_t>(length)),
                        expected.profile);
    }
    std::sort(ranges.begin(), ranges.end());
    for (std::size_t index = 1; index < ranges.size(); ++index)
    {
        if (ranges[index].first < ranges[index - 1].second)
        {
            invalid("overlapping cell data");
        }
    }
    BatchView result;
    result.bytes_ = bytes;
    result.columns_ = expected.descriptors.size();
    result.rows_ = rows;
    return result;
}

Batch Batch::parse(ByteView bytes, Expectation expected)
{
    const auto view = BatchView::parse(bytes, expected);
    Batch result;
    result.bytes_ = Bytes(bytes.begin(), bytes.end());
    result.expectation_ = std::move(expected);
    result.rows_ = view.rows();
    return result;
}

std::optional<ByteView> BatchView::cell(std::uint32_t row, std::size_t column) const
{
    if (row >= rows_ || column >= columns_)
        invalid("cell ordinal out of range");
    const auto offset = 96 + columns_ * 16 + (static_cast<std::size_t>(row) * columns_ + column) * 24;
    if (read_u16(bytes_, offset + 2) == 1)
        return std::nullopt;
    return bytes_.subspan(static_cast<std::size_t>(read_u64(bytes_, offset + 8)),
                          static_cast<std::size_t>(read_u64(bytes_, offset + 16)));
}

BatchWriter::BatchWriter(const Expectation& expected, std::uint32_t row_limit, std::size_t byte_limit)
    : expected_(expected), byte_limit_(byte_limit),
      base_(add(96, multiply(expected.descriptors.size(), 16, "descriptor overflow"), "descriptor overflow")),
      per_row_(multiply(expected.descriptors.size(), 24, "cell size overflow")), row_limit_(row_limit)
{
    expected.validate(0);
    if (expected.kind != 2 || !row_limit || row_limit > MAX_NODES || !byte_limit || byte_limit > MAX_BYTES)
        throw Error::capacity();
    if (base_ > byte_limit || per_row_ > byte_limit - base_)
        throw Error(NW_DOMAIN_ERROR, 14, "single row descriptors exceed batch capacity");
    reserved_rows_ = static_cast<std::uint32_t>(std::min<std::size_t>(row_limit, (byte_limit - base_) / per_row_));
    payload_offset_ = base_ + reserved_rows_ * per_row_;
    // The final frame is bounded by byte_limit. The unused cell-table prefix is
    // the only temporary extra space, covered by the existing stream grant.
    // Large configured limits must not allocate their entire allowance for a
    // one-row query. Grow only when an actual admitted cell needs more space.
    bytes_.reserve(payload_offset_ + std::min<std::size_t>(row_payload_limit(), 1024));
    bytes_.resize(payload_offset_, 0);
    std::copy_n("NWDB", 4, bytes_.begin());
    put_u16(bytes_, 4, 1);
    put_u16(bytes_, 6, expected.kind);
    put_u32(bytes_, 8, 96);
    std::copy(expected.hash.begin(), expected.hash.end(), bytes_.begin() + 24);
    put_u32(bytes_, 60, static_cast<std::uint32_t>(expected.descriptors.size()));
    put_u32(bytes_, 64, expected.entity_id);
    put_u32(bytes_, 72, expected.projection_id);
    put_u32(bytes_, 76, static_cast<std::uint32_t>(expected.profile));
    put_u64(bytes_, 80, 96);
    for (std::size_t i = 0; i < expected.descriptors.size(); ++i)
    {
        const auto& descriptor = expected.descriptors[i];
        const auto offset = 96 + i * 16;
        put_u32(bytes_, offset, descriptor.id);
        put_u16(bytes_, offset + 4, static_cast<std::uint16_t>(descriptor.codec.id));
        put_u16(bytes_, offset + 6, static_cast<std::uint16_t>(descriptor.codec.nullable));
        put_u32(bytes_, offset + 8, descriptor.bound_codec);
    }
}

void BatchWriter::begin_row()
{
    if (finished_ || in_row_ || rows_ >= row_limit_)
        throw Error::state("invalid batch row admission");
    in_row_ = true;
    row_fits_ = rows_ < reserved_rows_;
    row_start_ = bytes_.size();
    column_ = 0;
}

void BatchWriter::append_validated(std::optional<ByteView> value)
{
    if (!in_row_ || column_ >= expected_.descriptors.size())
        throw Error::state("invalid batch cell admission");
    const auto& descriptor = expected_.descriptors[column_];
    if (!value && !descriptor.codec.nullable)
        invalid("SQL NULL requires nullable metadata");
    if (row_fits_)
    {
        const auto tables = base_ + (rows_ + 1) * per_row_;
        const auto payload = bytes_.size() - payload_offset_;
        const auto length = value ? value->size() : 0;
        if (payload > byte_limit_ - tables || length > byte_limit_ - tables - payload)
            row_fits_ = false;
        else
        {
            const auto offset = base_ + rows_ * per_row_ + column_ * 24;
            put_u16(bytes_, offset, static_cast<std::uint16_t>(descriptor.codec.id));
            put_u16(bytes_, offset + 2, static_cast<std::uint16_t>(!value));
            put_u32(bytes_, offset + 4, descriptor.bound_codec);
            put_u64(bytes_, offset + 8, value && !value->empty() ? bytes_.size() : 0);
            put_u64(bytes_, offset + 16, length);
            if (value)
            {
                const auto needed = bytes_.size() + length;
                if (needed > bytes_.capacity())
                {
                    const auto maximum = byte_limit_ + payload_offset_ - base_;
                    const auto next = std::min(maximum, std::max(needed, bytes_.capacity() * 2));
                    bytes_.reserve(next);
                }
                bytes_.insert(bytes_.end(), value->begin(), value->end());
            }
        }
    }
    ++column_;
}

bool BatchWriter::commit_row()
{
    if (!in_row_ || column_ != expected_.descriptors.size())
        throw Error::state("incomplete batch row");
    in_row_ = false;
    if (!row_fits_)
    {
        bytes_.resize(row_start_);
        return false;
    }
    ++rows_;
    return true;
}

Bytes BatchWriter::finish()
{
    if (finished_ || in_row_)
        throw Error::state("invalid batch completion");
    finished_ = true;
    const auto payload = base_ + rows_ * per_row_;
    const auto length = bytes_.size() - payload_offset_;
    if (length && payload != payload_offset_)
        std::memmove(bytes_.data() + payload, bytes_.data() + payload_offset_, length);
    for (std::size_t i = 0; i < rows_ * expected_.descriptors.size(); ++i)
    {
        const auto offset = base_ + i * 24;
        if (read_u16(bytes_, offset + 2) == 0)
        {
            const auto old_start = static_cast<std::size_t>(read_u64(bytes_, offset + 8));
            put_u64(bytes_, offset + 8,
                    read_u64(bytes_, offset + 16) ? old_start - payload_offset_ + payload : payload);
        }
    }
    bytes_.resize(payload + length);
    put_u64(bytes_, 16, bytes_.size());
    put_u32(bytes_, 56, rows_);
    put_u64(bytes_, 88, payload);
    return std::move(bytes_);
}

std::size_t parameter_frame_size(const Expectation& expected, std::span<const Scalar> values)
{
    if (values.size() != expected.descriptors.size())
        invalid("parameter count differs");
    auto size = add(96, multiply(values.size(), 40, "parameter table overflow"), "parameter table overflow");
    for (std::size_t i = 0; i < values.size(); ++i)
    {
        const auto& descriptor = expected.descriptors[i];
        const auto& scalar = values[i];
        if (scalar.codec != descriptor.codec)
            invalid("parameter codec differs");
        if (scalar.value)
        {
            size = add(size, scalar.value->size(), "parameter payload overflow");
        }
        else if (!descriptor.codec.nullable)
            invalid("SQL NULL requires nullable metadata");
    }
    if (size > MAX_BYTES)
        invalid("encoded batch exceeds size limit");
    return size;
}

Bytes Batch::encode(const Expectation& expected, std::uint32_t rows, const std::vector<std::optional<Bytes>>& values)
{
    std::vector<std::optional<ByteView>> views;
    views.reserve(values.size());
    for (const auto& value : values)
    {
        views.push_back(value ? std::optional<ByteView>(*value) : std::nullopt);
    }
    return encode_views(expected, rows, views);
}

Bytes Batch::encode_views(const Expectation& expected, std::uint32_t rows,
                          std::span<const std::optional<ByteView>> values)
{
    expected.validate(rows);
    const auto count = multiply(rows, expected.descriptors.size(), "cell count overflow");
    if (values.size() != count)
    {
        invalid("row-major cell count mismatch");
    }
    const auto cells_offset =
        add(96, multiply(expected.descriptors.size(), 16, "descriptor overflow"), "wire length overflow");
    const auto payload = add(cells_offset, multiply(count, 24, "cell size overflow"), "wire length overflow");
    auto total = payload;
    for (const auto& value : values)
    {
        total = add(total, value ? value->size() : 0, "payload length overflow");
    }
    if (total > MAX_BYTES)
    {
        invalid("wire byte budget");
    }
    Bytes bytes(total, 0);
    const auto magic = view("NWDB");
    std::copy(magic.begin(), magic.end(), bytes.begin());
    put_u16(bytes, 4, 1);
    put_u16(bytes, 6, expected.kind);
    put_u32(bytes, 8, 96);
    put_u64(bytes, 16, total);
    std::copy(expected.hash.begin(), expected.hash.end(), bytes.begin() + 24);
    put_u32(bytes, 56, rows);
    put_u32(bytes, 60, static_cast<std::uint32_t>(expected.descriptors.size()));
    put_u32(bytes, 64, expected.entity_id);
    put_u32(bytes, 72, expected.projection_id);
    put_u32(bytes, 76, static_cast<std::uint32_t>(expected.profile));
    put_u64(bytes, 80, 96);
    put_u64(bytes, 88, payload);
    for (std::size_t index = 0; index < expected.descriptors.size(); ++index)
    {
        const auto& descriptor = expected.descriptors[index];
        const auto offset = 96 + index * 16;
        put_u32(bytes, offset, descriptor.id);
        put_u16(bytes, offset + 4, static_cast<std::uint16_t>(descriptor.codec.id));
        put_u16(bytes, offset + 6, static_cast<std::uint16_t>(descriptor.codec.nullable));
        put_u32(bytes, offset + 8, descriptor.bound_codec);
    }
    auto cursor = payload;
    for (std::size_t index = 0; index < values.size(); ++index)
    {
        const auto& descriptor = expected.descriptors[index % expected.descriptors.size()];
        const auto& value = values[index];
        const auto offset = cells_offset + index * 24;
        put_u16(bytes, offset, static_cast<std::uint16_t>(descriptor.codec.id));
        put_u16(bytes, offset + 2, static_cast<std::uint16_t>(!value));
        put_u32(bytes, offset + 4, descriptor.bound_codec);
        if (value)
        {
            validate_scalar(descriptor.codec, *value, expected.profile);
            put_u64(bytes, offset + 8, value->empty() ? payload : cursor);
            put_u64(bytes, offset + 16, value->size());
            std::copy(value->begin(), value->end(), bytes.begin() + static_cast<std::ptrdiff_t>(cursor));
            cursor += value->size();
        }
        else if (!descriptor.codec.nullable)
        {
            invalid("SQL NULL requires nullable metadata");
        }
    }
    return bytes;
}

const Bytes& Batch::bytes() const noexcept
{
    return bytes_;
}
std::uint32_t Batch::rows() const noexcept
{
    return rows_;
}
const Expectation& Batch::expectation() const noexcept
{
    return expectation_;
}

std::optional<ByteView> Batch::cell(std::uint32_t row, std::size_t column) const
{
    if (row >= rows_ || column >= expectation_.descriptors.size())
    {
        invalid("cell ordinal out of range");
    }
    const auto offset = 96 + expectation_.descriptors.size() * 16 +
                        (static_cast<std::size_t>(row) * expectation_.descriptors.size() + column) * 24;
    if (read_u16(bytes_, offset + 2) == 1)
    {
        return std::nullopt;
    }
    const auto start = read_u64(bytes_, offset + 8);
    const auto length = read_u64(bytes_, offset + 16);
    return ByteView(bytes_).subspan(static_cast<std::size_t>(start), static_cast<std::size_t>(length));
}

void validate_scalar(const Codec& codec, ByteView bytes, Profile profile)
{
    codec.validate(profile);
    std::size_t fixed = 0;
    switch (codec.id)
    {
        case 1:
            fixed = 2;
            break;
        case 2:
        case 8:
        case 12:
        case 17:
            fixed = 4;
            break;
        case 3:
        case 9:
        case 10:
        case 11:
        case 15:
            fixed = 8;
            break;
        case 4:
            fixed = 1;
            break;
        case 7:
        case 13:
        case 14:
            fixed = 16;
            break;
        default:
            break;
    }
    if (fixed != 0 && bytes.size() != fixed)
    {
        invalid("scalar fixed width mismatch");
    }
    switch (codec.id)
    {
        case 4:
            if (bytes[0] > 1)
            {
                invalid("invalid Boolean");
            }
            break;
        case 5:
            if (!valid_utf8(bytes))
            {
                invalid("invalid UTF-8");
            }
            break;
        case 8:
        {
            const auto value = std::bit_cast<std::int32_t>(read_u32(bytes, 0));
            if (value < -719162 || value > 2932896)
            {
                invalid("date range");
            }
            break;
        }
        case 9:
            if (read_u64(bytes, 0) > 86399999999ULL)
            {
                invalid("time range");
            }
            break;
        case 10:
        {
            const auto value = std::bit_cast<std::int64_t>(read_u64(bytes, 0));
            if (value < -62135596800000000LL || value > 253402300799999999LL)
            {
                invalid("instant range");
            }
            break;
        }
        case 11:
            if (!std::isfinite(std::bit_cast<double>(read_u64(bytes, 0))))
            {
                invalid("nonfinite double");
            }
            break;
        case 12:
            if (!std::isfinite(std::bit_cast<float>(read_u32(bytes, 0))))
            {
                invalid("nonfinite single");
            }
            break;
        case 13:
        case 14:
        {
            if (bytes[12] > 28 || bytes[13] > 1 || bytes[14] != 0 || bytes[15] != 0)
            {
                invalid("decimal flags/range");
            }
            // Work on the 96-bit decimal coefficient as three portable 32-bit limbs.
            // Division by ten cannot overflow the 64-bit accumulator and does not
            // require a compiler-specific 128-bit integer on clang-cl.
            std::array<std::uint32_t, 3> limbs{read_u32(bytes, 0), read_u32(bytes, 4), read_u32(bytes, 8)};
            const bool zero = std::all_of(limbs.begin(), limbs.end(), [](auto value) { return value == 0; });
            const auto divide_ten = [](std::array<std::uint32_t, 3>& value)
            {
                std::uint64_t remainder = 0;
                for (std::size_t index = value.size(); index-- > 0;)
                {
                    const auto digit = (remainder << 32) | value[index];
                    value[index] = static_cast<std::uint32_t>(digit / 10);
                    remainder = digit % 10;
                }
                return remainder;
            };
            auto divided = limbs;
            const auto remainder = divide_ten(divided);
            const auto scale = bytes[12];
            if ((zero && (scale != 0 || bytes[13] != 0)) || (!zero && scale > 0 && remainder == 0))
            {
                invalid("noncanonical decimal");
            }
            if (codec.id == 13)
            {
                const auto config = parse_json(view(*codec.configuration), "decimal config");
                const auto target_scale = number(config, "scale");
                const auto precision = number(config, "precision");
                std::uint64_t digits = 0;
                while (std::any_of(limbs.begin(), limbs.end(), [](auto value) { return value != 0; }))
                {
                    (void)divide_ten(limbs);
                    ++digits;
                }
                if (scale > target_scale || (!zero && digits + target_scale - scale > precision))
                {
                    invalid("decimal precision loss/overflow");
                }
            }
            break;
        }
        case 16:
            validate_raw_json(bytes);
            break;
        default:
            break;
    }
}

Relation Relation::parse(ByteView bytes, std::shared_ptr<const Model> parent, std::shared_ptr<const Model> child)
{
    if (!parent || !child)
    {
        invalid("relation model identity mismatch");
    }
    const std::array<std::pair<std::uint16_t, std::uint16_t>, 8> schema{
        {{1, 1}, {2, 5}, {3, 1}, {4, 5}, {5, 7}, {6, 7}, {7, 7}, {8, 5}}};
    const auto fields = control_fields(bytes, 23, schema);
    if (fields[0].size() != 4 || fields[2].size() != 4 || read_u32(fields[0], 0) != parent->table_id() ||
        read_u32(fields[2], 0) != child->table_id() || !equal(fields[1], parent->hash()) ||
        !equal(fields[3], child->hash()) || fields[7].size() != 32)
    {
        invalid("relation model identity mismatch");
    }
    Relation relation;
    relation.parent_ = std::move(parent);
    relation.child_ = std::move(child);
    relation.parent_columns_ = decode_u32_vector(fields[4]);
    relation.child_columns_ = decode_u32_vector(fields[5]);
    const auto flags = decode_u32_vector(fields[6]);
    if (relation.parent_columns_.empty() || relation.parent_columns_.size() != relation.child_columns_.size() ||
        relation.parent_columns_.size() != flags.size() ||
        relation.parent_columns_.size() != relation.parent_->keys().size() ||
        std::any_of(flags.begin(), flags.end(), [](auto value) { return value > 1; }))
    {
        invalid("incomplete relation pair vectors");
    }
    std::set<std::uint32_t> child_ids;
    for (std::size_t ordinal = 0; ordinal < flags.size(); ++ordinal)
    {
        const auto& parent_column = relation.parent_->column(relation.parent_columns_[ordinal]);
        const auto& child_column = relation.child_->column(relation.child_columns_[ordinal]);
        if (parent_column.key_ordinal != ordinal || parent_column.codec.nullable ||
            child_column.codec.nullable != (flags[ordinal] == 1) ||
            !parent_column.codec.same_scalar(child_column.codec) || parent_column.collation != child_column.collation ||
            !child_ids.insert(child_column.id).second)
        {
            invalid("relation key/codec/configuration/collation/NULL mismatch");
        }
        relation.child_nullable_.push_back(flags[ordinal] == 1);
    }
    relation.canonical_ = relation.encode_identity();
    relation.hash_ = sha256(relation.canonical_);
    if (!equal(fields[7], relation.hash_))
    {
        invalid("relation hash mismatch");
    }
    return relation;
}

const Bytes& Relation::bytes() const noexcept
{
    return canonical_;
}
Hash Relation::hash() const noexcept
{
    return hash_;
}
const Model& Relation::parent() const noexcept
{
    return *parent_;
}
const Model& Relation::child() const noexcept
{
    return *child_;
}
const std::vector<std::uint32_t>& Relation::parent_columns() const noexcept
{
    return parent_columns_;
}
const std::vector<std::uint32_t>& Relation::child_columns() const noexcept
{
    return child_columns_;
}
const std::vector<bool>& Relation::child_nullable() const noexcept
{
    return child_nullable_;
}

Bytes Relation::encode_identity() const
{
    std::string nullable;
    for (const bool value : child_nullable_)
    {
        if (!nullable.empty())
        {
            nullable += ',';
        }
        nullable += bool_text(value);
    }
    return owned("{\"version\":1,\"parent_model\":" + json_quote(hex(parent_->hash())) + ",\"parent_table\":" +
                 std::to_string(parent_->table_id()) + ",\"child_model\":" + json_quote(hex(child_->hash())) +
                 ",\"child_table\":" + std::to_string(child_->table_id()) + ",\"parent_columns\":[" +
                 numbers(parent_columns_) + "],\"child_columns\":[" + numbers(child_columns_) +
                 "],\"child_nullable\":[" + nullable + "]}");
}

Bytes Relation::encode_record() const
{
    Bytes parent_table(4);
    Bytes child_table(4);
    put_u32(parent_table, 0, parent_->table_id());
    put_u32(child_table, 0, child_->table_id());
    const auto parent_hash = parent_->hash();
    const auto child_hash = child_->hash();
    std::vector<std::uint32_t> flags;
    for (const bool value : child_nullable_)
    {
        flags.push_back(static_cast<std::uint32_t>(value));
    }
    return encode_control(23, {{1, 1, std::move(parent_table)},
                               {2, 5, Bytes(parent_hash.begin(), parent_hash.end())},
                               {3, 1, std::move(child_table)},
                               {4, 5, Bytes(child_hash.begin(), child_hash.end())},
                               {5, 7, encode_u32_vector(parent_columns_)},
                               {6, 7, encode_u32_vector(child_columns_)},
                               {7, 7, encode_u32_vector(flags)},
                               {8, 5, Bytes(hash_.begin(), hash_.end())}});
}

Expectation Relation::parent_key_expectation(Profile profile, std::span<const std::uint32_t> bound_codecs) const
{
    if (bound_codecs.size() != parent_columns_.size())
    {
        invalid("parent-key codec bindings missing");
    }
    Expectation expected{4, profile, parent_->hash(), parent_->table_id(), 0, {}, 1};
    for (std::size_t index = 0; index < parent_columns_.size(); ++index)
    {
        const auto id = parent_columns_[index];
        expected.descriptors.push_back({id, parent_->column(id).codec, bound_codecs[index]});
    }
    return expected;
}

std::vector<ByteView> control_fields(ByteView bytes, std::uint16_t kind,
                                     std::span<const std::pair<std::uint16_t, std::uint16_t>> fields)
{
    if (fields.size() > MAX_NODES)
    {
        invalid("control field count exceeds the finite limit");
    }
    const auto start = 16 + fields.size() * 16;
    if (bytes.size() < start || bytes.size() > MAX_BYTES || read_u16(bytes, 0) != 1 || read_u16(bytes, 2) != kind ||
        read_u32(bytes, 4) != bytes.size() || read_u32(bytes, 8) != fields.size() || read_u32(bytes, 12) != 0)
    {
        invalid("invalid control envelope");
    }
    std::vector<ByteView> result;
    std::vector<std::pair<std::uint64_t, std::uint64_t>> ranges;
    result.reserve(fields.size());
    ranges.reserve(fields.size());
    for (std::size_t index = 0; index < fields.size(); ++index)
    {
        const auto offset = 16 + index * 16;
        const auto length = read_u32(bytes, offset + 4);
        const auto payload = read_u64(bytes, offset + 8);
        if (read_u16(bytes, offset) != fields[index].first || read_u16(bytes, offset + 2) != fields[index].second ||
            payload < start || payload > bytes.size() || length > bytes.size() - payload)
        {
            invalid("control field/length/offset mismatch");
        }
        if (length != 0)
        {
            ranges.emplace_back(payload, payload + length);
        }
        result.push_back(bytes.subspan(static_cast<std::size_t>(payload), length));
    }
    std::sort(ranges.begin(), ranges.end());
    for (std::size_t index = 1; index < ranges.size(); ++index)
    {
        if (ranges[index].first < ranges[index - 1].second)
        {
            invalid("overlapping control ranges");
        }
    }
    return result;
}

Fields Fields::parse(ByteView bytes, std::uint16_t kind, std::span<const FieldSpec> allowed)
{
    if (bytes.size() < 16)
    {
        invalid("truncated control");
    }
    const auto count = read_u32(bytes, 8);
    if (count > allowed.size() || count > MAX_NODES || bytes.size() < 16 + static_cast<std::size_t>(count) * 16)
    {
        invalid("control count");
    }
    std::vector<std::pair<std::uint16_t, std::uint16_t>> schema;
    std::uint16_t previous = 0;
    schema.reserve(count);
    for (std::uint32_t index = 0; index < count; ++index)
    {
        const auto id = read_u16(bytes, 16 + static_cast<std::size_t>(index) * 16);
        if (id <= previous)
        {
            invalid("control order");
        }
        previous = id;
        const auto found =
            std::find_if(allowed.begin(), allowed.end(), [id](const FieldSpec& field) { return field.id == id; });
        if (found == allowed.end())
        {
            invalid("unknown control field");
        }
        schema.emplace_back(id, found->type);
    }
    for (const auto& field : allowed)
    {
        if (field.required &&
            std::none_of(schema.begin(), schema.end(), [&field](const auto& item) { return item.first == field.id; }))
        {
            invalid("required control field");
        }
    }
    const auto values = control_fields(bytes, kind, schema);
    Fields result;
    result.values_.reserve(schema.size());
    for (std::size_t index = 0; index < schema.size(); ++index)
    {
        const auto type = schema[index].second;
        const auto value = values[index];
        if ((type == 1 && value.size() != 4) || ((type == 2 || type == 3) && value.size() != 8))
        {
            invalid("control width");
        }
        if (type == 4 && !valid_utf8(value))
        {
            invalid("control UTF8");
        }
        result.values_.emplace_back(schema[index].first, value);
    }
    return result;
}

Fields Fields::parse(ByteView bytes, std::uint16_t kind, std::initializer_list<FieldSpec> allowed)
{
    return parse(bytes, kind, std::span<const FieldSpec>(allowed.begin(), allowed.size()));
}

std::optional<ByteView> Fields::get(std::uint16_t id) const noexcept
{
    const auto found =
        std::find_if(values_.begin(), values_.end(), [id](const auto& field) { return field.first == id; });
    return found == values_.end() ? std::nullopt : std::optional<ByteView>(found->second);
}

ByteView Fields::bytes(std::uint16_t id) const
{
    const auto found = get(id);
    if (!found)
    {
        invalid("required field missing");
    }
    return *found;
}

std::uint32_t Fields::u32(std::uint16_t id) const
{
    const auto value = bytes(id);
    if (value.size() != 4)
    {
        invalid("u32 width");
    }
    return read_u32(value, 0);
}

std::uint64_t Fields::u64(std::uint16_t id) const
{
    const auto value = bytes(id);
    if (value.size() != 8)
    {
        invalid("u64 width");
    }
    return read_u64(value, 0);
}

std::string_view Fields::text(std::uint16_t id) const
{
    const auto value = bytes(id);
    if (!valid_utf8(value))
    {
        invalid("UTF8");
    }
    return {reinterpret_cast<const char*>(value.data()), value.size()};
}

bool Fields::boolean(std::uint16_t id) const
{
    const auto value = u32(id);
    if (value > 1)
    {
        invalid("boolean");
    }
    return value != 0;
}

bool Fields::flag(std::uint16_t id) const
{
    return boolean(id);
}

void Fields::require_set(std::initializer_list<std::uint16_t> ids) const
{
    for (const auto id : ids)
    {
        (void)bytes(id);
    }
}

Bytes encode_control(std::uint16_t kind, std::span<const ControlField> fields)
{
    auto size = add(16, multiply(fields.size(), 16, "control length overflow"), "control length overflow");
    for (const auto& field : fields)
    {
        size = add(size, field.bytes.size(), "control length overflow");
    }
    if (size > std::numeric_limits<std::uint32_t>::max() || fields.size() > std::numeric_limits<std::uint32_t>::max())
    {
        invalid("control length overflow");
    }
    Bytes result(size, 0);
    put_u16(result, 0, 1);
    put_u16(result, 2, kind);
    put_u32(result, 4, static_cast<std::uint32_t>(size));
    put_u32(result, 8, static_cast<std::uint32_t>(fields.size()));
    auto cursor = 16 + fields.size() * 16;
    for (std::size_t index = 0; index < fields.size(); ++index)
    {
        const auto& field = fields[index];
        const auto offset = 16 + index * 16;
        put_u16(result, offset, field.id);
        put_u16(result, offset + 2, field.type);
        put_u32(result, offset + 4, static_cast<std::uint32_t>(field.bytes.size()));
        put_u64(result, offset + 8, cursor);
        std::copy(field.bytes.begin(), field.bytes.end(), result.begin() + static_cast<std::ptrdiff_t>(cursor));
        cursor += field.bytes.size();
    }
    return result;
}

Bytes encode_control(std::uint16_t kind, std::initializer_list<ControlField> fields)
{
    return encode_control(kind, std::span<const ControlField>(fields.begin(), fields.size()));
}

std::vector<ByteView> sequence(ByteView bytes)
{
    if (bytes.size() < 4)
    {
        invalid("sequence header");
    }
    const auto count = read_u32(bytes, 0);
    if (count > MAX_NODES)
    {
        throw Error::capacity();
    }
    std::size_t at = 4;
    std::vector<ByteView> result;
    result.reserve(count);
    for (std::uint32_t index = 0; index < count; ++index)
    {
        if (at > bytes.size() || bytes.size() - at < 4)
        {
            invalid("sequence length");
        }
        const auto length = read_u32(bytes, at);
        at += 4;
        if (length > bytes.size() - at)
        {
            invalid("sequence range");
        }
        result.push_back(bytes.subspan(at, length));
        at += length;
    }
    if (at != bytes.size())
    {
        invalid("sequence trailing bytes");
    }
    return result;
}

std::vector<std::uint32_t> decode_u32_vector(ByteView bytes)
{
    if (bytes.size() % 4 != 0 || bytes.size() / 4 > MAX_NODES)
    {
        invalid("invalid u32 vector");
    }
    std::vector<std::uint32_t> result;
    result.reserve(bytes.size() / 4);
    for (std::size_t offset = 0; offset < bytes.size(); offset += 4)
    {
        result.push_back(read_u32(bytes, offset));
    }
    return result;
}

Bytes encode_u32_vector(std::span<const std::uint32_t> values)
{
    Bytes result(multiply(values.size(), 4, "vector length overflow"));
    for (std::size_t index = 0; index < values.size(); ++index)
    {
        put_u32(result, index * 4, values[index]);
    }
    return result;
}

Bytes NativeBulkProgress::encode() const
{
    Bytes row_bytes(8), batch_bytes(4), committed_bytes(4), outcome_bytes(4);
    put_u64(row_bytes, 0, rows);
    put_u32(batch_bytes, 0, batches);
    put_u32(committed_bytes, 0, static_cast<std::uint32_t>(committed));
    put_u32(outcome_bytes, 0, outcome);
    return encode_control(15, {{1, 2, std::move(row_bytes)},
                               {2, 1, std::move(batch_bytes)},
                               {3, 1, std::move(committed_bytes)},
                               {4, 1, std::move(outcome_bytes)}});
}

Hash sha256(ByteView bytes)
{
    Hash result{};
    if (SHA256(bytes.data(), bytes.size(), result.data()) == nullptr)
    {
        throw Error(NW_NATIVE_PANIC, 0, "SHA-256 computation failed");
    }
    return result;
}

std::string hex(ByteView bytes)
{
    constexpr std::string_view digits = "0123456789abcdef";
    std::string result;
    result.reserve(multiply(bytes.size(), 2, "hex length overflow"));
    for (const auto byte : bytes)
    {
        result += digits[byte >> 4];
        result += digits[byte & 15];
    }
    return result;
}

std::string json_quote(std::string_view value)
{
    constexpr std::string_view digits = "0123456789abcdef";
    std::string result = "\"";
    for (const unsigned char byte : value)
    {
        if (byte == '"' || byte == '\\')
        {
            result += '\\';
            result += static_cast<char>(byte);
        }
        else if (byte <= 0x1f)
        {
            result += "\\u00";
            result += digits[byte >> 4];
            result += digits[byte & 15];
        }
        else
        {
            result += static_cast<char>(byte);
        }
    }
    return result + '"';
}

std::uint32_t stable_id(std::span<const std::optional<std::string_view>> parts)
{
    const auto text = identity(parts);
    return read_u32(sha256(view(text)), 0);
}

std::uint32_t stable_id(std::initializer_list<std::optional<std::string_view>> parts)
{
    return stable_id(std::span<const std::optional<std::string_view>>(parts.begin(), parts.size()));
}

bool valid_utf8(ByteView bytes) noexcept
{
    std::size_t at = 0;
    while (at < bytes.size())
    {
        const auto first = bytes[at++];
        if (first < 0x80)
        {
            continue;
        }
        std::size_t extra = 0;
        std::uint32_t value = 0;
        std::uint32_t minimum = 0;
        if (first >= 0xc2 && first <= 0xdf)
        {
            extra = 1;
            value = first & 0x1f;
            minimum = 0x80;
        }
        else if (first >= 0xe0 && first <= 0xef)
        {
            extra = 2;
            value = first & 0x0f;
            minimum = 0x800;
        }
        else if (first >= 0xf0 && first <= 0xf4)
        {
            extra = 3;
            value = first & 0x07;
            minimum = 0x10000;
        }
        else
        {
            return false;
        }
        if (extra > bytes.size() - at)
        {
            return false;
        }
        for (std::size_t index = 0; index < extra; ++index)
        {
            const auto byte = bytes[at++];
            if ((byte & 0xc0) != 0x80)
            {
                return false;
            }
            value = (value << 6) | (byte & 0x3f);
        }
        if (value < minimum || value > 0x10ffff || (value >= 0xd800 && value <= 0xdfff))
        {
            return false;
        }
    }
    return true;
}

std::uint16_t read_u16(ByteView bytes, std::size_t offset)
{
    return read_unsigned<std::uint16_t>(bytes, offset);
}
std::uint32_t read_u32(ByteView bytes, std::size_t offset)
{
    return read_unsigned<std::uint32_t>(bytes, offset);
}
std::uint64_t read_u64(ByteView bytes, std::size_t offset)
{
    return read_unsigned<std::uint64_t>(bytes, offset);
}
void put_u16(Bytes& bytes, std::size_t offset, std::uint16_t value)
{
    write_unsigned(bytes, offset, value);
}
void put_u32(Bytes& bytes, std::size_t offset, std::uint32_t value)
{
    write_unsigned(bytes, offset, value);
}
void put_u64(Bytes& bytes, std::size_t offset, std::uint64_t value)
{
    write_unsigned(bytes, offset, value);
}

} // namespace nw
