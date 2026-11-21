#include "fixtures/execution_runtime.hpp"

#include <barrier>
#include <future>
#include <iostream>
#include <stdexcept>

namespace
{
using namespace nw;
using namespace nw::fixtures::execution;
using namespace std::chrono_literals;
std::size_t checks = 0;
#define CHECK(expression)                                                                                              \
    do                                                                                                                 \
    {                                                                                                                  \
        ++checks;                                                                                                      \
        if (!(expression))                                                                                             \
        {                                                                                                              \
            throw std::runtime_error(#expression);                                                                     \
        }                                                                                                              \
    } while (false)

void settle(Executor& executor)
{
    const auto until = std::chrono::steady_clock::now() + 5s;
    while (executor.snapshot().tracked_tasks != 0)
    {
        CHECK(std::chrono::steady_clock::now() < until);
        std::this_thread::sleep_for(1ms);
    }
}
void closed(Executor& executor)
{
    CHECK(executor.join());
    const auto state = executor.snapshot();
    CHECK(state.closing && state.joined);
    CHECK(state.admitted == 0 && state.reserved_bytes == 0 && state.issued == 0 && state.running == 0 &&
          state.retiring == 0 && state.tracked_tasks == 0);
    CHECK(state.workers_started > 0 && state.workers_started == state.workers_stopped);
}
Reservation acquire(Executor& executor, std::uint64_t bytes)
{
    auto [status, owner] = executor.try_acquire(bytes);
    CHECK(status == AdmissionError::None && owner);
    return owner;
}
template <class T> T ready(std::future<T>& future)
{
    CHECK(future.wait_for(5s) == std::future_status::ready);
    return future.get();
}
void admission_and_waiters()
{
    try
    {
        Executor invalid({0, 1, 1, 1, 1, 1});
        CHECK(false);
    }
    catch (AdmissionError error)
    {
        CHECK(error == AdmissionError::InvalidLimits);
    }
    for (int index = 0; index < 3; ++index)
    {
        Limits limits;
        if (index == 0)
            limits.admitted = 0;
        if (index == 1)
            limits.cleanup = 0;
        if (index == 2)
            limits.bytes = 0;
        Executor executor(limits);
        CHECK(executor.try_acquire(1).first == AdmissionError::Impossible);
        CHECK(executor.acquire(1).poll().first == AdmissionError::Impossible);
        CHECK(executor.snapshot().waiters == 0);
        closed(executor);
    }
    {
        Executor executor({});
        auto first = acquire(executor, 6);
        CHECK(first->bytes == 6);
        CHECK(executor.try_acquire(3).first == AdmissionError::Full);
        CHECK(executor.try_acquire(9).first == AdmissionError::Impossible);
        auto second = acquire(executor, 2);
        CHECK(executor.try_acquire(0).first == AdmissionError::Full);
        auto retained = first;
        first.reset();
        CHECK(executor.snapshot().admitted == 2 && executor.snapshot().reserved_bytes == 8);
        retained.reset();
        second.reset();
        closed(executor);
    }
    {
        Executor executor({1, 1, 8, 2, 8, 1});
        auto first = acquire(executor, 1);
        CHECK(executor.try_acquire(1).first == AdmissionError::Full);
        CHECK(executor.snapshot().admitted == 1 && executor.snapshot().reserved_bytes == 1);
        first.reset();
        closed(executor);
    }
    {
        Executor executor({});
        auto first = acquire(executor, 4);
        auto big = std::make_unique<Waiting>(executor.acquire(8));
        auto small = executor.acquire(1);
        CHECK(big->poll().first == AdmissionError::Pending && small.poll().first == AdmissionError::Pending);
        CHECK(executor.try_acquire(1).first == AdmissionError::Full);
        CHECK(executor.acquire(0).poll().first == AdmissionError::Full);
        big.reset();
        auto [status, second] = small.poll();
        CHECK(status == AdmissionError::None);
        CHECK(executor.snapshot().admitted == 2 && executor.snapshot().waiters == 0);
        second.reset();
        first.reset();
        closed(executor);
    }
    {
        Executor executor({1, 1, 1, 2, 8, 1});
        auto first = acquire(executor, 8);
        auto second = executor.acquire(8);
        auto third = executor.acquire(1);
        CHECK(second.poll().first == AdmissionError::Pending && third.poll().first == AdmissionError::Pending);
        first.reset();
        CHECK(third.poll().first == AdmissionError::Pending);
        auto [second_status, second_owner] = second.poll();
        CHECK(second_status == AdmissionError::None);
        second_owner.reset();
        auto [third_status, third_owner] = third.poll();
        CHECK(third_status == AdmissionError::None);
        third_owner.reset();
        closed(executor);
    }
    {
        Executor executor({1, 1, 1, 0, 8, 1});
        auto issued = acquire(executor, 8);
        CHECK(executor.acquire(1).poll().first == AdmissionError::Full);
        issued.reset();
        closed(executor);
    }
    {
        Executor executor({});
        auto issued = acquire(executor, 8);
        auto waiting = executor.acquire(1);
        CHECK(waiting.poll().first == AdmissionError::Pending);
        executor.request_close();
        CHECK(waiting.poll().first == AdmissionError::Closed);
        CHECK(executor.try_acquire(0).first == AdmissionError::Closed);
        CHECK(executor.spawn<int>(
                  issued, [] { return 7; }, [](auto, auto, auto) { std::terminate(); }) == AdmissionError::Closed);
        CHECK(executor.snapshot().issued == 1 && executor.snapshot().reserved_bytes == 8);
        CHECK(!executor.try_join());
        issued.reset();
        closed(executor);
    }
    {
        Executor executor({1, 1, 1, 2, 8, 1});
        auto issued = acquire(executor, 8);
        auto waiting = executor.acquire(8);
        std::size_t wakes = 0;
        CHECK(waiting
                  .poll(
                      [&]
                      {
                          CHECK(executor.snapshot().admitted == 1);
                          ++wakes;
                      })
                  .first == AdmissionError::Pending);
        issued.reset();
        auto [status, next] = waiting.poll();
        CHECK(status == AdmissionError::None && wakes == 1);
        next.reset();
        closed(executor);
    }
}
void retirement_and_blocking()
{
    {
        Executor executor({});
        auto issued = acquire(executor, 8);
        std::promise<int> promise;
        auto future = promise.get_future();
        CHECK(executor.spawn<int>(
                  issued, [] { return 123; },
                  [&](auto status, auto result, auto)
                  {
                      if (status != NW_OK || !result)
                          std::terminate();
                      promise.set_value(*result);
                  }) == AdmissionError::None);
        CHECK(ready(future) == 123);
        settle(executor);
        std::this_thread::sleep_for(2ms);
        const auto state = executor.snapshot();
        CHECK(state.running == 0 && state.retiring == 1 && state.reserved_bytes == 8);
        CHECK(state.oldest_cleanup_millis >= 1);
        CHECK(!executor.try_join());
        issued.reset();
        closed(executor);
        CHECK(executor.snapshot().oldest_cleanup_millis == 0);
    }
    {
        Executor executor({});
        auto issued = acquire(executor, 8);
        std::promise<int> entered, release, done;
        auto entered_future = entered.get_future();
        auto release_future = release.get_future();
        auto done_future = done.get_future();
        CHECK(executor.spawn<int>(
                  issued,
                  [&]
                  {
                      entered.set_value(1);
                      release_future.wait();
                      return 7;
                  },
                  [&](auto status, auto result, auto)
                  {
                      if (status != NW_OK || !result)
                          std::terminate();
                      done.set_value(*result);
                  },
                  true) == AdmissionError::None);
        CHECK(ready(entered_future) == 1);
        executor.request_close();
        CHECK(!executor.try_join());
        CHECK(done_future.wait_for(20ms) == std::future_status::timeout);
        const auto state = executor.snapshot();
        CHECK(state.admitted == 1 && state.reserved_bytes == 8 && state.running == 1 && !state.joined);
        release.set_value(1);
        CHECK(ready(done_future) == 7);
        settle(executor);
        issued.reset();
        closed(executor);
        CHECK(executor.snapshot().workers_started == 2);
    }
    {
        Executor executor({});
        auto issued = acquire(executor, 1);
        std::promise<int> result;
        auto future = result.get_future();
        CHECK(executor.spawn<int>(
                  issued,
                  [&]
                  {
                      executor.wait_closed();
                      return 17;
                  },
                  [&](auto status, auto value, auto)
                  {
                      if (status != NW_OK || !value)
                          std::terminate();
                      result.set_value(*value);
                  }) == AdmissionError::None);
        executor.request_close();
        CHECK(ready(future) == 17);
        settle(executor);
        issued.reset();
        closed(executor);
    }
    {
        Executor first({}), second({});
        auto issued = acquire(first, 1);
        CHECK(second.spawn<int>(issued, [] { return 0; }, [](auto, auto, auto) {}) == AdmissionError::WrongOwner);
        std::promise<int> result;
        auto future = result.get_future();
        CHECK(first.spawn<int>(
                  issued, [] { return 0; }, [&](auto, auto, auto) { result.set_value(1); }) == AdmissionError::None);
        CHECK(first.spawn<int>(issued, [] { return 0; }, [](auto, auto, auto) {}) == AdmissionError::AlreadyStarted);
        CHECK(ready(future) == 1);
        settle(first);
        issued.reset();
        closed(first);
        closed(second);
    }
}
struct Dangerous
{
    std::shared_ptr<std::atomic<std::size_t>> destroyed;
    ~Dangerous() noexcept
    {
        ++*destroyed;
        std::terminate();
    }
};
void actual_exceptions()
{
    for (int mode = 0; mode < 3; ++mode)
    {
        Executor executor({});
        auto issued = acquire(executor, 1);
        auto destroyed = std::make_shared<std::atomic<std::size_t>>(0);
        std::promise<nw_status> result;
        auto future = result.get_future();
        CHECK(executor.spawn<int>(
                  issued,
                  [&, destroyed]
                  {
                      if (mode == 0)
                          throw Dangerous{destroyed};
                      if (mode == 1)
                          throw std::runtime_error("actual blocking work exception");
                      return 1;
                  },
                  [&, destroyed](auto status, auto, auto)
                  {
                      if (mode == 2)
                          throw Dangerous{destroyed};
                      result.set_value(status);
                  },
                  mode == 1) == AdmissionError::None);
        if (mode != 2)
        {
            CHECK(ready(future) == NW_NATIVE_PANIC);
        }
        settle(executor);
        const auto state = executor.snapshot();
        CHECK(state.poisoned && state.closing && state.panic_payloads == 1 && *destroyed == 0);
        CHECK(state.retiring == 1);
        CHECK(executor.try_acquire(1).first == AdmissionError::Closed);
        issued.reset();
        closed(executor);
        CHECK(executor.snapshot().workers_started == (mode == 1 ? 2 : 1));
    }
}
void close_races_and_ownership()
{
    for (int iteration = 0; iteration < 64; ++iteration)
    {
        Executor executor({});
        auto issued = acquire(executor, 8);
        std::barrier gate(2);
        AdmissionError started = AdmissionError::Pending;
        std::promise<int> result;
        auto future = result.get_future();
        std::thread starter(
            [&]
            {
                gate.arrive_and_wait();
                started = executor.spawn<int>(
                    issued, [] { return 1; },
                    [&](auto status, auto value, auto)
                    {
                        if (status != NW_OK || !value)
                            std::terminate();
                        result.set_value(*value);
                    });
            });
        gate.arrive_and_wait();
        executor.request_close();
        starter.join();
        if (started == AdmissionError::None)
        {
            CHECK(ready(future) == 1);
            settle(executor);
        }
        else
        {
            CHECK(started == AdmissionError::Closed && executor.snapshot().issued == 1);
        }
        issued.reset();
        closed(executor);
    }
    {
        Executor first({2, 1, 2, 2, 8, 2}), second({});
        closed(first);
        CHECK(first.snapshot().workers_started == 2);
        CHECK(!second.snapshot().closing && !second.snapshot().joined);
        closed(second);
        CHECK(second.snapshot().workers_started == 1);
    }
    {
        Executor executor({});
        auto issued = acquire(executor, 1);
        std::promise<bool> result;
        auto future = result.get_future();
        CHECK(executor.spawn<bool>(
                  issued, [&] { return executor.join(); },
                  [&](auto status, auto value, auto)
                  {
                      if (status != NW_OK || !value)
                          std::terminate();
                      result.set_value(*value);
                  }) == AdmissionError::None);
        CHECK(!ready(future));
        CHECK(executor.snapshot().closing);
        settle(executor);
        issued.reset();
        closed(executor);
    }
}
} // namespace
int main()
{
    try
    {
        admission_and_waiters();
        retirement_and_blocking();
        actual_exceptions();
        close_races_and_ownership();
        std::cout << "PASS native execution: " << checks << " assertions; 64 close/permit races\n";
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << "FAIL native execution: " << error.what() << '\n';
        return 1;
    }
}
