// SPDX-License-Identifier: MIT
// The retained B05 managed consumer drives the actual C++ Host coordinator.
// Only completion/retirement scheduling is controlled in this test-only module.
#include "nw/host.hpp"

#include <algorithm>
#include <array>
#include <limits>
#include <semaphore>

#ifndef NW_HOST_TEST_HOOKS
#error The transaction fault fixture must never be linked into a product library.
#endif

namespace nw
{
std::uint64_t module_identity()
{
    return 0x4442435050423035ULL;
}
} // namespace nw

namespace
{
using namespace nw;
struct Signals
{
    std::counting_semaphore<16> proceed{0};
};
struct Fixture
{
    std::shared_ptr<Source> source;
    std::shared_ptr<Session> session;
    std::shared_ptr<Transaction> transaction;
    std::shared_ptr<Signals> signals;
    std::mutex mutex;
    std::vector<std::shared_ptr<Savepoint>> points;

    ~Fixture()
    {
        transaction->request_close();
        // Finalization surrenders the wrapper, while real Host owners retain
        // every provider accessor through observed retirement.
        auto source_owner = std::move(source);
        auto session_owner = std::move(session);
        auto transaction_owner = std::move(transaction);
        std::thread(
            [source_owner, session_owner, transaction_owner]
            {
                transaction_owner->join();
                session_owner->request_close();
                session_owner->join();
                source_owner->join();
            })
            .detach();
    }
};
std::shared_ptr<Cancel> token(std::uint32_t canceled)
{
    auto result = std::make_shared<Cancel>();
    if (canceled)
        result->request();
    return result;
}
Error response_loss()
{
    Error error(NW_DOMAIN_ERROR, 1, "controlled provider response loss");
    error.provider = "08006";
    error.correlation = 71;
    return error;
}
std::uint32_t status(const Error& error, bool rollback = false)
{
    if (error.status == NW_CANCELED)
        return 2;
    if (error.code == 9)
        return 3;
    if (error.status == NW_DOMAIN_ERROR && error.code == 1)
        return rollback ? 5 : 4;
    return 1;
}
template <typename Action> std::uint32_t protect(Action action, bool rollback = false)
{
    try
    {
        action();
        return 0;
    }
    catch (const Error& error)
    {
        return status(error, rollback);
    }
    catch (...)
    {
        return 1;
    }
}
} // namespace

#pragma GCC visibility push(default)
extern "C"
{
    void* nw_tx_fixture_create(std::uint32_t mode, std::uint64_t commit_ms, std::uint64_t cleanup_ms)
    {
        try
        {
            HostConfig config{};
            config.provider.connection = ":memory:";
            config.provider.sqlite_mode = 3;
            config.provider.sqlite_journal = 2;
            config.name = "B05-controlled-completion";
            config.limits = {1,    16,        64, 65536,     64, 65536,         65536,         4096,
                             4096, MAX_BYTES, 64, MAX_BYTES, 64, 5'000'000'000, 30'000'000'000};
            auto source = Source::create(std::move(config));
            {
                std::lock_guard lock(source->mutex);
                ++source->leased;
            }
            auto session = Session::open_reserved(source, token(0));
            TransactionOptions options{};
            options.commit_timeout_ns = commit_ms * 1'000'000;
            options.cleanup_timeout_ns = cleanup_ms * 1'000'000;
            auto transaction = Transaction::begin(session, options, token(0), session->admit(true));
            auto signals = std::make_shared<Signals>();
            auto connection = session->connection;
            transaction->test_finish = [signals, connection, mode](bool commit)
            {
                if (commit && mode == 3)
                    signals->proceed.acquire();
                if (commit && mode == 1)
                    throw response_loss();
                if (!commit && mode == 4)
                {
                    connection->close();
                    throw response_loss();
                }
                const bool confirmed_commit = commit && mode != 2;
                (void)connection->execute(confirmed_commit ? "COMMIT" : "ROLLBACK", {},
                                          {{}, Clock::time_point::max(), false});
                return confirmed_commit;
            };
            if (mode == 5)
                transaction->test_retire_gate = [signals] { signals->proceed.acquire(); };
            auto result = std::make_unique<Fixture>();
            result->source = std::move(source);
            result->session = std::move(session);
            result->transaction = std::move(transaction);
            result->signals = std::move(signals);
            return result.release();
        }
        catch (...)
        {
            return nullptr;
        }
    }

    std::uint32_t nw_tx_fixture_finish(void* owner, std::uint32_t commit, std::uint32_t canceled)
    {
        auto& fixture = *static_cast<Fixture*>(owner);
        return protect(
            [&] { (void)fixture.transaction->finish(commit != 0, token(canceled), fixture.session->admit(true)); },
            commit == 0);
    }

    void nw_tx_fixture_snapshot(void* owner, std::uint64_t* output)
    {
        auto& fixture = *static_cast<Fixture*>(owner);
        const auto& tx = fixture.transaction;
        std::lock_guard lock(tx->mutex);
        const std::array<std::uint64_t, 16> values{tx->state,
                                                   tx->outcome,
                                                   tx->attempted,
                                                   tx->cleanup,
                                                   tx->disposition,
                                                   tx->retired,
                                                   tx->deadline_exceeded,
                                                   tx->correlation,
                                                   tx->provider.has_value(),
                                                   fixture.source->identity.generation,
                                                   fixture.session->connection ? fixture.session->connection->identity()
                                                                               : 0,
                                                   !tx->retired,
                                                   tx->driver_active,
                                                   tx->closing && !tx->retired,
                                                   tx->retired   ? 2ULL
                                                   : tx->closing ? 1ULL
                                                                 : 0ULL,
                                                   0};
        std::copy(values.begin(), values.end(), output);
    }

    void nw_tx_fixture_request_close(void* owner)
    {
        static_cast<Fixture*>(owner)->transaction->request_close();
    }
    void nw_tx_fixture_join(void* owner)
    {
        static_cast<Fixture*>(owner)->transaction->join();
    }
    void nw_tx_fixture_wait_close(void* owner, std::uint64_t ticks)
    {
        const auto nanoseconds = ticks > std::numeric_limits<std::uint64_t>::max() / 100
                                     ? std::numeric_limits<std::uint64_t>::max()
                                     : ticks * 100;
        (void)static_cast<Fixture*>(owner)->transaction->wait_close(nanoseconds);
    }
    void nw_tx_fixture_proceed(void* owner)
    {
        static_cast<Fixture*>(owner)->signals->proceed.release();
    }
    std::uint32_t nw_tx_fixture_fail_command(void* owner)
    {
        auto& fixture = *static_cast<Fixture*>(owner);
        return protect(
            [&]
            {
                auto gate = fixture.session->admit(false);
                gate->dispatched = true;
                gate->finish(true);
            });
    }
    std::uint32_t nw_tx_fixture_savepoint_create(void* owner, std::uint32_t canceled, std::uint64_t* sequence)
    {
        *sequence = 0;
        auto& fixture = *static_cast<Fixture*>(owner);
        return protect(
            [&]
            {
                auto point = fixture.transaction->create_savepoint(token(canceled), fixture.session->admit(true));
                std::lock_guard lock(fixture.mutex);
                *sequence = point->token;
                fixture.points.push_back(std::move(point));
            });
    }
    std::uint32_t nw_tx_fixture_savepoint_action(void* owner, std::uint64_t sequence, std::uint32_t action,
                                                 std::uint32_t canceled)
    {
        auto& fixture = *static_cast<Fixture*>(owner);
        return protect(
            [&]
            {
                {
                    std::lock_guard lock(fixture.mutex);
                    if (std::none_of(fixture.points.begin(), fixture.points.end(),
                                     [sequence](const auto& point) { return point->token == sequence; }))
                        throw Error::state("unknown savepoint token");
                }
                if (action > 1)
                    (void)fixture.transaction->dispose_savepoint(sequence);
                else
                    (void)fixture.transaction->savepoint_action(sequence, action, token(canceled),
                                                                fixture.session->admit(true));
            });
    }
    void nw_tx_fixture_release(void* owner)
    {
        delete static_cast<Fixture*>(owner);
    }
}

#pragma GCC visibility pop
