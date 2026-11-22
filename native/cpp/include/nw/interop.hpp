#ifndef NW_INTEROP_HPP
#define NW_INTEROP_HPP

#include "nw/contracts.hpp"

#include <atomic>
#include <limits>
#include <mutex>
#include <new>
#include <type_traits>

namespace nw
{
namespace interop
{
struct FixtureAccess;
}

enum class OperationPhase
{
    Created,
    Running,
    TerminalReady,
    Retired
};

struct OperationSnapshot
{
    OperationPhase phase;
    std::optional<nw_status> terminal_status;
    bool taken;
    std::size_t callbacks_in_flight;
    std::size_t sources;
    bool delivery_failed;
};

template <class R> class Operation;

// One native source retains the operation independently of the application Task.
template <class R> class WorkLease
{
  public:
    WorkLease() noexcept = default;
    WorkLease(const WorkLease&) = delete;
    WorkLease& operator=(const WorkLease&) = delete;
    WorkLease(WorkLease&& other) noexcept : operation_(std::move(other.operation_)) {}
    WorkLease& operator=(WorkLease&& other) noexcept
    {
        if (this != &other)
        {
            reset();
            operation_ = std::move(other.operation_);
        }
        return *this;
    }
    ~WorkLease()
    {
        reset();
    }
    [[nodiscard]] explicit operator bool() const noexcept
    {
        return bool(operation_);
    }
    [[nodiscard]] Operation<R>& operation() const noexcept
    {
        return *operation_;
    }
    void reset() noexcept
    {
        auto operation = std::move(operation_);
        if (operation)
        {
            std::lock_guard lock(operation->mutex_);
            --operation->state_.sources;
        }
        // The last operation/result destructor runs after the state gate unlocks.
    }

  private:
    explicit WorkLease(std::shared_ptr<Operation<R>> operation) noexcept : operation_(std::move(operation)) {}
    std::shared_ptr<Operation<R>> operation_;
    friend class Operation<R>;
};

// Exact D06 state: terminal notification, result transfer and native retirement
// are independent. No executor or worker is created by this component.
template <class R> class Operation : public std::enable_shared_from_this<Operation<R>>
{
    static_assert(std::is_nothrow_move_constructible_v<R>);
    static_assert(std::is_nothrow_destructible_v<R>);

    struct ResultSlot
    {
        std::optional<R> value;
    };
    struct State
    {
        OperationPhase phase = OperationPhase::Created;
        std::optional<nw_operation_callbacks_v1> callbacks;
        bool cancel_requested = false;
        std::optional<nw_status> terminal_status;
        nw_error_info_v1 error{sizeof(nw_error_info_v1), 0, 0, 0, 0};
        // Allocate the movable slot at operation creation, before publication.
        // Completion and typed take allocate nothing; even a moved-from R is
        // destroyed outside the gate, allowing owner destructors to reenter.
        std::unique_ptr<ResultSlot> result = std::make_unique<ResultSlot>();
        bool taken = false;
        std::size_t callbacks_in_flight = 0;
        std::size_t sources = 0;
        bool delivery_failed = false;
    };
    struct CallbackLease
    {
        Operation& operation;
        ~CallbackLease()
        {
            std::lock_guard lock(operation.mutex_);
            --operation.state_.callbacks_in_flight;
        }
    };

  public:
    Operation() = default;
    Operation(const Operation&) = delete;
    Operation& operator=(const Operation&) = delete;

    [[nodiscard]] nw_status start(nw_operation_callbacks_v1 callbacks)
    {
        if (callbacks.struct_size < sizeof(callbacks) || callbacks.flags != 0 || callbacks.cookie == 0 ||
            callbacks.on_terminal == nullptr)
        {
            return NW_INVALID_ARGUMENT;
        }
        std::lock_guard lock(mutex_);
        if (state_.phase != OperationPhase::Created)
        {
            return NW_INVALID_STATE;
        }
        state_.callbacks = callbacks;
        state_.phase = OperationPhase::Running;
        return NW_OK;
    }

    [[nodiscard]] nw_status request_cancel()
    {
        std::lock_guard lock(mutex_);
        state_.cancel_requested = true;
        return NW_OK;
    }

    [[nodiscard]] bool cancel_requested() const
    {
        std::lock_guard lock(mutex_);
        return state_.cancel_requested;
    }

    [[nodiscard]] nw_status complete(nw_status status, std::optional<R> result, ErrorFields fields = {})
    {
        if (status == NW_PENDING)
        {
            return NW_INVALID_ARGUMENT;
        }
        nw_operation_callbacks_v1 callbacks{};
        const nw_error_info_v1 error{sizeof(nw_error_info_v1), fields.domain, fields.code, 0, fields.correlation_id};
        {
            std::lock_guard lock(mutex_);
            if (state_.phase != OperationPhase::Running || state_.terminal_status.has_value())
            {
                return NW_INVALID_STATE;
            }
            if (!state_.callbacks)
            {
                return NW_CONTRACT_VIOLATION;
            }
            callbacks = *state_.callbacks;
            if (result)
            {
                state_.result->value.emplace(std::move(*result));
            }
            state_.error = error;
            state_.terminal_status = status;
            state_.phase = OperationPhase::TerminalReady;
            ++state_.callbacks_in_flight;
        }
        const CallbackLease lease{*this};
        nw_status delivered = NW_CALLBACK_FAILED;
        try
        {
            delivered = callbacks.on_terminal(callbacks.cookie, status, &error);
        }
        catch (...)
        {
            // A native callback must not unwind into an unrelated engine frame.
            // The committed business result remains unchanged and the Host
            // observes delivery_failed to poison further source admission.
            delivered = NW_CALLBACK_FAILED;
        }
        if (delivered != NW_OK)
        {
            std::lock_guard lock(mutex_);
            state_.delivery_failed = true;
        }
        return NW_OK;
    }

    [[nodiscard]] std::pair<nw_status, std::optional<R>> take_result()
    {
        std::unique_ptr<ResultSlot> result;
        {
            std::lock_guard lock(mutex_);
            if ((state_.phase != OperationPhase::TerminalReady && state_.phase != OperationPhase::Retired) ||
                state_.taken || state_.terminal_status != NW_OK || !state_.result || !state_.result->value)
            {
                return {NW_INVALID_STATE, std::nullopt};
            }
            result = std::move(state_.result);
            state_.taken = true;
        }
        return {NW_OK, std::optional<R>(std::move(*result->value))};
    }

    [[nodiscard]] std::pair<nw_status, WorkLease<R>> reserve_source()
    {
        auto self = this->weak_from_this().lock();
        if (!self)
        {
            return {NW_INVALID_STATE, WorkLease<R>{}};
        }
        {
            std::lock_guard lock(mutex_);
            if (state_.phase != OperationPhase::Running)
            {
                return {NW_INVALID_STATE, WorkLease<R>{}};
            }
            if (state_.sources == std::numeric_limits<std::size_t>::max())
            {
                return {NW_CONTRACT_VIOLATION, WorkLease<R>{}};
            }
            ++state_.sources;
        }
        return {NW_OK, WorkLease<R>(std::move(self))};
    }

    [[nodiscard]] nw_status try_retire()
    {
        std::lock_guard lock(mutex_);
        switch (state_.phase)
        {
            case OperationPhase::Created:
                return NW_INVALID_STATE;
            case OperationPhase::Running:
                return NW_PENDING;
            case OperationPhase::Retired:
                return NW_OK;
            case OperationPhase::TerminalReady:
                if (state_.callbacks_in_flight != 0 || state_.sources != 0)
                {
                    return NW_PENDING;
                }
                state_.phase = OperationPhase::Retired;
                state_.callbacks.reset();
                return NW_OK;
        }
        return NW_CONTRACT_VIOLATION;
    }

    [[nodiscard]] bool can_release() const
    {
        std::lock_guard lock(mutex_);
        return (state_.phase == OperationPhase::Created || state_.phase == OperationPhase::Retired) &&
               state_.callbacks_in_flight == 0 && state_.sources == 0;
    }

    [[nodiscard]] OperationSnapshot snapshot() const
    {
        std::lock_guard lock(mutex_);
        return {state_.phase,   state_.terminal_status, state_.taken, state_.callbacks_in_flight,
                state_.sources, state_.delivery_failed};
    }

  private:
    mutable std::mutex mutex_;
    State state_;
    friend class WorkLease<R>;
    friend struct interop::FixtureAccess;
};

enum class SubscriptionPhase
{
    Created,
    Active,
    StopRequested,
    Retired
};
struct SubscriptionSnapshot
{
    SubscriptionPhase phase;
    std::size_t active_callbacks;
    std::size_t active_sources;
};

class Subscription : public std::enable_shared_from_this<Subscription>
{
  public:
    class SourceLease
    {
      public:
        SourceLease() noexcept = default;
        SourceLease(const SourceLease&) = delete;
        SourceLease& operator=(const SourceLease&) = delete;
        SourceLease(SourceLease&&) noexcept;
        SourceLease& operator=(SourceLease&&) noexcept;
        ~SourceLease();
        void reset() noexcept;
        [[nodiscard]] explicit operator bool() const noexcept;
        [[nodiscard]] Subscription& subscription() const noexcept;

      private:
        explicit SourceLease(std::shared_ptr<Subscription> subscription) noexcept;
        std::shared_ptr<Subscription> subscription_;
        friend class Subscription;
    };
    class CallbackLease
    {
      public:
        CallbackLease() noexcept = default;
        CallbackLease(const CallbackLease&) = delete;
        CallbackLease& operator=(const CallbackLease&) = delete;
        CallbackLease(CallbackLease&&) noexcept;
        CallbackLease& operator=(CallbackLease&&) noexcept;
        ~CallbackLease();
        [[nodiscard]] nw_status invoke(std::uint32_t kind, ByteView payload) const;

      private:
        CallbackLease(Subscription* subscription, nw_callbacks_v1 callbacks) noexcept;
        void reset() noexcept;
        Subscription* subscription_ = nullptr;
        nw_callbacks_v1 callbacks_{};
        friend class Subscription;
    };

    [[nodiscard]] nw_status start(nw_callbacks_v1 callbacks);
    [[nodiscard]] nw_status emit(std::uint32_t kind, ByteView payload);
    [[nodiscard]] std::pair<nw_status, CallbackLease> admit_callback();
    [[nodiscard]] std::pair<nw_status, SourceLease> reserve_source();
    [[nodiscard]] nw_status request_stop();
    [[nodiscard]] nw_status try_retire();
    [[nodiscard]] bool can_release() const;
    [[nodiscard]] SubscriptionSnapshot snapshot() const;

  private:
    mutable std::mutex mutex_;
    SubscriptionPhase phase_ = SubscriptionPhase::Created;
    std::optional<nw_callbacks_v1> callbacks_;
    std::size_t active_callbacks_ = 0;
    std::size_t active_sources_ = 0;
};

namespace interop
{

[[nodiscard]] nw_status write_abi(std::uint32_t requested_major, std::uint32_t capacity, nw_abi_info_v1* output,
                                  std::uint32_t minor, std::uint64_t capabilities) noexcept;
[[nodiscard]] nw_status write_error(nw_status status, nw_error_info_v1* output, ErrorFields fields = {}) noexcept;
[[nodiscard]] std::optional<std::uintptr_t> checked_extent(std::uint64_t address, std::uint64_t length,
                                                           std::uint64_t count, std::uint64_t width,
                                                           std::uint64_t offset, std::uint64_t capacity,
                                                           std::uint64_t limit, std::uint32_t alignment) noexcept;

struct Failure : std::exception
{
    nw_status status;
    ErrorFields fields;
    explicit Failure(nw_status value, ErrorFields details = {}) noexcept
        : status(value <= NW_PENDING ? NW_CONTRACT_VIOLATION : value), fields(details)
    {
    }
    [[nodiscard]] const char* what() const noexcept override
    {
        return "native interop failure";
    }
};

// Engine-local ABI exception containment. Poison closes ordinary admission;
// trusted cleanup stays callable and never pretends to prove retirement.
class EngineBoundary
{
  public:
    [[nodiscard]] bool is_poisoned() const noexcept;
    [[nodiscard]] std::uint64_t active_calls() const noexcept;
    [[nodiscard]] std::uint64_t quarantined_payloads() const noexcept;
    void poison() noexcept;

    template <class F> [[nodiscard]] nw_status business(F&& body, ErrorFields* fields = nullptr) noexcept
    {
        return execute(false, std::forward<F>(body), fields);
    }
    template <class F> [[nodiscard]] nw_status cleanup(F&& body, ErrorFields* fields = nullptr) noexcept
    {
        return execute(true, std::forward<F>(body), fields);
    }

  private:
    static constexpr std::uint64_t POISONED = std::uint64_t{1} << 63U;
    static constexpr std::uint64_t ACTIVE_MASK = POISONED - 1;
    std::atomic<std::uint64_t> state_{0};
    std::atomic<std::uint64_t> quarantine_{0};
    [[nodiscard]] bool admit(bool cleanup) noexcept;
    void quarantine_exception() noexcept;
    [[nodiscard]] nw_status classify(nw_status status) noexcept;

    template <class F> nw_status execute(bool trusted_cleanup, F&& body, ErrorFields* fields) noexcept
    {
        if (!admit(trusted_cleanup))
        {
            return NW_ENGINE_POISONED;
        }
        struct Admission
        {
            EngineBoundary& boundary;
            ~Admission()
            {
                boundary.state_.fetch_sub(1, std::memory_order_release);
            }
        } admission{*this};
        try
        {
            return classify(std::forward<F>(body)());
        }
        catch (const Failure& failure)
        {
            if (fields != nullptr)
            {
                *fields = failure.fields;
            }
            return classify(failure.status <= NW_PENDING ? NW_CONTRACT_VIOLATION : failure.status);
        }
        catch (const Error& error)
        {
            if (fields != nullptr)
            {
                *fields = error.fields();
            }
            return classify(error.status <= NW_PENDING ? NW_CONTRACT_VIOLATION : error.status);
        }
        catch (const std::bad_alloc&)
        {
            return NW_OUT_OF_MEMORY;
        }
        catch (...)
        {
            poison();
            quarantine_exception();
            return NW_NATIVE_PANIC;
        }
    }
};

template <class T, class F>
[[nodiscard]] nw_status create_owner(EngineBoundary& boundary, T** output, F&& factory) noexcept
{
    if (output == nullptr || reinterpret_cast<std::uintptr_t>(output) % alignof(T*) != 0)
    {
        return NW_INVALID_ARGUMENT;
    }
    *output = nullptr;
    return boundary.business(
        [&]
        {
            auto owner = std::forward<F>(factory)();
            if (!owner)
            {
                throw Failure(NW_CONTRACT_VIOLATION);
            }
            *output = owner.release();
            return NW_OK;
        });
}

class BoundedDetail
{
  public:
    static constexpr std::size_t MAX_BYTES = 4096;
    [[nodiscard]] static BoundedDetail from_redacted(std::string_view text, std::size_t limit) noexcept;
    [[nodiscard]] ByteView bytes() const noexcept
    {
        return {bytes_.data(), length_};
    }
    [[nodiscard]] bool is_truncated() const noexcept
    {
        return truncated_;
    }

  private:
    std::array<std::uint8_t, MAX_BYTES> bytes_{};
    std::size_t length_ = 0;
    bool truncated_ = false;
};

} // namespace interop
} // namespace nw

#endif
