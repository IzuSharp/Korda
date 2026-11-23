// SPDX-License-Identifier: MIT
// Exact retained B06 C ABI, composed from the production Host and providers.
#include "nw/host.hpp"

#include <algorithm>
#include <array>
#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace nw
{
std::uint64_t module_identity()
{
    return 0x4442435050423036ULL;
}
} // namespace nw

namespace
{
using namespace nw;
std::atomic<std::size_t> owners{0};
struct Fixture
{
    std::mutex mutex;
    std::shared_ptr<Source> source;
    std::shared_ptr<Session> session;
    std::shared_ptr<Cursor> cursor;
    std::mutex stop_mutex;
    std::shared_ptr<Cursor> stop_cursor;
    std::shared_ptr<Cursor> get_stop(bool clear = false)
    {
        std::lock_guard lock(stop_mutex);
        auto result = stop_cursor;
        if (clear)
            stop_cursor.reset();
        return result;
    }
    std::shared_ptr<Buffer> ready;
    std::array<std::uint64_t, 20> stats{};
    bool counted = false;
    bool joined = false;

    ~Fixture()
    {
        if (counted)
            --owners;
        ready.reset();
        auto cursor_owner = get_stop(true);
        if (cursor_owner)
            cursor_owner->request_close();
        if (!joined && source)
        {
            auto source_owner = std::move(source);
            auto session_owner = std::move(session);
            std::thread(
                [source_owner, session_owner, cursor_owner]
                {
                    if (cursor_owner)
                        cursor_owner->join();
                    if (session_owner)
                    {
                        session_owner->request_close();
                        session_owner->join();
                    }
                    source_owner->join();
                })
                .detach();
        }
    }
};
struct BatchOwner
{
    std::shared_ptr<Buffer> buffer;
    std::uint32_t rows;
    bool end;
};
std::uint32_t status(const Error& error)
{
    if (error.status == NW_CANCELED)
        return 4;
    if (error.code == 8 && error.message == "provider retirement remains unconfirmed")
        return 7;
    if (error.code == 4)
        return 2;
    if (error.code == 14)
        return 3;
    if (error.code == 3)
        return 9;
    if (error.code == 6)
        return 6;
    if (error.status == NW_INVALID_STATE || error.code == 7 || error.code == 8)
        return 1;
    return 5;
}
template <typename Action> std::uint32_t protect(Action action)
{
    try
    {
        action();
        return 0;
    }
    catch (const Error& error)
    {
        if (status(error) == 5)
            std::fprintf(stderr, "B06 provider refusal: status=%u code=%u message=%s provider=%s\n", error.status,
                         error.code, error.what(), error.provider.value_or("").c_str());
        return status(error);
    }
    catch (...)
    {
        return 90;
    }
}
std::string query(std::uint32_t provider, std::uint32_t scenario)
{
    const bool pg = provider == 2;
    switch (scenario)
    {
        case 0:
            return "SELECT CAST(1 AS BIGINT) AS id, CAST(NULL AS TEXT) AS label WHERE 1=0";
        case 1:
            return "SELECT CAST(1 AS BIGINT) AS id, CAST('row-1' AS TEXT) AS label";
        case 2:
        case 9:
            return pg ? "SELECT i::bigint AS id, ('row-' || i::text)::text AS label FROM generate_series(1,17) AS i"
                      : "WITH RECURSIVE t(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM t WHERE i<17) SELECT i AS id, "
                        "'row-' || i AS label FROM t";
        case 3:
            return pg ? "SELECT 1::bigint AS id, repeat('x',4096)::text AS label"
                      : "SELECT 1 AS id, replace(hex(zeroblob(2048)),'0','x') AS label";
        case 4:
            return "SELECT CAST(1 AS BIGINT) AS id";
        case 5:
            return "SELECT CAST('x' AS TEXT) AS label, CAST(1 AS BIGINT) AS id";
        case 6:
            return pg ? "SELECT CAST(1 AS BIGINT) AS id, CAST(NULL AS BIGINT) AS label"
                      : "SELECT id,label FROM b06_null_type";
        case 7:
            return pg ? "SELECT 1::bigint AS id, NULL::text AS label" : "SELECT 1 AS id,NULL AS label";
        case 8:
            return pg ? "SELECT 1::bigint AS id, 'blocked'::text AS label FROM pg_sleep(3)"
                      : "WITH RECURSIVE t(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM t WHERE i<1000000000) "
                        "SELECT sum(i) AS id, 'blocked' AS label FROM t";
        case 20:
            return "SELECT CAST(1 AS BIGINT) WHERE 1=0";
        case 21:
            return "SELECT CAST(1 AS BIGINT)";
        case 22:
            return "SELECT CAST(1 AS BIGINT) UNION ALL SELECT CAST(2 AS BIGINT)";
        default:
            throw Error::state("unknown B06 scenario");
    }
}
std::unique_ptr<Fixture> open(std::uint32_t provider, std::uint32_t scenario, ByteView projection, std::size_t rows,
                              std::size_t bytes)
{
    if (provider != 1 && provider != 2)
        throw Error::unsupported();
    if (!rows || rows > MAX_NODES || !bytes || bytes > MAX_BYTES)
        throw Error::capacity();
    const auto profile = static_cast<Profile>(provider);
    auto shape = std::make_shared<Projection>(scenario >= 20 ? Projection::parse_scalar(projection, profile)
                                                             : Projection::parse(projection, profile));
    auto fixture = std::make_unique<Fixture>();
    HostConfig config{};
    config.provider.profile = profile;
    config.provider.statement_timeout_ns = 300'000'000;
    config.name = "B06-native-host-fixture";
    config.limits = {1,    16,        1000, 64 * 1024 * 1024, rows, bytes,         bytes,         4096,
                     4096, MAX_BYTES, 64,   MAX_BYTES,        64,   5'000'000'000, 30'000'000'000};
    if (provider == 1)
    {
        config.provider.connection = ":memory:";
        config.provider.sqlite_mode = 3;
        config.provider.sqlite_journal = 2;
        config.provider.sqlite_busy_timeout_ns = 100'000'000;
    }
    else
    {
        // Isolated fixture setup/reads; environment mutation never overlaps active provider work.
        // NOLINTNEXTLINE(concurrency-mt-unsafe)
        const auto* url = std::getenv("KORDA_TEST_POSTGRES_URL");
        if (!url)
            throw Error(NW_DOMAIN_ERROR, 1, "explicit PostgreSQL test URL required");
        config.provider.connection = url;
        config.provider.application_name = "Korda-B06-fixture";
        config.provider.verify_tls = false;
    }
    fixture->source = Source::create(std::move(config));
    // The original B06 component has an explicit 1 cursor / 64 MiB budget.
    fixture->source->stream_max_bytes = 64 * 1024 * 1024;
    {
        std::lock_guard lock(fixture->source->mutex);
        ++fixture->source->leased;
    }
    fixture->session = Session::open_reserved(fixture->source, std::make_shared<Cancel>());
    const CallContext context{{}, Clock::now() + std::chrono::seconds(5)};
    if (provider == 1)
    {
        const auto version = fixture->session->connection->query_text("SELECT sqlite_version()", context);
        if (version.size() != 1 || version[0][0] != "3.51.3")
            throw Error(NW_DOMAIN_ERROR, 1, "unexpected pinned SQLite version");
        fixture->stats[18] = 3'051'003;
        if (scenario == 6)
            fixture->session->connection->execute_script(
                "CREATE TEMP TABLE b06_null_type(id INTEGER NOT NULL,label INTEGER);"
                "INSERT INTO b06_null_type VALUES(1,NULL)",
                context);
    }
    else
    {
        const auto version = fixture->session->connection->query_text("SHOW server_version_num", context);
        fixture->stats[18] = std::stoull(*version.at(0).at(0));
    }
    auto plan = std::make_shared<SqlPlan>();
    plan->source = fixture->source;
    plan->sql = query(provider, scenario);
    plan->result_kind = 1;
    plan->timeout_ns = 30'000'000'000;
    plan->projection = ProjectionBinding{fixture->source->identity, 7, shape, fixture->source->codecs()};
    plan->expectation = {1, profile, sha256(text_view("{\"version\":1,\"parameters\":[]}")), 0, 0, {}, 1};
    plan->parameters = Batch::encode(plan->expectation, 1, {});
    auto result = fixture->session->execute(plan, std::make_shared<Cancel>(), fixture->session->admit(false));
    auto owner = std::get<std::shared_ptr<Owner>>(std::move(result));
    fixture->cursor = std::get<std::shared_ptr<Cursor>>(std::move(owner->data));
    // This bridge takes over the exact Cursor owner. Do not let the temporary
    // generic Owner request its closure while publishing the component ABI.
    owner->data = ModelBinding{};
    fixture->stop_cursor = fixture->cursor;
    fixture->counted = true;
    ++owners;
    return fixture;
}
} // namespace

#pragma GCC visibility push(default)
extern "C"
{
    std::uint32_t nw_stream_fixture_open(std::uint32_t provider, std::uint32_t scenario, const std::uint8_t* projection,
                                         std::size_t length, std::size_t rows, std::size_t bytes, void** output)
    {
        if (!output)
            return 1;
        *output = nullptr;
        return protect(
            [&]
            {
                if (!projection || !length || length > MAX_BYTES)
                    throw Error(NW_DOMAIN_ERROR, 4, "invalid projection extent");
                *output = open(provider, scenario, {projection, length}, rows, bytes).release();
            });
    }
    std::uint32_t nw_stream_fixture_fetch(void* owner)
    {
        return protect(
            [&]
            {
                auto& fixture = *static_cast<Fixture*>(owner);
                std::unique_lock lock(fixture.mutex, std::try_to_lock);
                if (!lock.owns_lock() || !fixture.cursor || fixture.ready)
                    throw Error::state("concurrent or duplicate fetch");
                fixture.ready =
                    fixture.cursor->fetch(std::make_shared<Cancel>(), fixture.source->config.limits.batch_rows,
                                          fixture.source->config.limits.batch_bytes);
            });
    }
    std::uint32_t nw_stream_fixture_take(void* owner, void** output)
    {
        if (!output)
            return 1;
        *output = nullptr;
        return protect(
            [&]
            {
                auto& fixture = *static_cast<Fixture*>(owner);
                std::unique_lock lock(fixture.mutex, std::try_to_lock);
                if (!lock.owns_lock() || !fixture.ready || !fixture.cursor)
                    throw Error::state("no untaken batch");
                auto batch = std::make_unique<BatchOwner>();
                batch->rows = Batch::parse(fixture.ready->bytes, fixture.cursor->expectation).rows();
                batch->end = fixture.cursor->ended;
                fixture.ready->mark_taken();
                batch->buffer = std::move(fixture.ready);
                *output = batch.release();
            });
    }
    void nw_stream_fixture_stop(void* owner)
    {
        if (auto cursor = static_cast<Fixture*>(owner)->get_stop())
            cursor->request_close();
    }
    std::uint32_t nw_stream_fixture_join(void* owner)
    {
        return protect(
            [&]
            {
                auto& fixture = *static_cast<Fixture*>(owner);
                std::unique_lock lock(fixture.mutex, std::try_to_lock);
                if (!lock.owns_lock())
                    throw Error::state("concurrent join");
                if (fixture.joined)
                    return;
                fixture.ready.reset();
                if (fixture.cursor)
                {
                    fixture.cursor->request_close();
                    fixture.cursor->join();
                    fixture.stats[7] = fixture.cursor->provider_rows_peak;
                    fixture.stats[8] = fixture.cursor->retired.load();
                    fixture.stats[9] = fixture.session->connection->healthy();
                    fixture.stats[10] = fixture.cursor->stop_observed;
                    (void)fixture.get_stop(true);
                    fixture.cursor.reset();
                }
                if (fixture.stats[9])
                {
                    const auto rows = fixture.session->connection->query_text("SELECT 42 AS probe", {});
                    fixture.stats[14] = rows.size() == 1 && rows[0].size() == 1 && rows[0][0] == "42";
                }
                fixture.session->request_close();
                fixture.session->join();
                fixture.stats[11] = fixture.session->retired.load();
                fixture.stats[12] = !fixture.session->connection;
                fixture.stats[13] = fixture.session->disposition == 0;
                fixture.stats[17] = fixture.session->active.load();
                fixture.source->join();
                if (fixture.source->config.provider.profile == Profile::Sqlite)
                    fixture.stats[15] = fixture.source->retired.load();
                fixture.joined = true;
            });
    }
    std::uint32_t nw_stream_fixture_stats(void* owner, std::uint64_t* output)
    {
        return protect(
            [&]
            {
                auto& fixture = *static_cast<Fixture*>(owner);
                std::lock_guard lock(fixture.mutex);
                auto values = fixture.stats;
                {
                    std::lock_guard budget(fixture.source->stream_mutex);
                    std::copy(fixture.source->stream_stats.begin(), fixture.source->stream_stats.end(), values.begin());
                }
                values[19] = owners.load();
                std::copy(values.begin(), values.end(), output);
            });
    }
    void nw_stream_fixture_batch_info(void* batch, std::uint64_t* output)
    {
        const auto& value = *static_cast<BatchOwner*>(batch);
        output[0] = value.buffer->bytes.size();
        output[1] = value.rows;
        output[2] = value.end;
    }
    std::uint32_t nw_stream_fixture_batch_copy(void* batch, std::uint8_t* output, std::size_t capacity)
    {
        const auto& value = *static_cast<BatchOwner*>(batch);
        if (!output || capacity < value.buffer->bytes.size())
            return 6;
        std::memcpy(output, value.buffer->bytes.data(), value.buffer->bytes.size());
        return 0;
    }
    void nw_stream_fixture_batch_release(void* batch)
    {
        delete static_cast<BatchOwner*>(batch);
    }
    void nw_stream_fixture_release(void* owner)
    {
        delete static_cast<Fixture*>(owner);
    }
    std::size_t nw_stream_fixture_owners()
    {
        return owners.load();
    }
}
#pragma GCC visibility pop
