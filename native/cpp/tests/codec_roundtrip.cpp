// SPDX-License-Identifier: MIT
// B07 real-service roundtrip of the unchanged managed JIT/AOT vector producer.
#include "nw/provider.hpp"
#include "nw/scalar.hpp"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <stdexcept>

namespace
{
using namespace nw;
using Json = nlohmann::json;
Bytes read(const std::filesystem::path& path)
{
    std::ifstream input(path, std::ios::binary);
    if (!input)
        throw std::runtime_error("cannot read " + path.string());
    return {std::istreambuf_iterator<char>(input), {}};
}
ByteView view(std::string_view text)
{
    return {reinterpret_cast<const std::uint8_t*>(text.data()), text.size()};
}
void check(bool value, std::string_view message)
{
    if (!value)
        throw std::runtime_error(std::string(message));
}
Bytes unhex(std::string_view input)
{
    check(input.size() % 2 == 0, "odd hex length");
    Bytes output;
    output.reserve(input.size() / 2);
    for (std::size_t i = 0; i < input.size(); i += 2)
        output.push_back(static_cast<std::uint8_t>(std::stoul(std::string(input.substr(i, 2)), nullptr, 16)));
    return output;
}
Codec codec(const Json& value)
{
    return {value.at("logicalId").get<std::uint32_t>(),
            value.at("configuration").is_null() ? std::nullopt
                                                : std::optional(value.at("configuration").get<std::string>()),
            value.at("nullable").get<bool>()};
}
Expectation expectation(const Json& value)
{
    const auto logical = codec(value);
    const auto schema =
        "{\"version\":1,\"parameters\":[{\"id\":1,\"name\":\"p0\",\"codec\":" + std::to_string(logical.id) +
        ",\"codec_config\":" + logical.configuration.value_or("null") +
        ",\"nullable\":" + (logical.nullable ? "true" : "false") + "}]}";
    auto supplied = unhex(value.at("hash").get<std::string>());
    const auto hash = sha256(view(schema));
    check(supplied.size() == hash.size() && std::equal(hash.begin(), hash.end(), supplied.begin()),
          "managed/native canonical parameter hash differs");
    return {1, profile_from(value.at("profile").get<std::uint32_t>()),   hash, 0,
            0, {{1, logical, value.at("boundId").get<std::uint32_t>()}}, 1};
}
std::string pg_sql(std::uint32_t id)
{
    const char* type = nullptr;
    switch (id)
    {
        case 1:
            type = "smallint";
            break;
        case 2:
        case 17:
            type = "integer";
            break;
        case 3:
        case 15:
            type = "bigint";
            break;
        case 4:
            type = "boolean";
            break;
        case 5:
            type = "text";
            break;
        case 6:
            type = "bytea";
            break;
        case 7:
            type = "uuid";
            break;
        case 8:
            type = "date";
            break;
        case 9:
            type = "time";
            break;
        case 10:
            type = "timestamptz";
            break;
        case 11:
            type = "double precision";
            break;
        case 12:
            type = "real";
            break;
        case 13:
            type = "numeric";
            break;
        case 16:
            type = "jsonb";
            break;
        default:
            throw std::runtime_error("unsupported fixture scalar");
    }
    return "SELECT $1::" + std::string(type);
}
template <typename Action> void reject(Action action, std::uint32_t code = 0)
{
    try
    {
        action();
    }
    catch (const Error& error)
    {
        check(code == 0 || error.code == code, "wrong native refusal category");
        return;
    }
    throw std::runtime_error("invalid scalar or wire accepted");
}
std::optional<Bytes> field(ProviderConnection& connection, std::string_view sql, const Codec& codec,
                           const std::vector<Scalar>& parameters = {}, std::size_t maximum = MAX_BYTES)
{
    auto statement = connection.query(sql, parameters, {});
    auto row = statement->next({codec}, maximum, {});
    check(row && row->size() == 1, "roundtrip result row shape");
    auto value = (*row)[0];
    check(!statement->next({codec}, maximum, {}), "roundtrip returned excess rows");
    statement->close();
    return value;
}
} // namespace

int main(int argc, char** argv)
{
    try
    {
        if (argc != 2)
            throw std::runtime_error("managed vector directory required");
        const std::filesystem::path directory(argv[1]);
        // The parent supplies a fixed test endpoint; this process never mutates its environment.
        // NOLINTNEXTLINE(concurrency-mt-unsafe)
        const auto* url = std::getenv("NATIVEWEAVE_TEST_POSTGRES_URL");
        if (!url)
            throw std::runtime_error("NATIVEWEAVE_TEST_POSTGRES_URL is required");
        ProviderOptions pg_options{};
        pg_options.profile = Profile::PostgreSql;
        pg_options.connection = url;
        pg_options.verify_tls = false;
        auto pg = ProviderConnection::open(pg_options, {});
        ProviderOptions sqlite_options{};
        sqlite_options.connection = ":memory:";
        sqlite_options.sqlite_mode = 3;
        sqlite_options.sqlite_journal = 2;
        auto sqlite = ProviderConnection::open(sqlite_options, {});
        const auto pg_version = *pg->query_text("SELECT version()", {}).at(0).at(0);
        const auto sqlite_version = *sqlite->query_text("SELECT sqlite_version()", {}).at(0).at(0);
        std::cout << "PostgreSQL: " << pg_version << "\nSQLite: " << sqlite_version << '\n';
        const auto values = Json::parse(read(directory / "cases.json"));
        auto results = Json::array();
        std::size_t provider_sends = 0;
        for (const auto& value : values)
        {
            const auto shape = expectation(value);
            const auto wire = unhex(value.at("wire").get<std::string>());
            auto parameters = decode_parameters(wire, shape, MAX_NODES, MAX_BYTES, MAX_BYTES);
            check(parameters.size() == 1, "one scalar parameter required");
            ++provider_sends;
            auto bytes = shape.profile == Profile::PostgreSql
                             ? field(*pg, pg_sql(codec(value).id), codec(value), parameters)
                             : field(*sqlite, "SELECT ?1", codec(value), parameters);
            Json encoded = nullptr;
            if (bytes)
            {
                auto text = hex(*bytes);
                std::transform(text.begin(), text.end(), text.begin(), [](unsigned char c) { return std::toupper(c); });
                encoded = text;
            }
            results.push_back({{"name", value.at("name")}, {"hex", encoded}});
        }
        std::cout << "PASS actual C# wire/C++ provider scalar roundtrips: " << results.size() << '\n';
        const Codec numeric{13, "{\"precision\":29,\"scale\":28}", false};
        for (const auto* sql : {"SELECT 1e100::numeric", "SELECT 1e-30::numeric", "SELECT 'NaN'::numeric"})
            reject([&] { (void)field(*pg, sql, numeric); });
        reject([&] { (void)field(*pg, "SELECT 1.239::numeric", {13, "{\"precision\":4,\"scale\":2}", false}); });
        reject([&] { (void)field(*pg, "SELECT NULL::text", {3, {}, true}); }, 4);
        reject(
            [&]
            {
                auto statement = pg->query("SELECT NULL::text", {}, {});
                (void)statement->next({{5, {}, true}, {5, {}, true}}, 4096, {});
            },
            4);
        reject([&] { (void)field(*pg, "SELECT 'Infinity'::double precision", {11, {}, false}); });
        for (const auto& [sql, id] :
             std::vector<std::pair<std::string, std::uint32_t>>{{"SELECT 'infinity'::timestamptz", 10},
                                                                {"SELECT '-infinity'::timestamptz", 10},
                                                                {"SELECT 'infinity'::date", 8},
                                                                {"SELECT '-infinity'::date", 8},
                                                                {"SELECT '24:00:00'::time", 9}})
            reject([&] { (void)field(*pg, sql, {id, {}, false}); });
        for (const auto& [sql, id] :
             std::vector<std::pair<std::string, std::uint32_t>>{{"SELECT 32768", 1},
                                                                {"SELECT 2147483648", 2},
                                                                {"SELECT 2", 4},
                                                                {"SELECT 1e30", 3},
                                                                {"SELECT 0.1", 12},
                                                                {"SELECT X'0011'", 7},
                                                                {"SELECT '2026-1-2'", 8},
                                                                {"SELECT '12:34:56.123456789'", 9},
                                                                {"SELECT '1.00'", 14},
                                                                {"SELECT 'not json'", 16}})
            reject([&] { (void)field(*sqlite, sql, {id, {}, false}); });
        reject([&] { (void)field(*sqlite, "SELECT 'too long'", {5, {}, false}, {}, 2); }, 14);
        reject(
            [&]
            {
                auto statement = sqlite->query("SELECT 'too long'", {}, {});
                (void)statement->next({{5, {}, true}, {5, {}, true}}, 2, {});
            },
            4);
        // PostgreSQL's microsecond storage cannot preserve an input with nine
        // fractional digits. The unchanged managed producer separately rejects
        // sub-microsecond TimeOnly/DateTimeOffset before native admission.
        const auto precision = field(*pg, "SELECT '2023-11-14 22:13:20.123456789+00'::timestamptz", {10, {}, false});
        check(precision && (read_u64(*precision, 0) % 1'000'000) * 1000 != 123'456'789,
              "actual returned timestamp did not exercise sub-microsecond precision loss");
        // The old SQLx argument-owner failure test becomes a real partial-bind
        // failure. The candidate must drop the half-built statement, perform no
        // write, and admit a fresh independent set of arguments afterward.
        for (auto* connection : {sqlite.get(), pg.get()})
        {
            (void)connection->execute("CREATE TEMP TABLE codec_atomic_probe(a BIGINT,b BIGINT)", {}, {});
            Bytes value(8);
            put_u64(value, 0, 7);
            const auto sql = connection->profile() == Profile::Sqlite ? "INSERT INTO codec_atomic_probe VALUES(?1,?2)"
                                                                      : "INSERT INTO codec_atomic_probe VALUES($1,$2)";
            reject([&]
                   { (void)connection->query(sql, {{{3, {}, false}, value}, {{3, {}, false}, Bytes{1, 2, 3}}}, {}); });
            check(connection->query_text("SELECT count(*) FROM codec_atomic_probe", {}).at(0).at(0) == "0",
                  "partially encoded argument set reached the database");
            (void)connection->execute(sql, {{{3, {}, false}, value}, {{3, {}, false}, value}}, {});
            check(connection->query_text("SELECT count(*) FROM codec_atomic_probe", {}).at(0).at(0) == "1",
                  "failed argument owner blocked a fresh statement");
        }
        std::cout << "PASS provider conversion refusals and precision-loss negative control\n";

        const auto sends_before = provider_sends;
        const auto original = unhex(values.at(0).at("wire").get<std::string>());
        const auto shape = expectation(values.at(0));
        std::size_t rejected = 0;
        const auto malformed = [&](ByteView wire)
        {
            try
            {
                (void)decode_parameters(wire, shape, MAX_NODES, MAX_BYTES, MAX_BYTES);
            }
            catch (const Error&)
            {
                ++rejected;
            }
        };
        for (std::size_t length = 0; length < original.size(); ++length)
            malformed(ByteView(original).first(length));
        std::uint64_t rng = 0x1234'5678'9abc'def0ULL;
        for (std::size_t i = 0; i < 4096; ++i)
        {
            rng ^= rng << 13;
            rng ^= rng >> 7;
            rng ^= rng << 17;
            auto bytes = original;
            bytes[rng % bytes.size()] ^= static_cast<std::uint8_t>(rng >> 32) | 1;
            malformed(bytes);
        }
        check(rejected > 3000 && provider_sends == sends_before, "malformed validation dispatched or missed cases");
        for (const auto profile : {Profile::Sqlite, Profile::PostgreSql})
        {
            auto parent = std::make_shared<Model>(Model::parse(read(directory / "parent.json"), profile));
            auto child = std::make_shared<Model>(Model::parse(read(directory / "child.json"), profile));
            const auto record = read(directory / "relation.bin");
            const auto relation = Relation::parse(record, parent, child);
            const std::array<std::uint32_t, 2> bindings{2, 7};
            const auto expected = relation.parent_key_expectation(profile, bindings);
            const auto key = read(directory / ("key-" + std::to_string(static_cast<std::uint32_t>(profile)) + ".bin"));
            const auto parameters = decode_parameters(key, expected, MAX_NODES, MAX_BYTES, MAX_BYTES);
            check(parameters.size() == 2, "composite key arity");
            for (const auto& parameter : parameters)
                if (parameter.value)
                {
                    if (profile == Profile::Sqlite)
                        (void)sqlite_encode(parameter.codec, *parameter.value);
                    else
                        (void)postgres_encode(parameter.codec, *parameter.value);
                }
            auto reordered = expected;
            std::swap(reordered.descriptors[0], reordered.descriptors[1]);
            reject([&] { (void)decode_parameters(key, reordered, MAX_NODES, MAX_BYTES, MAX_BYTES); });
            auto optional = expected;
            optional.descriptors[0].codec.nullable = true;
            reject([&] { (void)decode_parameters(key, optional, MAX_NODES, MAX_BYTES, MAX_BYTES); });
            auto bad_record = record;
            bad_record.back() ^= 1;
            reject([&] { (void)Relation::parse(bad_record, parent, child); });
            const auto parsed = Batch::parse(key, expected);
            check(hex(*parsed.cell(0, 1)) == "00112233445566778899aabbccddeeff", "composite key UUID byte order");
        }
        std::ofstream(directory / "results.json") << results.dump(2) << '\n';
        std::ofstream(directory / "native-summary.json")
            << Json{{"roundtrips", results.size()},
                    {"malformedRejected", rejected},
                    {"providerSendsForVectors", provider_sends},
                    {"postgres", pg_version},
                    {"sqlite", sqlite_version},
                    {"mode", "actual pinned C++ libpq/SQLite component; unchanged C# producer"}}
                   .dump(2)
            << '\n';
        pg->close();
        sqlite->close();
        std::cout << "PASS malformed wire lengths/mutations: " << rejected
                  << "; composite/optional key binding; zero validation provider sends\n";
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << "FAIL codec roundtrip: " << error.what() << '\n';
        return 1;
    }
}
