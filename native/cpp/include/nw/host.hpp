// SPDX-License-Identifier: MIT
#pragma once

#include "../../../database/binding/korda_cursor_pull_v1.h"
#include "nw/administration.hpp"
#include "nw/interop.hpp"
#include "nw/mutations.hpp"
#include "nw/provider.hpp"

#include <condition_variable>
#include <deque>
#include <thread>
#include <variant>

namespace nw
{

struct HostLimits
{
    std::size_t connections, waiters, buffered_rows, buffered_bytes, batch_rows, batch_bytes, field_bytes, nodes,
        parameters, parameter_bytes, prepared_plans, prepared_bytes;
    std::uint32_t depth;
    std::uint64_t admission_ns, command_ns;
    static HostLimits parse(ByteView bytes);
    [[nodiscard]] QueryLimits binding() const;
    [[nodiscard]] AdministrationLimits administration() const;
};
struct HostConfig
{
    ProviderOptions provider;
    HostLimits limits;
    std::string name;
    static HostConfig parse(ByteView bytes);
};

struct Source;
struct Session;
struct Cursor;
struct CursorPull;
struct Transaction;
struct Savepoint;
struct DbOperation;
struct Owner;
struct Buffer;
struct CommandGate;
struct SqlPlan;
struct StreamReservation;
struct QueryOwner
{
    std::mutex mutex;
    std::shared_ptr<NativeQuery> query;
};

using OwnerData = std::variant<std::monostate, std::shared_ptr<Session>, std::shared_ptr<Cursor>,
                               std::shared_ptr<Transaction>, std::shared_ptr<Savepoint>, ModelBinding,
                               std::shared_ptr<SqlPlan>, std::shared_ptr<RelationBinding>, std::shared_ptr<QueryOwner>,
                               std::shared_ptr<NativePlan>, std::shared_ptr<NativeCte>, std::shared_ptr<CursorPull>>;
struct Owner
{
    std::shared_ptr<Source> source;
    OwnerData data;
    Owner(std::shared_ptr<Source> source, OwnerData data = {});
    ~Owner();
    [[nodiscard]] std::uint32_t kind() const;
};
struct Buffer
{
    std::shared_ptr<Source> source;
    Bytes bytes;
    std::shared_ptr<Cursor> cursor;
    const bool known_end = false;
    std::atomic<bool> taken{false};
    void mark_taken();
    Buffer(std::shared_ptr<Source> source, Bytes bytes, std::shared_ptr<Cursor> cursor = {}, bool known_end = false);
    ~Buffer();
};
using Value = std::variant<std::shared_ptr<Owner>, std::shared_ptr<Buffer>, std::int64_t>;

class WorkerPool
{
  public:
    explicit WorkerPool(std::size_t workers);
    ~WorkerPool();
    void post(std::function<void()> work);
    void stop();
#ifdef NW_HOST_TEST_HOOKS
    std::function<void()> test_before_post;
#endif

  private:
    struct State;
    std::shared_ptr<State> state_;
    std::vector<std::thread> threads_;
    std::mutex stop_mutex_;
};

// Independent of the owners whose last references a worker releases. A worker
// may destroy those owners before completing its ticket without accessing them
// again, and an explicit parent join can await all descendant accessors.
class RetirementFence
{
  public:
    void add();
    void complete();
    void wait();
    void wait_until(Clock::time_point deadline);
    [[nodiscard]] bool done() const;

  private:
    mutable std::mutex mutex_;
    std::condition_variable changed_;
    std::size_t pending_ = 0;
};

struct Source : std::enable_shared_from_this<Source>
{
    HostConfig config;
    SourceIdentity identity;
    std::shared_ptr<ParameterRegistry> parameters;
    std::atomic<bool> closing{false}, retired{false}, poisoned{false};
    std::atomic<std::size_t> pending{0}, close_waiters{0};
    std::shared_ptr<RetirementFence> transaction_workers = std::make_shared<RetirementFence>();
    std::shared_ptr<RetirementFence> pull_workers = std::make_shared<RetirementFence>();
    WorkerPool workers;
    WorkerPool controls{2};
    std::mutex mutex, registry_mutex, join_mutex;
    std::condition_variable changed;
#ifdef NW_HOST_TEST_HOOKS
    std::function<void()> test_before_join_wait;
    std::function<void()> test_before_transaction_wait;
#endif
    std::vector<std::weak_ptr<Session>> children;
    std::vector<std::shared_ptr<ProviderConnection>> idle;
    std::size_t leased = 0;
    std::deque<std::shared_ptr<DbOperation>> admissions;
    std::deque<std::shared_ptr<DbOperation>> administration_admissions;
    std::thread admission_thread;
    bool admission_stop = false;
    // Protected by mutex with both queues. Waiting for the independent
    // administrative permit never occupies a provider worker thread.
    bool administration_active = false;
    std::weak_ptr<Cancel> administration_cancel;
    std::mutex stream_mutex;
    std::array<std::uint64_t, 7> stream_stats{};
    std::size_t stream_max_bytes = 0;
    std::shared_ptr<StreamReservation> reserve_stream(std::size_t bytes);
    std::vector<std::pair<std::uint32_t, Codec>> configured;
    std::map<Hash, std::shared_ptr<const Model>> models;
    std::map<Hash, ProjectionBinding> projections;
    std::map<Hash, std::shared_ptr<RelationBinding>> relations;
    std::uint32_t next_projection = 1;
    std::size_t registry_bytes = 0;
    ShapeCache shapes;

    static std::shared_ptr<Source> create(HostConfig config);
    ~Source();
    void admit() const;
    bool reserve_operation(bool control);
    void release_operation();
    void queue_admission(std::shared_ptr<DbOperation> operation);
    void queue_administration(std::shared_ptr<DbOperation> operation);
    void finish_administration();
    void return_connection(std::shared_ptr<ProviderConnection> connection, bool discard);
    void request_close();
    void join();
    [[nodiscard]] Bytes snapshot() const;
    [[nodiscard]] std::shared_ptr<const CodecBindings> codecs();
    std::uint32_t register_codec(std::uint32_t logical, std::optional<std::string> configuration);
    ModelBinding register_model(ByteView bytes);
    ProjectionBinding register_projection(ByteView bytes);
    ProjectionBinding resolve_projection(ProjectionBinding binding);
    ProjectionBinding projection(std::uint32_t id);
    std::shared_ptr<RelationBinding> register_relation(ModelBinding parent, ModelBinding child, ByteView bytes);

  private:
    explicit Source(HostConfig config);
    void registry_capacity(std::size_t bytes) const;
    void admission_loop();
};

struct DbOperation : std::enable_shared_from_this<DbOperation>
{
    enum class Dispatch
    {
        Worker,
        Admission,
        Administration,
        SourceClose,
        Control
    };
    std::shared_ptr<Source> source;
    std::shared_ptr<Operation<Value>> core = std::make_shared<Operation<Value>>();
    std::shared_ptr<Cancel> cancel = std::make_shared<Cancel>();
    std::function<Value(std::shared_ptr<Cancel>)> work;
    std::mutex mutex;
    std::optional<Error> error;
    std::atomic<bool> error_taken{false};
    std::uint32_t result_kind;
    Dispatch dispatch;
    bool counted;
    std::atomic<bool> administration_reserved{false};
    Clock::time_point admission_deadline;
    std::shared_ptr<WorkLease<Value>> lease;
    static std::shared_ptr<DbOperation> create(std::shared_ptr<Source> source, std::uint32_t kind, bool control,
                                               std::function<Value(std::shared_ptr<Cancel>)> work,
                                               Dispatch dispatch = Dispatch::Worker);
    ~DbOperation();
    nw_status start(nw_operation_callbacks_v1 callbacks);
    void run();
    void complete(Value value);
    void fail(Error error);
    void release_administration();

  private:
    DbOperation(std::shared_ptr<Source> source, std::uint32_t kind, bool counted,
                std::function<Value(std::shared_ptr<Cancel>)> work, Dispatch dispatch);
};

struct SqlPlan
{
    std::shared_ptr<Source> source;
    std::string sql;
    // Raw SQL keeps its exact accepted snapshot only for the frozen ABI's
    // optional equality check. Execution uses typed arguments for every route.
    Bytes parameters;
    std::vector<Scalar> arguments;
    std::size_t parameter_wire_bytes = 0;
    Expectation expectation;
    std::optional<ProjectionBinding> projection;
    std::shared_ptr<RelationBinding> relation;
    std::uint32_t kind = 0, result_kind = 0;
    bool scalar_count = false;
    std::uint64_t timeout_ns = 0;
    std::atomic<bool> submitted{false};
    static std::shared_ptr<SqlPlan> parse(std::shared_ptr<Source> source, ByteView sql, ByteView parameters,
                                          ByteView options);
    static std::shared_ptr<SqlPlan> rendered(std::shared_ptr<Source> source, RenderedQuery rendered);
    void capture_parameters(ByteView values);
    void submit(const Source& source);
};

struct CommandGate
{
    std::shared_ptr<Session> session;
    bool dispatched = false;
    bool completed = false;
    explicit CommandGate(std::shared_ptr<Session> session);
    ~CommandGate();
    void finish(bool failed = false);
};
struct Session : std::enable_shared_from_this<Session>
{
    std::shared_ptr<Source> source;
    std::shared_ptr<ProviderConnection> connection;
    std::atomic<bool> closing{false}, retired{false}, active{false}, discard{false}, cleanup_scheduled{false};
    std::atomic<bool> physical_retired{false};
    std::atomic<bool> provider_cleanup_failed{false};
    mutable std::mutex mutex;
    std::condition_variable changed;
    std::weak_ptr<Transaction> transaction;
    std::vector<std::weak_ptr<Cursor>> cursors;
    std::vector<std::weak_ptr<CursorPull>> pulls;
    std::weak_ptr<Cancel> current_cancel;
    std::uint32_t cleanup = 0, disposition = 0;
    std::optional<std::string> cleanup_provider;
    static std::shared_ptr<Session> open_reserved(std::shared_ptr<Source> source, std::shared_ptr<Cancel> cancel);
    ~Session();
    std::shared_ptr<CommandGate> admit(bool transaction_control);
    Value execute(std::shared_ptr<SqlPlan> plan, std::shared_ptr<Cancel> cancel, std::shared_ptr<CommandGate> gate);
    std::shared_ptr<Cursor> open_cursor(std::shared_ptr<SqlPlan> plan, std::shared_ptr<Cancel> cancel,
                                        std::shared_ptr<CommandGate> gate);
    void request_close();
    void schedule_cleanup();
    void retire_now();
    void retire_failed_provider();
    void join();
    Bytes snapshot() const;
};
struct StreamReservation
{
    std::shared_ptr<Source> source;
    std::size_t bytes;
    ~StreamReservation();
};
struct Cursor : std::enable_shared_from_this<Cursor>
{
    std::shared_ptr<Session> session;
    std::shared_ptr<SqlPlan> plan;
    std::shared_ptr<CommandGate> gate;
    std::unique_ptr<ProviderStatement> statement;
    Expectation expectation;
    std::vector<Codec> codecs;
    std::shared_ptr<StreamReservation> reservation;
    std::size_t provider_rows_peak = 0, rows_pulled = 0;
    bool stop_observed = false;
    Clock::time_point deadline;
    std::atomic<bool> closing{false}, retired{false}, active{false}, batch_live{false}, cleanup_scheduled{false};
    std::mutex mutex;
    std::condition_variable changed;
    std::weak_ptr<Cancel> current_cancel;
    bool ended = false, failed = false, end_sent = false;
    std::atomic<bool> close_started{false}, cleanup_unconfirmed{false};
    std::atomic<bool> protocol_quarantined{false};
#ifdef NW_HOST_TEST_HOOKS
    std::function<void()> test_retire_gate;
    std::function<void()> test_after_cleanup_gate;
    std::function<void()> test_before_join_wait;
#endif
    static std::shared_ptr<Cursor> open(std::shared_ptr<Session> session, std::shared_ptr<SqlPlan> plan,
                                        std::shared_ptr<Cancel> cancel, std::shared_ptr<CommandGate> gate,
                                        Clock::time_point deadline);
    ~Cursor();
    void check_read_admission();
    std::shared_ptr<Buffer> fetch(std::shared_ptr<Cancel> cancel, std::size_t rows, std::size_t bytes);
    void request_close();
    void close_now();
    void schedule_cleanup() noexcept;
    void join();
    Bytes snapshot() const;

  private:
    struct Completion
    {
        std::mutex mutex;
        std::condition_variable changed;
        bool done = false;
        void publish();
    };
    std::shared_ptr<Completion> completion_ = std::make_shared<Completion>();
    // An unconfirmed provider accessor cannot be released or returned to the
    // pool. Retain it without allocating on the cleanup-failure path.
    std::shared_ptr<Cursor> quarantine_;
    void retire_provider();
    void close_resources();
    void quarantine_cleanup() noexcept;
};

// One privately owned reader channel, with one accepted request/result at a time.
// Readiness detaches its callback; independent tickets retain only native tails.
struct CursorPull : std::enable_shared_from_this<CursorPull>
{
    std::shared_ptr<Session> session;
    static std::shared_ptr<CursorPull> create(std::shared_ptr<Session> session, std::shared_ptr<SqlPlan> plan,
                                              std::shared_ptr<CommandGate> gate,
                                              std::optional<Clock::time_point> admitted = {});
    ~CursorPull();
    void request(std::uint64_t generation, std::size_t rows, std::size_t bytes, nw_db_cursor_callbacks_v1 callbacks);
    std::shared_ptr<Buffer> take(std::uint64_t generation);
    Error take_error(std::uint64_t generation);
    void close_request(nw_db_cursor_callbacks_v1 callbacks);
    std::shared_ptr<Buffer> close_take();
    void check_read_admission();
    void request_close();
    void join();
    Bytes snapshot() const;
#ifdef NW_HOST_TEST_HOOKS
    // Copied into a request's independent ticket before acceptance. These
    // seams are absent from both production provider profiles.
    std::function<void()> test_before_work;
    std::function<void()> test_before_first_fetch;
    std::function<void()> test_after_ready;
    std::function<void()> test_before_close_work;
#endif

  private:
    struct Worker;
    struct CloseState;
    struct CloseWorker;
    CursorPull(std::shared_ptr<Session> session, std::shared_ptr<SqlPlan> plan, std::shared_ptr<CommandGate> gate,
               std::optional<Clock::time_point> admitted);
    void run(std::uint64_t generation, std::size_t rows, std::size_t bytes, std::shared_ptr<Cancel> cancel);
    void complete(std::uint64_t generation, std::shared_ptr<Buffer> buffer, std::optional<Error> error);
    void join_resources();
    Bytes resources_snapshot(bool include_final_notification = false) const;
    mutable std::mutex mutex_;
    std::shared_ptr<SqlPlan> plan_;
    std::shared_ptr<CommandGate> gate_;
    std::shared_ptr<Cursor> cursor_;
    std::shared_ptr<Cancel> cancel_;
    std::shared_ptr<Buffer> buffer_;
    std::optional<Error> error_;
    std::optional<Clock::time_point> admitted_;
    std::shared_ptr<RetirementFence> completion_ = std::make_shared<RetirementFence>();
    std::shared_ptr<RetirementFence> close_completion_ = std::make_shared<RetirementFence>();
    std::shared_ptr<CloseState> close_;
    std::uint64_t generation_ = 0;
    bool outstanding_ = false, ready_ = false, running_ = false, closing_ = false;
    nw_db_cursor_callbacks_v1 callbacks_{};
};

struct TransactionOptions
{
    std::uint32_t isolation = 0;
    bool read_only = false;
    std::uint64_t commit_timeout_ns = 0, cleanup_timeout_ns = 0;
    std::optional<std::string> recovery_key;
    static TransactionOptions parse(ByteView bytes);
    void validate(Profile profile) const;
};
struct Transaction : std::enable_shared_from_this<Transaction>
{
    std::shared_ptr<Session> session;
    TransactionOptions options;
    mutable std::mutex mutex;
    std::condition_variable changed;
    std::uint32_t state = 0, outcome = 0, cleanup = 0, disposition = 0;
    bool attempted = false, deadline_exceeded = false, retired = false, closing = false, finishing = false;
    std::uint64_t correlation = 0, next_savepoint = 1;
    std::optional<std::string> provider;
    std::vector<std::uint64_t> savepoints;
    bool primary_ready = false;
    bool driver_active = false;
    std::uint32_t cleanup_failure = 0, deadline_phase = 0;
    Clock::time_point phase_deadline = Clock::time_point::max();
    std::optional<Error> primary_error;
#ifdef NW_HOST_TEST_HOOKS
    // Only the isolated regression fixture defines this macro. Production
    // provider binaries contain neither these fields nor either dispatch seam.
    std::function<bool(bool)> test_finish;
    std::function<void()> test_retire_gate;
    std::function<void()> test_after_finish_worker;
    std::function<void()> test_after_deadline_worker;
    std::function<void()> test_before_finish_dispatch;
    std::function<void()> test_before_join_wait;
    std::function<void(std::uint32_t)> test_before_worker_start;
#endif
    static std::shared_ptr<Transaction> begin(std::shared_ptr<Session> session, TransactionOptions options,
                                              std::shared_ptr<Cancel> cancel, std::shared_ptr<CommandGate> gate);
    bool active() const;
    void command_failed();
    Bytes finish(bool commit, std::shared_ptr<Cancel> cancel, std::shared_ptr<CommandGate> gate);
    Bytes wait_close(std::uint64_t wait_ns);
    void request_close();
    void schedule_cleanup();
    void join();
    Bytes snapshot() const;
    std::shared_ptr<Savepoint> create_savepoint(std::shared_ptr<Cancel> cancel, std::shared_ptr<CommandGate> gate);
    Bytes savepoint_action(std::uint64_t token, std::uint32_t action, std::shared_ptr<Cancel> cancel,
                           std::shared_ptr<CommandGate> gate, bool disposal = false);
    Bytes dispose_savepoint(std::uint64_t token);

  private:
    struct Worker;
    class WorkerLaunch;
    std::shared_ptr<RetirementFence> completion_ = std::make_shared<RetirementFence>();
    void finish_worker(bool commit, std::shared_ptr<CommandGate> gate);
    void deadline_worker();
};
struct Savepoint : std::enable_shared_from_this<Savepoint>
{
    std::shared_ptr<Transaction> transaction;
    std::uint64_t token;
    std::atomic<bool> cleanup_requested{false};
    void request_close();
};

extern std::atomic<std::uint64_t> source_count, session_count, operation_count, cursor_count, buffer_count, pull_count;
std::uint64_t module_identity();
std::uint64_t capability_mask();
Bytes snapshot_record(std::uint32_t state, std::uint32_t outcome, bool attempted, std::uint32_t cleanup,
                      std::uint32_t disposition, bool released, bool deadline, std::uint64_t correlation,
                      const std::optional<std::string>& provider = {});
Bytes u32_bytes(std::uint32_t value);
Bytes u64_bytes(std::uint64_t value);
ByteView text_view(std::string_view value);

} // namespace nw
