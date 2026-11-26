#include "provider_test_support.hpp"

#include <atomic>
#include <bit>
#include <cstdlib>
#include <filesystem>
#include <future>
#include <iostream>
#include <stdexcept>
#include <thread>

namespace
{
using namespace nw;
using test::Row;

std::size_t checks = 0;
std::size_t failures = 0;
void require(bool value, const char* message)
{
    ++checks;
    if (!value)
        throw std::runtime_error(message);
}

template <typename F> Error rejects(F&& action)
{
    try
    {
        action();
    }
    catch (const Error& error)
    {
        ++checks;
        return error;
    }
    throw std::runtime_error("provider failure was not rejected");
}

template <typename F> void run_case(const char* name, F&& action)
{
    try
    {
        action();
        std::cout << "PASS " << name << std::endl;
    }
    catch (const std::exception& error)
    {
        ++failures;
        std::cerr << "FAIL " << name << ": " << error.what() << std::endl;
    }
}

Codec codec(std::uint32_t id, bool nullable = false)
{
    return {id, std::nullopt, nullable};
}

std::int64_t scalar(ProviderConnection& connection, std::string_view sql)
{
    const auto rows = connection.query_text(sql, {}, 1);
    require(rows.size() == 1 && rows[0].size() == 1 && rows[0][0], "scalar result shape");
    return std::stoll(*rows[0][0]);
}

std::string scalar_text(ProviderConnection& connection, std::string_view sql)
{
    const auto rows = connection.query_text(sql, {}, 1);
    require(rows.size() == 1 && rows[0].size() == 1 && rows[0][0], "text scalar result shape");
    return *rows[0][0];
}

void execute(ProviderConnection& connection, std::string_view sql)
{
    (void)connection.execute(sql, {}, {});
}

std::int64_t value(const Row& row)
{
    require(row.size() == 1 && row[0] && row[0]->size() == 8, "stream scalar shape");
    return std::bit_cast<std::int64_t>(read_u64(*row[0], 0));
}

ProviderOptions sqlite_options(const std::filesystem::path& root, const char* name)
{
    ProviderOptions options;
    options.profile = Profile::Sqlite;
    options.connection = (root / name).string();
    options.sqlite_busy_timeout_ns = 80'000'000;
    return options;
}

void sqlite_profile(const std::filesystem::path& root)
{
    const auto options = sqlite_options(root, "profile.db");
    auto connection = ProviderConnection::open(options, {});
    require(scalar_text(*connection, "SELECT sqlite_version()") == "3.51.3", "selected SQLite version");
    require(scalar(*connection, "PRAGMA foreign_keys") == 1, "SQLite foreign keys enabled");
    require(scalar_text(*connection, "PRAGMA journal_mode") == "wal", "SQLite WAL mode");
    require(scalar(*connection, "PRAGMA synchronous") == 2, "SQLite FULL durability");
    require(scalar(*connection, "SELECT sqlite_compileoption_used('THREADSAFE=1')") == 1, "SQLite threadsafe build");
    require(scalar(*connection, "PRAGMA busy_timeout") == 80, "SQLite configured busy_timeout remains observable");
    connection->close();
    auto readonly = options;
    readonly.sqlite_mode = 2;
    auto reader = ProviderConnection::open(readonly, {});
    const auto error = rejects([&] { execute(*reader, "CREATE TABLE forbidden(id INTEGER)"); });
    require(error.provider && std::stoi(*error.provider) % 256 == 8, "SQLite READONLY provider code");
    reader->close();
    auto missing = options;
    missing.connection = (root / "missing.db").string();
    missing.sqlite_mode = 1;
    (void)rejects([&] { (void)ProviderConnection::open(missing, {}); });
}

void sqlite_lock_modes(const std::filesystem::path& root)
{
    const auto options = sqlite_options(root, "locks.db");
    auto first = ProviderConnection::open(options, {});
    execute(*first, "CREATE TABLE locks(id INTEGER PRIMARY KEY)");
    auto second = ProviderConnection::open(options, {});
    execute(*first, "BEGIN IMMEDIATE");
    const auto began = Clock::now();
    const auto blocked = rejects([&] { execute(*second, "BEGIN IMMEDIATE"); });
    const auto elapsed = Clock::now() - began;
    require(blocked.provider && std::stoi(*blocked.provider) % 256 == 5, "SQLite BUSY after bounded lock wait");
    require(elapsed >= std::chrono::milliseconds(50) && elapsed < std::chrono::seconds(1), "SQLite busy timeout bound");
    require(!second->in_transaction(), "failed BEGIN leaves no transaction");
    execute(*second, "BEGIN DEFERRED");
    require(second->in_transaction(), "DEFERRED begins without writer reservation");
    (void)rejects([&] { execute(*second, "INSERT INTO locks VALUES(2)"); });
    execute(*second, "ROLLBACK");
    execute(*first, "ROLLBACK");
    execute(*second, "BEGIN IMMEDIATE");
    execute(*second, "INSERT INTO locks VALUES(3)");
    execute(*second, "COMMIT");
    require(scalar(*first, "SELECT sum(id) FROM locks") == 3, "committed write observed independently");
    first->close();
    second->close();
}

void sqlite_savepoints(const std::filesystem::path& root)
{
    auto connection = ProviderConnection::open(sqlite_options(root, "savepoint.db"), {});
    connection->execute_script(
        "CREATE TABLE parent(id INTEGER PRIMARY KEY); CREATE TABLE child(id INTEGER REFERENCES parent(id))", {});
    execute(*connection, "BEGIN");
    execute(*connection, "SAVEPOINT retained");
    const auto constraint = rejects([&] { execute(*connection, "INSERT INTO child VALUES(9)"); });
    require(constraint.code == 2 && constraint.provider && std::stoi(*constraint.provider) % 256 == 19,
            "SQLite constraint category");
    execute(*connection, "ROLLBACK TO SAVEPOINT retained");
    for (int i = 0; i < 3; ++i)
    {
        execute(*connection, "INSERT INTO parent VALUES(" + std::to_string(i) + ")");
        execute(*connection, "ROLLBACK TO SAVEPOINT retained");
    }
    execute(*connection, "INSERT INTO parent VALUES(1)");
    execute(*connection, "INSERT INTO child VALUES(1)");
    execute(*connection, "RELEASE SAVEPOINT retained");
    (void)rejects([&] { execute(*connection, "ROLLBACK TO SAVEPOINT retained"); });
    execute(*connection, "COMMIT");
    require(!connection->in_transaction() && scalar(*connection, "SELECT count(*) FROM parent") == 1,
            "savepoint rollback retains outer transaction");
    execute(*connection, "PRAGMA query_only=ON");
    execute(*connection, "BEGIN");
    (void)rejects([&] { execute(*connection, "INSERT INTO parent VALUES(2)"); });
    execute(*connection, "ROLLBACK");
    execute(*connection, "PRAGMA query_only=OFF");
    execute(*connection, "INSERT INTO parent VALUES(2)");
    require(scalar(*connection, "SELECT count(*) FROM parent") == 2, "read-only state reset permits next owner write");
    connection->close();
}

void sqlite_cancellation(const std::filesystem::path& root)
{
    auto connection = ProviderConnection::open(sqlite_options(root, "cancel.db"), {});
    const auto identity = connection->identity();
    const auto old = std::make_shared<Cancel>();
    const auto work = [&]
    {
        return connection->execute(
            "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<100000000) SELECT sum(x) FROM n", {},
            {old});
    };
    auto pending = std::async(std::launch::async, work);
    std::this_thread::sleep_for(std::chrono::milliseconds(20));
    old->request();
    const auto interrupted = rejects([&] { (void)pending.get(); });
    require(interrupted.status == NW_CANCELED, "SQLite cancellation category");
    old->request();
    require(
        scalar(*connection,
               "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<5000) SELECT sum(x) FROM n") ==
            12502500,
        "stale cancellation does not affect next command");
    require(connection->healthy() && connection->identity() == identity, "canceled SQLite connection safely reusable");
    (void)rejects([&] { (void)connection->query("SELECT 1", {}, {old}); });
    const auto timeout = rejects(
        [&]
        {
            (void)connection->execute(
                "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<100000000) SELECT sum(x) FROM n",
                {}, {{}, Clock::now() + std::chrono::milliseconds(15), true});
        });
    require(timeout.status == NW_TIMEOUT, "SQLite command deadline category");
    require(scalar(*connection, "SELECT 42") == 42, "timed-out SQLite command retired");
    connection->close();
}

void sqlite_stream(const std::filesystem::path& root)
{
    auto connection = ProviderConnection::open(sqlite_options(root, "stream.db"), {});
    const auto began = Clock::now();
    auto statement = connection->query(
        "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<100000000) SELECT x FROM n", {}, {});
    for (std::int64_t expected = 1; expected <= 3; ++expected)
    {
        const auto row = test::next_row(*statement, Profile::Sqlite, {codec(3)}, MAX_BYTES, {});
        require(row && value(*row) == expected, "SQLite streaming prefix order");
    }
    require(Clock::now() - began < std::chrono::seconds(1), "SQLite produces rows without full materialization");
    const auto concurrent = rejects([&] { (void)connection->query("SELECT 1", {}, {}); });
    require(concurrent.code == 7, "one command per physical connection");
    statement->close();
    require(scalar(*connection, "SELECT 44") == 44, "partial SQLite stream finalized before reuse");
    auto empty = connection->query("SELECT 1 AS id WHERE 0", {}, {});
    require(empty->columns().size() == 1 && empty->columns()[0].name == "id", "empty SQLite statement metadata");
    require(!test::next_row(*empty, Profile::Sqlite, {codec(3)}, 8, {}), "empty SQLite stream");
    empty->close();
    auto large = connection->query("SELECT zeroblob(1024)", {}, {});
    const auto too_large = rejects([&] { (void)test::next_row(*large, Profile::Sqlite, {codec(6)}, 512, {}); });
    require(too_large.code == 14, "field budget preserves LargeValueRequiresStreaming");
    large->close();
    auto wrong = connection->query("SELECT '42'", {}, {});
    require(rejects([&] { (void)test::next_row(*wrong, Profile::Sqlite, {codec(3)}, 512, {}); }).code == 4,
            "SQLite runtime type refuses coercion");
    wrong->close();
    auto null = connection->query("SELECT NULL", {}, {});
    require(rejects([&] { (void)test::next_row(*null, Profile::Sqlite, {codec(5)}, 512, {}); }).code == 3,
            "unexpected SQL NULL is conversion");
    null->close();
    connection->close();
}

void sqlite_prepare_then_bind(const std::filesystem::path& root)
{
    const auto options = sqlite_options(root, "prepare-bind.db");
    auto connection = ProviderConnection::open(options, {});
    auto observer = ProviderConnection::open(options, {});
    execute(*connection, "CREATE TABLE prepared_input(id INTEGER PRIMARY KEY)");
    auto statement = connection->prepare_unbound("INSERT INTO prepared_input VALUES(?1) RETURNING id", {});
    require(statement->columns().size() == 1 && statement->columns()[0].name == "id",
            "unbound SQLite statement provides metadata before parameter admission");
    require(scalar(*observer, "SELECT count(*) FROM prepared_input") == 0,
            "SQLite prepare has no mutation side effect");
    require(rejects([&] { (void)test::next_row(*statement, Profile::Sqlite, {codec(3)}, 8, {}); }).code == 8,
            "unbound SQLite statement cannot execute with implicit NULL parameters");
    (void)rejects([&] { statement->bind_parameters({}); });
    Bytes seven(8);
    put_u64(seven, 0, 7);
    statement->bind_parameters({{codec(3), seven}});
    require(scalar(*observer, "SELECT count(*) FROM prepared_input") == 0,
            "SQLite parameter binding has no mutation side effect");
    const auto row = test::next_row(*statement, Profile::Sqlite, {codec(3)}, 8, {});
    require(row && value(*row) == 7, "first SQLite fetch executes the admitted values");
    require(rejects([&] { statement->bind_parameters({{codec(3), seven}}); }).code == 8,
            "live SQLite statement cannot replace its parameters");
    statement->close();
    require(scalar(*observer, "SELECT count(*) FROM prepared_input") == 1,
            "SQLite statement retirement preserves exactly one committed mutation");
    const auto cancel = std::make_shared<Cancel>();
    auto canceled = connection->prepare_unbound("SELECT ?1 AS id", {cancel});
    cancel->request();
    require(rejects([&] { canceled->bind_parameters({{codec(3), seven}}); }).status == NW_CANCELED,
            "SQLite cancellation between preparation and binding prevents execution");
    canceled->close();
    require(scalar(*connection, "SELECT 42") == 42, "unbound/canceled SQLite statement releases its physical gate");
    observer->close();
    connection->close();
}

ProviderOptions postgres_options(const char* name = "NW_PG_URL")
{
    // Isolated fixture setup/reads; environment mutation never overlaps active provider work.
    // NOLINTNEXTLINE(concurrency-mt-unsafe)
    const auto* uri = std::getenv(name);
    if (!uri)
        throw std::runtime_error("PostgreSQL test endpoint missing");
    ProviderOptions result;
    result.profile = Profile::PostgreSql;
    result.connection = uri;
    // Isolated fixture setup/reads; environment mutation never overlaps active provider work.
    // NOLINTNEXTLINE(concurrency-mt-unsafe)
    if (const auto* root = std::getenv("NW_PG_ROOT"))
        result.root_certificate = root;
    result.application_name = "KordaCppProviderRegression";
    return result;
}

void postgres_tls()
{
    const auto options = postgres_options();
    auto connection = ProviderConnection::open(options, {});
    require(scalar(*connection, "SELECT count(*) FROM pg_stat_ssl WHERE pid=pg_backend_pid() AND ssl") == 1,
            "actual verified TLS connection");
    require(scalar_text(*connection, "SHOW application_name") == options.application_name, "explicit application name");
    connection->close();
    auto wrong_name = options;
    const auto host = wrong_name.connection.find("localhost");
    require(host != std::string::npos, "TLS test URI uses DNS name");
    wrong_name.connection.replace(host, 9, "127.0.0.1");
    (void)rejects([&] { (void)ProviderConnection::open(wrong_name, {}); });
    auto untrusted = options;
    // Isolated fixture setup/reads; environment mutation never overlaps active provider work.
    // NOLINTNEXTLINE(concurrency-mt-unsafe)
    untrusted.root_certificate = std::getenv("NW_PG_UNTRUSTED_ROOT");
    (void)rejects([&] { (void)ProviderConnection::open(untrusted, {}); });
    auto expired = postgres_options("NW_PG_EXPIRED_URL");
    (void)rejects([&] { (void)ProviderConnection::open(expired, {}); });
    auto plain = postgres_options("NW_PG_PLAIN_URL");
    (void)rejects([&] { (void)ProviderConnection::open(plain, {}); });
    plain.verify_tls = false;
    auto local = ProviderConnection::open(plain, {});
    require(scalar(*local, "SELECT count(*) FROM pg_stat_ssl WHERE pid=pg_backend_pid() AND ssl") == 0,
            "plaintext explicit opt-in");
    local->close();
}

void postgres_environment()
{
    const auto options = postgres_options();
    for (const auto* variable : {"PGHOSTADDR", "PGHOST", "PGPORT", "PGUSER", "PGDATABASE", "PGPASSWORD", "PGPASSFILE",
                                 "PGSSLMODE", "PGSSLROOTCERT", "PGSSLCERT", "PGSSLKEY", "PGAPPNAME", "PGOPTIONS"})
    {
#ifdef _WIN32
        _putenv_s(variable, "test-only-policy-value");
#else
        // Isolated fixture setup/reads; environment mutation never overlaps active provider work.
        // NOLINTNEXTLINE(concurrency-mt-unsafe)
        setenv(variable, "test-only-policy-value", 1);
#endif
        const auto error = rejects([&] { (void)ProviderConnection::open(options, {}); });
#ifdef _WIN32
        _putenv_s(variable, "");
#else
        // Isolated fixture setup/reads; environment mutation never overlaps active provider work.
        // NOLINTNEXTLINE(concurrency-mt-unsafe)
        unsetenv(variable);
#endif
        require(error.code == 8, "ambient PostgreSQL policy rejected");
    }
    for (const auto* suffix : {"?sslmode=disable", "#fragment"})
    {
        auto invalid = options;
        invalid.connection += suffix;
        require(rejects([&] { (void)ProviderConnection::open(invalid, {}); }).code == 8,
                "URI policy override rejected");
    }
}

void postgres_timeout_transaction()
{
    auto options = postgres_options();
    options.statement_timeout_ns = 50'000'000;
    auto connection = ProviderConnection::open(options, {});
    const auto pid = scalar(*connection, "SELECT pg_backend_pid()");
    execute(*connection, "CREATE TEMP TABLE tx_probe(id INT)");
    execute(*connection, "BEGIN");
    execute(*connection, "INSERT INTO tx_probe VALUES(1)");
    const auto timed_out = rejects([&] { execute(*connection, "SELECT pg_sleep(1)"); });
    require(timed_out.provider == "57014", "statement_timeout SQLSTATE preserved");
    const auto failed = rejects([&] { execute(*connection, "SELECT 1"); });
    require(failed.provider == "25P02", "failed PostgreSQL transaction remains failed");
    execute(*connection, "ROLLBACK");
    require(!connection->in_transaction() && scalar(*connection, "SELECT count(*) FROM tx_probe") == 0,
            "explicit rollback confirmed");
    require(scalar(*connection, "SELECT pg_backend_pid()") == pid, "timeout fully drained before same backend reuse");
    execute(*connection, "BEGIN");
    execute(*connection, "SAVEPOINT retained");
    (void)rejects([&] { execute(*connection, "SELECT 1/0"); });
    execute(*connection, "ROLLBACK TO SAVEPOINT retained");
    execute(*connection, "INSERT INTO tx_probe VALUES(2)");
    execute(*connection, "ROLLBACK TO SAVEPOINT retained");
    execute(*connection, "INSERT INTO tx_probe VALUES(3)");
    execute(*connection, "RELEASE SAVEPOINT retained");
    execute(*connection, "COMMIT");
    require(scalar(*connection, "SELECT sum(id) FROM tx_probe") == 3, "PG rollback-to retains tracked scope");
    connection->close();
}

void postgres_cancel_and_stream()
{
    auto connection = ProviderConnection::open(postgres_options(), {});
    const auto pid = scalar(*connection, "SELECT pg_backend_pid()");
    const auto token = std::make_shared<Cancel>();
    execute(*connection, "BEGIN");
    const auto began = Clock::now();
    auto pending =
        std::async(std::launch::async, [&] { return connection->execute("SELECT pg_sleep(0.25)", {}, {token}); });
    std::this_thread::sleep_for(std::chrono::milliseconds(30));
    token->request();
    require(rejects([&] { (void)pending.get(); }).status == NW_CANCELED, "PG caller cancellation category");
    require(Clock::now() - began >= std::chrono::milliseconds(150) && Clock::now() - began < std::chrono::seconds(2),
            "PG stop preserves original response drain without a cancel-key connection");
    require(connection->healthy() && scalar(*connection, "SELECT pg_backend_pid()") == pid,
            "PG cancellation drained before reuse");
    require(connection->in_transaction() && scalar(*connection, "SELECT 42::bigint") == 42,
            "caller stop does not abort the surrounding transaction");
    execute(*connection, "ROLLBACK");
    token->request();
    require(scalar(*connection, "SELECT 5000::bigint") == 5000, "stale PG token cannot cancel fresh owner");
    const auto streaming = Clock::now();
    auto rows = connection->query("SELECT n::bigint FROM generate_series(1,1000000) AS n", {}, {});
    for (std::int64_t expected = 1; expected <= 3; ++expected)
    {
        const auto row = test::next_row(*rows, Profile::PostgreSql, {codec(3)}, 8, {});
        require(row && value(*row) == expected, "PG single-row prefix order");
    }
    require(Clock::now() - streaming < std::chrono::seconds(2), "PG rows stream before full materialization");
    const auto closing = Clock::now();
    rows->close();
    require(Clock::now() - closing < std::chrono::seconds(6),
            "PG early exit observes its finite five-second cleanup budget");
    if (connection->healthy())
        require(scalar(*connection, "SELECT pg_backend_pid()") == pid,
                "drained partial stream reuses the same backend");
    else
    {
        connection = ProviderConnection::open(postgres_options(), {});
        require(scalar(*connection, "SELECT pg_backend_pid()") != pid,
                "undrained partial stream discards its physical backend");
    }
    auto empty = connection->query("SELECT 1::bigint AS exact_alias WHERE false", {}, {});
    require(empty->columns().size() == 1 && empty->columns()[0].name == "exact_alias" &&
                empty->columns()[0].type_name == "INT8",
            "empty PG result metadata");
    require(!test::next_row(*empty, Profile::PostgreSql, {codec(3)}, 8, {}), "empty PG stream EOF");
    empty->close();
    connection->close();
    auto short_budget = postgres_options();
    short_budget.cleanup_timeout_ns = 20'000'000;
    auto quarantined = ProviderConnection::open(short_budget, {});
    auto stop = std::make_shared<Cancel>();
    auto blocked =
        std::async(std::launch::async, [&] { return quarantined->execute("SELECT pg_sleep(1)", {}, {stop}); });
    std::this_thread::sleep_for(std::chrono::milliseconds(30));
    stop->request();
    require(rejects([&] { (void)blocked.get(); }).status == NW_CANCELED,
            "PG short cleanup retains caller cancellation");
    require(!quarantined->healthy(), "undrained response physically discarded after finite cleanup deadline");
    quarantined->close();

    auto late_failure = ProviderConnection::open(postgres_options(), {});
    execute(*late_failure, "BEGIN");
    auto unfinished = late_failure->query(
        "SELECT (CASE WHEN n=5 THEN 1/(n-5) ELSE n END)::bigint FROM generate_series(1,10) AS n", {}, {});
    auto first = test::next_row(*unfinished, Profile::PostgreSql, {codec(3)}, 8, {});
    require(first && value(*first) == 1, "late provider error follows a delivered row");
    unfinished->close();
    require(!late_failure->healthy(), "unread fatal response during drain discards the physical connection");
}

void postgres_preparation_and_schema()
{
    auto connection = ProviderConnection::open(postgres_options(), {});
    auto observer = ProviderConnection::open(postgres_options(), {});
    const auto suffix = std::to_string(scalar(*connection, "SELECT pg_backend_pid()"));
    const auto sequence = "nw_prepare_sequence_" + suffix;
    const auto table = "nw_prepare_schema_" + suffix;
    observer->execute_script("CREATE SEQUENCE " + sequence + "; CREATE TABLE " + table + " (id bigint); INSERT INTO " +
                                 table + " VALUES(7)",
                             {});
    const auto sql = "SELECT nextval('" + sequence + "') AS v";
    const auto dispatches = provider_dispatch_count();
    const auto submissions = provider_protocol_count();
    auto unopened = connection->query(sql, {}, {});
    require(provider_dispatch_count() == dispatches, "PostgreSQL preparation never dispatches Execute");
    require(provider_protocol_count() == submissions + 3,
            "preparation returns after Parse, Describe and their single confirming Sync");
    require(scalar(*observer, "SELECT is_called::int FROM " + sequence) == 0,
            "opening PostgreSQL cursor does not call volatile SQL");
    unopened->close(true);
    require(scalar(*observer, "SELECT is_called::int FROM " + sequence) == 0,
            "closing an unfetched PostgreSQL cursor does not execute it");
    auto executed = connection->query(sql, {}, {});
    const auto row = test::next_row(*executed, Profile::PostgreSql, {codec(3)}, 8, {});
    require(row && value(*row) == 1, "first PostgreSQL fetch executes the command exactly once");
    executed->close(true);
    require(scalar(*observer, "SELECT last_value FROM " + sequence) == 1,
            "PostgreSQL cleanup does not replay the executed command");

    const auto select = "SELECT id FROM " + table;
    auto original = connection->query(select, {}, {});
    require(original->columns().size() == 1 && original->columns()[0].type_name == "INT8",
            "fresh PostgreSQL description observes original database type");
    original->close();
    execute(*observer, "ALTER TABLE " + table + " ALTER COLUMN id TYPE text USING id::text");
    auto changed = connection->query(select, {}, {});
    require(changed->columns().size() == 1 && changed->columns()[0].type_name == "TEXT",
            "fresh PostgreSQL Parse observes external DDL instead of a cached-plan 0A000");
    const auto changed_row = test::next_row(*changed, Profile::PostgreSql, {codec(5)}, 8, {});
    require(changed_row && changed_row->size() == 1 && (*changed_row)[0] == Bytes{'7'},
            "post-DDL result uses the fresh description");
    changed->close();

    const auto before_error = scalar(*connection, "SELECT pg_backend_pid()");
    const auto error = rejects([&] { (void)connection->query("SELECT missing FROM absent_prepare_table", {}, {}); });
    require(error.provider == "42P01", "pipeline abort preserves the first Parse SQLSTATE");
    require(connection->healthy() && scalar(*connection, "SELECT pg_backend_pid()") == before_error,
            "failed metadata group is drained and leaves its physical connection reusable");
    observer->execute_script("DROP SEQUENCE " + sequence + "; DROP TABLE " + table, {});
    observer->close();
    connection->close();
}

void postgres_disconnect()
{
    auto victim = ProviderConnection::open(postgres_options(), {});
    auto observer = ProviderConnection::open(postgres_options(), {});
    const auto pid = scalar(*victim, "SELECT pg_backend_pid()");
    execute(*observer, "SELECT pg_terminate_backend(" + std::to_string(pid) + ")");
    (void)rejects([&] { execute(*victim, "SELECT 1"); });
    require(!victim->healthy(), "broken PostgreSQL physical connection is not reusable");
    victim->close();
    auto replacement = ProviderConnection::open(postgres_options(), {});
    require(scalar(*replacement, "SELECT pg_backend_pid()") != pid, "terminated backend replaced");
    replacement->close();
    observer->close();
}

} // namespace

int main()
{
    const auto root = std::filesystem::temp_directory_path() /
                      ("nw-provider-" + std::to_string(Clock::now().time_since_epoch().count()));
    std::filesystem::create_directories(root);
    run_case("sqlite_profile_durability_readonly", [&] { sqlite_profile(root); });
    run_case("sqlite_busy_and_transaction_modes", [&] { sqlite_lock_modes(root); });
    run_case("sqlite_savepoints_and_query_only", [&] { sqlite_savepoints(root); });
    run_case("sqlite_cancellation_deadline_and_stale_generation", [&] { sqlite_cancellation(root); });
    run_case("sqlite_streaming_early_exit_shape_and_field_limit", [&] { sqlite_stream(root); });
    run_case("sqlite_single_preparation_and_deferred_binding", [&] { sqlite_prepare_then_bind(root); });
    // Isolated fixture setup/reads; environment mutation never overlaps active provider work.
    // NOLINTNEXTLINE(concurrency-mt-unsafe)
    const bool postgres = std::getenv("NW_PG_URL") != nullptr;
    if (postgres)
    {
        run_case("postgres_tls_identity_expiry_and_opt_in", postgres_tls);
        run_case("postgres_environment_and_uri_policy", postgres_environment);
        run_case("postgres_timeout_rollback_and_savepoints", postgres_timeout_transaction);
        run_case("postgres_cancel_stream_drain_and_reuse", postgres_cancel_and_stream);
        run_case("postgres_grouped_preparation_deferred_execution_and_external_schema",
                 postgres_preparation_and_schema);
        run_case("postgres_disconnect_and_replacement", postgres_disconnect);
    }
    std::filesystem::remove_all(root);
    std::cout << "{\"status\":\"" << (failures == 0 ? "passed" : "failed") << "\",\"checks\":" << checks
              << ",\"failed_cases\":" << failures << ",\"postgres_verified\":" << (postgres ? "true" : "false")
              << "}\n";
    return failures == 0 ? 0 : 1;
}
