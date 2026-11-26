// SPDX-License-Identifier: MIT
// Real Host work and private C ABI contracts for one-shot command readiness.
#include "../../database/binding/korda_command_completion_v1.h"
#include "nw/host.hpp"
#include "nw_database_declarations.hpp"

#include <atomic>
#include <cstdlib>
#include <future>
#include <iostream>
#include <sqlite3.h>
#include <stdexcept>
#include <system_error>

extern "C" std::uint64_t NW_CDECL nw_db_diagnostic_count_v1(std::uint32_t);

namespace
{
using namespace nw;
std::atomic<std::size_t> checks{0};
constexpr auto bound = std::chrono::seconds(5);
void check(bool value, std::string_view message)
{
    ++checks;
    if (!value)
        throw std::runtime_error(std::string(message));
}
template <typename Predicate> void await(Predicate predicate, std::string_view message)
{
    const auto deadline = Clock::now() + bound;
    while (!predicate())
    {
        if (Clock::now() >= deadline)
            check(false, message);
        std::this_thread::yield();
    }
}
template <typename Action> Error reject(Action action)
{
    try
    {
        action();
    }
    catch (const Error& failure)
    {
        return failure;
    }
    throw std::runtime_error("expected a native command error");
}
Bytes bytes(std::string_view text)
{
    return {text.begin(), text.end()};
}
nw_bytes_view_v1 view(const Bytes& data)
{
    return {data.data(), data.size()};
}
nw_error_info_v1 error_slot()
{
    return {sizeof(nw_error_info_v1), 0, 0, 0, 0};
}
ControlField u32(std::uint16_t id, std::uint32_t value)
{
    return {id, 1, u32_bytes(value)};
}
ControlField u64(std::uint16_t id, std::uint64_t value, std::uint16_t type = 2)
{
    return {id, type, u64_bytes(value)};
}
bool released(ByteView record)
{
    return Fields::parse(record, 7,
                         {{1, 1, true},
                          {2, 1, true},
                          {3, 1, true},
                          {4, 1, true},
                          {5, 1, true},
                          {6, 1, true},
                          {7, 1, true},
                          {8, 2, true},
                          {9, 4, false}})
        .boolean(6);
}
struct Gate
{
    std::mutex mutex;
    std::condition_variable changed;
    bool entered = false, open = false, expired = false;
    void hold()
    {
        std::unique_lock lock(mutex);
        entered = true;
        changed.notify_all();
        if (!changed.wait_for(lock, bound, [&] { return open; }))
            expired = true;
    }
    void wait()
    {
        std::unique_lock lock(mutex);
        check(changed.wait_for(lock, bound, [&] { return entered; }), "a controlled native boundary did not enter");
    }
    void release()
    {
        std::lock_guard lock(mutex);
        open = true;
        changed.notify_all();
    }
};
struct Release
{
    Gate& gate;
    ~Release()
    {
        gate.release();
    }
};
struct Notice
{
    std::mutex mutex;
    std::condition_variable changed;
    std::atomic<unsigned> calls{0};
    bool done = false;
    std::exception_ptr failure;
    std::function<void()> inspect;
    static void NW_CDECL ready(void* context, std::uint64_t generation) noexcept
    {
        auto& self = *static_cast<Notice*>(context);
        try
        {
            check(generation == 0, "one-shot ready used a nonzero generation");
            if (self.inspect)
                self.inspect();
        }
        catch (...)
        {
            self.failure = std::current_exception();
        }
        ++self.calls;
        {
            std::lock_guard lock(self.mutex);
            self.done = true;
        }
        self.changed.notify_all();
    }
    nw_db_cursor_callbacks_v1 callbacks()
    {
        return {sizeof(nw_db_cursor_callbacks_v1), 0, this, ready};
    }
    void wait()
    {
        std::unique_lock lock(mutex);
        check(changed.wait_for(lock, bound, [&] { return done; }), "accepted command did not notify");
        if (failure)
            std::rethrow_exception(failure);
        check(calls == 1, "accepted command notified more than once");
    }
};
struct DrainCommand
{
    std::shared_ptr<CommandCompletion> command;
    ~DrainCommand()
    {
        // A failed assertion cannot retire the callback cookie while native
        // code may still use it. Release held gates before this guard unwinds.
        command->request_close();
        command->join();
    }
};
HostConfig configuration()
{
    HostConfig result{};
    result.provider.profile = Profile::Sqlite;
    result.provider.connection = ":memory:";
    result.provider.sqlite_mode = 3;
    result.provider.sqlite_journal = 2;
    result.name = "command-completion-contract";
    result.limits = {1,    8,         64, 65536,     1,  65536,         65536,         4096,
                     4096, MAX_BYTES, 64, MAX_BYTES, 64, 5'000'000'000, 30'000'000'000};
    return result;
}
struct Scope
{
    std::shared_ptr<Source> source;
    std::shared_ptr<Session> session;
    explicit Scope(HostConfig config = configuration()) : source(Source::create(std::move(config)))
    {
        {
            std::lock_guard lock(source->mutex);
            ++source->leased;
        }
        session = Session::open_reserved(source, std::make_shared<Cancel>());
    }
    ~Scope()
    {
        source->workers.test_before_post = {};
        session->request_close();
        session->join();
        source->join();
    }
    std::shared_ptr<SqlPlan> plan(std::string sql, bool count = false)
    {
        auto value = std::make_shared<SqlPlan>();
        value->source = source;
        value->sql = std::move(sql);
        value->kind = count ? 0 : 1;
        value->result_kind = count ? 1 : 0;
        value->scalar_count = count;
        value->timeout_ns = 30'000'000'000;
        value->expectation = {1, source->config.provider.profile, {}, 0, 0, {}, 1};
        value->capture_parameters(Batch::encode(value->expectation, 1, {}));
        if (count)
        {
            auto projection = std::make_shared<Projection>(Projection::parse_scalar(
                text_view("{\"version\":1,\"fields\":[{\"ordinal\":0,\"name\":\"\",\"codec\":3,"
                          "\"codec_config\":null,\"nullable\":false}]}"),
                source->config.provider.profile));
            value->projection = source->resolve_projection({source->identity, 1, projection, source->codecs()});
        }
        return value;
    }
    std::shared_ptr<CommandCompletion> command(std::shared_ptr<SqlPlan> plan, Notice& notice)
    {
        auto gate = session->admit(false);
        auto work = [session = session, plan = std::move(plan), gate](auto cancel) -> Value
        {
            if (plan->scalar_count)
                return session->execute_scalar_count(plan, cancel, gate);
            return session->execute(plan, cancel, gate);
        };
        return CommandCompletion::create(source, 100, false, std::move(work), notice.callbacks(), std::move(gate));
    }
};
void zero()
{
    check(source_count == 0 && session_count == 0 && operation_count == 0 && cursor_count == 0 && buffer_count == 0 &&
              pull_count == 0 && command_count == 0,
          "command fixture retained a native owner or notification tail");
}

void early_ready_and_tail()
{
    Scope scope;
    Notice notice;
    Gate tail;
    auto plan = scope.plan("CREATE TABLE ready_values(v BIGINT)");
    std::weak_ptr<SqlPlan> weak_plan = plan;
    auto command = scope.command(std::move(plan), notice);
    DrainCommand drain{command};
    auto* raw = command.get();
    notice.inspect = [&]
    {
        check(weak_plan.expired() && !scope.session->active && scope.source->pending == 0,
              "ready retained this worker's input, command gate or Source reservation");
        check(!released(raw->snapshot()), "ready incorrectly certified its own callback-return tail");
    };
    command->test_after_ready = [&] { tail.hold(); };
    bool ready_before_return = false;
    command->test_before_request_return = [&]
    {
        notice.wait();
        ready_before_return = true;
    };
    Release release{tail};
    command->start();
    check(ready_before_return, "request could not complete readiness before returning to setup");
    tail.wait();
    check(reject([&] { (void)command->take(3); }).status == NW_INVALID_STATE,
          "wrong typed take consumed a count result");
    check(std::get<std::int64_t>(command->take(100)) == 0, "actual DDL count differed");
    check(reject([&] { (void)command->take(100); }).status == NW_INVALID_STATE, "a count was transferred twice");
    check(!released(command->snapshot()) && !scope.source->command_workers->done(),
          "snapshot or Source ticket ignored the exact held notification tail");
    auto joined = std::async(std::launch::async, [command] { command->join(); });
    check(joined.wait_for(std::chrono::milliseconds(20)) != std::future_status::ready,
          "cold join returned before its notification tail");
    tail.release();
    joined.get();
    check(released(command->snapshot()) && !tail.expired, "cold join failed to prove actual notification return");
}

void queued_cancel_and_provider_priority()
{
    {
        Scope scope;
        Notice notice;
        Gate before;
        auto command = scope.command(scope.plan("CREATE TABLE canceled_side_effect(v BIGINT)"), notice);
        DrainCommand drain{command};
        command->test_before_work = [&] { before.hold(); };
        Release release{before};
        command->start();
        before.wait();
        check(reject([&] { (void)command->take(100); }).status == NW_PENDING,
              "an accepted pending command exposed or consumed a result");
        command->request_close();
        before.release();
        notice.wait();
        const auto failure = reject([&] { (void)command->take(100); });
        const auto rich = command->take_error();
        check(failure.status == NW_CANCELED && rich.status == NW_CANCELED && rich.correlation == failure.correlation,
              "queued cancellation changed to Closed or lost its original error");
        command->join();
        auto rows = scope.session->connection->query_text(
            "SELECT count(*) FROM sqlite_schema WHERE name='canceled_side_effect'", {});
        check(rows.size() == 1 && rows[0][0] == "0" && !before.expired,
              "canceled queued work executed its side effect");
    }
    zero();
    {
        Scope scope;
        Notice notice;
        Gate frozen;
        auto command = scope.command(scope.plan("INSERT INTO missing_completion_table VALUES (1)"), notice);
        DrainCommand drain{command};
        command->test_result_frozen = [&] { frozen.hold(); };
        Release release{frozen};
        command->start();
        frozen.wait();
        command->request_close();
        frozen.release();
        notice.wait();
        const auto failure = reject([&] { (void)command->take(100); });
        const auto rich = command->take_error();
        check(failure.status == NW_DOMAIN_ERROR && failure.code == 1 && rich.provider == "1" &&
                  failure.correlation == rich.correlation,
              "late cancellation replaced a frozen provider error");
        check(reject([&] { (void)command->take_error(); }).status == NW_INVALID_STATE,
              "rich command error was consumed twice");
        command->join();
        check(!frozen.expired, "frozen provider result gate timed out");
    }
}

void postgres_queued_cancel(std::string connection)
{
    auto config = configuration();
    config.provider.profile = Profile::PostgreSql;
    config.provider.connection = std::move(connection);
    config.provider.verify_tls = false;
    Scope scope(std::move(config));
    Notice notice;
    Gate before;
    auto command = scope.command(scope.plan("CREATE TABLE command_canceled_before_dispatch(v BIGINT)"), notice);
    DrainCommand drain{command};
    command->test_before_work = [&] { before.hold(); };
    Release release{before};
    const auto dispatched = provider_dispatch_count();
    command->start();
    before.wait();
    command->request_close();
    before.release();
    notice.wait();
    const auto failure = reject([&] { (void)command->take(100); });
    const auto rich = command->take_error();
    command->join();
    check(failure.status == NW_CANCELED && rich.status == NW_CANCELED && failure.correlation == rich.correlation &&
              provider_dispatch_count() == dispatched && !before.expired,
          "accepted queued PostgreSQL cancellation must precede every provider SQL dispatch");
    const auto rows =
        scope.session->connection->query_text("SELECT to_regclass('command_canceled_before_dispatch')::text", {});
    check(rows.size() == 1 && rows[0].size() == 1 && !rows[0][0],
          "a queued PostgreSQL command performed its canceled side effect");
}

void released_owner_keeps_tail_visible()
{
    Scope scope;
    Notice notice;
    Gate tail;
    auto command = scope.command(scope.plan("CREATE TABLE released_owner_tail(v BIGINT)"), notice);
    command->test_after_ready = [&] { tail.hold(); };
    Release release{tail};
    command->start();
    notice.wait();
    tail.wait();
    (void)command->take(100);
    command.reset();
    check(command_count == 1 && scope.source->pending == 0 && !scope.source->command_workers->done(),
          "releasing the public command owner hid its detached notification tail");
    scope.session->request_close();
    scope.session->join();
    auto joined = std::async(std::launch::async, [&] { scope.source->join(); });
    const bool retained = joined.wait_for(std::chrono::milliseconds(20)) != std::future_status::ready;
    tail.release();
    joined.get();
    check(retained && command_count == 0 && scope.source->command_workers->done() && !tail.expired,
          "Source join missed a detached command tail or returned before its last diagnostic owner dropped");
}

void queue_failure_and_capacity()
{
    for (const bool allocation : {false, true})
    {
        Scope scope;
        Notice notice;
        auto command = scope.command(scope.plan("CREATE TABLE rejected_queue(v BIGINT)"), notice);
        DrainCommand drain{command};
        scope.source->workers.test_before_post = [allocation]
        {
            if (allocation)
                throw std::bad_alloc();
            throw std::runtime_error("controlled post failure");
        };
        command->start();
        notice.wait();
        check(reject([&] { (void)command->take(100); }).status == (allocation ? NW_OUT_OF_MEMORY : NW_NATIVE_PANIC),
              "accepted queue failure lost its allocation/panic category");
        (void)command->take_error();
        scope.source->workers.test_before_post = {};
        command->join();
        check(!scope.session->active && scope.source->pending == 0 && scope.source->poisoned == !allocation,
              "queue rejection retained admission or changed poison policy");
    }
    zero();
    auto config = configuration();
    config.limits.nodes = config.limits.waiters = 1;
    auto source = Source::create(config);
    Notice notice;
    std::vector<std::shared_ptr<CommandCompletion>> pending;
    for (unsigned index = 0; index != 3; ++index)
        pending.push_back(CommandCompletion::create(
            source, 100, false, [](auto) -> Value { return std::int64_t{0}; }, notice.callbacks()));
    check(reject(
              [&]
              {
                  (void)CommandCompletion::create(
                      source, 100, false, [](auto) -> Value { return std::int64_t{0}; }, notice.callbacks());
              }).code == 6,
          "finite command admission did not reject before acceptance");
    pending.clear();
    check(notice.calls == 0 && source->pending == 0 && source->command_workers->done(),
          "unstarted/rejected commands invented a notification or retained admission");
    source->join();
    check(reject(
              [&]
              {
                  (void)CommandCompletion::create(
                      source, 101, true, [](auto) -> Value { return std::int64_t{0}; }, notice.callbacks());
              }).status == NW_INVALID_STATE &&
              notice.calls == 0,
          "stopped control admission accepted work or notified after rejection");
}

void count_contracts()
{
    Scope scope;
    const auto run = [&](std::string sql, std::optional<std::uint32_t> code, std::int64_t expected = 0)
    {
        const auto label = sql;
        Notice notice;
        auto command = scope.command(scope.plan(std::move(sql), true), notice);
        DrainCommand drain{command};
        command->start();
        notice.wait();
        if (code)
        {
            const auto failure = reject([&] { (void)command->take(100); });
            const auto rich = command->take_error();
            if (failure.status != NW_DOMAIN_ERROR || failure.code != *code || rich.correlation != failure.correlation)
                std::cerr << "SCALAR_DIFFERENCE sql=" << label << " expected_code=" << *code
                          << " status=" << failure.status << " code=" << failure.code << " rich_status=" << rich.status
                          << " message=" << failure.message << '\n';
            check(failure.status == NW_DOMAIN_ERROR && failure.code == *code && rich.correlation == failure.correlation,
                  "direct scalar changed conversion/cardinality priority");
        }
        else
            check(std::get<std::int64_t>(command->take(100)) == expected,
                  "direct count did not support legal MaxBatchRows=1");
        command->join();
        check(!scope.session->active && buffer_count == 0 && cursor_count == 0,
              "direct scalar retained a cursor/batch or its Session gate");
    };
    run("SELECT CAST(7 AS BIGINT)", {}, 7);
    run("SELECT CAST(7 AS BIGINT) WHERE FALSE", 4);
    run("SELECT CAST(1 AS BIGINT) UNION ALL SELECT CAST(2 AS BIGINT)", 4);
    run("SELECT CAST(NULL AS BIGINT)", 3);
    run("SELECT CAST(1 AS BIGINT) UNION ALL SELECT CAST(NULL AS BIGINT)", 3);
    // A BLOB cannot reach the Int64 writer: the provider's complete database
    // type check rejects it as shape mismatch before canonical conversion.
    run("SELECT X'01020304'", 4);
    run("SELECT CAST(1 AS BIGINT), CAST(2 AS BIGINT)", 4);
    check(sqlite3_create_function_v2(
              static_cast<sqlite3*>(scope.session->connection->test_sqlite_handle()), "completion_fail", 1, SQLITE_UTF8,
              nullptr, [](sqlite3_context* context, int, sqlite3_value**)
              { sqlite3_result_error(context, "late provider", -1); }, nullptr, nullptr, nullptr) == SQLITE_OK,
          "late-error SQLite function registration failed");
    run("SELECT CAST(1 AS BIGINT) UNION ALL SELECT completion_fail(2)", 1);
    run("SELECT CAST(11 AS BIGINT)", {}, 11);
}

void count_transaction_failure_state()
{
    Scope scope;
    auto transaction = Transaction::begin(scope.session, {0, false, 5'000'000'000, 5'000'000'000, {}},
                                          std::make_shared<Cancel>(), scope.session->admit(true));
    for (const bool malformed : {false, true})
    {
        Notice notice;
        auto command =
            scope.command(scope.plan(malformed ? "SELECT CAST(NULL AS BIGINT)"
                                               : "SELECT CAST(1 AS BIGINT) UNION ALL SELECT CAST(2 AS BIGINT)",
                                     true),
                          notice);
        DrainCommand drain{command};
        command->start();
        notice.wait();
        check(reject([&] { (void)command->take(100); }).code == (malformed ? 3u : 4u),
              "transaction scalar error classification changed");
        (void)command->take_error();
        command->join();
        check(transaction->active() == !malformed && !scope.session->active,
              "complete-row cardinality must retire normally while a malformed row marks transaction failure");
    }
    transaction->join();
    check(transaction->retired && transaction->outcome == 2,
          "a scalar contract failure did not permit confirmed transaction rollback");
}

void unknown_is_not_retirement()
{
    Scope scope;
    auto transaction = Transaction::begin(scope.session, {0, false, 5'000'000, 5'000'000'000, {}},
                                          std::make_shared<Cancel>(), scope.session->admit(true));
    Gate provider;
    transaction->test_finish = [&](bool commit)
    {
        check(commit, "Unknown fixture was not a commit");
        provider.hold();
        (void)scope.session->connection->execute("COMMIT", {}, {});
        return true;
    };
    Notice notice;
    auto gate = scope.session->admit(true);
    auto work = [transaction, gate](auto cancel) -> Value
    { return std::make_shared<Buffer>(transaction->session->source, transaction->finish(true, cancel, gate)); };
    auto command =
        CommandCompletion::create(scope.source, 101, false, std::move(work), notice.callbacks(), std::move(gate));
    DrainCommand drain{command};
    Release release{provider};
    command->start();
    provider.wait();
    notice.wait();
    check(reject([&] { (void)command->take(101); }).code == 9, "bounded commit did not report Unknown early");
    (void)command->take_error();
    {
        std::lock_guard lock(transaction->mutex);
        check(transaction->outcome == 3 && transaction->driver_active && !transaction->retired &&
                  transaction->cleanup == 1 && scope.source->leased == 1,
              "command readiness forged transaction cleanup or rewrote Unknown");
    }
    auto joined = std::async(std::launch::async, [command] { command->join(); });
    const bool command_only = joined.wait_for(std::chrono::seconds(1)) == std::future_status::ready;
    provider.release(); // Also rescues a faulty implementation before the assertion.
    joined.get();
    transaction->join();
    check(command_only && transaction->outcome == 3 && transaction->retired && !provider.expired,
          "command retirement joined an independently owned transaction or changed its late outcome");
}

void cleanup_keeps_primary()
{
    for (const unsigned variant : {0u, 1u, 2u, 3u})
        for (const unsigned failed_index : {0u, 1u})
        {
            Scope scope;
            auto transaction = Transaction::begin(scope.session, {0, false, 5'000'000'000, 5'000'000'000, {}},
                                                  std::make_shared<Cancel>(), scope.session->admit(true));
            Notice notice;
            const char* sql[] = {"INSERT INTO missing_command_table VALUES (1)", "SELECT CAST(NULL AS BIGINT)",
                                 "SELECT CAST(1 AS BIGINT) UNION ALL SELECT CAST(2 AS BIGINT)", "SELECT X'01020304'"};
            auto command = scope.command(scope.plan(sql[variant], variant != 0), notice);
            DrainCommand drain{command};
            std::optional<Error> expected;
            auto detail = std::make_shared<Error>(NW_TIMEOUT, 8, "retained cleanup detail");
            transaction->test_before_worker_start = [&](std::uint32_t index)
            {
                if (index != failed_index)
                    return;
                check(bool(std::current_exception()), "cleanup ran outside the primary command handler");
                try
                {
                    throw;
                }
                catch (Error& error)
                {
                    error.cleanup = detail;
                    error.bulk = NativeBulkProgress{7, 3, false, 2};
                    expected = error;
                }
                throw std::system_error(std::make_error_code(std::errc::resource_unavailable_try_again));
            };
            transaction->request_close();
            command->start();
            notice.wait();
            const auto failure = reject([&] { (void)command->take(100); });
            const auto rich = command->take_error();
            transaction->test_before_worker_start = {};
            const bool incomplete = !transaction->retired && !scope.session->active;
            command->join();
            transaction->join();
            check(expected && failure.status == expected->status && failure.code == expected->code &&
                      rich.correlation == expected->correlation && rich.provider == expected->provider &&
                      rich.message == expected->message && rich.cleanup == detail && rich.bulk &&
                      rich.bulk->rows == 7 && rich.bulk->batches == 3 && !rich.bulk->committed &&
                      rich.bulk->outcome == 2,
                  "one-shot completion replaced the original provider/NULL error or existing rich detail");
            check(incomplete && scope.session->discard && scope.source->poisoned && transaction->retired &&
                      transaction->outcome == 2 && scope.source->transaction_workers->done(),
                  "primary error completion invented retirement or stranded a retryable rollback");
        }
}

void canceled_helper_cleanup_keeps_primary()
{
    for (const bool begin : {false, true})
        for (const unsigned failed_index : {0u, 1u})
        {
            Scope scope;
            auto transaction = Transaction::begin(scope.session, {0, false, 5'000'000'000, 5'000'000'000, {}},
                                                  std::make_shared<Cancel>(), scope.session->admit(true));
            Notice notice;
            std::optional<Error> expected;
            auto detail = std::make_shared<Error>(NW_TIMEOUT, 8, "pre-dispatch retained cleanup detail");
            auto gate = scope.session->admit(begin);
            auto work = [&, gate, plan = scope.plan("CREATE TABLE canceled_helper(v BIGINT)")](auto cancel) -> Value
            {
                try
                {
                    if (begin)
                        return std::make_shared<Owner>(scope.source,
                                                       Transaction::begin(scope.session,
                                                                          {0, false, 5'000'000'000, 5'000'000'000, {}},
                                                                          cancel, gate));
                    return scope.session->execute(plan, cancel, gate);
                }
                catch (Error& error)
                {
                    if (begin)
                        error.cleanup = detail;
                    error.bulk = NativeBulkProgress{5, 2, false, 0};
                    expected = error;
                    throw;
                }
            };
            auto command = CommandCompletion::create(scope.source, begin ? 3 : 100, false, std::move(work),
                                                     notice.callbacks(), std::move(gate));
            DrainCommand drain{command};
            auto* raw = command.get();
            command->test_before_work = [&, raw]
            {
                transaction->request_close();
                raw->request_close();
            };
            unsigned launches = 0;
            transaction->test_before_worker_start = [&](std::uint32_t index)
            {
                if (index == failed_index)
                {
                    ++launches;
                    throw std::system_error(std::make_error_code(std::errc::resource_unavailable_try_again));
                }
            };
            command->start();
            notice.wait();
            const auto failure = reject([&] { (void)command->take(begin ? 3 : 100); });
            const auto rich = command->take_error();
            const bool incomplete = !transaction->retired && !scope.session->active;
            const bool discarded = scope.session->discard && scope.source->poisoned;
            transaction->test_before_worker_start = {};
            command->join();
            transaction->join();
            check(expected && launches == 1 && failure.status == NW_CANCELED &&
                      failure.correlation == expected->correlation && rich.correlation == expected->correlation &&
                      rich.message == expected->message && rich.cleanup && (!begin || rich.cleanup == detail) &&
                      rich.bulk && rich.bulk->rows == 5 && rich.bulk->batches == 2 && !rich.bulk->committed &&
                      rich.bulk->outcome == 0,
                  "entered pre-dispatch cancellation lost its primary error or existing cleanup detail");
            check(incomplete && discarded && transaction->retired && transaction->outcome == 2 &&
                      scope.source->transaction_workers->done(),
                  "entered pre-dispatch cancellation swallowed parent cleanup failure or invented retirement");
        }
}

// The following adapters only drive the frozen setup/close API; all tested
// business requests and typed transfers use the actual new C entry points.
struct LegacyOperation
{
    nw_db_ref value{};
    std::mutex mutex;
    std::condition_variable changed;
    bool started = false, done = false;
    nw_status status = NW_PENDING;
    static nw_status NW_CDECL ready(std::uint64_t cookie, nw_status status, const nw_error_info_v1*)
    {
        auto& self = *reinterpret_cast<LegacyOperation*>(cookie);
        std::lock_guard lock(self.mutex);
        self.status = status;
        self.done = true;
        self.changed.notify_all();
        return NW_OK;
    }
    void run()
    {
        auto error = error_slot();
        nw_operation_callbacks_v1 callbacks{sizeof(callbacks), 0, reinterpret_cast<std::uintptr_t>(this), ready};
        check(nw_db_operation_start_v1(value, &callbacks, &error) == NW_OK, "legacy setup/close start failed");
        started = true;
        {
            std::unique_lock lock(mutex);
            check(changed.wait_for(lock, bound, [&] { return done; }), "legacy setup/close notification missing");
        }
        await([&] { return nw_db_operation_try_retire_v1(value) == NW_OK; }, "legacy setup/close did not retire");
        check(status == NW_OK, "legacy setup/close business result failed");
    }
    ~LegacyOperation()
    {
        if (started)
        {
            (void)nw_db_operation_request_cancel_v1(value);
            while (nw_db_operation_try_retire_v1(value) == NW_PENDING)
                std::this_thread::yield();
        }
        nw_db_operation_release_v1(value);
    }
};
Bytes copy_buffer(nw_db_buffer* raw)
{
    std::unique_ptr<nw_db_buffer, decltype(&nw_db_buffer_release_v1)> buffer(raw, nw_db_buffer_release_v1);
    nw_bytes_view_v1 data{};
    auto error = error_slot();
    check(nw_db_buffer_view_v1(raw, &data, &error) == NW_OK, "real result buffer view failed");
    return {data.data, data.data + data.length};
}
Bytes cold_close(nw_db_ref owner)
{
    LegacyOperation operation;
    auto error = error_slot();
    check(nw_db_close_create_v1(owner, &operation.value, &error) == NW_OK, "cold command close creation failed");
    operation.run();
    nw_db_buffer* buffer{};
    check(nw_db_operation_take_buffer_v1(operation.value, &buffer, &error) == NW_OK, "cold close snapshot missing");
    return copy_buffer(buffer);
}
struct AbiOwner
{
    nw_db_ref value{};
    ~AbiOwner()
    {
        nw_db_owner_release_v1(value);
    }
};
struct AbiCommand
{
    Notice notice;
    nw_db_ref value{};
    void request(const nw_db_command_request_args_v1& args)
    {
        auto callbacks = notice.callbacks();
        auto error = error_slot();
        check(nw_db_command_request_v1(&args, &callbacks, &value, &error) == NW_OK && value,
              "actual one-shot request was not accepted with its owner");
        notice.wait();
    }
    nw_db_command_result_v1 take(std::uint32_t expected)
    {
        nw_db_command_result_v1 result{sizeof(result), 99, -1, reinterpret_cast<nw_db_ref>(1),
                                       reinterpret_cast<nw_db_buffer*>(1)};
        auto error = error_slot();
        check(nw_db_command_take_v1(value, expected, &result, &error) == NW_OK &&
                  result.struct_size == sizeof(result) && result.kind == expected,
              "actual command typed result differs");
        check((expected == 100 ? !result.owner && !result.buffer : result.count == 0) &&
                  (expected == 101 ? !result.owner && result.buffer : !result.buffer) &&
                  (expected == 3 || expected == 4 ? result.owner != nullptr : result.owner == nullptr),
              "successful command result populated an unrelated output field");
        return result;
    }
    ~AbiCommand()
    {
        if (value)
        {
            (void)nw_db_command_cancel_v1(value);
            (void)cold_close(value);
            nw_db_owner_release_v1(value);
        }
    }
};
void cleared(const nw_db_command_result_v1& result)
{
    check(result.struct_size == 0 && result.kind == 0 && result.count == 0 && !result.owner && !result.buffer,
          "a failed typed take left a dirty result field");
}
struct AbiScope
{
    AbiOwner source, session;
    AbiScope()
    {
        const auto limits =
            encode_control(10, {u64(1, 1), u64(2, 8), u64(3, 5'000'000'000, 3), u64(4, 64), u64(5, 65536), u64(6, 1),
                                u64(7, 65536), u64(8, 65536), u64(9, 4096), u64(10, 64), u64(11, 4096),
                                u64(12, MAX_BYTES), u64(13, 64), u64(14, MAX_BYTES), u64(15, 30'000'000'000, 3)});
        const auto options =
            encode_control(9, {u32(1, 3), u32(2, 2), u32(3, 0), u32(4, 0), u64(5, 1'000'000), u32(6, 1)});
        const auto config = encode_control(1, {u32(1, 1),
                                               {2, 4, bytes(":memory:")},
                                               {3, 5, limits},
                                               {4, 5, options},
                                               {5, 4, bytes("command-actual-abi")}});
        auto error = error_slot();
        check(nw_db_source_create_v1(view(config), &source.value, &error) == NW_OK, "C ABI Source setup failed");
        LegacyOperation open;
        check(nw_db_session_open_create_v1(source.value, &open.value, &error) == NW_OK, "C ABI Session setup failed");
        open.run();
        check(nw_db_operation_take_owner_v1(open.value, 1, &session.value, &error) == NW_OK,
              "C ABI Session transfer failed");
    }
    ~AbiScope()
    {
        (void)cold_close(session.value);
        (void)cold_close(source.value);
    }
    void sql(std::string_view text, AbiOwner& plan)
    {
        const auto parameters = Batch::encode(
            {1, Profile::Sqlite, sha256(text_view("{\"version\":1,\"parameters\":[]}")), 0, 0, {}, 1}, 1, {});
        const auto options = encode_control(
            20, {u32(1, 1), u32(2, 0), u64(3, 5'000'000'000, 3), u32(4, 0), {5, 5, u32_bytes(0)}, u32(6, 0)});
        const auto sql_bytes = bytes(text);
        auto error = error_slot();
        check(nw_db_sql_create_v1(session.value, view(sql_bytes), view(parameters), view(options), &plan.value,
                                  &error) == NW_OK,
              "C ABI typed SQL preparation failed");
    }
};

void actual_abi()
{
    static_assert(sizeof(nw_db_command_request_args_v1) == 48);
    static_assert(offsetof(nw_db_command_request_args_v1, payload) == 24);
    static_assert(offsetof(nw_db_command_request_args_v1, reserved) == 44);
    static_assert(sizeof(nw_db_command_result_v1) == 32);
    static_assert(offsetof(nw_db_command_result_v1, owner) == 16);
    static_assert(offsetof(nw_db_command_result_v1, buffer) == 24);
    AbiScope scope;
    AbiOwner plan;
    scope.sql("CREATE TABLE command_rows(v BIGINT)", plan);
    nw_db_command_request_args_v1 args{sizeof(args), 1, scope.session.value, plan.value, {}, 0, 0};
    auto error = error_slot();
    Notice rejected;
    auto callbacks = rejected.callbacks();
    auto dirty = reinterpret_cast<nw_db_ref>(1);
    args.reserved = 1;
    check(nw_db_command_request_v1(&args, &callbacks, &dirty, &error) == NW_INVALID_ARGUMENT && !dirty &&
              rejected.calls == 0,
          "dirty request rejection failed to clear its owner or invented a callback");
    args.reserved = 0;
    {
        AbiCommand command;
        command.request(args);
        nw_db_command_result_v1 result{sizeof(result), 99, -1, reinterpret_cast<nw_db_ref>(1),
                                       reinterpret_cast<nw_db_buffer*>(1)};
        check(nw_db_command_take_v1(command.value, 3, &result, &error) == NW_INVALID_STATE,
              "wrong C ABI typed take consumed the count");
        cleared(result);
        check(command.take(100).count == 0, "C ABI DDL count differs");
        result = {sizeof(result), 99, -1, reinterpret_cast<nw_db_ref>(1), reinterpret_cast<nw_db_buffer*>(1)};
        check(nw_db_command_take_v1(command.value, 100, &result, &error) == NW_INVALID_STATE,
              "C ABI count transferred twice");
        cleared(result);
    }
    {
        AbiOwner bad;
        scope.sql("INSERT INTO missing_completion_table VALUES (1)", bad);
        AbiCommand command;
        args.input = bad.value;
        command.request(args);
        nw_db_command_result_v1 result{sizeof(result), 99, -1, reinterpret_cast<nw_db_ref>(1),
                                       reinterpret_cast<nw_db_buffer*>(1)};
        check(nw_db_command_take_v1(command.value, 100, &result, &error) == NW_DOMAIN_ERROR && error.code == 1 &&
                  error.domain == 0x4442 && error.correlation_id != 0,
              "C ABI lost original provider error fields");
        cleared(result);
        nw_db_buffer* detail = reinterpret_cast<nw_db_buffer*>(1);
        check(nw_db_command_take_error_v1(command.value, &detail, &error) == NW_OK && !copy_buffer(detail).empty(),
              "C ABI rich error transfer failed");
        detail = reinterpret_cast<nw_db_buffer*>(1);
        check(nw_db_command_take_error_v1(command.value, &detail, &error) == NW_INVALID_STATE && !detail,
              "C ABI repeated rich take consumed twice or left a dirty output");
    }
    const auto options = encode_control(3, {u32(1, 0), u32(2, 0), u64(3, 5'000'000'000, 3), u64(4, 5'000'000'000, 3)});
    AbiOwner transaction;
    {
        AbiCommand begin;
        begin.request({sizeof(args), 3, scope.session.value, nullptr, view(options), 0, 0});
        transaction.value = begin.take(3).owner;
    }
    AbiOwner savepoint;
    {
        AbiCommand begin;
        begin.request({sizeof(args), 5, transaction.value, nullptr, {}, 0, 0});
        savepoint.value = begin.take(4).owner;
    }
    for (const unsigned action : {0u, 1u})
    {
        AbiCommand operation;
        operation.request({sizeof(args), 6, savepoint.value, nullptr, {}, action, 0});
        check(!copy_buffer(operation.take(101).buffer).empty(), "savepoint action snapshot is empty");
    }
    {
        AbiCommand finish;
        finish.request({sizeof(args), 4, transaction.value, nullptr, {}, 1, 0});
        (void)copy_buffer(finish.take(101).buffer);
    }
    {
        AbiCommand close;
        close.request({sizeof(args), 7, transaction.value, nullptr, {}, 0, 0});
        check(released(copy_buffer(close.take(101).buffer)),
              "explicit transaction close did not join actual retirement");
    }
    // An untaken *new* transaction owner belongs to this completion. Cold
    // command retirement must roll it back; it is not a snapshot of an existing
    // transaction whose bounded primary result must remain independently early.
    {
        AbiCommand untaken;
        untaken.request({sizeof(args), 3, scope.session.value, nullptr, view(options), 0, 0});
        check(released(cold_close(untaken.value)), "cold completion join left an untaken transaction owner active");
    }
    {
        AbiCommand next;
        next.request({sizeof(args), 3, scope.session.value, nullptr, view(options), 0, 0});
        AbiOwner owner;
        owner.value = next.take(3).owner;
        AbiCommand close;
        close.request({sizeof(args), 7, owner.value, nullptr, {}, 0, 0});
        check(released(copy_buffer(close.take(101).buffer)), "Session was not reusable after untaken-owner cleanup");
    }
}

} // namespace

int main(int argc, char** argv)
{
    try
    {
        if (argc == 2 && std::string_view(argv[1]) == "--postgres-queued-cancel-only")
        {
            const auto* connection = std::getenv("NW_TEST_POSTGRES");
            check(connection && *connection, "the real PostgreSQL cancellation fixture requires NW_TEST_POSTGRES");
            postgres_queued_cancel(connection);
            zero();
            std::cout << "PASS real PostgreSQL queued command cancellation: " << checks.load() << " checks\n";
            return 0;
        }
        const auto actual = Capabilities::from_mask(capability_mask(), Profile::Sqlite);
        check(actual.query && actual.command, "the actual provider capability closure permits Fluent submission");
        check(!Capabilities::from_mask(1ULL << 28, Profile::Sqlite).query,
              "command completion alone must not authorize query composition");
        check(reject([] { (void)Capabilities::from_mask(1ULL << 30, Profile::Sqlite); }).status == NW_INVALID_ARGUMENT,
              "the next reserved database capability must still be rejected");
        early_ready_and_tail();
        zero();
        released_owner_keeps_tail_visible();
        zero();
        queued_cancel_and_provider_priority();
        zero();
        queue_failure_and_capacity();
        zero();
        count_contracts();
        zero();
        count_transaction_failure_state();
        zero();
        unknown_is_not_retirement();
        zero();
        cleanup_keeps_primary();
        zero();
        canceled_helper_cleanup_keeps_primary();
        zero();
        actual_abi();
        zero();
        for (const auto index : {0u, 1u, 2u, 3u, 4u, 5u, 8u, 9u})
            check(nw_db_diagnostic_count_v1(index) == 0, "real C ABI retained a handle or command lifetime");
        std::cout << "PASS one-shot command completion: " << checks.load() << " checks\n";
        return 0;
    }
    catch (const std::exception& failure)
    {
        std::cerr << "FAIL one-shot command completion: " << failure.what() << '\n';
        return 1;
    }
}
