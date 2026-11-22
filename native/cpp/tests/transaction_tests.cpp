// SPDX-License-Identifier: MIT
// Ports native/database/transactions/tests/lifecycle.rs into the actual C++ Host.
#include "nw/host.hpp"

#include <atomic>
#include <filesystem>
#include <future>
#include <iostream>
#include <semaphore>
#include <stdexcept>

namespace nw
{
std::uint64_t module_identity()
{
    return 0x4442435050545831ULL;
}
} // namespace nw
namespace
{
using namespace nw;
std::size_t checks = 0;
void check(bool value, const char* message)
{
    ++checks;
    if (!value)
        throw std::runtime_error(message);
}
template <typename Action> Error reject(Action action)
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
    throw std::runtime_error("expected native rejection");
}
template <typename Condition> void await(Condition condition)
{
    const auto end = Clock::now() + std::chrono::seconds(3);
    while (!condition())
    {
        if (Clock::now() >= end)
            throw std::runtime_error("controlled transaction gate timeout");
        std::this_thread::yield();
    }
}
struct Probe
{
    std::counting_semaphore<16> proceed{0};
    std::atomic<std::size_t> commits{0}, rollbacks{0}, retires{0};
    bool block_commit = false, lost = false, known_rollback = false, rollback_error = false, block_retire = false;
};
struct Scope
{
    std::shared_ptr<Source> source;
    std::shared_ptr<Session> session;
    std::shared_ptr<Transaction> tx;
    std::shared_ptr<Probe> probe = std::make_shared<Probe>();
    explicit Scope(std::uint64_t commit_ns = 30'000'000'000, std::uint64_t cleanup_ns = 5'000'000'000,
                   std::string database = ":memory:")
    {
        HostConfig config{};
        config.provider.connection = std::move(database);
        const bool memory = config.provider.connection == ":memory:";
        config.provider.sqlite_mode = memory ? 3 : 0;
        config.provider.sqlite_journal = memory ? 2 : 0;
        config.name = "transaction-lifecycle";
        config.limits = {1,    16,        64, 65536,     64, 65536,         65536,         4096,
                         4096, MAX_BYTES, 64, MAX_BYTES, 64, 5'000'000'000, 30'000'000'000};
        source = Source::create(config);
        {
            std::lock_guard lock(source->mutex);
            ++source->leased;
        }
        session = Session::open_reserved(source, std::make_shared<Cancel>());
        TransactionOptions options{};
        options.commit_timeout_ns = commit_ns;
        options.cleanup_timeout_ns = cleanup_ns;
        tx = Transaction::begin(session, options, std::make_shared<Cancel>(), session->admit(true));
        auto state = probe;
        auto connection = session->connection;
        tx->test_finish = [state, connection](bool commit)
        {
            if (commit)
                ++state->commits;
            else
                ++state->rollbacks;
            if (commit && state->block_commit)
                state->proceed.acquire();
            if ((commit && state->lost) || (!commit && state->rollback_error))
            {
                if (!commit)
                    connection->close();
                Error error(NW_DOMAIN_ERROR, 1, "controlled response loss");
                error.provider = "08006";
                error.correlation = 71;
                throw error;
            }
            const bool committed = commit && !state->known_rollback;
            (void)connection->execute(committed ? "COMMIT" : "ROLLBACK", {}, {});
            return committed;
        };
        tx->test_retire_gate = [state]
        {
            ++state->retires;
            if (state->block_retire)
                state->proceed.acquire();
        };
    }
    ~Scope()
    {
        probe->proceed.release(4);
        if (tx)
            tx->join();
        session->request_close();
        session->join();
        source->join();
    }
    void finish(bool commit, std::shared_ptr<Cancel> cancel = std::make_shared<Cancel>())
    {
        (void)tx->finish(commit, std::move(cancel), session->admit(true));
    }
    std::shared_ptr<Savepoint> savepoint()
    {
        return tx->create_savepoint(std::make_shared<Cancel>(), session->admit(true));
    }
    void action(const std::shared_ptr<Savepoint>& point, std::uint32_t action, bool dispose = false)
    {
        if (dispose)
            (void)point->transaction->dispose_savepoint(point->token);
        else
            (void)point->transaction->savepoint_action(point->token, action, std::make_shared<Cancel>(),
                                                       point->transaction->session->admit(true));
    }
    void execute(std::string_view sql)
    {
        auto gate = session->admit(false);
        gate->dispatched = true;
        (void)session->connection->execute(sql, {}, {});
        gate->finish();
    }
    void fail_command()
    {
        auto gate = session->admit(false);
        gate->dispatched = true;
        gate->finish(true);
    }
};
void canceled_before()
{
    Scope scope;
    auto cancel = std::make_shared<Cancel>();
    cancel->request();
    check(reject([&] { scope.finish(true, cancel); }).status == NW_CANCELED, "precommit cancellation category");
    check(scope.tx->state == 0 && scope.tx->outcome == 0 && !scope.tx->attempted, "precommit preserves active state");
    check(scope.probe->commits == 0, "precommit does not reach provider");
    scope.tx->join();
    check(scope.tx->retired && scope.probe->rollbacks == 1, "join runs one actual rollback");
}
void cancel_after()
{
    Scope scope;
    scope.probe->block_commit = true;
    auto cancel = std::make_shared<Cancel>();
    auto work = std::async(std::launch::async, [&] { scope.finish(true, cancel); });
    await([&] { return scope.probe->commits == 1; });
    cancel->request();
    check(scope.tx->state == 2 && scope.tx->attempted, "commit admitted before caller cancellation");
    (void)reject([&] { scope.finish(false); });
    scope.probe->proceed.release();
    work.get();
    scope.tx->join();
    check(scope.tx->outcome == 1 && scope.probe->commits == 1, "admitted commit ignores request cancellation");
}
void deadline_owner()
{
    Scope scope(5'000'000);
    scope.probe->block_commit = true;
    check(reject([&] { scope.finish(true); }).code == 9, "commit deadline reports unknown");
    (void)scope.tx->wait_close(0);
    {
        std::lock_guard lock(scope.tx->mutex);
        check(scope.tx->state == 4 && scope.tx->outcome == 3 && scope.tx->cleanup == 1,
              "deadline state independent from cleanup");
        check(!scope.tx->retired && scope.tx->driver_active && scope.tx->disposition == 2,
              "deadline retains accessor and lease");
    }
    check(scope.session->connection && scope.source->leased == 1, "physical lease retained while commit runs");
    scope.probe->proceed.release();
    scope.tx->join();
    check(scope.tx->outcome == 3 && scope.tx->retired && !scope.tx->driver_active,
          "late confirmation never rewrites unknown");
}
void response_loss()
{
    Scope scope;
    scope.probe->lost = true;
    scope.tx->options.recovery_key = "operation-7";
    const auto error = reject([&] { scope.finish(true); });
    check(error.code == 9 && error.provider == "08006" && error.correlation == 71, "response loss identity preserved");
    scope.tx->join();
    check(scope.tx->outcome == 3 && scope.tx->options.recovery_key == "operation-7",
          "cleanup preserves unknown and recovery");
    check(!scope.session->connection->in_transaction() && scope.probe->commits == 1,
          "cleanup performed actual rollback");
}
void failed_transaction()
{
    Scope scope;
    auto gate = scope.session->admit(false);
    check(reject([&] { (void)scope.session->admit(false); }).code == 7, "concurrent commands reject");
    gate->dispatched = true;
    gate->finish(true);
    check(scope.tx->state == 1, "failed command marks transaction");
    (void)reject([&] { scope.finish(true); });
    scope.finish(false);
    scope.tx->join();
    check(scope.tx->outcome == 2 && scope.probe->commits == 0, "failed transaction only rolls back");
}
void known_rollback()
{
    Scope scope;
    scope.probe->known_rollback = true;
    check(reject([&] { scope.finish(true); }).code == 1, "known rollback reports provider failure");
    scope.tx->join();
    check(scope.tx->outcome == 2, "known rollback is never commit success or unknown");
}
void unconfirmed_rollback()
{
    Scope scope;
    scope.probe->rollback_error = true;
    check(reject([&] { scope.finish(false); }).code == 1, "unconfirmed rollback primary failure");
    scope.tx->join();
    check(scope.tx->state == 4 && scope.tx->outcome == 0 && !scope.tx->attempted,
          "no commit means unconfirmed remains in progress");
    check(scope.tx->cleanup == 3 && scope.tx->disposition == 3 && scope.tx->retired,
          "untrusted rollback physically discarded");
}
void savepoint_lifo()
{
    Scope scope;
    auto outer = scope.savepoint();
    auto inner = scope.savepoint();
    (void)reject([&] { scope.action(outer, 1); });
    scope.fail_command();
    (void)reject([&] { scope.action(inner, 1); });
    scope.action(inner, 0);
    check(scope.tx->state == 0 && scope.tx->savepoints.size() == 2, "rollback-to preserves scope");
    scope.action(inner, 1);
    scope.action(outer, 0, true);
    check(scope.tx->state == 0 && scope.tx->savepoints.empty() && scope.probe->commits == 0,
          "nested cleanup preserves outer transaction");
    scope.tx->join();
    scope.action(outer, 0, true);
}
void non_top_disposal()
{
    Scope scope;
    auto outer = scope.savepoint();
    auto inner = scope.savepoint();
    (void)inner;
    (void)reject([&] { scope.action(outer, 0, true); });
    scope.tx->join();
    check(scope.tx->cleanup == 4 && scope.probe->rollbacks == 1,
          "non-top disposal failure survives automatic rollback");
}
void released_savepoint_disposal()
{
    for (const bool queued_before_release : {false, true})
    {
        struct DatabaseFile
        {
            std::filesystem::path path = std::filesystem::temp_directory_path() /
                                         ("nativeweave-released-savepoint-" +
                                          std::to_string(Clock::now().time_since_epoch().count()) + ".sqlite");
            ~DatabaseFile()
            {
                std::error_code ignored;
                std::filesystem::remove(path, ignored);
                std::filesystem::remove(path.string() + "-wal", ignored);
                std::filesystem::remove(path.string() + "-shm", ignored);
            }
        } file;
        Scope scope(30'000'000'000, 5'000'000'000, file.path.string());
        scope.execute("CREATE TABLE released_savepoint_probe(id INTEGER)");
        scope.execute("INSERT INTO released_savepoint_probe VALUES (10)");
        auto point = scope.savepoint();
        auto owner = std::make_shared<Owner>(scope.source, point);
        const auto token = point->token;
        std::weak_ptr<Savepoint> weak = point;
        scope.execute("INSERT INTO released_savepoint_probe VALUES (90)");
        scope.action(point, 0);
        if (queued_before_release)
        {
            // Queue a real Owner destructor while its token still exists, then
            // finish RELEASE before that worker can inspect the token again.
            auto entered = std::make_shared<std::atomic<unsigned>>(0);
            for (unsigned index = 0; index != 3; ++index)
                scope.source->workers.post(
                    [entered, probe = scope.probe]
                    {
                        ++*entered;
                        probe->proceed.acquire();
                    });
            await([&] { return entered->load() == 3; });
            owner.reset();
        }
        scope.action(point, 1);
        auto commit_gate = scope.session->admit(true);
        // The explicit owner-close ABI calls this same production entry point.
        // Even with a later real command admitted, released disposal is a no-op.
        (void)scope.tx->dispose_savepoint(token);
        point.reset();
        owner.reset();
        if (queued_before_release)
            scope.probe->proceed.release(3);
        await([&] { return weak.expired(); });
        {
            std::lock_guard lock(scope.tx->mutex);
            check(!scope.tx->closing && scope.tx->state == 0 && scope.tx->cleanup == 0,
                  "released savepoint disposal closed the next transaction command");
        }
        check(scope.session->active.load() && !commit_gate->completed,
              "released savepoint disposal changed another caller's command gate");
        (void)scope.tx->finish(true, std::make_shared<Cancel>(), commit_gate);
        scope.tx->join();
        check(scope.tx->outcome == 1 && scope.probe->commits == 1 && scope.probe->rollbacks == 0,
              "released savepoint disposal prevented the admitted commit");
        auto independent = ProviderConnection::open(scope.source->config.provider, {});
        const auto rows = independent->query_text("SELECT id FROM released_savepoint_probe ORDER BY id", {});
        check(rows.size() == 1 && rows[0].size() == 1 && rows[0][0] == "10",
              "independent connection did not observe the outer commit and savepoint rollback");
        independent->close();
    }
}
void closed_savepoint_disposal()
{
    Scope scope;
    auto point = scope.savepoint();
    auto owner = std::make_shared<Owner>(scope.source, point);
    const auto token = point->token;
    std::weak_ptr<Savepoint> weak = point;
    scope.probe->block_retire = true;
    scope.finish(true);
    await([&] { return scope.probe->retires == 1; });
    {
        std::lock_guard lock(scope.tx->mutex);
        check(scope.tx->state == 4 && !scope.tx->retired && !scope.tx->savepoints.empty(),
              "closed savepoint test did not hold the real retirement boundary");
    }
    (void)scope.tx->dispose_savepoint(token);
    point.reset();
    owner.reset();
    await([&] { return weak.expired(); });
    check(scope.session->active.load() && scope.probe->rollbacks == 0,
          "closed savepoint disposal competed with the retiring transaction owner");
    scope.probe->proceed.release();
    scope.tx->join();
    check(scope.tx->outcome == 1 && scope.tx->retired, "closed savepoint disposal changed the committed outcome");
}
void parent_close_during_savepoint_disposal()
{
    Scope scope;
    auto point = scope.savepoint();
    auto gate = scope.session->admit(true);
    // Reproduce parent close after disposal acquired its real session gate but
    // before the provider action's second transaction-state validation.
    scope.tx->request_close();
    check(reject([&] { (void)scope.tx->savepoint_action(point->token, 0, std::make_shared<Cancel>(), gate, true); })
                  .status == NW_INVALID_STATE,
          "parent close did not reject the pending savepoint action");
    {
        std::lock_guard lock(scope.tx->mutex);
        check(scope.tx->cleanup_failure == 0 && !scope.tx->provider,
              "parent close misclassified a top savepoint as an ordering failure");
    }
    gate->finish();
    scope.tx->join();
    check(scope.tx->outcome == 2 && scope.tx->cleanup == 2 && scope.probe->rollbacks == 1,
          "parent close did not preserve normal automatic rollback cleanup");
}
void bounded_close()
{
    Scope scope(30'000'000'000, 5'000'000);
    scope.probe->block_retire = true;
    scope.finish(true);
    await([&] { return scope.probe->retires == 1; });
    (void)scope.tx->wait_close(10'000'000);
    check(scope.tx->cleanup == 1 && !scope.tx->retired && scope.tx->deadline_exceeded,
          "queued retirement is not released");
    check(scope.session->connection && scope.source->leased == 1, "bounded cleanup retains physical lease");
    scope.probe->proceed.release();
    scope.tx->join();
    check(scope.tx->retired, "retirement eventually observed");
}
void stale_owner()
{
    Scope old;
    auto stale = old.savepoint();
    old.tx->join();
    Scope current;
    auto valid = current.savepoint();
    (void)reject([&] { old.action(stale, 0); });
    check(current.tx->savepoints.size() == 1 && current.tx->active(), "old owner cannot affect next transaction");
    current.action(valid, 1);
    current.finish(true);
    current.tx->join();
    check(current.tx->outcome == 1, "current generation remains usable");
}
void capability_preflight()
{
    Scope scope;
    scope.tx->join();
    TransactionOptions options{};
    options.isolation = 1;
    options.commit_timeout_ns = options.cleanup_timeout_ns = 1'000'000;
    check(reject(
              [&]
              {
                  (void)Transaction::begin(scope.session, options, std::make_shared<Cancel>(),
                                           scope.session->admit(true));
              }).status == NW_NOT_SUPPORTED,
          "unsupported SQLite isolation rejects before BEGIN");
    check(!scope.session->connection->in_transaction(), "capability refusal sends no BEGIN");
    const auto parameters =
        Batch::encode({1, Profile::Sqlite, sha256(text_view("{\"version\":1,\"parameters\":[]}")), 0, 0, {}, 1}, 1, {});
    const auto options_record = encode_control(20, {{1, 1, u32_bytes(1)},
                                                    {2, 1, u32_bytes(0)},
                                                    {3, 3, u64_bytes(1'000'000'000)},
                                                    {4, 1, u32_bytes(0)},
                                                    {5, 5, u32_bytes(0)},
                                                    {6, 1, u32_bytes(0)}});
    (void)reject([&] { (void)SqlPlan::parse(scope.source, text_view("BEGIN"), parameters, options_record); });
    check(!scope.session->connection->in_transaction(), "raw transaction control never dispatches");
}
void final_owner()
{
    Scope scope;
    scope.probe->block_retire = true;
    auto owner = std::make_shared<Owner>(scope.source, scope.tx);
    std::weak_ptr<Transaction> weak = scope.tx;
    scope.tx.reset();
    owner.reset();
    await([&] { return scope.probe->retires == 1; });
    check(scope.probe->rollbacks == 1 && !weak.expired(), "last wrapper drop retains native cleanup");
    scope.probe->proceed.release();
    await([&] { return weak.expired(); });
    check(weak.expired(), "cleanup eventually releases final transaction owner");
}
} // namespace
int main()
{
    try
    {
        canceled_before();
        cancel_after();
        deadline_owner();
        response_loss();
        failed_transaction();
        known_rollback();
        unconfirmed_rollback();
        savepoint_lifo();
        non_top_disposal();
        released_savepoint_disposal();
        closed_savepoint_disposal();
        parent_close_during_savepoint_disposal();
        bounded_close();
        stale_owner();
        capability_preflight();
        final_owner();
        await([] { return source_count == 0 && session_count == 0; });
        check(source_count == 0 && session_count == 0, "all transaction parent owner counters returned");
        std::cout << "PASS transaction lifecycle: 13 retained families, " << checks << " checks\n";
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << "FAIL transaction lifecycle: " << error.what() << '\n';
        return 1;
    }
}
