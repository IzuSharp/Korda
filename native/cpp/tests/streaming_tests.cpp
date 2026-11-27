// SPDX-License-Identifier: MIT
// Ports the six B06 bounded.rs assertion families against actual Host/SQLite.
#include "nw/host.hpp"
#include <sqlite3.h>

#include <atomic>
#include <future>
#include <iostream>
#include <semaphore>
#include <source_location>
#include <stdexcept>
#include <sys/wait.h>
#include <unistd.h>

namespace nw
{
std::uint64_t module_identity()
{
    return 0x4442435050535431ULL;
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
template <typename Action>
Error reject(Action action, const std::source_location location = std::source_location::current())
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
    throw std::runtime_error("expected streaming rejection at line " + std::to_string(location.line()));
}
template <typename Condition>
void await(Condition condition, const std::source_location location = std::source_location::current())
{
    const auto deadline = Clock::now() + std::chrono::seconds(3);
    while (!condition())
    {
        if (Clock::now() >= deadline)
            throw std::runtime_error("streaming control timeout at line " + std::to_string(location.line()) +
                                     "; owners=" + std::to_string(source_count.load()) + "," +
                                     std::to_string(session_count.load()) + "," + std::to_string(cursor_count.load()) +
                                     "," + std::to_string(buffer_count.load()));
        std::this_thread::yield();
    }
}
struct Probe
{
    std::atomic<std::size_t> reads{0};
    std::size_t length = 50, stop_at = 0;
    std::counting_semaphore<8> proceed{0};
};
struct Scope
{
    std::shared_ptr<Source> source;
    std::shared_ptr<Session> session;
    std::shared_ptr<Probe> probe = std::make_shared<Probe>();
    std::shared_ptr<Projection> shape;
    Scope()
    {
        HostConfig config{};
        config.provider.connection = ":memory:";
        config.provider.sqlite_mode = 3;
        config.provider.sqlite_journal = 2;
        config.name = "bounded-stream";
        config.limits = {1,    16,        100, 65536,     3,  256,           128,           4096,
                         4096, MAX_BYTES, 64,  MAX_BYTES, 64, 5'000'000'000, 30'000'000'000};
        source = Source::create(config);
        source->stream_max_bytes = 65536;
        {
            std::lock_guard lock(source->mutex);
            ++source->leased;
        }
        session = Session::open_reserved(source, std::make_shared<Cancel>());
        shape = std::make_shared<Projection>(
            Projection::parse(text_view("{\"version\":1,\"fields\":[{\"ordinal\":0,\"name\":\"value\",\"codec\":5,"
                                        "\"codec_config\":null,\"nullable\":true}]}"),
                              Profile::Sqlite));
        const int status = sqlite3_create_function_v2(
            static_cast<sqlite3*>(session->connection->test_sqlite_handle()), "fixture_value", 1, SQLITE_UTF8,
            probe.get(),
            [](sqlite3_context* context, int, sqlite3_value**)
            {
                auto& probe = *static_cast<Probe*>(sqlite3_user_data(context));
                const auto index = ++probe.reads;
                if (probe.stop_at && index == probe.stop_at)
                    probe.proceed.acquire();
                const std::string value(probe.length, 'x');
                sqlite3_result_text(context, value.data(), static_cast<int>(value.size()), SQLITE_TRANSIENT);
            },
            nullptr, nullptr, nullptr);
        check(status == SQLITE_OK, "actual SQLite controlled row function installed");
    }
    ~Scope()
    {
        probe->proceed.release(4);
        session->request_close();
        session->join();
        source->join();
    }
    std::shared_ptr<SqlPlan> make_plan(std::size_t rows, std::optional<std::string> query = {})
    {
        auto plan = std::make_shared<SqlPlan>();
        plan->source = source;
        plan->result_kind = 1;
        plan->timeout_ns = 30'000'000'000;
        plan->sql = query.value_or("WITH RECURSIVE n(x) AS (SELECT 1 WHERE " + std::to_string(rows) +
                                   ">0 UNION ALL SELECT x+1 FROM n WHERE x<" + std::to_string(rows) +
                                   ") SELECT fixture_value(x) AS value FROM n");
        plan->projection = ProjectionBinding{source->identity, 7, shape, source->codecs()};
        plan->expectation = {1, Profile::Sqlite, sha256(text_view("{\"version\":1,\"parameters\":[]}")), 0, 0, {}, 1};
        plan->capture_parameters(Batch::encode(plan->expectation, 1, {}));
        return plan;
    }
    std::shared_ptr<CursorPull> pull(std::size_t rows, std::optional<std::string> query = {})
    {
        return CursorPull::create(session, make_plan(rows, std::move(query)), session->admit(false));
    }
    std::shared_ptr<Cursor> open(std::size_t rows, std::optional<std::string> query = {})
    {
        auto plan = make_plan(rows, std::move(query));
        auto result = session->execute(plan, std::make_shared<Cancel>(), session->admit(false));
        auto owner = std::get<std::shared_ptr<Owner>>(std::move(result));
        auto cursor = std::get<std::shared_ptr<Cursor>>(std::move(owner->data));
        owner->data = ModelBinding{};
        return cursor;
    }
    std::array<std::uint64_t, 7> budget()
    {
        std::lock_guard lock(source->stream_mutex);
        return source->stream_stats;
    }
};
void bounded_batches()
{
    for (const auto count : {0U, 1U, 17U})
    {
        Scope scope;
        auto cursor = scope.open(count);
        check(scope.probe->reads == 0, "no native prefetch before fetch");
        std::size_t seen = 0, batches = 0;
        for (;;)
        {
            auto buffer = cursor->fetch(std::make_shared<Cancel>(), 3, 256);
            auto batch = Batch::parse(buffer->bytes, cursor->expectation);
            check(batch.rows() <= 3 && buffer->bytes.size() <= 256, "row and serialized byte bounds");
            (void)reject([&] { (void)cursor->fetch(std::make_shared<Cancel>(), 3, 256); });
            const auto pulled = scope.probe->reads.load();
            std::this_thread::sleep_for(std::chrono::milliseconds(2));
            check(scope.probe->reads == pulled, "slow consumer stops provider reads");
            check(scope.budget()[3] == 1, "one independent live batch");
            buffer->mark_taken();
            seen += batch.rows();
            ++batches;
            const bool end = cursor->ended;
            buffer.reset();
            if (end)
                break;
        }
        check(seen == count && batches > 0, "empty and complete bounded stream cardinality");
        cursor->request_close();
        cursor->join();
        cursor->request_close();
        cursor->join();
        cursor.reset();
        const auto budget = scope.budget();
        check(budget[0] == 0 && budget[1] == 0 && budget[3] == 0 && budget[4] == 0,
              "all native stream credits returned");
        check(budget[5] <= 256 && budget[6] == batches, "peak batch bytes and exact transfer count");
    }
}
void batch_outlives_cursor()
{
    Scope scope;
    scope.probe->length = 10;
    auto cursor = scope.open(5);
    auto buffer = cursor->fetch(std::make_shared<Cancel>(), 3, 256);
    const auto expected = cursor->expectation;
    cursor->request_close();
    cursor->join();
    cursor.reset();
    check(scope.budget()[0] == 1 && scope.budget()[3] == 1, "transferred batch retains cursor reservation");
    check(Batch::parse(buffer->bytes, expected).rows() == 3, "owned batch stays readable after cursor close");
    const auto reads = scope.probe->reads.load();
    check(reject([&] { (void)scope.open(0); }).code == 6, "held batch prevents fresh cursor admission");
    check(scope.probe->reads == reads, "capacity rejection happens before provider SQL");
    buffer.reset();
    check(scope.budget()[1] == 0, "last batch returns inherited reservation");
}

void arena_lookahead_and_end()
{
    {
        Scope scope;
        auto cursor = scope.open(5);
        auto first = cursor->fetch(std::make_shared<Cancel>(), 3, 256);
        check(BatchView::parse(first->bytes, cursor->expectation).rows() == 1 && scope.probe->reads == 2,
              "byte-bound batch retains exactly one provider row as lookahead");
        check(!first->known_end && cursor->rows_pulled == 2, "held provider row is counted once without EOF claim");
        first.reset();
        auto canceled = std::make_shared<Cancel>();
        canceled->request();
        check(reject([&] { (void)cursor->fetch(canceled, 3, 256); }).status == NW_CANCELED,
              "held row does not bypass canceled fetch admission");
        check(scope.probe->reads == 2, "canceled held-row fetch performs no new step");
        auto second = cursor->fetch(std::make_shared<Cancel>(), 1, 256);
        check(BatchView::parse(second->bytes, cursor->expectation).rows() == 1 && scope.probe->reads == 2 &&
                  cursor->rows_pulled == 2,
              "next fetch consumes the held row without executing its function again");
        check(!second->known_end, "row-limit completion does not probe or claim EOF");
        second.reset();
        auto third = cursor->fetch(std::make_shared<Cancel>(), 3, 256);
        check(scope.probe->reads == 4 && cursor->rows_pulled == 4,
              "further byte-bound fetch never retains more than one provider row");
        third.reset();
        cursor->close_now();
        check(scope.probe->reads == 4, "closing a held SQLite row does not step the remainder");
    }
    for (const auto rows : {0U, 1U, 3U})
    {
        Scope scope;
        scope.probe->length = 10;
        auto cursor = scope.open(rows);
        auto buffer = cursor->fetch(std::make_shared<Cancel>(), 3, 256);
        check(BatchView::parse(buffer->bytes, cursor->expectation).rows() == rows,
              "EOF marker retains full business cardinality");
        check(buffer->known_end == (rows < 3), "only an observed provider end marks the transferred batch");
        check(scope.probe->reads == rows, "end knowledge adds no provider row prefetch");
        buffer.reset();
        if (rows == 3)
        {
            auto end = cursor->fetch(std::make_shared<Cancel>(), 3, 256);
            check(end->known_end && BatchView::parse(end->bytes, cursor->expectation).rows() == 0,
                  "exact row boundary keeps the legacy empty EOF fetch correct");
        }
        cursor->close_now();
    }
}

void known_end_read_admission()
{
    Scope scope;
    scope.probe->length = 10;
    auto cursor = scope.open(1);
    auto buffer = cursor->fetch(std::make_shared<Cancel>(), 3, 256);
    check(buffer->known_end, "read admission fixture observes natural EOF");
    check(reject([&] { cursor->check_read_admission(); }).status == NW_INVALID_STATE,
          "read admission preserves the live previous batch rejection");
    buffer.reset();
    const auto operations = operation_count.load();
    const auto executions = provider_dispatch_count();
    const auto submissions = provider_protocol_count();
    const auto reads = scope.probe->reads.load();
    const auto batches = scope.budget()[6];
    cursor->check_read_admission();
    check(!cursor->active.load() && operation_count == operations && provider_dispatch_count() == executions &&
              provider_protocol_count() == submissions && scope.probe->reads == reads && scope.budget()[6] == batches,
          "known EOF admission performs no operation allocation, provider read, batch or protocol work");
    cursor->active.store(true);
    const auto concurrent = reject([&] { cursor->check_read_admission(); });
    check(concurrent.status == NW_DOMAIN_ERROR && concurrent.code == 7 && cursor->active.load(),
          "concurrent read admission rejects without releasing another caller's gate");
    cursor->active.store(false);
    scope.source->poisoned.store(true);
    check(reject([&] { cursor->check_read_admission(); }).status == NW_ENGINE_POISONED,
          "known EOF still observes an independently poisoned native source");
    scope.source->poisoned.store(false);
    scope.session->request_close();
    scope.session->join();
    const auto closed = reject([&] { cursor->check_read_admission(); });
    check(closed.status == NW_INVALID_STATE && closed.code == 8,
          "parent session closure retains the original closed cursor status");
    scope.source->poisoned.store(true);
    check(reject([&] { cursor->check_read_admission(); }).status == NW_ENGINE_POISONED,
          "source admission remains earlier than closed cursor admission");
    scope.source->poisoned.store(false);

    Scope empty;
    auto ended = empty.open(0);
    auto end = ended->fetch(std::make_shared<Cancel>(), 3, 256);
    end.reset();
    check(reject([&] { ended->check_read_admission(); }).status == NW_INVALID_STATE,
          "read admission does not resurrect an already transferred empty end");
    ended->close_now();
}

void arena_lookahead_error()
{
    Scope scope;
    scope.probe->length = 40;
    scope.shape = std::make_shared<Projection>(Projection::parse(
        text_view("{\"version\":1,\"fields\":[{\"ordinal\":0,\"name\":\"value\",\"codec\":5,\"codec_config\":null,"
                  "\"nullable\":true},{\"ordinal\":1,\"name\":\"other\",\"codec\":4,\"codec_config\":null,\"nullable\":"
                  "false}]}"),
        Profile::Sqlite));
    auto cursor = scope.open(2, "WITH n(x) AS (VALUES(1),(2)) SELECT fixture_value(x) AS value,"
                                "CASE WHEN x=1 THEN 1 ELSE 2 END AS other FROM n");
    check(reject([&] { (void)cursor->fetch(std::make_shared<Cancel>(), 3, 256); }).code == 3,
          "late conversion failure in a full lookahead row wins over earlier-row publication");
    check(scope.probe->reads == 2 && scope.budget()[3] == 0,
          "lookahead conversion failure transfers no partial batch or extra row");
    cursor->join();
    cursor.reset();
    check(scope.budget()[1] == 0, "failed arena and held provider row retire their reservation");
}

void field_and_shape()
{
    Scope scope;
    scope.probe->length = 129;
    auto cursor = scope.open(5);
    check(reject([&] { (void)cursor->fetch(std::make_shared<Cancel>(), 3, 256); }).code == 14,
          "oversized field rejected");
    cursor->join();
    check(scope.probe->reads == 1 && cursor->retired, "oversized first field stops before extra reads");
    cursor.reset();
    check(scope.budget()[1] == 0, "field failure returns reservation after actual close");
    check(reject([&] { (void)scope.open(0, "SELECT 1 AS missing_nullable WHERE 0"); }).code == 4,
          "empty complete metadata mismatch rejects at open");
    check(scope.budget()[1] == 0, "shape failure leaves no reserved owner");
    scope.shape = std::make_shared<Projection>(Projection::parse(
        text_view("{\"version\":1,\"fields\":[{\"ordinal\":0,\"name\":\"value\",\"codec\":5,\"codec_config\":null,"
                  "\"nullable\":true},{\"ordinal\":1,\"name\":\"other\",\"codec\":5,\"codec_config\":null,\"nullable\":"
                  "true}]}"),
        Profile::Sqlite));
    cursor = scope.open(1, "SELECT replace(hex(zeroblob(250)),'0','x') AS value,1 AS other");
    check(reject([&] { (void)cursor->fetch(std::make_shared<Cancel>(), 3, 256); }).code == 4,
          "complete dynamic shape preflight precedes the first oversized scalar");
    cursor->join();
    cursor.reset();
    cursor = scope.open(1, "SELECT replace(hex(zeroblob(40)),'0','x') AS value,"
                           "replace(hex(zeroblob(40)),'0','x') AS other");
    check(reject([&] { (void)cursor->fetch(std::make_shared<Cancel>(), 3, 256); }).code == 14,
          "wide row enforces the remaining aggregate payload budget per column");
    cursor->join();
    cursor.reset();
    check(scope.budget()[1] == 0 && scope.budget()[3] == 0, "wide-row refusal returns staging and wire credits");
}
void abandoned_close()
{
    Scope scope;
    auto cursor = scope.open(5);
    auto proceed = std::make_shared<std::binary_semaphore>(0);
    auto entered = std::make_shared<std::atomic<bool>>(false);
    cursor->test_retire_gate = [proceed, entered]
    {
        entered->store(true);
        proceed->acquire();
    };
    auto owner = std::make_shared<Owner>(scope.source, cursor);
    auto completion = std::make_shared<std::promise<void>>();
    auto business_wait = completion->get_future();
    std::thread(
        [cursor, completion]
        {
            cursor->join();
            completion->set_value();
        })
        .detach();
    std::weak_ptr<Cursor> weak = cursor;
    owner.reset();
    cursor.reset();
    await([&] { return entered->load(); });
    check(!weak.expired() && scope.budget()[0] == 1, "abandoned close retains provider and bounded charge");
    // Dropping a bounded business wait does not cancel the retained close job.
    check(business_wait.wait_for(std::chrono::milliseconds(0)) == std::future_status::timeout,
          "bounded business waiter sees cleanup still pending");
    business_wait = {};
    check(scope.budget()[1] > 0 && scope.session->active, "unobserved retirement cannot admit a replacement");
    proceed->release();
    await([&] { return weak.expired() && scope.budget()[0] == 0; });
    check(scope.budget()[0] == 0 && scope.budget()[1] == 0, "late close receipt releases exact charge");
}
void partial_fetch_cancel()
{
    Scope scope;
    scope.probe->length = 5;
    scope.probe->stop_at = 2;
    auto cursor = scope.open(5);
    auto cancel = std::make_shared<Cancel>();
    auto work = std::async(std::launch::async, [cursor, cancel] { return cursor->fetch(cancel, 3, 256); });
    await([&] { return scope.probe->reads == 2; });
    check(reject([&] { (void)cursor->fetch(std::make_shared<Cancel>(), 3, 256); }).code == 7,
          "partial in-flight fetch rejects resume");
    check(scope.budget()[0] == 1 && scope.budget()[3] == 0, "partial staging has no transferable batch");
    cursor->request_close();
    check(!cursor->retired && scope.budget()[1] > 0, "canceled accessor retains reservation");
    scope.probe->proceed.release();
    check(reject([&] { (void)work.get(); }).status == NW_CANCELED, "actual SQLite accessor confirms cancel");
    cursor->join();
    cursor.reset();
    check(scope.budget()[1] == 0, "partial failure cleanup returns credits");
}
std::future<void> retirement_waiter(std::shared_ptr<Cursor> cursor, bool synchronous_close = false)
{
    auto completion = std::make_shared<std::promise<void>>();
    auto future = completion->get_future();
    std::thread(
        [cursor = std::move(cursor), completion, synchronous_close]() mutable
        {
            try
            {
                if (synchronous_close)
                    cursor->close_now();
                cursor->join();
                // The test's own waiting thread is also an actual owner.
                cursor.reset();
                completion->set_value();
            }
            catch (...)
            {
                cursor.reset();
                completion->set_exception(std::current_exception());
            }
        })
        .detach();
    return future;
}
void retirement_worker_owner_barrier()
{
    Scope scope;
    auto cursor = scope.open(0);
    auto entered = std::make_shared<std::atomic<bool>>(false);
    auto waiting = std::make_shared<std::atomic<bool>>(false);
    auto proceed = std::make_shared<std::binary_semaphore>(0);
    cursor->test_after_cleanup_gate = [entered, proceed]
    {
        entered->store(true);
        proceed->acquire();
    };
    cursor->test_before_join_wait = [waiting] { waiting->store(true); };
    cursor->request_close();
    await([&] { return entered->load(); });
    auto joined = retirement_waiter(cursor);
    auto closed = retirement_waiter(cursor, true);
    await([&]
          { return waiting->load() || joined.wait_for(std::chrono::milliseconds(0)) == std::future_status::ready; });
    const bool pending = joined.wait_for(std::chrono::milliseconds(0)) == std::future_status::timeout &&
                         closed.wait_for(std::chrono::milliseconds(0)) == std::future_status::timeout;
    proceed->release();
    check(joined.wait_for(std::chrono::seconds(3)) == std::future_status::ready &&
              closed.wait_for(std::chrono::seconds(3)) == std::future_status::ready,
          "concurrent cursor close/join did not finish after actual cleanup");
    joined.get();
    closed.get();
    cursor.reset();
    check(pending, "cursor join published while the cleanup worker still owned its reservation");
    check(scope.budget()[0] == 0 && scope.budget()[1] == 0,
          "join followed by last owner release must return stream credits immediately");
}
void retirement_notification_barrier()
{
    Scope scope;
    auto cursor = scope.open(0);
    auto entered = std::make_shared<std::atomic<bool>>(false);
    auto cleaned = std::make_shared<std::atomic<bool>>(false);
    auto proceed = std::make_shared<std::binary_semaphore>(0);
    cursor->test_before_join_wait = [entered, proceed]
    {
        if (!entered->exchange(true))
            proceed->acquire();
    };
    cursor->test_after_cleanup_gate = [cleaned] { cleaned->store(true); };
    auto joined = retirement_waiter(cursor);
    // Pause in the exact predicate-check / condition-variable-wait window.
    await([&] { return entered->load(); });
    cursor->request_close();
    // Cleanup has returned and its worker released the cursor, so publication
    // must now contend with the waiter's completion mutex, not notify past it.
    await([&] { return cleaned->load() && cursor.use_count() == 2; });
    proceed->release();
    check(joined.wait_for(std::chrono::seconds(3)) == std::future_status::ready,
          "cursor retirement lost its notification between predicate check and wait");
    joined.get();
    cursor->request_close();
    cursor->join();
    cursor.reset();
    check(scope.budget()[0] == 0 && scope.budget()[1] == 0,
          "notification barrier retains no worker owner after joined retirement");
}
struct PullReady
{
    std::atomic<std::uint64_t> generation{0};
    std::atomic<unsigned> calls{0};
    std::function<void()> before_return;
    static void NW_CDECL notify(void* context, std::uint64_t value)
    {
        auto& state = *static_cast<PullReady*>(context);
        ++state.calls;
        state.generation.store(value);
        if (state.before_return)
            state.before_return();
    }
    nw_db_cursor_callbacks_v1 callbacks()
    {
        return {sizeof(nw_db_cursor_callbacks_v1), 0, this, notify};
    }
    void wait(std::uint64_t value)
    {
        await([&] { return calls.load() != 0 && generation.load() == value; });
    }
};
bool snapshot_released(const Bytes& bytes)
{
    return Fields::parse(bytes, 7,
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
void pull_final_close_results()
{
    for (const auto rows : {0u, 1u, 7u})
    {
        Scope scope;
        auto pull = scope.pull(rows);
        PullReady fetched, closed;
        check(reject([&] { (void)pull->close_take(); }).status == NW_INVALID_STATE,
              "missing final-close result is not synthesized");
        auto invalid = closed.callbacks();
        invalid.reserved = 1;
        check(reject([&] { pull->close_request(invalid); }).status == NW_INVALID_ARGUMENT && closed.calls == 0,
              "invalid final callback is rejected without admission or notice");
        if (rows)
        {
            pull->request(1, 1, 256, fetched.callbacks());
            fetched.wait(1);
            if (rows == 1)
                pull->take(1).reset();
            // Seven rows leave the accepted batch unconsumed for close to drop.
        }
        auto entered = std::make_shared<std::atomic<bool>>(false);
        auto proceed = std::make_shared<std::binary_semaphore>(0);
        pull->test_before_close_work = [entered, proceed]
        {
            entered->store(true);
            proceed->acquire();
        };
        pull->close_request(closed.callbacks());
        await([&] { return entered->load(); });
        const auto pending = reject([&] { (void)pull->close_take(); }).status;
        const auto duplicate = reject([&] { pull->close_request(closed.callbacks()); }).status;
        const auto stopped = reject([&] { pull->request(rows ? 2 : 1, 1, 256, fetched.callbacks()); }).status;
        proceed->release();
        closed.wait(0);
        auto result = pull->close_take();
        const bool released = snapshot_released(result->bytes);
        result.reset();
        check(reject([&] { (void)pull->close_take(); }).status == NW_INVALID_STATE &&
                  reject([&] { (void)pull->take_error(0); }).status == NW_INVALID_STATE,
              "final receipt transfers once and cannot turn into an error result");
        pull->join();
        check(released && snapshot_released(pull->snapshot()) && closed.calls == 1 && pending == NW_PENDING &&
                  duplicate == NW_INVALID_STATE && stopped == NW_INVALID_STATE,
              "one-shot final ready independently stops admission and returns a joined receipt");
        pull.reset();
        check(operation_count == 0 && pull_count == 0 && cursor_count == 0 && buffer_count == 0 &&
                  scope.source->pending == 0 && scope.source->pull_workers->done() && !scope.session->active.load() &&
                  scope.probe->reads == (rows ? 1u : 0u),
              "normal final close creates no generic operation, performs no extra read and returns every owner");
    }
}
void pull_close_joins_old_fetch_tail()
{
    Scope scope;
    auto pull = scope.pull(7);
    auto held = std::make_shared<std::atomic<bool>>(false);
    auto proceed = std::make_shared<std::binary_semaphore>(0);
    pull->test_after_ready = [held, proceed]
    {
        held->store(true);
        proceed->acquire();
    };
    PullReady fetched, closed;
    pull->request(1, 1, 256, fetched.callbacks());
    fetched.wait(1);
    await([&] { return held->load(); });
    pull->take(1).reset();
    pull->close_request(closed.callbacks());
    const bool pending = reject([&] { (void)pull->close_take(); }).status == NW_PENDING;
    const bool not_released = !snapshot_released(pull->snapshot());
    auto fallback = std::async(std::launch::async, [pull] { pull->join(); });
    const bool waiting = fallback.wait_for(std::chrono::milliseconds(20)) != std::future_status::ready;
    proceed->release();
    closed.wait(0);
    auto result = pull->close_take();
    const bool released = snapshot_released(result->bytes);
    result.reset();
    fallback.get();
    pull.reset();
    check(pending && not_released && waiting && released && closed.calls == 1,
          "final readiness and generic fallback both await the old detached fetch tail");
    check(pull_count == 0 && cursor_count == 0 && scope.source->pending == 0,
          "old-tail final close leaves no native accessor or admission");
}
void pull_close_notification_tail()
{
    Scope scope;
    auto pull = scope.pull(0);
    struct Held
    {
        std::atomic<bool> entered{false};
        std::atomic<std::uint64_t> generation{1};
        std::binary_semaphore proceed{0};
        static void NW_CDECL notify(void* context, std::uint64_t generation)
        {
            auto& held = *static_cast<Held*>(context);
            held.generation.store(generation);
            held.entered.store(true);
            held.proceed.acquire();
        }
    } held;
    pull->close_request({sizeof(nw_db_cursor_callbacks_v1), 0, &held, Held::notify});
    await([&] { return held.entered.load(); });
    auto result = pull->close_take();
    const bool direct_released = snapshot_released(result->bytes);
    result.reset();
    const bool public_pending = !snapshot_released(pull->snapshot());
    scope.session->request_close();
    auto fallback = std::async(std::launch::async, [pull] { pull->join(); });
    auto parent = std::async(std::launch::async, [source = scope.source] { source->join(); });
    const bool fallback_pending = fallback.wait_for(std::chrono::milliseconds(20)) != std::future_status::ready;
    const bool parent_pending = parent.wait_for(std::chrono::milliseconds(20)) != std::future_status::ready;
    held.proceed.release();
    fallback.get();
    parent.get();
    check(direct_released && public_pending && fallback_pending && parent_pending && held.generation == 0 &&
              snapshot_released(pull->snapshot()) && scope.source->pull_workers->done(),
          "direct receipt excludes its own detached notification while generic snapshot/join and Source wait it");
    pull.reset();
    check(pull_count == 0 && cursor_count == 0 && scope.source->pending == 0,
          "final callback return retires both independent ledgers without a channel access");
}
void pull_close_rejection_and_errors()
{
    for (const bool allocation : {true, false})
    {
        Scope scope;
        auto pull = scope.pull(3, "SELECT value FROM missing_pull_table");
        PullReady fetched, closed;
        pull->request(1, 1, 256, fetched.callbacks());
        fetched.wait(1);
        const auto fetch_error = reject([&] { (void)pull->take(1); });
        scope.source->controls.test_before_post = [allocation]
        {
            if (allocation)
                throw std::bad_alloc();
            throw std::runtime_error("controlled close queue failure");
        };
        pull->close_request(closed.callbacks());
        closed.wait(0);
        const auto error = reject([&] { (void)pull->close_take(); });
        const auto close_rich = pull->take_error(0);
        const auto fetch_rich = pull->take_error(1);
        check(error.status == (allocation ? NW_OUT_OF_MEMORY : NW_NATIVE_PANIC) &&
                  close_rich.correlation == error.correlation && fetch_rich.correlation == fetch_error.correlation &&
                  fetch_error.code == 1 && fetched.calls == 1 && closed.calls == 1,
              "accepted close queue failure preserves independent close and positive-generation rich errors");
        check(reject([&] { (void)pull->take_error(0); }).status == NW_INVALID_STATE &&
                  reject([&] { (void)pull->close_take(); }).status == NW_INVALID_STATE,
              "close error is consumed once and never fabricates a successful result");
        scope.source->controls.test_before_post = {};
        pull->join();
        pull.reset();
        check(scope.source->pending == 0 && scope.source->pull_workers->done() && pull_count == 0 &&
                  scope.source->poisoned.load() == !allocation,
              "close queue failure settles its exact notification and reservations");
    }
    {
        Scope scope;
        auto pull = scope.pull(0);
        PullReady closed;
        Error original(NW_DOMAIN_ERROR, 1, "primary close");
        original.provider = "57P01";
        original.cleanup = std::make_shared<Error>(NW_NATIVE_PANIC, 8, "driver cleanup");
        scope.source->controls.test_before_post = [original] { throw original; };
        pull->close_request(closed.callbacks());
        closed.wait(0);
        const auto primary = reject([&] { (void)pull->close_take(); });
        const auto rich = pull->take_error(0);
        check(primary.status == original.status && primary.code == original.code &&
                  primary.correlation == original.correlation && rich.provider == original.provider &&
                  rich.cleanup == original.cleanup && rich.correlation == original.correlation &&
                  scope.source->poisoned.load() && scope.session->discard.load(),
              "original close SQLSTATE/correlation/cleanup identity survives queue failure and its poison evidence");
        check(reject([&] { (void)pull->take_error(0); }).status == NW_INVALID_STATE && closed.calls == 1,
              "rich provider close error is consumed once after its single notification");
        scope.source->controls.test_before_post = {};
        pull->join();
        pull.reset();
    }
    {
        Scope scope;
        auto pull = scope.pull(0);
        PullReady closed;
        scope.source->join();
        check(scope.source->admission_stop &&
                  reject([&] { pull->close_request(closed.callbacks()); }).status == NW_INVALID_STATE &&
                  closed.calls == 0 && reject([&] { (void)pull->close_take(); }).status == NW_INVALID_STATE,
              "Source admission_stop rejects final notification without a late ledger ticket or callback");
        pull->join();
        check(snapshot_released(pull->snapshot()) && scope.source->pull_workers->done(),
              "frozen cold join remains available after final-request admission rejection");
        pull.reset();
    }
}
void pull_close_cleanup_failure()
{
    for (const bool queue_failure : {false, true})
    {
        const auto child = fork();
        check(child >= 0, "final-close quarantine subprocess starts");
        if (child == 0)
        {
            try
            {
                auto* scope = new Scope;
                auto pull = scope->pull(3);
                PullReady fetched, closed;
                pull->request(1, 1, 256, fetched.callbacks());
                fetched.wait(1);
                auto batch = pull->take(1);
                if (!queue_failure)
                    batch->cursor->test_retire_gate = [] { throw Error::state("controlled unconfirmed cleanup"); };
                batch.reset();
                if (queue_failure)
                {
                    scope->source->controls.test_before_post = [] { throw std::bad_alloc(); };
                    scope->source->workers.test_before_post = [] { throw std::bad_alloc(); };
                }
                pull->close_request(closed.callbacks());
                closed.wait(0);
                const auto error = reject([&] { (void)pull->close_take(); });
                const auto rich = pull->take_error(0);
                check(error.status == (queue_failure ? NW_OUT_OF_MEMORY : NW_DOMAIN_ERROR) && error.code == 8 &&
                          rich.correlation == error.correlation && rich.cleanup && closed.calls == 1 &&
                          scope->source->poisoned.load() && scope->session->discard.load(),
                      "cleanup failure produces one original rich close error and excludes provider reuse");
                check(reject([&] { pull->join(); }).code == 8 && !snapshot_released(pull->snapshot()),
                      "error readiness and frozen generic join never claim unconfirmed retirement is safe");
                pull.reset();
                check(scope->source->pending == 0 && scope->source->pull_workers->done() && pull_count == 0 &&
                          cursor_count == 1 && scope->budget()[0] == 1,
                      "cleanup failure retains only its bounded quarantine, not a final notification ticket");
                _exit(0);
            }
            catch (const std::exception& error)
            {
                std::cerr << "FAIL final close subprocess: " << error.what() << '\n';
                _exit(1);
            }
        }
        int status = 0;
        check(waitpid(child, &status, 0) == child && WIFEXITED(status) && WEXITSTATUS(status) == 0,
              "unconfirmed final-close isolation assertions");
    }
}
void pull_generations_and_tail()
{
    Scope scope;
    auto pull = scope.pull(7);
    check(scope.probe->reads == 0 && cursor_count == 0, "lazy pull creation does not open or step a cursor");
    PullReady ready;
    check(reject([&] { pull->request(0, 1, 256, ready.callbacks()); }).status == NW_INVALID_STATE,
          "zero generation is rejected without a callback");
    check(reject([&] { pull->request(2, 1, 256, ready.callbacks()); }).status == NW_INVALID_STATE,
          "skipped generation is rejected without a callback");
    check(reject([&] { pull->request(1, 0, 256, ready.callbacks()); }).status == NW_INVALID_ARGUMENT,
          "invalid request bounds preserve the generation");
    auto callbacks = ready.callbacks();
    callbacks.reserved = 1;
    check(reject([&] { pull->request(1, 1, 256, callbacks); }).status == NW_INVALID_ARGUMENT,
          "invalid callback record creates no native callback obligation");
    check(ready.calls == 0 && scope.probe->reads == 0, "rejected requests have no callback or provider effects");

    auto blocked = std::make_shared<std::atomic<bool>>(false);
    auto proceed = std::make_shared<std::binary_semaphore>(0);
    pull->test_after_ready = [blocked, proceed]
    {
        if (!blocked->exchange(true))
            proceed->acquire();
    };
    pull->request(1, 1, 256, ready.callbacks());
    ready.wait(1);
    await([&] { return blocked->load(); });
    const bool no_generic_operation = operation_count == 0;
    const auto mismatch = reject([&] { (void)pull->take(2); }).status;
    auto first = pull->take(1);
    const auto first_rows = Batch::parse(first->bytes, first->cursor->expectation).rows();
    const auto still_live = reject([&] { pull->request(2, 1, 256, ready.callbacks()); }).status;
    first.reset();
    // Readiness has cleared CursorReadAdmission even while generation one's
    // independent callback-return/worker tail is held by the deterministic gate.
    pull->request(2, 1, 256, ready.callbacks());
    ready.wait(2);
    auto second = pull->take(2);
    const auto second_rows = Batch::parse(second->bytes, second->cursor->expectation).rows();
    second.reset();
    const auto stale = reject([&] { pull->request(2, 1, 256, ready.callbacks()); }).status;
    auto entered = std::make_shared<std::atomic<bool>>(false);
    auto joined = std::async(std::launch::async,
                             [pull, entered]
                             {
                                 entered->store(true);
                                 pull->join();
                             });
    await([&] { return entered->load(); });
    const bool pending = joined.wait_for(std::chrono::milliseconds(20)) != std::future_status::ready;
    proceed->release();
    joined.get();
    pull.reset();
    check(no_generic_operation, "per-batch pull requests allocate no generic native operation");
    check(mismatch == NW_INVALID_STATE && first_rows == 1 && second_rows == 1,
          "generation-checked batches transfer exactly their rows");
    check(still_live == NW_INVALID_STATE && stale == NW_INVALID_STATE,
          "a live batch and a stale generation cannot be reused");
    check(ready.calls == 2 && scope.probe->reads == 2, "each accepted generation notifies once without prefetch");
    check(pending, "final pull close joins independent old-generation tails");
    check(pull_count == 0 && cursor_count == 0 && scope.budget()[0] == 0,
          "joined pull drops its native owner and stream reservation immediately");
}
void prepared_pull_generations()
{
    Scope scope;
    auto pull = scope.pull(3);
    PullReady ready;
    auto posts = std::make_shared<std::atomic<unsigned>>(0);
    scope.source->workers.test_before_post = [posts] { ++*posts; };
    check(reject([&] { pull->prepare(0, 1, 256, ready.callbacks()); }).status == NW_INVALID_STATE,
          "prepared zero generation preserves admission");
    check(reject([&] { pull->prepare(1, 0, 256, ready.callbacks()); }).status == NW_INVALID_ARGUMENT,
          "prepared invalid capacity has no accepted scheduling obligation");
    pull->prepare(1, 1, 256, ready.callbacks());
    check(scope.probe->reads == 0 && cursor_count == 0 && ready.calls == 0 && *posts == 0 &&
              scope.source->pending == 1 && !scope.source->pull_workers->done(),
          "prepare owns bounded native retirement before scheduling without queueing or provider I/O");
    check(reject([&] { pull->prepare(2, 1, 256, ready.callbacks()); }).code == 7 &&
              reject([&] { CursorPull::run_prepared(pull, 2); }).status == NW_INVALID_STATE,
          "an unclaimed generation cannot be replaced or run under another identity");
    CursorPull::run_prepared(pull, 1);
    ready.wait(1);
    check(*posts == 0 && scope.probe->reads == 1 && scope.source->pending == 0 && scope.source->pull_workers->done(),
          "synchronous prepared run retires its native accessors without posting a provider job");
    check(reject([&] { CursorPull::run_prepared(pull, 1); }).status == NW_INVALID_STATE &&
              reject([&] { CursorPull::abandon_prepared(pull, 1); }).status == NW_INVALID_STATE,
          "run and abandon share a single consumed claim");
    auto first = pull->take(1);
    check(Batch::parse(first->bytes, first->cursor->expectation).rows() == 1 &&
              reject([&] { pull->prepare(2, 1, 256, ready.callbacks()); }).status == NW_INVALID_STATE,
          "prepared execution retains the original live-batch read admission");
    first.reset();
    pull->prepare(2, 1, 256, ready.callbacks());
    check(reject([&] { CursorPull::run_prepared(pull, 1); }).status == NW_INVALID_STATE,
          "late old-generation work cannot steal the next prepared request");
    CursorPull::run_prepared(pull, 2);
    auto second = pull->take(2);
    check(Batch::parse(second->bytes, second->cursor->expectation).rows() == 1 && ready.calls == 2 &&
              scope.probe->reads == 2 && *posts == 0,
          "prepared generations preserve exact rows, callbacks and one-time take");
    second.reset();
    scope.source->workers.test_before_post = {};
    pull->join();
    pull.reset();
    check(pull_count == 0 && cursor_count == 0 && buffer_count == 0, "prepared successful pulls return every owner");
}

void prepared_pull_never_run()
{
    // Explicit queue abandonment, pull stop, Session stop, Source stop and
    // final native owner release all have to settle an accepted never-run job.
    for (const int mode : {0, 1, 2, 3, 4})
    {
        Scope scope;
        auto pull = scope.pull(3);
        auto dispatches = std::make_shared<std::atomic<unsigned>>(0);
        pull->test_before_work = [dispatches] { ++*dispatches; };
        PullReady ready;
        pull->prepare(1, 1, 256, ready.callbacks());
        check(scope.source->pending == 1, "never-run work has an admitted pending reservation");
        if (mode == 0)
            CursorPull::abandon_prepared(pull, 1);
        else if (mode == 1)
            pull->request_close();
        else if (mode == 2)
            scope.session->request_close();
        else if (mode == 3)
            scope.source->request_close();
        else
        {
            auto owner = std::make_shared<Owner>(scope.source, pull);
            owner.reset();
        }
        ready.wait(1);
        const auto failure = reject([&] { (void)pull->take(1); });
        const auto rich = pull->take_error(1);
        check(failure.status == (mode == 2 || mode == 3 ? NW_INVALID_STATE : NW_CANCELED) &&
                  rich.status == failure.status && rich.correlation == failure.correlation,
              "never-run settlement preserves parent/cancel error ordering and rich identity");
        check(scope.probe->reads == 0 && *dispatches == 0 && ready.calls == 1 && scope.source->pending == 0 &&
                  scope.source->pull_workers->done() && !scope.session->active.load(),
              "never-run settlement executes no provider dispatch and releases both retirement tickets");
        if (mode != 0)
            CursorPull::run_prepared(pull, 1);
        check(reject([&] { CursorPull::abandon_prepared(pull, 1); }).status == NW_INVALID_STATE && ready.calls == 1 &&
                  scope.probe->reads == 0,
              "automatic close leaves exactly one harmless late queue claim");
        pull->join();
        pull.reset();
        check(pull_count == 0 && cursor_count == 0 && buffer_count == 0,
              "abandoned prepared self-ownership is actively broken");
    }
}

void prepared_pull_running_retirement()
{
    Scope scope;
    auto pull = scope.pull(1);
    auto entered = std::make_shared<std::atomic<bool>>(false);
    auto proceed = std::make_shared<std::binary_semaphore>(0);
    pull->test_after_ready = [entered, proceed]
    {
        entered->store(true);
        proceed->acquire();
    };
    PullReady ready;
    pull->prepare(1, 1, 256, ready.callbacks());
    std::future<void> executing, joined;
    bool pending = false;
    {
        struct Release
        {
            std::shared_ptr<std::binary_semaphore> proceed;
            ~Release()
            {
                proceed->release();
            }
        } release{proceed};
        // Transfer the execution caller's only extra channel reference into the
        // static entry exactly as the ABI does. The retained owner stays here.
        executing = std::async(std::launch::async,
                               [current = pull]() mutable { CursorPull::run_prepared(std::move(current), 1); });
        ready.wait(1);
        await([&] { return entered->load(); });
        auto batch = pull->take(1);
        batch.reset();
        joined = std::async(std::launch::async, [source = scope.source] { source->join(); });
        await([&] { return scope.source->closing.load(); });
        pending = joined.wait_for(std::chrono::milliseconds(20)) != std::future_status::ready &&
                  !scope.source->retired.load() && scope.source->pending == 1 && !scope.source->pull_workers->done();
    }
    executing.get();
    joined.get();
    check(pending && scope.source->retired.load() && scope.source->pending == 0 && scope.source->pull_workers->done(),
          "Source join waits for the prepared execution frame's last native accessor, not only readiness");
    pull.reset();
    check(pull_count == 0 && cursor_count == 0 && buffer_count == 0,
          "prepared exported execution leaves no accessor after joined retirement");
}

void prepared_pull_concurrent_claim()
{
    for (const bool abandon : {false, true})
    {
        Scope scope;
        auto pull = scope.pull(1);
        PullReady ready;
        pull->prepare(1, 1, 256, ready.callbacks());
        std::counting_semaphore<2> start{0};
        std::array<nw_status, 2> status{};
        auto claimant = [&](std::size_t index)
        {
            start.acquire();
            try
            {
                if (abandon && index == 1)
                    CursorPull::abandon_prepared(pull, 1);
                else
                    CursorPull::run_prepared(pull, 1);
                status[index] = NW_OK;
            }
            catch (const Error& error)
            {
                status[index] = error.status;
            }
        };
        std::thread first(claimant, 0), second(claimant, 1);
        start.release(2);
        first.join();
        second.join();
        ready.wait(1);
        check((status[0] == NW_OK && status[1] == NW_INVALID_STATE) ||
                  (status[1] == NW_OK && status[0] == NW_INVALID_STATE),
              "racing run/run and run/abandon each consume exactly one prepared claim");
        if (abandon && status[1] == NW_OK)
        {
            check(reject([&] { (void)pull->take(1); }).status == NW_CANCELED &&
                      pull->take_error(1).status == NW_CANCELED && scope.probe->reads == 0,
                  "winning abandonment has no provider effect");
        }
        else
        {
            auto batch = pull->take(1);
            check(scope.probe->reads == 1, "winning execution reads once despite duplicate callers");
        }
        check(ready.calls == 1 && scope.source->pending == 0 && scope.source->pull_workers->done(),
              "racing prepared claim publishes one terminal and retires once");
        pull->join();
        pull.reset();
    }
}

void prepared_pull_inflight_stop()
{
    for (const bool external : {false, true})
    {
        Scope scope;
        scope.probe->stop_at = 1;
        auto pull = scope.pull(3);
        PullReady ready;
        std::future<void> executing;
        bool held = false;
        {
            struct Release
            {
                std::shared_ptr<Probe> probe;
                ~Release()
                {
                    probe->proceed.release();
                }
            } release{scope.probe};
            // Require another row after the held provider call. Cancellation
            // racing the last successful row may preserve that finished batch;
            // this gate observes cancellation before unfinished work instead.
            if (external)
            {
                pull->prepare(1, 2, 256, ready.callbacks());
                executing = std::async(std::launch::async,
                                       [current = pull]() mutable { CursorPull::run_prepared(std::move(current), 1); });
            }
            else
                pull->request(1, 2, 256, ready.callbacks());
            await([&] { return scope.probe->reads == 1; });
            pull->request_close();
            held = ready.calls == 0 && scope.source->pending == 1 && !scope.source->pull_workers->done();
        }
        if (external)
            executing.get();
        ready.wait(1);
        const auto failure = reject([&] { (void)pull->take(1); });
        const auto rich = pull->take_error(1);
        check(held && failure.status == NW_CANCELED && rich.status == NW_CANCELED && ready.calls == 1,
              "legacy and prepared cancellation retain the active provider until unfinished native work exits");
        pull->join();
        pull.reset();
        check(scope.source->pending == 0 && scope.source->pull_workers->done() && pull_count == 0 && cursor_count == 0,
              "in-flight prepared cancellation retires the original provider/read admission");
    }
}

void prepared_pull_late_claim_during_close_callback()
{
    Scope scope;
    auto pull = scope.pull(3);
    auto entered = std::make_shared<std::atomic<bool>>(false);
    auto proceed = std::make_shared<std::binary_semaphore>(0);
    PullReady ready;
    ready.before_return = [entered, proceed]
    {
        entered->store(true);
        proceed->acquire();
    };
    pull->prepare(1, 1, 256, ready.callbacks());
    std::future<void> stopped, joined;
    bool held = false;
    {
        struct Release
        {
            std::shared_ptr<std::binary_semaphore> proceed;
            ~Release()
            {
                proceed->release();
            }
        } release{proceed};
        stopped = std::async(std::launch::async, [pull] { pull->request_close(); });
        await([&] { return entered->load(); });
        CursorPull::run_prepared(pull, 1);
        check(reject([&] { CursorPull::abandon_prepared(pull, 1); }).status == NW_INVALID_STATE,
              "the one late no-op claim is consumed even while auto-close's callback is returning");
        joined = std::async(std::launch::async, [source = scope.source] { source->join(); });
        await([&] { return scope.source->closing.load(); });
        held = joined.wait_for(std::chrono::milliseconds(20)) != std::future_status::ready &&
               stopped.wait_for(std::chrono::milliseconds(0)) != std::future_status::ready &&
               scope.source->pending == 1 && !scope.source->pull_workers->done() && scope.probe->reads == 0;
    }
    stopped.get();
    joined.get();
    check(held && ready.calls == 1 && scope.source->retired.load() && scope.source->pending == 0 &&
              scope.source->pull_workers->done(),
          "late no-op claim cannot retire an independent automatic close callback frame");
    check(reject([&] { (void)pull->take(1); }).status == NW_CANCELED && pull->take_error(1).status == NW_CANCELED,
          "later Source close leaves automatic cancellation's already-frozen error unchanged");
    pull.reset();
}

void pull_first_fetch_admission()
{
    for (const auto mode : {0, 1, 2})
    {
        Scope scope;
        auto pull = scope.pull(3);
        auto entered = std::make_shared<std::atomic<bool>>(false);
        auto proceed = std::make_shared<std::binary_semaphore>(0);
        pull->test_before_first_fetch = [entered, proceed]
        {
            entered->store(true);
            proceed->acquire();
        };
        PullReady ready;
        pull->request(1, 1, 256, ready.callbacks());
        await([&] { return entered->load(); });
        const bool no_read = scope.probe->reads == 0;
        if (mode == 0)
            pull->request_close();
        else if (mode == 1)
            scope.source->poisoned.store(true);
        else
            scope.session->request_close();
        proceed->release();
        ready.wait(1);
        const auto failure = reject([&] { (void)pull->take(1); });
        const auto rich = pull->take_error(1);
        const auto duplicate = reject([&] { (void)pull->take_error(1); }).status;
        pull->join();
        pull.reset();
        check(no_read && scope.probe->reads == 0, "metadata admission and first fetch remain separate effects");
        check(failure.status == (mode == 0   ? NW_CANCELED
                                 : mode == 1 ? NW_ENGINE_POISONED
                                             : NW_INVALID_STATE),
              "first fetch preserves cancellation, source poison and Session closure priority");
        check(rich.status == failure.status && rich.correlation == failure.correlation &&
                  duplicate == NW_INVALID_STATE && ready.calls == 1,
              "failure identity survives the failed batch take and is transferred once");
        check(pull_count == 0 && cursor_count == 0 && buffer_count == 0,
              "first-fetch admission failure leaves no pull, cursor or buffer owner");
    }
}

void pull_stop_before_worker()
{
    Scope scope;
    auto pull = scope.pull(3);
    auto entered = std::make_shared<std::atomic<bool>>(false);
    auto proceed = std::make_shared<std::binary_semaphore>(0);
    pull->test_before_work = [entered, proceed]
    {
        entered->store(true);
        proceed->acquire();
    };
    PullReady ready;
    pull->request(1, 1, 256, ready.callbacks());
    await([&] { return entered->load(); });
    pull->request_close();
    const bool no_read = scope.probe->reads == 0;
    proceed->release();
    ready.wait(1);
    const auto failure = reject([&] { (void)pull->take(1); });
    const auto rich = pull->take_error(1);
    const auto duplicate = reject([&] { (void)pull->take_error(1); }).status;
    pull->join();
    pull.reset();
    if (failure.status != NW_CANCELED)
        std::cerr << "queued stop observed status=" << failure.status << " code=" << failure.code
                  << " message=" << failure.what() << " rich-status=" << rich.status << '\n';
    check(failure.status == NW_CANCELED, "accepted stop before worker dispatch preserves cancellation");
    check(no_read && scope.probe->reads == 0, "queued cancellation never opens or steps the provider");
    check(rich.status == failure.status && rich.correlation == failure.correlation && duplicate == NW_INVALID_STATE &&
              ready.calls == 1,
          "queued cancellation has one readiness and one matching rich error");
    check(pull_count == 0 && cursor_count == 0 && buffer_count == 0 && !scope.session->active.load(),
          "queued cancellation returns its channel, cursor, buffer and Session gate");
}

void pull_stop_before_wake()
{
    Scope scope;
    auto pull = scope.pull(3);
    auto entered = std::make_shared<std::atomic<bool>>(false);
    auto work = std::make_shared<std::binary_semaphore>(0);
    pull->test_before_work = [entered, work]
    {
        entered->store(true);
        work->acquire();
    };
    auto stopping = std::make_shared<std::atomic<bool>>(false);
    auto wake = std::make_shared<std::binary_semaphore>(0);
    pull->test_before_stop_wake = [stopping, wake]
    {
        stopping->store(true);
        wake->acquire();
    };
    PullReady ready;
    pull->request(1, 1, 256, ready.callbacks());
    await([&] { return entered->load(); });
    auto stop = std::async(std::launch::async, [&] { pull->request_close(); });
    bool released = false;
    std::optional<Error> failure, rich;
    try
    {
        await([&] { return stopping->load(); });
        work->release();
        released = true;
        ready.wait(1);
        failure = reject([&] { (void)pull->take(1); });
        rich = pull->take_error(1);
    }
    catch (...)
    {
        if (!released)
            work->release();
        wake->release();
        stop.wait();
        throw;
    }
    wake->release();
    stop.get();
    pull->join();
    pull.reset();
    check(failure->status == NW_CANCELED && rich->status == NW_CANCELED && ready.calls == 1,
          "stop intent is visible before its out-of-lock provider wake, with one canceled result");
    check(scope.probe->reads == 0 && pull_count == 0 && cursor_count == 0 && buffer_count == 0 &&
              !scope.session->active.load(),
          "the stop publication window cannot execute SQL or retain a request owner");
}

void pull_queued_parent_priority()
{
    for (const bool poison : {false, true})
    {
        Scope scope;
        auto pull = scope.pull(3);
        auto entered = std::make_shared<std::atomic<bool>>(false);
        auto proceed = std::make_shared<std::binary_semaphore>(0);
        pull->test_before_work = [entered, proceed]
        {
            entered->store(true);
            proceed->acquire();
        };
        PullReady ready;
        pull->request(1, 1, 256, ready.callbacks());
        await([&] { return entered->load(); });
        pull->request_close();
        if (poison)
            scope.source->poisoned.store(true);
        else
            scope.session->request_close();
        proceed->release();
        ready.wait(1);
        const auto failure = reject([&] { (void)pull->take(1); });
        const auto rich = pull->take_error(1);
        pull->join();
        pull.reset();
        check(failure.status == (poison ? NW_ENGINE_POISONED : NW_INVALID_STATE) && rich.status == failure.status &&
                  scope.probe->reads == 0 && ready.calls == 1,
              "queued Source poison and Session closure retain priority over the marked channel stop");
    }
    {
        Scope scope;
        auto pull = scope.pull(0);
        Error original(NW_DOMAIN_ERROR, 1, "original asynchronous provider error");
        original.provider = "57014";
        original.cleanup = std::make_shared<Error>(NW_NATIVE_PANIC, 8, "original cleanup");
        scope.source->workers.test_before_post = [original] { throw original; };
        PullReady ready;
        pull->request(1, 1, 256, ready.callbacks());
        ready.wait(1);
        pull->request_close();
        const auto failure = reject([&] { (void)pull->take(1); });
        const auto rich = pull->take_error(1);
        scope.source->workers.test_before_post = {};
        pull->join();
        pull.reset();
        check(failure.status == original.status && failure.code == original.code &&
                  failure.correlation == original.correlation && rich.provider == original.provider &&
                  rich.correlation == original.correlation && rich.cleanup == original.cleanup && ready.calls == 1,
              "later stop never replaces a frozen provider SQLSTATE, correlation or cleanup error");
    }
}

void closed_cursor_cancel_scope()
{
    Scope scope;
    auto cursor = scope.open(3);
    auto canceled = std::make_shared<Cancel>();
    canceled->request();
    cursor->request_close();
    check(reject([&] { (void)cursor->fetch(canceled, 1, 256, true); }).status == NW_CANCELED,
          "cursor closure after channel admission preserves the marked fetch cancellation");
    check(reject([&] { (void)cursor->fetch(canceled, 1, 256); }).status == NW_INVALID_STATE,
          "the frozen standalone fetch keeps closed-state priority even with a canceled token");
    check(reject([&] { (void)cursor->fetch(std::make_shared<Cancel>(), 1, 256, true); }).status == NW_INVALID_STATE,
          "an unrelated closed cursor still reports invalid state without cancellation");
    cursor->join();
    cursor.reset();
    check(scope.probe->reads == 0 && cursor_count == 0 && buffer_count == 0,
          "closed-cursor admission never dispatches provider rows");
}
void pull_end_and_abandonment()
{
    Scope scope;
    {
        auto lazy = scope.pull(3);
        check(scope.session->active.load(), "accepted lazy channel owns the original Session command gate");
    }
    check(!scope.session->active.load() && scope.probe->reads == 0 && pull_count == 0,
          "abandoning an idle lazy channel returns admission without opening a cursor");
    auto pull = scope.pull(1);
    PullReady ready;
    pull->request(1, 3, 256, ready.callbacks());
    ready.wait(1);
    auto batch = pull->take(1);
    check(batch->known_end && scope.probe->reads == 1, "known EOF carries only the already observed driver end");
    const auto held = reject([&] { pull->check_read_admission(); }).status;
    batch.reset();
    pull->check_read_admission();
    scope.source->poisoned.store(true);
    check(reject([&] { pull->check_read_admission(); }).status == NW_ENGINE_POISONED,
          "known EOF still checks source poison after releasing the final batch");
    scope.source->poisoned.store(false);
    pull->join();
    pull.reset();
    check(held == NW_INVALID_STATE && pull_count == 0 && cursor_count == 0,
          "held EOF batch blocks new admission until its physical owner is released");

    auto transaction = Transaction::begin(scope.session, {0, false, 5'000'000'000, 5'000'000'000, {}},
                                          std::make_shared<Cancel>(), scope.session->admit(true));
    auto existing = scope.pull(4);
    PullReady during_close;
    existing->request(1, 1, 256, during_close.callbacks());
    during_close.wait(1);
    auto first = existing->take(1);
    first.reset();
    transaction->request_close();
    existing->check_read_admission();
    std::size_t remaining = 0;
    for (std::uint64_t generation = 2;; ++generation)
    {
        existing->request(generation, 3, 256, during_close.callbacks());
        during_close.wait(generation);
        auto next = existing->take(generation);
        remaining += Batch::parse(next->bytes, next->cursor->expectation).rows();
        const bool end = next->known_end;
        next.reset();
        if (end)
            break;
    }
    existing->check_read_admission();
    existing->join();
    existing.reset();
    transaction->join();
    transaction.reset();
    check(remaining == 3 && pull_count == 0 && cursor_count == 0 && buffer_count == 0,
          "transaction disposal waits for an existing pull reader without forbidding its completion");
}
void pull_pending_and_active_abandonment()
{
    Scope scope;
    auto pull = scope.pull(3);
    auto entered = std::make_shared<std::atomic<bool>>(false);
    auto proceed = std::make_shared<std::binary_semaphore>(0);
    pull->test_before_work = [entered, proceed]
    {
        entered->store(true);
        proceed->acquire();
    };
    PullReady ready;
    pull->request(1, 1, 256, ready.callbacks());
    await([&] { return entered->load(); });
    const auto pending = reject([&] { (void)pull->take(1); }).status;
    const auto duplicate = reject([&] { pull->request(2, 1, 256, ready.callbacks()); }).status;
    std::weak_ptr<CursorPull> observed = pull;
    auto owner = std::make_shared<Owner>(scope.source, pull);
    pull.reset();
    owner.reset(); // The real ABI owner destructor requests stop nonblockingly.
    const bool retained = !observed.expired() && scope.source->pending == 1;
    proceed->release();
    ready.wait(1);
    await([&] { return observed.expired() && scope.source->pending == 0; });
    check(pending == NW_PENDING && duplicate == NW_DOMAIN_ERROR,
          "pending take and overlapping request cannot consume or replace a generation");
    check(retained && ready.calls == 1 && scope.probe->reads == 0,
          "active abandonment retains its accepted notification without dispatching after stop");
    check(pull_count == 0 && cursor_count == 0 && buffer_count == 0 && !scope.session->active.load(),
          "abandoned request drops every native channel and Session gate after its tail");
}

void pull_queue_failure_completion()
{
    for (const bool allocation_failure : {true, false})
    {
        Scope scope;
        auto pull = scope.pull(3);
        auto posts = std::make_shared<std::atomic<unsigned>>(0);
        scope.source->workers.test_before_post = [posts, allocation_failure]
        {
            if (++*posts != 1)
                return;
            if (allocation_failure)
                throw std::bad_alloc();
            throw std::runtime_error("controlled accepted queue failure");
        };
        PullReady ready;
        pull->request(1, 1, 256, ready.callbacks());
        ready.wait(1);
        const auto primary = reject([&] { (void)pull->take(1); });
        const auto rich = pull->take_error(1);
        pull->join();
        pull.reset();
        check(primary.status == (allocation_failure ? NW_OUT_OF_MEMORY : NW_NATIVE_PANIC) &&
                  rich.status == primary.status && rich.correlation == primary.correlation && !rich.cleanup,
              "accepted queue failure preserves its primary allocation/panic classification");
        check(ready.calls == 1 && scope.probe->reads == 0 && !scope.session->active.load() &&
                  scope.source->pending == 0 && scope.source->pull_workers->done(),
              "accepted queue failure notifies once and returns every request reservation without SQL");
        check(scope.source->poisoned.load() == !allocation_failure && pull_count == 0 && cursor_count == 0,
              "only unexpected queue failures poison the source and no channel is retained");
    }
}
void pull_cleanup_queue_failure()
{
    // Unconfirmed cleanup intentionally retains its physical accessor. Keep
    // that process lifetime separate from the positive zero-owner assertions.
    for (const bool read_guard : {false, true})
    {
        const auto child = fork();
        check(child >= 0, "pull cleanup quarantine subprocess starts");
        if (child == 0)
        {
            try
            {
                auto* scope = new Scope;
                auto pull = scope->pull(3);
                auto posts = std::make_shared<std::atomic<unsigned>>(0);
                scope->source->workers.test_before_post = [posts]
                {
                    if (++*posts == 2)
                        throw std::bad_alloc();
                };
                Error expected(NW_TIMEOUT, 8, "primary timeout");
                if (read_guard)
                    scope->probe->length = 129;
                else
                    pull->test_before_first_fetch = [expected] { throw expected; };
                PullReady ready;
                pull->request(1, 1, 256, ready.callbacks());
                ready.wait(1);
                const auto primary = reject([&] { (void)pull->take(1); });
                const auto rich = pull->take_error(1);
                check(primary.status == (read_guard ? NW_DOMAIN_ERROR : NW_TIMEOUT) &&
                          primary.code == (read_guard ? 14u : 8u) &&
                          (read_guard || primary.correlation == expected.correlation),
                      "cleanup queue failure does not replace the original provider/timeout error");
                check(rich.cleanup && rich.correlation == primary.correlation && ready.calls == 1 &&
                          scope->source->poisoned.load() && scope->session->discard.load(),
                      "cleanup queue failure enriches one ready result and prevents connection reuse");
                check(reject([&] { pull->join(); }).code == 8,
                      "cleanup queue failure completes join with an unconfirmed-retirement error");
                const auto receipt = pull->snapshot();
                const auto fields = Fields::parse(receipt, 7,
                                                  {{1, 1, true},
                                                   {2, 1, true},
                                                   {3, 1, true},
                                                   {4, 1, true},
                                                   {5, 1, true},
                                                   {6, 1, true},
                                                   {7, 1, true},
                                                   {8, 2, true},
                                                   {9, 4, false}});
                check(!fields.boolean(6) && fields.u32(4) == 4 && fields.u32(5) == 2,
                      "failed cleanup never publishes a released or reusable receipt");
                pull.reset();
                check(scope->source->pending == 0 && scope->source->pull_workers->done() && pull_count == 0 &&
                          cursor_count == 1 && scope->budget()[0] == 1 && scope->budget()[1] > 0,
                      "only the bounded unconfirmed cursor remains, not an accepted request or callback tail");
                _exit(0);
            }
            catch (const std::exception& error)
            {
                std::cerr << "FAIL pull cleanup subprocess: " << error.what() << '\n';
                _exit(1);
            }
        }
        int status = 0;
        check(waitpid(child, &status, 0) == child && WIFEXITED(status) && WEXITSTATUS(status) == 0,
              "ready survives cleanup queue failure from completion and the read-guard destructor");
    }
}
void pull_parent_cleanup_failure()
{
    for (const bool prepare_failure : {false, true})
    {
        Scope scope;
        auto transaction = Transaction::begin(scope.session, {0, false, 5'000'000'000, 5'000'000'000, {}},
                                              std::make_shared<Cancel>(), scope.session->admit(true));
        auto pull = prepare_failure ? scope.pull(3, "SELECT value FROM missing_pull_table") : scope.pull(3);
        transaction->test_before_worker_start = [](std::uint32_t)
        { throw std::runtime_error("controlled transaction cleanup launch failure"); };
        transaction->request_close();
        auto posts = std::make_shared<std::atomic<unsigned>>(0);
        scope.source->workers.test_before_post = [posts, prepare_failure]
        {
            if (++*posts == 1 && !prepare_failure)
                throw std::bad_alloc();
        };
        PullReady ready;
        pull->request(1, 1, 256, ready.callbacks());
        ready.wait(1);
        const auto primary = reject([&] { (void)pull->take(1); });
        const auto rich = pull->take_error(1);
        pull->join();
        pull.reset();
        check(primary.status == (prepare_failure ? NW_DOMAIN_ERROR : NW_OUT_OF_MEMORY) &&
                  (!prepare_failure || primary.code == 1) && rich.cleanup && rich.correlation == primary.correlation &&
                  ready.calls == 1 && scope.source->poisoned.load() && scope.probe->reads == 0,
              "lazy gate cleanup exception preserves the accepted queue/provider error and delivers one ready");
        check(!scope.session->active.load() && scope.source->pending == 0 && scope.source->transaction_workers->done(),
              "failed parent launch leaves no request, transaction worker or session command guard");
        transaction->test_before_worker_start = {};
        transaction->join();
        transaction.reset();
    }
    {
        Scope scope;
        auto pull = scope.pull(3);
        auto posts = std::make_shared<std::atomic<unsigned>>(0);
        scope.source->workers.test_before_post = [posts]
        {
            if (++*posts == 2)
                throw std::bad_alloc();
        };
        auto entered = std::make_shared<std::atomic<bool>>(false);
        auto proceed = std::make_shared<std::binary_semaphore>(0);
        pull->test_before_work = [entered, proceed]
        {
            entered->store(true);
            proceed->acquire();
        };
        PullReady ready;
        pull->request(1, 1, 256, ready.callbacks());
        await([&] { return entered->load(); });
        scope.session->request_close();
        proceed->release();
        ready.wait(1);
        const auto primary = reject([&] { (void)pull->take(1); });
        const auto rich = pull->take_error(1);
        pull->join();
        pull.reset();
        check(primary.status == NW_INVALID_STATE && rich.cleanup && ready.calls == 1 && scope.source->poisoned.load() &&
                  !scope.session->cleanup_scheduled.load(),
              "failed Session cleanup post still publishes ready and leaves retirement retryable");
        scope.session->request_close();
        scope.session->join();
        check(scope.session->retired.load() && scope.source->pending == 0 && scope.probe->reads == 0,
              "a later Session close retires the failed cleanup launch without executing the lazy query");
    }
}

void unknown_and_invalid()
{
    // The original test deliberately quarantines an unconfirmed retirement.
    // Isolate that retained process lifetime from the remaining positive suite.
    const auto child = fork();
    check(child >= 0, "quarantine subprocess starts");
    if (child == 0)
    {
        try
        {
            auto* scope = new Scope;
            auto cursor = scope->open(0);
            cursor->test_retire_gate = [] { throw Error::state("unconfirmed provider retirement"); };
            cursor->request_close();
            check(reject([&] { cursor->join(); }).code == 8, "unknown cleanup returns pending failure");
            cursor.reset();
            check(scope->budget()[0] == 1 && scope->budget()[1] > 0, "unknown cleanup retains a bounded reservation");
            _exit(0);
        }
        catch (...)
        {
            _exit(1);
        }
    }
    int status = 0;
    check(waitpid(child, &status, 0) == child && WIFEXITED(status) && WEXITSTATUS(status) == 0,
          "unconfirmed retirement isolation assertions");
    Scope scope;
    scope.source->config.limits.batch_rows = SIZE_MAX;
    (void)reject([&] { (void)scope.open(0); });
    scope.source->config.limits.batch_rows = 3;
    scope.source->config.limits.batch_bytes = 135;
    check(reject([&] { (void)scope.open(0); }).code == 14, "undersized row table rejects before dispatch");
    check(scope.probe->reads == 0 && scope.budget()[1] == 0, "invalid limits never dispatch or retain credits");
}
} // namespace
int main()
{
    try
    {
        // fork is performed before any persistent test-owned worker exists.
        unknown_and_invalid();
        pull_cleanup_queue_failure();
        pull_close_cleanup_failure();
        bounded_batches();
        batch_outlives_cursor();
        arena_lookahead_and_end();
        known_end_read_admission();
        arena_lookahead_error();
        field_and_shape();
        abandoned_close();
        partial_fetch_cancel();
        retirement_worker_owner_barrier();
        retirement_notification_barrier();
        pull_generations_and_tail();
        prepared_pull_generations();
        prepared_pull_never_run();
        prepared_pull_running_retirement();
        prepared_pull_concurrent_claim();
        prepared_pull_inflight_stop();
        prepared_pull_late_claim_during_close_callback();
        pull_stop_before_worker();
        pull_stop_before_wake();
        pull_queued_parent_priority();
        closed_cursor_cancel_scope();
        pull_first_fetch_admission();
        pull_end_and_abandonment();
        pull_pending_and_active_abandonment();
        pull_queue_failure_completion();
        pull_parent_cleanup_failure();
        pull_final_close_results();
        pull_close_joins_old_fetch_tail();
        pull_close_notification_tail();
        pull_close_rejection_and_errors();
        await(
            []
            {
                return source_count == 0 && session_count == 0 && cursor_count == 0 && buffer_count == 0 &&
                       pull_count == 0;
            });
        check(source_count == 0 && session_count == 0 && cursor_count == 0 && buffer_count == 0 && pull_count == 0,
              "positive paths release all native owner counters");
        std::cout << "PASS bounded streaming: 6 retained families, " << checks << " checks\n";
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << "FAIL bounded streaming: " << error.what() << '\n';
        return 1;
    }
}
