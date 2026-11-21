#include "nw/contracts.hpp"
#include "nw/generated/schema.hpp"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <bit>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <limits>
#include <stdexcept>

namespace
{
using namespace nw;

void require(bool value, const char* message)
{
    if (!value)
        throw std::runtime_error(message);
}

template <std::size_t Count>
void schema_keys(const nlohmann::ordered_json& object, const std::array<std::string_view, Count>& expected)
{
    require(object.is_object() && object.size() == Count, "generated schema field count");
    std::size_t index = 0;
    for (auto item = object.begin(); item != object.end(); ++item, ++index)
        require(item.key() == expected[index], "generated schema canonical field order");
}

template <typename F> void rejects(F&& action)
{
    try
    {
        action();
    }
    catch (const Error&)
    {
        return;
    }
    throw std::runtime_error("malformed contract accepted");
}

Bytes bytes(std::string_view value)
{
    return Bytes(value.begin(), value.end());
}

template <typename T> Bytes little(T value)
{
    using U = std::conditional_t<sizeof(T) == 8, std::uint64_t,
                                 std::conditional_t<sizeof(T) == 4, std::uint32_t, std::uint16_t>>;
    auto bits = std::bit_cast<U>(value);
    Bytes result(sizeof(T));
    for (std::size_t i = 0; i < sizeof(T); ++i)
        result[i] = static_cast<std::uint8_t>(bits >> (8 * i));
    return result;
}

Bytes decimal(std::uint32_t coefficient, std::uint8_t scale, bool negative)
{
    Bytes result(16);
    put_u32(result, 0, coefficient);
    result[12] = scale;
    result[13] = negative;
    return result;
}
} // namespace

int main(int argc, char** argv)
{
    try
    {
        require(argc == 2, "managed fixture directory argument required");
        const std::filesystem::path root(argv[1]);
        auto read = [&](const char* name)
        {
            std::ifstream input(root / name, std::ios::binary);
            require(input.good(), "fixture missing");
            return Bytes(std::istreambuf_iterator<char>(input), {});
        };
        const auto parent_bytes = read("parent.json");
        const auto child_bytes = read("child.json");
        const auto parent_json = nlohmann::ordered_json::parse(parent_bytes);
        schema_keys(parent_json, schema::MODEL_KEYS);
        require(parent_json["version"] == schema::SCHEMA_VERSION, "generated metadata version");
        for (const auto& column : parent_json["columns"])
            schema_keys(column, schema::COLUMN_KEYS);
        auto parent = std::make_shared<Model>(Model::parse(parent_bytes, Profile::Sqlite));
        auto child = std::make_shared<Model>(Model::parse(child_bytes, Profile::Sqlite));
        require(parent->encode() == parent_bytes, "parent canonical bytes");
        require(child->encode() == child_bytes, "child canonical bytes");
        require(Model::parse(read("unicode-model.json"), Profile::Sqlite).encode() == read("unicode-model.json"),
                "unicode canonical bytes");
        rejects([&] { (void)Model::parse(read("configured-model.json"), Profile::Sqlite); });
        require(Model::parse(read("configured-model.json"), Profile::PostgreSql).encode() ==
                    read("configured-model.json"),
                "decimal metadata canonical bytes");
        const auto projection_bytes = read("projection.json");
        const auto scalar_bytes = read("scalar-projection.json");
        const auto projection_json = nlohmann::ordered_json::parse(projection_bytes);
        schema_keys(projection_json, schema::PROJECTION_KEYS);
        for (const auto& field : projection_json["fields"])
            schema_keys(field, schema::PROJECTION_FIELD_KEYS);
        require(Projection::parse(projection_bytes, Profile::Sqlite).encode() == projection_bytes,
                "projection canonical bytes");
        require(Projection::parse_scalar(scalar_bytes, Profile::Sqlite).encode() == scalar_bytes,
                "scalar projection bytes");
        const auto relation_bytes = read("relation.bin");
        schema_keys(nlohmann::ordered_json::parse(read("relation.json")), schema::RELATION_KEYS);
        require(read_u16(relation_bytes, 0) == schema::WIRE_VERSION &&
                    read_u16(relation_bytes, 2) == schema::RELATION_CONTROL_KIND &&
                    read_u32(relation_bytes, 8) * schema::CONTROL_ENTRY_BYTES + schema::CONTROL_HEADER_BYTES <=
                        relation_bytes.size(),
                "generated control envelope schema");
        const auto relation = Relation::parse(relation_bytes, parent, child);
        require(relation.bytes() == read("relation.json"), "relation identity canonical bytes");
        require(relation.encode_record() == relation_bytes, "relation control bytes");
        const std::array<std::uint32_t, 1> bound{3};
        const auto key_expected = relation.parent_key_expectation(Profile::Sqlite, bound);
        const auto key_bytes = read("key.bin");
        const auto key = Batch::parse(key_bytes, key_expected);
        const auto key_value = little(std::int64_t{123});
        require(std::ranges::equal(*key.cell(0, 0), key_value), "key value");
        require(Batch::encode(key_expected, 1, {key_value}) == key_bytes, "independent key encoding");
        Expectation expected{1, Profile::PostgreSql, {}, 0, 0, {}, 1};
        for (std::uint32_t i = 1; i <= 19; ++i)
        {
            Codec codec{i, std::nullopt, false};
            if (i == 13)
                codec.configuration = "{\"precision\":18,\"scale\":2}";
            if (i == 15)
                codec.configuration = "{\"scale\":2}";
            if (i == 18)
                codec = {5, std::nullopt, true};
            if (i == 19)
                codec = {6, std::nullopt, false};
            expected.descriptors.push_back({i, codec, i == 13 || i == 15 ? 1023 + i : codec.id});
        }
        const auto values_bytes = read("values.bin");
        require(read_u16(values_bytes, 4) == schema::WIRE_VERSION &&
                    read_u32(values_bytes, 8) == schema::HEADER_BYTES &&
                    read_u64(values_bytes, 80) == schema::HEADER_BYTES,
                "generated batch header schema");
        const auto values = Batch::parse(values_bytes, expected);
        const std::vector<std::optional<Bytes>> raw{
            little(std::int16_t{-7}),
            little(std::int32_t{42}),
            little(std::numeric_limits<std::int64_t>::max()),
            Bytes{1},
            bytes("e\u0301 🦀"),
            Bytes{0, 1, 255},
            Bytes{0, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff},
            little(std::int32_t{0}),
            little(std::uint64_t{123456}),
            little(std::int64_t{0}),
            little(-0.0),
            little(1.5F),
            decimal(1234, 2, false),
            decimal(123, 2, true),
            little(std::int64_t{123456}),
            bytes("{\"v\":1}"),
            little(std::int32_t{42}),
            std::nullopt,
            Bytes{}};
        require(Batch::encode(expected, 1, raw) == values_bytes, "all independent scalar bytes");
        require(!values.cell(0, 17), "SQL NULL");
        require(values.cell(0, 18) && values.cell(0, 18)->empty(), "empty blob");

        std::size_t malformed = 0;
        auto reject = [&](auto&& action)
        {
            rejects(action);
            ++malformed;
        };
        reject([&] { (void)Projection::parse(scalar_bytes, Profile::Sqlite); });
        reject([&] { (void)Projection::parse_scalar(projection_bytes, Profile::Sqlite); });
        reject(
            [&]
            {
                (void)Projection::parse_scalar(
                    bytes("{\"version\":1,\"fields\":[{\"ordinal\":0,\"name\":\"\",\"codec\":3,\"codec_config\":null,"
                          "\"nullable\":false},{\"ordinal\":1,\"name\":\"\",\"codec\":3,\"codec_config\":null,"
                          "\"nullable\":false}]}"),
                    Profile::Sqlite);
            });
        const std::vector<std::optional<Bytes>> two_keys{little(std::int64_t{1}), little(std::int64_t{2})};
        reject([&] { (void)Batch::encode(key_expected, 2, two_keys); });
        auto generic = key_expected;
        generic.exact_rows.reset();
        const auto multirow = Batch::encode(generic, 2, two_keys);
        reject([&] { (void)Batch::parse(multirow, key_expected); });
        require(Batch::parse(multirow, generic).rows() == 2, "generic mutation rows");
        auto generated_only = key_expected;
        generated_only.descriptors.clear();
        require(Batch::parse(Batch::encode(generated_only, 1, {}), generated_only).rows() == 1, "generated-only row");
        const Codec json{16, std::nullopt, false};
        for (const auto& text :
             {std::string("1e400"), std::string("{\"x\":1e400}"),
              std::string("{\"$serde_json::private::Number\":\"ordinary-text\"}"), std::string("\"\\ud800\""),
              std::string("\"\\udc00\""), std::string("{\"\\ud800\":1}"), std::string("\"\\ud800\\udc00\""),
              std::string(64, '[') + "0" + std::string(64, ']'), std::string(64, '[') + std::string(64, ']')})
            validate_scalar(json, bytes(text), Profile::Sqlite);
        for (const auto& text : {std::string(65, '[') + std::string(65, ']'), std::string("{\"x\":01}"),
                                 std::string("[1,]"), std::string("NaN"), std::string("\xef\xbb\xbf") + "0"})
            reject([&] { validate_scalar(json, bytes(text), Profile::Sqlite); });
        for (const auto& text : {std::string("\"\\ud80\""), std::string("\"\\ud80x\""), std::string("\"\\u\"")})
            rejects([&] { validate_scalar(json, bytes(text), Profile::Sqlite); });
        for (const auto& [from, to] : std::initializer_list<std::pair<std::string, std::string>>{
                 {"\"version\":1", "\"version\":2"},
                 {"\"version\":1", "\"version\":1,\"version\":1"},
                 {"\"relations\":[]", "\"relations\":[1]"},
                 {"\"codec\":3", "\"codec\":99"},
                 {"\"nullable\":false,\"key_ordinal\":0", "\"nullable\":true,\"key_ordinal\":0"}})
        {
            auto text = std::string(parent_bytes.begin(), parent_bytes.end());
            const auto pos = text.find(from);
            require(pos != std::string::npos, "model mutation target");
            text.replace(pos, from.size(), to);
            reject([&] { (void)Model::parse(bytes(text), Profile::Sqlite); });
        }
        auto space = parent_bytes;
        space.push_back(' ');
        reject([&] { (void)Model::parse(space, Profile::Sqlite); });
        auto invalid_utf8 = parent_bytes;
        invalid_utf8[20] = 255;
        reject([&] { (void)Model::parse(invalid_utf8, Profile::Sqlite); });
        for (const auto& [offset, replacement] : std::initializer_list<std::pair<std::size_t, Bytes>>{
                 {4, {2}},
                 {12, {1}},
                 {76, {9}},
                 {68, {1}},
                 {56, little(std::numeric_limits<std::uint32_t>::max())},
                 {88, little(std::numeric_limits<std::uint64_t>::max())},
                 {108, {1}},
                 {100, {99}}})
        {
            auto copy = values_bytes;
            std::copy(replacement.begin(), replacement.end(), copy.begin() + static_cast<std::ptrdiff_t>(offset));
            reject([&] { (void)Batch::parse(copy, expected); });
        }
        const auto cells = schema::HEADER_BYTES + expected.descriptors.size() * schema::DESCRIPTOR_BYTES;
        require(read_u64(values_bytes, 88) == cells + expected.descriptors.size() * schema::CELL_BYTES,
                "generated descriptor and cell extents");
        for (const auto& [offset, replacement] : std::initializer_list<std::pair<std::size_t, Bytes>>{
                 {cells + 8, little(std::uint64_t{0})},
                 {cells + 16, little(std::numeric_limits<std::uint64_t>::max())},
                 {cells + 2, {1}},
                 {cells + 17 * schema::CELL_BYTES, {6}}})
        {
            auto copy = values_bytes;
            std::copy(replacement.begin(), replacement.end(), copy.begin() + static_cast<std::ptrdiff_t>(offset));
            reject([&] { (void)Batch::parse(copy, expected); });
        }
        auto overlap = values_bytes;
        put_u64(overlap, cells + 32, read_u64(overlap, cells + 8));
        reject([&] { (void)Batch::parse(overlap, expected); });
        for (const auto index : {3U, 4U, 10U, 12U})
        {
            auto copy = values_bytes;
            const auto start = read_u64(copy, cells + index * schema::CELL_BYTES + 8);
            if (index == 3)
                copy[start] = 2;
            if (index == 4)
                copy[start] = 255;
            if (index == 10)
                put_u64(copy, start, std::bit_cast<std::uint64_t>(std::numeric_limits<double>::quiet_NaN()));
            if (index == 12)
                copy[start + 15] = 1;
            reject([&] { (void)Batch::parse(copy, expected); });
        }
        for (const auto offset : std::vector<std::size_t>{0, 2, 12, 16, 18, 20, 24, relation_bytes.size() - 1})
        {
            auto copy = relation_bytes;
            copy[offset] ^= 1;
            reject([&] { (void)Relation::parse(copy, parent, child); });
        }
        reject([&] { (void)Relation::parse(relation_bytes, child, parent); });
        MetadataRegistry registry(Profile::Sqlite, 1, parent_bytes.size());
        auto first = registry.register_model(parent_bytes, parent->hash());
        auto second = registry.register_model(parent_bytes, parent->hash());
        require(first == second, "metadata deduplication");
        const auto before = registry.counts();
        rejects([&] { (void)registry.register_model(child_bytes, child->hash()); });
        require(registry.counts() == before, "capacity registration atomicity");
        rejects([&] { (void)registry.register_model(parent_bytes, {}); });
        require(registry.counts() == before, "hash registration atomicity");
        rejects([] { (void)profile_from(0); });
        rejects([] { (void)profile_from(3); });
        std::cout << "{\"status\":\"passed\",\"platform\":\"linux-x64\",\"cross_language_goldens\":10,\"malformed_"
                     "rejections\":"
                  << malformed << ",\"scalar_kinds\":17,\"registry_atomicity\":true,\"parent_hash\":\""
                  << hex(parent->hash()) << "\",\"relation_hash\":\"" << hex(relation.hash()) << "\"}\n";
    }
    catch (const std::exception& error)
    {
        std::cerr << "FAIL " << error.what() << '\n';
        return 1;
    }
}
