#include "execution_runtime.hpp"
#include "support.hpp"

#include <numeric>

namespace
{
using namespace nw;
using namespace nw::fixtures;
using namespace nw::fixtures::execution;
std::atomic<std::uint64_t> jobs_created{0}, jobs_released{0}, engines_created{0}, engines_released{0};
std::atomic<std::uint64_t> rejected{0}, bad_releases{0}, closes_created{0}, closes_released{0};
interop::EngineBoundary boundary;

struct Engine
{
    std::shared_ptr<Executor> executor;
    std::mutex mutex;
    std::condition_variable released;
    bool gate = false;
    std::atomic<std::uint64_t> blocking_entered{0};
    std::atomic<bool> close_created{false};
};
using EngineOwner = std::shared_ptr<Engine>;
struct Job
{
    EngineOwner engine;
    Reservation admission;
    // C++ destroys members in reverse order: actual payload precedes its credit.
    std::shared_ptr<Bytes> payload;
    std::shared_ptr<Operation<std::uint64_t>> operation = std::make_shared<Operation<std::uint64_t>>();
    std::mutex held_mutex;
    WorkLease<std::uint64_t> held;
    std::uint32_t mode;
};
struct JoinResult
{
    std::uint64_t joined = 0, started = 0, stopped = 0, outstanding = 0, bytes = 0;
};
static_assert(sizeof(JoinResult) == 40);
struct Close
{
    std::shared_ptr<Operation<JoinResult>> operation = std::make_shared<Operation<JoinResult>>();
    EngineOwner engine;
    std::mutex observer_mutex;
    bool installed = false, joining = false, joined = false, failed = false;
    std::atomic<bool> finished{false};
    std::thread observer;
};
nw_status admission_status(AdmissionError error)
{
    switch (error)
    {
        case AdmissionError::None:
            return NW_OK;
        case AdmissionError::Impossible:
        case AdmissionError::InvalidLimits:
            return NW_INVALID_ARGUMENT;
        case AdmissionError::Full:
            return NW_OUT_OF_MEMORY;
        default:
            return NW_INVALID_STATE;
    }
}
template <class R>
void finish(Operation<R>& operation, nw_status status, std::optional<R> result = {}, ErrorFields fields = {})
{
    (void)operation.complete(status, std::move(result), fields);
}
struct Work
{
    WorkLease<std::uint64_t> source;
    EngineOwner engine;
    std::shared_ptr<Bytes> payload;
    std::uint32_t mode;
};
} // namespace

NW_FIXTURE_API nw_status nw_execution_fixture_create_v1(std::uint32_t workers, std::uint32_t admitted,
                                                        std::uint32_t waiters, std::uint64_t bytes,
                                                        std::uint32_t cleanup, EngineOwner** output) noexcept
{
    return interop::create_owner(boundary, output,
                                 [&]
                                 {
                                     auto engine = std::make_shared<Engine>();
                                     try
                                     {
                                         engine->executor = std::make_shared<Executor>(
                                             Limits{workers, 1, admitted, waiters, bytes, cleanup});
                                     }
                                     catch (AdmissionError error)
                                     {
                                         throw interop::Failure(admission_status(error));
                                     }
                                     auto owner = std::make_unique<EngineOwner>(std::move(engine));
                                     ++engines_created;
                                     return owner;
                                 });
}
NW_FIXTURE_API void nw_execution_fixture_request_close_v1(const EngineOwner* engine) noexcept
{
    if (engine != nullptr)
    {
        (*engine)->executor->request_close();
    }
}
NW_FIXTURE_API void nw_execution_fixture_engine_release_v1(EngineOwner* engine) noexcept
{
    if (engine == nullptr)
    {
        return;
    }
    (*engine)->executor->request_close();
    delete engine;
    ++engines_released;
}
NW_FIXTURE_API nw_status nw_execution_fixture_job_create_v1(const EngineOwner* engine, std::uint32_t mode,
                                                            const std::uint8_t* data, std::uint64_t length,
                                                            Job** output) noexcept
{
    return interop::create_owner(boundary, output,
                                 [&]
                                 {
                                     if (engine == nullptr || mode > 5 || length > 4096 ||
                                         (length != 0 && data == nullptr))
                                     {
                                         throw interop::Failure(NW_INVALID_ARGUMENT);
                                     }
                                     auto [status, admission] = (*engine)->executor->try_acquire(length);
                                     if (status != AdmissionError::None)
                                     {
                                         ++rejected;
                                         throw interop::Failure(admission_status(status));
                                     }
                                     auto job = std::make_unique<Job>();
                                     job->engine = *engine;
                                     job->admission = std::move(admission);
                                     job->payload = std::make_shared<Bytes>();
                                     job->payload->reserve(static_cast<std::size_t>(length));
                                     if (length != 0)
                                     {
                                         job->payload->insert(job->payload->end(), data, data + length);
                                     }
                                     job->mode = mode;
                                     ++jobs_created;
                                     return job;
                                 });
}
NW_FIXTURE_API nw_status nw_execution_fixture_job_start_v1(Job* job,
                                                           const nw_operation_callbacks_v1* callbacks) noexcept
{
    if (job == nullptr || !callback_table(callbacks))
    {
        return NW_INVALID_ARGUMENT;
    }
    const auto started = job->operation->start(*callbacks);
    if (started != NW_OK)
    {
        return started;
    }
    const auto setup = boundary.cleanup(
        [&]
        {
            if (job->mode == 5)
            {
                auto [status, source] = job->operation->reserve_source();
                if (status != NW_OK)
                {
                    throw interop::Failure(status);
                }
                std::lock_guard lock(job->held_mutex);
                job->held = std::move(source);
            }
            auto [status, source] = job->operation->reserve_source();
            if (status != NW_OK)
            {
                throw interop::Failure(status);
            }
            auto work = std::make_shared<Work>(Work{std::move(source), job->engine, job->payload, job->mode});
            const auto admission = job->engine->executor->spawn<std::uint64_t>(
                job->admission,
                [work]
                {
                    if (work->mode == 3 || work->mode == 4)
                    {
                        throw std::runtime_error("actual C++ execution fixture exception");
                    }
                    if (work->mode == 1)
                    {
                        ++work->engine->blocking_entered;
                        std::unique_lock lock(work->engine->mutex);
                        work->engine->released.wait(lock, [&] { return work->engine->gate; });
                    }
                    if (work->mode == 2)
                    {
                        work->engine->executor->wait_closed();
                    }
                    if (work->source.operation().cancel_requested() || work->engine->executor->is_closing())
                    {
                        throw interop::Failure(NW_CANCELED);
                    }
                    return std::accumulate(work->payload->begin(), work->payload->end(), std::uint64_t{0});
                },
                [work](nw_status terminal, std::optional<std::uint64_t> value, ErrorFields fields)
                { finish(work->source.operation(), terminal, std::move(value), fields); },
                job->mode == 1 || job->mode == 4);
            if (admission != AdmissionError::None)
            {
                finish(*job->operation, NW_CANCELED);
            }
            return NW_OK;
        });
    if (setup != NW_OK)
    {
        finish(*job->operation, setup);
    }
    // Callback acceptance is committed even if close won the issued-permit race.
    return NW_OK;
}
NW_FIXTURE_API nw_status nw_execution_fixture_job_cancel_v1(Job* job) noexcept
{
    return job == nullptr ? NW_INVALID_ARGUMENT : job->operation->request_cancel();
}
NW_FIXTURE_API nw_status nw_execution_fixture_job_take_v1(Job* job, std::uint64_t* output) noexcept
{
    if (job == nullptr || output == nullptr)
    {
        return NW_INVALID_ARGUMENT;
    }
    *output = 0;
    auto [status, value] = job->operation->take_result();
    if (value)
    {
        *output = *value;
    }
    return status;
}
NW_FIXTURE_API nw_status nw_execution_fixture_job_retire_v1(Job* job) noexcept
{
    return job == nullptr ? NW_INVALID_ARGUMENT : job->operation->try_retire();
}
NW_FIXTURE_API nw_status nw_execution_fixture_source_take_v1(Job* job, WorkLease<std::uint64_t>** output) noexcept
{
    return interop::create_owner(boundary, output,
                                 [&]
                                 {
                                     if (job == nullptr)
                                     {
                                         throw interop::Failure(NW_INVALID_ARGUMENT);
                                     }
                                     auto owner = std::make_unique<WorkLease<std::uint64_t>>();
                                     std::lock_guard lock(job->held_mutex);
                                     if (!job->held)
                                     {
                                         throw interop::Failure(NW_INVALID_STATE);
                                     }
                                     *owner = std::move(job->held);
                                     return owner;
                                 });
}
NW_FIXTURE_API void nw_execution_fixture_source_release_v1(WorkLease<std::uint64_t>* source) noexcept
{
    delete source;
}
NW_FIXTURE_API void nw_execution_fixture_job_release_v1(Job* job) noexcept
{
    if (job == nullptr)
    {
        return;
    }
    if (!job->operation->can_release())
    {
        ++bad_releases;
        return;
    }
    delete job;
    ++jobs_released;
}
NW_FIXTURE_API void nw_execution_fixture_open_gate_v1(const EngineOwner* engine) noexcept
{
    if (engine == nullptr)
    {
        return;
    }
    {
        std::lock_guard lock((*engine)->mutex);
        (*engine)->gate = true;
    }
    (*engine)->released.notify_all();
}
NW_FIXTURE_API std::uint64_t nw_execution_fixture_snapshot_v1(const EngineOwner* engine, std::uint32_t kind) noexcept
{
    if (engine == nullptr)
    {
        return UINT64_MAX;
    }
    const auto state = (*engine)->executor->snapshot();
    switch (kind)
    {
        case 0:
            return state.closing;
        case 1:
            return state.joined;
        case 2:
            return state.admitted;
        case 3:
            return state.reserved_bytes;
        case 4:
            return state.issued;
        case 5:
            return state.running;
        case 6:
            return state.retiring;
        case 7:
            return state.workers_started;
        case 8:
            return state.workers_stopped;
        case 9:
            return state.oldest_cleanup_millis;
        case 10:
            return (*engine)->blocking_entered;
        case 11:
            return state.poisoned;
        case 12:
            return state.panic_payloads;
        case 13:
            return state.tracked_tasks;
        default:
            return UINT64_MAX;
    }
}
NW_FIXTURE_API nw_status nw_execution_fixture_close_create_v1(const EngineOwner* engine, Close** output) noexcept
{
    return interop::create_owner(boundary, output,
                                 [&]
                                 {
                                     if (engine == nullptr)
                                     {
                                         throw interop::Failure(NW_INVALID_ARGUMENT);
                                     }
                                     if ((*engine)->close_created.exchange(true))
                                     {
                                         throw interop::Failure(NW_INVALID_STATE);
                                     }
                                     auto owner = std::make_unique<Close>();
                                     owner->engine = *engine;
                                     ++closes_created;
                                     return owner;
                                 });
}
NW_FIXTURE_API nw_status nw_execution_fixture_close_start_v1(Close* close,
                                                             const nw_operation_callbacks_v1* callbacks) noexcept
{
    if (close == nullptr || !callback_table(callbacks))
    {
        return NW_INVALID_ARGUMENT;
    }
    const auto started = close->operation->start(*callbacks);
    if (started != NW_OK)
    {
        return started;
    }
    close->engine->executor->request_close();
    auto [status, source] = close->operation->reserve_source();
    if (status != NW_OK)
    {
        finish(*close->operation, status);
        return NW_OK;
    }
    std::thread thread;
    const auto setup = boundary.cleanup(
        [&]
        {
            thread = std::thread(
                [close, source = std::move(source), engine = close->engine]() mutable
                {
                    const auto result = boundary.cleanup(
                        [&]
                        {
                            const bool joined = engine->executor->join();
                            const auto state = engine->executor->snapshot();
                            if (joined)
                            {
                                finish(source.operation(), NW_OK,
                                       std::optional<JoinResult>{{1, state.workers_started, state.workers_stopped,
                                                                  state.admitted, state.reserved_bytes}});
                            }
                            else
                            {
                                finish(source.operation(), NW_CONTRACT_VIOLATION);
                            }
                            return NW_OK;
                        });
                    if (result != NW_OK)
                    {
                        finish(source.operation(), result);
                    }
                    source.reset();
                    close->finished = true;
                });
            return NW_OK;
        });
    {
        std::lock_guard lock(close->observer_mutex);
        close->installed = true;
        close->joined = setup != NW_OK;
        close->observer = std::move(thread);
    }
    if (setup != NW_OK)
    {
        finish(*close->operation, setup);
    }
    return NW_OK;
}
NW_FIXTURE_API nw_status nw_execution_fixture_close_cancel_v1(Close* close) noexcept
{
    return close == nullptr ? NW_INVALID_ARGUMENT : NW_OK;
}
NW_FIXTURE_API nw_status nw_execution_fixture_close_take_v1(Close* close, JoinResult* output) noexcept
{
    if (close == nullptr || output == nullptr)
    {
        return NW_INVALID_ARGUMENT;
    }
    *output = {};
    auto [status, value] = close->operation->take_result();
    if (value)
    {
        *output = *value;
    }
    return status;
}
NW_FIXTURE_API nw_status nw_execution_fixture_close_retire_v1(Close* close) noexcept
{
    if (close == nullptr)
    {
        return NW_INVALID_ARGUMENT;
    }
    std::thread observer;
    {
        std::lock_guard lock(close->observer_mutex);
        if (close->failed)
        {
            return NW_CONTRACT_VIOLATION;
        }
        if (!close->installed || close->joining)
        {
            return NW_PENDING;
        }
        if (close->joined)
        {
            return close->operation->try_retire();
        }
        if (!close->finished)
        {
            return NW_PENDING;
        }
        close->joining = true;
        observer = std::move(close->observer);
    }
    observer.join();
    {
        std::lock_guard lock(close->observer_mutex);
        close->joining = false;
        close->joined = true;
    }
    return close->operation->try_retire();
}
NW_FIXTURE_API void nw_execution_fixture_close_release_v1(Close* close) noexcept
{
    if (close == nullptr)
    {
        return;
    }
    if (!close->operation->can_release())
    {
        ++bad_releases;
        return;
    }
    close->engine->executor->request_close();
    delete close;
    ++closes_released;
}
NW_FIXTURE_API std::uint64_t nw_execution_fixture_count_v1(std::uint32_t kind) noexcept
{
    switch (kind)
    {
        case 0:
            return jobs_created;
        case 1:
            return jobs_released;
        case 2:
            return jobs_created.load() - jobs_released.load();
        case 3:
            return engines_created;
        case 4:
            return engines_released;
        case 5:
            return rejected;
        case 6:
            return bad_releases;
        case 7:
            return closes_created;
        case 8:
            return closes_released;
        default:
            return UINT64_MAX;
    }
}
