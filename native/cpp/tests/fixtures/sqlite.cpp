// SPDX-License-Identifier: MIT
// B03's retained fourteen scenario groups, using the real C++ provider and Host.
// This synchronous test bridge and its SQLite fault hooks are never packaged.
#include "nw/host.hpp"
#include "support.hpp"

#include <sqlite3.h>

#include <bit>
#include <filesystem>
#include <future>
#include <iostream>
#include <limits>

#ifndef NW_HOST_TEST_HOOKS
#error The B03 fault fixture must not be compiled into a product library.
#endif

namespace nw
{
std::uint64_t module_identity()
{
    return 0x4442435050423033ULL;
}
} // namespace nw

namespace
{
using namespace nw;
using namespace std::chrono_literals;
std::atomic<std::uint32_t> runtimes{0};
std::atomic<std::size_t> assertions{0};
constexpr std::uint64_t WAIT_NS = 15'000'000'000;

void check(bool value, const char* message)
{
    ++assertions;
    if (!value)
        throw std::runtime_error(message);
}
template <class F> Error rejects(F&& action)
{
    try
    {
        action();
    }
    catch (const Error& error)
    {
        ++assertions;
        return error;
    }
    throw std::runtime_error("invalid operation was accepted");
}
template <class F> void eventually(F&& condition, const char* message)
{
    const auto deadline = Clock::now() + 15s;
    while (!condition())
    {
        if (Clock::now() >= deadline)
            throw std::runtime_error(message);
        std::this_thread::yield();
    }
}
template <class T> T joined(std::future<T>& future)
{
    check(future.wait_for(15s) == std::future_status::ready, "bounded native worker completion");
    return future.get();
}
Bytes bytes(std::string_view value)
{
    return {value.begin(), value.end()};
}
std::shared_ptr<Cancel> token()
{
    return std::make_shared<Cancel>();
}
CallContext call(std::shared_ptr<Cancel> cancel = {})
{
    return {std::move(cancel), Clock::now() + 15s, true};
}
Bytes option_record(const ProviderOptions& options)
{
    return encode_control(9, {{1, 1, u32_bytes(options.sqlite_mode)},
                              {2, 1, u32_bytes(options.sqlite_journal)},
                              {3, 1, u32_bytes(options.sqlite_synchronous)},
                              {4, 1, u32_bytes(options.sqlite_begin)},
                              {5, 2, u64_bytes(options.sqlite_busy_timeout_ns)},
                              {6, 1, u32_bytes(options.sqlite_foreign_keys)}});
}
HostConfig configuration(const std::string& path, ByteView options, std::uint64_t connections = 1,
                         std::uint64_t acquisition_ns = 5'000'000'000)
{
    const auto limits = encode_control(10, {{1, 2, u64_bytes(connections)},
                                            {2, 2, u64_bytes(16)},
                                            {3, 3, u64_bytes(acquisition_ns)},
                                            {4, 2, u64_bytes(64)},
                                            {5, 2, u64_bytes(65536)},
                                            {6, 2, u64_bytes(64)},
                                            {7, 2, u64_bytes(65536)},
                                            {8, 2, u64_bytes(65536)},
                                            {9, 2, u64_bytes(4096)},
                                            {10, 2, u64_bytes(64)},
                                            {11, 2, u64_bytes(4096)},
                                            {12, 2, u64_bytes(MAX_BYTES)},
                                            {13, 2, u64_bytes(32)},
                                            {14, 2, u64_bytes(MAX_BYTES)},
                                            {15, 3, u64_bytes(WAIT_NS)}});
    return HostConfig::parse(encode_control(
        1, {{1, 1, u32_bytes(1)}, {2, 4, bytes(path)}, {3, 5, limits}, {4, 5, Bytes(options.begin(), options.end())}}));
}
HostConfig configuration(const std::string& path, ProviderOptions options = {}, std::uint64_t connections = 1)
{
    return configuration(path, option_record(options), connections);
}
struct Scope
{
    explicit Scope(HostConfig config) : source(Source::create(std::move(config))) {}
    ~Scope()
    {
        source->request_close();
        source->join();
    }
    std::shared_ptr<Session> open()
    {
        {
            std::lock_guard gate(source->mutex);
            ++source->leased; // Same reservation as the source admission dispatcher.
        }
        auto session = Session::open_reserved(source, token());
        sessions.push_back(session);
        return session;
    }
    std::shared_ptr<Source> source;
    std::vector<std::shared_ptr<Session>> sessions;
};
void close_session(const std::shared_ptr<Session>& session)
{
    session->request_close();
    session->join();
    check(session->retired.load() && !session->connection, "physical session lease released after cleanup");
}
std::shared_ptr<SqlPlan> plan(const std::shared_ptr<Source>& source, std::string sql, bool rows)
{
    auto value = std::make_shared<SqlPlan>();
    value->source = source;
    value->sql = std::move(sql);
    value->kind = rows ? 0 : 1;
    value->result_kind = rows ? 3 : 0;
    value->timeout_ns = WAIT_NS;
    value->expectation = {1, Profile::Sqlite, {}, 0, 0, {}, 1};
    value->parameters = Batch::encode(value->expectation, 1, {});
    if (rows)
    {
        auto projection = std::make_shared<Projection>(
            Projection::parse_scalar(bytes("{\"version\":1,\"fields\":[{\"ordinal\":0,\"name\":\"\",\"codec\":3,"
                                           "\"codec_config\":null,\"nullable\":false}]}"),
                                     Profile::Sqlite));
        value->projection = source->resolve_projection({source->identity, 1, projection, source->codecs()});
    }
    return value;
}
std::int64_t execute(const std::shared_ptr<Session>& session, std::string sql)
{
    return std::get<std::int64_t>(
        session->execute(plan(session->source, std::move(sql), false), token(), session->admit(false)));
}
struct Stream
{
    std::shared_ptr<Owner> owner;
    std::shared_ptr<Cursor> cursor;
    void close()
    {
        cursor->request_close();
        cursor->join();
        owner.reset();
    }
    ~Stream()
    {
        close();
    }
};
Stream stream(const std::shared_ptr<Session>& session, std::string sql)
{
    auto result = session->execute(plan(session->source, std::move(sql), true), token(), session->admit(false));
    auto owner = std::get<std::shared_ptr<Owner>>(result);
    auto cursor = std::get<std::shared_ptr<Cursor>>(owner->data);
    return {std::move(owner), std::move(cursor)};
}
std::int64_t integer(ByteView value)
{
    check(value.size() == 8, "int64 canonical width");
    return std::bit_cast<std::int64_t>(read_u64(value, 0));
}
std::int64_t scalar(const std::shared_ptr<Session>& session, std::string sql)
{
    auto rows = stream(session, std::move(sql));
    auto buffer = rows.cursor->fetch(token(), 2, 65536);
    auto batch = Batch::parse(buffer->bytes, rows.cursor->expectation);
    check(batch.rows() == 1 && batch.cell(0, 0).has_value(), "exact scalar row");
    const auto value = integer(*batch.cell(0, 0));
    buffer.reset();
    auto end = rows.cursor->fetch(token(), 2, 65536);
    check(Batch::parse(end->bytes, rows.cursor->expectation).rows() == 0, "natural EOF");
    end.reset();
    return value;
}
std::string text_scalar(ProviderConnection& connection, std::string_view sql)
{
    const auto rows = connection.query_text(sql, call(), 1);
    check(rows.size() == 1 && rows[0].size() == 1 && rows[0][0], "independent provider scalar shape");
    return *rows[0][0];
}
void mark(const std::shared_ptr<Session>& session)
{
    execute(session, "CREATE TEMP TABLE physical_marker(value INTEGER NOT NULL)");
    execute(session, "INSERT INTO physical_marker VALUES (731)");
}
void same(const std::shared_ptr<Session>& session)
{
    check(scalar(session, "SELECT value FROM physical_marker") == 731, "same physical session reused");
}
TransactionOptions transaction_options()
{
    return {0, false, WAIT_NS, WAIT_NS, {}};
}
std::shared_ptr<Transaction> begin(const std::shared_ptr<Session>& session,
                                   TransactionOptions options = transaction_options())
{
    return Transaction::begin(session, std::move(options), token(), session->admit(true));
}
void finish(const std::shared_ptr<Transaction>& tx, bool commit)
{
    (void)tx->finish(commit, token(), tx->session->admit(true));
    tx->join();
}
void profile(const std::filesystem::path& file)
{
    ProviderOptions options;
    options.sqlite_busy_timeout_ns = 40'000'000;
    Scope scope(configuration(file.string(), options, 2));
    for (int i = 0; i < 2; ++i)
    {
        auto session = scope.open();
        auto& connection = *session->connection;
        check(text_scalar(connection, "SELECT sqlite_version()") == "3.51.3", "pinned SQLite engine");
        check(text_scalar(connection, "PRAGMA foreign_keys") == "1", "foreign keys enabled");
        check(text_scalar(connection, "PRAGMA journal_mode") == "wal", "WAL journal");
        check(text_scalar(connection, "PRAGMA synchronous") == "2", "FULL durability");
        check(text_scalar(connection, "PRAGMA busy_timeout") == "40", "observable busy timeout");
        check(text_scalar(connection, "SELECT sqlite_compileoption_used('THREADSAFE=1')") == "1",
              "SQLite thread safety");
    }
    (void)rejects([&] { (void)configuration(""); });
    (void)rejects([&] { (void)configuration("file:unbounded?cache=shared"); });
}
void memory()
{
    ProviderOptions options;
    options.sqlite_mode = 3;
    options.sqlite_journal = 2;
    (void)rejects([&] { (void)configuration(":memory:", options, 4); });
    auto wrong = options;
    wrong.sqlite_journal = 0;
    (void)rejects([&] { (void)configuration(":memory:", wrong); });
    (void)rejects([&] { (void)configuration(":memory:"); });
    Scope first(configuration(":memory:", options));
    Scope second(configuration(":memory:", options));
    auto session = first.open();
    mark(session);
    execute(session, "CREATE TABLE private_value(value INTEGER)");
    close_session(session);
    auto reused = first.open();
    same(reused);
    auto isolated = second.open();
    check(scalar(isolated, "SELECT count(*) FROM sqlite_master WHERE name='private_value'") == 0,
          "memory sources have independent physical databases");
}
void codecs(const std::filesystem::path& file)
{
    Scope scope(configuration(file.string()));
    auto session = scope.open();
    Expectation expected{1, Profile::Sqlite, {}, 0, 0, {{1, {3, {}, false}, 3}, {2, {5, {}, true}, 5}}, 1};
    expected.hash.fill(7);
    const auto value = u64_bytes(9'223'372'036'854'775'000ULL);
    const auto encoded = Batch::encode(expected, 1, {value, std::nullopt});
    auto parameters = decode_parameters(encoded, expected, 4096, MAX_BYTES, 65536);
    auto rows = session->connection->query("SELECT ?1 AS number, ?2 AS optional", parameters, call());
    auto row = rows->next({{3, {}, false}, {5, {}, true}}, 1024, call());
    check(row && (*row)[0] == std::optional<Bytes>(value) && !(*row)[1], "int64 precision and nullable text");
    check(!rows->next({{3, {}, false}, {5, {}, true}}, 1024, call()), "typed query EOF");
    rows->close();
    auto null_row = session->connection->query("SELECT NULL", {}, call());
    check(rejects([&] { (void)null_row->next({{3, {}, false}}, 1024, call()); }).code == 3,
          "NULL cannot materialize a nonnullable scalar");
    null_row->close();
    auto wrong = expected;
    wrong.hash[0] ^= 1;
    (void)rejects([&] { (void)decode_parameters(encoded, wrong, 4096, MAX_BYTES, 65536); });
    check(!session->active.load(), "no admitted command retained after codecs");
}
void savepoints(const std::filesystem::path& file)
{
    Scope scope(configuration(file.string()));
    auto session = scope.open();
    mark(session);
    execute(session, "CREATE TABLE parent(id INTEGER PRIMARY KEY)");
    execute(session, "CREATE TABLE child(id INTEGER REFERENCES parent(id))");
    auto tx = begin(session);
    auto point = tx->create_savepoint(token(), session->admit(true));
    (void)rejects([&] { execute(session, "INSERT INTO child VALUES (9)"); });
    check(!tx->active(), "constraint failure closes ordinary transaction admission");
    check(rejects([&] { (void)tx->finish(true, token(), session->admit(true)); }).status == NW_INVALID_STATE,
          "failed transaction cannot commit");
    (void)tx->savepoint_action(point->token, 0, token(), session->admit(true));
    check(tx->active(), "rollback-to recovers transaction admission");
    for (int i = 0; i < 3; ++i)
    {
        execute(session, "INSERT INTO parent VALUES (" + std::to_string(i) + ")");
        (void)tx->savepoint_action(point->token, 0, token(), session->admit(true));
    }
    execute(session, "INSERT INTO parent VALUES (1)");
    execute(session, "INSERT INTO child VALUES (1)");
    (void)tx->savepoint_action(point->token, 1, token(), session->admit(true));
    (void)rejects([&] { (void)tx->savepoint_action(point->token, 0, token(), session->admit(true)); });
    finish(tx, true);
    check(tx->outcome == 1 && tx->cleanup == 2 && tx->disposition == 0 && tx->retired,
          "confirmed commit retires cleanly on the original physical lease");
    same(session);
    check(scalar(session, "SELECT count(*) FROM parent") == 1, "repeated rollback-to preserves only final write");
}
struct Gate
{
    std::mutex mutex;
    std::condition_variable changed;
    bool entered = false, released = false, timed_out = false;
    void enter()
    {
        std::unique_lock lock(mutex);
        entered = true;
        changed.notify_all();
        if (!changed.wait_for(lock, 15s, [&] { return released; }))
            timed_out = true;
    }
    void wait()
    {
        std::unique_lock lock(mutex);
        check(changed.wait_for(lock, 15s, [&] { return entered; }), "actual SQLite hook reached");
    }
    void release()
    {
        std::lock_guard lock(mutex);
        released = true;
        changed.notify_all();
    }
};
struct ReleaseGate
{
    std::shared_ptr<Gate> gate;
    ~ReleaseGate()
    {
        gate->release();
    }
};
sqlite3* handle(const std::shared_ptr<Session>& session)
{
    auto* result = static_cast<sqlite3*>(session->connection->test_sqlite_handle());
    check(result != nullptr, "borrowed real SQLite test handle");
    return result;
}
void cancellation(const std::filesystem::path& file)
{
    Scope scope(configuration(file.string()));
    auto session = scope.open();
    mark(session);
    {
        auto rows = stream(session, "SELECT 1 UNION ALL SELECT 2");
        auto retained = rows.cursor->fetch(token(), 1, 65536);
        auto canceled = token();
        canceled->request();
        check(rejects([&] { (void)rows.cursor->fetch(canceled, 1, 65536); }).status == NW_INVALID_STATE,
              "live batch applies backpressure before another fetch");
        const auto copy = retained->bytes;
        retained.reset();
        check(rejects([&] { (void)rows.cursor->fetch(canceled, 1, 65536); }).status == NW_CANCELED,
              "cancellation before the next fetch never advances SQLite");
        check(integer(*Batch::parse(copy, rows.cursor->expectation).cell(0, 0)) == 1,
              "already delivered row remains owned after cancellation");
    }
    same(session);
    auto gate = std::make_shared<Gate>();
    struct Comparison
    {
        std::shared_ptr<Gate> gate;
        std::atomic<bool> first{true};
    };
    auto* comparison = new Comparison{gate};
    const auto status = sqlite3_create_collation_v2(
        handle(session), "b03_worker_gate", SQLITE_UTF8, comparison,
        [](void* cookie, int left_size, const void* left, int right_size, const void* right)
        {
            auto& comparison = *static_cast<Comparison*>(cookie);
            if (comparison.first.exchange(false))
                comparison.gate->enter();
            return std::string_view(static_cast<const char*>(left), static_cast<std::size_t>(left_size))
                .compare(std::string_view(static_cast<const char*>(right), static_cast<std::size_t>(right_size)));
        },
        [](void* cookie) { delete static_cast<Comparison*>(cookie); });
    if (status != SQLITE_OK)
        delete comparison;
    check(status == SQLITE_OK, "actual SQLite collation installed");
    auto canceled = token();
    auto worker =
        std::async(std::launch::async,
                   [&]
                   {
                       auto rows = session->connection->query(
                           "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE "
                           "x<100000000 AND CAST(x AS TEXT) COLLATE b03_worker_gate != '') SELECT sum(x) FROM n",
                           {}, call(canceled));
                       auto error = rejects([&] { (void)rows->next({{3, {}, false}}, 1024, call(canceled)); });
                       rows->close();
                       return error;
                   });
    ReleaseGate release{gate};
    gate->wait();
    canceled->request();
    gate->release();
    check(joined(worker).status == NW_CANCELED && !gate->timed_out,
          "real in-worker cancellation interrupts and retires after explicit release");
    canceled->request();
    check(scalar(session,
                 "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<5000) SELECT sum(x) FROM n") ==
              12'502'500,
          "old cancellation cannot interrupt the fresh 5000-row generation");
    check(rejects([&] { (void)session->connection->query("SELECT 45", {}, call(canceled)); }).status == NW_CANCELED,
          "already canceled command rejected before dispatch");
    same(session);
}
void install_counter(const std::shared_ptr<Session>& session, std::atomic<std::size_t>& count)
{
    check(sqlite3_create_function_v2(
              handle(session), "b03_count_row", 1, SQLITE_UTF8, &count,
              [](sqlite3_context* context, int, sqlite3_value** values)
              {
                  ++*static_cast<std::atomic<std::size_t>*>(sqlite3_user_data(context));
                  sqlite3_result_value(context, values[0]);
              },
              nullptr, nullptr, nullptr) == SQLITE_OK,
          "real provider row counter installed");
}
const std::string row_sql =
    "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<10000) SELECT b03_count_row(x) FROM n";
void early_stream(const std::filesystem::path& file)
{
    std::atomic<std::size_t> advanced{0};
    Scope scope(configuration(file.string()));
    auto session = scope.open();
    mark(session);
    auto described = session->connection->query("SELECT value AS exact_name FROM physical_marker WHERE 0", {}, call());
    check(described->columns().size() == 1 && described->columns()[0].name == "exact_name" &&
              described->columns()[0].type_name == std::optional<std::string>("INTEGER"),
          "zero-row declared column metadata");
    described->close();
    install_counter(session, advanced);
    {
        auto rows = stream(session, row_sql);
        auto buffer = rows.cursor->fetch(token(), 1, 65536);
        check(integer(*Batch::parse(buffer->bytes, rows.cursor->expectation).cell(0, 0)) == 1, "first streamed row");
        check(advanced == 1, "provider has not loaded the complete result");
        rows.close();
        check(rows.cursor->retired.load() && !session->active.load(),
              "early close finalizes without draining consumer");
        check(advanced == 1, "early close does not step remaining rows");
    }
    same(session);
}
void pool_retirement(const std::filesystem::path& file)
{
    Scope scope(configuration(file.string()));
    auto session = scope.open();
    mark(session);
    execute(session, "CREATE TABLE items(id INTEGER)");
    auto tx = begin(session);
    execute(session, "INSERT INTO items VALUES (1)");
    tx->request_close();
    tx->join();
    check(tx->outcome == 2 && !tx->attempted && tx->retired, "close rolls back an uncommitted transaction");
    close_session(session);
    auto next = scope.open();
    same(next);
    check(scalar(next, "SELECT count(*) FROM items") == 0, "rollback visible after physical pool reuse");
    for (int i = 0; i < 8; ++i)
    {
        execute(next, "SELECT 1");
        check(!next->active.load(), "single-writer reservation retires before sequential caller resumes");
        finish(begin(next), false);
    }
    auto held = DbOperation::create(scope.source, 0, false, [](auto) -> Value { return std::int64_t{0}; });
    auto closing = std::async(std::launch::async, [&] { scope.source->join(); });
    eventually([&] { return scope.source->closing.load(); }, "source close admitted");
    check(closing.wait_for(0ms) != std::future_status::ready, "pool close waits for its retained native operation");
    (void)rejects([&] { scope.source->admit(); });
    held.reset();
    joined(closing);
    check(scope.source->leased == 0 && scope.source->idle.empty(), "closed source retains no physical connections");
}
void lost_commit(const std::filesystem::path& file)
{
    Scope scope(configuration(file.string()));
    auto session = scope.open();
    mark(session);
    execute(session, "CREATE TABLE durable_items(id INTEGER)");
    auto options = transaction_options();
    options.commit_timeout_ns = 20'000'000;
    auto tx = begin(session, options);
    execute(session, "INSERT INTO durable_items VALUES (99)");
    auto gate = std::make_shared<Gate>();
    sqlite3_commit_hook(
        handle(session),
        [](void* cookie)
        {
            static_cast<Gate*>(cookie)->enter();
            return 0;
        },
        gate.get());
    auto waiter = std::async(std::launch::async,
                             [&] { return rejects([&] { (void)tx->finish(true, token(), session->admit(true)); }); });
    ReleaseGate release{gate};
    gate->wait();
    check(joined(waiter).code == 9, "commit deadline publishes outcome unknown");
    {
        std::lock_guard lock(tx->mutex);
        check(tx->outcome == 3 && tx->attempted && !tx->retired && tx->disposition == 2 && tx->cleanup == 1,
              "unknown commit retains the quarantined physical owner until real completion");
    }
    check(session->active.load() && session->connection != nullptr, "provider call remains owned after public timeout");
    gate->release();
    tx->join();
    check(tx->outcome == 3 && tx->retired && tx->disposition == 0 && !gate->timed_out,
          "late confirmation never rewrites published Unknown");
    sqlite3_commit_hook(handle(session), nullptr, nullptr);
    same(session);
    auto independent = ProviderConnection::open(scope.source->config.provider, call());
    check(text_scalar(*independent, "SELECT count(*) FROM durable_items WHERE id=99") == "1",
          "independent connection verifies committed data despite unknown public result");
    independent->close();
}
void admission(const std::filesystem::path& file)
{
    const auto options = option_record({});
    for (auto timeout : {std::uint64_t{0}, std::numeric_limits<std::uint64_t>::max()})
    {
        (void)rejects([&] { (void)configuration(file.string(), options, 1, timeout); });
        check(!std::filesystem::exists(file), "invalid acquisition bound fails before SQLite I/O");
    }
    auto config = configuration(file.string());
    config.limits.nodes = 2;
    config.limits.waiters = 1;
    config.limits.parameter_bytes = 1024;
    Scope scope(config);
    auto session = scope.open();
    auto rows = stream(session, "SELECT 1 UNION ALL SELECT 2");
    check(rejects([&] { (void)session->admit(false); }).code == 7,
          "single-session gate rejects competing commands before retaining another provider input");
    std::vector<std::shared_ptr<DbOperation>> held;
    const auto maximum = config.limits.nodes + config.limits.connections + config.limits.waiters;
    for (std::size_t i = 0; i < maximum; ++i)
        held.push_back(DbOperation::create(scope.source, 0, false, [](auto) -> Value { return std::int64_t{0}; }));
    check(rejects(
              [&]
              {
                  (void)DbOperation::create(scope.source, 0, false, [](auto) -> Value { return std::int64_t{0}; });
              }).code == 6,
          "source operation count is bounded");
    held.clear();
    check(scope.source->pending == 0, "created operation reservations released without dispatch");
    auto payload = Batch::encode({1, Profile::Sqlite, {}, 0, 0, {}, 1}, 1, {});
    (void)rejects([&] { (void)SqlPlan::parse(scope.source, bytes(std::string(1025, ' ')), payload, {}); });
    check(!session->closing.load(), "capacity rejection does not close a healthy session");
}
void concurrency(const std::filesystem::path& file)
{
    ProviderOptions options;
    options.sqlite_begin = 1;
    options.sqlite_busy_timeout_ns = 25'000'000;
    Scope scope(configuration(file.string(), options, 2));
    auto first = scope.open();
    auto second = scope.open();
    mark(first);
    mark(second);
    execute(first, "CREATE TABLE once_only(scope TEXT,key TEXT,hash TEXT,PRIMARY KEY(scope,key))");
    auto tx = begin(first);
    execute(first, "INSERT INTO once_only VALUES ('s','k','h')");
    check(scalar(second, "SELECT count(*) FROM once_only") == 0, "WAL reader sees committed snapshot");
    const auto error = rejects([&] { (void)begin(second); });
    check(error.provider && std::stoi(*error.provider) % 256 == SQLITE_BUSY && second->retired.load(),
          "second IMMEDIATE writer waits then discards its failed transaction lease");
    finish(tx, true);
    second = scope.open();
    check(scalar(second, "SELECT count(*) FROM sqlite_temp_master WHERE name='physical_marker'") == 0,
          "replacement connection is not misreported as physical reuse");
    mark(second);
    auto contender = begin(second);
    check(execute(second, "INSERT INTO once_only VALUES ('s','k','h') ON CONFLICT(scope,key) DO NOTHING") == 0,
          "duplicate idempotency record affects zero rows");
    check(scalar(second, "SELECT count(*) FROM once_only WHERE scope='s' AND key='k' AND hash='h'") == 1,
          "idempotency record is durable exactly once");
    finish(contender, true);
    same(first);
    same(second);
}
AdministrationRequest migration(const std::vector<std::string>& scripts)
{
    Bytes sequence_bytes = u32_bytes(static_cast<std::uint32_t>(scripts.size()));
    for (std::size_t i = 0; i < scripts.size(); ++i)
    {
        const auto hash = sha256(bytes(scripts[i]));
        const auto entry = encode_control(11, {{1, 2, u64_bytes(i + 1)},
                                               {2, 4, bytes("B03 native substrate probe")},
                                               {3, 4, bytes(scripts[i])},
                                               {4, 1, u32_bytes(0)},
                                               {5, 5, Bytes(hash.begin(), hash.end())}});
        const auto length = u32_bytes(static_cast<std::uint32_t>(entry.size()));
        sequence_bytes.insert(sequence_bytes.end(), length.begin(), length.end());
        sequence_bytes.insert(sequence_bytes.end(), entry.begin(), entry.end());
    }
    const auto hash = sha256(encode_control(12, {{1, 1, u32_bytes(1)}, {2, 5, sequence_bytes}}));
    auto bundle =
        encode_control(12, {{1, 1, u32_bytes(1)}, {2, 5, sequence_bytes}, {3, 5, Bytes(hash.begin(), hash.end())}});
    return parse_administration(
        Profile::Sqlite, 0, encode_control(5, {{1, 1, u32_bytes(1)}, {2, 5, bundle}, {4, 3, u64_bytes(WAIT_NS)}}), {});
}
void migrations(const std::filesystem::path& file)
{
    Scope scope(configuration(file.string()));
    auto session = scope.open();
    auto& connection = *session->connection;
    const std::string create = "CREATE TABLE migrated(id INTEGER PRIMARY KEY)";
    const auto good = migration({create});
    (void)administer(good, connection, {}, call());
    check(rejects(
              [&]
              {
                  (void)administer(migration({"CREATE TABLE migrated(id INTEGER PRIMARY KEY, changed TEXT)"}),
                                   connection, {}, call());
              }).code == 15,
          "SQLx migration checksum mismatch is retained");
    (void)rejects(
        [&]
        {
            (void)administer(
                migration({create,
                           "CREATE TABLE must_rollback(id INTEGER); INSERT INTO table_that_does_not_exist VALUES (1)"}),
                connection, {}, call());
        });
    auto independent = ProviderConnection::open(scope.source->config.provider, call());
    check(text_scalar(*independent, "SELECT count(*) FROM sqlite_master WHERE name='must_rollback'") == "0",
          "failed migration schema rolled back atomically");
    check(text_scalar(*independent, "SELECT count(*) FROM _sqlx_migrations WHERE success=1 AND length(checksum)=48") ==
              "1",
          "original SHA384 migration history remains durable");
    independent->close();
    (void)administer(good, connection, {}, call());
}
void read_only(const std::filesystem::path& file)
{
    Scope scope(configuration(file.string()));
    auto session = scope.open();
    execute(session, "CREATE TABLE readonly_probe(id INTEGER)");
    auto options = transaction_options();
    options.isolation = 1;
    check(rejects([&] { (void)begin(session, options); }).status == NW_NOT_SUPPORTED,
          "SQLite isolation capability rejection");
    options.isolation = 0;
    options.read_only = true;
    auto tx = begin(session, options);
    (void)rejects([&] { execute(session, "INSERT INTO readonly_probe VALUES (1)"); });
    finish(tx, false);
    auto next = begin(session);
    execute(session, "INSERT INTO readonly_probe VALUES (2)");
    finish(next, true);
    check(scalar(session, "SELECT count(*) FROM readonly_probe") == 1,
          "read-only PRAGMA restored for next transaction");
}
void last_parent(const std::filesystem::path& file)
{
    Scope scope(configuration(file.string()));
    auto session = scope.open();
    execute(session, "CREATE TABLE final_parent(id INTEGER)");
    auto commit_gate = std::make_shared<Gate>();
    auto close_gate = std::make_shared<Gate>();
    auto* retained_gate = new std::shared_ptr<Gate>(close_gate);
    check(sqlite3_create_collation_v2(
              handle(session), "b03_close_gate", SQLITE_UTF8, retained_gate,
              [](void*, int, const void*, int, const void*) { return 0; },
              [](void* cookie)
              {
                  std::unique_ptr<std::shared_ptr<Gate>> owner(static_cast<std::shared_ptr<Gate>*>(cookie));
                  (*owner)->enter();
              }) == SQLITE_OK,
          "actual ConnectionState destructor barrier installed");
    auto tx = begin(session);
    execute(session, "INSERT INTO final_parent VALUES (1)");
    sqlite3_commit_hook(
        handle(session),
        [](void* cookie)
        {
            static_cast<Gate*>(cookie)->enter();
            return 0;
        },
        commit_gate.get());
    auto committing = std::async(std::launch::async, [&] { (void)tx->finish(true, token(), session->admit(true)); });
    ReleaseGate release_commit{commit_gate};
    ReleaseGate release_close{close_gate};
    commit_gate->wait();
    // Disposal follows admitted COMMIT. Discard forces the real close path so
    // the public retirement receipt cannot get ahead of sqlite3_close_v2.
    session->discard.store(true);
    session->request_close();
    commit_gate->release();
    close_gate->wait();
    bool retired;
    {
        std::lock_guard lock(tx->mutex);
        retired = tx->retired;
    }
    check(!retired && !session->retired.load(), "last parent transaction waits for actual physical close");
    {
        std::lock_guard lock(scope.source->mutex);
        check(scope.source->leased == 1, "physical lease stays counted through the destructor barrier");
    }
    close_gate->release();
    joined(committing);
    tx->join();
    session->join();
    check(tx->outcome == 1 && tx->retired && session->retired.load() && !close_gate->timed_out,
          "retirement follows confirmed commit and completed physical close");
}
void row_handoff(const std::filesystem::path& file)
{
    std::atomic<std::size_t> advanced{0};
    Scope scope(configuration(file.string()));
    auto session = scope.open();
    mark(session);
    install_counter(session, advanced);
    for (int round = 0; round < 4; ++round)
    {
        advanced = 0;
        auto rows = stream(session, row_sql);
        std::size_t count = 0;
        for (;;)
        {
            auto buffer = rows.cursor->fetch(token(), 2, 65536);
            const auto batch = Batch::parse(buffer->bytes, rows.cursor->expectation);
            check(batch.rows() <= 2 && advanced - count <= 2, "provider plus receiver handoff remains bounded");
            if (batch.rows() == 0)
                break;
            for (std::uint32_t row = 0; row < batch.rows(); ++row)
                check(integer(*batch.cell(row, 0)) == static_cast<std::int64_t>(++count), "ordered streaming row");
            check(rejects([&] { (void)rows.cursor->fetch(token(), 2, 65536); }).status == NW_INVALID_STATE,
                  "live batch supplies deterministic receiver backpressure");
            if (count % 7 == 0)
                std::this_thread::yield();
        }
        rows.close();
        check(count == 10000 && advanced == 10000 && !session->active.load(),
              "all rows retired without read-ahead materialization");
    }
    same(session);
}
void run_suite(const std::filesystem::path& root)
{
    std::filesystem::create_directories(root);
    for (std::uint32_t i = 1; i <= 14; ++i)
    {
        const auto file = root / ("case-" + std::to_string(i) + ".db");
        switch (i)
        {
            case 1:
                profile(file);
                break;
            case 2:
                memory();
                break;
            case 3:
                codecs(file);
                break;
            case 4:
                savepoints(file);
                break;
            case 5:
                cancellation(file);
                break;
            case 6:
                early_stream(file);
                break;
            case 7:
                pool_retirement(file);
                break;
            case 8:
                lost_commit(file);
                break;
            case 9:
                admission(file);
                break;
            case 10:
                concurrency(file);
                break;
            case 11:
                migrations(file);
                break;
            case 12:
                read_only(file);
                break;
            case 13:
                last_parent(file);
                break;
            case 14:
                row_handoff(file);
                break;
        }
        check(source_count == 0 && session_count == 0 && cursor_count == 0 && buffer_count == 0 && operation_count == 0,
              "scenario leaked native owners");
        std::cout << "PASS B03 native scenario " << i << std::endl;
    }
    std::cout << "PASS B03 C++ assertions=" << assertions.load() << std::endl;
}
std::string path(const std::uint8_t* data, std::uint32_t length)
{
    if (data == nullptr || length == 0 || length > MAX_BYTES)
        throw Error::invalid("fixture borrowed path");
    const ByteView value(data, length);
    if (!valid_utf8(value) || std::find(value.begin(), value.end(), 0) != value.end())
        throw Error::invalid("fixture UTF-8 path");
    return {value.begin(), value.end()};
}
} // namespace

NW_FIXTURE_API nw_status nw_sqlite_get_abi_v1(std::uint32_t major, std::uint32_t capacity,
                                              nw_abi_info_v1* output) noexcept
{
    return nw::interop::write_abi(major, capacity, output, 0, 1);
}
NW_FIXTURE_API std::uint32_t nw_b03_runtime_count()
{
    return runtimes.load();
}
NW_FIXTURE_API std::uint32_t nw_b03_run_suite(const std::uint8_t* root, std::uint32_t length)
{
    std::string directory;
    try
    {
        directory = path(root, length);
    }
    catch (...)
    {
        return 1;
    }
    try
    {
        ++runtimes;
        run_suite(directory);
        return 0;
    }
    catch (const Error& error)
    {
        std::cerr << "B03 C++ provider suite error: " << error.what() << " code=" << error.code << std::endl;
        return 2;
    }
    catch (const std::exception& error)
    {
        std::cerr << "B03 C++ provider suite failure: " << error.what() << std::endl;
        return 2;
    }
    catch (...)
    {
        return 3;
    }
}
NW_FIXTURE_API std::uint32_t nw_b03_probe_options(const std::uint8_t* root, std::uint32_t root_length,
                                                  const std::uint8_t* record, std::uint32_t record_length,
                                                  std::uint64_t* observed)
{
    HostConfig config;
    try
    {
        if (observed == nullptr || reinterpret_cast<std::uintptr_t>(observed) % alignof(std::uint64_t) != 0 ||
            record == nullptr || record_length == 0 || record_length > MAX_BYTES)
            return 1;
        config = configuration(path(root, root_length), ByteView(record, record_length));
    }
    catch (...)
    {
        return 1;
    }
    try
    {
        ++runtimes;
        auto connection = ProviderConnection::open(config.provider, call());
        const auto busy = std::stoull(text_scalar(*connection, "PRAGMA busy_timeout"));
        const auto foreign_keys = std::stoull(text_scalar(*connection, "PRAGMA foreign_keys"));
        const auto synchronous = std::stoull(text_scalar(*connection, "PRAGMA synchronous"));
        const auto mode = text_scalar(*connection, "PRAGMA journal_mode");
        const auto journal = mode == "wal" ? 0U : mode == "delete" ? 1U : mode == "memory" ? 2U : 99U;
        check(journal != 99 && text_scalar(*connection, "SELECT sqlite_version()") == "3.51.3",
              "observed SQLite profile");
        connection->close();
        *observed = (busy << 32) | journal | (synchronous << 8) | (foreign_keys << 16);
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << "B03 C++ options probe failed: " << error.what() << std::endl;
        return 2;
    }
    catch (...)
    {
        return 3;
    }
}
