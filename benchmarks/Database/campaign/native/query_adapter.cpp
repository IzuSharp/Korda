#include "query_adapter.hpp"

#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <cctype>
#include <cerrno>
#include <charconv>
#include <fstream>
#include <limits>
#include <set>
#include <string_view>
#include <utility>

#include <libpq-fe.h>
#include <nlohmann/json.hpp>
#include <poll.h>
#include <sqlite3.h>

namespace db_perf
{
namespace
{
using Json = nlohmann::json;
using Clock = std::chrono::steady_clock;
std::atomic<std::uint64_t> connections{0};
std::atomic<std::uint64_t> sqlite_statements{0};
std::atomic<std::uint64_t> postgres_results{0};
std::atomic<std::uint64_t> unconfirmed_sqlite_closes{0};

[[noreturn]] void reject(std::string message)
{
    throw DatabaseError("contract", std::move(message));
}

std::uint64_t unsigned_decimal(const Json& value)
{
    if (value.is_number_unsigned())
        return value.get<std::uint64_t>();
    if (value.is_number_integer())
    {
        const auto number = value.get<std::int64_t>();
        if (number < 0)
            reject("Negative unsigned contract value");
        return static_cast<std::uint64_t>(number);
    }
    const auto text = value.get<std::string>();
    std::uint64_t number = 0;
    const auto parsed = std::from_chars(text.data(), text.data() + text.size(), number);
    if (parsed.ec != std::errc{} || parsed.ptr != text.data() + text.size())
        reject("Invalid decimal contract value");
    return number;
}

std::string identifier(const Json& value)
{
    if (value.is_string())
        return value.get<std::string>();
    if (value.is_number_integer())
        return value.dump();
    reject("Case ID must be a string or integer");
}

ValueKind value_kind(std::string_view type)
{
    if (type == "int32" || type == "nullable-int32")
        return ValueKind::Int32;
    if (type == "int64" || type == "nullable-int64")
        return ValueKind::Int64;
    if (type == "string" || type == "text")
        return ValueKind::String;
    if (type == "money" || type == "nullable-money")
        return ValueKind::Money;
    if (type == "settings")
        return ValueKind::Settings;
    reject("Unsupported value type: " + std::string(type));
}

RowKind row_kind(std::string_view name)
{
    if (name == "text" || name == "raw-sql")
        return RowKind::Text;
    if (name == "money")
        return RowKind::Money;
    if (name == "json")
        return RowKind::Json;
    if (name == "wide")
        return RowKind::Wide;
    if (name == "left-join" || name == "inner-join")
        return RowKind::Join;
    if (name == "customer-totals")
        return RowKind::CustomerTotal;
    if (name == "product-totals")
        return RowKind::ProductTotal;
    if (name == "empty-sum")
        return RowKind::NullableMoney;
    if (name == "window")
        return RowKind::Rank;
    if (name == "running-total")
        return RowKind::Running;
    if (name == "three-join")
        return RowKind::Detail;
    if (name == "related")
        return RowKind::Related;
    if (name == "tx-commit" || name == "tx-rollback" || name == "partial-update" || name == "stale-version")
        return RowKind::State;
    static constexpr std::array scalar_names{"numeric",
                                             "optional-filter",
                                             "range",
                                             "null-notes",
                                             "literal-note",
                                             "cursor",
                                             "offset",
                                             "exists",
                                             "in-subquery",
                                             "cte",
                                             "union-all",
                                             "union",
                                             "intersect",
                                             "except",
                                             "anti-association",
                                             "nullable-order",
                                             "stream-large",
                                             "optional-filter-absent"};
    if (std::find(scalar_names.begin(), scalar_names.end(), name) != scalar_names.end())
        return RowKind::Int64;
    reject("Unsupported read case: " + std::string(name));
}

std::string_view materialization_name(RowKind kind)
{
    switch (kind)
    {
        case RowKind::Int64:
            return "scalar-int64";
        case RowKind::Text:
            return "text";
        case RowKind::Money:
            return "money";
        case RowKind::Json:
            return "json";
        case RowKind::Wide:
            return "wide";
        case RowKind::Join:
            return "join";
        case RowKind::CustomerTotal:
            return "customer-total";
        case RowKind::ProductTotal:
            return "product-total";
        case RowKind::NullableMoney:
            return "nullable-money";
        case RowKind::Rank:
            return "rank";
        case RowKind::Running:
            return "running";
        case RowKind::Detail:
            return "detail";
        case RowKind::Related:
            return "related";
        case RowKind::State:
            return "state";
    }
    reject("Unknown materialization shape");
}

void require_shape(const ReadCase& query)
{
    using K = ValueKind;
    using Field = std::pair<K, bool>;
    std::vector<Field> expected;
    switch (query.row_kind)
    {
        case RowKind::Int64:
            expected = {{K::Int64, false}};
            break;
        case RowKind::Text:
        case RowKind::Join:
            expected = {{K::Int64, false}, {K::String, true}};
            break;
        case RowKind::Money:
            expected = {{K::Int64, false}, {K::Money, false}};
            break;
        case RowKind::Json:
            expected = {{K::Int64, false}, {K::Settings, false}};
            break;
        case RowKind::Wide:
            expected = {{K::Int64, false}, {K::Int64, false},    {K::Int64, false},
                        {K::Int64, true},  {K::Money, false},    {K::String, true},
                        {K::Int64, false}, {K::Settings, false}, {K::String, false}};
            break;
        case RowKind::CustomerTotal:
            expected = {{K::Int64, true}, {K::Int64, false}, {K::Money, true}};
            break;
        case RowKind::ProductTotal:
            expected = {{K::String, false}, {K::Int64, true}};
            break;
        case RowKind::NullableMoney:
            expected = {{K::Money, true}};
            break;
        case RowKind::Rank:
            expected = {{K::Int64, false}, {K::Int64, false}};
            break;
        case RowKind::Running:
            expected = {{K::Int64, false}, {K::Int64, true}};
            break;
        case RowKind::Detail:
            expected = {{K::Int64, false}, {K::String, false}, {K::String, false}, {K::Int64, false}};
            break;
        case RowKind::Related:
            expected = {{K::Int64, false},  {K::Int64, false}, {K::Int64, false},
                        {K::String, false}, {K::Int64, false}, {K::Int32, false}};
            break;
        case RowKind::State:
            expected = {{K::Int64, false}, {K::Int64, false}, {K::Int64, false}, {K::String, true}};
            break;
    }
    if (query.columns.size() != expected.size())
        reject("Column count differs from owned row shape: " + query.name);
    for (std::size_t i = 0; i < expected.size(); ++i)
    {
        const auto& column = query.columns[i];
        if (column.kind != expected[i].first || column.nullable != expected[i].second)
            reject("Column kind/nullability differs from owned row shape: " + query.name + "/" + column.name);
    }
}

struct Digest
{
    std::uint64_t value = 14695981039346656037ULL;
    void byte(unsigned char byte)
    {
        value = (value ^ byte) * 1099511628211ULL;
    }
    void integer(std::int64_t number)
    {
        auto bits = static_cast<std::uint64_t>(number);
        for (int i = 0; i < 8; ++i)
        {
            byte(static_cast<unsigned char>(bits & 255));
            bits >>= 8;
        }
    }
    void text(std::string_view text)
    {
        integer(static_cast<std::int64_t>(text.size()));
        for (unsigned char item : text)
            byte(item);
    }
    void text(const std::optional<std::string>& text)
    {
        if (text)
            this->text(std::string_view(*text));
        else
            integer(-1);
    }
    void nullable(std::optional<std::int64_t> value)
    {
        byte(value.has_value());
        if (value)
            integer(*value);
    }
};

struct Money
{
    std::int64_t cents;
};
struct Settings
{
    std::string channel;
    bool gift;
};

// This is the fixed workload's owned typed JSON object, not a raw-byte checksum.
// Arbitrary JSON schemas and System.Text.Json edge-case equivalence are not claimed.
struct SettingsSax final : nlohmann::json_sax<Json>
{
    Settings value{};
    int key_kind = 0;
    bool entered = false;
    bool ended = false;
    bool channel = false;
    bool gift = false;
    bool null() override
    {
        return false;
    }
    bool boolean(bool input) override
    {
        if (key_kind != 2 || gift)
            return false;
        value.gift = input;
        gift = true;
        key_kind = 0;
        return true;
    }
    bool number_integer(number_integer_t) override
    {
        return false;
    }
    bool number_unsigned(number_unsigned_t) override
    {
        return false;
    }
    bool number_float(number_float_t, const string_t&) override
    {
        return false;
    }
    bool string(string_t& input) override
    {
        if (key_kind != 1 || channel)
            return false;
        value.channel = std::move(input);
        channel = true;
        key_kind = 0;
        return true;
    }
    bool binary(binary_t&) override
    {
        return false;
    }
    bool start_object(std::size_t) override
    {
        if (entered)
            return false;
        entered = true;
        return true;
    }
    bool key(string_t& input) override
    {
        if (!entered || ended || key_kind != 0)
            return false;
        key_kind = input == "Channel" ? 1 : input == "Gift" ? 2 : 0;
        return key_kind != 0;
    }
    bool end_object() override
    {
        ended = true;
        return channel && gift && key_kind == 0;
    }
    bool start_array(std::size_t) override
    {
        return false;
    }
    bool end_array() override
    {
        return false;
    }
    bool parse_error(std::size_t, const std::string&, const nlohmann::detail::exception&) override
    {
        return false;
    }
};

std::unique_ptr<Settings> parse_settings(std::string_view bytes)
{
    SettingsSax sax;
    if (!Json::sax_parse(bytes, &sax) || !sax.ended)
        throw DatabaseError("materialization", "JSON differs from the fixed Channel/Gift object");
    return std::make_unique<Settings>(std::move(sax.value));
}

struct TextRow
{
    std::int64_t id;
    std::optional<std::string> note;
};
struct MoneyRow
{
    std::int64_t id;
    Money total;
};
struct JsonRow
{
    std::int64_t id;
    std::unique_ptr<Settings> settings;
};
struct OrderRow
{
    std::int64_t tenant, id, created;
    std::optional<std::int64_t> customer;
    Money total;
    std::optional<std::string> note;
    std::int64_t version;
    std::unique_ptr<Settings> settings;
    std::string memo;
};
struct JoinRow
{
    std::int64_t id;
    std::optional<std::string> name;
};
struct CustomerTotalRow
{
    std::optional<std::int64_t> customer;
    std::int64_t count;
    std::optional<Money> total;
};
struct ProductTotalRow
{
    std::string product;
    std::optional<std::int64_t> quantity;
};
struct RankRow
{
    std::int64_t id, rank;
};
struct RunningRow
{
    std::int64_t id;
    std::optional<std::int64_t> quantity;
};
struct DetailRow
{
    std::int64_t order;
    std::string name, product;
    std::int64_t quantity;
};
struct OrderLineRow
{
    std::int64_t id, tenant, order;
    std::string product;
    std::int64_t quantity;
};

void nullable_money(Digest& digest, const std::optional<Money>& money)
{
    digest.byte(money.has_value());
    if (money)
        digest.integer(money->cents);
}

// Out-of-line consumption keeps the owned business object alive through the
// business boundary. No provider pointer is retained in a DTO.
[[gnu::noinline]] void consume(Digest& digest, std::unique_ptr<TextRow> row)
{
    digest.integer(row->id);
    digest.text(row->note);
}
[[gnu::noinline]] void consume(Digest& digest, std::unique_ptr<MoneyRow> row)
{
    digest.integer(row->id);
    digest.integer(row->total.cents);
}
[[gnu::noinline]] void consume(Digest& digest, std::unique_ptr<JsonRow> row)
{
    digest.integer(row->id);
    digest.text(std::string_view(row->settings->channel));
    digest.byte(row->settings->gift);
}
[[gnu::noinline]] void consume(Digest& digest, std::unique_ptr<OrderRow> row)
{
    digest.integer(row->tenant);
    digest.integer(row->id);
    digest.integer(row->created);
    digest.nullable(row->customer);
    digest.integer(row->total.cents);
    digest.text(row->note);
    digest.integer(row->version);
    digest.text(std::string_view(row->settings->channel));
    digest.byte(row->settings->gift);
    digest.text(std::string_view(row->memo));
}
[[gnu::noinline]] void consume(Digest& digest, std::unique_ptr<JoinRow> row)
{
    digest.integer(row->id);
    digest.text(row->name);
}
[[gnu::noinline]] void consume(Digest& digest, std::unique_ptr<CustomerTotalRow> row)
{
    digest.nullable(row->customer);
    digest.integer(row->count);
    nullable_money(digest, row->total);
}
[[gnu::noinline]] void consume(Digest& digest, std::unique_ptr<ProductTotalRow> row)
{
    digest.text(std::string_view(row->product));
    digest.nullable(row->quantity);
}
[[gnu::noinline]] void consume(Digest& digest, std::unique_ptr<RankRow> row)
{
    digest.integer(row->id);
    digest.integer(row->rank);
}
[[gnu::noinline]] void consume(Digest& digest, std::unique_ptr<RunningRow> row)
{
    digest.integer(row->id);
    digest.nullable(row->quantity);
}
[[gnu::noinline]] void consume(Digest& digest, std::unique_ptr<DetailRow> row)
{
    digest.integer(row->order);
    digest.text(std::string_view(row->name));
    digest.text(std::string_view(row->product));
    digest.integer(row->quantity);
}
[[gnu::noinline]] void consume(Digest& digest, const OrderLineRow& row)
{
    digest.integer(row.id);
    digest.integer(row.tenant);
    digest.integer(row.order);
    digest.text(std::string_view(row.product));
    digest.integer(row.quantity);
}

struct SqliteReader
{
    sqlite3_stmt* statement;
    bool is_null(int column) const
    {
        return sqlite3_column_type(statement, column) == SQLITE_NULL;
    }
    std::int64_t integer(int column) const
    {
        if (sqlite3_column_type(statement, column) != SQLITE_INTEGER)
            throw DatabaseError("materialization", "SQLite integer storage differs");
        return sqlite3_column_int64(statement, column);
    }
    std::optional<std::int64_t> nullable_integer(int column) const
    {
        return is_null(column) ? std::nullopt : std::optional(integer(column));
    }
    std::string_view bytes(int column) const
    {
        if (sqlite3_column_type(statement, column) != SQLITE_TEXT)
            throw DatabaseError("materialization", "SQLite text storage differs");
        const auto* data = sqlite3_column_text(statement, column);
        const int length = sqlite3_column_bytes(statement, column);
        if (data == nullptr)
            throw DatabaseError("provider", "SQLite could not provide text bytes", "SQLITE_NOMEM");
        return {reinterpret_cast<const char*>(data), static_cast<std::size_t>(length)};
    }
    std::string text(int column) const
    {
        return std::string(bytes(column));
    }
    std::optional<std::string> nullable_text(int column) const
    {
        return is_null(column) ? std::nullopt : std::optional(text(column));
    }
    Money money(int column) const
    {
        return {integer(column)};
    }
    std::optional<Money> nullable_money(int column) const
    {
        return is_null(column) ? std::nullopt : std::optional(money(column));
    }
    std::unique_ptr<Settings> settings(int column) const
    {
        return parse_settings(bytes(column));
    }
};

std::uint64_t network_unsigned(std::string_view bytes)
{
    std::uint64_t value = 0;
    for (unsigned char byte : bytes)
        value = (value << 8) | byte;
    return value;
}

struct PgReader
{
    PGresult* result;
    bool is_null(int column) const
    {
        return PQgetisnull(result, 0, column);
    }
    std::string_view bytes(int column) const
    {
        if (is_null(column) || PQfformat(result, column) != 1)
            throw DatabaseError("materialization", "PostgreSQL non-NULL binary value required");
        return {PQgetvalue(result, 0, column), static_cast<std::size_t>(PQgetlength(result, 0, column))};
    }
    std::int64_t integer(int column) const
    {
        const auto data = bytes(column);
        const auto type = PQftype(result, column);
        if (type == 20 && data.size() == 8)
            return std::bit_cast<std::int64_t>(network_unsigned(data));
        if (type == 23 && data.size() == 4)
            return std::bit_cast<std::int32_t>(static_cast<std::uint32_t>(network_unsigned(data)));
        if (type == 21 && data.size() == 2)
            return std::bit_cast<std::int16_t>(static_cast<std::uint16_t>(network_unsigned(data)));
        throw DatabaseError("materialization", "PostgreSQL integer OID/length differs");
    }
    std::optional<std::int64_t> nullable_integer(int column) const
    {
        return is_null(column) ? std::nullopt : std::optional(integer(column));
    }
    std::string text(int column) const
    {
        const auto type = PQftype(result, column);
        if (type != 25 && type != 1043 && type != 1042)
            throw DatabaseError("materialization", "PostgreSQL string OID differs");
        return std::string(bytes(column));
    }
    std::optional<std::string> nullable_text(int column) const
    {
        return is_null(column) ? std::nullopt : std::optional(text(column));
    }
    Money money(int column) const
    {
        const auto data = bytes(column);
        if (PQftype(result, column) != 1700 || data.size() < 8)
            throw DatabaseError("materialization", "PostgreSQL numeric OID/header differs");
        const auto digits = static_cast<int>(network_unsigned(data.substr(0, 2)));
        const auto weight =
            std::bit_cast<std::int16_t>(static_cast<std::uint16_t>(network_unsigned(data.substr(2, 2))));
        const auto sign = network_unsigned(data.substr(4, 2));
        const auto scale = network_unsigned(data.substr(6, 2));
        if (data.size() != static_cast<std::size_t>(8 + digits * 2) || digits > 16 || weight < -16 || weight > 4 ||
            scale > 2 || (sign != 0 && sign != 0x4000))
            throw DatabaseError("materialization", "PostgreSQL exact scale-2 numeric encoding differs");
        __int128 cents = 0;
        for (int i = 0; i < digits; ++i)
        {
            const auto digit = network_unsigned(data.substr(8 + i * 2, 2));
            if (digit >= 10000)
                throw DatabaseError("materialization", "PostgreSQL numeric base-10000 digit differs");
            const int exponent = weight - i;
            if (exponent >= 0)
            {
                __int128 term = digit * 100;
                for (int power = 0; power < exponent; ++power)
                    term *= 10000;
                cents += term;
            }
            else if (exponent == -1)
            {
                if (digit % 100 != 0)
                    throw DatabaseError("materialization", "Money conversion would round");
                cents += digit / 100;
            }
            else if (digit != 0)
                throw DatabaseError("materialization", "Money conversion would round");
        }
        if (sign != 0)
            cents = -cents;
        if (cents < std::numeric_limits<std::int64_t>::min() || cents > std::numeric_limits<std::int64_t>::max())
            throw DatabaseError("materialization", "Money conversion exceeds signed cents");
        return {static_cast<std::int64_t>(cents)};
    }
    std::optional<Money> nullable_money(int column) const
    {
        return is_null(column) ? std::nullopt : std::optional(money(column));
    }
    std::unique_ptr<Settings> settings(int column) const
    {
        auto data = bytes(column);
        const auto type = PQftype(result, column);
        if (type == 3802)
        {
            if (data.empty() || data.front() != 1)
                throw DatabaseError("materialization", "PostgreSQL JSONB version differs");
            data.remove_prefix(1);
        }
        else if (type != 114)
            throw DatabaseError("materialization", "PostgreSQL JSON OID differs");
        return parse_settings(data);
    }
};

struct Accumulator
{
    const ReadCase& query;
    Digest digest;
    std::uint64_t rows = 0;
    std::vector<std::vector<std::unique_ptr<OrderLineRow>>> groups;
    explicit Accumulator(const ReadCase& value) : query(value)
    {
        if (query.row_kind == RowKind::Related)
            groups.resize(query.parent_ids.size());
    }

    template <class Reader> void row(const Reader& input)
    {
        if (rows >= query.expected_rows)
            throw DatabaseError("validation", "Query returned more than its declared rows: " + query.name);
        // Validate NULL contracts before readers access nonnullable values.
        for (std::size_t i = 0; i < query.columns.size(); ++i)
        {
            if (!query.columns[i].nullable && input.is_null(static_cast<int>(i)))
                throw DatabaseError("materialization", "Unexpected NULL for " + query.columns[i].name);
            if (query.columns[i].kind == ValueKind::Int32 && !input.is_null(static_cast<int>(i)))
            {
                const auto value = input.integer(static_cast<int>(i));
                if (value < std::numeric_limits<std::int32_t>::min() ||
                    value > std::numeric_limits<std::int32_t>::max())
                    throw DatabaseError("materialization", "Int32 result exceeds its declared range");
            }
        }
        switch (query.row_kind)
        {
            case RowKind::Int64:
                digest.integer(input.integer(0));
                break;
            case RowKind::Text:
                consume(digest, std::make_unique<TextRow>(TextRow{input.integer(0), input.nullable_text(1)}));
                break;
            case RowKind::Money:
                consume(digest, std::make_unique<MoneyRow>(MoneyRow{input.integer(0), input.money(1)}));
                break;
            case RowKind::Json:
                consume(digest, std::make_unique<JsonRow>(JsonRow{input.integer(0), input.settings(1)}));
                break;
            case RowKind::Wide:
                consume(digest, std::make_unique<OrderRow>(
                                    OrderRow{input.integer(0), input.integer(1), input.integer(2),
                                             input.nullable_integer(3), input.money(4), input.nullable_text(5),
                                             input.integer(6), input.settings(7), input.text(8)}));
                break;
            case RowKind::Join:
                consume(digest, std::make_unique<JoinRow>(JoinRow{input.integer(0), input.nullable_text(1)}));
                break;
            case RowKind::CustomerTotal:
                consume(digest, std::make_unique<CustomerTotalRow>(CustomerTotalRow{
                                    input.nullable_integer(0), input.integer(1), input.nullable_money(2)}));
                break;
            case RowKind::ProductTotal:
                consume(digest,
                        std::make_unique<ProductTotalRow>(ProductTotalRow{input.text(0), input.nullable_integer(1)}));
                break;
            case RowKind::NullableMoney:
                nullable_money(digest, input.nullable_money(0));
                break;
            case RowKind::Rank:
                consume(digest, std::make_unique<RankRow>(RankRow{input.integer(0), input.integer(1)}));
                break;
            case RowKind::Running:
                consume(digest, std::make_unique<RunningRow>(RunningRow{input.integer(0), input.nullable_integer(1)}));
                break;
            case RowKind::Detail:
                consume(digest, std::make_unique<DetailRow>(
                                    DetailRow{input.integer(0), input.text(1), input.text(2), input.integer(3)}));
                break;
            case RowKind::Related:
            {
                const auto index = static_cast<std::int32_t>(input.integer(5));
                if (index < 0 || static_cast<std::uint64_t>(index) >= groups.size())
                    throw DatabaseError("materialization", "Related parent ordinal exceeds the input batch");
                groups[index].push_back(std::make_unique<OrderLineRow>(OrderLineRow{
                    input.integer(0), input.integer(1), input.integer(2), input.text(3), input.integer(4)}));
                break;
            }
            case RowKind::State:
                reject("Business state rows require per-step state validation");
        }
        ++rows;
    }

    OperationResult finish()
    {
        if (query.row_kind == RowKind::Related)
        {
            for (std::size_t i = 0; i < groups.size(); ++i)
            {
                auto& group = groups[i];
                std::sort(group.begin(), group.end(),
                          [](const auto& left, const auto& right) { return left->id < right->id; });
                digest.integer(query.parent_ids[i]);
                digest.integer(static_cast<std::int64_t>(group.size()));
                for (const auto& child : group)
                    consume(digest, *child);
            }
        }
        return {rows, digest.value};
    }
};

struct SqliteStatement
{
    sqlite3_stmt* value = nullptr;
    SqliteStatement(sqlite3* db, const std::string& sql)
    {
        if (sql.size() > static_cast<std::size_t>(std::numeric_limits<int>::max()))
            reject("SQL exceeds sqlite3_prepare_v3 size");
        const char* tail = nullptr;
        const int status =
            sqlite3_prepare_v3(db, sql.data(), static_cast<int>(sql.size()), SQLITE_PREPARE_PERSISTENT, &value, &tail);
        if (status != SQLITE_OK || value == nullptr)
        {
            if (value)
                sqlite3_finalize(value);
            value = nullptr;
            throw DatabaseError("provider", sqlite3_errmsg(db), std::to_string(sqlite3_extended_errcode(db)));
        }
        while (tail && *tail && std::isspace(static_cast<unsigned char>(*tail)))
            ++tail;
        if (tail && *tail)
        {
            sqlite3_finalize(value);
            value = nullptr;
            reject("A read case must contain one SQLite statement");
        }
        ++sqlite_statements;
    }
    ~SqliteStatement()
    {
        if (value)
        {
            sqlite3_finalize(value);
            --sqlite_statements;
        }
    }
    SqliteStatement(const SqliteStatement&) = delete;
    SqliteStatement& operator=(const SqliteStatement&) = delete;
};

struct PgResult
{
    PGresult* value;
    explicit PgResult(PGresult* result) : value(result)
    {
        if (value)
            ++postgres_results;
    }
    ~PgResult()
    {
        if (value)
        {
            PQclear(value);
            --postgres_results;
        }
    }
    PgResult(const PgResult&) = delete;
    PgResult& operator=(const PgResult&) = delete;
    PgResult(PgResult&& other) noexcept : value(std::exchange(other.value, nullptr)) {}
};
} // namespace

DatabaseError::DatabaseError(std::string category, std::string message, std::string native_code)
    : std::runtime_error(std::move(message)), category(std::move(category)), native_code(std::move(native_code))
{
}

CleanupError::CleanupError(std::string message, std::exception_ptr primary, std::exception_ptr cleanup)
    : DatabaseError("cleanup", std::move(message)), primary(std::move(primary)), cleanup(std::move(cleanup))
{
}

std::string assemble_sql(const StatementDefinition& query)
{
    std::string sql;
    sql.reserve(query.sql.size());
    for (const auto& clause : query.fragments)
        sql.append(clause);
    return sql;
}

static void load_statement(StatementDefinition& statement, const Json& item, const std::string& dialect,
                           bool runtime_parameters)
{
    statement.sql = item.at("sql").at(dialect);
    statement.fragments = item.at("fragments").at(dialect).get<std::vector<std::string>>();
    if (statement.sql.empty() || statement.sql.find('\0') != std::string::npos || statement.fragments.empty() ||
        assemble_sql(statement) != statement.sql)
        reject("Dynamic clauses do not reconstruct the declared SQL: " + statement.name);
    for (const auto& parameter : item.at("parameters"))
    {
        const std::string type = parameter.at("type");
        if (type != "int32" && type != "int64" && type != "text")
            reject("Unsupported parameter type: " + type);
        Parameter value{value_kind(type), std::monostate{}};
        if (parameter.contains("runtime"))
        {
            if (!runtime_parameters || type != "int64" || parameter.at("runtime") != "worker_id_plus_one")
                reject("Unsupported runtime parameter: " + statement.name);
            value.worker_key = true;
        }
        if (!parameter.at("value").is_null())
        {
            if (value.kind == ValueKind::Int64 || value.kind == ValueKind::Int32)
            {
                value.value = parameter.at("value").get<std::int64_t>();
                const auto number = std::get<std::int64_t>(value.value);
                if (value.kind == ValueKind::Int32 && (number < std::numeric_limits<std::int32_t>::min() ||
                                                       number > std::numeric_limits<std::int32_t>::max()))
                    reject("Int32 parameter exceeds its declared range");
            }
            else
            {
                auto text = parameter.at("value").get<std::string>();
                if (text.find('\0') != std::string::npos)
                    reject("PostgreSQL text parameters cannot contain a zero byte");
                value.value = std::move(text);
            }
        }
        statement.parameters.push_back(std::move(value));
    }
    if (statement.parameters.size() > 65535)
        reject("Too many parameters");
}

Contract load_contract(const std::string& path, Provider provider)
{
    std::ifstream stream(path);
    if (!stream)
        reject("Cannot open query contract");
    const Json document = Json::parse(stream);
    if (document.at("schema_version") != 1)
        reject("Unsupported query contract version");
    const auto& dataset = document.at("dataset");
    Contract contract{provider,           dataset.at("tenants"),    dataset.at("orders_per_tenant"),
                      dataset.at("rows"), dataset.at("batch_rows"), {}};
    if (contract.tenants < 1 || contract.orders_per_tenant < 32 || contract.rows < 1 || contract.rows > 1000000 ||
        contract.batch_rows < 1 || contract.batch_rows > 4096)
        reject("Dataset exceeds the admitted contract limits");
    const std::string dialect = provider == Provider::Sqlite ? "sqlite" : "postgresql";
    std::set<std::string> ids;
    std::set<std::string> names;
    for (const auto& item : document.at("cases"))
    {
        ReadCase query;
        query.id = identifier(item.at("id"));
        query.name = item.at("name");
        if (query.id.empty() || !ids.insert(query.id).second || !names.insert(query.name).second)
            reject("Duplicate or empty query identity");
        query.row_kind = row_kind(query.name);
        load_statement(query, item, dialect, false);
        query.materialization = item.at("materialization").get<std::string>();
        if (query.materialization != materialization_name(query.row_kind))
            reject("Materialization label differs from the declared case: " + query.name);
        std::set<std::string> column_names;
        for (const auto& column : item.at("columns"))
        {
            const std::string type = column.at("type");
            Column value{column.at("name"), value_kind(type), column.at("nullable")};
            if (value.name.empty() || !column_names.insert(value.name).second)
                reject("Duplicate or empty result alias");
            if ((type == "nullable-int32" || type == "nullable-int64" || type == "nullable-money") && !value.nullable)
                reject("Contradictory nullable column declaration");
            query.columns.push_back(std::move(value));
        }
        query.expected_rows = unsigned_decimal(item.at("expected").at("rows"));
        query.expected_digest = unsigned_decimal(item.at("expected").at("digest"));
        query.expected_outcome = item.at("expected").value("outcome", "success");
        if (query.expected_outcome != "success" &&
            !(query.row_kind == RowKind::State && query.expected_outcome == "conflict"))
            reject("Unsupported expected outcome: " + query.name);
        if (query.expected_rows > 10000000)
            reject("Result row bound exceeds this adapter's contract");
        if (query.row_kind == RowKind::Related)
        {
            if (item.contains("parent_ids"))
                query.parent_ids = item.at("parent_ids").get<std::vector<std::int64_t>>();
            else
            {
                const auto count = std::min({contract.rows, contract.batch_rows, contract.orders_per_tenant});
                for (std::int64_t i = 1; i <= count; ++i)
                    query.parent_ids.push_back(i);
            }
            if (query.parent_ids.size() > static_cast<std::size_t>(contract.batch_rows))
                reject("Related parent group count exceeds the batch limit");
            if (item.contains("group_count") && unsigned_decimal(item.at("group_count")) != query.parent_ids.size())
                reject("Related group count differs from parent IDs");
            if (query.columns.empty() || query.columns.back().name != "__korda_parent_index")
                reject("Related result lacks the declared parent ordinal");
        }
        require_shape(query);
        if (query.row_kind == RowKind::State)
        {
            const auto& steps = item.at("steps");
            if (!steps.is_array() || steps.empty() || steps.size() > 16)
                reject("Business operation requires 1..16 bounded steps");
            bool transaction = false;
            std::uint64_t selected_rows = 0;
            for (const auto& item_step : steps)
            {
                BusinessStep step;
                step.kind = item_step.at("kind");
                step.name = query.name + "/" + std::to_string(query.steps.size()) + "/" + step.kind;
                load_statement(step, item_step, dialect, true);
                if (step.kind == "select")
                {
                    step.columns = query.columns;
                    const auto& expected = item_step.at("expected_state");
                    step.expected_counter = expected.at("counter");
                    step.expected_version = expected.at("version");
                    if (!expected.at("note").is_null())
                        step.expected_note = expected.at("note").get<std::string>();
                    ++selected_rows;
                }
                else if (step.kind == "execute")
                {
                    step.expected_affected = item_step.at("expected_affected").get<std::int64_t>();
                    if (*step.expected_affected < 0 || *step.expected_affected > 1)
                        reject("Business write affected-row contract must be 0 or 1");
                }
                else if (step.kind == "begin")
                {
                    if (transaction)
                        reject("Nested business transactions are outside this contract");
                    transaction = true;
                }
                else if (step.kind == "commit" || step.kind == "rollback")
                {
                    if (!transaction)
                        reject("Business transaction terminal has no begin");
                    transaction = false;
                }
                else
                    reject("Unknown business step kind: " + step.kind);
                query.steps.push_back(std::move(step));
            }
            if (transaction || selected_rows != query.expected_rows)
                reject("Business transaction/selected-row contract is incomplete");
        }
        else if (item.contains("steps"))
            reject("Read cases cannot hide a business step sequence");
        contract.cases.push_back(std::move(query));
    }
    if (contract.cases.empty() || contract.cases.size() > 128)
        reject("Empty or unbounded case set");
    return contract;
}

OwnerCounts live_owners()
{
    return {connections.load(), sqlite_statements.load(), postgres_results.load(), unconfirmed_sqlite_closes.load()};
}

RuntimeIdentity runtime_identity()
{
    RuntimeIdentity result{sqlite3_libversion(), sqlite3_sourceid(), {}, PQlibVersion()};
    if (result.sqlite_version != SQLITE_VERSION || result.sqlite_source_id != SQLITE_SOURCE_ID)
        throw DatabaseError("configuration", "Loaded SQLite engine differs from its selected locked header");
#ifdef DB_PERF_EXPECTED_PQ_VERSION
    if (result.libpq_version != DB_PERF_EXPECTED_PQ_VERSION)
        throw DatabaseError("configuration", "Linked libpq version differs from its locked build receipt");
#endif
    for (int i = 0; const char* option = sqlite3_compileoption_get(i); ++i)
        result.sqlite_compile_options.emplace_back(option);
    std::sort(result.sqlite_compile_options.begin(), result.sqlite_compile_options.end());
    return result;
}

struct QueryAdapter::Impl
{
    const Contract& contract;
    ConnectionOptions options;
    Counters counts;
    std::map<std::string, std::string> configuration;
    sqlite3* sqlite = nullptr;
    PGconn* postgres = nullptr;
    bool counted = false;
    Clock::time_point deadline;
    std::vector<std::unique_ptr<SqliteStatement>> sqlite_cache;
    std::vector<bool> postgres_prepared;
    std::vector<std::string> statement_names;
    std::vector<std::size_t> cache_offsets;
    std::vector<std::array<char, 32>> parameter_text;
    std::vector<const char*> parameter_values;
    std::vector<Oid> parameter_types;
    std::vector<int> parameter_lengths, parameter_formats;

    Impl(const Contract& contract, ConnectionOptions options) : contract(contract), options(std::move(options))
    {
        if (this->options.connection.empty() || this->options.operation_timeout.count() < 1 ||
            this->options.operation_timeout.count() > 3600000 || this->options.sqlite_busy_timeout.count() < 0 ||
            this->options.sqlite_busy_timeout.count() > this->options.operation_timeout.count())
            reject("Invalid connection or timeout limits");
        if (contract.cases.empty() || contract.cases.size() > 128)
            reject("Invalid selected-case contract bounds");
        std::array<bool, 128> selected{};
        if (this->options.selected_cases.empty())
            std::fill_n(selected.begin(), contract.cases.size(), true);
        else
            for (const auto index : this->options.selected_cases)
            {
                if (index >= contract.cases.size() || selected[index])
                    reject("Unknown or duplicated selected case index");
                selected[index] = true;
            }
        std::size_t max_parameters = 0;
        for (std::size_t i = 0; i < contract.cases.size(); ++i)
        {
            cache_offsets.push_back(std::numeric_limits<std::size_t>::max());
            if (!selected[i])
                continue;
            cache_offsets[i] = statement_names.size();
            const auto& query = contract.cases[i];
            if (query.steps.empty())
            {
                statement_names.push_back("db_perf_" + std::to_string(i));
                max_parameters = std::max(max_parameters, query.parameters.size());
            }
            else
                for (std::size_t step = 0; step < query.steps.size(); ++step)
                {
                    statement_names.push_back("db_perf_" + std::to_string(i) + "_" + std::to_string(step));
                    max_parameters = std::max(max_parameters, query.steps[step].parameters.size());
                }
        }
        if (statement_names.size() > 2048)
            reject("Physical statement cache exceeds 2048 bounded entries");
        sqlite_cache.resize(statement_names.size());
        postgres_prepared.resize(statement_names.size(), false);
        parameter_text.resize(max_parameters);
        parameter_values.resize(max_parameters);
        parameter_types.resize(max_parameters);
        parameter_lengths.resize(max_parameters);
        parameter_formats.resize(max_parameters);
        connect();
    }

    ~Impl()
    {
        close();
    }

    [[noreturn]] void fail_postgres(PGresult* result = nullptr) const
    {
        const char* sqlstate = result ? PQresultErrorField(result, PG_DIAG_SQLSTATE) : nullptr;
        const bool disconnected = !postgres || PQstatus(postgres) == CONNECTION_BAD ||
                                  (sqlstate && std::string_view(sqlstate).starts_with("08"));
        throw DatabaseError(disconnected                                        ? "connection"
                            : sqlstate && std::string_view(sqlstate) == "57014" ? "timeout-or-canceled"
                                                                                : "provider",
                            result     ? PQresultErrorMessage(result)
                            : postgres ? PQerrorMessage(postgres)
                                       : "PostgreSQL connection is closed",
                            sqlstate ? sqlstate : "");
    }

    [[noreturn]] void fail_sqlite() const
    {
        const int code = sqlite3_extended_errcode(sqlite);
        const auto category = (code & 255) == SQLITE_CANTOPEN                                ? "connection"
                              : (code & 255) == SQLITE_BUSY || (code & 255) == SQLITE_LOCKED ? "busy"
                              : (code & 255) == SQLITE_INTERRUPT                             ? "timeout-or-canceled"
                                                                                             : "provider";
        throw DatabaseError(category, sqlite3_errmsg(sqlite), std::to_string(code));
    }

    void check_deadline() const
    {
        if (Clock::now() >= deadline)
            throw DatabaseError("timeout", "Native read operation deadline elapsed");
    }

    static int progress(void* context)
    {
        return Clock::now() >= static_cast<Impl*>(context)->deadline;
    }

    void wait(short events)
    {
        check_deadline();
        const auto left = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - Clock::now()).count();
        const int timeout = static_cast<int>(std::clamp<std::int64_t>(left, 1, 20));
        pollfd item{PQsocket(postgres), events, 0};
        ++counts.poll_wait_calls;
        const int result = poll(&item, 1, timeout);
        if (result < 0 && errno != EINTR)
            throw DatabaseError("provider", "PostgreSQL poll failed", std::to_string(errno));
        if (result > 0 && (item.revents & (POLLERR | POLLHUP | POLLNVAL)))
            fail_postgres();
    }

    void flush()
    {
        for (;;)
        {
            check_deadline();
            const int status = PQflush(postgres);
            if (status == 0)
                return;
            if (status < 0)
                fail_postgres();
            wait(POLLOUT);
        }
    }

    PGresult* receive()
    {
        flush();
        while (PQisBusy(postgres))
        {
            check_deadline();
            if (!PQconsumeInput(postgres))
                fail_postgres();
            if (PQisBusy(postgres))
                wait(POLLIN);
        }
        return PQgetResult(postgres);
    }

    void command_results()
    {
        bool received = false;
        while (auto* raw = receive())
        {
            PgResult result(raw);
            if (PQresultStatus(raw) != PGRES_COMMAND_OK)
                fail_postgres(raw);
            received = true;
        }
        if (!received)
            throw DatabaseError("provider", "PostgreSQL command did not acknowledge completion");
    }

    PgResult setup_query(const std::string& sql, ExecStatusType expected)
    {
        if (!PQsendQuery(postgres, sql.c_str()))
            fail_postgres();
        ++counts.setup_sql_executions;
        PgResult result(receive());
        if (!result.value || PQresultStatus(result.value) != expected)
            fail_postgres(result.value);
        PgResult extra(receive());
        if (extra.value)
            throw DatabaseError("configuration", "Unexpected extra PostgreSQL setup result");
        return result;
    }

    std::string sqlite_scalar(const std::string& sql)
    {
        SqliteStatement statement(sqlite, sql);
        ++counts.setup_sql_executions;
        if (sqlite3_step(statement.value) != SQLITE_ROW || sqlite3_column_count(statement.value) != 1)
            fail_sqlite();
        const auto* text = sqlite3_column_text(statement.value, 0);
        if (text == nullptr)
            throw DatabaseError("configuration", "Missing SQLite configuration value");
        std::string value(reinterpret_cast<const char*>(text));
        if (sqlite3_step(statement.value) != SQLITE_DONE)
            fail_sqlite();
        return value;
    }

    void verify_dataset()
    {
        constexpr const char* sql = "SELECT schema_version,tenants,orders_per_tenant FROM db_perf_metadata";
        std::int64_t version = 0, tenants = 0, orders = 0;
        if (sqlite)
        {
            ++counts.setup_sql_executions;
            SqliteStatement statement(sqlite, sql);
            if (sqlite3_step(statement.value) != SQLITE_ROW || sqlite3_column_count(statement.value) != 3)
                throw DatabaseError("configuration", "SQLite dataset metadata is missing");
            const SqliteReader row{statement.value};
            version = row.integer(0);
            tenants = row.integer(1);
            orders = row.integer(2);
            if (sqlite3_step(statement.value) != SQLITE_DONE)
                throw DatabaseError("configuration", "SQLite dataset metadata is not unique");
        }
        else
        {
            auto result = setup_query(sql, PGRES_TUPLES_OK);
            if (!result.value || PQresultStatus(result.value) != PGRES_TUPLES_OK || PQntuples(result.value) != 1 ||
                PQnfields(result.value) != 3)
                throw DatabaseError("configuration", "PostgreSQL dataset metadata is missing or not unique");
            auto number = [&](int column)
            {
                if (PQgetisnull(result.value, 0, column))
                    throw DatabaseError("configuration", "NULL dataset metadata");
                const std::string_view text{PQgetvalue(result.value, 0, column),
                                            static_cast<std::size_t>(PQgetlength(result.value, 0, column))};
                std::int64_t value = 0;
                const auto parsed = std::from_chars(text.data(), text.data() + text.size(), value);
                if (parsed.ec != std::errc{} || parsed.ptr != text.data() + text.size())
                    throw DatabaseError("configuration", "Invalid dataset metadata");
                return value;
            };
            version = number(0);
            tenants = number(1);
            orders = number(2);
        }
        if (version != 1 || tenants != contract.tenants || orders != contract.orders_per_tenant)
            throw DatabaseError("configuration", "Dataset metadata differs from the input contract");
    }

    void connect(Clock::time_point limit = Clock::time_point::max())
    {
        if (sqlite || postgres)
            return;
        deadline = std::min(Clock::now() + options.operation_timeout, limit);
        check_deadline();
        try
        {
            if (contract.provider == Provider::Sqlite)
            {
                const int status =
                    sqlite3_open_v2(options.connection.c_str(), &sqlite,
                                    SQLITE_OPEN_READWRITE | SQLITE_OPEN_FULLMUTEX | SQLITE_OPEN_PRIVATECACHE, nullptr);
                if (status != SQLITE_OK)
                    fail_sqlite();
                sqlite3_extended_result_codes(sqlite, 1);
                sqlite3_progress_handler(sqlite, 1000, progress, this);
                if (sqlite3_busy_timeout(sqlite, static_cast<int>(options.sqlite_busy_timeout.count())) != SQLITE_OK)
                    fail_sqlite();
                char* error = nullptr;
                const int configured =
                    sqlite3_exec(sqlite, "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;",
                                 nullptr, nullptr, &error);
                counts.setup_sql_executions += 3;
                if (configured != SQLITE_OK)
                {
                    std::string message = error ? error : sqlite3_errmsg(sqlite);
                    sqlite3_free(error);
                    throw DatabaseError("configuration", std::move(message));
                }
                configuration = {{"journal_mode", sqlite_scalar("PRAGMA journal_mode")},
                                 {"synchronous", sqlite_scalar("PRAGMA synchronous")},
                                 {"foreign_keys", sqlite_scalar("PRAGMA foreign_keys")},
                                 {"busy_timeout_ms", sqlite_scalar("PRAGMA busy_timeout")},
                                 {"sqlite_version", sqlite3_libversion()},
                                 {"sqlite_source_id", sqlite3_sourceid()},
                                 {"open_flags", "readwrite/fullmutex/privatecache"}};
                if (configuration.at("journal_mode") != "wal" || configuration.at("synchronous") != "2" ||
                    configuration.at("foreign_keys") != "1" ||
                    configuration.at("busy_timeout_ms") != std::to_string(options.sqlite_busy_timeout.count()))
                    throw DatabaseError("configuration", "SQLite effective durability or timeout differs");
            }
            else
            {
                const auto connect_timeout =
                    std::to_string(std::max<std::int64_t>(1, (options.operation_timeout.count() + 999) / 1000));
                const char* keys[]{"dbname", "sslmode", "connect_timeout", "client_encoding", nullptr};
                const char* values[]{options.connection.c_str(), "disable", connect_timeout.c_str(), "UTF8", nullptr};
                postgres = PQconnectStartParams(keys, values, 1);
                if (!postgres || PQstatus(postgres) == CONNECTION_BAD)
                    fail_postgres();
                for (;;)
                {
                    check_deadline();
                    const auto status = PQconnectPoll(postgres);
                    if (status == PGRES_POLLING_OK)
                        break;
                    if (status == PGRES_POLLING_READING)
                        wait(POLLIN);
                    else if (status == PGRES_POLLING_WRITING)
                        wait(POLLOUT);
                    else if (status != PGRES_POLLING_ACTIVE)
                        fail_postgres();
                }
                if (PQsetnonblocking(postgres, 1))
                    fail_postgres();
                {
                    const auto sql = "SET statement_timeout TO " + std::to_string(options.operation_timeout.count());
                    auto result = setup_query(sql, PGRES_COMMAND_OK);
                }
                {
                    const char* sql =
                        "SELECT current_setting('transaction_isolation'), current_setting('synchronous_commit'), "
                        "current_setting('fsync'), current_setting('full_page_writes'), "
                        "current_setting('statement_timeout'), "
                        "current_setting('client_encoding')";
                    auto result = setup_query(sql, PGRES_TUPLES_OK);
                    if (!result.value || PQresultStatus(result.value) != PGRES_TUPLES_OK ||
                        PQntuples(result.value) != 1 || PQnfields(result.value) != 6)
                        fail_postgres(result.value);
                    const std::array names{"transaction_isolation", "synchronous_commit", "fsync",
                                           "full_page_writes",      "statement_timeout",  "client_encoding"};
                    for (std::size_t i = 0; i < names.size(); ++i)
                        configuration[names[i]] = PQgetvalue(result.value, 0, static_cast<int>(i));
                    configuration["server_version_number"] = std::to_string(PQserverVersion(postgres));
                    configuration["tls_in_use"] = PQsslInUse(postgres) ? "true" : "false";
                    if (configuration.at("client_encoding") != "UTF8" || PQtransactionStatus(postgres) != PQTRANS_IDLE)
                        throw DatabaseError("configuration",
                                            "PostgreSQL encoding or initial transaction state differs");
                }
            }
            verify_dataset();
            configuration["selected_case_count"] =
                std::to_string(options.selected_cases.empty() ? contract.cases.size() : options.selected_cases.size());
            configuration["statement_cache_capacity"] = std::to_string(statement_names.size());
            configuration["parameter_capacity"] = std::to_string(parameter_values.size());
            counted = true;
            ++connections;
            ++counts.connection_opens;
        }
        catch (...)
        {
            close();
            throw;
        }
    }

    void close() noexcept
    {
        for (auto& statement : sqlite_cache)
            statement.reset();
        std::fill(postgres_prepared.begin(), postgres_prepared.end(), false);
        if (sqlite)
        {
            sqlite3_progress_handler(sqlite, 0, nullptr, nullptr);
            if (sqlite3_close(sqlite) != SQLITE_OK)
            {
                // Do not report this as proven retirement, even if close_v2 can
                // defer the remaining SQLite cleanup successfully.
                ++unconfirmed_sqlite_closes;
                ++counts.cleanup_failures;
                sqlite3_close_v2(sqlite);
            }
            sqlite = nullptr;
        }
        if (postgres)
        {
            PQfinish(postgres);
            postgres = nullptr;
        }
        if (counted)
        {
            --connections;
            counted = false;
        }
    }

    void validate_sqlite_shape(sqlite3_stmt* statement, const StatementDefinition& query, bool mutation = false)
    {
        if ((!query.columns.empty() && !sqlite3_stmt_readonly(statement)) ||
            (mutation && sqlite3_stmt_readonly(statement)) ||
            sqlite3_column_count(statement) != static_cast<int>(query.columns.size()) ||
            sqlite3_bind_parameter_count(statement) != static_cast<int>(query.parameters.size()))
            throw DatabaseError("contract", "SQLite read statement result/parameter shape differs: " + query.name);
        for (std::size_t i = 0; i < query.columns.size(); ++i)
            if (query.columns[i].name != sqlite3_column_name(statement, static_cast<int>(i)))
                throw DatabaseError("contract", "SQLite result alias differs: " + query.name);
    }

    void prepare_postgres(std::size_t index, const StatementDefinition& query, const std::string& sql)
    {
        const char* name = options.mode == ConstructionMode::Prepared ? statement_names[index].c_str() : "";
        for (std::size_t i = 0; i < query.parameters.size(); ++i)
            parameter_types[i] = query.parameters[i].kind == ValueKind::Int32   ? 23
                                 : query.parameters[i].kind == ValueKind::Int64 ? 20
                                                                                : 25;
        if (!PQsendPrepare(postgres, name, sql.c_str(), static_cast<int>(query.parameters.size()),
                           parameter_types.data()))
            fail_postgres();
        ++counts.physical_prepare_calls;
        ++counts.protocol_submissions;
        command_results();
        if (!PQsendDescribePrepared(postgres, name))
            fail_postgres();
        ++counts.describe_calls;
        ++counts.protocol_submissions;
        bool described = false;
        while (auto* raw = receive())
        {
            PgResult result(raw);
            if (PQresultStatus(raw) != PGRES_COMMAND_OK)
                fail_postgres(raw);
            if (described || PQnfields(raw) != static_cast<int>(query.columns.size()) ||
                PQnparams(raw) != static_cast<int>(query.parameters.size()))
                throw DatabaseError("contract", "PostgreSQL prepared description differs: " + query.name);
            for (std::size_t i = 0; i < query.columns.size(); ++i)
            {
                if (query.columns[i].name != PQfname(raw, static_cast<int>(i)))
                    throw DatabaseError("contract", "PostgreSQL result alias differs: " + query.name);
                const auto oid = PQftype(raw, static_cast<int>(i));
                bool matches = false;
                switch (query.columns[i].kind)
                {
                    case ValueKind::Int32:
                        matches = oid == 23;
                        break;
                    case ValueKind::Int64:
                        matches = oid == 20 || oid == 23 || oid == 21;
                        break;
                    case ValueKind::String:
                        matches = oid == 25 || oid == 1043 || oid == 1042;
                        break;
                    case ValueKind::Money:
                        matches = oid == 1700;
                        break;
                    case ValueKind::Settings:
                        matches = oid == 3802 || oid == 114;
                        break;
                }
                if (!matches)
                    throw DatabaseError("contract", "PostgreSQL result OID differs: " + query.name + "/" +
                                                        query.columns[i].name + " oid=" + std::to_string(oid));
            }
            for (std::size_t i = 0; i < query.parameters.size(); ++i)
                if (PQparamtype(raw, static_cast<int>(i)) != parameter_types[i])
                    throw DatabaseError("contract", "PostgreSQL parameter type differs: " + query.name);
            described = true;
        }
        if (!described)
            throw DatabaseError("provider", "PostgreSQL prepared description is missing");
        if (options.mode == ConstructionMode::Prepared)
            postgres_prepared[index] = true;
    }

    void confirm_protocol()
    {
        if (options.confirmation == ProtocolConfirmation::DrainOnly)
            return;
        if (PQstatus(postgres) != CONNECTION_OK || PQpipelineStatus(postgres) != PQ_PIPELINE_OFF ||
            !PQenterPipelineMode(postgres) || !PQpipelineSync(postgres))
            fail_postgres();
        ++counts.protocol_submissions;
        ++counts.confirmation_syncs;
        {
            PgResult result(receive());
            if (!result.value || PQresultStatus(result.value) != PGRES_PIPELINE_SYNC)
                throw DatabaseError("provider", "PostgreSQL Sync confirmation did not complete");
        }
        {
            PgResult extra(receive());
            if (extra.value)
                throw DatabaseError("provider", "Unexpected result after PostgreSQL Sync confirmation");
        }
        if (!PQexitPipelineMode(postgres) || PQpipelineStatus(postgres) != PQ_PIPELINE_OFF)
            fail_postgres();
    }

    void configure_sqlite_deadline()
    {
        const auto remaining = std::max<std::int64_t>(
            0, std::chrono::duration_cast<std::chrono::milliseconds>(deadline - Clock::now()).count());
        if (sqlite3_busy_timeout(sqlite, static_cast<int>(std::min<std::int64_t>(options.sqlite_busy_timeout.count(),
                                                                                 remaining))) != SQLITE_OK)
            fail_sqlite();
    }

    void bind_sqlite(sqlite3_stmt* statement, const StatementDefinition& query, std::int64_t worker_key)
    {
        for (std::size_t i = 0; i < query.parameters.size(); ++i)
        {
            const auto& parameter = query.parameters[i];
            const auto& value = parameter.value;
            int status;
            if (parameter.worker_key)
                status = sqlite3_bind_int64(statement, static_cast<int>(i + 1), worker_key);
            else if (std::holds_alternative<std::monostate>(value))
                status = sqlite3_bind_null(statement, static_cast<int>(i + 1));
            else if (const auto* integer = std::get_if<std::int64_t>(&value))
                status = parameter.kind == ValueKind::Int32
                             ? sqlite3_bind_int(statement, static_cast<int>(i + 1), static_cast<int>(*integer))
                             : sqlite3_bind_int64(statement, static_cast<int>(i + 1), *integer);
            else
            {
                const auto& text = std::get<std::string>(value);
                status = sqlite3_bind_text64(statement, static_cast<int>(i + 1), text.data(), text.size(),
                                             SQLITE_TRANSIENT, SQLITE_UTF8);
            }
            if (status != SQLITE_OK)
                fail_sqlite();
        }
    }

    void bind_postgres(const StatementDefinition& query, std::int64_t worker_key)
    {
        for (std::size_t i = 0; i < query.parameters.size(); ++i)
        {
            const auto& parameter = query.parameters[i];
            const auto& value = parameter.value;
            const auto* integer = std::get_if<std::int64_t>(&value);
            parameter_lengths[i] = 0;
            parameter_formats[i] = 0;
            if (parameter.worker_key || integer)
            {
                auto& bytes = parameter_text[i];
                const auto number = parameter.worker_key ? worker_key : *integer;
                auto bits = static_cast<std::uint64_t>(number);
                const int size = parameter.kind == ValueKind::Int32 ? 4 : 8;
                for (int byte = size - 1; byte >= 0; --byte)
                {
                    bytes[byte] = static_cast<char>(bits & 255);
                    bits >>= 8;
                }
                parameter_values[i] = bytes.data();
                parameter_lengths[i] = size;
                parameter_formats[i] = 1;
            }
            else if (std::holds_alternative<std::monostate>(value))
                parameter_values[i] = nullptr;
            else
                parameter_values[i] = std::get<std::string>(value).c_str();
        }
    }

    struct StateRow
    {
        std::int64_t id, counter, version;
        std::optional<std::string> note;
    };

    template <class Reader>
    void consume_state(const Reader& input, const BusinessStep& step, std::int64_t worker_key, Digest& digest,
                       std::uint64_t& step_rows)
    {
        if (step_rows != 0 || input.is_null(0) || input.is_null(1) || input.is_null(2))
            throw DatabaseError("validation", "Business state row cardinality/NULL differs: " + step.name);
        // An owned typed DTO is materialized before validation and hashing. The
        // identity is checked separately and deliberately excluded from digest.
        auto row = std::make_unique<StateRow>(
            StateRow{input.integer(0), input.integer(1), input.integer(2), input.nullable_text(3)});
        if (row->id != worker_key || row->counter != step.expected_counter || row->version != step.expected_version ||
            row->note != step.expected_note)
            throw DatabaseError("validation", "Business persisted state differs: " + step.name);
        digest.integer(row->counter);
        digest.integer(row->version);
        digest.text(row->note);
        ++step_rows;
        ++counts.rows_consumed;
    }

    void execute_step(std::size_t slot, const BusinessStep& step, std::int64_t worker_key, Digest& digest,
                      std::uint64_t& total_rows)
    {
        check_deadline();
        const bool read = step.kind == "select";
        std::string constructed;
        if (options.mode == ConstructionMode::Dynamic)
            constructed = assemble_sql(step);
        const auto& sql = options.mode == ConstructionMode::Dynamic ? constructed : step.sql;
        std::uint64_t step_rows = 0;
        std::int64_t affected = -1;
        if (sqlite)
        {
            configure_sqlite_deadline();
            std::unique_ptr<SqliteStatement> temporary;
            if (options.mode == ConstructionMode::Prepared)
            {
                if (!sqlite_cache[slot])
                {
                    sqlite_cache[slot] = std::make_unique<SqliteStatement>(sqlite, sql);
                    ++counts.physical_prepare_calls;
                }
            }
            else
            {
                temporary = std::make_unique<SqliteStatement>(sqlite, sql);
                ++counts.physical_prepare_calls;
            }
            auto* statement = options.mode == ConstructionMode::Prepared ? sqlite_cache[slot]->value : temporary->value;
            validate_sqlite_shape(statement, step, step.kind == "execute");
            bind_sqlite(statement, step, worker_key);
            ++counts.sql_executions;
            int status;
            while ((status = sqlite3_step(statement)) == SQLITE_ROW)
            {
                check_deadline();
                ++counts.rows_fetched;
                if (!read)
                    throw DatabaseError("contract", "Business command unexpectedly returned a row: " + step.name);
                consume_state(SqliteReader{statement}, step, worker_key, digest, step_rows);
            }
            if (status != SQLITE_DONE)
                fail_sqlite();
            if (step.expected_affected)
                affected = sqlite3_changes64(sqlite);
            if (sqlite3_reset(statement) != SQLITE_OK || sqlite3_clear_bindings(statement) != SQLITE_OK)
                fail_sqlite();
        }
        else
        {
            if (options.mode == ConstructionMode::Dynamic || !postgres_prepared[slot])
                prepare_postgres(slot, step, sql);
            confirm_protocol();
            bind_postgres(step, worker_key);
            const char* name = options.mode == ConstructionMode::Prepared ? statement_names[slot].c_str() : "";
            if (!PQsendQueryPrepared(postgres, name, static_cast<int>(step.parameters.size()), parameter_values.data(),
                                     parameter_lengths.data(), parameter_formats.data(), 1))
                fail_postgres();
            ++counts.sql_executions;
            ++counts.protocol_submissions;
            if (!PQsetSingleRowMode(postgres))
                throw DatabaseError("provider", "Business PostgreSQL single-row mode was not admitted");
            bool terminal = false;
            while (auto* raw = receive())
            {
                PgResult result(raw);
                const auto status = PQresultStatus(raw);
                if (read && status == PGRES_SINGLE_TUPLE)
                {
                    if (terminal || PQntuples(raw) != 1 || PQnfields(raw) != 4)
                        throw DatabaseError("contract", "Business PostgreSQL state shape differs");
                    ++counts.rows_fetched;
                    consume_state(PgReader{raw}, step, worker_key, digest, step_rows);
                }
                else if (read && status == PGRES_TUPLES_OK && !terminal && PQntuples(raw) == 0)
                    terminal = true;
                else if (!read && status == PGRES_COMMAND_OK && !terminal)
                {
                    terminal = true;
                    if (step.expected_affected)
                    {
                        const std::string_view number(PQcmdTuples(raw));
                        const auto parsed = std::from_chars(number.data(), number.data() + number.size(), affected);
                        if (parsed.ec != std::errc{} || parsed.ptr != number.data() + number.size())
                            throw DatabaseError("contract", "Business affected-row count is absent or invalid");
                    }
                }
                else
                    fail_postgres(raw);
            }
            if (!terminal)
                throw DatabaseError("provider", "Business PostgreSQL command did not drain");
            confirm_protocol();
        }
        check_deadline();
        if ((read && step_rows != 1) || (step.expected_affected && affected != *step.expected_affected))
            throw DatabaseError("validation", "Business row/affected count differs: " + step.name);
        total_rows += step_rows;
    }

    void rollback_after_failure()
    {
        // Retirement has its own finite budget, including draining an aborted
        // libpq command. A late terminal is retained by the outer scheduler and
        // cannot become a successful operation at the declared drain cutoff.
        deadline = Clock::now() + options.operation_timeout;
        if (sqlite)
        {
            for (auto& statement : sqlite_cache)
                if (statement)
                {
                    (void)sqlite3_reset(statement->value);
                    (void)sqlite3_clear_bindings(statement->value);
                }
            if (sqlite3_get_autocommit(sqlite))
                return;
            configure_sqlite_deadline();
            SqliteStatement rollback(sqlite, "ROLLBACK");
            ++counts.physical_prepare_calls;
            ++counts.sql_executions;
            if (sqlite3_step(rollback.value) != SQLITE_DONE || !sqlite3_get_autocommit(sqlite))
                fail_sqlite();
        }
        else if (postgres)
        {
            while (auto* raw = receive())
            {
                PgResult retired(raw);
            }
            const auto transaction = PQtransactionStatus(postgres);
            if (transaction == PQTRANS_IDLE)
                return;
            if (transaction != PQTRANS_INTRANS && transaction != PQTRANS_INERROR)
                throw DatabaseError("connection", "Cannot establish transaction state before rollback");
            if (!PQsendQuery(postgres, "ROLLBACK"))
                fail_postgres();
            ++counts.protocol_submissions;
            ++counts.sql_executions;
            command_results();
            if (PQtransactionStatus(postgres) != PQTRANS_IDLE)
                throw DatabaseError("provider", "Rollback did not retire the PostgreSQL transaction");
        }
    }

    static std::string failure_text(const std::exception_ptr& failure)
    {
        try
        {
            std::rethrow_exception(failure);
        }
        catch (const std::exception& error)
        {
            return error.what();
        }
        catch (...)
        {
            return "non-standard exception";
        }
    }

    OperationResult execute_business(std::size_t index, std::int64_t worker_key, Clock::time_point limit)
    {
        const auto& query = contract.cases[index];
        if (worker_key < 1 || worker_key > 128)
            reject("Logical worker key must lie within the fixed 128-row state table");
        Digest digest;
        std::uint64_t rows = 0;
        bool transaction = false, observed_conflict = false;
        try
        {
            for (std::size_t i = 0; i < query.steps.size(); ++i)
            {
                const auto& step = query.steps[i];
                // Match the common five-second command limit. An entire fetch,
                // including every result, shares this one deadline. The outer
                // drain deadline also bounds the whole business operation.
                deadline = std::min(Clock::now() + options.operation_timeout, limit);
                execute_step(cache_offsets[index] + i, step, worker_key, digest, rows);
                if (step.kind == "begin")
                    transaction = true;
                else if (step.kind == "commit" || step.kind == "rollback")
                    transaction = false;
                if (step.expected_affected && *step.expected_affected == 0)
                    observed_conflict = true;
                if ((sqlite && (sqlite3_get_autocommit(sqlite) == 0) != transaction) ||
                    (postgres && PQtransactionStatus(postgres) != (transaction ? PQTRANS_INTRANS : PQTRANS_IDLE)))
                    throw DatabaseError("validation", "Business transaction state differs after: " + step.name);
            }
            const std::string_view outcome = observed_conflict ? "conflict" : "success";
            if (rows != query.expected_rows || digest.value != query.expected_digest ||
                outcome != query.expected_outcome)
                throw DatabaseError("validation", "Business aggregate oracle differs: " + query.name);
            return {rows, digest.value, outcome};
        }
        catch (...)
        {
            const auto primary = std::current_exception();
            try
            {
                rollback_after_failure();
            }
            catch (...)
            {
                const auto cleanup = std::current_exception();
                ++counts.cleanup_failures;
                throw CleanupError("Business failure: " + failure_text(primary) +
                                       "; rollback retirement failure: " + failure_text(cleanup),
                                   primary, cleanup);
            }
            std::rethrow_exception(primary);
        }
    }

    void prepare_all()
    {
        if (options.mode != ConstructionMode::Prepared)
            return;
        connect();
        auto prepare = [&](std::size_t slot, const StatementDefinition& query, bool mutation)
        {
            deadline = Clock::now() + options.operation_timeout;
            if (sqlite && !sqlite_cache[slot])
            {
                sqlite_cache[slot] = std::make_unique<SqliteStatement>(sqlite, query.sql);
                ++counts.physical_prepare_calls;
                validate_sqlite_shape(sqlite_cache[slot]->value, query, mutation);
            }
            if (postgres && !postgres_prepared[slot])
                prepare_postgres(slot, query, query.sql);
        };
        for (std::size_t i = 0; i < contract.cases.size(); ++i)
        {
            if (cache_offsets[i] == std::numeric_limits<std::size_t>::max())
                continue;
            const auto& query = contract.cases[i];
            if (query.steps.empty())
                prepare(cache_offsets[i], query, false);
            else
                for (std::size_t step = 0; step < query.steps.size(); ++step)
                    prepare(cache_offsets[i] + step, query.steps[step], query.steps[step].kind == "execute");
        }
    }

    OperationResult execute(std::size_t index, Clock::time_point limit, std::int64_t worker_key)
    {
        const auto& query = contract.cases.at(index);
        const auto slot = cache_offsets.at(index);
        if (slot == std::numeric_limits<std::size_t>::max())
            throw DatabaseError("configuration", "Attempted to execute a case outside the selected workload");
        try
        {
            connect(limit);
            deadline = std::min(Clock::now() + options.operation_timeout, limit);
            check_deadline();
            if (!query.steps.empty())
                return execute_business(index, worker_key, limit);
            std::string dynamic_sql;
            if (options.mode == ConstructionMode::Dynamic)
                dynamic_sql = assemble_sql(query);
            const auto& sql = options.mode == ConstructionMode::Dynamic ? dynamic_sql : query.sql;
            Accumulator result(query);
            if (sqlite)
            {
                const auto remaining = std::max<std::int64_t>(
                    0, std::chrono::duration_cast<std::chrono::milliseconds>(deadline - Clock::now()).count());
                if (sqlite3_busy_timeout(sqlite, static_cast<int>(std::min<std::int64_t>(
                                                     options.sqlite_busy_timeout.count(), remaining))) != SQLITE_OK)
                    fail_sqlite();
                std::unique_ptr<SqliteStatement> temporary;
                if (options.mode == ConstructionMode::Prepared)
                {
                    if (!sqlite_cache[slot])
                    {
                        sqlite_cache[slot] = std::make_unique<SqliteStatement>(sqlite, sql);
                        ++counts.physical_prepare_calls;
                    }
                }
                else
                {
                    temporary = std::make_unique<SqliteStatement>(sqlite, sql);
                    ++counts.physical_prepare_calls;
                }
                auto* statement =
                    options.mode == ConstructionMode::Prepared ? sqlite_cache[slot]->value : temporary->value;
                validate_sqlite_shape(statement, query);
                bind_sqlite(statement, query, worker_key);
                ++counts.sql_executions;
                int status;
                while ((status = sqlite3_step(statement)) == SQLITE_ROW)
                {
                    check_deadline();
                    ++counts.rows_fetched;
                    result.row(SqliteReader{statement});
                    if (query.row_kind != RowKind::Related)
                        ++counts.rows_consumed;
                }
                if (status != SQLITE_DONE)
                    fail_sqlite();
                if (sqlite3_reset(statement) != SQLITE_OK || sqlite3_clear_bindings(statement) != SQLITE_OK)
                    fail_sqlite();
            }
            else
            {
                if (options.mode == ConstructionMode::Dynamic || !postgres_prepared[slot])
                    prepare_postgres(slot, query, sql);
                confirm_protocol();
                bind_postgres(query, worker_key);
                const char* name = options.mode == ConstructionMode::Prepared ? statement_names[slot].c_str() : "";
                if (!PQsendQueryPrepared(postgres, name, static_cast<int>(query.parameters.size()),
                                         parameter_values.data(), parameter_lengths.data(), parameter_formats.data(),
                                         1))
                    fail_postgres();
                ++counts.sql_executions;
                ++counts.protocol_submissions;
                if (!PQsetSingleRowMode(postgres))
                    throw DatabaseError("provider", "PostgreSQL single-row mode was not admitted");
                bool terminal = false;
                while (auto* raw = receive())
                {
                    PgResult row(raw);
                    if (PQresultStatus(raw) == PGRES_SINGLE_TUPLE)
                    {
                        if (terminal || PQntuples(raw) != 1 || PQnfields(raw) != static_cast<int>(query.columns.size()))
                            throw DatabaseError("contract", "PostgreSQL single-row shape differs");
                        ++counts.rows_fetched;
                        result.row(PgReader{raw});
                        if (query.row_kind != RowKind::Related)
                            ++counts.rows_consumed;
                    }
                    else if (PQresultStatus(raw) == PGRES_TUPLES_OK && PQntuples(raw) == 0 && !terminal)
                        terminal = true;
                    else
                        fail_postgres(raw);
                }
                if (!terminal || PQtransactionStatus(postgres) != PQTRANS_IDLE)
                    throw DatabaseError("provider", "PostgreSQL read did not drain to idle");
                confirm_protocol();
            }
            auto actual = result.finish();
            if (query.row_kind == RowKind::Related)
                counts.rows_consumed += actual.rows;
            if (actual.rows != query.expected_rows || actual.digest != query.expected_digest)
                throw DatabaseError("validation", "Row count or result digest differs: " + query.name +
                                                      " rows=" + std::to_string(actual.rows) +
                                                      " digest=" + std::to_string(actual.digest));
            return actual;
        }
        catch (...)
        {
            // The scheduler may continue with its next planned operation. It
            // never inherits an unproven protocol/transaction state: reconnect
            // lazily on the next execute, without silently retrying this attempt.
            if (sqlite || postgres)
                ++counts.discarded_connections;
            close();
            throw;
        }
    }
};

QueryAdapter::QueryAdapter(const Contract& contract, ConnectionOptions options)
    : impl_(std::make_unique<Impl>(contract, std::move(options)))
{
}
QueryAdapter::~QueryAdapter() = default;
void QueryAdapter::prepare_all()
{
    try
    {
        impl_->prepare_all();
    }
    catch (...)
    {
        if (impl_->sqlite || impl_->postgres)
            ++impl_->counts.discarded_connections;
        impl_->close();
        throw;
    }
}
OperationResult QueryAdapter::execute(std::size_t index, std::int64_t worker_key)
{
    return impl_->execute(index, Clock::time_point::max(), worker_key);
}
OperationResult QueryAdapter::execute_until(std::size_t index, std::chrono::steady_clock::time_point deadline,
                                            std::int64_t worker_key)
{
    return impl_->execute(index, deadline, worker_key);
}
void QueryAdapter::close() noexcept
{
    impl_->close();
}
const Counters& QueryAdapter::counters() const noexcept
{
    return impl_->counts;
}
const std::map<std::string, std::string>& QueryAdapter::effective_configuration() const noexcept
{
    return impl_->configuration;
}
} // namespace db_perf
