#pragma once

#include <chrono>
#include <cstdint>
#include <exception>
#include <map>
#include <memory>
#include <optional>
#include <stdexcept>
#include <string>
#include <string_view>
#include <variant>
#include <vector>

namespace db_perf
{
enum class Provider
{
    Sqlite,
    PostgreSql
};

enum class ConstructionMode
{
    // Assemble clauses and prepare/bind/consume/finalize on every operation.
    Dynamic,
    // One physical statement per contract case and worker, retained until close.
    Prepared
};

enum class ProtocolConfirmation
{
    Product,
    DrainOnly
};

enum class ValueKind
{
    Int32,
    Int64,
    String,
    Money,
    Settings
};

enum class RowKind
{
    Int64,
    Text,
    Money,
    Json,
    Wide,
    Join,
    CustomerTotal,
    ProductTotal,
    NullableMoney,
    Rank,
    Running,
    Detail,
    Related,
    State
};

struct Parameter
{
    // NULL remains a typed parameter. The initial read contract uses int64/text.
    ValueKind kind;
    std::variant<std::monostate, std::int64_t, std::string> value;
    bool worker_key = false;
};

struct Column
{
    std::string name;
    ValueKind kind;
    bool nullable;
};

struct StatementDefinition
{
    std::string name;
    std::string sql;
    std::vector<std::string> fragments;
    std::vector<Parameter> parameters;
    std::vector<Column> columns;
};

struct BusinessStep : StatementDefinition
{
    std::string kind;
    std::optional<std::int64_t> expected_affected;
    std::int64_t expected_counter = 0;
    std::int64_t expected_version = 0;
    std::optional<std::string> expected_note;
};

struct ReadCase : StatementDefinition
{
    std::string id;
    RowKind row_kind;
    std::string materialization;
    std::uint64_t expected_rows;
    std::uint64_t expected_digest;
    std::string expected_outcome = "success";
    // Related results retain input positions and empty groups. SQL carries a
    // zero-based parent ordinal in its final column. These IDs enter the digest.
    std::vector<std::int64_t> parent_ids;
    std::vector<BusinessStep> steps;
};

struct Contract
{
    Provider provider;
    std::int64_t tenants;
    std::int64_t orders_per_tenant;
    std::int64_t rows;
    std::int64_t batch_rows;
    std::vector<ReadCase> cases;
};

Contract load_contract(const std::string& path, Provider provider);
std::string assemble_sql(const StatementDefinition& query);

class DatabaseError : public std::runtime_error
{
  public:
    // Categories are stable across drivers; native_code retains SQLSTATE or the
    // SQLite extended result code. The scheduler owns terminal/retry accounting.
    std::string category;
    std::string native_code;
    DatabaseError(std::string category, std::string message, std::string native_code = {});
};

class CleanupError : public DatabaseError
{
  public:
    std::exception_ptr primary;
    std::exception_ptr cleanup;
    CleanupError(std::string message, std::exception_ptr primary, std::exception_ptr cleanup);
};

struct OperationResult
{
    std::uint64_t rows;
    std::uint64_t digest;
    std::string_view outcome = "success";
};

struct Counters
{
    std::uint64_t sql_executions = 0;
    std::uint64_t protocol_submissions = 0;
    std::uint64_t physical_prepare_calls = 0;
    std::uint64_t describe_calls = 0;
    std::uint64_t confirmation_syncs = 0;
    std::uint64_t poll_wait_calls = 0;
    std::uint64_t rows_fetched = 0;
    std::uint64_t rows_consumed = 0;
    std::uint64_t connection_opens = 0;
    std::uint64_t discarded_connections = 0;
    std::uint64_t setup_sql_executions = 0;
    std::uint64_t cleanup_failures = 0;
};

struct OwnerCounts
{
    std::uint64_t connections;
    std::uint64_t sqlite_statements;
    std::uint64_t postgres_results;
    std::uint64_t unconfirmed_sqlite_closes;
};

// These are only explicitly counted client owners, not native heap counters.
OwnerCounts live_owners();

struct ConnectionOptions
{
    std::string connection;
    // Empty selects the complete contract. The campaign supplies its distinct
    // selected case indices, so a single-case run retains only its plan cache
    // slots and parameter scratch capacity. Contract text itself stays shared.
    std::vector<std::size_t> selected_cases;
    ConstructionMode mode = ConstructionMode::Dynamic;
    ProtocolConfirmation confirmation = ProtocolConfirmation::Product;
    std::chrono::milliseconds operation_timeout{5000};
    std::chrono::milliseconds sqlite_busy_timeout{5000};
};

struct RuntimeIdentity
{
    std::string sqlite_version;
    std::string sqlite_source_id;
    std::vector<std::string> sqlite_compile_options;
    int libpq_version;
};

RuntimeIdentity runtime_identity();

// One connection belongs to exactly one worker. Contract and all parameters
// remain immutable and alive until its workers are closed. Calls are sequential.
class QueryAdapter
{
  public:
    QueryAdapter(const Contract& contract, ConnectionOptions options);
    ~QueryAdapter();
    QueryAdapter(const QueryAdapter&) = delete;
    QueryAdapter& operator=(const QueryAdapter&) = delete;
    QueryAdapter(QueryAdapter&&) = delete;
    QueryAdapter& operator=(QueryAdapter&&) = delete;

    // Preparing does not execute a query. Call before a measured interval when
    // every case must begin with a warm, finite physical statement cache.
    void prepare_all();
    OperationResult execute(std::size_t case_index, std::int64_t worker_key = 1);
    OperationResult execute_until(std::size_t case_index, std::chrono::steady_clock::time_point deadline,
                                  std::int64_t worker_key = 1);
    void close() noexcept;
    const Counters& counters() const noexcept;
    const std::map<std::string, std::string>& effective_configuration() const noexcept;

  private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};
} // namespace db_perf
