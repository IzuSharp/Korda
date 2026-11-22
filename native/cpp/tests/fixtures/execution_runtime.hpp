#ifndef NW_EXECUTION_FIXTURE_RUNTIME_HPP
#define NW_EXECUTION_FIXTURE_RUNTIME_HPP

#include "nw/interop.hpp"

#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <deque>
#include <functional>
#include <thread>

// This optional conformance runtime has no exported ABI. Database Host owns its
// provider runtime; the existing Core execution consumers exercise this adapter.
namespace nw::fixtures::execution
{
enum class AdmissionError
{
    None,
    Pending,
    InvalidLimits,
    Impossible,
    Full,
    Closed,
    WrongOwner,
    AlreadyStarted
};
struct Limits
{
    std::size_t workers = 1, blocking_workers = 1, admitted = 2, waiters = 2;
    std::uint64_t bytes = 8;
    std::size_t cleanup = 2;
};
struct Snapshot
{
    bool closing, poisoned, joined;
    std::size_t admitted, waiters;
    std::uint64_t reserved_bytes;
    std::size_t issued, running, retiring, tracked_tasks;
    std::uint64_t workers_started, workers_stopped, oldest_cleanup_millis, panic_payloads;
};
struct State;
struct Admission
{
    enum class Stage
    {
        Issued,
        Running,
        Retiring,
        Released
    };
    std::shared_ptr<State> state;
    std::uint64_t bytes;
    std::size_t slot;
    Stage stage = Stage::Issued;
    Admission(std::shared_ptr<State> owner, std::uint64_t size, std::size_t index)
        : state(std::move(owner)), bytes(size), slot(index)
    {
    }
    ~Admission();
    void terminal();
};
using Reservation = std::shared_ptr<Admission>;
using Acquired = std::pair<AdmissionError, Reservation>;
struct WaitEntry
{
    std::uint64_t bytes;
    AdmissionError result = AdmissionError::Pending;
    Reservation reservation;
    std::function<void()> wake;
};
struct State : std::enable_shared_from_this<State>
{
    using Clock = std::chrono::steady_clock;
    struct Slot
    {
        bool used = false;
        std::optional<Clock::time_point> retiring_at;
    };
    const Limits limits;
    mutable std::mutex mutex;
    std::condition_variable changed;
    bool closing = false, stop_workers = false;
    std::size_t admitted = 0, issued = 0, running = 0, retiring = 0, tasks = 0;
    std::uint64_t reserved_bytes = 0;
    std::vector<Slot> cleanup;
    std::deque<std::shared_ptr<WaitEntry>> waiters;
    std::deque<std::function<void()>> normal_queue, blocking_queue;
    interop::EngineBoundary boundary;
    std::atomic<std::uint64_t> started{0}, stopped{0};
    std::atomic<bool> owner_finished{false};
    inline static thread_local State* current_worker = nullptr;

    explicit State(Limits value) : limits(value), cleanup(std::min(value.cleanup, value.admitted)) {}
    bool available(std::uint64_t bytes) const
    {
        return admitted < limits.admitted && bytes <= limits.bytes - reserved_bytes &&
               std::any_of(cleanup.begin(), cleanup.end(), [](const Slot& slot) { return !slot.used; });
    }
    Reservation reserve(std::uint64_t bytes)
    {
        auto slot = std::find_if(cleanup.begin(), cleanup.end(), [](const Slot& value) { return !value.used; });
        const auto index = static_cast<std::size_t>(slot - cleanup.begin());
        auto reservation = std::make_shared<Admission>(shared_from_this(), bytes, index);
        *slot = Slot{true, std::nullopt};
        ++admitted;
        ++issued;
        reserved_bytes += bytes;
        return reservation;
    }
    void pump_locked(std::vector<std::function<void()>>& wakes)
    {
        while (!waiters.empty())
        {
            auto entry = waiters.front();
            if (!closing && !available(entry->bytes))
            {
                break;
            }
            if (closing)
            {
                entry->result = AdmissionError::Closed;
            }
            else
            {
                entry->reservation = reserve(entry->bytes);
                entry->result = AdmissionError::None;
            }
            waiters.pop_front();
            if (entry->wake)
            {
                wakes.push_back(std::move(entry->wake));
            }
        }
    }
    static void wake_all(std::vector<std::function<void()>> wakes)
    {
        // A wake may reenter admission diagnostics or release another owner.
        for (auto& wake : wakes)
        {
            wake();
        }
    }
    void close()
    {
        std::vector<std::function<void()>> wakes;
        {
            std::lock_guard lock(mutex);
            closing = true;
            pump_locked(wakes);
        }
        changed.notify_all();
        wake_all(std::move(wakes));
    }
    void work(bool blocking)
    {
        current_worker = this;
        ++started;
        for (;;)
        {
            std::function<void()> task;
            {
                std::unique_lock lock(mutex);
                auto& queue = blocking ? blocking_queue : normal_queue;
                changed.wait(lock, [&] { return stop_workers || !queue.empty(); });
                if (queue.empty() && stop_workers)
                {
                    break;
                }
                task = std::move(queue.front());
                queue.pop_front();
            }
            task();
            // Destroy captured owners outside the admission gate.
        }
        ++stopped;
        current_worker = nullptr;
    }
};

inline Admission::~Admission()
{
    std::vector<std::function<void()>> wakes;
    {
        std::lock_guard lock(state->mutex);
        if (stage == Stage::Running || stage == Stage::Released)
        {
            std::terminate();
        }
        if (stage == Stage::Issued)
        {
            --state->issued;
        }
        else
        {
            --state->retiring;
        }
        stage = Stage::Released;
        --state->admitted;
        state->reserved_bytes -= bytes;
        state->cleanup[slot] = {};
        state->pump_locked(wakes);
    }
    state->changed.notify_all();
    State::wake_all(std::move(wakes));
}
inline void Admission::terminal()
{
    {
        std::lock_guard lock(state->mutex);
        if (stage != Stage::Running)
        {
            std::terminate();
        }
        stage = Stage::Retiring;
        --state->running;
        ++state->retiring;
        --state->tasks;
        state->cleanup[slot].retiring_at = State::Clock::now();
    }
    state->changed.notify_all();
}

class Waiting
{
  public:
    Waiting(std::shared_ptr<State> state, std::shared_ptr<WaitEntry> entry)
        : state_(std::move(state)), entry_(std::move(entry))
    {
    }
    Waiting(const Waiting&) = delete;
    Waiting& operator=(const Waiting&) = delete;
    Waiting(Waiting&&) noexcept = default;
    ~Waiting()
    {
        if (!state_)
        {
            return;
        }
        std::vector<std::function<void()>> wakes;
        {
            std::lock_guard lock(state_->mutex);
            auto found = std::find(state_->waiters.begin(), state_->waiters.end(), entry_);
            if (found != state_->waiters.end())
            {
                state_->waiters.erase(found);
            }
            state_->pump_locked(wakes);
        }
        State::wake_all(std::move(wakes));
        // An issued but unclaimed reservation is also returned outside the gate.
        entry_.reset();
    }
    Acquired poll(std::function<void()> wake = {})
    {
        std::lock_guard lock(state_->mutex);
        if (entry_->result == AdmissionError::Pending)
        {
            entry_->wake = std::move(wake);
            return {AdmissionError::Pending, {}};
        }
        const auto result = entry_->result;
        entry_->result = AdmissionError::AlreadyStarted;
        return {result, std::move(entry_->reservation)};
    }

  private:
    std::shared_ptr<State> state_;
    std::shared_ptr<WaitEntry> entry_;
};

class Executor
{
    struct Threads
    {
        std::mutex mutex;
        std::vector<std::thread> workers;
        bool blocking_started = false;
    };
    std::shared_ptr<State> state_;
    std::shared_ptr<Threads> threads_;
    std::thread owner_;
    mutable std::mutex join_mutex_;
    std::condition_variable join_changed_;
    bool joining_ = false, joined_ = false;

    AdmissionError validate(std::uint64_t bytes) const
    {
        return bytes > state_->limits.bytes || state_->limits.admitted == 0 || state_->limits.cleanup == 0
                   ? AdmissionError::Impossible
                   : AdmissionError::None;
    }

  public:
    explicit Executor(Limits limits)
    {
        if (limits.workers == 0 || limits.blocking_workers == 0 || limits.workers > 64 ||
            limits.blocking_workers > 64 || limits.admitted > 1000000 || limits.waiters > 1000000 ||
            limits.cleanup > 1000000)
        {
            throw AdmissionError::InvalidLimits;
        }
        state_ = std::make_shared<State>(limits);
        threads_ = std::make_shared<Threads>();
        try
        {
            for (std::size_t index = 0; index < limits.workers; ++index)
            {
                threads_->workers.emplace_back([state = state_] { state->work(false); });
            }
            owner_ = std::thread(
                [state = state_, threads = threads_]
                {
                    {
                        std::unique_lock lock(state->mutex);
                        state->changed.wait(lock, [&]
                                            { return state->closing && state->admitted == 0 && state->tasks == 0; });
                        state->stop_workers = true;
                    }
                    state->changed.notify_all();
                    {
                        std::lock_guard lock(threads->mutex);
                        for (auto& worker : threads->workers)
                        {
                            worker.join();
                        }
                    }
                    state->owner_finished = true;
                    state->changed.notify_all();
                });
        }
        catch (...)
        {
            {
                std::lock_guard lock(state_->mutex);
                state_->stop_workers = true;
            }
            state_->changed.notify_all();
            for (auto& worker : threads_->workers)
            {
                if (worker.joinable())
                {
                    worker.join();
                }
            }
            throw AdmissionError::InvalidLimits;
        }
    }
    Executor(const Executor&) = delete;
    Executor& operator=(const Executor&) = delete;
    ~Executor()
    {
        request_close();
        // Finalization never joins a thread that may still own live callbacks.
        // The runtime-owner thread retains the actual workers until they return.
        if (owner_.joinable())
        {
            owner_.detach();
        }
    }
    Acquired try_acquire(std::uint64_t bytes)
    {
        const auto valid = validate(bytes);
        if (valid != AdmissionError::None)
        {
            return {valid, {}};
        }
        std::lock_guard lock(state_->mutex);
        if (state_->closing)
        {
            return {AdmissionError::Closed, {}};
        }
        if (!state_->waiters.empty() || !state_->available(bytes))
        {
            return {AdmissionError::Full, {}};
        }
        return {AdmissionError::None, state_->reserve(bytes)};
    }
    Waiting acquire(std::uint64_t bytes)
    {
        auto entry = std::make_shared<WaitEntry>();
        entry->bytes = bytes;
        {
            std::lock_guard lock(state_->mutex);
            entry->result = validate(bytes);
            if (entry->result == AdmissionError::None)
            {
                if (state_->closing)
                {
                    entry->result = AdmissionError::Closed;
                }
                else if (state_->waiters.empty() && state_->available(bytes))
                {
                    entry->reservation = state_->reserve(bytes);
                }
                else if (state_->waiters.size() == state_->limits.waiters)
                {
                    entry->result = AdmissionError::Full;
                }
                else
                {
                    entry->result = AdmissionError::Pending;
                    state_->waiters.push_back(entry);
                }
            }
        }
        return {state_, std::move(entry)};
    }
    void request_close()
    {
        state_->close();
    }
    bool is_closing() const
    {
        std::lock_guard lock(state_->mutex);
        return state_->closing;
    }
    void wait_closed()
    {
        std::unique_lock lock(state_->mutex);
        state_->changed.wait(lock, [&] { return state_->closing; });
    }
    void poison()
    {
        state_->boundary.poison();
        request_close();
    }

    template <class R, class Body, class Completion>
    AdmissionError spawn(const Reservation& reservation, Body body, Completion completion, bool blocking = false)
    {
        if (reservation->state != state_)
        {
            return AdmissionError::WrongOwner;
        }
        // Allocate the trusted supervisor and captures before spending the permit.
        auto task = [state = state_, reservation, body = std::move(body), completion = std::move(completion)]() mutable
        {
            std::optional<R> result;
            ErrorFields fields{};
            const auto status = state->boundary.cleanup(
                [&]
                {
                    result.emplace(body());
                    return NW_OK;
                },
                &fields);
            if (state->boundary.is_poisoned())
            {
                state->close();
            }
            (void)state->boundary.cleanup(
                [&]
                {
                    completion(status, std::move(result), fields);
                    return NW_OK;
                });
            if (state->boundary.is_poisoned())
            {
                state->close();
            }
            reservation->terminal();
        };
        std::function<void()> supervisor(std::move(task));
        {
            std::lock_guard lock(state_->mutex);
            if (state_->closing)
            {
                return AdmissionError::Closed;
            }
            if (reservation->stage != Admission::Stage::Issued)
            {
                return AdmissionError::AlreadyStarted;
            }
            if (blocking)
            {
                std::lock_guard thread_lock(threads_->mutex);
                if (!threads_->blocking_started)
                {
                    for (std::size_t index = 0; index < state_->limits.blocking_workers; ++index)
                    {
                        threads_->workers.emplace_back([state = state_] { state->work(true); });
                    }
                    threads_->blocking_started = true;
                }
            }
            auto& queue = blocking ? state_->blocking_queue : state_->normal_queue;
            queue.push_back(std::move(supervisor));
            reservation->stage = Admission::Stage::Running;
            --state_->issued;
            ++state_->running;
            ++state_->tasks;
        }
        state_->changed.notify_all();
        return AdmissionError::None;
    }
    bool join(bool only_if_ready = false)
    {
        request_close();
        if (State::current_worker == state_.get())
        {
            return false;
        }
        std::unique_lock lock(join_mutex_);
        if (only_if_ready && (!state_->owner_finished || joining_))
        {
            return joined_;
        }
        join_changed_.wait(lock, [&] { return !joining_; });
        if (joined_)
        {
            return true;
        }
        joining_ = true;
        lock.unlock();
        owner_.join();
        lock.lock();
        joined_ = true;
        joining_ = false;
        lock.unlock();
        join_changed_.notify_all();
        return true;
    }
    bool try_join()
    {
        return join(true);
    }
    Snapshot snapshot() const
    {
        std::lock_guard join_lock(join_mutex_);
        std::lock_guard lock(state_->mutex);
        std::uint64_t oldest = 0;
        for (const auto& slot : state_->cleanup)
        {
            if (slot.retiring_at)
            {
                const auto age =
                    std::chrono::duration_cast<std::chrono::milliseconds>(State::Clock::now() - *slot.retiring_at)
                        .count();
                oldest = std::max(oldest, static_cast<std::uint64_t>(age));
            }
        }
        return {state_->closing,
                state_->boundary.is_poisoned(),
                joined_,
                state_->admitted,
                state_->waiters.size(),
                state_->reserved_bytes,
                state_->issued,
                state_->running,
                state_->retiring,
                state_->tasks,
                state_->started,
                state_->stopped,
                oldest,
                state_->boundary.quarantined_payloads()};
    }
};
} // namespace nw::fixtures::execution
#endif
