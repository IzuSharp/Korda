#include <sqlite3.h>
#include "allocation_profile.h"
#include <libpq-fe.h>
#include <openssl/evp.h>
#include <nlohmann/json.hpp>
#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <charconv>
#include <chrono>
#include <cctype>
#include <cerrno>
#include <cmath>
#include <condition_variable>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <dlfcn.h>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <latch>
#include <limits>
#include <malloc.h>
#include <memory>
#include <mutex>
#include <optional>
#include <poll.h>
#include <sstream>
#include <stdexcept>
#include <string>
#include <string_view>
#include <sys/resource.h>
#include <thread>
#include <unistd.h>
#include <vector>

using Json = nlohmann::json;
using Clock = std::chrono::steady_clock;
static std::atomic<std::int64_t> live_connections, live_sqlite_statements, live_pg_results, live_cancel_owners;

struct Options
{
    std::string provider, connection, manifest, output, cases, selected = "numeric", mode = "prepared", physical = "per-operation", confirmation = "product";
    std::uint64_t operations = 1000;
    int concurrency = 1, warmup = 64, queue_capacity = 0;
    double seconds = 0, idle_seconds = 1, rate = 0, recovery_rate = 50, recovery_seconds = 2, overload_rate = 1000000, overload_seconds = 2;
    bool verify = false, empty_driver = false, explain = false, allocation_profile = false, recovery_checks = false, lifecycle_checks = false;
};
static Options options(int argc, char** argv)
{
    Options result;
    for (int i = 1; i < argc; ++i)
    {
        std::string key = argv[i];
        if (key == "--verify") { result.verify = true; continue; }
        if (key == "--empty-driver") { result.empty_driver = true; continue; }
        if (key == "--explain") { result.explain = true; continue; }
        if (key == "--allocation-profile") { result.allocation_profile = true; continue; }
        if (key == "--recovery-checks") { result.recovery_checks = true; continue; }
        if (key == "--lifecycle-checks") { result.lifecycle_checks = true; continue; }
        if (++i == argc) throw std::runtime_error("missing option value");
        std::string value = argv[i];
        if (key == "--provider") result.provider = value;
        else if (key == "--connection") result.connection = value;
        else if (key == "--manifest") result.manifest = value;
        else if (key == "--output") result.output = value;
        else if (key == "--case") result.selected = value;
        else if (key == "--cases") result.cases = value;
        else if (key == "--mode") result.mode = value;
        else if (key == "--physical-prepare") result.physical = value;
        else if (key == "--protocol-confirmation") result.confirmation = value;
        else if (key == "--concurrency") result.concurrency = std::stoi(value);
        else if (key == "--warmup") result.warmup = std::stoi(value);
        else if (key == "--operations") { result.operations = std::stoull(value); result.seconds = 0; }
        else if (key == "--seconds") { result.seconds = std::stod(value); result.operations = 0; }
        else if (key == "--idle-seconds") result.idle_seconds = std::stod(value);
        else if (key == "--rate" || key == "--arrival-rate") result.rate = std::stod(value);
        else if (key == "--queue-capacity") result.queue_capacity = std::stoi(value);
        else if (key == "--recovery-rate") result.recovery_rate = std::stod(value);
        else if (key == "--recovery-seconds") result.recovery_seconds = std::stod(value);
        else if (key == "--overload-rate") result.overload_rate = std::stod(value);
        else if (key == "--overload-seconds") result.overload_seconds = std::stod(value);
        else if (key == "--session" && value == "persistent") { }
        else throw std::runtime_error("unsupported native comparator option: " + key);
    }
    if ((result.provider != "sqlite" && result.provider != "postgresql") || result.connection.empty() || result.manifest.empty() ||
        (result.mode != "dynamic" && result.mode != "prepared") || (result.physical != "per-operation" && result.physical != "reuse") ||
        (result.mode == "dynamic" && result.physical == "reuse") || result.concurrency < 1 || result.concurrency > 256 ||
        (result.confirmation != "product" && result.confirmation != "drain-only") ||
        result.warmup < 1 || result.warmup > 100000 || result.seconds < 0 || result.seconds > 86400 ||
        !std::isfinite(result.seconds) || result.idle_seconds < 0 || result.idle_seconds > 30 || !std::isfinite(result.idle_seconds) ||
        !std::isfinite(result.rate) || result.rate < 0 || result.rate > 1000000 || (result.rate && !result.seconds) ||
        result.queue_capacity < 0 || result.queue_capacity > 65536 || (result.cases.size() && result.selected != "mixed") ||
        (!result.operations && !result.seconds)) throw std::runtime_error("invalid or unbounded workload options");
    for (double value : {result.recovery_rate, result.overload_rate})
        if (!std::isfinite(value) || value <= 0 || value > 1000000) throw std::runtime_error("recovery rates must be finite and bounded");
    for (double value : {result.recovery_seconds, result.overload_seconds})
        if (!std::isfinite(value) || value <= 0 || value > 60) throw std::runtime_error("diagnostic phase durations must be positive and at most 60 seconds");
    if ((result.recovery_checks && result.lifecycle_checks) || ((result.recovery_checks || result.lifecycle_checks) && result.empty_driver))
        throw std::runtime_error("select one real-database diagnostic mode");
    if (result.lifecycle_checks && result.allocation_profile) throw std::runtime_error("allocation profiles use the steady or recovery lanes; active cancellation has separate correctness evidence");
    if (result.recovery_checks && (result.rate != 0 || result.overload_rate <= result.recovery_rate))
        throw std::runtime_error("recovery checks require dedicated rates and an overload rate above recovery rate");
    if (!result.queue_capacity) result.queue_capacity = 4 * result.concurrency;
    return result;
}
static std::string hash_file(const std::filesystem::path& path)
{
    std::ifstream input(path, std::ios::binary);
    if (!input) throw std::runtime_error("cannot hash " + path.string());
    auto context = std::unique_ptr<EVP_MD_CTX, decltype(&EVP_MD_CTX_free)>(EVP_MD_CTX_new(), EVP_MD_CTX_free);
    if (!context || EVP_DigestInit_ex(context.get(), EVP_sha256(), nullptr) != 1) throw std::runtime_error("SHA256 initialization failed");
    std::array<char, 65536> bytes{};
    while (input) { input.read(bytes.data(), bytes.size()); if (input.gcount()) EVP_DigestUpdate(context.get(), bytes.data(), input.gcount()); }
    std::array<unsigned char, EVP_MAX_MD_SIZE> hash{}; unsigned length = 0;
    if (EVP_DigestFinal_ex(context.get(), hash.data(), &length) != 1 || length != 32) throw std::runtime_error("SHA256 finalization failed");
    std::ostringstream text; text << std::hex << std::setfill('0');
    for (unsigned i = 0; i < length; ++i) text << std::setw(2) << static_cast<unsigned>(hash[i]);
    return text.str();
}
static std::string utc_now()
{
    auto now = std::chrono::system_clock::now(); auto value = std::chrono::system_clock::to_time_t(now); std::tm tm{};
    gmtime_r(&value, &tm); std::array<char, 32> buffer{}; std::strftime(buffer.data(), buffer.size(), "%Y-%m-%dT%H:%M:%S", &tm);
    auto micros = std::chrono::duration_cast<std::chrono::microseconds>(now.time_since_epoch()).count() % 1000000;
    std::ostringstream text; text << buffer.data() << '.' << std::setw(6) << std::setfill('0') << micros << 'Z'; return text.str();
}
static std::uint64_t monotonic_ns()
{ timespec now{}; if (clock_gettime(CLOCK_MONOTONIC, &now)) throw std::runtime_error("CLOCK_MONOTONIC unavailable"); return now.tv_sec * 1000000000ULL + now.tv_nsec; }
struct AllocationProfile
{
    db_perf_alloc_snapshot_fn snapshot = nullptr;
    db_perf_alloc_snapshot_v1 before{}, after{}, drained{};
    explicit AllocationProfile(bool enabled)
    {
        if (enabled)
        {
            snapshot = reinterpret_cast<db_perf_alloc_snapshot_fn>(dlsym(RTLD_DEFAULT, "db_perf_alloc_snapshot"));
            if (!snapshot) throw std::runtime_error("--allocation-profile requires the verified LD_PRELOAD allocation profiler");
        }
    }
    void take(db_perf_alloc_snapshot_v1& value, bool reset = false)
    { if (snapshot && (snapshot(&value, sizeof(value), reset) || value.version != 1)) throw std::runtime_error("allocation profiler snapshot failed"); }
    static bool complete(const db_perf_alloc_snapshot_v1& value)
    { return !(value.unknown_frees || value.unknown_reallocs || value.dropped_allocations || value.duplicate_addresses || value.initialization_failures); }
    static Json raw(const db_perf_alloc_snapshot_v1& value)
    {
        Json result;
#define FIELD(name) result[#name] = value.name;
        FIELD(version) FIELD(table_capacity) FIELD(table_bytes) FIELD(malloc_calls) FIELD(calloc_calls) FIELD(realloc_calls)
        FIELD(aligned_calls) FIELD(free_calls) FIELD(null_frees) FIELD(successful_allocations) FIELD(failed_allocations) FIELD(deallocations)
        FIELD(allocated_requested_bytes) FIELD(released_requested_bytes) FIELD(allocated_usable_bytes) FIELD(released_usable_bytes)
        FIELD(live_allocations) FIELD(live_requested_bytes) FIELD(live_usable_bytes) FIELD(peak_live_allocations) FIELD(peak_requested_bytes) FIELD(peak_usable_bytes)
        FIELD(unknown_frees) FIELD(unknown_reallocs) FIELD(dropped_allocations) FIELD(duplicate_addresses) FIELD(initialization_failures)
#undef FIELD
        return result;
    }
    Json json() const
    {
        if (!snapshot) return nullptr;
        Dl_info info{}; if (!dladdr(reinterpret_cast<void*>(snapshot), &info) || !info.dli_fname) throw std::runtime_error("profiler identity unavailable");
        auto path = std::filesystem::canonical(info.dli_fname);
        bool reconciles = after.live_requested_bytes + after.released_requested_bytes == before.live_requested_bytes + before.released_requested_bytes
            + after.allocated_requested_bytes - before.allocated_requested_bytes;
        return {{"scope", "separate instrumented lane; all interposed public glibc allocator calls across all threads; excludes private mmap/allocator metadata/hidden internal calls"},
            {"libraryPath", path.string()}, {"librarySha256", hash_file(path)}, {"before", raw(before)}, {"after", raw(after)}, {"afterDrain", raw(drained)},
            {"allocationCalls", after.successful_allocations - before.successful_allocations}, {"deallocationEvents", after.deallocations - before.deallocations},
            {"allocatedRequestedBytes", after.allocated_requested_bytes - before.allocated_requested_bytes},
            {"releasedRequestedBytes", after.released_requested_bytes - before.released_requested_bytes},
            {"allocatedUsableBytes", after.allocated_usable_bytes - before.allocated_usable_bytes},
            {"releasedUsableBytes", after.released_usable_bytes - before.released_usable_bytes},
            {"trackedRequestedHeapPeakBytes", after.peak_requested_bytes}, {"trackedUsableHeapPeakBytes", after.peak_usable_bytes},
            {"observedAllocationCoverageComplete", complete(before) && complete(after) && complete(drained)}, {"requestedByteConservation", reconciles},
            {"fullAddressSpaceHeapPeakProven", false}, {"timingComparisonEligible", false},
            {"peakScope", "live tracked allocation sizes at wrapper registration boundaries; excludes transient internal realloc copy overlap and allocator metadata"},
            {"counterSemantics", "successful realloc retires the old tracked allocation and records the replacement, including in-place resize; free_calls also includes null frees"}};
    }
};
static std::optional<std::uint64_t> proc_kb(const char* path, const char* key)
{
    std::ifstream file(path); std::string line;
    while (std::getline(file, line))
        if (line.starts_with(key)) { std::istringstream input(line.substr(std::strlen(key))); std::uint64_t value; if (input >> value) return value * 1024; }
    return {};
}
struct ProcessReading
{
    double user = 0, system = 0;
    std::uint64_t peak_rss = 0, heap_used = 0, heap_reserved = 0;
    std::optional<std::uint64_t> rss, pss;
    static ProcessReading read()
    {
        rusage usage{}; if (getrusage(RUSAGE_SELF, &usage)) throw std::runtime_error("getrusage failed");
        auto heap = mallinfo2();
        return {usage.ru_utime.tv_sec + usage.ru_utime.tv_usec / 1e6, usage.ru_stime.tv_sec + usage.ru_stime.tv_usec / 1e6,
                static_cast<std::uint64_t>(usage.ru_maxrss) * 1024, heap.uordblks + heap.hblkhd, heap.arena + heap.hblkhd,
                proc_kb("/proc/self/status", "VmRSS:"), proc_kb("/proc/self/smaps_rollup", "Pss:")};
    }
    Json json() const
    {
        Json result{{"userCpuSeconds", user}, {"systemCpuSeconds", system}, {"peakRssBytes", peak_rss},
                    {"glibcAllocatedBlockBytes", heap_used}, {"glibcReservedBytes", heap_reserved}};
        result["rssBytes"] = rss ? Json(*rss) : Json(nullptr); result["pssBytes"] = pss ? Json(*pss) : Json(nullptr); return result;
    }
};
struct Digest
{
    std::uint64_t value = 14695981039346656037ULL;
    void byte(unsigned char b) { value = (value ^ b) * 1099511628211ULL; }
    void integer(std::int64_t number) { auto bits = static_cast<std::uint64_t>(number); for (int i = 0; i < 8; ++i) { byte(bits & 255); bits >>= 8; } }
    void text(std::string_view text) { integer(static_cast<std::int64_t>(text.size())); for (unsigned char b : text) byte(b); }
    void text(const std::optional<std::string>& text) { if (text) this->text(std::string_view(*text)); else integer(-1); }
    void nullable(std::optional<std::int64_t> number) { byte(number.has_value()); if (number) integer(*number); }
};
struct Settings { std::string channel; bool gift = false; };
struct SettingsSax final : nlohmann::json_sax<Json>
{
    Settings value; int key_kind = 0, objects = 0; bool channel = false, gift = false;
    bool null() override { return false; }
    bool boolean(bool v) override { if (key_kind != 2) return false; value.gift = v; gift = true; return true; }
    bool number_integer(number_integer_t) override { return false; }
    bool number_unsigned(number_unsigned_t) override { return false; }
    bool number_float(number_float_t, const string_t&) override { return false; }
    bool string(string_t& v) override { if (key_kind != 1) return false; value.channel = std::move(v); channel = true; return true; }
    bool binary(binary_t&) override { return false; }
    bool start_object(std::size_t) override { return ++objects == 1; }
    bool key(string_t& key) override { key_kind = key == "Channel" ? 1 : key == "Gift" ? 2 : 0; return key_kind != 0; }
    bool end_object() override { return --objects == 0 && channel && gift; }
    bool start_array(std::size_t) override { return false; }
    bool end_array() override { return false; }
    bool parse_error(std::size_t, const std::string&, const nlohmann::detail::exception&) override { return false; }
};
static std::unique_ptr<Settings> settings(std::string_view bytes)
{
    SettingsSax sax;
    if (!Json::sax_parse(bytes, &sax) || !sax.channel || !sax.gift) throw std::runtime_error("typed Settings JSON differs from the fixed workload");
    return std::make_unique<Settings>(std::move(sax.value));
}
struct TextRow { std::int64_t id; std::optional<std::string> note; };
struct MoneyRow { std::int64_t id, cents; };
struct JsonRow { std::int64_t id; std::unique_ptr<Settings> settings; };
struct CustomerTotal { std::optional<std::int64_t> customer; std::int64_t count; std::optional<std::int64_t> cents; };
struct ProductTotal { std::string product; std::optional<std::int64_t> quantity; };
struct RankRow { std::int64_t id, rank; };
struct RunningRow { std::int64_t id; std::optional<std::int64_t> quantity; };
struct DetailRow { std::int64_t order; std::string name, product; std::int64_t quantity; };
struct LineRow { std::int64_t id, tenant, order; std::string product; std::int64_t quantity; };
struct WideRow
{
    std::int64_t tenant, id, created; std::optional<std::int64_t> customer;
    std::int64_t cents; std::optional<std::string> note; std::int64_t version;
    std::unique_ptr<Settings> settings; std::string memo;
};
struct ExpectedOrder
{
    std::int64_t tenant, id, created; std::optional<std::int64_t> customer;
    std::int64_t cents; std::optional<std::string> note; std::int64_t version;
    Settings settings; std::string memo;
};
static ExpectedOrder expected_order(const Json& value)
{
    return {value.at("TenantId"), value.at("Id"), value.at("CreatedAt"),
        value.at("CustomerId").is_null() ? std::nullopt : std::optional(value.at("CustomerId").get<std::int64_t>()),
        value.at("TotalCents"), value.at("Note").is_null() ? std::nullopt : std::optional(value.at("Note").get<std::string>()),
        value.at("Version"), {value.at("Settings").at("Channel"), value.at("Settings").at("Gift")}, value.at("InternalMemo")};
}
static bool equal_order(const WideRow& row, const ExpectedOrder& expected)
{
    return row.tenant == expected.tenant && row.id == expected.id && row.created == expected.created && row.customer == expected.customer
        && row.cents == expected.cents && row.note == expected.note && row.version == expected.version && row.memo == expected.memo
        && row.settings->channel == expected.settings.channel && row.settings->gift == expected.settings.gift;
}
static void hash_order(Digest& digest, const WideRow& row)
{
    digest.integer(row.tenant); digest.integer(row.id); digest.integer(row.created); digest.nullable(row.customer);
    digest.integer(row.cents); digest.text(row.note); digest.integer(row.version);
    digest.text(std::string_view(row.settings->channel)); digest.byte(row.settings->gift); digest.text(std::string_view(row.memo));
}
[[gnu::noinline]] static void consume(Digest& digest, std::unique_ptr<TextRow> row) { digest.integer(row->id); digest.text(row->note); }
[[gnu::noinline]] static void consume(Digest& digest, std::unique_ptr<MoneyRow> row) { digest.integer(row->id); digest.integer(row->cents); }
[[gnu::noinline]] static void consume(Digest& digest, std::unique_ptr<JsonRow> row)
{ digest.integer(row->id); digest.text(std::string_view(row->settings->channel)); digest.byte(row->settings->gift); }
[[gnu::noinline]] static void consume(Digest& digest, std::unique_ptr<WideRow> row)
{
    hash_order(digest, *row);
}
[[gnu::noinline]] static void consume(Digest& digest, std::unique_ptr<CustomerTotal> row)
{ digest.nullable(row->customer); digest.integer(row->count); digest.nullable(row->cents); }
[[gnu::noinline]] static void consume(Digest& digest, std::unique_ptr<ProductTotal> row)
{ digest.text(std::string_view(row->product)); digest.nullable(row->quantity); }
[[gnu::noinline]] static void consume(Digest& digest, std::unique_ptr<RankRow> row)
{ digest.integer(row->id); digest.integer(row->rank); }
[[gnu::noinline]] static void consume(Digest& digest, std::unique_ptr<RunningRow> row)
{ digest.integer(row->id); digest.nullable(row->quantity); }
[[gnu::noinline]] static void consume(Digest& digest, std::unique_ptr<DetailRow> row)
{ digest.integer(row->order); digest.text(std::string_view(row->name)); digest.text(std::string_view(row->product)); digest.integer(row->quantity); }
[[gnu::noinline]] static void consume(Digest& digest, const LineRow& row)
{ digest.integer(row.id); digest.integer(row.tenant); digest.integer(row.order); digest.text(std::string_view(row.product)); digest.integer(row.quantity); }
enum class Kind { Numeric, Text, Money, Json, Wide, CustomerTotal, ProductTotal, NullableMoney, Rank, Running, Detail, Related, Transaction, Command };
enum class ParameterKind { Int64, Int32, String };
struct Parameter { ParameterKind kind; std::int64_t integer = 0; std::string text; bool is_null = false; };
struct SqlToken
{
    enum class Kind { Literal, Identifier, Parameter } kind;
    std::string text;
    std::size_t parameter = 0;
};
struct Query
{
    std::string name, sql; Kind kind;
    std::vector<std::string> columns, aliases;
    std::vector<Parameter> parameters;
    std::vector<Oid> parameter_types;
    std::vector<SqlToken> recipe;
    std::vector<std::int64_t> parent_ids;
    std::vector<Query> steps;
    std::optional<ExpectedOrder> expected_order;
    std::optional<std::int64_t> expected_affected;
    std::size_t cache_slot = 0;
    std::uint64_t stop_after = 0;
    bool active_transaction_after = false, hash_conflict = false, reconstruct = false;
    bool core = false;
    std::int64_t tenant = 1, take = 32, expected_rows = 32;
    std::uint64_t expected_digest = 0;
};
static void parameters(Query& query, const Json& values)
{
    if (values.size() > 12288) throw std::runtime_error("parameter contract exceeds the bounded native consumer");
    for (const auto& parameter : values)
    {
        Parameter value; std::string type = parameter.at("kind");
        value.kind = type == "int64" || type == "nullable-int64" ? ParameterKind::Int64
            : type == "int32" ? ParameterKind::Int32 : type == "string" || type == "nullable-string" ? ParameterKind::String
            : throw std::runtime_error("unsupported native parameter type: " + type);
        value.is_null = parameter.at("value").is_null();
        if (!value.is_null)
        {
            if (value.kind == ParameterKind::String) value.text = parameter.at("value");
            else value.integer = parameter.at("value");
        }
        if (value.kind == ParameterKind::Int32 && (value.integer < INT32_MIN || value.integer > INT32_MAX))
            throw std::runtime_error("int32 parameter overflow");
        query.parameter_types.push_back(value.kind == ParameterKind::Int32 ? 23 : value.kind == ParameterKind::Int64 ? 20 : 25);
        query.parameters.push_back(std::move(value));
    }
}
static std::vector<SqlToken> sql_recipe(const std::string& sql, bool postgres)
{
    std::vector<SqlToken> tokens;
    std::size_t next_parameter = 1;
    for (std::size_t position = 0; position < sql.size();)
    {
        auto start = position;
        if (sql[position] == '"')
        {
            std::string identifier; ++position; bool closed = false;
            while (position < sql.size())
            {
                char value = sql[position++];
                if (value == '"')
                {
                    if (position < sql.size() && sql[position] == '"') { ++position; identifier += '"'; }
                    else { closed = true; break; }
                }
                else identifier += value;
            }
            if (!closed) throw std::runtime_error("unterminated SQL identifier in manifest");
            tokens.push_back({SqlToken::Kind::Identifier, std::move(identifier), 0});
        }
        else if (sql[position] == '\'')
        {
            ++position; bool closed = false;
            while (position < sql.size())
                if (sql[position++] == '\'')
                {
                    if (position < sql.size() && sql[position] == '\'') ++position;
                    else { closed = true; break; }
                }
            if (!closed) throw std::runtime_error("unterminated SQL string in manifest");
            tokens.push_back({SqlToken::Kind::Literal, sql.substr(start, position - start), 0});
        }
        else if ((!postgres && sql[position] == '?') || (postgres && sql[position] == '$'))
        {
            ++position; std::size_t index = next_parameter++;
            if (postgres)
            {
                auto first = position;
                while (position < sql.size() && std::isdigit(static_cast<unsigned char>(sql[position]))) ++position;
                if (first == position) throw std::runtime_error("unrecognized SQL dollar token");
                index = std::stoull(sql.substr(first, position - first));
            }
            tokens.push_back({SqlToken::Kind::Parameter, {}, index});
        }
        else
        {
            ++position;
            while (position < sql.size() && sql[position] != '"' && sql[position] != '\'' && sql[position] != '?' && sql[position] != '$') ++position;
            tokens.push_back({SqlToken::Kind::Literal, sql.substr(start, position - start), 0});
        }
    }
    return tokens;
}
static std::string construct_sql(const Query& query, bool postgres)
{
    if (!query.core)
    {
        std::string sql;
        for (const auto& token : query.recipe)
        {
            if (token.kind == SqlToken::Kind::Literal) sql += token.text;
            else if (token.kind == SqlToken::Kind::Identifier)
            {
                sql += '"';
                for (char character : token.text) { sql += character; if (character == '"') sql += character; }
                sql += '"';
            }
            else if (postgres)
            {
                sql += '$'; std::array<char, 32> index{};
                auto converted = std::to_chars(index.data(), index.data() + index.size(), token.parameter);
                sql.append(index.data(), converted.ptr);
            }
            else sql += '?';
        }
        return sql;
    }
    std::string sql = "SELECT ";
    for (std::size_t i = 0; i < query.columns.size(); ++i)
    { if (i) sql += ", "; sql += "\"orders\".\"" + query.columns[i] + "\" AS \"" + query.aliases[i] + "\""; }
    sql += " FROM \"orders\" WHERE \"orders\".\"TenantId\" = "; sql += postgres ? "$1" : "?";
    sql += " ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT "; sql += postgres ? "$2" : "?"; return sql;
}
static std::vector<Query> queries(const Json& manifest, const Options& opt)
{
    if (manifest.at("schemaVersion") != 1 && manifest.at("schemaVersion") != 2 && manifest.at("schemaVersion") != 3) throw std::runtime_error("unsupported query manifest");
    std::vector<Query> result;
    std::size_t next_cache_slot = 0;
    const std::array<std::pair<std::string_view, Kind>, 35> names{{
        {"numeric", Kind::Numeric}, {"text", Kind::Text}, {"money", Kind::Money}, {"json", Kind::Json}, {"wide", Kind::Wide},
        {"optional-filter", Kind::Numeric}, {"range", Kind::Numeric}, {"left-join", Kind::Text}, {"inner-join", Kind::Text},
        {"null-notes", Kind::Numeric}, {"literal-note", Kind::Numeric}, {"customer-totals", Kind::CustomerTotal},
        {"product-totals", Kind::ProductTotal}, {"empty-sum", Kind::NullableMoney}, {"cursor", Kind::Numeric},
        {"offset", Kind::Numeric}, {"exists", Kind::Numeric}, {"in-subquery", Kind::Numeric}, {"cte", Kind::Numeric},
        {"window", Kind::Rank}, {"union-all", Kind::Numeric}, {"union", Kind::Numeric}, {"intersect", Kind::Numeric},
        {"except", Kind::Numeric}, {"running-total", Kind::Running}, {"three-join", Kind::Detail},
        {"anti-association", Kind::Numeric}, {"nullable-order", Kind::Numeric}, {"raw-sql", Kind::Text}, {"related", Kind::Related},
        {"tx-commit", Kind::Transaction}, {"tx-rollback", Kind::Transaction}, {"partial-update", Kind::Transaction},
        {"stale-conflict", Kind::Transaction}, {"early-stop", Kind::Wide}}};
    for (const auto& item : manifest.at("queries"))
    {
        std::string name = item.at("name"); auto found = std::find_if(names.begin(), names.end(), [&](const auto& value) { return value.first == name; });
        if (found == names.end() || std::any_of(result.begin(), result.end(), [&](const auto& q) { return q.name == name; }))
            throw std::runtime_error("unsupported or repeated query name: " + name);
        Query query; query.name = name; query.kind = found->second; query.sql = item.at("sql").at(opt.provider);
        query.core = found < names.begin() + 5 || name == "early-stop"; query.cache_slot = next_cache_slot++;
        for (const auto& column : item.at("columns")) { query.columns.push_back(column.at("name")); query.aliases.push_back(column.at("alias")); }
        parameters(query, item.at("parameters"));
        if (query.core)
        {
            if (query.parameters.size() != 2 || query.parameters[0].kind != ParameterKind::Int64 || query.parameters[1].kind != ParameterKind::Int64)
                throw std::runtime_error("core parameter contract differs");
            query.tenant = query.parameters[0].integer; query.take = query.parameters[1].integer;
        }
        query.recipe = sql_recipe(query.sql, opt.provider == "postgresql");
        if (item.contains("construction"))
        {
            std::string assembled;
            for (const auto& fragment : item.at("construction").at(opt.provider).at("fragments")) assembled += fragment.get<std::string>();
            if (assembled != query.sql) throw std::runtime_error("declared construction fragments differ from SQL");
        }
        if (query.kind == Kind::Related)
        {
            for (const auto& parent : item.at("related").at("parentKeys")) query.parent_ids.push_back(parent.at("id"));
            if (query.parent_ids.empty() || query.parent_ids.size() > 4096 || item.at("related").at("ordinalBase") != 0)
                throw std::runtime_error("related parent batch exceeds its bound or changes ordinal semantics");
        }
        if (query.kind == Kind::Transaction)
        {
            const auto& lifecycle = item.at("lifecycle");
            if (lifecycle.at("kind") != query.name) throw std::runtime_error("lifecycle identity differs");
            query.expected_order = expected_order(lifecycle.at("initial"));
            Query begin; begin.name = name + "/begin"; begin.kind = Kind::Command; begin.sql = lifecycle.at("begin").at(opt.provider);
            begin.recipe = sql_recipe(begin.sql, opt.provider == "postgresql"); begin.cache_slot = next_cache_slot++;
            begin.reconstruct = true; begin.active_transaction_after = true; begin.expected_rows = 0;
            query.steps.push_back(std::move(begin)); bool active = true;
            for (const auto& source : lifecycle.at("steps"))
            {
                Query step; step.name = name + "/" + source.at("name").get<std::string>();
                step.kind = source.contains("expectedRows") ? Kind::Wide : Kind::Command;
                step.sql = source.at("sql").at(opt.provider); step.recipe = sql_recipe(step.sql, opt.provider == "postgresql");
                step.cache_slot = next_cache_slot++; step.reconstruct = true; parameters(step, source.at("parameters"));
                step.expected_rows = source.value("expectedRows", 0);
                if (source.contains("expectedAffected")) step.expected_affected = source.at("expectedAffected");
                step.hash_conflict = source.value("hash", "") == "boolean true";
                if (step.sql == "COMMIT" || step.sql == "ROLLBACK")
                { if (!active) throw std::runtime_error("lifecycle contains repeated transaction completion"); active = false; }
                step.active_transaction_after = active;
                if (step.kind == Kind::Wide)
                {
                    if (step.expected_rows != 1 || source.at("hash") != "full-order") throw std::runtime_error("unexpected transaction read contract");
                    step.columns = query.columns; step.aliases = query.aliases; auto expected = lifecycle.at("initial");
                    if (source.contains("changedFields")) expected.update(source.at("changedFields"));
                    step.expected_order = expected_order(expected);
                }
                else if (step.hash_conflict && step.expected_affected != 0) throw std::runtime_error("conflict contract requires zero affected rows");
                if (construct_sql(step, opt.provider == "postgresql") != step.sql) throw std::runtime_error("lifecycle SQL reconstruction differs");
                query.steps.push_back(std::move(step));
            }
            if (active || query.steps.size() != item.at("queryExecutionsPerOperation").get<std::size_t>() || query.steps.size() > 8)
                throw std::runtime_error("lifecycle completion or statement count differs");
        }
        else if (name == "early-stop")
        {
            query.stop_after = item.at("consumeRows");
            if (query.stop_after != 3 || item.at("lifecycle").at("consumeRows") != query.stop_after || query.take <= 3)
                throw std::runtime_error("early-stop consumption contract differs");
        }
        query.expected_rows = item.at("expectedRows"); query.expected_digest = std::stoull(item.at("expectedDigest").get<std::string>());
        if (query.expected_rows < 0 || query.expected_rows > 1000000 || query.take < 1 || query.take > 1000000 || query.sql != construct_sql(query, opt.provider == "postgresql"))
            throw std::runtime_error("SQL construction or row bound differs from the shared contract");
        result.push_back(std::move(query));
    }
    if (result.empty() || result.size() > names.size() || (opt.selected != "mixed" && std::none_of(result.begin(), result.end(), [&](const auto& q) { return q.name == opt.selected; })))
        throw std::runtime_error("case list differs");
    return result;
}
struct SqliteReader
{
    sqlite3_stmt* statement;
    bool is_null(int column) const { return sqlite3_column_type(statement, column) == SQLITE_NULL; }
    std::int64_t integer(int column) const
    { if (sqlite3_column_type(statement, column) != SQLITE_INTEGER) throw std::runtime_error("SQLite integer type mismatch"); return sqlite3_column_int64(statement, column); }
    std::int32_t ordinal(int column) const
    { auto value = integer(column); if (value < 0 || value > INT32_MAX) throw std::runtime_error("SQLite ordinal overflow"); return static_cast<std::int32_t>(value); }
    std::optional<std::int64_t> nullable(int column) const { return sqlite3_column_type(statement, column) == SQLITE_NULL ? std::nullopt : std::optional(integer(column)); }
    std::optional<std::string> text(int column, bool nullable = true) const
    {
        int type = sqlite3_column_type(statement, column); if (type == SQLITE_NULL && nullable) return {};
        if (type != SQLITE_TEXT) throw std::runtime_error("SQLite text type mismatch");
        return std::string(reinterpret_cast<const char*>(sqlite3_column_text(statement, column)), sqlite3_column_bytes(statement, column));
    }
    std::int64_t money(int column) const { return integer(column); }
    std::optional<std::int64_t> nullable_money(int column) const { return is_null(column) ? std::nullopt : std::optional(money(column)); }
    std::unique_ptr<Settings> json(int column) const
    {
        if (sqlite3_column_type(statement, column) != SQLITE_TEXT) throw std::runtime_error("SQLite JSON type mismatch");
        return settings({reinterpret_cast<const char*>(sqlite3_column_text(statement, column)), static_cast<std::size_t>(sqlite3_column_bytes(statement, column))});
    }
};
static std::uint64_t network_unsigned(std::string_view bytes)
{ std::uint64_t value = 0; for (unsigned char byte : bytes) value = (value << 8) | byte; return value; }
struct PgReader
{
    PGresult* result;
    bool is_null(int column) const { return PQgetisnull(result, 0, column) != 0; }
    std::string_view bytes(int column) const
    { if (PQgetisnull(result, 0, column)) throw std::runtime_error("unexpected PostgreSQL NULL"); return {PQgetvalue(result, 0, column), static_cast<std::size_t>(PQgetlength(result, 0, column))}; }
    std::int64_t integer(int column) const
    { auto value = bytes(column); if (PQftype(result, column) != 20 || value.size() != 8) throw std::runtime_error("PostgreSQL int8 type mismatch"); return static_cast<std::int64_t>(network_unsigned(value)); }
    std::int32_t ordinal(int column) const
    { auto value = bytes(column); if (PQftype(result, column) != 23 || value.size() != 4) throw std::runtime_error("PostgreSQL int4 ordinal type mismatch"); return static_cast<std::int32_t>(network_unsigned(value)); }
    std::optional<std::int64_t> nullable(int column) const { return PQgetisnull(result, 0, column) ? std::nullopt : std::optional(integer(column)); }
    std::optional<std::string> text(int column, bool nullable = true) const
    {
        if (PQgetisnull(result, 0, column) && nullable) return {};
        if (PQftype(result, column) != 25) throw std::runtime_error("PostgreSQL text type mismatch"); return std::string(bytes(column));
    }
    std::int64_t money(int column) const
    {
        auto value = bytes(column);
        if (PQftype(result, column) != 1700 || value.size() < 8) throw std::runtime_error("PostgreSQL numeric type mismatch");
        int digits = static_cast<int>(network_unsigned(value.substr(0, 2)));
        int weight = static_cast<std::int16_t>(network_unsigned(value.substr(2, 2)));
        auto sign = network_unsigned(value.substr(4, 2)); auto scale = network_unsigned(value.substr(6, 2));
        if (value.size() != static_cast<std::size_t>(8 + digits * 2) || digits > 8 || weight < -8 || weight > 4 || scale > 2 || (sign != 0 && sign != 0x4000))
            throw std::runtime_error("numeric(18,2) encoding differs");
        __int128 cents = 0;
        for (int i = 0; i < digits; ++i)
        {
            auto digit = network_unsigned(value.substr(8 + i * 2, 2)); if (digit >= 10000) throw std::runtime_error("numeric base digit differs");
            int exponent = weight - i;
            if (exponent >= 0) { __int128 term = digit * 100; for (int p = 0; p < exponent; ++p) term *= 10000; cents += term; }
            else if (exponent == -1) { if (digit % 100) throw std::runtime_error("money conversion would round"); cents += digit / 100; }
            else if (digit) throw std::runtime_error("money conversion would round");
        }
        if (sign) cents = -cents;
        if (cents < std::numeric_limits<std::int64_t>::min() || cents > std::numeric_limits<std::int64_t>::max()) throw std::runtime_error("money overflow");
        return static_cast<std::int64_t>(cents);
    }
    std::optional<std::int64_t> nullable_money(int column) const { return is_null(column) ? std::nullopt : std::optional(money(column)); }
    std::unique_ptr<Settings> json(int column) const
    {
        auto value = bytes(column);
        if (PQftype(result, column) != 3802 || value.empty() || value[0] != 1) throw std::runtime_error("PostgreSQL JSONB encoding differs");
        return settings(value.substr(1));
    }
};
template<class Reader> static std::unique_ptr<WideRow> read_order(const Reader& row)
{
    return std::make_unique<WideRow>(WideRow{row.integer(0), row.integer(1), row.integer(2), row.nullable(3),
        row.money(4), row.text(5), row.integer(6), row.json(7), *row.text(8, false)});
}
template<class Reader> static void materialize(Kind kind, const Reader& row, Digest& digest)
{
    switch (kind)
    {
    case Kind::Numeric: digest.integer(row.integer(0)); break;
    case Kind::Text: consume(digest, std::make_unique<TextRow>(TextRow{row.integer(0), row.text(1)})); break;
    case Kind::Money: consume(digest, std::make_unique<MoneyRow>(MoneyRow{row.integer(0), row.money(1)})); break;
    case Kind::Json: consume(digest, std::make_unique<JsonRow>(JsonRow{row.integer(0), row.json(1)})); break;
    case Kind::Wide: consume(digest, read_order(row)); break;
    case Kind::CustomerTotal: consume(digest, std::make_unique<CustomerTotal>(CustomerTotal{row.nullable(0), row.integer(1), row.nullable_money(2)})); break;
    case Kind::ProductTotal: consume(digest, std::make_unique<ProductTotal>(ProductTotal{*row.text(0, false), row.nullable(1)})); break;
    case Kind::NullableMoney: digest.nullable(row.nullable_money(0)); break;
    case Kind::Rank: consume(digest, std::make_unique<RankRow>(RankRow{row.integer(0), row.integer(1)})); break;
    case Kind::Running: consume(digest, std::make_unique<RunningRow>(RunningRow{row.integer(0), row.nullable(1)})); break;
    case Kind::Detail: consume(digest, std::make_unique<DetailRow>(DetailRow{row.integer(0), *row.text(1, false), *row.text(2, false), row.integer(3)})); break;
    case Kind::Related: throw std::runtime_error("related rows require buffered grouping");
    case Kind::Transaction: case Kind::Command: throw std::runtime_error("commands require the transaction operation contract");
    }
}
struct Materializer
{
    const Query& query;
    Digest digest;
    std::uint64_t rows = 0;
    std::vector<std::vector<std::unique_ptr<LineRow>>> groups;
    explicit Materializer(const Query& shape) : query(shape), groups(shape.parent_ids.size()) { }
    template<class Reader> void row(const Reader& source)
    {
        if (query.kind == Kind::Related)
        {
            auto ordinal = source.ordinal(5);
            if (ordinal < 0 || static_cast<std::size_t>(ordinal) >= groups.size()) throw std::runtime_error("related parent ordinal outside requested keys");
            groups[ordinal].push_back(std::make_unique<LineRow>(LineRow{source.integer(0), source.integer(1), source.integer(2), *source.text(3, false), source.integer(4)}));
        }
        else materialize(query.kind, source, digest);
        if (++rows > static_cast<std::uint64_t>(query.expected_rows)) throw std::runtime_error("row count exceeds the declared bounded result");
    }
    std::uint64_t finish()
    {
        if (query.kind == Kind::Related)
            for (std::size_t parent = 0; parent < groups.size(); ++parent)
            {
                auto& group = groups[parent];
                std::sort(group.begin(), group.end(), [](const auto& left, const auto& right) { return left->id < right->id; });
                digest.integer(query.parent_ids[parent]); digest.integer(static_cast<std::int64_t>(group.size()));
                for (const auto& child : group) consume(digest, *child);
            }
        if (rows != static_cast<std::uint64_t>(query.expected_rows) || digest.value != query.expected_digest)
            throw std::runtime_error("rows/digest differ for " + query.name);
        return rows;
    }
};
struct SqliteStatement
{
    sqlite3_stmt* value = nullptr;
    explicit SqliteStatement(sqlite3* db, const std::string& sql)
    {
        const char* tail = nullptr;
        if (sqlite3_prepare_v3(db, sql.c_str(), static_cast<int>(sql.size()), SQLITE_PREPARE_PERSISTENT, &value, &tail) != SQLITE_OK)
        { if (value) sqlite3_finalize(value); value = nullptr; throw std::runtime_error(sqlite3_errmsg(db)); }
        if (!value) throw std::runtime_error("empty SQLite statement"); ++live_sqlite_statements;
    }
    ~SqliteStatement() { if (value) { sqlite3_finalize(value); --live_sqlite_statements; } }
};
struct PgResult
{
    PGresult* value = nullptr;
    explicit PgResult(PGresult* p) : value(p) { if (value) ++live_pg_results; }
    ~PgResult() { if (value) { PQclear(value); --live_pg_results; } }
};
struct PgObserver
{
    PGconn* value;
    explicit PgObserver(PGconn* connection) : value(connection) { if (value) ++live_connections; }
    ~PgObserver() { if (value) { PQfinish(value); --live_connections; } }
};
struct PgCancellation
{
    PGcancelConn* value;
    explicit PgCancellation(PGcancelConn* cancel) : value(cancel) { if (value) ++live_cancel_owners; }
    ~PgCancellation() { if (value) { PQcancelFinish(value); --live_cancel_owners; } }
};
struct Counters { std::uint64_t executions = 0, submissions = 0, prepare = 0, describe = 0, confirmations = 0, waits = 0, unmaterialized_rows = 0; };
struct ConcurrencyConflict final : std::runtime_error
{
    ConcurrencyConflict() : std::runtime_error("optimistic concurrency check affected zero rows") { }
};
class Connection
{
    const Options& opt_;
    sqlite3* sqlite_ = nullptr;
    PGconn* pg_ = nullptr;
    Clock::time_point deadline_;
    std::vector<std::unique_ptr<SqliteStatement>> cached_;
    std::vector<std::string> names_;
    std::vector<bool> prepared_;
    std::vector<std::array<char, 32>> parameter_numbers_;
    std::vector<const char*> parameter_values_;
    bool counted_ = false;
    static int progress(void* context) { return Clock::now() >= static_cast<Connection*>(context)->deadline_; }
    void fail_pg() const { throw std::runtime_error(PQerrorMessage(pg_)); }
    void wait(short events)
    {
        if (Clock::now() >= deadline_) throw std::runtime_error("PostgreSQL operation deadline elapsed");
        pollfd item{PQsocket(pg_), events, 0}; ++counts.waits;
        int result = poll(&item, 1, 1000);
        if (result < 0 && errno != EINTR) throw std::runtime_error("PostgreSQL poll failed");
        if (result > 0 && (item.revents & (POLLERR | POLLHUP | POLLNVAL))) fail_pg();
    }
    void flush()
    { while (true) { int status = PQflush(pg_); if (status == 0) return; if (status < 0) fail_pg(); wait(POLLOUT); } }
    PGresult* receive()
    {
        flush();
        while (PQisBusy(pg_)) { if (!PQconsumeInput(pg_)) fail_pg(); if (PQisBusy(pg_)) wait(POLLIN); }
        return PQgetResult(pg_);
    }
    void command_result()
    {
        while (auto* raw = receive())
        {
            PgResult result(raw); if (PQresultStatus(raw) != PGRES_COMMAND_OK) throw std::runtime_error(PQresultErrorMessage(raw));
        }
    }
    void confirm_protocol()
    {
        if (opt_.confirmation == "drain-only") return;
        if (PQstatus(pg_) != CONNECTION_OK || PQpipelineStatus(pg_) != PQ_PIPELINE_OFF || !PQenterPipelineMode(pg_) || !PQpipelineSync(pg_)) fail_pg();
        ++counts.submissions; ++counts.confirmations;
        {
            PgResult sync(receive());
            if (!sync.value || PQresultStatus(sync.value) != PGRES_PIPELINE_SYNC) throw std::runtime_error("protocol confirmation missing ReadyForQuery");
        }
        { PgResult extra(receive()); if (extra.value) throw std::runtime_error("protocol confirmation returned an unexpected result"); }
        if (!PQexitPipelineMode(pg_) || PQpipelineStatus(pg_) != PQ_PIPELINE_OFF) fail_pg();
    }
    void prepare_pg(std::size_t index, const Query& query, const std::string& sql)
    {
        const char* name = opt_.physical == "reuse" ? names_[index].c_str() : "";
        if (!PQsendPrepare(pg_, name, sql.c_str(), static_cast<int>(query.parameter_types.size()), query.parameter_types.data())) fail_pg();
        ++counts.prepare; ++counts.submissions; command_result();
        if (!PQsendDescribePrepared(pg_, name)) fail_pg(); ++counts.describe; ++counts.submissions;
        while (auto* raw = receive())
        {
            PgResult result(raw);
            if (PQresultStatus(raw) != PGRES_COMMAND_OK || PQnfields(raw) != static_cast<int>(query.aliases.size()) || PQnparams(raw) != static_cast<int>(query.parameters.size()))
                throw std::runtime_error("PostgreSQL prepared description differs");
            for (int column = 0; column < PQnfields(raw); ++column)
                if (query.aliases[column] != PQfname(raw, column)) throw std::runtime_error("PostgreSQL alias differs");
        }
        prepared_[index] = true;
    }
    void bind_sqlite(sqlite3_stmt* statement, const Query& query)
    {
        for (std::size_t index = 0; index < query.parameters.size(); ++index)
        {
            const auto& value = query.parameters[index]; int position = static_cast<int>(index + 1), status;
            if (value.is_null) status = sqlite3_bind_null(statement, position);
            else if (value.kind == ParameterKind::String) status = sqlite3_bind_text64(statement, position, value.text.data(), value.text.size(), SQLITE_TRANSIENT, SQLITE_UTF8);
            else status = sqlite3_bind_int64(statement, position, value.integer);
            if (status != SQLITE_OK) throw std::runtime_error(sqlite3_errmsg(sqlite_));
        }
    }
    void bind_postgres(const Query& query)
    {
        for (std::size_t parameter = 0; parameter < query.parameters.size(); ++parameter)
        {
            const auto& value = query.parameters[parameter];
            if (value.is_null) parameter_values_[parameter] = nullptr;
            else if (value.kind == ParameterKind::String) parameter_values_[parameter] = value.text.c_str();
            else
            {
                auto& buffer = parameter_numbers_[parameter];
                auto converted = std::to_chars(buffer.data(), buffer.data() + buffer.size() - 1, value.integer); *converted.ptr = '\0';
                parameter_values_[parameter] = buffer.data();
            }
        }
    }
public:
    Counters counts;
    Json configuration;
    explicit Connection(const Options& opt, std::size_t query_count, std::size_t max_parameters) : opt_(opt), cached_(query_count), names_(query_count),
        prepared_(query_count), parameter_numbers_(max_parameters), parameter_values_(max_parameters)
    {
        try
        {
            deadline_ = Clock::now() + std::chrono::seconds(30);
            if (opt.provider == "sqlite")
            {
                if (sqlite3_open_v2(opt.connection.c_str(), &sqlite_, SQLITE_OPEN_READWRITE | SQLITE_OPEN_FULLMUTEX | SQLITE_OPEN_PRIVATECACHE, nullptr) != SQLITE_OK)
                    throw std::runtime_error(sqlite_ ? sqlite3_errmsg(sqlite_) : "SQLite open failed");
                sqlite3_extended_result_codes(sqlite_, 1); sqlite3_busy_timeout(sqlite_, 5000); sqlite3_progress_handler(sqlite_, 1000, progress, this);
                char* error = nullptr;
                if (sqlite3_exec(sqlite_, "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;", nullptr, nullptr, &error) != SQLITE_OK)
                { std::string message = error ? error : "SQLite configuration failed"; sqlite3_free(error); throw std::runtime_error(message); }
                configuration = {{"journal_mode", "wal"}, {"synchronous", "FULL"}, {"foreign_keys", true}, {"busy_timeout_ms", 5000}, {"open", "readwrite/fullmutex/privatecache"}};
            }
            else
            {
                const char* keywords[]{"dbname", "sslmode", "connect_timeout", "client_encoding", nullptr};
                const char* values[]{opt.connection.c_str(), "disable", "30", "UTF8", nullptr};
                pg_ = PQconnectdbParams(keywords, values, 1);
                if (!pg_ || PQstatus(pg_) != CONNECTION_OK) throw std::runtime_error(pg_ ? PQerrorMessage(pg_) : "PostgreSQL open failed");
                { PgResult setup(PQexec(pg_, "SET statement_timeout TO 30000")); if (!setup.value || PQresultStatus(setup.value) != PGRES_COMMAND_OK) fail_pg(); }
                if (PQsetnonblocking(pg_, 1)) fail_pg();
                configuration = {{"tls", "disabled-for-local-development"}, {"serverVersion", PQserverVersion(pg_)}, {"clientEncoding", PQparameterStatus(pg_, "client_encoding")}, {"statementTimeoutMs", 30000}};
            }
            for (std::size_t i = 0; i < query_count; ++i) names_[i] = "db_perf_" + std::to_string(i);
            ++live_connections; counted_ = true;
        }
        catch (...) { close(); throw; }
    }
    ~Connection() { close(); }
    void close()
    {
        cached_.clear();
        if (sqlite_) { if (sqlite3_close(sqlite_) != SQLITE_OK) std::terminate(); sqlite_ = nullptr; }
        if (pg_) { PQfinish(pg_); pg_ = nullptr; }
        if (counted_) { --live_connections; counted_ = false; }
    }
    void verify_dataset(std::int64_t tenants, std::int64_t orders)
    {
        std::int64_t actual_tenants = 0, actual_orders = 0;
        const std::string sql = "SELECT tenants,orders_per_tenant FROM db_perf_metadata WHERE schema_version=1";
        if (sqlite_)
        {
            SqliteStatement query(sqlite_, sql);
            if (sqlite3_step(query.value) != SQLITE_ROW) throw std::runtime_error("dataset metadata absent");
            actual_tenants = sqlite3_column_int64(query.value, 0); actual_orders = sqlite3_column_int64(query.value, 1);
            if (sqlite3_step(query.value) != SQLITE_DONE) throw std::runtime_error("dataset metadata ambiguous");
        }
        else
        {
            PgResult result(PQexec(pg_, sql.c_str()));
            if (!result.value || PQresultStatus(result.value) != PGRES_TUPLES_OK || PQntuples(result.value) != 1) throw std::runtime_error("dataset metadata absent or ambiguous");
            actual_tenants = std::stoll(PQgetvalue(result.value, 0, 0)); actual_orders = std::stoll(PQgetvalue(result.value, 0, 1));
        }
        if (actual_tenants != tenants || actual_orders != orders) throw std::runtime_error("dataset metadata differs from manifest");
    }
    Json explain(const Query& query)
    {
        deadline_ = Clock::now() + std::chrono::seconds(30);
        if (sqlite_)
        {
            SqliteStatement statement(sqlite_, "EXPLAIN QUERY PLAN " + query.sql); bind_sqlite(statement.value, query);
            Json rows = Json::array(); int status;
            while ((status = sqlite3_step(statement.value)) == SQLITE_ROW)
                rows.push_back({{"id", sqlite3_column_int64(statement.value, 0)}, {"parent", sqlite3_column_int64(statement.value, 1)},
                    {"auxiliary", sqlite3_column_int64(statement.value, 2)}, {"detail", reinterpret_cast<const char*>(sqlite3_column_text(statement.value, 3))}});
            if (status != SQLITE_DONE) throw std::runtime_error(sqlite3_errmsg(sqlite_));
            return rows;
        }
        bind_postgres(query); auto sql = "EXPLAIN (FORMAT JSON, ANALYZE FALSE, COSTS TRUE) " + query.sql;
        if (!PQsendQueryParams(pg_, sql.c_str(), static_cast<int>(query.parameters.size()), query.parameter_types.data(), parameter_values_.data(), nullptr, nullptr, 0)) fail_pg();
        Json plan; bool received = false;
        while (auto* raw = receive())
        {
            PgResult result(raw);
            if (received || PQresultStatus(raw) != PGRES_TUPLES_OK || PQntuples(raw) != 1 || PQnfields(raw) != 1)
                throw std::runtime_error("PostgreSQL EXPLAIN result differs");
            plan = Json::parse(PQgetvalue(raw, 0, 0)); received = true;
        }
        if (!received || PQtransactionStatus(pg_) != PQTRANS_IDLE) throw std::runtime_error("PostgreSQL EXPLAIN did not drain");
        return plan;
    }
    Json active_cancel_check()
    {
        deadline_ = Clock::now() + std::chrono::seconds(30);
        const auto began = monotonic_ns();
        if (sqlite_)
        {
            struct Progress { std::atomic<bool> entered = false, done = false; std::atomic<std::uint64_t> callbacks = 0, interrupted_at = 0; } state;
            SqliteStatement statement(sqlite_, "WITH RECURSIVE numbers(n) AS (VALUES(0) UNION ALL SELECT n+1 FROM numbers WHERE n<100000000) SELECT sum(n) FROM numbers");
            sqlite3_progress_handler(sqlite_, 1000, [](void* value)
            { auto& state = *static_cast<Progress*>(value); ++state.callbacks; state.entered.store(true, std::memory_order_release); return 0; }, &state);
            std::thread interrupter([&]
            {
                while (!state.entered.load(std::memory_order_acquire) && !state.done.load(std::memory_order_acquire)) std::this_thread::yield();
                if (state.entered.load(std::memory_order_acquire))
                { state.interrupted_at = monotonic_ns(); sqlite3_interrupt(sqlite_); }
            });
            ++counts.executions; int status = sqlite3_step(statement.value);
            state.done.store(true, std::memory_order_release); interrupter.join();
            sqlite3_progress_handler(sqlite_, 1000, progress, this);
            int reset = sqlite3_reset(statement.value);
            bool passed = state.entered && state.callbacks > 0 && state.interrupted_at > began && status == SQLITE_INTERRUPT
                && reset == SQLITE_INTERRUPT && sqlite3_get_autocommit(sqlite_) != 0;
            return {{"name", "active-engine-cancellation"}, {"passed", passed}, {"provider", "sqlite"},
                {"engineProgressCallbacks", state.callbacks.load()}, {"startedMonotonicNs", began}, {"cancelIssuedMonotonicNs", state.interrupted_at.load()},
                {"completedMonotonicNs", monotonic_ns()}, {"stepStatus", status}, {"resetStatus", reset},
                {"evidence", "sqlite3_interrupt issued from another thread after the executing statement entered a progress callback"}};
        }
        if (!PQsendQuery(pg_, "SELECT pg_sleep(30)")) fail_pg(); ++counts.executions; ++counts.submissions; flush();
        const char* keywords[]{"dbname", "sslmode", "connect_timeout", "client_encoding", nullptr};
        const char* values[]{opt_.connection.c_str(), "disable", "5", "UTF8", nullptr};
        PgObserver observer(PQconnectdbParams(keywords, values, 1));
        if (!observer.value || PQstatus(observer.value) != CONNECTION_OK) throw std::runtime_error("cancellation observer could not connect");
        const auto sql = "SELECT state,wait_event FROM pg_stat_activity WHERE pid=" + std::to_string(PQbackendPID(pg_));
        bool running = false; std::uint64_t observations = 0; auto observe_deadline = Clock::now() + std::chrono::seconds(5);
        while (!running && Clock::now() < observe_deadline)
        {
            PgResult result(PQexec(observer.value, sql.c_str())); ++observations;
            if (!result.value || PQresultStatus(result.value) != PGRES_TUPLES_OK) throw std::runtime_error("cancellation observer query failed");
            running = PQntuples(result.value) == 1 && !PQgetisnull(result.value, 0, 1)
                && std::string_view(PQgetvalue(result.value, 0, 0)) == "active" && std::string_view(PQgetvalue(result.value, 0, 1)) == "PgSleep";
            if (!running) std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
        const auto issued = monotonic_ns(); PgCancellation cancellation(PQcancelCreate(pg_));
        if (!cancellation.value || !PQcancelBlocking(cancellation.value)) throw std::runtime_error("PostgreSQL cancellation request failed");
        std::string state; bool canceled = false;
        while (auto* raw = receive())
        {
            PgResult result(raw); const char* code = PQresultErrorField(raw, PG_DIAG_SQLSTATE);
            if (code) state = code;
            if (PQresultStatus(raw) == PGRES_FATAL_ERROR && state == "57014") canceled = true;
            else throw std::runtime_error("PostgreSQL canceled statement returned an unexpected result");
        }
        bool idle = PQtransactionStatus(pg_) == PQTRANS_IDLE;
        return {{"name", "active-engine-cancellation"}, {"passed", running && canceled && idle}, {"provider", "postgresql"},
            {"serverObservedActivePgSleep", running}, {"controlSqlObservations", observations}, {"sqlState", state}, {"transactionIdle", idle},
            {"startedMonotonicNs", began}, {"cancelIssuedMonotonicNs", issued}, {"completedMonotonicNs", monotonic_ns()},
            {"evidence", "independent pg_stat_activity observed this backend active in PgSleep before PQcancelBlocking; SQLSTATE57014 drained through final null"}};
    }
private:
    template<class OnRow> std::uint64_t statement(const Query& query, OnRow&& on_row)
    {
        std::string dynamic_sql;
        bool construct = opt_.mode == "dynamic" || query.kind == Kind::Related || query.reconstruct;
        if (construct) dynamic_sql = construct_sql(query, pg_ != nullptr);
        const auto& sql = construct ? dynamic_sql : query.sql;
        const auto index = query.cache_slot; std::uint64_t materialized = 0;
        if (sqlite_)
        {
            std::unique_ptr<SqliteStatement> temporary;
            if (opt_.physical == "reuse")
            { if (!cached_[index]) { cached_[index] = std::make_unique<SqliteStatement>(sqlite_, sql); ++counts.prepare; } }
            else { temporary = std::make_unique<SqliteStatement>(sqlite_, sql); ++counts.prepare; }
            auto* statement = opt_.physical == "reuse" ? cached_[index]->value : temporary->value;
            if (sqlite3_column_count(statement) != static_cast<int>(query.aliases.size()) || sqlite3_bind_parameter_count(statement) != static_cast<int>(query.parameters.size()))
                throw std::runtime_error("SQLite result or parameter shape differs");
            for (int column = 0; column < sqlite3_column_count(statement); ++column)
                if (query.aliases[column] != sqlite3_column_name(statement, column)) throw std::runtime_error("SQLite alias differs");
            bind_sqlite(statement, query);
            ++counts.executions;
            int status; bool early = false;
            while ((status = sqlite3_step(statement)) == SQLITE_ROW)
            {
                if (query.kind == Kind::Command) throw std::runtime_error("SQLite command unexpectedly returned a row");
                on_row(SqliteReader{statement}); ++materialized;
                if (query.stop_after && materialized == query.stop_after) { early = true; break; }
            }
            if (!early && status != SQLITE_DONE) throw std::runtime_error(sqlite3_errmsg(sqlite_));
            if (query.expected_affected && sqlite3_changes64(sqlite_) != *query.expected_affected)
                throw std::runtime_error("SQLite affected row count differs for " + query.name);
            if (sqlite3_reset(statement) != SQLITE_OK || sqlite3_clear_bindings(statement) != SQLITE_OK) throw std::runtime_error("SQLite reset failed");
            if ((sqlite3_get_autocommit(sqlite_) == 0) != query.active_transaction_after)
                throw std::runtime_error("SQLite transaction state differs after " + query.name);
        }
        else
        {
            if (opt_.physical != "reuse" || !prepared_[index]) prepare_pg(index, query, sql);
            confirm_protocol();
            bind_postgres(query);
            const char* name = opt_.physical == "reuse" ? names_[index].c_str() : "";
            if (!PQsendQueryPrepared(pg_, name, static_cast<int>(query.parameters.size()), parameter_values_.data(), nullptr, nullptr, 1)) fail_pg();
            ++counts.executions; ++counts.submissions;
            if (!PQsetSingleRowMode(pg_)) throw std::runtime_error("single-row mode was rejected");
            bool terminal = false;
            while (auto* raw = receive())
            {
                PgResult result(raw); auto status = PQresultStatus(raw);
                if (status == PGRES_SINGLE_TUPLE)
                {
                    if (terminal || query.kind == Kind::Command || PQntuples(raw) != 1 || PQnfields(raw) != static_cast<int>(query.aliases.size()))
                        throw std::runtime_error("single-row result shape differs");
                    if (!query.stop_after || materialized < query.stop_after) { on_row(PgReader{raw}); ++materialized; }
                    else ++counts.unmaterialized_rows;
                }
                else if (status == PGRES_TUPLES_OK && query.kind != Kind::Command && PQntuples(raw) == 0 && !terminal) terminal = true;
                else if (status == PGRES_COMMAND_OK && query.kind == Kind::Command && !terminal)
                {
                    terminal = true;
                    if (query.expected_affected)
                    {
                        std::string_view digits(PQcmdTuples(raw)); std::int64_t affected = -1;
                        auto parsed = std::from_chars(digits.data(), digits.data() + digits.size(), affected);
                        if (parsed.ec != std::errc() || parsed.ptr != digits.data() + digits.size() || affected != *query.expected_affected)
                            throw std::runtime_error("PostgreSQL affected row count differs for " + query.name);
                    }
                }
                else throw std::runtime_error(PQresultErrorMessage(raw));
            }
            if (!terminal || PQtransactionStatus(pg_) != (query.active_transaction_after ? PQTRANS_INTRANS : PQTRANS_IDLE))
                throw std::runtime_error("PostgreSQL transaction state or terminal drain differs after " + query.name);
            confirm_protocol();
        }
        return materialized;
    }
public:
    std::uint64_t execute(std::size_t, const Query& query)
    {
        deadline_ = Clock::now() + std::chrono::seconds(30);
        try
        {
            if (query.kind != Kind::Transaction)
            {
                Materializer materializer(query);
                statement(query, [&](const auto& row) { materializer.row(row); });
                return materializer.finish();
            }
            Digest digest; std::array<std::unique_ptr<WideRow>, 3> owned_orders{}; std::uint64_t rows = 0;
            for (const auto& step : query.steps)
            {
                auto step_rows = statement(step, [&](const auto& source)
                {
                    if (!step.expected_order || rows >= owned_orders.size()) throw std::runtime_error("transaction returned an unexpected row");
                    auto owned = read_order(source);
                    if (!equal_order(*owned, *step.expected_order)) throw std::runtime_error("transaction changed an unexpected field at " + step.name);
                    hash_order(digest, *owned); owned_orders[rows++] = std::move(owned);
                });
                if (step_rows != static_cast<std::uint64_t>(step.expected_rows)) throw std::runtime_error("transaction step row count differs at " + step.name);
                if (step.hash_conflict)
                {
                    // Match the business-observable checked-update exception
                    // path, including construction and catch, in every route.
                    bool caught = false;
                    try { throw ConcurrencyConflict(); }
                    catch (const ConcurrencyConflict&) { caught = true; }
                    digest.byte(caught);
                }
            }
            if (rows != static_cast<std::uint64_t>(query.expected_rows) || digest.value != query.expected_digest)
                throw std::runtime_error("transaction rows/digest differ for " + query.name);
            return rows;
        }
        catch (...)
        {
            // A failed validation or driver result must release an active write
            // transaction promptly. Closing the owner rolls it back; the caller
            // then marks this worker unavailable and never silently retries.
            close(); throw;
        }
    }
};
struct Second { std::uint64_t planned = 0, sent = 0, started = 0, completed = 0, failed = 0, rejected = 0, rows = 0; };
struct CaseCounts { std::uint64_t started = 0, succeeded = 0, failed = 0, rows = 0; };
struct Worker
{
    std::unique_ptr<Connection> connection;
    std::array<std::uint64_t, 8192> histogram{};
    std::array<std::uint64_t, 8192> scheduled{}, all_scheduled{}, start_delay{};
    std::vector<Second> seconds;
    std::vector<CaseCounts> cases;
    std::uint64_t started = 0, completed = 0, failed = 0, poisoned_failures = 0, rows = 0, xor_digest = 0;
    std::string error;
    Counters before, after;
    bool truncated = false, poisoned = false;
};
static void add_latency(std::array<std::uint64_t, 8192>& histogram, std::uint64_t nanoseconds)
{
    auto elapsed = std::max<std::uint64_t>(1, nanoseconds);
    auto exponent = static_cast<unsigned>(std::bit_width(elapsed) - 1); auto lower = std::uint64_t{1} << exponent;
    auto fraction = (elapsed - lower) * 128 / lower; ++histogram[exponent * 128 + fraction];
}
static void wait_until_ns(std::uint64_t due)
{
    while (true)
    {
        auto now = monotonic_ns(); if (now >= due) return;
        // Match the managed arrival clock's 1 ms sleeps above a 2 ms margin,
        // then short processor pauses. Clock CPU remains visible in E2E and
        // separately observable with --empty-driver; it is never subtracted.
        if (due - now > 2000000) std::this_thread::sleep_for(std::chrono::milliseconds(1));
        else for (int spin = 0; spin < 32; ++spin) __builtin_ia32_pause();
    }
}
static double quantile(const std::array<std::uint64_t, 8192>& histogram, std::uint64_t total, double quantile)
{
    if (!total) return 0; auto desired = static_cast<std::uint64_t>(std::ceil(total * quantile)); std::uint64_t count = 0;
    for (std::size_t i = 0; i < histogram.size(); ++i)
    { count += histogram[i]; if (count >= desired) { double lower = std::ldexp(1., static_cast<int>(i / 128)); return (lower + lower * ((i % 128 + 1) / 128.)) / 1000.; } }
    throw std::runtime_error("latency histogram missing samples");
}
static Json count_json(Counters value) { return {{"sqlExecutions", value.executions}, {"protocolSubmissions", value.submissions}, {"physicalPrepareCalls", value.prepare}, {"describeCalls", value.describe}, {"confirmationSyncs", value.confirmations}, {"pollWaitCalls", value.waits}, {"unmaterializedRowsDrained", value.unmaterialized_rows}}; }
static Json owner_counts()
{ return {{"connections", live_connections.load()}, {"sqliteStatements", live_sqlite_statements.load()}, {"pgResults", live_pg_results.load()}, {"cancellationOwners", live_cancel_owners.load()}}; }
static Json run(const Options& opt, const Json& manifest, const std::vector<Query>& query, std::vector<std::unique_ptr<Worker>>* retained_workers = nullptr)
{
    AllocationProfile profile(opt.allocation_profile);
    std::vector<std::unique_ptr<Worker>> local_workers;
    auto& workers = retained_workers ? *retained_workers : local_workers;
    bool reused = !workers.empty();
    std::vector<std::size_t> active;
    if (!opt.cases.empty())
    {
        std::istringstream names(opt.cases); std::string name;
        while (std::getline(names, name, ','))
        {
            auto found = std::find_if(query.begin(), query.end(), [&](const auto& value) { return value.name == name; });
            if (found == query.end()) throw std::runtime_error("--cases contains an unsupported case: " + name);
            auto index = static_cast<std::size_t>(found - query.begin());
            if (std::find(active.begin(), active.end(), index) != active.end()) throw std::runtime_error("--cases contains duplicates");
            active.push_back(index);
        }
    }
    else for (std::size_t q = 0; q < query.size(); ++q) if (opt.selected == "mixed" || query[q].name == opt.selected) active.push_back(q);
    if (active.empty()) throw std::runtime_error("no active query cases");
    auto choose = [&](std::uint64_t iteration) { return active[iteration % active.size()]; };
    std::size_t max_parameters = 0, cache_slots = 0;
    for (const auto& shape : query)
    {
        max_parameters = std::max(max_parameters, shape.parameters.size()); cache_slots = std::max(cache_slots, shape.cache_slot + 1);
        for (const auto& step : shape.steps)
        { max_parameters = std::max(max_parameters, step.parameters.size()); cache_slots = std::max(cache_slots, step.cache_slot + 1); }
    }
    Json plans = Json::object();
    if (reused && workers.size() != static_cast<std::size_t>(opt.concurrency)) throw std::runtime_error("recovery worker count changed");
    for (int i = 0; i < opt.concurrency && !reused; ++i)
    {
        auto worker = std::make_unique<Worker>(); worker->connection = std::make_unique<Connection>(opt, cache_slots, max_parameters);
        worker->connection->verify_dataset(manifest.at("dataset").at("tenants"), manifest.at("dataset").at("ordersPerTenant"));
        worker->seconds.resize(opt.seconds ? static_cast<std::size_t>(std::ceil(opt.seconds)) + 301 : 3602);
        worker->cases.resize(query.size());
        if (i == 0 && opt.explain) for (auto q : active) plans[query[q].name] = worker->connection->explain(query[q]);
        if (opt.verify || retained_workers) for (auto q : active) worker->connection->execute(q, query[q]);
        for (int warm = 0; warm < opt.warmup; ++warm) { auto q = choose(warm); if (!opt.empty_driver) worker->connection->execute(q, query[q]); }
        workers.push_back(std::move(worker));
    }
    if (reused)
        for (auto& worker : workers)
        {
            if (worker->poisoned) throw std::runtime_error("a failed connection cannot be reopened or silently reused in a recovery check");
            worker->histogram.fill(0); worker->scheduled.fill(0); worker->all_scheduled.fill(0); worker->start_delay.fill(0);
            worker->seconds.assign(opt.seconds ? static_cast<std::size_t>(std::ceil(opt.seconds)) + 301 : 3602, {});
            std::fill(worker->cases.begin(), worker->cases.end(), CaseCounts{});
            worker->started = worker->completed = worker->failed = worker->poisoned_failures = worker->rows = worker->xor_digest = 0;
            worker->error.clear(); worker->truncated = false;
        }
    auto warm_owners = owner_counts(); Json connection_ids = Json::array();
    for (const auto& worker : workers) connection_ids.push_back(reinterpret_cast<std::uintptr_t>(worker->connection.get()));
    struct Job { std::uint64_t index = 0, planned_ns = 0; };
    std::vector<Job> queue(opt.rate ? opt.queue_capacity : 0);
    std::mutex queue_gate; std::condition_variable queue_changed;
    std::size_t queue_head = 0, queue_size = 0, queue_peak = 0; bool arrivals_done = false;
    std::uint64_t planned = opt.rate ? static_cast<std::uint64_t>(std::ceil(opt.rate * opt.seconds)) : opt.operations;
    std::uint64_t attempted = 0, sent = 0, rejected = 0, started_monotonic = 0, deadline_monotonic = 0, offered_end_monotonic = 0;
    std::string producer_error;
    std::vector<Second> producer_seconds(workers[0]->seconds.size());
    bool producer_truncated = false;
    if (opt.rate)
        for (std::size_t second = 0; second < static_cast<std::size_t>(std::ceil(opt.seconds)); ++second)
            producer_seconds[second].planned = std::min(planned, static_cast<std::uint64_t>(std::ceil((second + 1) * opt.rate)))
                - std::min(planned, static_cast<std::uint64_t>(std::ceil(second * opt.rate)));
    std::mutex gate; std::condition_variable changed; bool start = false;
    std::latch ready(opt.concurrency + (opt.rate ? 1 : 0)); std::vector<std::thread> threads;
    Clock::time_point started, deadline;
    for (int i = 0; i < opt.concurrency; ++i)
        threads.emplace_back([&, i]
        {
            auto& worker = *workers[i];
            { std::unique_lock lock(gate); ready.count_down(); changed.wait(lock, [&] { return start; }); }
            bool& poisoned = worker.poisoned;
            auto perform = [&](std::uint64_t operation, std::uint64_t planned_ns)
            {
                auto q = choose(operation); auto before_ns = monotonic_ns(); ++worker.started; ++worker.cases[q].started; bool failed = false; std::uint64_t rows = 0;
                if (!planned_ns) planned_ns = before_ns;
                auto began_second = (before_ns - started_monotonic) / 1000000000ULL;
                if (began_second < worker.seconds.size()) ++worker.seconds[began_second].started; else worker.truncated = true;
                add_latency(worker.start_delay, before_ns - planned_ns);
                try
                {
                    if (poisoned) throw std::runtime_error("connection unavailable after an unproven failed protocol drain");
                    rows = opt.empty_driver ? static_cast<std::uint64_t>(query[q].expected_rows) : worker.connection->execute(q, query[q]);
                    auto ended_ns = monotonic_ns();
                    ++worker.completed; worker.rows += rows; worker.xor_digest ^= query[q].expected_digest;
                    ++worker.cases[q].succeeded; worker.cases[q].rows += rows;
                    add_latency(worker.histogram, ended_ns - before_ns); add_latency(worker.scheduled, ended_ns - planned_ns);
                    add_latency(worker.all_scheduled, ended_ns - planned_ns);
                }
                catch (const std::exception& error)
                {
                    add_latency(worker.all_scheduled, monotonic_ns() - planned_ns);
                    if (poisoned) ++worker.poisoned_failures;
                    ++worker.failed; ++worker.cases[q].failed; failed = true; poisoned = true;
                    if (worker.error.empty()) worker.error = error.what();
                }
                auto second = (monotonic_ns() - started_monotonic) / 1000000000ULL;
                if (static_cast<std::size_t>(second) < worker.seconds.size())
                { auto& slot = worker.seconds[second]; if (failed) ++slot.failed; else { ++slot.completed; slot.rows += rows; } }
                else worker.truncated = true;
                return !failed;
            };
            if (opt.rate)
            {
                while (true)
                {
                    Job job;
                    {
                        std::unique_lock lock(queue_gate);
                        queue_changed.wait(lock, [&] { return queue_size || arrivals_done; });
                        if (!queue_size) break;
                        job = queue[queue_head]; queue_head = (queue_head + 1) % queue.size(); --queue_size;
                    }
                    // Accepted requests remain accounted after a failed drain;
                    // this worker reports failures and never silently retries.
                    perform(job.index, job.planned_ns);
                }
            }
            else
            {
                auto operations = opt.operations / opt.concurrency + (static_cast<std::uint64_t>(i) < opt.operations % opt.concurrency ? 1 : 0);
                for (std::uint64_t operation = 0; opt.seconds ? Clock::now() < deadline : operation < operations; ++operation)
                    if (!perform(operation, 0)) break;
            }
            worker.after = worker.connection->counts;
        });
    std::thread producer;
    if (opt.rate)
        producer = std::thread([&]
        {
            { std::unique_lock lock(gate); ready.count_down(); changed.wait(lock, [&] { return start; }); }
            try
            {
                for (std::uint64_t index = 0; index < planned; ++index)
                {
                    auto due = started_monotonic + static_cast<std::uint64_t>(index * (1000000000. / opt.rate));
                    wait_until_ns(due); ++attempted;
                    bool admitted;
                    {
                        std::lock_guard lock(queue_gate); admitted = queue_size < queue.size();
                        if (admitted)
                        {
                            queue[(queue_head + queue_size) % queue.size()] = {index, due};
                            ++queue_size; queue_peak = std::max(queue_peak, queue_size);
                        }
                    }
                    auto second = (monotonic_ns() - started_monotonic) / 1000000000ULL;
                    if (admitted) { ++sent; queue_changed.notify_one(); } else ++rejected;
                    if (second < producer_seconds.size())
                    { if (admitted) ++producer_seconds[second].sent; else ++producer_seconds[second].rejected; }
                    else producer_truncated = true;
                }
                wait_until_ns(deadline_monotonic); offered_end_monotonic = monotonic_ns();
            }
            catch (const std::exception& error) { producer_error = error.what(); offered_end_monotonic = monotonic_ns(); }
            { std::lock_guard lock(queue_gate); arrivals_done = true; }
            queue_changed.notify_all();
        });
    ready.wait();
    auto idle_before = ProcessReading::read(); auto idle_start = Clock::now();
    std::this_thread::sleep_for(std::chrono::duration<double>(opt.idle_seconds));
    auto idle_after = ProcessReading::read(); double idle_elapsed = std::chrono::duration<double>(Clock::now() - idle_start).count();
    for (auto& worker : workers) worker->before = worker->connection->counts;
    sqlite3_int64 sqlite_used_before = 0, sqlite_peak_before = 0;
    sqlite3_status64(SQLITE_STATUS_MEMORY_USED, &sqlite_used_before, &sqlite_peak_before, 1);
    auto before = ProcessReading::read(); auto started_utc = utc_now(); rusage cpu_before{}, cpu_after{};
    profile.take(profile.before, true);
    { std::lock_guard lock(gate); if (getrusage(RUSAGE_SELF, &cpu_before)) throw std::runtime_error("getrusage boundary failed");
      started_monotonic = monotonic_ns(); deadline_monotonic = started_monotonic + static_cast<std::uint64_t>(opt.seconds * 1000000000.);
      started = Clock::now(); deadline = started + std::chrono::duration_cast<Clock::duration>(std::chrono::duration<double>(opt.seconds)); start = true; }
    changed.notify_all();
    for (auto& thread : threads) thread.join();
    if (producer.joinable()) producer.join();
    auto ended = Clock::now(); auto ended_monotonic = monotonic_ns();
    if (getrusage(RUSAGE_SELF, &cpu_after)) throw std::runtime_error("getrusage boundary failed");
    profile.take(profile.after);
    auto after = ProcessReading::read(); auto finished_utc = utc_now();
    before.user = cpu_before.ru_utime.tv_sec + cpu_before.ru_utime.tv_usec / 1e6;
    before.system = cpu_before.ru_stime.tv_sec + cpu_before.ru_stime.tv_usec / 1e6;
    after.user = cpu_after.ru_utime.tv_sec + cpu_after.ru_utime.tv_usec / 1e6;
    after.system = cpu_after.ru_stime.tv_sec + cpu_after.ru_stime.tv_usec / 1e6;
    sqlite3_int64 sqlite_used_after = 0, sqlite_peak_after = 0;
    sqlite3_status64(SQLITE_STATUS_MEMORY_USED, &sqlite_used_after, &sqlite_peak_after, 0);
    std::uint64_t actual_started = 0, completed = 0, failed = 0, poisoned_failures = 0, rows = 0; Counters counts;
    std::array<std::uint64_t, 8192> histogram{}, scheduled{}, all_scheduled{}, start_delay{};
    Json errors = Json::array(), sql = Json::object(); bool truncated = producer_truncated;
    if (!producer_error.empty()) errors.push_back("arrival-producer: " + producer_error);
    std::vector<Second> series = producer_seconds;
    std::vector<CaseCounts> case_counts(query.size());
    Json config = workers[0]->connection->configuration;
    for (auto& worker : workers)
    {
        actual_started += worker->started; completed += worker->completed; failed += worker->failed; rows += worker->rows;
        poisoned_failures += worker->poisoned_failures;
        counts.executions += worker->after.executions - worker->before.executions; counts.submissions += worker->after.submissions - worker->before.submissions;
        counts.prepare += worker->after.prepare - worker->before.prepare; counts.describe += worker->after.describe - worker->before.describe;
        counts.confirmations += worker->after.confirmations - worker->before.confirmations; counts.waits += worker->after.waits - worker->before.waits;
        counts.unmaterialized_rows += worker->after.unmaterialized_rows - worker->before.unmaterialized_rows;
        if (!worker->error.empty()) errors.push_back(worker->error); truncated = truncated || worker->truncated;
        for (std::size_t i = 0; i < histogram.size(); ++i)
        { histogram[i] += worker->histogram[i]; scheduled[i] += worker->scheduled[i]; all_scheduled[i] += worker->all_scheduled[i]; start_delay[i] += worker->start_delay[i]; }
        for (std::size_t i = 0; i < series.size(); ++i)
        { series[i].started += worker->seconds[i].started; series[i].completed += worker->seconds[i].completed; series[i].failed += worker->seconds[i].failed; series[i].rows += worker->seconds[i].rows; }
        for (std::size_t q = 0; q < query.size(); ++q)
        { case_counts[q].started += worker->cases[q].started; case_counts[q].succeeded += worker->cases[q].succeeded; case_counts[q].failed += worker->cases[q].failed; case_counts[q].rows += worker->cases[q].rows; }
        if (!retained_workers) worker->connection.reset();
    }
    profile.take(profile.drained);
    auto after_owners = owner_counts();
    auto drained = ProcessReading::read();
    std::this_thread::sleep_for(std::chrono::duration<double>(opt.idle_seconds));
    auto retained = ProcessReading::read();
    sqlite3_int64 sqlite_retained = 0, ignored = 0; sqlite3_status64(SQLITE_STATUS_MEMORY_USED, &sqlite_retained, &ignored, 0);
    double elapsed = std::chrono::duration<double>(ended - started).count();
    Json seconds = Json::array();
    for (std::size_t i = 0; i < std::min(series.size(), static_cast<std::size_t>(std::ceil(elapsed)) + 1); ++i)
        seconds.push_back({{"second", i}, {"planned", series[i].planned}, {"sent", series[i].sent}, {"started", series[i].started},
            {"succeeded", series[i].completed}, {"completed", series[i].completed + series[i].failed}, {"failed", series[i].failed}, {"rejected", series[i].rejected}, {"rows", series[i].rows}});
    Json active_cases = Json::array(), executed_cases = Json::array(), cases = Json::object(), lifecycle_sql = Json::object();
    for (auto q : active)
    {
        sql[query[q].name] = query[q].sql; active_cases.push_back(query[q].name);
        if (query[q].kind == Kind::Transaction)
        {
            Json steps = Json::array();
            for (const auto& step : query[q].steps)
                steps.push_back({{"name", step.name}, {"sql", step.sql}, {"activeTransactionAfter", step.active_transaction_after},
                    {"expectedAffectedRows", step.expected_affected ? Json(*step.expected_affected) : Json(nullptr)}, {"expectedRows", step.expected_rows}});
            lifecycle_sql[query[q].name] = steps;
        }
    }
    for (std::size_t q = 0; q < query.size(); ++q)
        if (case_counts[q].started)
        {
            executed_cases.push_back(query[q].name);
            cases[query[q].name] = {{"started", case_counts[q].started}, {"succeeded", case_counts[q].succeeded}, {"failed", case_counts[q].failed}, {"rows", case_counts[q].rows}};
        }
    bool reconciled = actual_started == completed + failed;
    if (opt.rate) reconciled = reconciled && attempted == planned && attempted == sent + rejected && actual_started == sent && queue_size == 0 && producer_error.empty();
    Json error_classes = {{"database-or-validation", failed - poisoned_failures}, {"connection-unavailable-after-error", poisoned_failures}};
    Json offered = nullptr;
    if (opt.rate)
        offered = {{"requestsPerSecond", opt.rate}, {"offeredSeconds", opt.seconds}, {"queueCapacity", opt.queue_capacity}, {"queuePeak", queue_peak},
            {"startedTicks", started_monotonic}, {"deadlineTicks", deadline_monotonic}, {"offeredEndTicks", offered_end_monotonic},
            {"drainedTicks", ended_monotonic}, {"timerFrequency", 1000000000}, {"planned", planned}, {"attempted", attempted},
            {"sent", sent}, {"started", actual_started}, {"completed", completed + failed}, {"succeeded", completed}, {"failed", failed},
            {"rejected", rejected}, {"retries", 0}, {"rows", rows}, {"suspensions", nullptr},
            {"successServiceHistogram", histogram}, {"successScheduledHistogram", scheduled}, {"allCompletedScheduledHistogram", all_scheduled},
            {"startDelayHistogram", start_delay}, {"seconds", seconds}, {"timeSeriesTruncated", truncated}, {"errorClasses", error_classes},
            {"errors", errors}, {"accountingReconciles", reconciled}, {"producerOverrunSeconds", (static_cast<std::int64_t>(offered_end_monotonic) - static_cast<std::int64_t>(deadline_monotonic)) / 1e9},
            {"drainSeconds", (ended_monotonic - offered_end_monotonic) / 1e9}};
    const auto& primary_histogram = opt.rate ? scheduled : histogram;
    Json result{{"schemaVersion", 3}, {"route", "direct-native-cpp"}, {"scope", "shared read and restoring-transaction contracts; long-run acceptance requires independent qualified campaign evidence"},
        {"declaredCaseIds", active_cases}, {"executedCaseIds", executed_cases}, {"caseCounts", cases}, {"emptyDriver", opt.empty_driver},
        {"diagnosticExecutionPlans", plans}, {"planScope", "EXPLAIN before warmup with identical SQL/parameters; not EXPLAIN ANALYZE or proof of a subsequently cached generic plan"},
        {"provider", opt.provider}, {"case", opt.selected}, {"mode", opt.mode}, {"physicalPreparation", opt.physical}, {"protocolConfirmation", opt.confirmation}, {"sessionMode", "persistent"},
        {"concurrency", opt.concurrency}, {"warmupPerWorker", reused ? 0 : opt.warmup}, {"startedUtc", started_utc}, {"finishedUtc", finished_utc},
        {"beforeOwners", warm_owners}, {"afterOwners", after_owners}, {"sameWarmOwners", warm_owners == after_owners},
        {"connectionIds", connection_ids}, {"connectionsRetainedForRecovery", retained_workers != nullptr},
        {"steadyStartMonotonicNs", started_monotonic}, {"steadyEndMonotonicNs", ended_monotonic},
        {"effectiveSeconds", elapsed}, {"planned", opt.rate ? planned : opt.seconds ? actual_started : opt.operations},
        {"attempted", opt.rate ? attempted : actual_started}, {"sent", opt.rate ? sent : actual_started}, {"started", actual_started},
        {"completed", completed + failed}, {"succeeded", completed}, {"failed", failed}, {"rejected", rejected},
        {"notStarted", opt.rate ? planned - actual_started : opt.seconds ? 0 : opt.operations - actual_started}, {"retries", 0}, {"rows", rows},
        {"successOpsPerSecond", completed / elapsed}, {"rowsPerSecond", rows / elapsed}, {"actualSql", sql}, {"transactionSqlSteps", lifecycle_sql}, {"configuration", config},
        {"scheduling", opt.rate ? "fixed offered rate; bounded FIFO admission; explicit rejection; scheduled-to-complete latency" : "closed-loop; next operation planned after preceding completion; no external arrival rate"},
        {"offeredLoad", offered}, {"accountingReconciles", reconciled}, {"protocol", count_json(counts)},
        {"physicalNetworkRoundTrips", nullptr}, {"physicalNetworkRoundTripsStatus", "not instrumented; libpq submission count and poll wait count are not physical RTT"},
        {"materialization", "owned typed C++ DTO per nonscalar row, optional strings, owned typed Settings; related retains grouped owned children and sorts by Id; all fields and NULL markers consumed"},
        {"dynamicConstruction", "core projection clauses built from typed column metadata; advanced SQL identifiers/placeholders assembled from token recipes; direct SQL text construction differs from Fluent graph construction"},
        {"relatedPreparation", "immutable parent keys reused; SQL text reconstructed in both modes, matching the absence of a public prepared-related overload"},
        {"transactionContract", "construct CRUD SQL in both modes; BEGIN IMMEDIATE or READ COMMITTED; validate owned original/changed/persisted full rows; restore before commit or rollback; stale checked update constructs and catches a native conflict exception"},
        {"transactionFrameworkOverhead", "does not impose Korda-specific query_only pragma or pre-commit SELECT 1; native protocol-confirmation option is labeled separately"},
        {"earlyStopContract", "consume three owned rows; SQLite reset/finalize without further stepping; PostgreSQL drain remaining single-row results without materialization or cancel-key; cleanup included"},
        {"physicalCacheSlotBoundPerConnection", cache_slots},
        {"jsonMaterialization", "nlohmann 3.12 SAX into owned Settings; exact fixed Channel/Gift shape, no DOM"},
        {"moneyMaterialization", "exact signed cents plus fixed scale 2; PostgreSQL binary numeric decoder rejects rounding"},
        {"postgresResultFormat", "binary with single-row mode and drain through final null"},
        {"preparedComparison", "Korda PreparedQuery reuses compiled shape but physically prepares/describes each execution; native physical reuse is a separately labeled layer"},
        {"processBefore", before.json()}, {"processAfter", after.json()}, {"processAfterDrain", drained.json()}, {"processAfterIdle", retained.json()},
        {"cpuBoundary", "getrusage immediately before start release through all workers/arrival producer joined; memory observations occur outside these CPU boundaries"},
        {"userCpuSeconds", after.user - before.user}, {"systemCpuSeconds", after.system - before.system},
        {"cpuSecondsPerSuccessfulOperation", completed ? (after.user + after.system - before.user - before.system) / completed : 0},
        {"idleObservationSeconds", idle_elapsed}, {"idleUserCpuSeconds", idle_after.user - idle_before.user}, {"idleSystemCpuSeconds", idle_after.system - idle_before.system},
        {"managedAllocatedBytes", nullptr}, {"managedAllocationStatus", "not applicable to direct C++"}, {"gcCounts", nullptr},
        {"nativeAllocationCalls", profile.snapshot ? Json(profile.after.successful_allocations - profile.before.successful_allocations) : Json(nullptr)},
        {"nativeFreeCalls", profile.snapshot ? Json(profile.after.free_calls - profile.before.free_calls) : Json(nullptr)},
        {"nativeAllocationProfile", profile.json()}, {"nativeAllocationStatus", profile.snapshot ? "instrumented separate lane; scope and raw counters in nativeAllocationProfile" : "cumulative allocation/free calls not instrumented in this timing binary"},
        {"nativeHeapPeakBytes", nullptr}, {"nativeHeapStatus", "mallinfo2 observations are glibc allocated/reserved blocks, not a whole-process native heap peak"},
        {"sqliteMemoryUsedBefore", sqlite_used_before}, {"sqliteMemoryUsedAfter", sqlite_used_after}, {"sqliteEngineHighwaterSinceWarmup", sqlite_peak_after},
        {"sqliteMemoryAfterDrain", sqlite_retained}, {"sqliteMemoryStatus", "SQLite engine allocator only, excludes C++/libpq/OpenSSL allocations"},
        {"drainedConnections", live_connections.load()}, {"drainedSqliteStatements", live_sqlite_statements.load()}, {"drainedPgResults", live_pg_results.load()},
        {"ownersReturnedToZero", live_connections == 0 && live_sqlite_statements == 0 && live_pg_results == 0 && live_cancel_owners == 0},
        {"latencies", {{"p50MicrosecondsUpper", quantile(primary_histogram, completed, .50)}, {"p95MicrosecondsUpper", quantile(primary_histogram, completed, .95)},
                       {"p99MicrosecondsUpper", quantile(primary_histogram, completed, .99)}, {"scope", opt.rate ? "successful planned-arrival-to-completion" : "successful service time"},
                       {"method", "base-2 histogram with 128 subdivisions per exponent; upper bounds"}, {"histogram", primary_histogram}}},
        {"serviceLatencies", {{"p50MicrosecondsUpper", quantile(histogram, completed, .50)}, {"p95MicrosecondsUpper", quantile(histogram, completed, .95)}, {"p99MicrosecondsUpper", quantile(histogram, completed, .99)}}},
        {"seconds", seconds}, {"timeSeriesTruncated", truncated}, {"errorClasses", error_classes}, {"errors", errors}};
    return result;
}
static Json recovery_checks(const Options& opt, const Json& manifest, const std::vector<Query>& query)
{
    std::array<Options, 3> phase_options{opt, opt, opt};
    for (auto& phase : phase_options)
    { phase.operations = 0; phase.rate = opt.recovery_rate; phase.seconds = opt.recovery_seconds; }
    phase_options[1].rate = opt.overload_rate; phase_options[1].seconds = opt.overload_seconds;
    std::array<const char*, 3> names{"reference", "overload", "recovery"};
    std::vector<std::unique_ptr<Worker>> retained; Json phases = Json::array(), errors = Json::array(), identities;
    bool passed = true, same_connections = true;
    for (std::size_t i = 0; i < phase_options.size(); ++i)
    {
        try
        {
            auto result = run(phase_options[i], manifest, query, &retained);
            if (!i) identities = result.at("connectionIds");
            same_connections = same_connections && result.at("connectionIds") == identities;
            passed = passed && result.at("connectionIds") == identities && result.at("sameWarmOwners") == true
                && result.at("failed") == 0 && result.at("accountingReconciles") == true;
            passed = passed && (i == 1 ? result.at("rejected").get<std::uint64_t>() > 0 : result.at("rejected") == 0);
            bool failed = result.at("failed") != 0;
            phases.push_back({{"name", names[i]}, {"measurement", std::move(result)}});
            if (failed) break;
        }
        catch (const std::exception& error) { errors.push_back({{"phase", names[i]}, {"error", error.what()}}); passed = false; break; }
    }
    retained.clear(); auto final = owner_counts();
    bool owners_zero = live_connections == 0 && live_sqlite_statements == 0 && live_pg_results == 0 && live_cancel_owners == 0;
    passed = passed && phases.size() == 3 && errors.empty() && owners_zero;
    Json ratio = nullptr;
    if (phases.size() == 3)
    {
        double before = phases[0]["measurement"]["latencies"]["p99MicrosecondsUpper"];
        double after = phases[2]["measurement"]["latencies"]["p99MicrosecondsUpper"];
        if (before > 0) ratio = after / before;
    }
    return {{"schemaVersion", 1}, {"route", "direct-native-cpp"}, {"diagnostic", "same-process-same-connection-recovery"},
        {"provider", opt.provider}, {"mode", opt.mode}, {"concurrency", opt.concurrency}, {"passed", passed},
        {"phases", phases}, {"errors", errors}, {"finalOwners", final}, {"ownersReturnedToZero", owners_zero},
        {"sameConnectionsThroughout", same_connections && phases.size() == 3}, {"sameWorkerObjectsThroughout", phases.size() == 3}, {"workerThreads", "joined after each phase, relaunched on the same retained connections and worker objects"},
        {"recoveryP99Ratio", ratio}, {"latencyRecoveryThresholdApplied", false},
        {"passContract", "reference and recovery have zero failures/rejections; overload has observed rejection; every accepted job drained; retained native owners return to warmed baseline after every phase; final owners zero"},
        {"timingComparisonEligible", false}, {"nativeAllocationInstrumented", opt.allocation_profile}};
}
static Json lifecycle_checks(const Options& opt, const Json& manifest, const std::vector<Query>& query)
{
    std::size_t slots = 0, parameter_count = 0;
    for (const auto& shape : query)
    {
        slots = std::max(slots, shape.cache_slot + 1); parameter_count = std::max(parameter_count, shape.parameters.size());
        for (const auto& step : shape.steps) { slots = std::max(slots, step.cache_slot + 1); parameter_count = std::max(parameter_count, step.parameters.size()); }
    }
    auto selected = std::find_if(query.begin(), query.end(), [](const Query& q) { return q.kind == Kind::Numeric; });
    if (selected == query.end()) selected = query.begin();
    Json checks = Json::array(), errors = Json::array(); bool passed = false;
    try
    {
        Connection connection(opt, slots, parameter_count);
        connection.verify_dataset(manifest.at("dataset").at("tenants"), manifest.at("dataset").at("ordersPerTenant"));
        connection.execute(0, *selected);
        const auto id = reinterpret_cast<std::uintptr_t>(&connection);
        auto check = connection.active_cancel_check(); passed = check.at("passed"); checks.push_back(std::move(check));
        auto rows = connection.execute(0, *selected);
        checks.push_back({{"name", "reuse-after-active-cancellation"}, {"passed", rows == static_cast<std::uint64_t>(selected->expected_rows)},
            {"connectionId", id}, {"case", selected->name}, {"validatedRows", rows}, {"reopened", false}});
    }
    catch (const std::exception& error) { errors.push_back(error.what()); passed = false; }
    bool owners_zero = live_connections == 0 && live_sqlite_statements == 0 && live_pg_results == 0 && live_cancel_owners == 0;
    return {{"schemaVersion", 1}, {"route", "direct-native-cpp"}, {"diagnostic", "active-cancellation-and-reuse"},
        {"provider", opt.provider}, {"mode", opt.mode}, {"passed", passed && owners_zero && checks.size() == 2},
        {"checks", checks}, {"errors", errors}, {"finalOwners", owner_counts()}, {"ownersReturnedToZero", owners_zero},
        {"timingComparisonEligible", false}, {"transactionScope", "active statement outside an explicit transaction; early enumerator disposal continues to drain without cancel-key"}};
}
int main(int argc, char** argv)
{
    try
    {
        auto opt = options(argc, argv);
        if (!opt.output.empty() && std::filesystem::exists(opt.output)) throw std::runtime_error("result output already exists; preserve prior evidence");
        auto input_hash = hash_file(opt.manifest);
        std::ifstream input(opt.manifest); if (!input) throw std::runtime_error("query manifest unavailable");
        Json manifest = Json::parse(input); auto query = queries(manifest, opt);
        auto result = opt.recovery_checks ? recovery_checks(opt, manifest, query)
            : opt.lifecycle_checks ? lifecycle_checks(opt, manifest, query) : run(opt, manifest, query);
        auto executable = std::filesystem::canonical("/proc/self/exe");
        result["executableSha256"] = hash_file(executable); result["manifestSha256"] = input_hash;
        result["manifestSha256After"] = hash_file(opt.manifest); result["inputImmutable"] = input_hash == result["manifestSha256After"].get<std::string>();
        result["buildReceiptSha256"] = hash_file(executable.parent_path() / "build-receipt.json");
        result["sqliteVersion"] = sqlite3_libversion(); result["sqliteSourceId"] = sqlite3_sourceid(); result["libpqVersion"] = PQlibVersion();
        Dl_info sqlite_info{};
        if (!dladdr(reinterpret_cast<void*>(sqlite3_libversion), &sqlite_info) || !sqlite_info.dli_fname) throw std::runtime_error("loaded SQLite identity unavailable");
        auto sqlite_path = std::filesystem::canonical(sqlite_info.dli_fname);
        result["loadedSqlitePath"] = sqlite_path.string(); result["loadedSqliteSha256"] = hash_file(sqlite_path);
        Json defines = Json::array(); for (int i = 0; sqlite3_compileoption_get(i); ++i) defines.push_back(sqlite3_compileoption_get(i)); result["sqliteCompileOptions"] = defines;
        if (opt.output.empty()) std::cout << result.dump() << '\n';
        else { std::ofstream output(opt.output); if (!output) throw std::runtime_error("output unavailable"); output << result.dump(2) << '\n'; std::cout << "DB_PERF_NATIVE_RESULT output=" << opt.output << '\n'; }
        bool profile_valid = true;
        if (opt.allocation_profile)
        {
            auto valid = [](const Json& measurement)
            { const auto& profile = measurement.at("nativeAllocationProfile"); return profile.at("observedAllocationCoverageComplete") == true && profile.at("requestedByteConservation") == true; };
            if (opt.recovery_checks) for (const auto& phase : result.at("phases")) profile_valid = profile_valid && valid(phase.at("measurement"));
            else profile_valid = valid(result);
        }
        if (opt.recovery_checks || opt.lifecycle_checks) return result.at("passed") == true && result["inputImmutable"] == true && profile_valid ? 0 : 1;
        return result["failed"] == 0 && result["ownersReturnedToZero"] == true && result["accountingReconciles"] == true && result["inputImmutable"] == true && profile_valid ? 0 : 1;
    }
    catch (const std::exception& failure) { std::cerr << "DB_PERF_NATIVE_ERROR " << failure.what() << '\n'; return 2; }
}
