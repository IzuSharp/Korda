// SPDX-License-Identifier: MIT
#pragma once

#include "nw/contracts.hpp"

#include <atomic>
#include <chrono>
#include <functional>
#include <limits>
#include <memory>
#include <mutex>

namespace nw
{

using Clock = std::chrono::steady_clock;

// Monotonic module-local diagnostics. These count provider dispatches and
// libpq protocol submissions, not rows, TCP packets, or elapsed round trips.
[[nodiscard]] std::uint64_t provider_dispatch_count() noexcept;
[[nodiscard]] std::uint64_t provider_protocol_count() noexcept;

class Cancel
{
  public:
    void request();
    void install(std::function<void()> hook);
    void detach();
    [[nodiscard]] bool requested() const noexcept;

  private:
    std::atomic<bool> requested_{false};
    std::mutex mutex_;
    std::function<void()> hook_;
};

struct CallContext
{
    std::shared_ptr<Cancel> cancel;
    Clock::time_point deadline = Clock::time_point::max();
    bool observe_cancel = true;
    void check() const;
};

struct ProviderOptions
{
    Profile profile = Profile::Sqlite;
    std::string connection;
    std::string application_name = "Korda";
    std::optional<std::string> root_certificate;
    bool verify_tls = true;
    std::uint64_t connect_timeout_ns = 30'000'000'000;
    std::uint64_t statement_timeout_ns = 30'000'000'000;
    std::uint64_t cleanup_timeout_ns = 5'000'000'000;
    std::uint32_t sqlite_mode = 0;
    std::uint32_t sqlite_journal = 0;
    std::uint32_t sqlite_synchronous = 1;
    std::uint32_t sqlite_begin = 0;
    std::uint64_t sqlite_busy_timeout_ns = 5'000'000'000;
    bool sqlite_foreign_keys = true;
};

struct Scalar
{
    Codec codec;
    std::optional<Bytes> value;
};
using Row = std::vector<std::optional<Bytes>>;
using TextRow = std::vector<std::optional<std::string>>;

struct ProviderColumn
{
    std::string name;
    std::optional<std::string> type_name;
    std::uint32_t native_type = 0;
};

class ProviderConnection;

// One live statement owns a physical connection through its last drain/finalize.
// next() holds at most the current provider row; the Host owns bounded batching.
class ProviderStatement
{
  public:
    ~ProviderStatement();
    ProviderStatement(const ProviderStatement&) = delete;
    ProviderStatement& operator=(const ProviderStatement&) = delete;
    [[nodiscard]] const std::vector<ProviderColumn>& columns() const noexcept;
    [[nodiscard]] std::optional<Row> next(const std::vector<Codec>& codecs, std::size_t field_bytes,
                                          const CallContext& context,
                                          std::size_t row_payload_bytes = std::numeric_limits<std::size_t>::max());
    [[nodiscard]] std::optional<TextRow> next_text(const CallContext& context);
    [[nodiscard]] std::int64_t affected() const noexcept;
    // The Host's PostgreSQL description and stream-retirement boundaries each
    // require their own protocol confirmation. Ordinary low-level statements do
    // not implicitly add confirmations to transaction/admin commands.
    void confirm_description();
    void close(bool confirm_protocol = false);

  private:
    friend class ProviderConnection;
    struct Impl;
    explicit ProviderStatement(std::unique_ptr<Impl> impl);
    std::unique_ptr<Impl> impl_;
};

class ProviderConnection : public std::enable_shared_from_this<ProviderConnection>
{
  public:
    [[nodiscard]] static std::shared_ptr<ProviderConnection>
    open(const ProviderOptions& options, const CallContext& context, bool defer_session_initialization = false);
    ~ProviderConnection();
    [[nodiscard]] std::unique_ptr<ProviderStatement> query(std::string_view sql, const std::vector<Scalar>& parameters,
                                                           const CallContext& context);
    // SQLite's retained query path describes SQL before parameter admission.
    // This prepares/finalizes metadata without binding values or stepping SQL.
    [[nodiscard]] std::vector<ProviderColumn> describe_sqlite(std::string_view sql, const CallContext& context);
    [[nodiscard]] std::int64_t execute(std::string_view sql, const std::vector<Scalar>& parameters,
                                       const CallContext& context);
    void execute_script(std::string_view sql, const CallContext& context);
    [[nodiscard]] std::vector<TextRow> query_text(std::string_view sql, const CallContext& context,
                                                  std::size_t maximum_rows = MAX_NODES);
    // Called only by the exclusive physical owner after its statement retires.
    // Uses one provider cleanup deadline and ignores business cancellation.
    // A failed confirmation physically closes this connection before throwing.
    void synchronize_protocol();
    // The retained pool's test-before-acquire uses the acquisition's absolute
    // deadline and cancellation, not the independent cleanup budget.
    void verify_idle(const CallContext& acquisition);
    // The retained Host initializes its leased PostgreSQL Session after pool
    // acquisition, outside that deadline and its cancellation selection.
    void initialize_session();
    // SQLx return_to_pool performs another, unbounded protocol ping. Its failure
    // discards the physical connection but is swallowed by that pool layer.
    [[nodiscard]] bool confirm_pool_return() noexcept;
    void close() noexcept;
    [[nodiscard]] bool healthy() const noexcept;
    [[nodiscard]] bool in_transaction() const noexcept;
    [[nodiscard]] Profile profile() const noexcept;
    [[nodiscard]] std::uint64_t identity() const noexcept;
    void interrupt() noexcept;
#ifdef NW_HOST_TEST_HOOKS
    // Borrowed only while the isolated fixture owns this idle connection.
    [[nodiscard]] void* test_sqlite_handle() const noexcept;
#endif

  private:
    friend class ProviderStatement;
    struct Impl;
    explicit ProviderConnection(std::unique_ptr<Impl> impl);
    [[nodiscard]] std::unique_ptr<ProviderStatement> prepare(std::string_view sql,
                                                             const std::vector<Scalar>& parameters,
                                                             const CallContext& context, bool bind_parameters);
    std::unique_ptr<Impl> impl_;
};

[[nodiscard]] bool type_matches(Profile profile, std::uint32_t codec, std::optional<std::string_view> type);
[[nodiscard]] std::vector<Scalar> decode_parameters(ByteView bytes, const Expectation& expected,
                                                    std::size_t maximum_parameters, std::size_t maximum_bytes,
                                                    std::size_t maximum_field_bytes);

} // namespace nw
