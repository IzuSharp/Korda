#include "query_adapter.hpp"

#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <cerrno>
#include <charconv>
#include <chrono>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <latch>
#include <memory>
#include <poll.h>
#include <sstream>
#include <string>
#include <thread>
#include <utility>

#include <libpq-fe.h>
#include <nlohmann/json.hpp>
#include <openssl/evp.h>
#include <sqlite3.h>

// A separate correctness executable. No fault branch is added to the measured
// workload, and no result here is a latency/throughput/allocation acceptance.
namespace
{
using Json = nlohmann::json;
using Clock = std::chrono::steady_clock;
using Time = Clock::time_point;
constexpr auto command_budget = std::chrono::seconds(5);

struct Owners
{
    std::atomic<unsigned> connections{0}, sqlite_statements{0}, pg_results{0}, pg_cancels{0}, close_failures{0};

    Json json() const
    {
        return {{"connections", connections.load()},
                {"sqlite_statements", sqlite_statements.load()},
                {"postgres_results", pg_results.load()},
                {"postgres_cancel_connections", pg_cancels.load()},
                {"unconfirmed_sqlite_closes", close_failures.load()}};
    }
    bool empty() const
    {
        return connections == 0 && sqlite_statements == 0 && pg_results == 0 && pg_cancels == 0 && close_failures == 0;
    }
} owners;
std::atomic<unsigned> next_connection_id{1};

void require(bool condition, const std::string& message)
{
    if (!condition)
        throw std::runtime_error(message);
}

std::int64_t ns(Time time)
{
    return std::chrono::duration_cast<std::chrono::nanoseconds>(time.time_since_epoch()).count();
}

std::string sha256(const std::filesystem::path& path)
{
    std::ifstream stream(path, std::ios::binary);
    require(bool(stream), "Cannot open hash input: " + path.string());
    std::unique_ptr<EVP_MD_CTX, decltype(&EVP_MD_CTX_free)> context(EVP_MD_CTX_new(), EVP_MD_CTX_free);
    require(context && EVP_DigestInit_ex(context.get(), EVP_sha256(), nullptr) == 1, "SHA256 initialization failed");
    std::array<char, 65536> buffer{};
    while (stream)
    {
        stream.read(buffer.data(), buffer.size());
        if (stream.gcount())
            require(EVP_DigestUpdate(context.get(), buffer.data(), static_cast<std::size_t>(stream.gcount())) == 1,
                    "SHA256 update failed");
    }
    require(stream.eof(), "SHA256 read failed");
    std::array<unsigned char, 32> digest{};
    unsigned length = 0;
    require(EVP_DigestFinal_ex(context.get(), digest.data(), &length) == 1 && length == 32,
            "SHA256 finalization failed");
    std::ostringstream text;
    text << std::hex << std::setfill('0');
    for (auto value : digest)
        text << std::setw(2) << static_cast<unsigned>(value);
    return text.str();
}

std::int64_t integer(std::string_view text)
{
    std::int64_t value = 0;
    const auto parsed = std::from_chars(text.data(), text.data() + text.size(), value);
    require(parsed.ec == std::errc{} && parsed.ptr == text.data() + text.size(), "Invalid provider integer");
    return value;
}

struct SqliteStatement
{
    sqlite3_stmt* value = nullptr;
    explicit SqliteStatement(sqlite3* connection, const char* sql)
    {
        const int status = sqlite3_prepare_v3(connection, sql, -1, 0, &value, nullptr);
        if (value)
            ++owners.sqlite_statements;
        if (status != SQLITE_OK)
        {
            if (value)
            {
                sqlite3_finalize(value);
                --owners.sqlite_statements;
            }
            throw std::runtime_error("SQLite prepare: " + std::string(sqlite3_errmsg(connection)));
        }
    }
    SqliteStatement(const SqliteStatement&) = delete;
    ~SqliteStatement()
    {
        if (value)
        {
            sqlite3_finalize(value);
            --owners.sqlite_statements;
        }
    }
    int close()
    {
        auto* statement = std::exchange(value, nullptr);
        if (!statement)
            return SQLITE_OK;
        --owners.sqlite_statements;
        return sqlite3_finalize(statement);
    }
};

struct SqliteConnection
{
    sqlite3* value = nullptr;
    unsigned id = next_connection_id++;
    explicit SqliteConnection(const std::string& connection)
    {
        const int result = sqlite3_open_v2(connection.c_str(), &value,
                                           SQLITE_OPEN_READWRITE | SQLITE_OPEN_FULLMUTEX | SQLITE_OPEN_URI, nullptr);
        if (value)
            ++owners.connections;
        try
        {
            require(result == SQLITE_OK, "SQLite open failed");
            sqlite3_extended_result_codes(value, 1);
            require(sqlite3_busy_timeout(value, 5000) == SQLITE_OK, "SQLite busy timeout configuration failed");
            execute("PRAGMA foreign_keys=ON");
            execute("PRAGMA journal_mode=WAL");
            execute("PRAGMA synchronous=FULL");
        }
        catch (...)
        {
            close();
            throw;
        }
    }
    SqliteConnection(const SqliteConnection&) = delete;
    ~SqliteConnection()
    {
        close();
    }
    void close()
    {
        if (!value)
            return;
        const int result = sqlite3_close(value);
        if (result != SQLITE_OK)
        {
            ++owners.close_failures;
            sqlite3_close_v2(value);
        }
        value = nullptr;
        --owners.connections;
    }
    std::int64_t execute(const char* sql)
    {
        SqliteStatement statement(value, sql);
        int result;
        do
            result = sqlite3_step(statement.value);
        while (result == SQLITE_ROW);
        require(result == SQLITE_DONE, "SQLite execute: " + std::string(sqlite3_errmsg(value)));
        return sqlite3_changes64(value);
    }
    std::array<std::int64_t, 2> state()
    {
        SqliteStatement statement(value, "SELECT \"Counter\",\"Version\" FROM perf_lifecycle WHERE \"Id\"=1");
        require(sqlite3_step(statement.value) == SQLITE_ROW, "Missing lifecycle row");
        const std::array<std::int64_t, 2> result{sqlite3_column_int64(statement.value, 0),
                                                 sqlite3_column_int64(statement.value, 1)};
        require(sqlite3_step(statement.value) == SQLITE_DONE, "Unexpected extra lifecycle row");
        return result;
    }
    std::int64_t scalar(const char* sql)
    {
        SqliteStatement statement(value, sql);
        require(sqlite3_step(statement.value) == SQLITE_ROW &&
                    sqlite3_column_type(statement.value, 0) == SQLITE_INTEGER,
                "Expected one typed SQLite scalar");
        const auto result = sqlite3_column_int64(statement.value, 0);
        require(sqlite3_step(statement.value) == SQLITE_DONE, "Unexpected extra SQLite scalar row");
        return result;
    }
};

struct PgResultDelete
{
    void operator()(PGresult* result) const
    {
        if (result)
        {
            PQclear(result);
            --owners.pg_results;
        }
    }
};
using PgResult = std::unique_ptr<PGresult, PgResultDelete>;

struct PgCancelDelete
{
    void operator()(PGcancelConn* connection) const
    {
        if (connection)
        {
            PQcancelFinish(connection);
            --owners.pg_cancels;
        }
    }
};

void poll_socket(int socket, short events, Time deadline)
{
    require(socket >= 0, "Invalid PostgreSQL socket");
    for (;;)
    {
        const auto remaining = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - Clock::now()).count();
        require(remaining > 0, "PostgreSQL operation deadline expired");
        pollfd descriptor{socket, events, 0};
        const int result = ::poll(&descriptor, 1, static_cast<int>(std::min<std::int64_t>(remaining, 50)));
        if (result < 0 && errno == EINTR)
            continue;
        require(result >= 0, "PostgreSQL poll failed");
        if (result > 0)
        {
            require(!(descriptor.revents & (POLLERR | POLLHUP | POLLNVAL)), "PostgreSQL socket closed during poll");
            return;
        }
    }
}

std::int64_t pg_integer(const PGresult* result, int row, int column)
{
    require(PQftype(result, column) == 20 && PQfformat(result, column) == 1 && !PQgetisnull(result, row, column) &&
                PQgetlength(result, row, column) == 8,
            "PostgreSQL scalar is not binary int64");
    std::uint64_t value = 0;
    const auto* bytes = reinterpret_cast<const unsigned char*>(PQgetvalue(result, row, column));
    for (int i = 0; i < 8; ++i)
        value = (value << 8) | bytes[i];
    return std::bit_cast<std::int64_t>(value);
}

struct PgConnection
{
    PGconn* value = nullptr;
    unsigned id = next_connection_id++;
    explicit PgConnection(const std::string& connection)
    {
        const std::array<const char*, 5> keys{"dbname", "connect_timeout", "sslmode", "application_name", nullptr};
        const std::array<const char*, 5> values{connection.c_str(), "5", "disable", "db_perf_native_lifecycle",
                                                nullptr};
        value = PQconnectdbParams(keys.data(), values.data(), 1);
        if (value)
            ++owners.connections;
        try
        {
            require(value && PQstatus(value) == CONNECTION_OK, "PostgreSQL connect failed");
            require(PQsetnonblocking(value, 1) == 0, "Cannot select nonblocking libpq");
            execute("SET statement_timeout=5000");
            execute("SET TIME ZONE 'UTC'");
        }
        catch (...)
        {
            close();
            throw;
        }
    }
    PgConnection(const PgConnection&) = delete;
    ~PgConnection()
    {
        close();
    }
    void close()
    {
        if (value)
        {
            PQfinish(std::exchange(value, nullptr));
            --owners.connections;
        }
    }
    void send(const char* sql, Time deadline, int count = 0, const Oid* types = nullptr,
              const char* const* parameters = nullptr, int format = 1)
    {
        require(PQsendQueryParams(value, sql, count, types, parameters, nullptr, nullptr, format) == 1,
                "PostgreSQL send: " + std::string(PQerrorMessage(value)));
        for (;;)
        {
            const int flush = PQflush(value);
            require(flush >= 0, "PostgreSQL flush failed");
            if (flush == 0)
                break;
            poll_socket(PQsocket(value), POLLOUT, deadline);
        }
    }
    PgResult next(Time deadline)
    {
        for (;;)
        {
            require(PQconsumeInput(value) == 1, "PostgreSQL consume: " + std::string(PQerrorMessage(value)));
            if (!PQisBusy(value))
                break;
            poll_socket(PQsocket(value), POLLIN, deadline);
        }
        auto* result = PQgetResult(value);
        if (result)
            ++owners.pg_results;
        return PgResult(result);
    }
    PgResult query(const char* sql, int count = 0, const Oid* types = nullptr, const char* const* parameters = nullptr,
                   int format = 1)
    {
        const auto deadline = Clock::now() + command_budget;
        send(sql, deadline, count, types, parameters, format);
        auto result = next(deadline);
        require(bool(result), "Missing PostgreSQL result");
        require(!next(deadline), "Unexpected additional PostgreSQL result");
        const auto status = PQresultStatus(result.get());
        require(status == PGRES_TUPLES_OK || status == PGRES_COMMAND_OK,
                "PostgreSQL query: " + std::string(PQresultErrorMessage(result.get())));
        return result;
    }
    std::int64_t execute(const char* sql)
    {
        auto result = query(sql);
        require(PQresultStatus(result.get()) == PGRES_COMMAND_OK, "Expected PostgreSQL command completion");
        const std::string_view affected(PQcmdTuples(result.get()));
        return affected.empty() ? 0 : integer(affected);
    }
    std::array<std::int64_t, 2> state()
    {
        auto result = query("SELECT \"Counter\",\"Version\" FROM perf_lifecycle WHERE \"Id\"=1");
        require(PQntuples(result.get()) == 1 && PQnfields(result.get()) == 2, "Unexpected lifecycle state row count");
        return {pg_integer(result.get(), 0, 0), pg_integer(result.get(), 0, 1)};
    }
    std::int64_t scalar(const char* sql)
    {
        auto result = query(sql);
        require(PQntuples(result.get()) == 1 && PQnfields(result.get()) == 1, "Expected one PostgreSQL scalar");
        return pg_integer(result.get(), 0, 0);
    }
};

template <typename Connection> void reset(Connection& connection)
{
    connection.execute(
        "CREATE TABLE IF NOT EXISTS perf_lifecycle(\"Id\" BIGINT PRIMARY KEY,\"Counter\" BIGINT NOT NULL,"
        "\"Version\" BIGINT NOT NULL)");
    connection.execute("DELETE FROM perf_lifecycle");
    require(connection.execute("INSERT INTO perf_lifecycle(\"Id\",\"Counter\",\"Version\") VALUES(1,0,1)") == 1,
            "Lifecycle row initialization failed");
}

template <typename Connection> Json conflict(const std::string& text, Connection& control)
{
    reset(control);
    std::array<std::unique_ptr<Connection>, 2> connections{std::make_unique<Connection>(text),
                                                           std::make_unique<Connection>(text)};
    std::array<std::array<std::int64_t, 2>, 2> observed{};
    std::array<std::int64_t, 2> affected{-1, -1}, starts{}, terminals{};
    std::array<std::exception_ptr, 2> failures{};
    std::latch read_done(2), release(1);
    std::array<std::thread, 2> threads;
    try
    {
        for (std::size_t i = 0; i < 2; ++i)
            threads[i] = std::thread(
                [&, i]
                {
                    try
                    {
                        observed[i] = connections[i]->state();
                        require(observed[i] == std::array<std::int64_t, 2>{0, 1},
                                "Worker did not read common version 1");
                    }
                    catch (...)
                    {
                        failures[i] = std::current_exception();
                    }
                    read_done.count_down();
                    release.wait();
                    if (failures[i])
                        return;
                    try
                    {
                        starts[i] = ns(Clock::now());
                        affected[i] = connections[i]->execute(
                            "UPDATE perf_lifecycle SET \"Counter\"=1,\"Version\"=2 WHERE \"Id\"=1 AND \"Version\"=1");
                        terminals[i] = ns(Clock::now());
                    }
                    catch (...)
                    {
                        failures[i] = std::current_exception();
                    }
                });
    }
    catch (...)
    {
        release.count_down();
        for (auto& thread : threads)
            if (thread.joinable())
                thread.join();
        throw;
    }
    read_done.wait();
    const auto released = ns(Clock::now());
    release.count_down();
    for (auto& thread : threads)
        thread.join();
    for (const auto& failure : failures)
        if (failure)
            std::rethrow_exception(failure);
    const auto persisted = control.state();
    require((affected == std::array<std::int64_t, 2>{1, 0} || affected == std::array<std::int64_t, 2>{0, 1}) &&
                persisted == std::array<std::int64_t, 2>{1, 2},
            "Two-worker conflict or persisted state did not match");
    return {{"name", "two-worker-version-conflict"},
            {"passed", true},
            {"observed_before_barrier", observed},
            {"barrier_released_monotonic_ns", released},
            {"actual_start_monotonic_ns", starts},
            {"terminal_monotonic_ns", terminals},
            {"update_call_intervals_overlap", std::max(starts[0], starts[1]) < std::min(terminals[0], terminals[1])},
            {"affected", affected},
            {"persisted_counter_version", persisted},
            {"implicit_retries", 0},
            {"connection_ids", {connections[0]->id, connections[1]->id}}};
}

Json sqlite_cancel(SqliteConnection& connection)
{
    struct Progress
    {
        std::atomic<std::uint64_t> calls{0};
        Time deadline;
    } progress{{0}, Clock::now() + command_budget};
    SqliteStatement statement(connection.value,
                              "WITH RECURSIVE work(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM work WHERE n<100000000) "
                              "SELECT sum(n) FROM work");
    sqlite3_progress_handler(
        connection.value, 1000,
        [](void* state)
        {
            auto& value = *static_cast<Progress*>(state);
            ++value.calls;
            return Clock::now() >= value.deadline ? 1 : 0;
        },
        &progress);
    struct RemoveProgress
    {
        sqlite3* connection;
        ~RemoveProgress()
        {
            sqlite3_progress_handler(connection, 0, nullptr, nullptr);
        }
    } remove_progress{connection.value};
    std::atomic<bool> finished{false};
    int result = -1;
    std::jthread worker(
        [&]
        {
            result = sqlite3_step(statement.value);
            finished.store(true, std::memory_order_release);
        });
    const auto observe_deadline = Clock::now() + std::chrono::seconds(2);
    while (progress.calls.load(std::memory_order_acquire) == 0 && !finished.load(std::memory_order_acquire) &&
           Clock::now() < observe_deadline)
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    const auto observed = progress.calls.load(std::memory_order_acquire);
    const auto pending = !finished.load(std::memory_order_acquire);
    const auto requested = ns(Clock::now());
    sqlite3_interrupt(connection.value);
    worker.join();
    const auto terminal = ns(Clock::now());
    sqlite3_progress_handler(connection.value, 0, nullptr, nullptr);
    const auto finalization = statement.close();
    require(observed > 0 && pending && result == SQLITE_INTERRUPT && Clock::now() < progress.deadline,
            "SQLite did not observe caller interruption of a running VM before timeout");
    require(finalization == SQLITE_INTERRUPT, "SQLite interrupted statement finalization changed result");
    require(connection.scalar("SELECT CAST(42 AS BIGINT)") == 42, "SQLite connection did not recover after interrupt");
    return {{"name", "caller-cancel-running-query"},
            {"passed", true},
            {"actual_execution_observed", true},
            {"vm_progress_callbacks_before_cancel", observed},
            {"api_incomplete_before_cancel", pending},
            {"cancellation_api", "sqlite3_interrupt from a different thread than sqlite3_step"},
            {"sqlite_result_code", result},
            {"sqlite_finalize_code", finalization},
            {"cancel_requested_monotonic_ns", requested},
            {"terminal_monotonic_ns", terminal},
            {"deadline_was_not_trigger", true},
            {"same_connection_reused", true},
            {"connection_id", connection.id}};
}

Json postgres_cancel_probe(PgConnection& connection, const std::string& text)
{
    PgConnection observer(text);
    const auto deadline = Clock::now() + command_budget;
    connection.send("SELECT pg_sleep(30)", deadline);
    const auto pid = std::to_string(PQbackendPID(connection.value));
    const std::array<const char*, 1> parameters{pid.c_str()};
    const std::array<Oid, 1> types{23};
    bool active_sleep = false;
    std::string last_state, last_wait_type, last_wait;
    const auto observe_deadline = Clock::now() + std::chrono::seconds(2);
    while (Clock::now() < observe_deadline)
    {
        auto status = observer.query("SELECT state,wait_event_type,wait_event FROM pg_stat_activity WHERE pid=$1", 1,
                                     types.data(), parameters.data(), 0);
        require(PQntuples(status.get()) == 1 && PQnfields(status.get()) == 3,
                "Cannot observe pending PostgreSQL backend");
        last_state = PQgetvalue(status.get(), 0, 0);
        last_wait_type = PQgetvalue(status.get(), 0, 1);
        last_wait = PQgetvalue(status.get(), 0, 2);
        active_sleep = last_state == "active" && last_wait_type == "Timeout" && last_wait == "PgSleep";
        if (active_sleep)
            break;
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    }
    require(PQconsumeInput(connection.value) == 1, "Cannot inspect PostgreSQL pending state");
    const bool pending = PQisBusy(connection.value) != 0;
    std::unique_ptr<PGcancelConn, PgCancelDelete> cancellation(PQcancelCreate(connection.value));
    require(cancellation != nullptr, "Cannot allocate libpq cancellation connection");
    ++owners.pg_cancels;
    const auto requested = ns(Clock::now());
    bool sent = false;
    std::string cancellation_error;
    try
    {
        require(PQcancelStart(cancellation.get()) == 1, "PQcancelStart failed");
        for (;;)
        {
            const auto state = PQcancelPoll(cancellation.get());
            if (state == PGRES_POLLING_OK)
            {
                sent = true;
                break;
            }
            require(state != PGRES_POLLING_FAILED, "PQcancelPoll failed");
            poll_socket(PQcancelSocket(cancellation.get()), state == PGRES_POLLING_READING ? POLLIN : POLLOUT,
                        deadline);
        }
    }
    catch (const std::exception& error)
    {
        cancellation_error = std::string(error.what()) + ": " + PQcancelErrorMessage(cancellation.get());
    }
    cancellation.reset();
    std::string state, message;
    while (auto result = connection.next(deadline))
    {
        if (PQresultStatus(result.get()) == PGRES_FATAL_ERROR)
        {
            const auto* code = PQresultErrorField(result.get(), PG_DIAG_SQLSTATE);
            state = code ? code : "";
            message = PQresultErrorMessage(result.get());
        }
    }
    const auto terminal = ns(Clock::now());
    require(active_sleep && pending && sent && cancellation_error.empty() && state == "57014" &&
                Clock::now() < deadline && PQtransactionStatus(connection.value) == PQTRANS_IDLE,
            "PostgreSQL running cancellation was not confirmed: " + cancellation_error + message);
    require(connection.scalar("SELECT 42::bigint") == 42, "PostgreSQL connection did not recover after cancel");
    return {{"name", "caller-cancel-running-query"},
            {"passed", true},
            {"actual_execution_observed", true},
            {"server_state", last_state},
            {"server_wait_event_type", last_wait_type},
            {"server_wait_event", last_wait},
            {"api_incomplete_before_cancel", pending},
            {"cancellation_api", "PQcancelCreate/PQcancelStart/PQcancelPoll/PQcancelFinish"},
            {"sqlstate", state},
            {"server_error", message},
            {"cancel_requested_monotonic_ns", requested},
            {"terminal_monotonic_ns", terminal},
            {"deadline_was_not_trigger", true},
            {"same_connection_reused", true},
            {"connection_id", connection.id},
            {"backend_pid", integer(pid)}};
}

Json early(SqliteConnection& connection, const db_perf::ReadCase& query)
{
    const auto available = connection.scalar("SELECT COUNT(*) FROM orders WHERE \"TenantId\"=1");
    require(available == 8192, "Early-stop fixture does not contain 8192 tenant rows");
    SqliteStatement statement(connection.value, query.sql.c_str());
    require(sqlite3_bind_int64(statement.value, 1, 1) == SQLITE_OK &&
                sqlite3_bind_int64(statement.value, 2, 8192) == SQLITE_OK,
            "Early-stop SQLite parameter binding failed");
    require(sqlite3_step(statement.value) == SQLITE_ROW && sqlite3_column_int64(statement.value, 0) == 1,
            "First SQLite business row changed");
    require(statement.close() == SQLITE_OK, "Early-stop SQLite finalize failed");
    require(connection.scalar("SELECT COUNT(*) FROM orders WHERE \"TenantId\"=1") == 8192,
            "Same SQLite connection failed after early stop");
    return {{"name", "early-enumeration-stop"},
            {"passed", true},
            {"available_rows", available},
            {"business_rows_consumed", 1},
            {"provider_rows_read_for_cleanup", 0},
            {"statement_retirement", "sqlite3_finalize after the first SQLITE_ROW"},
            {"same_connection_reused", true},
            {"connection_id", connection.id},
            {"sql", query.sql},
            {"parameters_int64", {1, 8192}}};
}

Json early(PgConnection& connection, const db_perf::ReadCase& query)
{
    const auto available = connection.scalar("SELECT COUNT(*) FROM orders WHERE \"TenantId\"=1");
    require(available == 8192, "Early-stop fixture does not contain 8192 tenant rows");
    const std::array<const char*, 2> parameters{"1", "8192"};
    const std::array<Oid, 2> types{20, 20};
    const auto deadline = Clock::now() + command_budget;
    connection.send(query.sql.c_str(), deadline, 2, types.data(), parameters.data());
    require(PQsetSingleRowMode(connection.value) == 1, "Cannot enable PostgreSQL single-row mode");
    {
        auto first = connection.next(deadline);
        require(first && PQresultStatus(first.get()) == PGRES_SINGLE_TUPLE && PQntuples(first.get()) == 1 &&
                    pg_integer(first.get(), 0, 0) == 1,
                "First PostgreSQL business row changed");
    }
    std::uint64_t drained_rows = 0;
    unsigned confirmations = 0;
    // libpq has no reader-dispose object. Retiring its pending results is the
    // direct driver's explicit early-consumer-exit contract. No remaining row
    // is materialized into a business value, but its result owner is released.
    while (auto result = connection.next(deadline))
    {
        const auto status = PQresultStatus(result.get());
        if (status == PGRES_SINGLE_TUPLE)
            drained_rows += static_cast<std::uint64_t>(PQntuples(result.get()));
        else
        {
            require(status == PGRES_TUPLES_OK, "Unexpected PostgreSQL early-stop retirement error");
            ++confirmations;
        }
    }
    require(drained_rows == 8191 && confirmations == 1 && PQtransactionStatus(connection.value) == PQTRANS_IDLE,
            "PostgreSQL early-stop results did not retire completely");
    require(connection.scalar("SELECT COUNT(*) FROM orders WHERE \"TenantId\"=1") == 8192,
            "Same PostgreSQL connection failed after early stop");
    return {
        {"name", "early-enumeration-stop"},
        {"passed", true},
        {"available_rows", available},
        {"business_rows_consumed", 1},
        {"provider_rows_read_for_cleanup", drained_rows},
        {"statement_retirement", "PQclear first row then drain and clear remaining PGresults; no DTO materialization"},
        {"same_connection_reused", true},
        {"connection_id", connection.id},
        {"backend_pid", PQbackendPID(connection.value)},
        {"sql", query.sql},
        {"parameters_int64", {1, 8192}}};
}

template <typename Connection, typename Cancel>
void provider_cases(Json& output, const std::string& text, const db_perf::ReadCase& stream, Cancel cancel)
{
    Connection control(text);
    try
    {
        output["cases"].push_back(conflict<Connection>(text, control));
        output["cases"].push_back(early(control, stream));
        output["cases"].push_back(cancel(control));
        reset(control);
        output["lifecycle_table_restored"] = control.state() == std::array<std::int64_t, 2>{0, 1};
        require(output["lifecycle_table_restored"].get<bool>(), "Lifecycle fixture restoration failed");
        output["owners_before_connection_close"] = owners.json();
    }
    catch (...)
    {
        const auto primary = std::current_exception();
        try
        {
            reset(control);
            output["lifecycle_table_restored_after_failure"] = control.state() == std::array<std::int64_t, 2>{0, 1};
        }
        catch (const std::exception& cleanup)
        {
            output["cleanup_error"] = cleanup.what();
        }
        std::rethrow_exception(primary);
    }
}

} // namespace

int main(int argc, char** argv)
{
    std::string provider, connection, manifest, destination;
    Json output{{"schema_version", 1},
                {"task", "DB-Perf-01"},
                {"route", "direct-native-cpp"},
                {"scope", "separate real-driver lifecycle correctness; no performance/allocation acceptance"},
                {"cases", Json::array()},
                {"can_qualify", false},
                {"native_heap_instrumented", false}};
    int exit_code = 2;
    try
    {
        std::map<std::string, std::string> options;
        for (int i = 1; i < argc; i += 2)
        {
            require(i + 1 < argc, "Missing lifecycle option value");
            require(options.emplace(argv[i], argv[i + 1]).second, "Duplicate lifecycle option");
        }
        require(options.size() == 4 && options.contains("--provider") && options.contains("--connection") &&
                    options.contains("--manifest") && options.contains("--output"),
                "Required options: --provider --connection --manifest --output");
        provider = options.at("--provider");
        connection = options.at("--connection");
        manifest = options.at("--manifest");
        destination = options.at("--output");
        require(provider == "sqlite" || provider == "postgresql", "Unknown lifecycle provider");
        require(!std::filesystem::exists(destination), "Refusing to overwrite existing lifecycle evidence");
        output["provider"] = provider;
        output["executable_sha256"] = sha256("/proc/self/exe");
        output["contract_sha256"] = sha256(manifest);
        const auto contract = db_perf::load_contract(manifest, provider == "sqlite" ? db_perf::Provider::Sqlite
                                                                                    : db_perf::Provider::PostgreSql);
        const auto selected = std::find_if(contract.cases.begin(), contract.cases.end(),
                                           [](const auto& query) { return query.name == "stream-large"; });
        require(selected != contract.cases.end() && selected->expected_rows == 8192 &&
                    selected->parameters.size() == 2 && selected->parameters[0].kind == db_perf::ValueKind::Int64 &&
                    selected->parameters[1].kind == db_perf::ValueKind::Int64 &&
                    std::get<std::int64_t>(selected->parameters[0].value) == 1 &&
                    std::get<std::int64_t>(selected->parameters[1].value) == 8192,
                "Early-stop contract input differs");
        const auto identity = db_perf::runtime_identity();
        output["runtime"] = {{"sqlite_version", identity.sqlite_version},
                             {"sqlite_source_id", identity.sqlite_source_id},
                             {"sqlite_compile_options", identity.sqlite_compile_options},
                             {"libpq_version", identity.libpq_version}};
        output["started_monotonic_ns"] = ns(Clock::now());
        if (provider == "sqlite")
            provider_cases<SqliteConnection>(output, connection, *selected, sqlite_cancel);
        else
            provider_cases<PgConnection>(output, connection, *selected,
                                         [&](auto& owner) { return postgres_cancel_probe(owner, connection); });
        output["finished_monotonic_ns"] = ns(Clock::now());
        require(owners.empty(), "Lifecycle owners did not return to zero");
        require(sha256(manifest) == output["contract_sha256"].get<std::string>(),
                "Contract changed during lifecycle run");
        output["status"] = "passed_lifecycle_correctness";
        exit_code = 0;
    }
    catch (const std::exception& error)
    {
        output["status"] = "failed_lifecycle_correctness";
        output["error"] = error.what();
    }
    output["owners_after_close"] = owners.json();
    output["owners_returned_to_zero"] = owners.empty();
    if (!destination.empty() && !std::filesystem::exists(destination))
    {
        const auto parent = std::filesystem::path(destination).parent_path();
        if (!parent.empty())
            std::filesystem::create_directories(parent);
        std::ofstream result(destination);
        result << output.dump(2) << '\n';
        if (!result)
            return 2;
    }
    std::cout << output.dump() << '\n';
    return exit_code;
}
