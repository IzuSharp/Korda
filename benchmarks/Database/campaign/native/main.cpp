#include "query_adapter.hpp"

#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <cerrno>
#include <charconv>
#include <cmath>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <ctime>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <latch>
#include <limits>
#include <malloc.h>
#include <mutex>
#include <optional>
#include <set>
#include <sstream>
#include <string_view>
#include <thread>
#include <utility>

#include <dlfcn.h>
#include <fcntl.h>
#include <libpq-fe.h>
#include <nlohmann/json.hpp>
#include <openssl/evp.h>
#include <sqlite3.h>
#include <sys/resource.h>
#include <unistd.h>

namespace
{
using Json = nlohmann::json;
using Clock = std::chrono::steady_clock;
using Time = Clock::time_point;
constexpr std::size_t histogram_bins = 8192;
constexpr auto drain_budget = std::chrono::seconds(30);
constexpr auto command_timeout = std::chrono::seconds(5);
constexpr std::array<std::string_view, 7> error_names{"connection", "database", "validation", "timeout",
                                                      "cancel",     "resource", "unknown"};

std::int64_t nanoseconds(Time value)
{
    return std::chrono::duration_cast<std::chrono::nanoseconds>(value.time_since_epoch()).count();
}

std::uint64_t duration_ns(Time end, Time start)
{
    return static_cast<std::uint64_t>(
        std::max<std::int64_t>(1, std::chrono::duration_cast<std::chrono::nanoseconds>(end - start).count()));
}

void wait_until(Time target, const std::atomic<bool>* stop = nullptr)
{
    for (;;)
    {
        if (stop && stop->load(std::memory_order_acquire))
            return;
        timespec current{};
        if (::clock_gettime(CLOCK_MONOTONIC, &current) != 0)
            throw std::runtime_error("clock_gettime failed");
        const auto remaining = std::chrono::duration_cast<std::chrono::nanoseconds>(target - Clock::now());
        if (remaining.count() <= 0)
            return;
        const auto duration =
            stop ? std::min(remaining, std::chrono::nanoseconds(std::chrono::milliseconds(50))) : remaining;
        const auto nanoseconds_part = current.tv_nsec + duration.count() % 1000000000;
        timespec absolute{
            static_cast<time_t>(current.tv_sec + duration.count() / 1000000000 + nanoseconds_part / 1000000000),
            static_cast<long>(nanoseconds_part % 1000000000)};
        int status;
        do
            status = ::clock_nanosleep(CLOCK_MONOTONIC, TIMER_ABSTIME, &absolute, nullptr);
        while (status == EINTR);
        if (status != 0)
            throw std::runtime_error("clock_nanosleep failed");
    }
}

void phase(std::string_view name)
{
    // These bounded phase writes are the only stdout activity before final output.
    // They create no metric objects in the measurement interval.
    constexpr std::string_view prefix = "DB_PERF_PHASE ";
    const std::array<std::string_view, 3> parts{prefix, name, "\n"};
    for (auto part : parts)
        while (!part.empty())
        {
            const auto written = ::write(STDOUT_FILENO, part.data(), part.size());
            if (written < 0 && errno == EINTR)
                continue;
            if (written <= 0)
                throw std::runtime_error("Phase output failed");
            part.remove_prefix(static_cast<std::size_t>(written));
        }
}

std::string sha256(const std::filesystem::path& path)
{
    std::ifstream input(path, std::ios::binary);
    if (!input)
        throw std::runtime_error("Cannot hash " + path.string());
    const auto context = std::unique_ptr<EVP_MD_CTX, decltype(&EVP_MD_CTX_free)>(EVP_MD_CTX_new(), EVP_MD_CTX_free);
    if (!context || EVP_DigestInit_ex(context.get(), EVP_sha256(), nullptr) != 1)
        throw std::runtime_error("SHA256 initialization failed");
    std::array<char, 65536> buffer{};
    while (input)
    {
        input.read(buffer.data(), buffer.size());
        if (input.gcount() &&
            EVP_DigestUpdate(context.get(), buffer.data(), static_cast<std::size_t>(input.gcount())) != 1)
            throw std::runtime_error("SHA256 update failed");
    }
    if (!input.eof())
        throw std::runtime_error("SHA256 input read failed");
    std::array<unsigned char, EVP_MAX_MD_SIZE> bytes{};
    unsigned int length = 0;
    if (EVP_DigestFinal_ex(context.get(), bytes.data(), &length) != 1 || length != 32)
        throw std::runtime_error("SHA256 finalization failed");
    std::ostringstream output;
    output << std::hex << std::setfill('0');
    for (unsigned int i = 0; i < length; ++i)
        output << std::setw(2) << static_cast<unsigned int>(bytes[i]);
    return output.str();
}

std::string utc(std::chrono::system_clock::time_point point)
{
    const auto time = std::chrono::system_clock::to_time_t(point);
    std::tm calendar{};
    gmtime_r(&time, &calendar);
    std::array<char, 40> buffer{};
    std::strftime(buffer.data(), buffer.size(), "%Y-%m-%dT%H:%M:%SZ", &calendar);
    return buffer.data();
}

std::optional<std::uint64_t> proc_bytes(const char* path, std::string_view key)
{
    const int file = ::open(path, O_RDONLY | O_CLOEXEC);
    if (file < 0)
        return {};
    std::array<char, 16384> buffer{};
    ssize_t size;
    do
        size = ::read(file, buffer.data(), buffer.size());
    while (size < 0 && errno == EINTR);
    ::close(file);
    if (size <= 0)
        return {};
    const std::string_view text(buffer.data(), static_cast<std::size_t>(size));
    std::size_t offset = 0;
    while (offset < text.size())
    {
        const auto end = text.find('\n', offset);
        auto line = text.substr(offset, end == std::string_view::npos ? text.size() - offset : end - offset);
        if (line.starts_with(key))
        {
            line.remove_prefix(key.size());
            while (!line.empty() && (line.front() == ' ' || line.front() == '\t'))
                line.remove_prefix(1);
            std::uint64_t value = 0;
            const auto result = std::from_chars(line.data(), line.data() + line.size(), value);
            if (result.ec == std::errc{} && value <= std::numeric_limits<std::uint64_t>::max() / 1024)
                return value * 1024;
            return {};
        }
        if (end == std::string_view::npos)
            break;
        offset = end + 1;
    }
    return {};
}

struct ProcessReading
{
    std::int64_t observed_ns = 0;
    std::uint64_t user_us = 0, system_us = 0, peak_rss = 0, glibc_used = 0, glibc_reserved = 0;
    std::optional<std::uint64_t> rss, pss;

    static ProcessReading read()
    {
        rusage usage{};
        if (getrusage(RUSAGE_SELF, &usage) != 0)
            throw std::runtime_error("getrusage failed");
        const auto heap = mallinfo2();
        return {nanoseconds(Clock::now()),
                static_cast<std::uint64_t>(usage.ru_utime.tv_sec) * 1000000 + usage.ru_utime.tv_usec,
                static_cast<std::uint64_t>(usage.ru_stime.tv_sec) * 1000000 + usage.ru_stime.tv_usec,
                static_cast<std::uint64_t>(usage.ru_maxrss) * 1024,
                static_cast<std::uint64_t>(heap.uordblks + heap.hblkhd),
                static_cast<std::uint64_t>(heap.arena + heap.hblkhd),
                proc_bytes("/proc/self/status", "VmRSS:"),
                proc_bytes("/proc/self/smaps_rollup", "Pss:")};
    }

    Json json() const
    {
        return {{"observed_monotonic_ns", observed_ns},
                {"user_seconds", user_us / 1e6},
                {"system_seconds", system_us / 1e6},
                {"rss_bytes", rss ? Json(*rss) : Json(nullptr)},
                {"pss_bytes", pss ? Json(*pss) : Json(nullptr)},
                {"peak_rss_bytes", peak_rss},
                {"glibc_allocated_block_bytes", glibc_used},
                {"glibc_reserved_block_bytes", glibc_reserved}};
    }
};

Json cpu_delta(const ProcessReading& begin, const ProcessReading& end, std::uint64_t successful_operations = 0)
{
    return {{"user_seconds", (end.user_us - begin.user_us) / 1e6},
            {"system_seconds", (end.system_us - begin.system_us) / 1e6},
            {"cpu_seconds_per_success", successful_operations
                                            ? Json(((end.user_us - begin.user_us) + (end.system_us - begin.system_us)) /
                                                   1e6 / successful_operations)
                                            : Json(nullptr)}};
}

struct Options
{
    std::string provider, connection, manifest, output;
    std::string mode = "prepared", session_mode = "persistent", selected = "mixed";
    std::set<std::string> excluded;
    int concurrency = 1, warmup = 64;
    double seconds = 10, rate = 0;
    std::optional<double> recovery_rate, recovery_after;
    std::uint64_t operations = 0;
    bool empty_driver = false, verify_only = false;
};

Options parse_options(int argc, char** argv)
{
    Options options;
    std::set<std::string> seen;
    for (int i = 1; i < argc; ++i)
    {
        const std::string name = argv[i];
        if (!seen.insert(name).second)
            throw std::invalid_argument("Duplicate option: " + name);
        auto value = [&]() -> std::string
        {
            if (++i >= argc)
                throw std::invalid_argument("Missing value for " + name);
            return argv[i];
        };
        if (name == "--provider")
            options.provider = value();
        else if (name == "--manifest")
            options.manifest = value();
        else if (name == "--connection")
            options.connection = value();
        else if (name == "--output")
            options.output = value();
        else if (name == "--mode")
            options.mode = value();
        else if (name == "--session-mode")
            options.session_mode = value();
        else if (name == "--concurrency")
            options.concurrency = std::stoi(value());
        else if (name == "--seconds")
            options.seconds = std::stod(value());
        else if (name == "--rate")
            options.rate = std::stod(value());
        else if (name == "--recovery-rate")
            options.recovery_rate = std::stod(value());
        else if (name == "--recovery-after")
            options.recovery_after = std::stod(value());
        else if (name == "--operations")
        {
            const auto text = value();
            const auto parsed = std::from_chars(text.data(), text.data() + text.size(), options.operations);
            if (parsed.ec != std::errc{} || parsed.ptr != text.data() + text.size())
                throw std::invalid_argument("Invalid --operations");
        }
        else if (name == "--warmup")
            options.warmup = std::stoi(value());
        else if (name == "--case")
            options.selected = value();
        else if (name == "--exclude")
        {
            std::istringstream input(value());
            std::string item;
            while (std::getline(input, item, ','))
                if (!item.empty())
                    options.excluded.insert(item);
        }
        else if (name == "--empty-driver")
            options.empty_driver = true;
        else if (name == "--verify-only")
            options.verify_only = true;
        else
            throw std::invalid_argument("Unknown option: " + name);
    }
    if (options.provider != "sqlite" && options.provider != "postgresql")
        throw std::invalid_argument("--provider must be sqlite or postgresql");
    if (options.connection.empty() || options.manifest.empty() || options.output.empty())
        throw std::invalid_argument("--connection, --manifest and --output are required");
    if (options.mode != "dynamic" && options.mode != "prepared")
        throw std::invalid_argument("--mode must be dynamic or prepared");
    if (options.session_mode != "persistent" && options.session_mode != "per-operation")
        throw std::invalid_argument("--session-mode must be persistent or per-operation");
    if (options.concurrency < 1 || options.concurrency > 128 || options.warmup < 0 || options.warmup > 1000000 ||
        !std::isfinite(options.seconds) || options.seconds < 0 || options.seconds > 7200 ||
        !std::isfinite(options.rate) || options.rate < 0 || options.rate > 1000000 ||
        options.operations > 1000000000ULL || (!options.verify_only && options.operations == 0 && options.seconds <= 0))
        throw std::invalid_argument("Invalid or unbounded campaign limits");
    if (options.rate > 0 && (options.operations > 0 || options.seconds <= 0))
        throw std::invalid_argument("Fixed rate requires positive seconds and no --operations override");
    if (options.recovery_rate.has_value() != options.recovery_after.has_value())
        throw std::invalid_argument("--recovery-rate and --recovery-after must be provided together");
    if (options.recovery_rate)
    {
        const auto rate = *options.recovery_rate, after = *options.recovery_after;
        if (options.rate <= 0 || options.operations != 0 || !std::isfinite(rate) || rate <= 0 || rate > 1000000 ||
            !std::isfinite(after) || after <= 0 || after >= options.seconds)
            throw std::invalid_argument(
                "Recovery requires fixed arrival rates, no operation override, and 0 < T < seconds");
        const auto first = std::floor(options.rate * after);
        const auto second = std::floor(rate * (options.seconds - after));
        if (first < 1 || second < 1 || first + second > 1e9)
            throw std::invalid_argument("Each arrival phase needs at least one bounded planned operation");
    }
    else if (options.rate > 0 &&
             (std::floor(options.rate * options.seconds) < 1 || std::floor(options.rate * options.seconds) > 1e9))
        throw std::invalid_argument("Fixed-rate schedule requires 1..1,000,000,000 planned operations");
    if (std::filesystem::exists(options.output))
        throw std::invalid_argument("Output already exists; use a new evidence path");
    return options;
}

struct Histogram
{
    std::array<std::uint64_t, histogram_bins> bins{};
    std::uint64_t count = 0;
    void record(std::uint64_t ns)
    {
        ns = std::max<std::uint64_t>(1, ns);
        const auto exponent = static_cast<unsigned int>(std::bit_width(ns) - 1);
        const auto lower = std::uint64_t{1} << exponent;
        const auto fraction = static_cast<std::uint64_t>((static_cast<unsigned __int128>(ns - lower) * 128) / lower);
        ++bins[exponent * 128 + fraction];
        ++count;
    }
    void add(const Histogram& other)
    {
        for (std::size_t i = 0; i < bins.size(); ++i)
            bins[i] += other.bins[i];
        count += other.count;
    }
    Json quantile(double q) const
    {
        if (count == 0)
            return nullptr;
        const auto desired = static_cast<std::uint64_t>(std::ceil(count * q));
        std::uint64_t total = 0;
        for (std::size_t i = 0; i < bins.size(); ++i)
        {
            total += bins[i];
            if (total >= desired)
            {
                const auto lower = std::ldexp(1., static_cast<int>(i / 128));
                return (lower + lower * static_cast<double>(i % 128 + 1) / 128.) / 1000.;
            }
        }
        throw std::runtime_error("Histogram lost a successful sample");
    }
    Json json() const
    {
        return {{"samples", count},
                {"histogram_ns", bins},
                {"p50_us_upper", quantile(.50)},
                {"p95_us_upper", quantile(.95)},
                {"p99_us_upper", quantile(.99)},
                {"method", "base-2 nanoseconds; 128 sub-bins per exponent; quantile upper bounds"}};
    }
};

struct Counts
{
    std::uint64_t started = 0, completed = 0, failed = 0, late = 0, total_rows = 0;
    std::uint64_t started_after_cutoff = 0, completed_after_cutoff = 0, failed_after_cutoff = 0;
    std::uint64_t completed_in_window = 0, failed_in_window = 0, rows_in_window = 0;
    std::uint64_t unfinished = 0, late_terminal = 0, max_schedule_lag_ns = 0;

    void add(const Counts& other)
    {
        started += other.started;
        completed += other.completed;
        failed += other.failed;
        late += other.late;
        total_rows += other.total_rows;
        started_after_cutoff += other.started_after_cutoff;
        completed_after_cutoff += other.completed_after_cutoff;
        failed_after_cutoff += other.failed_after_cutoff;
        completed_in_window += other.completed_in_window;
        failed_in_window += other.failed_in_window;
        rows_in_window += other.rows_in_window;
        unfinished += other.unfinished;
        late_terminal += other.late_terminal;
        max_schedule_lag_ns = std::max(max_schedule_lag_ns, other.max_schedule_lag_ns);
    }

    Json json(std::uint64_t planned, bool fixed_rate) const
    {
        const auto before_cutoff = started - started_after_cutoff;
        const auto queued = fixed_rate ? planned - before_cutoff : 0;
        const auto in_flight = before_cutoff - completed_in_window - failed_in_window;
        return {{"planned", planned},
                {"started", started},
                {"completed", completed},
                {"failed", failed},
                {"retries", 0},
                {"late", late},
                {"max_schedule_lag_ns", max_schedule_lag_ns},
                {"queued_at_cutoff", queued},
                {"in_flight_at_cutoff", in_flight},
                {"started_after_cutoff", started_after_cutoff},
                {"completed_after_cutoff", completed_after_cutoff},
                {"failed_after_cutoff", failed_after_cutoff},
                {"not_started", planned - started},
                {"unfinished", unfinished},
                {"unfinished_at_drain", unfinished},
                {"late_terminal", late_terminal},
                {"completed_in_window", completed_in_window},
                {"failed_in_window", failed_in_window},
                {"rows_in_window", rows_in_window},
                {"total_rows", total_rows}};
    }
};

struct Second
{
    std::uint64_t planned = 0, started = 0, completed = 0, failed = 0, rows = 0, late = 0, late_terminal = 0;
    void add(const Second& other)
    {
        planned += other.planned;
        started += other.started;
        completed += other.completed;
        failed += other.failed;
        rows += other.rows;
        late += other.late;
        late_terminal += other.late_terminal;
    }
};

struct ErrorObservation
{
    std::uint64_t count = 0;
    std::array<char, 2048> message{};
    std::array<char, 80> code{};
    std::array<char, 80> source_category{};
    void record(const char* text, const char* native_code, const char* original_category = "unknown")
    {
        if (count++ != 0)
            return;
        std::memcpy(message.data(), text, std::min(message.size() - 1, std::strlen(text)));
        std::memcpy(code.data(), native_code, std::min(code.size() - 1, std::strlen(native_code)));
        std::memcpy(source_category.data(), original_category,
                    std::min(source_category.size() - 1, std::strlen(original_category)));
    }
};

std::size_t category(const db_perf::DatabaseError& error)
{
    if (error.category == "connection")
        return 0;
    if (error.category == "provider" || error.category == "busy")
        return error.native_code.starts_with("08") ? 0 : 1;
    if (error.category == "contract" || error.category == "materialization" || error.category == "validation" ||
        error.category == "configuration")
        return 2;
    if (error.category == "timeout" || error.category == "timeout-or-canceled")
        return 3;
    if (error.category == "cancel")
        return 4;
    if (error.category == "resource")
        return 5;
    return 6;
}

struct Validation
{
    std::uint64_t attempts = 0, passed = 0;
    std::optional<db_perf::OperationResult> actual;
    std::string first_error;
};

class ConnectionPool
{
    std::vector<std::unique_ptr<db_perf::QueryAdapter>>& adapters_;
    std::vector<std::size_t> available_;
    std::mutex mutex_;
    std::condition_variable changed_;
    std::size_t head_ = 0, count_, leased_ = 0, peak_leased_ = 0;
    std::uint64_t checkouts_ = 0, wait_timeouts_ = 0;

    void release(std::size_t index) noexcept
    {
        {
            std::lock_guard lock(mutex_);
            available_[(head_ + count_) % available_.size()] = index;
            ++count_;
            --leased_;
        }
        changed_.notify_one();
    }

  public:
    struct Lease
    {
        ConnectionPool* pool;
        std::size_t index;
        Lease(ConnectionPool* pool, std::size_t index) : pool(pool), index(index) {}
        Lease(const Lease&) = delete;
        Lease& operator=(const Lease&) = delete;
        Lease(Lease&& other) noexcept : pool(std::exchange(other.pool, nullptr)), index(other.index) {}
        ~Lease()
        {
            if (pool)
                pool->release(index);
        }
        db_perf::QueryAdapter& adapter() const
        {
            return *pool->adapters_[index];
        }
    };
    explicit ConnectionPool(std::vector<std::unique_ptr<db_perf::QueryAdapter>>& adapters)
        : adapters_(adapters), available_(adapters.size()), count_(adapters.size())
    {
        for (std::size_t i = 0; i < available_.size(); ++i)
            available_[i] = i;
    }
    Lease acquire(Time deadline)
    {
        std::unique_lock lock(mutex_);
        if (!changed_.wait_until(lock, std::min(deadline, Clock::now() + command_timeout), [&] { return count_ != 0; }))
        {
            ++wait_timeouts_;
            throw db_perf::DatabaseError("resource", "Bounded connection-pool wait timed out");
        }
        const auto index = available_[head_];
        head_ = (head_ + 1) % available_.size();
        --count_;
        ++leased_;
        ++checkouts_;
        peak_leased_ = std::max(peak_leased_, leased_);
        return Lease(this, index);
    }
    Json json() const
    {
        return {{"capacity", available_.size()},
                {"leased_after_drain", leased_},
                {"peak_leased", peak_leased_},
                {"checkouts_including_warmup", checkouts_},
                {"wait_timeouts_including_warmup", wait_timeouts_}};
    }
};

struct Shared
{
    const Options& options;
    const db_perf::Contract& contract;
    const std::vector<std::size_t>& mix;
    std::vector<std::unique_ptr<db_perf::QueryAdapter>>& adapters;
    ConnectionPool& pool;
    std::uint64_t planned = 0;
    std::uint64_t initial_phase_planned = 0;
    Time start{}, cutoff{}, drain_cutoff{};
    bool begin = false, exit = false;
    std::atomic<bool> abort{false};
    std::mutex gate;
    std::condition_variable start_changed, done_changed;
    std::atomic<int> done{0};
    std::atomic<std::uint64_t> next_sequence{0};

    Shared(const Options& options, const db_perf::Contract& contract, const std::vector<std::size_t>& mix,
           std::vector<std::unique_ptr<db_perf::QueryAdapter>>& adapters, ConnectionPool& pool)
        : options(options), contract(contract), mix(mix), adapters(adapters), pool(pool)
    {
    }

    std::size_t arrival_phase(std::uint64_t sequence) const
    {
        return options.recovery_rate && sequence >= initial_phase_planned ? 1 : 0;
    }

    Time planned_at(std::uint64_t sequence) const
    {
        if (arrival_phase(sequence) == 1)
            return start +
                   std::chrono::duration_cast<Clock::duration>(std::chrono::duration<double>(*options.recovery_after)) +
                   std::chrono::duration_cast<Clock::duration>(
                       std::chrono::duration<double>((sequence - initial_phase_planned) / *options.recovery_rate));
        return start +
               std::chrono::duration_cast<Clock::duration>(std::chrono::duration<double>(sequence / options.rate));
    }

    Time arrival_phase_cutoff(std::size_t phase_index) const
    {
        return phase_index == 0 && options.recovery_rate
                   ? start + std::chrono::duration_cast<Clock::duration>(
                                 std::chrono::duration<double>(*options.recovery_after))
                   : cutoff;
    }

    db_perf::OperationResult execute(int worker, std::size_t index, Time limit, bool allow_empty)
    {
        if (allow_empty && options.empty_driver)
            return {contract.cases[index].expected_rows, contract.cases[index].expected_digest,
                    contract.cases[index].expected_outcome};
        if (options.session_mode == "persistent")
            return adapters[worker]->execute_until(index, limit, worker + 1);
        auto lease = pool.acquire(limit);
        return lease.adapter().execute_until(index, limit, worker + 1);
    }
};

struct ArrivalPhaseMetrics
{
    Counts counts;
    Histogram service, offered, service_total, offered_total;

    void add(const ArrivalPhaseMetrics& other)
    {
        counts.add(other.counts);
        service.add(other.service);
        offered.add(other.offered);
        service_total.add(other.service_total);
        offered_total.add(other.offered_total);
    }
};

struct Worker
{
    Counts counts;
    Histogram service, offered, service_total, offered_total;
    std::vector<Counts> cases;
    std::vector<Second> seconds;
    std::vector<ArrivalPhaseMetrics> arrival_phases;
    std::array<ErrorObservation, error_names.size()> errors{};
    Time finished{}, last_terminal{};
    bool series_truncated = false;

    Worker(std::size_t case_count, std::size_t second_count, bool recovery)
        : cases(case_count), seconds(second_count), arrival_phases(recovery ? 2 : 0)
    {
    }

    Second* at(Time point, Time start)
    {
        const auto second = std::chrono::duration_cast<std::chrono::seconds>(point - start).count();
        if (second < 0 || static_cast<std::size_t>(second) >= seconds.size())
        {
            series_truncated = true;
            return nullptr;
        }
        return &seconds[second];
    }

    void run(int worker, Shared& shared)
    {
        const auto& opt = shared.options;
        const bool operation_mode = opt.operations > 0;
        const bool fixed = opt.rate > 0;
        std::uint64_t fixed_sequence = static_cast<std::uint64_t>(worker);
        for (;;)
        {
            if (!fixed && !operation_mode && Clock::now() >= shared.cutoff)
                break;
            const auto sequence = fixed ? fixed_sequence : shared.next_sequence.fetch_add(1, std::memory_order_relaxed);
            if (fixed)
                fixed_sequence += opt.concurrency;
            if ((fixed || operation_mode) && sequence >= shared.planned)
                break;
            const auto planned_at = fixed ? shared.planned_at(sequence) : Clock::now();
            if (fixed && Clock::now() < planned_at)
                wait_until(planned_at, &shared.abort);
            auto began = Clock::now();
            if (shared.abort.load(std::memory_order_acquire))
                break;
            if (!operation_mode && began >= (fixed ? shared.drain_cutoff : shared.cutoff))
                break;
            const auto index = shared.mix[sequence % shared.mix.size()];
            auto& per_case = cases[index];
            const auto phase_index = shared.arrival_phase(sequence);
            auto* arrival_phase = arrival_phases.empty() ? nullptr : &arrival_phases[phase_index];
            const auto phase_cutoff = shared.arrival_phase_cutoff(phase_index);
            ++counts.started;
            ++per_case.started;
            if (arrival_phase)
            {
                ++arrival_phase->counts.started;
                if (began >= phase_cutoff)
                    ++arrival_phase->counts.started_after_cutoff;
            }
            const bool after_cutoff = !operation_mode && began >= shared.cutoff;
            if (after_cutoff)
            {
                ++counts.started_after_cutoff;
                ++per_case.started_after_cutoff;
            }
            const auto planned = fixed ? planned_at : began;
            const bool late = fixed && began > planned;
            if (late)
            {
                ++counts.late;
                ++per_case.late;
                const auto lag = duration_ns(began, planned);
                counts.max_schedule_lag_ns = std::max(counts.max_schedule_lag_ns, lag);
                per_case.max_schedule_lag_ns = std::max(per_case.max_schedule_lag_ns, lag);
                if (arrival_phase)
                {
                    ++arrival_phase->counts.late;
                    arrival_phase->counts.max_schedule_lag_ns =
                        std::max(arrival_phase->counts.max_schedule_lag_ns, lag);
                }
            }
            if (auto* slot = at(began, shared.start))
            {
                ++slot->started;
                if (!fixed)
                    ++slot->planned;
                if (late)
                    ++slot->late;
            }
            bool success = false;
            db_perf::OperationResult result{};
            std::size_t failure_category = 6;
            try
            {
                result = shared.execute(worker, index, operation_mode ? Time::max() : shared.drain_cutoff, true);
                success = true;
            }
            catch (const db_perf::DatabaseError& error)
            {
                failure_category = category(error);
                errors[failure_category].record(error.what(), error.native_code.c_str(), error.category.c_str());
            }
            catch (const std::bad_alloc& error)
            {
                failure_category = 5;
                errors[failure_category].record(error.what(), "");
            }
            catch (const std::exception& error)
            {
                errors[failure_category].record(error.what(), "");
            }
            catch (...)
            {
                errors[failure_category].record("Non-standard exception", "");
            }
            const auto ended = Clock::now();
            last_terminal = ended;
            if (!operation_mode && ended > shared.drain_cutoff)
            {
                ++counts.unfinished;
                ++per_case.unfinished;
                ++counts.late_terminal;
                ++per_case.late_terminal;
                if (arrival_phase)
                {
                    ++arrival_phase->counts.unfinished;
                    ++arrival_phase->counts.late_terminal;
                }
                if (auto* slot = at(ended, shared.start))
                    ++slot->late_terminal;
                // The driver has retired safely, but this terminal result was
                // unavailable at the declared drain deadline. It is not success.
                break;
            }
            const bool in_window = operation_mode || ended <= shared.cutoff;
            if (success)
            {
                ++counts.completed;
                ++per_case.completed;
                counts.total_rows += result.rows;
                per_case.total_rows += result.rows;
                service_total.record(duration_ns(ended, began));
                offered_total.record(duration_ns(ended, planned));
                if (in_window)
                {
                    ++counts.completed_in_window;
                    ++per_case.completed_in_window;
                    counts.rows_in_window += result.rows;
                    per_case.rows_in_window += result.rows;
                    service.record(duration_ns(ended, began));
                    offered.record(duration_ns(ended, planned));
                }
                else
                {
                    ++counts.completed_after_cutoff;
                    ++per_case.completed_after_cutoff;
                }
            }
            else
            {
                ++counts.failed;
                ++per_case.failed;
                if (in_window)
                {
                    ++counts.failed_in_window;
                    ++per_case.failed_in_window;
                }
                else
                {
                    ++counts.failed_after_cutoff;
                    ++per_case.failed_after_cutoff;
                }
            }
            if (arrival_phase)
            {
                auto& phase_counts = arrival_phase->counts;
                const bool phase_in_window = ended <= phase_cutoff;
                if (success)
                {
                    ++phase_counts.completed;
                    phase_counts.total_rows += result.rows;
                    arrival_phase->service_total.record(duration_ns(ended, began));
                    arrival_phase->offered_total.record(duration_ns(ended, planned));
                    if (phase_in_window)
                    {
                        ++phase_counts.completed_in_window;
                        phase_counts.rows_in_window += result.rows;
                        arrival_phase->service.record(duration_ns(ended, began));
                        arrival_phase->offered.record(duration_ns(ended, planned));
                    }
                    else
                        ++phase_counts.completed_after_cutoff;
                }
                else
                {
                    ++phase_counts.failed;
                    if (phase_in_window)
                        ++phase_counts.failed_in_window;
                    else
                        ++phase_counts.failed_after_cutoff;
                }
            }
            if (auto* slot = at(ended, shared.start))
            {
                if (success)
                {
                    ++slot->completed;
                    slot->rows += result.rows;
                }
                else
                    ++slot->failed;
            }
        }
        finished = Clock::now();
    }
};

Json owners_json(const db_perf::OwnerCounts& value)
{
    return {{"connections", value.connections},
            {"sqlite_statements", value.sqlite_statements},
            {"postgres_results", value.postgres_results},
            {"unconfirmed_sqlite_closes", value.unconfirmed_sqlite_closes}};
}

using CounterMember = std::uint64_t db_perf::Counters::*;
constexpr std::array<std::pair<const char*, CounterMember>, 12> counter_fields{
    {{"sql_executions", &db_perf::Counters::sql_executions},
     {"protocol_submissions", &db_perf::Counters::protocol_submissions},
     {"physical_prepare_calls", &db_perf::Counters::physical_prepare_calls},
     {"describe_calls", &db_perf::Counters::describe_calls},
     {"confirmation_syncs", &db_perf::Counters::confirmation_syncs},
     {"poll_wait_calls", &db_perf::Counters::poll_wait_calls},
     {"rows_fetched", &db_perf::Counters::rows_fetched},
     {"rows_consumed", &db_perf::Counters::rows_consumed},
     {"connection_opens", &db_perf::Counters::connection_opens},
     {"discarded_connections", &db_perf::Counters::discarded_connections},
     {"setup_sql_executions", &db_perf::Counters::setup_sql_executions},
     {"cleanup_failures", &db_perf::Counters::cleanup_failures}}};

Json measured_counters(const std::vector<std::unique_ptr<db_perf::QueryAdapter>>& adapters,
                       const std::vector<db_perf::Counters>& before)
{
    Json output = Json::object();
    for (const auto& [name, member] : counter_fields)
    {
        std::uint64_t value = 0;
        for (std::size_t i = 0; i < adapters.size(); ++i)
            value += adapters[i]->counters().*member - before[i].*member;
        output[name] = value;
    }
    return output;
}

Json run(const Options& options, const db_perf::Contract& contract, const std::string& manifest_hash)
{
    std::vector<std::size_t> mix;
    std::set<std::string> names;
    for (std::size_t i = 0; i < contract.cases.size(); ++i)
    {
        const auto& name = contract.cases[i].name;
        names.insert(name);
        if ((options.selected == "mixed" || options.selected == name) && !options.excluded.contains(name))
            mix.push_back(i);
    }
    if (mix.empty())
        throw std::invalid_argument("The selected case set is empty");
    for (const auto& name : options.excluded)
        if (!names.contains(name))
            throw std::invalid_argument("Unknown excluded case: " + name);

    const auto identity = db_perf::runtime_identity();
    std::vector<std::unique_ptr<db_perf::QueryAdapter>> adapters;
    db_perf::ConnectionOptions connection;
    connection.connection = options.connection;
    connection.selected_cases = mix;
    connection.mode =
        options.mode == "prepared" ? db_perf::ConstructionMode::Prepared : db_perf::ConstructionMode::Dynamic;
    connection.operation_timeout = command_timeout;
    connection.sqlite_busy_timeout = command_timeout;
    for (int i = 0; i < options.concurrency; ++i)
        adapters.push_back(std::make_unique<db_perf::QueryAdapter>(contract, connection));
    ConnectionPool pool(adapters);
    Shared shared{options, contract, mix, adapters, pool};
    shared.planned =
        options.rate > 0 ? static_cast<std::uint64_t>(std::floor(options.rate * options.seconds)) : options.operations;
    if (options.recovery_rate)
    {
        shared.initial_phase_planned = static_cast<std::uint64_t>(std::floor(options.rate * *options.recovery_after));
        shared.planned = shared.initial_phase_planned +
                         static_cast<std::uint64_t>(
                             std::floor(*options.recovery_rate * (options.seconds - *options.recovery_after)));
    }

    std::vector<Validation> validation(contract.cases.size());
    bool valid = true;
    for (std::size_t worker = 0; worker < adapters.size(); ++worker)
        for (auto index : mix)
        {
            auto& result = validation[index];
            ++result.attempts;
            try
            {
                result.actual = adapters[worker]->execute(index, static_cast<std::int64_t>(worker + 1));
                ++result.passed;
            }
            catch (const std::exception& failure)
            {
                valid = false;
                if (result.first_error.empty())
                    result.first_error = failure.what();
            }
        }
    if (valid && !options.verify_only)
        for (int worker = 0; worker < options.concurrency; ++worker)
            for (int i = 0; i < options.warmup; ++i)
                (void)shared.execute(worker,
                                     mix[(static_cast<std::size_t>(worker) + i * options.concurrency) % mix.size()],
                                     Time::max(), false);

    const bool measure = valid && !options.verify_only;
    const std::size_t second_capacity =
        options.operations > 0 ? 3601 : static_cast<std::size_t>(std::ceil(options.seconds)) + 31;
    std::vector<std::unique_ptr<Worker>> workers;
    for (int i = 0; i < options.concurrency; ++i)
        workers.push_back(std::make_unique<Worker>(contract.cases.size(), second_capacity,
                                                   measure && options.recovery_rate.has_value()));
    std::vector<db_perf::Counters> counters_before;
    for (const auto& adapter : adapters)
        counters_before.push_back(adapter->counters());
    std::vector<std::map<std::string, std::string>> settings;
    for (const auto& adapter : adapters)
        settings.push_back(adapter->effective_configuration());
    const auto owners_before = db_perf::live_owners();
    sqlite3_int64 sqlite_before = 0, sqlite_ignored = 0, sqlite_after = 0, sqlite_peak = 0;
    sqlite3_status64(SQLITE_STATUS_MEMORY_USED, &sqlite_before, &sqlite_ignored, 1);

    std::vector<std::thread> threads;
    std::latch ready(options.concurrency);
    if (measure)
    {
        try
        {
            for (int i = 0; i < options.concurrency; ++i)
                threads.emplace_back(
                    [&, i]
                    {
                        {
                            std::unique_lock lock(shared.gate);
                            ready.count_down();
                            shared.start_changed.wait(lock, [&] { return shared.begin; });
                        }
                        if (!shared.abort.load(std::memory_order_acquire))
                        {
                            try
                            {
                                workers[i]->run(i, shared);
                            }
                            catch (const std::exception& error)
                            {
                                workers[i]->errors.back().record(error.what(), "", "scheduler");
                                workers[i]->finished = Clock::now();
                            }
                            catch (...)
                            {
                                workers[i]->errors.back().record("Non-standard worker exception", "", "scheduler");
                                workers[i]->finished = Clock::now();
                            }
                        }
                        {
                            std::lock_guard lock(shared.gate);
                            shared.done.fetch_add(1, std::memory_order_release);
                        }
                        shared.done_changed.notify_all();
                        std::unique_lock exit_lock(shared.gate);
                        shared.start_changed.wait(exit_lock, [&] { return shared.exit; });
                    });
        }
        catch (...)
        {
            {
                std::lock_guard lock(shared.gate);
                shared.abort = true;
                shared.begin = true;
                shared.exit = true;
            }
            shared.start_changed.notify_all();
            for (auto& thread : threads)
                thread.join();
            throw;
        }
        ready.wait();
    }
    if (measure)
        phase("READY");
    const auto start_wall = std::chrono::system_clock::now();
    if (measure)
        phase("START");
    const auto before = ProcessReading::read();
    {
        std::lock_guard lock(shared.gate);
        shared.start = Clock::now();
        shared.cutoff = options.operations > 0 ? Time::max()
                                               : shared.start + std::chrono::duration_cast<Clock::duration>(
                                                                    std::chrono::duration<double>(options.seconds));
        shared.drain_cutoff = options.operations > 0 ? Time::max() : shared.cutoff + drain_budget;
        shared.begin = true;
    }
    if (measure)
        shared.start_changed.notify_all();
    if (measure && options.operations == 0)
        wait_until(shared.cutoff);
    if (measure && options.operations > 0)
    {
        std::unique_lock lock(shared.gate);
        shared.done_changed.wait(lock,
                                 [&] { return shared.done.load(std::memory_order_acquire) == options.concurrency; });
        shared.cutoff = shared.start;
        for (const auto& worker : workers)
            shared.cutoff = std::max(shared.cutoff, worker->last_terminal);
        shared.drain_cutoff = shared.cutoff + drain_budget;
    }
    if (!measure)
    {
        shared.cutoff = shared.start;
        shared.drain_cutoff = shared.start + drain_budget;
    }
    const auto at_cutoff = ProcessReading::read();
    if (measure)
        phase("END");
    if (measure)
    {
        std::unique_lock lock(shared.gate);
        shared.done_changed.wait(lock,
                                 [&] { return shared.done.load(std::memory_order_acquire) == options.concurrency; });
    }
    const auto all_done = Clock::now();
    const auto after_drain = ProcessReading::read();
    if (measure)
        phase("DRAINED");
    const auto owners_after_drain = db_perf::live_owners();
    sqlite3_status64(SQLITE_STATUS_MEMORY_USED, &sqlite_after, &sqlite_peak, 0);
    if (measure)
        phase("IDLE_OPEN");
    const auto before_idle_open = ProcessReading::read();
    const auto idle_open_started = Clock::now();
    std::this_thread::sleep_for(std::chrono::seconds(1));
    const auto after_idle_open = ProcessReading::read();
    const auto idle_open_elapsed = std::chrono::duration<double>(Clock::now() - idle_open_started).count();
    if (measure)
        phase("IDLE_OPEN_DONE");
    {
        std::lock_guard lock(shared.gate);
        shared.exit = true;
    }
    shared.start_changed.notify_all();
    for (auto& thread : threads)
        thread.join();
    for (auto& adapter : adapters)
        adapter->close();
    const auto owners_after_close = db_perf::live_owners();
    if (measure)
        phase("CLOSED");
    if (measure)
        phase("IDLE");
    const auto after_close = ProcessReading::read();
    const auto idle_started = Clock::now();
    std::this_thread::sleep_for(std::chrono::seconds(1));
    const auto after_idle = ProcessReading::read();
    const auto idle_elapsed = std::chrono::duration<double>(Clock::now() - idle_started).count();
    sqlite3_int64 sqlite_retained = 0;
    sqlite3_status64(SQLITE_STATUS_MEMORY_USED, &sqlite_retained, &sqlite_ignored, 0);
    if (measure)
        phase("IDLE_DONE");

    Counts total;
    Histogram service, offered, service_total, offered_total;
    std::vector<Counts> case_counts(contract.cases.size());
    std::vector<Second> seconds(second_capacity);
    std::vector<ArrivalPhaseMetrics> arrival_phase_totals(measure && options.recovery_rate ? 2 : 0);
    bool truncated = false;
    std::uint64_t validation_errors = 0, unknown_errors = 0;
    Json errors = Json::array();
    Json errors_normalized = Json::object();
    for (const auto name : error_names)
        errors_normalized[std::string(name)] = 0;
    for (std::size_t i = 0; i < workers.size(); ++i)
    {
        const auto& worker = *workers[i];
        total.add(worker.counts);
        service.add(worker.service);
        offered.add(worker.offered);
        service_total.add(worker.service_total);
        offered_total.add(worker.offered_total);
        for (std::size_t c = 0; c < case_counts.size(); ++c)
            case_counts[c].add(worker.cases[c]);
        for (std::size_t s = 0; s < seconds.size(); ++s)
            seconds[s].add(worker.seconds[s]);
        for (std::size_t p = 0; p < arrival_phase_totals.size(); ++p)
            arrival_phase_totals[p].add(worker.arrival_phases[p]);
        truncated = truncated || worker.series_truncated;
        validation_errors += worker.errors[2].count;
        unknown_errors += worker.errors[6].count;
        for (std::size_t kind = 0; kind < error_names.size(); ++kind)
        {
            const auto name = std::string(error_names[kind]);
            errors_normalized[name] = errors_normalized[name].get<std::uint64_t>() + worker.errors[kind].count;
            if (worker.errors[kind].count)
                errors.push_back({{"worker", i},
                                  {"category", error_names[kind]},
                                  {"count", worker.errors[kind].count},
                                  {"first_error", worker.errors[kind].message.data()},
                                  {"first_source_category", worker.errors[kind].source_category.data()},
                                  {"native_code", worker.errors[kind].code.data()}});
        }
    }
    const auto planned = !measure ? 0 : options.rate > 0 || options.operations > 0 ? shared.planned : total.started;
    if (options.rate > 0 && measure)
        for (std::size_t second = 0; second < seconds.size(); ++second)
        {
            const auto count_before = [](double at, double start, double rate, std::uint64_t phase_planned)
            {
                return static_cast<std::uint64_t>(
                    std::clamp(std::ceil((at - start) * rate), 0., static_cast<double>(phase_planned)));
            };
            const auto phase_count = [&](double start, double rate, std::uint64_t phase_planned)
            {
                return count_before(static_cast<double>(second + 1), start, rate, phase_planned) -
                       count_before(static_cast<double>(second), start, rate, phase_planned);
            };
            seconds[second].planned = options.recovery_rate
                                          ? phase_count(0, options.rate, shared.initial_phase_planned) +
                                                phase_count(*options.recovery_after, *options.recovery_rate,
                                                            planned - shared.initial_phase_planned)
                                          : phase_count(0, options.rate, planned);
        }
    if (planned < total.started || total.started != total.completed + total.failed + total.unfinished)
        throw std::runtime_error("Campaign terminal accounting invariant failed");
    Json arrival_phases = Json::array();
    Counts phase_total_counts;
    for (std::size_t p = 0; p < arrival_phase_totals.size(); ++p)
    {
        const auto& metrics = arrival_phase_totals[p];
        const auto phase_planned =
            p == 0 ? shared.initial_phase_planned : shared.planned - shared.initial_phase_planned;
        const auto start_offset = p == 0 ? 0. : *options.recovery_after;
        const auto end_offset = p == 0 ? *options.recovery_after : options.seconds;
        const auto rate = p == 0 ? options.rate : *options.recovery_rate;
        const auto& count = metrics.counts;
        if (phase_planned < count.started || count.started != count.completed + count.failed + count.unfinished ||
            metrics.service.count != count.completed_in_window || metrics.offered.count != count.completed_in_window ||
            metrics.service_total.count != count.completed || metrics.offered_total.count != count.completed)
            throw std::runtime_error("Arrival phase terminal or histogram accounting invariant failed");
        phase_total_counts.add(count);
        arrival_phases.push_back(
            {{"name", p == 0 ? "saturation" : "recovery"},
             {"start_offset_seconds", start_offset},
             {"end_offset_seconds", end_offset},
             {"rate", rate},
             {"counts", count.json(phase_planned, true)},
             {"latency", {{"service", metrics.service.json()}, {"offered", metrics.offered.json()}}},
             {"latency_total", {{"service", metrics.service_total.json()}, {"offered", metrics.offered_total.json()}}},
             {"successful_ops_per_second", count.completed_in_window / (end_offset - start_offset)},
             {"successful_rows_per_second", count.rows_in_window / (end_offset - start_offset)}});
    }
    if (!arrival_phase_totals.empty() &&
        (phase_total_counts.started != total.started || phase_total_counts.completed != total.completed ||
         phase_total_counts.failed != total.failed || phase_total_counts.unfinished != total.unfinished ||
         phase_total_counts.total_rows != total.total_rows))
        throw std::runtime_error("Arrival phase counts do not sum to campaign totals");
    Json cases = Json::array(), correctness = Json::array(), selected = Json::array();
    for (std::size_t position = 0; position < mix.size(); ++position)
    {
        const auto index = mix[position];
        const auto& query = contract.cases[index];
        const auto& check = validation[index];
        selected.push_back(query.name);
        correctness.push_back(
            {{"id", query.id},
             {"name", query.name},
             {"attempts", check.attempts},
             {"passed", check.passed},
             {"expected_rows", query.expected_rows},
             {"expected_digest", std::to_string(query.expected_digest)},
             {"expected_outcome", query.expected_outcome},
             {"actual_rows", check.actual ? Json(check.actual->rows) : Json(nullptr)},
             {"actual_digest", check.actual ? Json(std::to_string(check.actual->digest)) : Json(nullptr)},
             {"actual_outcome", check.actual ? Json(check.actual->outcome) : Json(nullptr)},
             {"first_error", check.first_error.empty() ? Json(nullptr) : Json(check.first_error)}});
        const auto case_planned = !measure ? 0
                                  : options.rate > 0 || options.operations > 0
                                      ? planned / mix.size() + (position < planned % mix.size() ? 1 : 0)
                                      : case_counts[index].started;
        auto case_result = case_counts[index].json(case_planned, options.rate > 0);
        case_result["id"] = query.id;
        case_result["name"] = query.name;
        case_result["selected"] = true;
        cases.push_back(std::move(case_result));
    }
    Json series = Json::array();
    const auto observed_seconds =
        static_cast<std::size_t>(std::ceil(std::chrono::duration<double>(all_done - shared.start).count())) + 1;
    for (std::size_t second = 0; second < std::min(seconds.size(), observed_seconds); ++second)
    {
        const auto& value = seconds[second];
        series.push_back({{"second", second},
                          {"planned", value.planned},
                          {"started", value.started},
                          {"completed", value.completed},
                          {"failed", value.failed},
                          {"rows", value.rows},
                          {"late", value.late},
                          {"late_terminal", value.late_terminal}});
    }
    const double window_seconds = measure ? std::chrono::duration<double>(shared.cutoff - shared.start).count() : 0;
    const double actual_drain_seconds =
        measure ? std::max(0., std::chrono::duration<double>(all_done - shared.cutoff).count()) : 0;
    const bool drained = owners_after_close.connections == 0 && owners_after_close.sqlite_statements == 0 &&
                         owners_after_close.postgres_results == 0 && owners_after_close.unconfirmed_sqlite_closes == 0;
    const bool overrun = measure && all_done > shared.drain_cutoff;
    const bool complete = (measure || options.verify_only) && valid && planned == total.started &&
                          total.unfinished == 0 && validation_errors == 0 && unknown_errors == 0 && !overrun &&
                          !truncated && drained;
    const auto cpu_window_us = at_cutoff.user_us + at_cutoff.system_us - before.user_us - before.system_us;
    Json output{
        {"schema_version", 1},
        {"task", "DB-Perf-01"},
        {"route", "direct-native-cpp"},
        {"provider", options.provider},
        {"contract_sha256", manifest_hash},
        {"mode", options.mode},
        {"session_mode", options.session_mode},
        {"selected_cases", selected},
        {"excluded_cases", options.excluded},
        {"concurrency", options.concurrency},
        {"pool", pool.json()},
        {"warmup_per_worker", options.warmup},
        {"requested_seconds", options.seconds},
        {"seconds_active", options.operations == 0},
        {"requested_rate", options.rate},
        {"requested_recovery_rate", options.recovery_rate ? Json(*options.recovery_rate) : Json(nullptr)},
        {"requested_recovery_after_seconds", options.recovery_after ? Json(*options.recovery_after) : Json(nullptr)},
        {"requested_operations", options.operations},
        {"closed_loop_sequence_claims", shared.next_sequence.load(std::memory_order_relaxed)},
        {"closed_loop_sequence_claims_scope",
         "Includes each worker's final atomic claim at the finite-count boundary; indices outside the configured [0,N) "
         "schedule are not planned requests."},
        {"schedule", options.rate > 0 ? "fixed-rate global sequence worker_id+k*concurrency"
                                      : "closed-loop shared atomic next sequence; case=sequence%mix_count"},
        {"empty_driver", options.empty_driver},
        {"verify_only", options.verify_only},
        {"measurement_executed", measure},
        {"correctness_passed", valid},
        {"correctness", correctness},
        {"physical_preparation", options.mode == "prepared"
                                     ? "one reusable physical statement per case per connection"
                                     : "assemble SQL clauses and physically prepare per operation"},
        {"prepared_comparison",
         "Physical statement reuse differs from current Korda compiled-shape reuse; retain as an explicit layer"},
        {"protocol_confirmation", "product"},
        {"timer_policy", "CLOCK_MONOTONIC absolute clock_nanosleep; EINTR retry; workers wait at most 50ms per "
                         "blocking slice; no spin tail"},
        {"phase_protocol",
         "DB_PERF_PHASE READY after verification/warmup; START before start counters; END after cutoff counters; "
         "DRAINED after admitted APIs retire while workers remain alive; IDLE_OPEN before open-owner idle; "
         "IDLE_OPEN_DONE after open-owner idle and before disposal; CLOSED after disposal; "
         "IDLE before closed-owner idle; IDLE_DONE after closed-owner idle"},
        {"command_timeout_ms", 5000},
        {"command_timeout_scope", "One deadline covers all fetches/results of each SQL command; the business operation "
                                  "is additionally bounded by the scheduler drain deadline. Exceptional rollback "
                                  "retirement has its own 5-second budget and any overrun remains invalid."},
        {"pool_wait_timeout_ms", 5000},
        {"counts", total.json(planned, options.rate > 0)},
        {"arrival_phases", arrival_phases},
        {"arrival_phase_scope",
         "Requests belong to their planned arrival phase. Phase in-window counts and primary latency stop at that "
         "phase's end; phase total latency includes its successful requests through the final global drain. "
         "Unstarted and unfinished use the final global drain deadline. Workers, connections, pool, and global mix "
         "sequence are retained across the transition. No phase histogram storage is allocated without recovery."},
        {"by_case", cases},
        {"by_second", series},
        {"time_series_truncated", truncated},
        {"time_series_capacity_seconds", second_capacity},
        {"latency", {{"service", service.json()}, {"offered", offered.json()}}},
        {"latency_total", {{"service", service_total.json()}, {"offered", offered_total.json()}}},
        {"latency_scope",
         "successful terminal results; primary histograms finish inside window; total includes bounded drain"},
        {"late_definition", "actual start later than planned timestamp, with no rounding threshold"},
        {"started_utc", utc(start_wall)},
        {"start_monotonic_ns", nanoseconds(shared.start)},
        {"cutoff_monotonic_ns", nanoseconds(shared.cutoff)},
        {"drain_cutoff_monotonic_ns", nanoseconds(shared.drain_cutoff)},
        {"window_seconds", window_seconds},
        {"actual_drain_seconds", actual_drain_seconds},
        {"drain_budget_seconds", 30},
        {"drain_overrun", overrun},
        {"cutoff_observation_delay_ns", std::max<std::int64_t>(0, at_cutoff.observed_ns - nanoseconds(shared.cutoff))},
        {"successful_ops_per_second",
         window_seconds > 0 ? Json(total.completed_in_window / window_seconds) : Json(nullptr)},
        {"successful_rows_per_second",
         window_seconds > 0 ? Json(total.rows_in_window / window_seconds) : Json(nullptr)},
        {"cpu",
         {{"window", cpu_delta(before, at_cutoff, total.completed_in_window)},
          {"drain", cpu_delta(at_cutoff, after_drain, total.completed_after_cutoff)},
          {"idle_open", cpu_delta(before_idle_open, after_idle_open)},
          {"idle_open_seconds", idle_open_elapsed},
          {"close", cpu_delta(after_idle_open, after_close)},
          {"idle", cpu_delta(after_close, after_idle)},
          {"idle_seconds", idle_elapsed}}},
        {"cpu_seconds_per_successful_operation",
         total.completed_in_window ? Json(cpu_window_us / 1e6 / total.completed_in_window) : Json(nullptr)},
        {"process_before", before.json()},
        {"process_at_cutoff", at_cutoff.json()},
        {"process_after_drain", after_drain.json()},
        {"process_before_idle_open", before_idle_open.json()},
        {"process_after_idle_open", after_idle_open.json()},
        {"process_after_close", after_close.json()},
        {"process_after_idle", after_idle.json()},
        {"process_peak_scope", "getrusage peak includes startup and warmup; RSS/PSS are point observations; harness "
                               "buffers retained through idle"},
        {"effective_configuration", settings},
        {"driver_counters", measured_counters(adapters, counters_before)},
        {"owners_before", owners_json(owners_before)},
        {"owners_after_drain", owners_json(owners_after_drain)},
        {"owners_after_close", owners_json(owners_after_close)},
        {"owners_returned_to_zero", drained},
        {"owner_counter_scope",
         "explicit client connections/statements/PGresults only; not a complete native heap trace"},
        {"managed_allocated_bytes", nullptr},
        {"managed_allocation_status", "not applicable to direct C++"},
        {"gc_counts", nullptr},
        {"native_allocation_calls", nullptr},
        {"native_free_calls", nullptr},
        {"native_requested_bytes", nullptr},
        {"native_heap_peak_bytes", nullptr},
        {"native_allocation_status", "not instrumented; mallinfo2 is current glibc block accounting, not cumulative "
                                     "allocation or whole-process peak"},
        {"sqlite_engine_bytes_before", sqlite_before},
        {"sqlite_engine_bytes_after_drain", sqlite_after},
        {"sqlite_engine_highwater_since_warmup", sqlite_peak},
        {"sqlite_engine_bytes_after_close_idle", sqlite_retained},
        {"sqlite_memory_scope", "SQLite engine allocator only, excluding C++/libpq/OpenSSL and direct mmap"},
        {"physical_network_round_trips", nullptr},
        {"physical_network_round_trips_status",
         "not instrumented; protocol submissions and waits are different metrics"},
        {"runtime",
         {{"sqlite_version", identity.sqlite_version},
          {"sqlite_source_id", identity.sqlite_source_id},
          {"sqlite_compile_options", identity.sqlite_compile_options},
          {"libpq_version", identity.libpq_version}}},
        {"errors", errors},
        {"errors_normalized", errors_normalized},
        {"run_complete", complete},
        {"status", !valid                ? "correctness_failed"
                   : options.verify_only ? "verified"
                   : !complete           ? "incomplete_or_invalid"
                   : total.failed        ? "completed_with_failures"
                                         : "completed"},
        {"cannot_qualify", !complete || options.empty_driver || options.operations > 0 || window_seconds < 1800},
        {"can_qualify",
         complete && measure && !options.empty_driver && options.operations == 0 && window_seconds >= 1800},
        {"slo_evaluated", false},
        {"performance_acceptance_claimed", false},
        {"acceptance_status",
         "Read/write adapter result only; no genuine concurrent-conflict/cancellation, independent native heap, server "
         "resource, or dedicated-host acceptance implied"}};
    if (!measure)
        for (const auto* field : {"successful_ops_per_second", "successful_rows_per_second", "latency", "latency_total",
                                  "cpu", "cpu_seconds_per_successful_operation"})
            output.erase(field);
    return output;
}

void write_json(const std::filesystem::path& path, const Json& value)
{
    if (!path.parent_path().empty())
        std::filesystem::create_directories(path.parent_path());
    const auto bytes = value.dump(2) + "\n";
    const int file = ::open(path.c_str(), O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC, 0644);
    if (file < 0)
        throw std::runtime_error("Cannot create new result file");
    std::size_t offset = 0;
    while (offset < bytes.size())
    {
        const auto count = ::write(file, bytes.data() + offset, bytes.size() - offset);
        if (count < 0 && errno == EINTR)
            continue;
        if (count <= 0)
        {
            ::close(file);
            throw std::runtime_error("Result write failed");
        }
        offset += static_cast<std::size_t>(count);
    }
    if (::close(file) != 0)
        throw std::runtime_error("Result close failed");
}
} // namespace

int main(int argc, char** argv)
{
    try
    {
        const auto options = parse_options(argc, argv);
        const auto manifest_hash = sha256(options.manifest);
        const auto provider = options.provider == "sqlite" ? db_perf::Provider::Sqlite : db_perf::Provider::PostgreSql;
        const auto contract = db_perf::load_contract(options.manifest, provider);
        if (sha256(options.manifest) != manifest_hash)
            throw std::runtime_error("Contract changed while loading");
        auto receipt = run(options, contract, manifest_hash);
        receipt["contract_unchanged_after_run"] = sha256(options.manifest) == manifest_hash;
        if (!receipt["contract_unchanged_after_run"].get<bool>())
        {
            receipt["cannot_qualify"] = true;
            receipt["can_qualify"] = false;
            receipt["run_complete"] = false;
            receipt["status"] = "contract_changed_during_run";
        }
        const auto executable = std::filesystem::canonical("/proc/self/exe");
        receipt["executable_sha256"] = sha256("/proc/self/exe");
        receipt["executable_path"] = executable.string();
        const auto build_receipt = executable.parent_path() / "build-receipt.json";
        receipt["build_receipt_sha256"] =
            std::filesystem::exists(build_receipt) ? Json(sha256(build_receipt)) : Json(nullptr);
        Json libraries = Json::array();
        std::set<std::string> paths;
        std::ifstream maps("/proc/self/maps");
        std::string line;
        while (std::getline(maps, line))
        {
            const auto slash = line.find('/');
            if (slash == std::string::npos)
                continue;
            const auto path = line.substr(slash);
            if (paths.insert(path).second && std::filesystem::is_regular_file(path))
                libraries.push_back({{"path", path}, {"sha256", sha256(path)}});
        }
        receipt["mapped_file_identities"] = libraries;
        receipt["native_driver_linkage"] =
            "SQLite and patched libpq are static in this executable; archive hashes belong to the build receipt";
        write_json(options.output, receipt);
        std::cout << "DB_PERF_NATIVE_RESULT output=" << options.output << "\n";
        if (options.verify_only)
            return receipt["correctness_passed"].get<bool>() && receipt["owners_returned_to_zero"].get<bool>() &&
                           receipt["contract_unchanged_after_run"].get<bool>()
                       ? 0
                       : 2;
        return receipt["run_complete"].get<bool>() ? 0 : 2;
    }
    catch (const std::exception& error)
    {
        std::cerr << "DB_PERF_NATIVE_ERROR " << error.what() << '\n';
        return 2;
    }
}
