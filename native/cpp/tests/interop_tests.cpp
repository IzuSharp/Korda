#include "nw/interop.hpp"

#include <algorithm>
#include <barrier>
#include <condition_variable>
#include <cstring>
#include <functional>
#include <iostream>
#include <stdexcept>
#include <thread>

namespace
{
std::atomic<std::size_t> assertions{0};
void check(bool value, const char* expression, int line)
{
    ++assertions;
    if (!value)
    {
        throw std::runtime_error(std::string("line ") + std::to_string(line) + ": " + expression);
    }
}
#define CHECK(value) check((value), #value, __LINE__)

using nw::Operation;
using nw::OperationPhase;
using nw::Subscription;
using nw::SubscriptionPhase;
using namespace nw::interop;

nw_status NW_CDECL notice(std::uintptr_t cookie, std::uint32_t, const nw_error_info_v1*)
{
    ++*reinterpret_cast<std::atomic<std::size_t>*>(cookie);
    return NW_OK;
}
nw_status NW_CDECL reject(std::uintptr_t, std::uint32_t, const nw_error_info_v1*)
{
    return NW_CALLBACK_FAILED;
}
nw_operation_callbacks_v1 callbacks(std::atomic<std::size_t>& count)
{
    return {sizeof(nw_operation_callbacks_v1), 0, reinterpret_cast<std::uintptr_t>(&count), notice};
}

void async_state()
{
    std::atomic<std::size_t> count{0};
    auto op = std::make_shared<Operation<int>>();
    CHECK(op->can_release());
    CHECK(op->try_retire() == NW_INVALID_STATE);
    CHECK(op->reserve_source().first == NW_INVALID_STATE);
    CHECK(op->request_cancel() == NW_OK);
    auto table = callbacks(count);
    table.flags = 1;
    CHECK(op->start(table) == NW_INVALID_ARGUMENT);
    CHECK(op->snapshot().phase == OperationPhase::Created);
    CHECK(op->start(callbacks(count)) == NW_OK);
    CHECK(op->start(callbacks(count)) == NW_INVALID_STATE);
    CHECK(op->cancel_requested());
    CHECK(op->try_retire() == NW_PENDING);
    CHECK(op->complete(NW_PENDING, 9) == NW_INVALID_ARGUMENT);
    CHECK(op->complete(NW_OK, 42) == NW_OK);
    CHECK(count == 1);
    CHECK(op->try_retire() == NW_OK);
    auto [status, value] = op->take_result();
    CHECK(status == NW_OK && value == 42);
    CHECK(op->take_result().first == NW_INVALID_STATE);
    CHECK(op->complete(NW_CANCELED, std::nullopt) == NW_INVALID_STATE);
    CHECK(count == 1 && op->can_release());
    CHECK(op->request_cancel() == NW_OK);

    auto failed = std::make_shared<Operation<std::vector<int>>>();
    CHECK(failed->start({sizeof(nw_operation_callbacks_v1), 0, 1, reject}) == NW_OK);
    CHECK(failed->complete(NW_OK, std::vector<int>{1, 2}) == NW_OK);
    CHECK(failed->snapshot().terminal_status == NW_OK);
    CHECK(failed->snapshot().delivery_failed);
    CHECK(failed->try_retire() == NW_OK);
    CHECK(failed->take_result().second == std::vector<int>({1, 2}));

    auto source_op = std::make_shared<Operation<int>>();
    CHECK(source_op->start(callbacks(count)) == NW_OK);
    auto [reserved, source] = source_op->reserve_source();
    CHECK(reserved == NW_OK);
    CHECK(source_op->complete(NW_OK, 7) == NW_OK);
    CHECK(source_op->take_result().second == 7);
    CHECK(source_op->try_retire() == NW_PENDING);
    CHECK(!source_op->can_release());
    source.reset();
    CHECK(source_op->try_retire() == NW_OK);

    for (int iteration = 0; iteration < 32; ++iteration)
    {
        std::atomic<std::size_t> notices{0};
        std::atomic<int> winners{0};
        auto concurrent = std::make_shared<Operation<int>>();
        CHECK(concurrent->start(callbacks(notices)) == NW_OK);
        std::vector<std::thread> workers;
        for (int i = 0; i < 4; ++i)
        {
            workers.emplace_back(
                [&, i]
                {
                    const auto result = concurrent->complete(NW_OK, i);
                    CHECK(result == NW_OK || result == NW_INVALID_STATE);
                    if (result == NW_OK)
                    {
                        ++winners;
                    }
                });
        }
        for (auto& worker : workers)
        {
            worker.join();
        }
        CHECK(notices == 1 && winners == 1);
        CHECK(concurrent->try_retire() == NW_OK);
        CHECK(concurrent->take_result().first == NW_OK);
        CHECK(concurrent->take_result().first == NW_INVALID_STATE);
    }
}

struct TerminalGate
{
    std::mutex mutex;
    std::condition_variable changed;
    bool entered = false;
    bool released = false;
};
nw_status NW_CDECL gated_notice(std::uintptr_t cookie, std::uint32_t, const nw_error_info_v1*)
{
    auto& gate = *reinterpret_cast<TerminalGate*>(cookie);
    std::unique_lock lock(gate.mutex);
    gate.entered = true;
    gate.changed.notify_all();
    gate.changed.wait(lock, [&] { return gate.released; });
    return NW_OK;
}
void callback_and_owner_retirement()
{
    TerminalGate gate;
    auto op = std::make_shared<Operation<int>>();
    CHECK(op->start({sizeof(nw_operation_callbacks_v1), 0, reinterpret_cast<std::uintptr_t>(&gate), gated_notice}) ==
          NW_OK);
    std::thread complete([&] { CHECK(op->complete(NW_OK, 3) == NW_OK); });
    {
        std::unique_lock lock(gate.mutex);
        gate.changed.wait(lock, [&] { return gate.entered; });
    }
    CHECK(op->snapshot().callbacks_in_flight == 1);
    CHECK(op->take_result().second == 3);
    CHECK(op->try_retire() == NW_PENDING && !op->can_release());
    {
        std::lock_guard lock(gate.mutex);
        gate.released = true;
    }
    gate.changed.notify_all();
    complete.join();
    CHECK(op->try_retire() == NW_OK);

    struct Reentrant
    {
        std::shared_ptr<std::function<void()>> destroy;
        explicit Reentrant(std::shared_ptr<std::function<void()>> callback) : destroy(std::move(callback)) {}
        Reentrant(Reentrant&& other) noexcept : destroy(other.destroy) {}
        ~Reentrant() noexcept
        {
            (*destroy)();
        }
    };
    std::atomic<std::size_t> count{0};
    auto owning = std::make_shared<Operation<Reentrant>>();
    std::weak_ptr<Operation<Reentrant>> weak = owning;
    std::atomic<int> destroyed{0};
    auto destroy = std::make_shared<std::function<void()>>(
        [&]
        {
            if (const auto live = weak.lock())
            {
                static_cast<void>(live->snapshot());
            }
            ++destroyed;
        });
    CHECK(owning->start(callbacks(count)) == NW_OK);
    CHECK(owning->complete(NW_OK, Reentrant(destroy)) == NW_OK);
    CHECK(owning->take_result().first == NW_OK);
    CHECK(destroyed >= 3);
    CHECK(owning->try_retire() == NW_OK);
}

struct EventContext
{
    std::shared_ptr<Subscription> subscription = std::make_shared<Subscription>();
    std::atomic<std::size_t> calls{0};
    std::atomic<std::size_t> bad{0};
    bool stop = false;
};
nw_status NW_CDECL event(std::uintptr_t cookie, std::uint32_t, const void*, std::uint64_t)
{
    auto& context = *reinterpret_cast<EventContext*>(cookie);
    ++context.calls;
    if (context.subscription->snapshot().phase == SubscriptionPhase::Retired)
    {
        ++context.bad;
    }
    if (context.stop)
    {
        if (context.subscription->request_stop() != NW_OK || context.subscription->try_retire() != NW_PENDING)
        {
            ++context.bad;
        }
    }
    return NW_OK;
}
nw_callbacks_v1 event_table(EventContext& context)
{
    return {sizeof(nw_callbacks_v1), 0, reinterpret_cast<std::uintptr_t>(&context), event};
}
void callback_state()
{
    EventContext context;
    auto table = event_table(context);
    table.flags = 1;
    CHECK(context.subscription->start(table) == NW_INVALID_ARGUMENT);
    CHECK(context.subscription->can_release());
    CHECK(context.subscription->start(event_table(context)) == NW_OK);
    table.cookie = 0;
    table.on_event = nullptr;
    CHECK(context.subscription->emit(1, {}) == NW_OK);
    CHECK(context.calls == 1);
    auto [status, source] = context.subscription->reserve_source();
    CHECK(status == NW_OK);
    CHECK(context.subscription->request_stop() == NW_OK);
    CHECK(context.subscription->try_retire() == NW_PENDING);
    source.reset();
    CHECK(context.subscription->try_retire() == NW_OK);
    CHECK(context.subscription->emit(1, {}) == NW_INVALID_STATE);

    EventContext reentrant;
    reentrant.stop = true;
    CHECK(reentrant.subscription->start(event_table(reentrant)) == NW_OK);
    CHECK(reentrant.subscription->emit(1, {}) == NW_OK);
    CHECK(reentrant.bad == 0 && reentrant.subscription->try_retire() == NW_OK);

    for (int iteration = 0; iteration < 32; ++iteration)
    {
        EventContext concurrent;
        CHECK(concurrent.subscription->start(event_table(concurrent)) == NW_OK);
        std::vector<std::thread> producers;
        for (int n = 0; n < 4; ++n)
        {
            producers.emplace_back(
                [&]
                {
                    for (int call = 0; call < 128; ++call)
                    {
                        const auto result = concurrent.subscription->emit(5, {});
                        CHECK(result == NW_OK || result == NW_INVALID_STATE);
                    }
                });
        }
        CHECK(concurrent.subscription->request_stop() == NW_OK);
        const auto retired = concurrent.subscription->try_retire();
        CHECK(retired == NW_OK || retired == NW_PENDING);
        for (auto& producer : producers)
        {
            producer.join();
        }
        CHECK(concurrent.subscription->try_retire() == NW_OK);
        CHECK(concurrent.bad == 0);
    }
}

void wire_and_errors()
{
    static_assert(sizeof(nw_abi_info_v1) == 32 && alignof(nw_abi_info_v1) == 8);
    static_assert(sizeof(nw_error_info_v1) == 24 && alignof(nw_error_info_v1) == 8);
    static_assert(sizeof(nw_operation_callbacks_v1) == 24 && sizeof(nw_callbacks_v1) == 24);
    alignas(8) std::array<std::uint8_t, 64> memory{};
    for (std::uint32_t capacity = 0; capacity < 32; ++capacity)
    {
        memory.fill(0xa5);
        CHECK(write_abi(1, capacity, reinterpret_cast<nw_abi_info_v1*>(memory.data()), 0, 0) == NW_BUFFER_TOO_SMALL);
        CHECK(std::ranges::all_of(memory, [](auto byte) { return byte == 0xa5; }));
    }
    memory.fill(0xa5);
    CHECK(write_abi(2, 64, reinterpret_cast<nw_abi_info_v1*>(memory.data()), 0, 0) == NW_NOT_SUPPORTED);
    CHECK(write_abi(1, 64, reinterpret_cast<nw_abi_info_v1*>(memory.data() + 1), 0, 0) == NW_INVALID_ARGUMENT);
    CHECK(write_abi(1, 64, reinterpret_cast<nw_abi_info_v1*>(memory.data()), 3, UINT64_C(0x8000000000000001)) == NW_OK);
    CHECK(std::ranges::all_of(std::span(memory).subspan(32), [](auto byte) { return byte == 0xa5; }));
    for (std::uint32_t status = 0; status <= 15; ++status)
    {
        const auto wire_status = status == 15 ? UINT32_MAX : status;
        for (std::uint32_t capacity = 0; capacity <= 64; ++capacity)
        {
            memory.fill(0xa5);
            std::memcpy(memory.data(), &capacity, sizeof(capacity));
            const auto original = memory;
            CHECK(write_error(wire_status, reinterpret_cast<nw_error_info_v1*>(memory.data()),
                              {UINT32_MAX, INT32_MIN, UINT64_MAX}) == wire_status);
            if (wire_status <= NW_PENDING || capacity < 24)
            {
                CHECK(memory == original);
            }
            else
            {
                nw_error_info_v1 value{};
                std::memcpy(&value, memory.data(), sizeof(value));
                CHECK(value.struct_size == 24 && value.domain == UINT32_MAX && value.code == INT32_MIN &&
                      value.flags == 0 && value.correlation_id == UINT64_MAX);
                CHECK(std::equal(memory.begin() + 24, memory.end(), original.begin() + 24));
            }
        }
    }
    CHECK(checked_extent(0, 0, 0, 1, 0, 0, 0, 1) == 0);
    CHECK(!checked_extent(0, 1, 1, 1, 0, 1, 1, 1));
    CHECK(checked_extent(8, 12, 3, 4, 0, 12, 12, 4) == 8);
    CHECK(!checked_extent(8, 12, 3, 4, UINT64_MAX, 12, 12, 4));
    CHECK(!checked_extent(8, UINT64_MAX, UINT64_MAX, 2, 0, UINT64_MAX, UINT64_MAX, 1));
    const std::string utf8 = "A\xc3\xa9\xe6\xb0\xb4\xf0\x9f\xa6\x80Z";
    for (std::size_t limit = 0; limit <= utf8.size() + 1; ++limit)
    {
        const auto detail = BoundedDetail::from_redacted(utf8, limit);
        CHECK(detail.bytes().size() <= limit);
        CHECK(detail.bytes().size() == utf8.size() ||
              (static_cast<unsigned char>(utf8[detail.bytes().size()]) & 0xc0U) != 0x80U);
        CHECK(detail.is_truncated() == (detail.bytes().size() < utf8.size()));
    }
}

void boundary_admission()
{
    EngineBoundary boundary;
    CHECK(boundary.business([]() -> nw_status { throw Failure(NW_INVALID_ARGUMENT); }) == NW_INVALID_ARGUMENT);
    CHECK(!boundary.is_poisoned());
    CHECK(boundary.business([] { return NW_OK; }) == NW_OK);
    struct Dangerous
    {
        ~Dangerous() noexcept
        {
            std::abort();
        }
    };
    CHECK(boundary.business([]() -> nw_status { throw Dangerous{}; }) == NW_NATIVE_PANIC);
    CHECK(boundary.is_poisoned() && boundary.active_calls() == 0 && boundary.quarantined_payloads() == 1);
    CHECK(boundary.business([] { return NW_OK; }) == NW_ENGINE_POISONED);
    CHECK(boundary.cleanup([] { return NW_OK; }) == NW_OK);
    for (nw_status invalid : {NW_OK, NW_PENDING, NW_CONTRACT_VIOLATION})
    {
        EngineBoundary invalid_boundary;
        CHECK(invalid_boundary.business(
                  [&]() -> nw_status
                  {
                      Failure failure(NW_INVALID_STATE);
                      failure.status = invalid;
                      throw failure;
                  }) == NW_CONTRACT_VIOLATION);
        CHECK(invalid_boundary.is_poisoned());
    }
    EngineBoundary concurrent;
    std::barrier entered(9), proceed(9);
    std::atomic<int> success{0};
    std::vector<std::thread> threads;
    for (int i = 0; i < 8; ++i)
    {
        threads.emplace_back(
            [&, i]
            {
                const auto status = concurrent.business(
                    [&]
                    {
                        entered.arrive_and_wait();
                        proceed.arrive_and_wait();
                        if (i < 2)
                        {
                            throw std::runtime_error("controlled exception");
                        }
                        return NW_OK;
                    });
                if (status == NW_OK)
                {
                    ++success;
                }
                else
                {
                    CHECK(status == NW_NATIVE_PANIC);
                }
            });
    }
    entered.arrive_and_wait();
    CHECK(concurrent.active_calls() == 8);
    concurrent.poison();
    CHECK(concurrent.business([] { return NW_OK; }) == NW_ENGINE_POISONED);
    CHECK(concurrent.cleanup([] { return NW_OK; }) == NW_OK);
    proceed.arrive_and_wait();
    for (auto& thread : threads)
    {
        thread.join();
    }
    CHECK(success == 6 && concurrent.quarantined_payloads() == 2 && concurrent.active_calls() == 0);
}

void owner_publication()
{
    std::size_t drops = 0;
    struct Owner
    {
        std::size_t& drops;
        explicit Owner(std::size_t& value) : drops(value) {}
        ~Owner()
        {
            ++drops;
        }
    };
    EngineBoundary boundary;
    auto* output = reinterpret_cast<Owner*>(alignof(Owner));
    CHECK(create_owner(boundary, &output,
                       [&]() -> std::unique_ptr<Owner>
                       {
                           auto temporary = std::make_unique<Owner>(drops);
                           throw Failure(NW_INVALID_ARGUMENT);
                       }) == NW_INVALID_ARGUMENT);
    CHECK(output == nullptr && drops == 1);
    CHECK(create_owner(boundary, &output, [&] { return std::make_unique<Owner>(drops); }) == NW_OK);
    CHECK(output != nullptr && drops == 1);
    delete output;
    CHECK(drops == 2);
}
} // namespace

int main()
{
    try
    {
        async_state();
        callback_and_owner_retirement();
        callback_state();
        wire_and_errors();
        boundary_admission();
        owner_publication();
        std::cout << "PASS native interop: " << assertions << " assertions\n";
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
