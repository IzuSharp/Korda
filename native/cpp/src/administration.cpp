#include "nw/administration.hpp"

#include <nlohmann/json.hpp>
#include <openssl/sha.h>

#include <algorithm>
#include <bit>
#include <limits>
#include <map>
#include <string_view>

namespace nw
{
namespace
{

constexpr std::uint64_t HOUR_NS = 3'600'000'000'000;
using Applied = std::map<std::int64_t, Bytes>;

[[noreturn]] void checksum_error(const char* message)
{
    throw Error(NW_DOMAIN_ERROR, 15, message);
}
[[noreturn]] void dirty_error(const char* message)
{
    throw Error(NW_DOMAIN_ERROR, 8, message);
}
[[noreturn]] void capacity_error()
{
    throw Error(NW_DOMAIN_ERROR, 6, "administrative result exceeds its finite budget");
}

Bytes bytes(std::string_view value)
{
    return Bytes(value.begin(), value.end());
}
Bytes u32(std::uint32_t value)
{
    Bytes result(4);
    put_u32(result, 0, value);
    return result;
}
Bytes u64(std::uint64_t value)
{
    Bytes result(8);
    put_u64(result, 0, value);
    return result;
}
Codec codec(std::uint32_t id, bool nullable = false)
{
    return {id, std::nullopt, nullable};
}
Scalar text_parameter(std::string_view value)
{
    return {codec(5), bytes(value)};
}
Scalar long_parameter(std::int64_t value)
{
    return {codec(3), u64(std::bit_cast<std::uint64_t>(value))};
}
Scalar blob_parameter(ByteView value)
{
    return {codec(6), Bytes(value.begin(), value.end())};
}
Scalar bool_parameter(bool value)
{
    return {codec(4), Bytes{static_cast<std::uint8_t>(value)}};
}

template <typename F> auto provider(F&& action)
{
    try
    {
        return action();
    }
    catch (const Error& error)
    {
        if (error.status == NW_CANCELED || error.status == NW_TIMEOUT)
            throw;
        Error mapped(NW_DOMAIN_ERROR, 1, "administrative provider command failed");
        mapped.provider = error.provider;
        mapped.cleanup = error.cleanup;
        throw mapped;
    }
}

void execute(ProviderConnection& connection, std::string_view sql, const std::vector<Scalar>& parameters,
             const CallContext& context)
{
    (void)provider([&] { return connection.execute(sql, parameters, context); });
}

std::unique_ptr<ProviderStatement> query(ProviderConnection& connection, std::string_view sql,
                                         const std::vector<Scalar>& parameters, const CallContext& context)
{
    return provider([&] { return connection.query(sql, parameters, context); });
}

std::optional<Row> next(ProviderStatement& statement, const std::vector<Codec>& codecs, const CallContext& context)
{
    return provider([&] { return statement.next(codecs, MAX_BYTES, context); });
}

const Bytes& cell(const Row& row, std::size_t ordinal)
{
    if (ordinal >= row.size() || !row[ordinal])
        throw Error(NW_DOMAIN_ERROR, 1, "administrative provider returned an invalid row");
    return *row[ordinal];
}

std::string text(const Row& row, std::size_t ordinal)
{
    const auto& value = cell(row, ordinal);
    return {value.begin(), value.end()};
}

std::int64_t number(const Row& row, std::size_t ordinal)
{
    const auto& value = cell(row, ordinal);
    if (value.size() == 8)
        return std::bit_cast<std::int64_t>(read_u64(value, 0));
    if (value.size() == 4)
        return std::bit_cast<std::int32_t>(read_u32(value, 0));
    if (value.size() == 1)
        return value[0];
    throw Error(NW_DOMAIN_ERROR, 1, "administrative provider returned an invalid integer");
}

Row one(ProviderConnection& connection, std::string_view sql, const std::vector<Scalar>& parameters,
        const std::vector<Codec>& codecs, const CallContext& context)
{
    auto statement = query(connection, sql, parameters, context);
    auto row = next(*statement, codecs, context);
    if (!row || next(*statement, codecs, context))
        throw Error(NW_DOMAIN_ERROR, 1, "administrative scalar row count differs");
    statement->close();
    return std::move(*row);
}

class Budget
{
  public:
    explicit Budget(const AdministrationLimits& limits) : limits_(limits) {}
    void row(std::size_t overhead)
    {
        if (++rows_ > limits_.rows || overhead > maximum() - std::min(bytes_, maximum()))
            capacity_error();
        bytes_ += overhead;
    }
    void text(std::string_view value)
    {
        if (value.size() > limits_.field_bytes || value.find('\0') != std::string_view::npos ||
            value.size() > maximum() - std::min(bytes_, maximum()))
            capacity_error();
        bytes_ += value.size();
    }

  private:
    std::size_t maximum() const
    {
        return std::min(limits_.bytes, MAX_BYTES);
    }
    const AdministrationLimits& limits_;
    std::size_t rows_ = 0;
    std::size_t bytes_ = 48;
};

bool ascii_alpha(char ch)
{
    return (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');
}
bool ascii_digit(char ch)
{
    return ch >= '0' && ch <= '9';
}
bool ascii_space(char ch)
{
    return ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r' || ch == '\v' || ch == '\f';
}
std::string uppercase(std::string_view value)
{
    std::string result(value);
    for (auto& ch : result)
        if (ch >= 'a' && ch <= 'z')
            ch = static_cast<char>(ch - ('a' - 'A'));
    return result;
}

// Exact bounded statement-prefix guard from the accepted product. Database
// parsers still interpret scripts; quoted/comment text is not transaction SQL.
void validate_script(std::string_view sql)
{
    std::size_t at = 0;
    bool prefix = true;
    while (at < sql.size())
    {
        if (ascii_space(sql[at]))
        {
            ++at;
            continue;
        }
        if (sql.substr(at).starts_with("--"))
        {
            at += 2;
            while (at < sql.size() && sql[at] != '\n')
                ++at;
            continue;
        }
        if (sql.substr(at).starts_with("/*"))
        {
            at += 2;
            std::size_t depth = 1;
            while (at < sql.size() && depth > 0)
            {
                if (sql.substr(at).starts_with("/*"))
                {
                    ++depth;
                    at += 2;
                }
                else if (sql.substr(at).starts_with("*/"))
                {
                    --depth;
                    at += 2;
                }
                else
                    ++at;
            }
            if (depth != 0)
                throw Error::invalid("unterminated migration comment");
            continue;
        }
        if (sql[at] == '\'' || sql[at] == '"' || sql[at] == '`' || sql[at] == '[')
        {
            const auto quote = sql[at] == '[' ? ']' : sql[at];
            ++at;
            bool closed = false;
            while (at < sql.size())
            {
                if (sql[at++] == quote)
                {
                    if (at < sql.size() && sql[at] == quote)
                        ++at;
                    else
                    {
                        closed = true;
                        break;
                    }
                }
            }
            if (!closed)
                throw Error::invalid("unterminated migration quote");
            prefix = false;
            continue;
        }
        if (sql[at] == '$')
        {
            const auto start = at;
            auto end = at + 1;
            while (end < sql.size() && (ascii_alpha(sql[end]) || ascii_digit(sql[end]) || sql[end] == '_'))
                ++end;
            if (end < sql.size() && sql[end] == '$')
            {
                const auto delimiter = sql.substr(start, end - start + 1);
                const auto closing = sql.find(delimiter, end + 1);
                if (closing == std::string_view::npos)
                    throw Error::invalid("unterminated migration dollar quote");
                at = closing + delimiter.size();
                prefix = false;
                continue;
            }
        }
        if (sql[at] == ';')
        {
            prefix = true;
            ++at;
            continue;
        }
        if (ascii_alpha(sql[at]) || sql[at] == '_')
        {
            const auto start = at;
            while (at < sql.size() && (ascii_alpha(sql[at]) || ascii_digit(sql[at]) || sql[at] == '_'))
                ++at;
            if (prefix)
            {
                const auto word = uppercase(sql.substr(start, at - start));
                if (word == "BEGIN" || word == "COMMIT" || word == "END" || word == "ROLLBACK" || word == "SAVEPOINT" ||
                    word == "RELEASE" || word == "START" || word == "PREPARE")
                    throw Error::invalid("migration scripts cannot issue transaction control");
            }
            prefix = false;
            continue;
        }
        prefix = false;
        ++at;
    }
}

void validate_limits(const AdministrationLimits& limits)
{
    if (limits.rows > 1'000'000 || limits.bytes > (1ULL << 30) || limits.field_bytes == 0 ||
        limits.field_bytes > (64ULL << 20) || limits.command_timeout_ns == 0 || limits.command_timeout_ns > HOUR_NS)
        throw Error::invalid("invalid administrative limits");
}

MigrationBundle parse_bundle(ByteView input, const AdministrationLimits& limits)
{
    const auto fields = Fields::parse(input, 12, {{1, 1, true}, {2, 5, true}, {3, 5, true}});
    MigrationBundle result;
    result.provider = fields.u32(1);
    const auto entries = fields.bytes(2);
    const auto canonical =
        encode_control(12, {{1, 1, u32(result.provider)}, {2, 5, Bytes(entries.begin(), entries.end())}});
    result.content_hash = sha256(canonical);
    if (!std::ranges::equal(fields.bytes(3), result.content_hash))
        throw Error::invalid("migration bundle hash mismatch");
    const auto sequence_entries = sequence(entries);
    if (sequence_entries.size() > limits.nodes)
        capacity_error();
    if (result.provider > 1 || sequence_entries.size() > MAX_NODES)
        throw Error::invalid("invalid migration bundle");
    std::int64_t previous = 0;
    std::size_t size = 0;
    for (const auto entry : sequence_entries)
    {
        const auto record =
            Fields::parse(entry, 11, {{1, 2, true}, {2, 4, true}, {3, 4, true}, {4, 1, true}, {5, 5, true}});
        const auto version = record.u64(1);
        if (version > static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max()))
            throw Error::invalid("migration version overflow");
        MigrationEntry migration;
        migration.version = static_cast<std::int64_t>(version);
        migration.description = record.text(2);
        migration.sql = record.text(3);
        migration.non_transactional = record.boolean(4);
        if (migration.version <= 0 || migration.description.empty() || migration.sql.empty() ||
            migration.description.find('\0') != std::string::npos || migration.sql.find('\0') != std::string::npos ||
            migration.description.size() > MAX_BYTES || migration.sql.size() > MAX_BYTES)
            throw Error::invalid("invalid migration entry");
        validate_script(migration.sql);
        migration.sql_hash = sha256(bytes(migration.sql));
        if (!std::ranges::equal(record.bytes(5), migration.sql_hash))
            throw Error::invalid("migration SQL hash mismatch");
        migration.checksum.resize(SHA384_DIGEST_LENGTH);
        if (SHA384(reinterpret_cast<const unsigned char*>(migration.sql.data()), migration.sql.size(),
                   migration.checksum.data()) == nullptr)
            throw Error(NW_DOMAIN_ERROR, 1, "migration checksum computation failed");
        if (migration.version <= previous)
            throw Error::invalid("migration versions must be positive and strictly ascending");
        previous = migration.version;
        size += migration.description.size() + migration.sql.size();
        if (size > MAX_BYTES)
            capacity_error();
        result.migrations.push_back(std::move(migration));
    }
    return result;
}

Bytes encode_sequence(const std::vector<Bytes>& entries)
{
    Bytes output = u32(static_cast<std::uint32_t>(entries.size()));
    for (const auto& entry : entries)
    {
        const auto length = u32(static_cast<std::uint32_t>(entry.size()));
        output.insert(output.end(), length.begin(), length.end());
        output.insert(output.end(), entry.begin(), entry.end());
    }
    return output;
}

Bytes report(std::uint32_t applied, std::optional<std::int64_t> current)
{
    std::vector<ControlField> fields{{1, 1, u32(0)}, {3, 1, u32(applied)}};
    if (current)
        fields.push_back({4, 2, u64(static_cast<std::uint64_t>(*current))});
    fields.push_back({5, 1, u32(1)});
    return encode_control(19, fields);
}

Bytes statuses(const MigrationBundle& bundle, const Applied& applied, const AdministrationLimits& limits)
{
    Budget budget(limits);
    std::vector<Bytes> entries;
    for (const auto& entry : bundle.migrations)
    {
        const auto checksum = hex(entry.checksum);
        budget.row(112);
        budget.text(entry.description);
        budget.text(checksum);
        entries.push_back(encode_control(16, {{1, 2, u64(static_cast<std::uint64_t>(entry.version))},
                                              {2, 4, bytes(entry.description)},
                                              {3, 4, bytes(checksum)},
                                              {4, 1, u32(applied.contains(entry.version))}}));
    }
    return encode_control(19, {{1, 1, u32(1)}, {2, 5, encode_sequence(entries)}});
}

bool exists(ProviderConnection& connection, std::string_view table, const CallContext& context)
{
    if (connection.profile() == Profile::PostgreSql)
        return number(
                   one(connection, "SELECT to_regclass($1) IS NOT NULL", {text_parameter(table)}, {codec(4)}, context),
                   0) != 0;
    return number(one(connection, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name=?1",
                      {text_parameter(table)}, {codec(3)}, context),
                  0) != 0;
}

Applied ledger(ProviderConnection& connection, const AdministrationLimits& limits, const CallContext& context)
{
    Applied result;
    if (!exists(connection, "_sqlx_migrations", context))
        return result;
    auto statement =
        query(connection, "SELECT version, checksum, success FROM _sqlx_migrations ORDER BY version", {}, context);
    Budget budget(limits);
    while (auto row = next(*statement, {codec(3), codec(6), codec(4)}, context))
    {
        budget.row(96);
        if (!number(*row, 2))
            dirty_error("SQLx ledger contains an unsuccessful migration");
        const auto version = number(*row, 0);
        const auto& checksum = cell(*row, 1);
        if (version <= 0 || checksum.size() != 48 || !result.emplace(version, checksum).second)
            checksum_error("invalid SQLx migration ledger");
    }
    statement->close();
    return result;
}

const MigrationEntry* find_entry(const MigrationBundle& bundle, std::int64_t version)
{
    const auto found =
        std::lower_bound(bundle.migrations.begin(), bundle.migrations.end(), version,
                         [](const MigrationEntry& entry, std::int64_t value) { return entry.version < value; });
    return found != bundle.migrations.end() && found->version == version ? &*found : nullptr;
}

void validate_ledger(const MigrationBundle& bundle, const Applied& applied)
{
    for (const auto& [version, checksum] : applied)
    {
        const auto* entry = find_entry(bundle, version);
        if (!entry)
            checksum_error("applied migration is absent from this bundle");
        if (entry->checksum != checksum)
            checksum_error("migration checksum differs from SQLx ledger");
    }
}

void phases(ProviderConnection& connection, const AdministrationRequest& request, const Applied& applied,
            const AdministrationLimits& limits, const CallContext& context)
{
    if (!exists(connection, "_nativeweave_migration_phases", context))
        return;
    const auto& bundle = *request.bundle;
    const bool postgres = request.profile == Profile::PostgreSql;
    auto statement = query(connection,
                           "SELECT version, provider, sql_hash, bundle_hash, description, non_transactional, phase "
                           "FROM _nativeweave_migration_phases ORDER BY version",
                           {}, context);
    Budget budget(limits);
    while (auto row =
               next(*statement, {codec(3), codec(postgres ? 2 : 3), codec(6), codec(6), codec(5), codec(4), codec(5)},
                    context))
    {
        budget.row(128);
        const auto version = number(*row, 0);
        const auto* entry = find_entry(bundle, version);
        if (!entry)
            checksum_error("migration phase is absent from the supplied bundle");
        const auto description = text(*row, 4);
        const auto phase = text(*row, 6);
        budget.text(description);
        if (number(*row, 1) != (postgres ? 0 : 1) || !std::ranges::equal(cell(*row, 2), entry->sql_hash) ||
            cell(*row, 3).size() != 32 || description != entry->description ||
            (number(*row, 5) != 0) != entry->non_transactional ||
            (postgres && phase != "applying" && phase != "applied"))
            checksum_error("persisted migration phase identity differs");
        if (postgres)
        {
            if (!applied.contains(version) && (phase == "applied" || request.action == 2 || !entry->non_transactional))
                dirty_error("migration has an unfinished execution phase");
        }
        else if (phase != "applied" || !applied.contains(version))
            dirty_error("migration has an unfinished execution phase");
    }
    statement->close();
}

void phase(ProviderConnection& connection, const MigrationEntry& entry, const MigrationBundle& bundle,
           std::string_view phase, const CallContext& context)
{
    if (connection.profile() == Profile::PostgreSql)
    {
        execute(connection,
                "INSERT INTO _nativeweave_migration_phases (version, provider, sql_hash, bundle_hash, description, "
                "non_transactional, phase) VALUES ($1,0,$2,$3,$4,$5,$6) ON CONFLICT (version) DO UPDATE SET "
                "phase=EXCLUDED.phase",
                {long_parameter(entry.version), blob_parameter(entry.sql_hash), blob_parameter(bundle.content_hash),
                 text_parameter(entry.description), bool_parameter(entry.non_transactional), text_parameter(phase)},
                context);
    }
    else
    {
        execute(connection,
                "INSERT INTO _nativeweave_migration_phases (version, provider, sql_hash, bundle_hash, description, "
                "non_transactional, phase) VALUES (?1,1,?2,?3,?4,0,?5) ON CONFLICT (version) DO UPDATE SET "
                "phase=excluded.phase",
                {long_parameter(entry.version), blob_parameter(entry.sql_hash), blob_parameter(bundle.content_hash),
                 text_parameter(entry.description), text_parameter(phase)},
                context);
    }
}

void ensure_tables(ProviderConnection& connection, const CallContext& context)
{
    const bool postgres = connection.profile() == Profile::PostgreSql;
    execute(connection,
            postgres ? "CREATE TABLE IF NOT EXISTS _sqlx_migrations (version BIGINT PRIMARY KEY, description TEXT NOT "
                       "NULL, installed_on TIMESTAMPTZ NOT NULL DEFAULT now(), success BOOLEAN NOT NULL, checksum "
                       "BYTEA NOT NULL, execution_time BIGINT NOT NULL)"
                     : "CREATE TABLE IF NOT EXISTS _sqlx_migrations (version BIGINT PRIMARY KEY, description TEXT NOT "
                       "NULL, installed_on TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, success BOOLEAN NOT NULL, "
                       "checksum BLOB NOT NULL, execution_time BIGINT NOT NULL)",
            {}, context);
    execute(connection,
            postgres ? "CREATE TABLE IF NOT EXISTS _nativeweave_migration_phases (version BIGINT PRIMARY KEY, provider "
                       "INTEGER NOT NULL, sql_hash BYTEA NOT NULL, bundle_hash BYTEA NOT NULL, description TEXT NOT "
                       "NULL, non_transactional BOOLEAN NOT NULL, phase TEXT NOT NULL)"
                     : "CREATE TABLE IF NOT EXISTS _nativeweave_migration_phases (version BIGINT PRIMARY KEY, provider "
                       "INTEGER NOT NULL, sql_hash BLOB NOT NULL, bundle_hash BLOB NOT NULL, description TEXT NOT "
                       "NULL, non_transactional BOOLEAN NOT NULL, phase TEXT NOT NULL)",
            {}, context);
}

void append_cleanup(Error& error, const Error& cleanup)
{
    auto* owner = &error;
    while (owner->cleanup)
        owner = owner->cleanup.get();
    owner->cleanup = std::make_shared<Error>(cleanup);
}

CallContext cleanup_context()
{
    return {{}, Clock::now() + std::chrono::seconds(5), false};
}

void rollback(ProviderConnection& connection, Error& original)
{
    // The accepted adapter explicitly rolled back its tracked transaction even
    // if SQLite interruption had already auto-aborted it. Keep that secondary
    // "no transaction" failure in the cleanup chain instead of hiding it.
    try
    {
        execute(connection, "ROLLBACK", {}, cleanup_context());
    }
    catch (const Error& cleanup)
    {
        append_cleanup(original, cleanup);
    }
}

void apply_entry(ProviderConnection& connection, const MigrationEntry& entry, const CallContext& context)
{
    const auto began = Clock::now();
    if (!entry.non_transactional)
        execute(connection, "SAVEPOINT _nw_migration_entry", {}, context);
    provider([&] { connection.execute_script(entry.sql, context); });
    const bool postgres = connection.profile() == Profile::PostgreSql;
    execute(connection,
            postgres ? "INSERT INTO _sqlx_migrations (version, description, success, checksum, execution_time) VALUES "
                       "($1,$2,TRUE,$3,-1)"
                     : "INSERT INTO _sqlx_migrations (version, description, success, checksum, execution_time) VALUES "
                       "(?1,?2,TRUE,?3,-1)",
            {long_parameter(entry.version), text_parameter(entry.description), blob_parameter(entry.checksum)},
            context);
    if (!entry.non_transactional)
        execute(connection, "RELEASE SAVEPOINT _nw_migration_entry", {}, context);
    const auto elapsed = std::chrono::duration_cast<std::chrono::nanoseconds>(Clock::now() - began).count();
    execute(connection,
            postgres ? "UPDATE _sqlx_migrations SET execution_time=$1 WHERE version=$2"
                     : "UPDATE _sqlx_migrations SET execution_time=?1 WHERE version=?2",
            {long_parameter(elapsed), long_parameter(entry.version)}, context);
}

std::uint32_t crc32(std::string_view bytes)
{
    std::uint32_t crc = 0xffffffff;
    for (const auto byte : bytes)
    {
        crc ^= static_cast<unsigned char>(byte);
        for (unsigned bit = 0; bit < 8; ++bit)
            crc = (crc >> 1) ^ ((crc & 1) ? 0xedb88320U : 0U);
    }
    return ~crc;
}

struct CatalogColumn
{
    std::string name;
    std::string store_type;
    bool nullable = false;
    bool primary_key = false;
};

struct CatalogTable
{
    std::string schema;
    std::string name;
    std::vector<CatalogColumn> columns;
};

Bytes encode_tables(const std::vector<CatalogTable>& tables)
{
    std::vector<Bytes> records;
    for (const auto& table : tables)
    {
        std::vector<Bytes> columns;
        for (const auto& column : table.columns)
            columns.push_back(encode_control(17, {{1, 4, bytes(column.name)},
                                                  {2, 4, bytes(column.store_type)},
                                                  {3, 1, u32(column.nullable)},
                                                  {4, 1, u32(column.primary_key)}}));
        records.push_back(encode_control(
            18, {{1, 4, bytes(table.schema)}, {2, 4, bytes(table.name)}, {3, 5, encode_sequence(columns)}}));
    }
    return encode_control(19, {{1, 1, u32(3)}, {2, 5, encode_sequence(records)}});
}

Bytes catalog(ProviderConnection& connection, const AdministrationRequest& request, const AdministrationLimits& limits,
              const CallContext& context)
{
    Budget budget(limits);
    std::vector<CatalogTable> tables;
    if (request.profile == Profile::PostgreSql)
    {
        const auto selected = request.schema
                                  ? *request.schema
                                  : text(one(connection, "SELECT current_schema()::text", {}, {codec(5)}, context), 0);
        if (selected.starts_with("pg_") || selected == "information_schema")
            throw Error::invalid("system schema scope is not admitted");
        auto statement = query(
            connection,
            "SELECT c.relname::text AS table_name, a.attname::text AS column_name, format_type(a.atttypid,a.atttypmod) "
            "AS store_type, NOT a.attnotnull AS nullable, EXISTS (SELECT 1 FROM pg_index i WHERE i.indrelid=c.oid AND "
            "i.indisprimary AND a.attnum=ANY(i.indkey)) AS primary_key FROM pg_class c JOIN pg_namespace n ON "
            "n.oid=c.relnamespace LEFT JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped "
            "WHERE n.nspname=$1 AND c.relkind IN ('r','p') ORDER BY c.relname,a.attnum",
            {text_parameter(selected)}, context);
        while (auto row =
                   next(*statement, {codec(5), codec(5, true), codec(5, true), codec(4, true), codec(4)}, context))
        {
            context.check();
            const auto name = text(*row, 0);
            if (tables.empty() || tables.back().name != name)
            {
                budget.row(80);
                budget.text(selected);
                budget.text(name);
                tables.push_back({selected, name, {}});
            }
            if (!(*row)[1])
                continue;
            const auto column = text(*row, 1);
            const auto store = text(*row, 2);
            budget.row(92);
            budget.text(column);
            budget.text(store);
            tables.back().columns.push_back({column, store, number(*row, 3) != 0, number(*row, 4) != 0});
        }
        statement->close();
    }
    else
    {
        std::vector<std::string> names;
        {
            auto statement =
                query(connection,
                      "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name",
                      {}, context);
            while (auto row = next(*statement, {codec(5)}, context))
            {
                context.check();
                const auto name = text(*row, 0);
                budget.row(80);
                budget.text("main");
                budget.text(name);
                names.push_back(name);
            }
            statement->close();
        }
        for (auto& name : names)
        {
            CatalogTable table{"main", std::move(name), {}};
            const auto primary_index =
                number(one(connection, "SELECT count(*) FROM pragma_index_list(?1) WHERE origin='pk'",
                           {text_parameter(table.name)}, {codec(3)}, context),
                       0);
            auto statement =
                query(connection, "SELECT name,type,\"notnull\",pk FROM pragma_table_info(?1) ORDER BY cid",
                      {text_parameter(table.name)}, context);
            while (auto row = next(*statement, {codec(5), codec(5), codec(3), codec(3)}, context))
            {
                context.check();
                const auto column = text(*row, 0);
                const auto store = text(*row, 1);
                budget.row(92);
                budget.text(column);
                budget.text(store);
                const bool primary_key = number(*row, 3) > 0;
                const bool nullable =
                    number(*row, 2) == 0 && !(primary_key && primary_index == 0 && uppercase(store) == "INTEGER");
                table.columns.push_back({column, store, nullable, primary_key});
            }
            statement->close();
            tables.push_back(std::move(table));
        }
    }
    return encode_tables(tables);
}

Bytes execute_locked(ProviderConnection& connection, const AdministrationRequest& request,
                     const AdministrationLimits& limits, const CallContext& context)
{
    const auto& bundle = *request.bundle;
    if (request.action == 0)
        ensure_tables(connection, context);
    auto applied = ledger(connection, limits, context);
    validate_ledger(bundle, applied);
    phases(connection, request, applied, limits, context);
    if (request.action == 1)
        return statuses(bundle, applied, limits);
    if (request.action == 2)
        return encode_control(19, {{1, 1, u32(2)}});
    if (request.target && !applied.empty() && applied.rbegin()->first > *request.target)
        throw Error::invalid("migrations cannot downgrade the current version");
    std::uint32_t count = 0;
    const bool postgres = request.profile == Profile::PostgreSql;
    if (!postgres)
    {
        for (const auto& entry : bundle.migrations)
        {
            if (request.target && entry.version > *request.target)
                break;
            if (!applied.contains(entry.version))
            {
                phase(connection, entry, bundle, "applying", context);
                ++count;
            }
        }
        context.check();
        for (const auto& entry : bundle.migrations)
        {
            if (request.target && entry.version > *request.target)
                break;
            if (!applied.contains(entry.version))
                apply_entry(connection, entry, context);
        }
        context.check();
        for (const auto& entry : bundle.migrations)
        {
            if (request.target && entry.version > *request.target)
                break;
            if (!applied.contains(entry.version))
                phase(connection, entry, bundle, "applied", context);
            applied.insert_or_assign(entry.version, entry.checksum);
        }
    }
    else
    {
        for (const auto& entry : bundle.migrations)
        {
            if (request.target && entry.version > *request.target)
                break;
            if (applied.contains(entry.version))
                continue;
            context.check();
            if (entry.non_transactional)
            {
                phase(connection, entry, bundle, "applying", context);
                apply_entry(connection, entry, context);
                phase(connection, entry, bundle, "applied", context);
            }
            else
            {
                execute(connection, "BEGIN", {}, context);
                try
                {
                    phase(connection, entry, bundle, "applying", context);
                    apply_entry(connection, entry, context);
                    context.check();
                    phase(connection, entry, bundle, "applied", context);
                    execute(connection, "COMMIT", {}, context);
                }
                catch (Error& error)
                {
                    rollback(connection, error);
                    throw;
                }
            }
            applied.emplace(entry.version, entry.checksum);
            ++count;
        }
        context.check();
    }
    return report(count, applied.empty() ? std::nullopt : std::optional(applied.rbegin()->first));
}

} // namespace

AdministrationRequest parse_administration(Profile profile, std::uint32_t action, ByteView input,
                                           const AdministrationLimits& limits)
{
    validate_limits(limits);
    if (input.size() > limits.parameter_bytes)
        capacity_error();
    if (action == 4)
    {
        const auto fields = Fields::parse(input, 5, {{1, 1, true}, {7, 5, true}, {8, 5, true}});
        if (fields.u32(1) != (profile == Profile::PostgreSql ? 0U : 1U))
            throw Error::invalid("schema draft provider differs from source");
        AdministrationRequest result;
        result.profile = profile;
        result.action = action;
        std::size_t nodes = 0;
        const auto parse_models = [&](ByteView encoded, std::vector<Model>& models)
        {
            for (const auto entry : sequence(encoded))
            {
                auto model = Model::parse(entry, profile);
                nodes += 1 + model.columns().size();
                if (nodes > limits.nodes)
                    capacity_error();
                if (model.table().size() > limits.field_bytes ||
                    (model.schema() && model.schema()->size() > limits.field_bytes))
                    capacity_error();
                for (const auto& column : model.columns())
                    if (column.name.size() > limits.field_bytes)
                        capacity_error();
                models.push_back(std::move(model));
            }
        };
        parse_models(fields.bytes(7), result.previous_models);
        parse_models(fields.bytes(8), result.current_models);
        return result;
    }
    const auto fields = Fields::parse(
        input, 5, {{1, 1, true}, {2, 5, false}, {3, 2, false}, {4, 3, false}, {5, 1, false}, {6, 4, false}});
    const auto provider_id = profile == Profile::PostgreSql ? 0U : 1U;
    if (fields.u32(1) != provider_id)
        throw Error::invalid("administration provider differs from source");
    AdministrationRequest result;
    result.profile = profile;
    result.action = action;
    if (const auto bundle = fields.get(2))
        result.bundle = parse_bundle(*bundle, limits);
    if (fields.get(3))
    {
        const auto target = fields.u64(3);
        if (target > static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max()))
            throw Error::invalid("target version overflow");
        result.target = static_cast<std::int64_t>(target);
    }
    if (fields.get(4))
        result.lock_timeout_ns = fields.u64(4);
    if (fields.get(5))
        result.allow_non_transactional = fields.boolean(5);
    if (fields.get(6))
        result.schema = fields.text(6);
    if (action > 3 || result.lock_timeout_ns == 0 || result.lock_timeout_ns > HOUR_NS ||
        (result.target && *result.target <= 0) || ((action == 3) != !result.bundle) ||
        (action != 0 && (result.target || result.allow_non_transactional)) || (action != 3 && result.schema))
        throw Error::invalid("invalid administration action");
    if (result.bundle)
    {
        if (result.bundle->provider != provider_id)
            throw Error::invalid("migration provider differs from source");
        std::size_t selected = 0;
        for (const auto& entry : result.bundle->migrations)
        {
            if (entry.description.size() > limits.field_bytes || entry.sql.size() > limits.field_bytes)
                capacity_error();
            const bool included = !result.target || entry.version <= *result.target;
            if (included)
                ++selected;
            if (entry.non_transactional &&
                (provider_id == 1 || (action == 0 && !result.allow_non_transactional && included)))
                throw Error(NW_NOT_SUPPORTED, 5, "nontransactional migration requires an explicit PostgreSQL profile");
        }
        if (action == 0 && selected > limits.rows)
            capacity_error();
    }
    if (result.schema)
    {
        const auto& schema = *result.schema;
        bool valid = !schema.empty() && schema.size() <= 63;
        for (std::size_t i = 0; i < schema.size(); ++i)
            valid = valid && (ascii_alpha(schema[i]) || schema[i] == '_' || (i > 0 && ascii_digit(schema[i])));
        if (!valid || schema.starts_with("pg_") || schema == "information_schema" ||
            (provider_id == 1 && schema != "main"))
            throw Error::invalid("schema scope is not an admitted user schema");
    }
    if (limits.bytes < (action == 0 ? 100U : 36U) || (action == 0 && limits.rows == 0))
        capacity_error();
    return result;
}

namespace
{
std::string draft_identifier(std::string_view value, Profile profile)
{
    if (value.empty() || value.find('\0') != std::string_view::npos ||
        (profile == Profile::PostgreSql && value.size() > 63))
        throw Error::invalid("schema draft identifier is empty, contains NUL or exceeds the provider limit");
    std::string result = "\"";
    for (const auto ch : value)
    {
        result += ch;
        if (ch == '"')
            result += '"';
    }
    return result + '"';
}

std::string draft_table(const Model& model, Profile profile)
{
    const auto name = draft_identifier(model.table(), profile);
    if (!model.schema())
        return name;
    if ((profile == Profile::Sqlite && *model.schema() != "main") || model.schema()->starts_with("pg_") ||
        *model.schema() == "information_schema")
        throw Error::invalid("schema draft admits only user schemas in the selected provider");
    return draft_identifier(*model.schema(), profile) + '.' + name;
}

[[noreturn]] void unsupported_draft(const Model& model, const Column* column, std::string_view reason)
{
    throw Error::invalid("schema draft cannot " + std::string(reason) + " for table " + model.table() +
                         (column ? ", column " + column->name : "") + "; use reviewed explicit SQL");
}

std::string draft_type(const Model& model, const Column& column, Profile profile)
{
    const auto id = column.codec.id;
    if (profile == Profile::Sqlite)
    {
        switch (id)
        {
            case 1:
            case 2:
            case 3:
            case 4:
            case 10:
            case 15:
            case 17:
                return "INTEGER";
            case 5:
            case 8:
            case 9:
            case 14:
            case 16:
                return "TEXT";
            case 6:
            case 7:
                return "BLOB";
            case 11:
            case 12:
                return "REAL";
            default:
                unsupported_draft(model, &column, "create this SQLite scalar type");
        }
    }
    switch (id)
    {
        case 1:
            return "SMALLINT";
        case 2:
        case 17:
            return "INTEGER";
        case 3:
        case 15:
            return "BIGINT";
        case 4:
            return "BOOLEAN";
        case 5:
            return "TEXT";
        case 6:
            return "BYTEA";
        case 7:
            return "UUID";
        case 8:
            return "DATE";
        case 9:
            return "TIME";
        case 10:
            return "TIMESTAMPTZ";
        case 11:
            return "DOUBLE PRECISION";
        case 12:
            return "REAL";
        case 13:
        {
            const auto config = nlohmann::json::parse(*column.codec.configuration);
            return "NUMERIC(" + std::to_string(config.at("precision").get<unsigned>()) + ',' +
                   std::to_string(config.at("scale").get<unsigned>()) + ')';
        }
        case 16:
            return "JSONB";
        default:
            unsupported_draft(model, &column, "create this PostgreSQL scalar type");
    }
}

std::string draft_column(const Model& model, const Column& column, Profile profile, bool new_table)
{
    if (column.collation || column.max_length)
        unsupported_draft(model, &column, "infer collation or byte-length constraints");
    if (column.generated &&
        (!new_table || model.keys().size() != 1 || !column.key_ordinal ||
         (profile == Profile::Sqlite ? column.codec.id != 3 : column.codec.id < 1 || column.codec.id > 3)))
        unsupported_draft(model, &column, "infer a generated/default expression");
    std::string sql = draft_identifier(column.name, profile) + ' ' + draft_type(model, column, profile);
    if (column.generated && profile == Profile::PostgreSql)
        sql += " GENERATED BY DEFAULT AS IDENTITY";
    if (!column.codec.nullable)
        sql += " NOT NULL";
    if (column.generated && profile == Profile::Sqlite)
        sql += " PRIMARY KEY AUTOINCREMENT";
    return sql;
}

std::string draft_create(const Model& model, Profile profile)
{
    std::string sql = "CREATE TABLE " + draft_table(model, profile) + " (";
    for (std::size_t index = 0; index < model.projection().size(); ++index)
    {
        if (index)
            sql += ", ";
        sql += draft_column(model, model.column(model.projection()[index]), profile, true);
    }
    const auto keys = model.keys();
    const bool inline_key = profile == Profile::Sqlite && keys.size() == 1 && keys.front()->generated;
    if (!keys.empty() && !inline_key)
    {
        sql += ", PRIMARY KEY (";
        for (std::size_t index = 0; index < keys.size(); ++index)
        {
            if (index)
                sql += ", ";
            sql += draft_identifier(keys[index]->name, profile);
        }
        sql += ')';
    }
    return sql + ");";
}

std::string draft_name(std::string value, Profile profile)
{
    if (profile == Profile::Sqlite)
        for (auto& ch : value)
            if (ch >= 'A' && ch <= 'Z')
                ch = static_cast<char>(ch + ('a' - 'A'));
    return value;
}

using DraftModels = std::map<std::pair<std::string, std::string>, const Model*>;
DraftModels draft_models(const std::vector<Model>& models, Profile profile)
{
    DraftModels result;
    for (const auto& model : models)
    {
        (void)draft_table(model, profile);
        const auto schema = profile == Profile::Sqlite ? "main" : model.schema().value_or("");
        if (!result.emplace(std::pair{schema, draft_name(model.table(), profile)}, &model).second)
            throw Error::invalid("schema draft contains duplicate provider table names");
    }
    return result;
}
} // namespace

Bytes draft_schema(const AdministrationRequest& request, const AdministrationLimits& limits, const CallContext& context)
try
{
    validate_limits(limits);
    if (request.action != 4)
        throw Error::invalid("not a schema draft request");
    context.check();
    const auto before = draft_models(request.previous_models, request.profile);
    const auto after = draft_models(request.current_models, request.profile);
    Budget budget(limits);
    std::vector<Bytes> changes;
    const auto append = [&](std::uint32_t kind, const Model& model, const Column* column, const std::string& sql)
    {
        context.check();
        budget.row(180);
        budget.text(model.table());
        budget.text(sql);
        if (model.schema())
            budget.text(*model.schema());
        if (column)
            budget.text(column->name);
        std::vector<ControlField> fields{{2, 1, u32(kind)}};
        if (model.schema())
            fields.push_back({3, 4, bytes(*model.schema())});
        fields.push_back({4, 4, bytes(model.table())});
        if (column)
            fields.push_back({5, 4, bytes(column->name)});
        fields.push_back({6, 4, bytes(sql)});
        fields.push_back({7, 1, u32(kind >= 2)});
        // Review approval belongs to this versioned provider operation, even
        // when two dialects happen to render identical SQL for a DROP.
        auto identity = bytes("NWDB-SCHEMA-DRAFT-CHANGE-v1");
        const auto provider = u32(request.profile == Profile::PostgreSql ? 0U : 1U);
        identity.insert(identity.end(), provider.begin(), provider.end());
        const auto operation = encode_control(21, fields);
        identity.insert(identity.end(), operation.begin(), operation.end());
        const auto digest = sha256(identity);
        const auto id = hex(digest);
        budget.text(id);
        fields.insert(fields.begin(), {1, 4, bytes(id)});
        changes.push_back(encode_control(21, fields));
    };
    for (const auto& [key, model] : after)
    {
        context.check();
        const auto old = before.find(key);
        if (old == before.end())
        {
            append(0, *model, nullptr, draft_create(*model, request.profile));
            continue;
        }
        const auto& previous = *old->second;
        if (model->table() != previous.table() || model->schema() != previous.schema())
            unsupported_draft(*model, nullptr, "infer a table/schema rename");
        std::map<std::string, const Column*> previous_columns;
        std::map<std::string, const Column*> current_columns;
        for (const auto& column : previous.columns())
            previous_columns.emplace(draft_name(column.name, request.profile), &column);
        for (const auto& column : model->columns())
            current_columns.emplace(draft_name(column.name, request.profile), &column);
        for (const auto id : model->projection())
        {
            const auto& column = model->column(id);
            const auto prior = previous_columns.find(draft_name(column.name, request.profile));
            if (prior == previous_columns.end())
            {
                if (!column.codec.nullable || column.key_ordinal || column.generated)
                    unsupported_draft(*model, &column,
                                      "add a required/key/generated column without an explicit backfill");
                append(1, *model, &column,
                       "ALTER TABLE " + draft_table(*model, request.profile) + " ADD COLUMN " +
                           draft_column(*model, column, request.profile, false) + ';');
                continue;
            }
            const auto& original = *prior->second;
            if (column.name != original.name || column.codec != original.codec ||
                column.key_ordinal != original.key_ordinal || column.generated != original.generated ||
                column.collation != original.collation || column.max_length != original.max_length)
                unsupported_draft(*model, &column, "alter a column, key, type, nullability or storage policy");
        }
        for (const auto id : previous.projection())
        {
            const auto& column = previous.column(id);
            if (current_columns.contains(draft_name(column.name, request.profile)))
                continue;
            if (column.key_ordinal)
                unsupported_draft(previous, &column, "drop a primary-key column");
            append(2, previous, &column,
                   "ALTER TABLE " + draft_table(previous, request.profile) + " DROP COLUMN " +
                       draft_identifier(column.name, request.profile) + ';');
        }
    }
    for (const auto& [key, model] : before)
        if (!after.contains(key))
            append(3, *model, nullptr, "DROP TABLE " + draft_table(*model, request.profile) + ';');
    auto result = encode_control(19, {{1, 1, u32(4)}, {2, 5, encode_sequence(changes)}});
    if (result.size() > std::min(limits.bytes, MAX_BYTES))
        capacity_error();
    return result;
}
catch (const Error& error)
{
    // Draft validation has no provider effects. Return its bounded reason as an
    // action-specific result so the managed caller retains the exact offending
    // table/column after releasing the result buffer. Other native failures keep
    // their ordinary status, cancellation and resource-limit behavior.
    if (error.status != NW_INVALID_ARGUMENT)
        throw;
    Budget budget(limits);
    budget.text(error.message);
    auto result = encode_control(19, {{1, 1, u32(4)}, {6, 4, bytes(error.message)}});
    if (result.size() > std::min(limits.bytes, MAX_BYTES))
        capacity_error();
    return result;
}

Bytes administer(const AdministrationRequest& request, ProviderConnection& connection,
                 const AdministrationLimits& limits, const CallContext& context)
{
    validate_limits(limits);
    if (request.profile != connection.profile())
        throw Error::invalid("administration provider differs from connection");
    context.check();
    auto active = context;
    Bytes result;
    if (request.profile == Profile::PostgreSql)
    {
        execute(
            connection, "SELECT set_config('statement_timeout',$1,false), set_config('lock_timeout',$2,false)",
            {text_parameter(std::to_string(std::max<std::uint64_t>(1, limits.command_timeout_ns / 1'000'000)) + "ms"),
             text_parameter(std::to_string(std::max<std::uint64_t>(1, request.lock_timeout_ns / 1'000'000)) + "ms")},
            active);
        active.check();
        if (request.action == 3)
            result = catalog(connection, request, limits, active);
        else
        {
            const auto database = text(one(connection, "SELECT current_database()::text", {}, {codec(5)}, active), 0);
            const auto lock_id = static_cast<std::int64_t>(0x3d32ad9e) * crc32(database);
            execute(connection, "SELECT pg_advisory_lock($1)", {long_parameter(lock_id)}, active);
            active.check();
            result = execute_locked(connection, request, limits, active);
        }
    }
    else
    {
        active.deadline = std::min(active.deadline, Clock::now() + std::chrono::nanoseconds(limits.command_timeout_ns +
                                                                                            request.lock_timeout_ns));
        if (request.action == 3)
            result = catalog(connection, request, limits, active);
        else
        {
            active.check();
            execute(connection, "BEGIN IMMEDIATE", {}, active);
            try
            {
                result = execute_locked(connection, request, limits, active);
                // Cancellation no longer owns the worker once application work
                // completed. Commit/rollback establish a definite local outcome.
                execute(connection, "COMMIT", {}, cleanup_context());
            }
            catch (Error& error)
            {
                rollback(connection, error);
                throw;
            }
        }
    }
    if (result.size() > std::min(limits.bytes, MAX_BYTES))
        capacity_error();
    return result;
}

} // namespace nw
