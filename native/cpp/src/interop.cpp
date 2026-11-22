#include "nw/interop.hpp"

#include <algorithm>
#include <bit>
#include <cstdlib>
#include <cstring>

namespace nw
{

Subscription::SourceLease::SourceLease(std::shared_ptr<Subscription> subscription) noexcept
    : subscription_(std::move(subscription))
{
}
Subscription::SourceLease::SourceLease(SourceLease&& other) noexcept : subscription_(std::move(other.subscription_)) {}
Subscription::SourceLease& Subscription::SourceLease::operator=(SourceLease&& other) noexcept
{
    if (this != &other)
    {
        reset();
        subscription_ = std::move(other.subscription_);
    }
    return *this;
}
Subscription::SourceLease::~SourceLease()
{
    reset();
}
void Subscription::SourceLease::reset() noexcept
{
    auto subscription = std::move(subscription_);
    if (subscription)
    {
        std::lock_guard lock(subscription->mutex_);
        --subscription->active_sources_;
    }
}
Subscription::SourceLease::operator bool() const noexcept
{
    return bool(subscription_);
}
Subscription& Subscription::SourceLease::subscription() const noexcept
{
    return *subscription_;
}

Subscription::CallbackLease::CallbackLease(Subscription* subscription, nw_callbacks_v1 callbacks) noexcept
    : subscription_(subscription), callbacks_(callbacks)
{
}
Subscription::CallbackLease::CallbackLease(CallbackLease&& other) noexcept
    : subscription_(std::exchange(other.subscription_, nullptr)), callbacks_(other.callbacks_)
{
}
Subscription::CallbackLease& Subscription::CallbackLease::operator=(CallbackLease&& other) noexcept
{
    if (this != &other)
    {
        reset();
        subscription_ = std::exchange(other.subscription_, nullptr);
        callbacks_ = other.callbacks_;
    }
    return *this;
}
Subscription::CallbackLease::~CallbackLease()
{
    reset();
}
void Subscription::CallbackLease::reset() noexcept
{
    if (auto* subscription = std::exchange(subscription_, nullptr))
    {
        std::lock_guard lock(subscription->mutex_);
        --subscription->active_callbacks_;
    }
}
nw_status Subscription::CallbackLease::invoke(std::uint32_t kind, ByteView payload) const
{
    if (subscription_ == nullptr || callbacks_.on_event == nullptr)
    {
        return NW_CONTRACT_VIOLATION;
    }
    nw_status status = NW_CALLBACK_FAILED;
    try
    {
        status = callbacks_.on_event(callbacks_.cookie, kind, payload.data(), payload.size());
    }
    catch (...)
    {
        // Admission remains closed on native callback failure as on a returned
        // managed bridge error; no exception escapes the callback ABI.
        status = NW_CALLBACK_FAILED;
    }
    if (status != NW_OK)
    {
        static_cast<void>(subscription_->request_stop());
    }
    return status;
}
nw_status Subscription::start(nw_callbacks_v1 callbacks)
{
    if (callbacks.struct_size < sizeof(callbacks) || callbacks.flags != 0 || callbacks.cookie == 0 ||
        callbacks.on_event == nullptr)
    {
        return NW_INVALID_ARGUMENT;
    }
    std::lock_guard lock(mutex_);
    if (phase_ != SubscriptionPhase::Created)
    {
        return NW_INVALID_STATE;
    }
    callbacks_ = callbacks;
    phase_ = SubscriptionPhase::Active;
    return NW_OK;
}
nw_status Subscription::emit(std::uint32_t kind, ByteView payload)
{
    auto [status, lease] = admit_callback();
    return status == NW_OK ? lease.invoke(kind, payload) : status;
}
std::pair<nw_status, Subscription::CallbackLease> Subscription::admit_callback()
{
    std::lock_guard lock(mutex_);
    if (phase_ != SubscriptionPhase::Active)
    {
        return {NW_INVALID_STATE, CallbackLease{}};
    }
    if (!callbacks_ || active_callbacks_ == std::numeric_limits<std::size_t>::max())
    {
        return {NW_CONTRACT_VIOLATION, CallbackLease{}};
    }
    ++active_callbacks_;
    return {NW_OK, CallbackLease(this, *callbacks_)};
}
std::pair<nw_status, Subscription::SourceLease> Subscription::reserve_source()
{
    auto self = weak_from_this().lock();
    if (!self)
    {
        return {NW_INVALID_STATE, SourceLease{}};
    }
    {
        std::lock_guard lock(mutex_);
        if (phase_ != SubscriptionPhase::Active)
        {
            return {NW_INVALID_STATE, SourceLease{}};
        }
        if (active_sources_ == std::numeric_limits<std::size_t>::max())
        {
            return {NW_CONTRACT_VIOLATION, SourceLease{}};
        }
        ++active_sources_;
    }
    return {NW_OK, SourceLease(std::move(self))};
}
nw_status Subscription::request_stop()
{
    std::lock_guard lock(mutex_);
    if (phase_ == SubscriptionPhase::Active)
    {
        phase_ = SubscriptionPhase::StopRequested;
    }
    return NW_OK;
}
nw_status Subscription::try_retire()
{
    std::lock_guard lock(mutex_);
    switch (phase_)
    {
        case SubscriptionPhase::Created:
            return NW_INVALID_STATE;
        case SubscriptionPhase::Active:
            return NW_PENDING;
        case SubscriptionPhase::Retired:
            return NW_OK;
        case SubscriptionPhase::StopRequested:
            if (active_callbacks_ != 0 || active_sources_ != 0)
            {
                return NW_PENDING;
            }
            phase_ = SubscriptionPhase::Retired;
            callbacks_.reset();
            return NW_OK;
    }
    return NW_CONTRACT_VIOLATION;
}
bool Subscription::can_release() const
{
    std::lock_guard lock(mutex_);
    return (phase_ == SubscriptionPhase::Created || phase_ == SubscriptionPhase::Retired) && active_callbacks_ == 0 &&
           active_sources_ == 0;
}
SubscriptionSnapshot Subscription::snapshot() const
{
    std::lock_guard lock(mutex_);
    return {phase_, active_callbacks_, active_sources_};
}

namespace interop
{

nw_status write_abi(std::uint32_t requested_major, std::uint32_t capacity, nw_abi_info_v1* output, std::uint32_t minor,
                    std::uint64_t capabilities) noexcept
{
    if (output == nullptr)
    {
        return NW_INVALID_ARGUMENT;
    }
    if (requested_major != 1)
    {
        return NW_NOT_SUPPORTED;
    }
    if (capacity < sizeof(nw_abi_info_v1))
    {
        return NW_BUFFER_TOO_SMALL;
    }
    if (reinterpret_cast<std::uintptr_t>(output) % alignof(nw_abi_info_v1) != 0)
    {
        return NW_INVALID_ARGUMENT;
    }
    const nw_abi_info_v1 value{sizeof(nw_abi_info_v1), 1, minor, sizeof(void*) * 8, capabilities, 0};
    std::memcpy(output, &value, sizeof(value));
    return NW_OK;
}

nw_status write_error(nw_status status, nw_error_info_v1* output, ErrorFields fields) noexcept
{
    if (status == NW_OK || status == NW_PENDING || output == nullptr ||
        reinterpret_cast<std::uintptr_t>(output) % alignof(nw_error_info_v1) != 0)
    {
        return status;
    }
    std::uint32_t capacity = 0;
    std::memcpy(&capacity, output, sizeof(capacity));
    if (capacity < sizeof(nw_error_info_v1))
    {
        return status;
    }
    const nw_error_info_v1 value{sizeof(nw_error_info_v1), fields.domain, fields.code, 0, fields.correlation_id};
    std::memcpy(output, &value, sizeof(value));
    return status;
}

std::optional<std::uintptr_t> checked_extent(std::uint64_t address, std::uint64_t length, std::uint64_t count,
                                             std::uint64_t width, std::uint64_t offset, std::uint64_t capacity,
                                             std::uint64_t limit, std::uint32_t alignment) noexcept
{
    constexpr auto MAX = std::numeric_limits<std::uint64_t>::max();
    constexpr auto POINTER_MAX = std::numeric_limits<std::uintptr_t>::max();
    if (width == 0 || !std::has_single_bit(alignment) ||
        length > static_cast<std::uint64_t>(std::numeric_limits<std::ptrdiff_t>::max()) || length > limit ||
        address > POINTER_MAX || capacity > POINTER_MAX || count > MAX / width || count * width != length ||
        offset > MAX - length || offset + length > capacity || address > MAX - offset)
    {
        return std::nullopt;
    }
    const auto start = address + offset;
    if (start > MAX - length || start + length > POINTER_MAX || start % alignment != 0 ||
        (address == 0 && (length != 0 || offset != 0)))
    {
        return std::nullopt;
    }
    return static_cast<std::uintptr_t>(start);
}

bool EngineBoundary::is_poisoned() const noexcept
{
    return (state_.load(std::memory_order_acquire) & POISONED) != 0;
}
std::uint64_t EngineBoundary::active_calls() const noexcept
{
    return state_.load(std::memory_order_acquire) & ACTIVE_MASK;
}
std::uint64_t EngineBoundary::quarantined_payloads() const noexcept
{
    return quarantine_.load(std::memory_order_acquire);
}
void EngineBoundary::poison() noexcept
{
    state_.fetch_or(POISONED, std::memory_order_acq_rel);
}
bool EngineBoundary::admit(bool cleanup) noexcept
{
    auto current = state_.load(std::memory_order_acquire);
    for (;;)
    {
        if ((!cleanup && (current & POISONED) != 0) || (current & ACTIVE_MASK) == ACTIVE_MASK)
        {
            return false;
        }
        if (state_.compare_exchange_weak(current, current + 1, std::memory_order_acq_rel, std::memory_order_acquire))
        {
            return true;
        }
    }
}
nw_status EngineBoundary::classify(nw_status status) noexcept
{
    if (status == NW_CONTRACT_VIOLATION || status == NW_NATIVE_PANIC || status == NW_ENGINE_POISONED)
    {
        poison();
    }
    return status;
}

namespace
{
struct QuarantinedException
{
    std::exception_ptr payload;
    QuarantinedException* next;
};
// Unknown exception destructors are not trusted cleanup. Their payloads remain
// process-lifetime reachable, as with the original Rust panic quarantine. Counts
// are exact, reachable byte sizes are unknown, and no unload/quiet claim follows.
std::atomic<QuarantinedException*> quarantine_head{nullptr};
} // namespace

void EngineBoundary::quarantine_exception() noexcept
{
    auto* node = new (std::nothrow) QuarantinedException{std::current_exception(), nullptr};
    if (node == nullptr)
    {
        std::abort();
    }
    node->next = quarantine_head.load(std::memory_order_relaxed);
    while (
        !quarantine_head.compare_exchange_weak(node->next, node, std::memory_order_release, std::memory_order_relaxed))
    {
    }
    quarantine_.fetch_add(1, std::memory_order_acq_rel);
}

BoundedDetail BoundedDetail::from_redacted(std::string_view text, std::size_t limit) noexcept
{
    BoundedDetail result;
    result.length_ = std::min({text.size(), limit, MAX_BYTES});
    while (result.length_ != 0 && result.length_ < text.size() &&
           (static_cast<unsigned char>(text[result.length_]) & 0xc0U) == 0x80U)
    {
        --result.length_;
    }
    result.truncated_ = result.length_ < text.size();
    if (result.length_ != 0)
    {
        std::memcpy(result.bytes_.data(), text.data(), result.length_);
    }
    return result;
}

} // namespace interop
} // namespace nw
