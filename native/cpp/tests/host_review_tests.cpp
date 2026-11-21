// SPDX-License-Identifier: MIT
// Independent DB-CPP-01 review regressions. Link host.cpp and provider.cpp with
// the common native sources; this test does not need the public ABI entry points.
#include "nw/host.hpp"

#include <filesystem>
#include <iostream>
#include <sqlite3.h>
#include <stdexcept>

namespace nw
{
std::uint64_t module_identity()
{
    return 0x4442435050303101ULL;
}
} // namespace nw

namespace
{
using namespace nw;
void check(bool value, std::string_view message)
{
    if (!value)
        throw std::runtime_error(std::string(message));
}
Bytes bytes(std::string_view value)
{
    return {value.begin(), value.end()};
}
ControlField number(std::uint16_t id, std::uint32_t value)
{
    return {id, 1, u32_bytes(value)};
}
HostConfig configuration(std::string path = ":memory:")
{
    HostConfig config{};
    config.provider.profile = Profile::Sqlite;
    config.provider.connection = std::move(path);
    config.provider.sqlite_mode = config.provider.connection == ":memory:" ? 3 : 0;
    config.provider.sqlite_journal = config.provider.sqlite_mode == 3 ? 2 : 1;
    config.provider.sqlite_busy_timeout_ns = 1'000'000;
    config.name = "host-review";
    config.limits = {1,    16,        64, 65536,     64, 65536,         65536,        4096,
                     4096, MAX_BYTES, 64, MAX_BYTES, 64, 1'000'000'000, 5'000'000'000};
    return config;
}
std::shared_ptr<Session> open_session(const std::shared_ptr<Source>& source)
{
    {
        std::lock_guard lock(source->mutex);
        ++source->leased; // Mirrors the source admission dispatcher reservation.
    }
    return Session::open_reserved(source, std::make_shared<Cancel>());
}
void close_session(const std::shared_ptr<Session>& session)
{
    session->request_close();
    session->join();
}

void parameter_name()
{
    auto source = Source::create(configuration());
    auto definition = encode_control(21, {number(1, 1), {2, 4, bytes("p0")}, number(3, 3), number(4, 0)});
    auto definitions = u32_bytes(1);
    auto length = u32_bytes(static_cast<std::uint32_t>(definition.size()));
    definitions.insert(definitions.end(), length.begin(), length.end());
    definitions.insert(definitions.end(), definition.begin(), definition.end());
    auto schema = bytes("{\"version\":1,\"parameters\":[{\"id\":1,\"name\":\"p0\",\"codec\":3,"
                        "\"codec_config\":null,\"nullable\":false}]}");
    Expectation expected{1, Profile::Sqlite, sha256(schema), 0, 0, {{1, {3, {}, false}, 3}}, 1};
    auto payload = Batch::encode(expected, 1, {u64_bytes(7)});
    auto options = encode_control(20, {number(1, 1),
                                       number(2, 0),
                                       {3, 3, u64_bytes(1'000'000'000)},
                                       number(4, 0),
                                       {5, 5, definitions},
                                       number(6, 0)});
    auto plan = SqlPlan::parse(source, bytes("INSERT INTO t VALUES (@p0)"), payload, options);
    check(plan->sql == "INSERT INTO t VALUES (?1)", "generated p0 parameter was not accepted");
    source->join();
}

void retired_savepoint_dispose()
{
    // No SQL is needed: the token was already released or the transaction retired.
    auto session = std::make_shared<Session>();
    ++session_count;
    auto transaction = std::make_shared<Transaction>();
    transaction->session = session;
    transaction->retired = true;
    session->transaction = transaction;
    session->active.store(true);
    auto gate = std::make_shared<CommandGate>(session);
    auto receipt = transaction->savepoint_action(1, 0, std::make_shared<Cancel>(), gate, true);
    check(!receipt.empty() && !session->active.load(), "retired savepoint cleanup did not finish");
}

void cursor_end_once()
{
    auto source = Source::create(configuration());
    auto session = open_session(source);
    auto projection = std::make_shared<Projection>(
        Projection::parse(bytes("{\"version\":1,\"fields\":[{\"ordinal\":0,\"name\":\"v\",\"codec\":3,"
                                "\"codec_config\":null,\"nullable\":false}]}"),
                          Profile::Sqlite));
    auto binding = source->resolve_projection({source->identity, 1, projection, source->codecs()});
    auto plan = std::make_shared<SqlPlan>();
    plan->source = source;
    plan->sql = "SELECT 1 AS \"v\"";
    plan->expectation = {1, Profile::Sqlite, {}, 0, 0, {}, 1};
    plan->parameters = Batch::encode(plan->expectation, 1, {});
    plan->projection = binding;
    plan->result_kind = 3;
    plan->timeout_ns = 5'000'000'000;
    auto value = session->execute(plan, std::make_shared<Cancel>(), session->admit(false));
    auto owner = std::get<std::shared_ptr<Owner>>(value);
    auto cursor = std::get<std::shared_ptr<Cursor>>(owner->data);
    auto first = cursor->fetch(std::make_shared<Cancel>(), 2, 65536);
    check(Batch::parse(first->bytes, cursor->expectation).rows() == 1, "first row missing");
    first.reset();
    auto end = cursor->fetch(std::make_shared<Cancel>(), 2, 65536);
    check(Batch::parse(end->bytes, cursor->expectation).rows() == 0, "end batch missing");
    end.reset();
    bool rejected = false;
    try
    {
        (void)cursor->fetch(std::make_shared<Cancel>(), 2, 65536);
    }
    catch (const Error& error)
    {
        rejected = error.status == NW_INVALID_STATE;
    }
    cursor->request_close();
    cursor->join();
    close_session(session);
    source->join();
    check(rejected, "cursor transferred EOF more than once");
}

void failed_begin_discard()
{
    auto path = std::filesystem::temp_directory_path() /
                ("nativeweave-host-review-" + std::to_string(Clock::now().time_since_epoch().count()) + ".sqlite");
    auto config = configuration(path.string());
    config.provider.sqlite_begin = 1;
    auto source = Source::create(config);
    auto session = open_session(source);
    const CallContext call{{}, Clock::now() + std::chrono::seconds(5), false};
    (void)session->connection->execute("CREATE TABLE t(v INTEGER)", {}, call);
    auto blocker = ProviderConnection::open(config.provider, call);
    (void)blocker->execute("BEGIN IMMEDIATE", {}, call);
    bool rejected = false;
    try
    {
        TransactionOptions options{0, true, 1'000'000'000, 1'000'000'000, {}};
        (void)Transaction::begin(session, options, std::make_shared<Cancel>(), session->admit(true));
    }
    catch (const Error& error)
    {
        rejected = error.status == NW_DOMAIN_ERROR;
    }
    const bool discarded = session->discard.load() || !session->connection || !session->connection->healthy();
    close_session(session);
    (void)blocker->execute("ROLLBACK", {}, call);
    blocker->close();
    auto next = open_session(source);
    (void)next->connection->execute("INSERT INTO t VALUES (1)", {}, call);
    close_session(next);
    source->join();
    std::filesystem::remove(path);
    check(rejected && discarded, "failed BEGIN reused an unconfirmed provider session");
}

struct BlockingPoint
{
    std::mutex mutex;
    std::condition_variable changed;
    bool entered = false, released = false;
    void arrive()
    {
        std::unique_lock lock(mutex);
        entered = true;
        changed.notify_all();
        changed.wait(lock, [&] { return released; });
    }
    void await()
    {
        std::unique_lock lock(mutex);
        check(changed.wait_for(lock, std::chrono::seconds(3), [&] { return entered; }),
              "actual worker or provider close did not enter");
    }
    void release()
    {
        std::lock_guard lock(mutex);
        released = true;
        changed.notify_all();
    }
};
template <typename F> void eventually(F&& condition, std::string_view message)
{
    const auto deadline = Clock::now() + std::chrono::seconds(3);
    while (!condition())
    {
        check(Clock::now() < deadline, message);
        std::this_thread::yield();
    }
}
nw_status NW_CDECL operation_notice(std::uint64_t, std::uint32_t, const nw_error_info_v1*)
{
    return NW_OK;
}
void start(const std::shared_ptr<DbOperation>& operation)
{
    check(operation->start({sizeof(nw_operation_callbacks_v1), 0, 1, operation_notice}) == NW_OK,
          "actual asynchronous operation start failed");
}
void terminal(const std::shared_ptr<DbOperation>& operation, nw_status status, std::uint32_t code = 0)
{
    eventually([&] { return operation->core->snapshot().terminal_status.has_value(); },
               "actual operation did not reach a terminal result");
    check(operation->core->snapshot().terminal_status == status, "actual operation terminal category differs");
    eventually([&] { return operation->core->try_retire() == NW_OK; }, "actual operation source did not retire");
    if (code)
        check(operation->error && operation->error->code == code, "actual operation error code differs");
}
std::shared_ptr<DbOperation> administration(const std::shared_ptr<Source>& source,
                                            std::function<Value(std::shared_ptr<Cancel>)> work)
{
    auto operation = DbOperation::create(source, 102, false, std::move(work), DbOperation::Dispatch::Administration);
    start(operation);
    return operation;
}

void administrative_admission()
{
    auto config = configuration();
    // Four waiters exceed the three provider workers. A worker-blocking gate
    // would hide excess work in that pool and cannot pass this actual start path.
    config.limits.waiters = 4;
    config.limits.admission_ns = 200'000'000;
    auto source = Source::create(config);
    auto active_gate = std::make_shared<BlockingPoint>();
    auto held = administration(source,
                               [config, active_gate](auto cancel) -> Value
                               {
                                   auto connection = ProviderConnection::open(config.provider, {cancel});
                                   active_gate->arrive();
                                   connection->close();
                                   CallContext{cancel}.check();
                                   return std::int64_t{1};
                               });
    active_gate->await();
    std::array<std::shared_ptr<BlockingPoint>, 2> workers;
    std::vector<std::shared_ptr<DbOperation>> ordinary;
    for (auto& gate : workers)
    {
        gate = std::make_shared<BlockingPoint>();
        auto operation = DbOperation::create(source, 102, false,
                                             [gate](auto) -> Value
                                             {
                                                 gate->arrive();
                                                 return std::int64_t{2};
                                             });
        start(operation);
        gate->await();
        ordinary.push_back(std::move(operation));
    }
    std::atomic<std::size_t> effects{0};
    const auto unexpected = [&](auto) -> Value
    {
        ++effects;
        return std::int64_t{3};
    };
    std::vector<std::shared_ptr<DbOperation>> waiting;
    for (std::size_t i = 0; i < config.limits.waiters; ++i)
        waiting.push_back(administration(source, unexpected));
    {
        std::lock_guard lock(source->mutex);
        check(source->administration_active && source->administration_admissions.size() == config.limits.waiters,
              "administrative start did not charge every bounded waiter");
    }
    auto overflow = administration(source, unexpected);
    check(overflow->core->snapshot().terminal_status == NW_DOMAIN_ERROR && overflow->error->code == 6,
          "excess administrative waiter hid in a saturated worker queue");
    (void)waiting.front()->core->request_cancel();
    waiting.front()->cancel->request();
    terminal(waiting.front(), NW_CANCELED);
    for (std::size_t i = 1; i < waiting.size(); ++i)
        terminal(waiting[i], NW_TIMEOUT, 1);
    check(effects.load() == 0, "canceled or timed-out administrative waiter reached work");
    // Neither cancellation nor the admission deadline needs a free worker.
    for (auto& gate : workers)
        gate->release();
    for (const auto& operation : ordinary)
        terminal(operation, NW_OK);
    auto close_waiter = administration(source, unexpected);
    auto late_start = DbOperation::create(source, 102, false, unexpected, DbOperation::Dispatch::Administration);
    source->request_close();
    check(held->cancel->requested(), "source close missed the active administrative cancel owner");
    terminal(close_waiter, NW_INVALID_STATE, 8);
    start(late_start);
    terminal(late_start, NW_INVALID_STATE, 8);
    active_gate->release();
    terminal(held, NW_CANCELED);
    terminal(overflow, NW_DOMAIN_ERROR, 6);
    held.reset();
    close_waiter.reset();
    late_start.reset();
    overflow.reset();
    waiting.clear();
    ordinary.clear();
    source->join();
    check(source->administration_admissions.empty() && !source->administration_active && effects.load() == 0,
          "administrative shutdown retained a waiter/permit or dispatched closed work");
}

void administrative_physical_close()
{
    auto config = configuration();
    auto source = Source::create(config);
    auto body = std::make_shared<BlockingPoint>();
    auto physical_close = std::make_shared<BlockingPoint>();
    auto held = administration(source,
                               [config, body, physical_close](auto cancel) -> Value
                               {
                                   auto connection = ProviderConnection::open(config.provider, {cancel});
                                   auto cookie = std::make_unique<std::shared_ptr<BlockingPoint>>(physical_close);
                                   check(sqlite3_create_collation_v2(
                                             static_cast<sqlite3*>(connection->test_sqlite_handle()), "nw_admin_close",
                                             SQLITE_UTF8, cookie.get(),
                                             [](void*, int, const void*, int, const void*) { return 0; },
                                             [](void* value)
                                             {
                                                 std::unique_ptr<std::shared_ptr<BlockingPoint>> gate(
                                                     static_cast<std::shared_ptr<BlockingPoint>*>(value));
                                                 (*gate)->arrive();
                                             }) == SQLITE_OK,
                                         "real SQLite physical-close gate registration failed");
                                   (void)cookie.release();
                                   body->arrive();
                                   connection->close();
                                   return std::int64_t{1};
                               });
    body->await();
    std::atomic<std::size_t> effects{0};
    auto next = administration(source,
                               [config, &effects](auto cancel) -> Value
                               {
                                   auto connection = ProviderConnection::open(config.provider, {cancel});
                                   auto rows = connection->query_text("SELECT 7", {cancel});
                                   connection->close();
                                   check(rows.size() == 1 && rows[0][0] == "7", "next dedicated connection failed");
                                   ++effects;
                                   return std::int64_t{7};
                               });
    body->release();
    physical_close->await();
    auto ordinary = DbOperation::create(source, 102, false, [](auto) -> Value { return std::int64_t{8}; });
    start(ordinary);
    terminal(ordinary, NW_OK);
    check(!held->core->snapshot().terminal_status && !next->core->snapshot().terminal_status && effects.load() == 0,
          "administrative permit released before physical close returned");
    physical_close->release();
    terminal(held, NW_OK);
    // The first terminal operation handle is deliberately still retained.
    terminal(next, NW_OK);
    check(effects.load() == 1, "retained result handle incorrectly retained the administrative permit");
    auto failed = administration(source, [](auto) -> Value
                                 { throw Error(NW_DOMAIN_ERROR, 1, "controlled administrative failure"); });
    terminal(failed, NW_DOMAIN_ERROR, 1);
    auto after_failure = administration(source, [](auto) -> Value { return std::int64_t{9}; });
    terminal(after_failure, NW_OK);
    held.reset();
    next.reset();
    ordinary.reset();
    failed.reset();
    after_failure.reset();
    source->join();
    check(!source->administration_active && source->administration_admissions.empty(),
          "administrative failure did not release its actual work permit");
}

void control_during_runtime_join()
{
    auto source = Source::create(configuration());
    auto worker_exit = std::make_shared<BlockingPoint>();
    source->workers.post([worker_exit] { worker_exit->arrive(); });
    worker_exit->await();
    std::thread joining([source] { source->join(); });
    eventually(
        [&]
        {
            std::lock_guard lock(source->mutex);
            return source->admission_stop && !source->retired.load();
        },
        "actual source join did not stop dispatch before joining workers");
    const auto caller = std::this_thread::get_id();
    std::thread::id executor;
    auto control = DbOperation::create(
        source, 102, true,
        [&](auto) -> Value
        {
            executor = std::this_thread::get_id();
            return std::int64_t{1};
        },
        DbOperation::Dispatch::Control);
    const bool uncounted = !control->counted && source->pending.load() == 0;
    start(control);
    terminal(control, NW_OK);
    const bool synchronous = executor == caller;
    worker_exit->release();
    joining.join();
    check(uncounted && synchronous && source->retired.load(),
          "control creation raced the stopped-runtime gate or queued after shutdown began");
}
} // namespace

int main(int argc, char** argv)
{
    try
    {
        const std::string mode = argc > 1 ? argv[1] : "all";
        if (mode == "all" || mode == "parameter")
            parameter_name();
        if (mode == "all" || mode == "savepoint")
            retired_savepoint_dispose();
        if (mode == "all" || mode == "cursor")
            cursor_end_once();
        if (mode == "all" || mode == "begin")
            failed_begin_discard();
        if (mode == "all" || mode == "administration")
        {
            administrative_admission();
            administrative_physical_close();
        }
        if (mode == "all" || mode == "control")
            control_during_runtime_join();
        std::cout << "host review: " << mode << " passed\n";
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << "host review failure: " << error.what() << '\n';
        return 1;
    }
}
