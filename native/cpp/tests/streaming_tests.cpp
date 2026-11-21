// SPDX-License-Identifier: MIT
// Ports the six B06 bounded.rs assertion families against actual Host/SQLite.
#include "nw/host.hpp"
#include <sqlite3.h>

#include <atomic>
#include <future>
#include <iostream>
#include <semaphore>
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
    throw std::runtime_error("expected streaming rejection");
}
template <typename Condition> void await(Condition condition)
{
    const auto deadline = Clock::now() + std::chrono::seconds(3);
    while (!condition())
    {
        if (Clock::now() >= deadline)
            throw std::runtime_error("streaming control timeout");
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
    std::shared_ptr<Cursor> open(std::size_t rows, std::optional<std::string> query = {})
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
        plan->parameters = Batch::encode(plan->expectation, 1, {});
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
        bounded_batches();
        batch_outlives_cursor();
        field_and_shape();
        abandoned_close();
        partial_fetch_cancel();
        await([] { return source_count == 0 && session_count == 0 && cursor_count == 0 && buffer_count == 0; });
        check(source_count == 0 && session_count == 0 && cursor_count == 0 && buffer_count == 0,
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
