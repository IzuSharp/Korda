#include "nw/administration.hpp"

#include <algorithm>
#include <chrono>
#include <filesystem>
#include <future>
#include <iostream>
#include <stdexcept>
#include <thread>

namespace
{
using namespace nw;

std::size_t checks = 0;
void require(bool value, const char* message)
{
    ++checks;
    if (!value)
        throw std::runtime_error(message);
}

template <typename F> Error rejects(std::uint32_t status, std::uint32_t code, F&& action)
{
    try
    {
        action();
    }
    catch (const Error& error)
    {
        require(error.status == status && error.code == code, "administration error category");
        return error;
    }
    throw std::runtime_error("invalid administration accepted");
}

Bytes text(std::string_view text)
{
    return Bytes(text.begin(), text.end());
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

Bytes payload(Profile profile, const std::vector<std::string>& scripts, std::optional<std::uint64_t> target = {},
              bool non_transactional = false, bool allow = false,
              std::optional<std::size_t> non_transactional_index = {})
{
    const auto provider = profile == Profile::PostgreSql ? 0U : 1U;
    Bytes sequence = u32(static_cast<std::uint32_t>(scripts.size()));
    for (std::size_t i = 0; i < scripts.size(); ++i)
    {
        const auto hash = sha256(text(scripts[i]));
        const auto entry = encode_control(11, {{1, 2, u64(i + 1)},
                                               {2, 4, text("migration " + std::to_string(i + 1))},
                                               {3, 4, text(scripts[i])},
                                               {4, 1, u32(non_transactional || non_transactional_index == i)},
                                               {5, 5, Bytes(hash.begin(), hash.end())}});
        const auto length = u32(static_cast<std::uint32_t>(entry.size()));
        sequence.insert(sequence.end(), length.begin(), length.end());
        sequence.insert(sequence.end(), entry.begin(), entry.end());
    }
    const auto canonical = encode_control(12, {{1, 1, u32(provider)}, {2, 5, sequence}});
    const auto hash = sha256(canonical);
    const auto bundle =
        encode_control(12, {{1, 1, u32(provider)}, {2, 5, sequence}, {3, 5, Bytes(hash.begin(), hash.end())}});
    std::vector<ControlField> fields{{1, 1, u32(provider)}, {2, 5, bundle}};
    if (target)
        fields.push_back({3, 2, u64(*target)});
    fields.push_back({4, 3, u64(2'000'000'000)});
    if (allow)
        fields.push_back({5, 1, u32(1)});
    return encode_control(5, fields);
}

AdministrationRequest request(Profile profile, std::uint32_t action, const std::vector<std::string>& scripts = {},
                              std::optional<std::uint64_t> target = {})
{
    return parse_administration(profile, action,
                                action == 3 ? encode_control(5, {{1, 1, u32(profile == Profile::PostgreSql ? 0 : 1)}})
                                            : payload(profile, scripts, target),
                                {});
}

Bytes run(ProviderOptions options, const AdministrationRequest& request, const AdministrationLimits& limits = {},
          std::shared_ptr<Cancel> cancel = {})
{
    options.sqlite_busy_timeout_ns = request.lock_timeout_ns;
    CallContext context{cancel, Clock::time_point::max(), true};
    auto connection = ProviderConnection::open(options, context);
    try
    {
        auto result = administer(request, *connection, limits, context);
        connection->close();
        return result;
    }
    catch (...)
    {
        connection->close();
        throw;
    }
}

std::uint32_t applied(const Bytes& output)
{
    return Fields::parse(output, 19, {{1, 1, true}, {3, 1, true}, {4, 2, false}, {5, 1, true}}).u32(3);
}

std::int64_t scalar(ProviderOptions options, std::string_view sql)
{
    auto connection = ProviderConnection::open(options, {});
    const auto rows = connection->query_text(sql, {}, 1);
    connection->close();
    require(rows.size() == 1 && rows[0].size() == 1 && rows[0][0].has_value(), "independent SQL row");
    return std::stoll(*rows[0][0]);
}

void verify_catalog(const Bytes& output)
{
    const auto envelope = Fields::parse(output, 19, {{1, 1, true}, {2, 5, true}});
    require(envelope.u32(1) == 3, "catalog kind");
    std::size_t found = 0;
    for (const auto table : sequence(envelope.bytes(2)))
    {
        const auto fields = Fields::parse(table, 18, {{1, 4, false}, {2, 4, true}, {3, 5, true}});
        const auto name = fields.text(2);
        if (name != "single_key" && name != "composite_key" && name != "descending_key")
            continue;
        ++found;
        for (const auto column : sequence(fields.bytes(3)))
        {
            const auto record = Fields::parse(column, 17, {{1, 4, true}, {2, 4, true}, {3, 1, true}, {4, 1, true}});
            require(record.boolean(4), "catalog primary key");
            require(record.boolean(3) == (name != "single_key"), "SQLite actual PK nullable semantics");
        }
    }
    require(found == 3, "all catalog tables");
}

} // namespace

int main(int argc, char** argv)
{
    const auto root = std::filesystem::temp_directory_path() /
                      ("nw-cpp-administration-" + std::to_string(Clock::now().time_since_epoch().count()));
    try
    {
        std::filesystem::create_directories(root);
        for (const auto* sql :
             {"BEGIN; SELECT 1", "CREATE TABLE t(a); COMMIT;", "-- x\nSAVEPOINT bad",
              "/* nested /* comment */ */ ROLLBACK", "SELECT 'unterminated", "/* unterminated", "DO $$unterminated"})
            (void)rejects(NW_INVALID_ARGUMENT, 0, [&] { (void)request(Profile::Sqlite, 0, {sql}); });
        const auto dollar = request(Profile::PostgreSql, 0, {"DO $$ BEGIN RAISE NOTICE 'COMMIT'; END $$;"});
        require(dollar.bundle->migrations[0].checksum.size() == 48, "SQLx SHA384 checksum width");
        auto corrupt = payload(Profile::Sqlite, {"SELECT 1"});
        corrupt.back() ^= 1;
        (void)rejects(NW_INVALID_ARGUMENT, 0, [&] { (void)parse_administration(Profile::Sqlite, 0, corrupt, {}); });
        (void)rejects(
            NW_INVALID_ARGUMENT, 0,
            [&] { (void)parse_administration(Profile::PostgreSql, 0, payload(Profile::Sqlite, {"SELECT 1"}), {}); });
        (void)rejects(NW_NOT_SUPPORTED, 5,
                      [&]
                      {
                          (void)parse_administration(Profile::Sqlite, 0,
                                                     payload(Profile::Sqlite, {"SELECT 1"}, {}, true, true), {});
                      });
        (void)rejects(NW_NOT_SUPPORTED, 5,
                      [&]
                      {
                          (void)parse_administration(Profile::PostgreSql, 0,
                                                     payload(Profile::PostgreSql, {"SELECT 1"}, {}, true), {});
                      });
        const auto allowed = parse_administration(Profile::PostgreSql, 0,
                                                  payload(Profile::PostgreSql, {"SELECT 1"}, {}, true, true), {});
        require(allowed.allow_non_transactional, "explicit nontransactional admission");
        auto tiny = AdministrationLimits{};
        tiny.rows = 1;
        (void)rejects(NW_DOMAIN_ERROR, 6,
                      [&]
                      {
                          (void)parse_administration(Profile::Sqlite, 0,
                                                     payload(Profile::Sqlite, {"SELECT 1", "SELECT 2"}), tiny);
                      });
        (void)rejects(NW_INVALID_ARGUMENT, 0, [&] { (void)request(Profile::Sqlite, 1, {"SELECT 1"}, 1); });

        ProviderOptions options;
        options.profile = Profile::Sqlite;
        options.connection = (root / "history.db").string();
        const std::vector<std::string> scripts{
            "CREATE TABLE contended(id INTEGER PRIMARY KEY, name TEXT NOT NULL); CREATE TABLE audit(value TEXT);",
            "INSERT INTO contended VALUES(1,'one'); INSERT INTO audit VALUES('one;two')"};
        const auto manifest = request(Profile::Sqlite, 0, scripts);
        {
            auto connection = ProviderConnection::open(options, {});
            connection->close();
        }
        auto first = std::async(std::launch::async, [&] { return run(options, manifest); });
        auto second = std::async(std::launch::async, [&] { return run(options, manifest); });
        require(applied(first.get()) + applied(second.get()) == 2, "contended migrations exactly once");
        require(applied(run(options, manifest)) == 0, "idempotency");
        require(scalar(options, "SELECT count(*) FROM contended") == 1, "committed application data");
        require(scalar(options, "SELECT count(*) FROM audit WHERE value='one;two'") == 1,
                "script preserved quoted semicolons");
        require(scalar(options, "SELECT count(*) FROM _sqlx_migrations WHERE success=1 AND length(checksum)=48") == 2,
                "SQLx ledger format");
        const auto status = run(options, request(Profile::Sqlite, 1, scripts));
        const auto status_fields = Fields::parse(status, 19, {{1, 1, true}, {2, 5, true}});
        require(status_fields.u32(1) == 1, "status kind");
        const auto entries = sequence(status_fields.bytes(2));
        require(entries.size() == 2, "status rows");
        for (const auto entry : entries)
        {
            const auto fields = Fields::parse(entry, 16, {{1, 2, true}, {2, 4, true}, {3, 4, true}, {4, 1, true}});
            require(fields.boolean(4) && fields.text(3).size() == 96, "status SHA384 and application");
        }
        const auto validation = run(options, request(Profile::Sqlite, 2, scripts));
        require(Fields::parse(validation, 19, {{1, 1, true}}).u32(1) == 2, "history validation");
        (void)rejects(
            NW_DOMAIN_ERROR, 15, [&]
            { (void)run(options, request(Profile::Sqlite, 0, {"CREATE TABLE different(id INTEGER)", scripts[1]})); });
        (void)rejects(NW_INVALID_ARGUMENT, 0, [&] { (void)run(options, request(Profile::Sqlite, 0, scripts, 1)); });
        tiny.rows = 2;
        (void)rejects(NW_DOMAIN_ERROR, 6, [&] { (void)run(options, request(Profile::Sqlite, 3), tiny); });

        auto broken = options;
        broken.connection = (root / "atomic.db").string();
        (void)rejects(NW_DOMAIN_ERROR, 1,
                      [&]
                      {
                          (void)run(broken, request(Profile::Sqlite, 0,
                                                    {"CREATE TABLE first_entry(id INTEGER PRIMARY KEY)",
                                                     "INSERT INTO absent_table VALUES(1)"}));
                      });
        require(scalar(broken, "SELECT count(*) FROM sqlite_schema WHERE name IN "
                               "('first_entry','_sqlx_migrations','_nativeweave_migration_phases')") == 0,
                "whole SQLite batch and ledger rolled back");

        auto catalog_options = options;
        catalog_options.connection = (root / "catalog.db").string();
        auto catalog_connection = ProviderConnection::open(catalog_options, {});
        catalog_connection->execute_script(
            "CREATE TABLE single_key(id INTEGER PRIMARY KEY); CREATE TABLE composite_key(a INTEGER,b INTEGER,PRIMARY "
            "KEY(a,b)); CREATE TABLE descending_key(id INTEGER PRIMARY KEY DESC)",
            {});
        catalog_connection->close();
        verify_catalog(run(catalog_options, request(Profile::Sqlite, 3)));

        auto canceled_options = options;
        canceled_options.connection = (root / "canceled.db").string();
        const auto long_request = request(Profile::Sqlite, 0,
                                          {"CREATE TABLE canceled AS WITH RECURSIVE numbers(n) AS (VALUES(1) UNION ALL "
                                           "SELECT n+1 FROM numbers WHERE n<100000000) SELECT n FROM numbers"});
        const auto token = std::make_shared<Cancel>();
        auto work = std::async(std::launch::async, [&] { return run(canceled_options, long_request, {}, token); });
        std::this_thread::sleep_for(std::chrono::milliseconds(30));
        token->request();
        const auto canceled = rejects(NW_CANCELED, 8, [&] { (void)work.get(); });
        require(canceled.cleanup != nullptr, "SQLite auto-abort retains tracked rollback failure");
        require(scalar(canceled_options, "SELECT count(*) FROM sqlite_schema WHERE name='canceled'") == 0,
                "canceled DDL never committed");
        if (argc > 1)
        {
            ProviderOptions pg;
            pg.profile = Profile::PostgreSql;
            pg.connection = argv[1];
            pg.verify_tls = false;
            const std::vector<std::string> pg_scripts{"CREATE TABLE seed(id BIGINT PRIMARY KEY)",
                                                      "INSERT INTO seed VALUES(1),(2)"};
            const auto pg_manifest = request(Profile::PostgreSql, 0, pg_scripts);
            auto pg_first = std::async(std::launch::async, [&] { return run(pg, pg_manifest); });
            auto pg_second = std::async(std::launch::async, [&] { return run(pg, pg_manifest); });
            require(applied(pg_first.get()) + applied(pg_second.get()) == 2,
                    "PostgreSQL advisory lock and concurrent idempotency");
            require(applied(run(pg, pg_manifest)) == 0, "PostgreSQL idempotency");
            require(scalar(pg, "SELECT count(*) FROM _sqlx_migrations WHERE success AND length(checksum)=48") == 2,
                    "PostgreSQL SQLx history");
            auto pg_broken = pg_scripts;
            pg_broken.push_back("CREATE TABLE rolled_back(id INT); INSERT INTO absent_table VALUES(1)");
            (void)rejects(NW_DOMAIN_ERROR, 1, [&] { (void)run(pg, request(Profile::PostgreSql, 0, pg_broken)); });
            require(scalar(pg, "SELECT count(*) FROM pg_class WHERE relname='rolled_back'") == 0,
                    "PostgreSQL failed entry rolls back");
            require(scalar(pg, "SELECT count(*) FROM seed") == 2, "PostgreSQL prior entry stays committed");
            auto pg_partial_scripts = pg_scripts;
            pg_partial_scripts.push_back("CREATE INDEX CONCURRENTLY partial_index ON seed ((1/(id-1)))");
            const auto pg_partial = parse_administration(
                Profile::PostgreSql, 0, payload(Profile::PostgreSql, pg_partial_scripts, {}, false, true, 2), {});
            const auto pg_partial_error = rejects(NW_DOMAIN_ERROR, 1, [&] { (void)run(pg, pg_partial); });
            require(pg_partial_error.provider == "22012", "PostgreSQL partial index SQLSTATE");
            require(
                scalar(pg, "SELECT count(*) FROM _nativeweave_migration_phases WHERE version=3 AND phase='applying'") ==
                    1,
                "nontransactional intent persisted");
            require(
                scalar(pg,
                       "SELECT count(*) FROM pg_index WHERE indexrelid='partial_index'::regclass AND NOT indisvalid") ==
                    1,
                "PostgreSQL unfinished concurrent index remains visible");
            auto pg_validate = pg_partial;
            pg_validate.action = 2;
            pg_validate.allow_non_transactional = false;
            (void)rejects(NW_DOMAIN_ERROR, 8, [&] { (void)run(pg, pg_validate); });
            auto pg_status = pg_validate;
            pg_status.action = 1;
            const auto partial_status_bytes = run(pg, pg_status);
            const auto partial_status = Fields::parse(partial_status_bytes, 19, {{1, 1, true}, {2, 5, true}});
            const auto partial_entries = sequence(partial_status.bytes(2));
            require(partial_entries.size() == 3, "partial status retains all entries");
            require(!Fields::parse(partial_entries.back(), 16, {{1, 2, true}, {2, 4, true}, {3, 4, true}, {4, 1, true}})
                         .boolean(4),
                    "unfinished entry is not reported applied");
            auto observer = ProviderConnection::open(pg, {});
            observer->execute_script(
                "DROP INDEX partial_index; DELETE FROM seed WHERE id=1; CREATE TABLE empty_catalog ()", {});
            observer->close();
            require(applied(run(pg, pg_partial)) == 1, "identical explicit nontransactional replay");
            require(applied(run(pg, pg_partial)) == 0, "recovered nontransactional idempotency");
            pg_validate = pg_partial;
            pg_validate.action = 2;
            pg_validate.allow_non_transactional = false;
            (void)run(pg, pg_validate);
            const auto pg_catalog_bytes = run(pg, request(Profile::PostgreSql, 3));
            const auto pg_catalog = Fields::parse(pg_catalog_bytes, 19, {{1, 1, true}, {2, 5, true}});
            bool empty_found = false;
            for (const auto item : sequence(pg_catalog.bytes(2)))
            {
                const auto table = Fields::parse(item, 18, {{1, 4, false}, {2, 4, true}, {3, 5, true}});
                if (table.text(2) == "empty_catalog")
                    empty_found = sequence(table.bytes(3)).empty();
            }
            require(empty_found, "PostgreSQL zero-column catalog table");
        }
        std::filesystem::remove_all(root);
        std::cout << "{\"status\":\"passed\",\"checks\":" << checks
                  << ",\"sqlite_atomicity\":true,\"concurrent_idempotency\":true,\"sqlx_history_sha384\":true,"
                     "\"catalog_pk_nullability\":true,\"cancellation_cleanup\":true,\"postgres_verified\":"
                  << (argc > 1 ? "true" : "false") << "}\n";
    }
    catch (const std::exception& error)
    {
        std::cerr << "FAIL " << error.what() << "\nevidence directory: " << root << '\n';
        return 1;
    }
}
