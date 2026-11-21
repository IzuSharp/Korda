// SPDX-License-Identifier: MIT
#include "nw/provider.hpp"
#include "nw/scalar.hpp"

#include <algorithm>
#include <bit>
#include <charconv>
#include <climits>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <limits>
#include <map>
#include <thread>

#if NW_ENABLE_SQLITE
#include <sqlite3.h>
#endif
#if NW_ENABLE_POSTGRES
#include <libpq-fe.h>
#ifdef _WIN32
#include <winsock2.h>
#else
#include <poll.h>
#endif
#endif

namespace nw
{
namespace
{
Error shape_error()
{
    return Error(NW_DOMAIN_ERROR, 4, "result database type differs");
}
Error large_error()
{
    return Error(NW_DOMAIN_ERROR, 14, "single inline field exceeds capacity");
}
Error configuration_error()
{
    return Error(NW_DOMAIN_ERROR, 8, "invalid provider configuration");
}
std::atomic<std::uint64_t> next_connection{1};

#if NW_ENABLE_SQLITE
std::optional<std::string> sqlite_declared(const char* declaration)
{
    if (!declaration || !*declaration)
        return {};
    std::string type(declaration);
    std::transform(type.begin(), type.end(), type.begin(),
                   [](unsigned char c) { return static_cast<char>(std::toupper(c)); });
    if (type.find("INT") != std::string::npos)
        return "INTEGER";
    if (type.find("CHAR") != std::string::npos || type.find("CLOB") != std::string::npos ||
        type.find("TEXT") != std::string::npos)
        return "TEXT";
    if (type.find("BLOB") != std::string::npos)
        return "BLOB";
    if (type.find("REAL") != std::string::npos || type.find("FLOA") != std::string::npos ||
        type.find("DOUB") != std::string::npos)
        return "REAL";
    return type;
}
std::string_view sqlite_storage(int type)
{
    switch (type)
    {
        case SQLITE_INTEGER:
            return "INTEGER";
        case SQLITE_FLOAT:
            return "REAL";
        case SQLITE_TEXT:
            return "TEXT";
        case SQLITE_BLOB:
            return "BLOB";
        default:
            return "NULL";
    }
}
Error sqlite_error(sqlite3* db, int result)
{
    const int extended = db ? sqlite3_extended_errcode(db) : result;
    const int primary = extended & 255;
    Error error(primary == SQLITE_INTERRUPT ? NW_CANCELED : NW_DOMAIN_ERROR, primary == SQLITE_CONSTRAINT ? 2 : 1,
                "SQLite operation failed");
    error.provider = std::to_string(extended);
    return error;
}
#endif

#if NW_ENABLE_POSTGRES
Error postgres_error(PGresult* result = nullptr)
{
    Error error(NW_DOMAIN_ERROR, 1, "PostgreSQL operation failed");
    if (const char* state = result ? PQresultErrorField(result, PG_DIAG_SQLSTATE) : nullptr)
    {
        std::string code(state);
        if (code.size() == 5 && std::all_of(code.begin(), code.end(), [](unsigned char c) { return std::isalnum(c); }))
        {
            error.provider = code;
            if (code.starts_with("23"))
                error.code = 2;
        }
    }
    return error;
}
void wait_socket(int socket, bool write, const CallContext& context)
{
    context.check();
    if (socket < 0)
        throw postgres_error();
    int timeout = 20;
    if (context.deadline != Clock::time_point::max())
    {
        timeout = static_cast<int>(std::clamp<std::int64_t>(
            std::chrono::duration_cast<std::chrono::milliseconds>(context.deadline - Clock::now()).count(), 1, 20));
    }
#ifdef _WIN32
    WSAPOLLFD descriptor{static_cast<SOCKET>(socket), static_cast<SHORT>(write ? POLLWRNORM : POLLRDNORM), 0};
    const int status = WSAPoll(&descriptor, 1, timeout);
    if (status < 0)
        throw postgres_error();
#else
    pollfd descriptor{socket, static_cast<short>(write ? POLLOUT : POLLIN), 0};
    const int status = poll(&descriptor, 1, timeout);
    if (status < 0 && errno != EINTR)
        throw postgres_error();
#endif
    context.check();
}
void flush(PGconn* connection, const CallContext& context)
{
    for (;;)
    {
        const int status = PQflush(connection);
        if (status == 0)
            return;
        if (status < 0)
            throw postgres_error();
        wait_socket(PQsocket(connection), true, context);
    }
}
PGresult* receive(PGconn* connection, const CallContext& context)
{
    flush(connection, context);
    for (;;)
    {
        context.check();
        if (!PQconsumeInput(connection))
            throw postgres_error();
        if (!PQisBusy(connection))
            return PQgetResult(connection);
        wait_socket(PQsocket(connection), false, context);
    }
}
std::uint64_t big_unsigned(ByteView bytes)
{
    std::uint64_t value = 0;
    for (auto byte : bytes)
        value = (value << 8) | byte;
    return value;
}
std::string text_result(std::uint32_t oid, ByteView bytes)
{
    if (oid == 16 && bytes.size() == 1)
        return bytes[0] ? "t" : "f";
    if (oid == 21 && bytes.size() == 2)
        return std::to_string(static_cast<std::int16_t>(big_unsigned(bytes)));
    if ((oid == 23 || oid == 26) && bytes.size() == 4)
        return oid == 26 ? std::to_string(big_unsigned(bytes))
                         : std::to_string(static_cast<std::int32_t>(big_unsigned(bytes)));
    if (oid == 20 && bytes.size() == 8)
        return std::to_string(static_cast<std::int64_t>(big_unsigned(bytes)));
    if (oid == 3802 && !bytes.empty() && bytes[0] == 1)
        bytes = bytes.subspan(1);
    return std::string(reinterpret_cast<const char*>(bytes.data()), bytes.size());
}
#endif
} // namespace

void Cancel::request()
{
    requested_.store(true, std::memory_order_release);
    std::lock_guard lock(mutex_);
    if (hook_)
        hook_();
}
void Cancel::install(std::function<void()> hook)
{
    std::lock_guard lock(mutex_);
    hook_ = std::move(hook);
    if (requested() && hook_)
        hook_();
}
void Cancel::detach()
{
    std::lock_guard lock(mutex_);
    hook_ = {};
}
bool Cancel::requested() const noexcept
{
    return requested_.load(std::memory_order_acquire);
}
void CallContext::check() const
{
    if (observe_cancel && cancel && cancel->requested())
        throw Error::canceled();
    if (Clock::now() >= deadline)
        throw Error(NW_TIMEOUT, 8, "database command deadline exceeded");
}

struct ProviderConnection::Impl
{
    explicit Impl(ProviderOptions value) : options(std::move(value)), id(next_connection.fetch_add(1)) {}
    ProviderOptions options;
    std::uint64_t id;
    std::atomic<bool> usable{true};
    std::atomic<bool> transaction{false};
    std::atomic<bool> interrupted{false};
    bool statement_live = false;
    std::mutex lifetime;
    CallContext current;
    Clock::time_point busy_started;
#if NW_ENABLE_SQLITE
    sqlite3* sqlite = nullptr;
#endif
#if NW_ENABLE_POSTGRES
    PGconn* postgres = nullptr;
    std::map<Oid, std::string> extension_types;
#endif
};

struct ProviderStatement::Impl
{
    std::shared_ptr<ProviderConnection> connection;
    std::vector<ProviderColumn> columns;
    std::int64_t affected = 0;
    bool closed = false;
    bool ended = false;
    CallContext context;
#if NW_ENABLE_SQLITE
    sqlite3_stmt* sqlite = nullptr;
#endif
#if NW_ENABLE_POSTGRES
    PGresult* postgres = nullptr;
    bool postgres_started = false;
    bool postgres_pending = false;
    std::vector<std::optional<std::string>> postgres_parameters;
#endif
    void update_transaction()
    {
        auto& provider = *connection->impl_;
#if NW_ENABLE_SQLITE
        if (provider.sqlite)
            provider.transaction.store(sqlite3_get_autocommit(provider.sqlite) == 0);
#endif
#if NW_ENABLE_POSTGRES
        if (provider.postgres)
        {
            const auto state = PQtransactionStatus(provider.postgres);
            provider.transaction.store(state == PQTRANS_INTRANS || state == PQTRANS_INERROR);
            if (PQstatus(provider.postgres) != CONNECTION_OK)
                provider.usable.store(false);
        }
#endif
    }
    bool advance(const CallContext& call)
    {
        if (closed || ended)
            return false;
        auto& provider = *connection->impl_;
        if (context.cancel && context.cancel != call.cancel)
            context.cancel->detach();
        context = call;
        provider.current = call;
        call.check();
        if (call.observe_cancel && call.cancel)
            call.cancel->install(
                [weak = connection->weak_from_this()]
                {
                    if (auto owner = weak.lock())
                        owner->interrupt();
                });
#if NW_ENABLE_SQLITE
        if (sqlite)
        {
            const int status = sqlite3_step(sqlite);
            update_transaction();
            if (status == SQLITE_ROW)
                return true;
            if (status != SQLITE_DONE)
            {
                call.check();
                throw sqlite_error(provider.sqlite, status);
            }
            affected = sqlite3_stmt_readonly(sqlite) ? 0 : sqlite3_changes64(provider.sqlite);
            ended = true;
            return false;
        }
#endif
#if NW_ENABLE_POSTGRES
        if (provider.postgres)
        {
            if (!postgres_started)
            {
                std::vector<const char*> values;
                values.reserve(postgres_parameters.size());
                for (const auto& value : postgres_parameters)
                    values.push_back(value ? value->c_str() : nullptr);
                if (!PQsendQueryPrepared(provider.postgres, "", static_cast<int>(values.size()), values.data(), nullptr,
                                         nullptr, 1))
                    throw postgres_error();
                postgres_started = true;
                postgres_pending = true;
                if (!PQsetSingleRowMode(provider.postgres))
                {
                    provider.usable.store(false);
                    throw postgres_error();
                }
            }
            for (;;)
            {
                if (!postgres)
                    postgres = receive(provider.postgres, call);
                if (!postgres)
                {
                    ended = true;
                    postgres_pending = false;
                    update_transaction();
                    return false;
                }
                const auto status = PQresultStatus(postgres);
                if (status == PGRES_SINGLE_TUPLE)
                    return true;
                if (status != PGRES_TUPLES_OK && status != PGRES_COMMAND_OK)
                {
                    auto error = postgres_error(postgres);
                    PQclear(postgres);
                    postgres = nullptr;
                    throw error;
                }
                const char* count = PQcmdTuples(postgres);
                if (count && *count)
                {
                    std::int64_t parsed = 0;
                    auto result = std::from_chars(count, count + std::strlen(count), parsed);
                    if (result.ec == std::errc{})
                        affected += parsed;
                }
                PQclear(postgres);
                postgres = nullptr;
            }
        }
#endif
        throw Error::closed();
    }
    void consume_row()
    {
#if NW_ENABLE_POSTGRES
        if (postgres)
        {
            PQclear(postgres);
            postgres = nullptr;
        }
#endif
    }
};

ProviderConnection::ProviderConnection(std::unique_ptr<Impl> impl) : impl_(std::move(impl)) {}
ProviderConnection::~ProviderConnection()
{
    close();
}
ProviderStatement::ProviderStatement(std::unique_ptr<Impl> impl) : impl_(std::move(impl)) {}
ProviderStatement::~ProviderStatement()
{
    try
    {
        close();
    }
    catch (...)
    {
        impl_->connection->close();
    }
}

std::shared_ptr<ProviderConnection> ProviderConnection::open(const ProviderOptions& options, const CallContext& context)
{
    context.check();
    if (options.connection.empty() || options.connection.size() > 8192 ||
        options.connection.find('\0') != std::string::npos || options.application_name.empty() ||
        options.application_name.size() > 256 || options.application_name.find('\0') != std::string::npos ||
        options.connect_timeout_ns == 0 || options.connect_timeout_ns / 1'000'000'000 > 3600 ||
        options.cleanup_timeout_ns == 0 || options.cleanup_timeout_ns / 1'000'000'000 > 3600)
        throw configuration_error();
    if (options.profile == Profile::Sqlite &&
        (options.connection.size() > 4096 || options.connection.starts_with("file:") || options.sqlite_mode > 3 ||
         options.sqlite_journal > 2 || options.sqlite_synchronous > 1 || options.sqlite_begin > 1 ||
         options.sqlite_busy_timeout_ns % 1'000'000 || options.sqlite_busy_timeout_ns / 1'000'000 > INT32_MAX ||
         ((options.sqlite_mode == 3 || options.connection == ":memory:") &&
          (options.sqlite_mode != 3 || options.sqlite_journal != 2))))
        throw configuration_error();
    auto connection = std::shared_ptr<ProviderConnection>(new ProviderConnection(std::make_unique<Impl>(options)));
    auto& state = *connection->impl_;
    state.current = context;
    auto connect_context = context;
    connect_context.deadline =
        std::min(context.deadline, Clock::now() + std::chrono::nanoseconds(options.connect_timeout_ns));
#if NW_ENABLE_SQLITE
    if (options.profile == Profile::Sqlite)
    {
        int flags = SQLITE_OPEN_FULLMUTEX | SQLITE_OPEN_PRIVATECACHE;
        flags |= options.sqlite_mode == 2 ? SQLITE_OPEN_READONLY : SQLITE_OPEN_READWRITE;
        if (options.sqlite_mode == 0 || options.sqlite_mode == 3)
            flags |= SQLITE_OPEN_CREATE;
        if (options.sqlite_mode == 3)
            flags |= SQLITE_OPEN_MEMORY;
        const int status = sqlite3_open_v2(options.connection.c_str(), &state.sqlite, flags, nullptr);
        if (status != SQLITE_OK)
            throw sqlite_error(state.sqlite, status);
        sqlite3_extended_result_codes(state.sqlite, 1);
        sqlite3_progress_handler(
            state.sqlite, 1000,
            [](void* cookie)
            {
                const auto& current = static_cast<Impl*>(cookie)->current;
                return static_cast<int>((current.observe_cancel && current.cancel && current.cancel->requested()) ||
                                        Clock::now() >= current.deadline);
            },
            &state);
        // Match the SQLx baseline's observable busy_timeout and waiting policy.
        // Cancellation interrupts execution; a busy wait retains its finite budget.
        sqlite3_busy_timeout(state.sqlite, static_cast<int>(options.sqlite_busy_timeout_ns / 1'000'000));
        (void)connection->execute(std::string("PRAGMA foreign_keys=") + (options.sqlite_foreign_keys ? "ON" : "OFF"),
                                  {}, context);
        const char* journals[] = {"WAL", "DELETE", "MEMORY"};
        (void)connection->query_text(std::string("PRAGMA journal_mode=") + journals[options.sqlite_journal], context);
        (void)connection->execute(
            std::string("PRAGMA synchronous=") + (options.sqlite_synchronous == 0 ? "NORMAL" : "FULL"), {}, context);
        return connection;
    }
#endif
#if NW_ENABLE_POSTGRES
    if (options.profile == Profile::PostgreSql)
    {
        const std::string& uri = options.connection;
        if ((!uri.starts_with("postgres://") && !uri.starts_with("postgresql://")) ||
            uri.find_first_of("?#") != std::string::npos)
            throw configuration_error();
        for (const char* key : {"PGHOSTADDR", "PGHOST", "PGPORT", "PGUSER", "PGDATABASE", "PGPASSWORD", "PGPASSFILE",
                                "PGSSLMODE", "PGSSLROOTCERT", "PGSSLCERT", "PGSSLKEY", "PGAPPNAME", "PGOPTIONS"})
        {
            // C++11 permits concurrent getenv reads. Product code never changes
            // the process environment; only serial test setup mutates overrides.
            // NOLINTNEXTLINE(concurrency-mt-unsafe)
            if (std::getenv(key))
                throw configuration_error();
        }
        char* parsing_error = nullptr;
        std::unique_ptr<PQconninfoOption, decltype(&PQconninfoFree)> parsed(
            PQconninfoParse(uri.c_str(), &parsing_error), PQconninfoFree);
        if (parsing_error)
            PQfreemem(parsing_error);
        if (!parsed)
            throw configuration_error();
        std::map<std::string, std::string> values;
        for (auto* option = parsed.get(); option->keyword; ++option)
            if (option->val)
                values.emplace(option->keyword, option->val);
        if (values["host"].empty() || values["host"].find('%') != std::string::npos ||
            values["host"].starts_with('/') || values["user"].empty() || values["dbname"].empty())
            throw configuration_error();
        if (options.root_certificate && (!std::filesystem::path(*options.root_certificate).is_absolute() ||
                                         !std::filesystem::is_regular_file(*options.root_certificate)))
            throw configuration_error();
        const std::string port = values.contains("port") ? values["port"] : "5432";
        const std::string root = options.root_certificate.value_or(options.verify_tls ? "system" : "");
        const char* keys[] = {"host",
                              "port",
                              "user",
                              "password",
                              "dbname",
                              "sslmode",
                              "sslrootcert",
                              "sslcertmode",
                              "sslcrl",
                              "application_name",
                              "passfile",
                              "gssencmode",
                              "client_encoding",
                              nullptr};
#ifdef _WIN32
        constexpr const char* no_passfile = "NUL/nativeweave-no-passfile";
#else
        constexpr const char* no_passfile = "/dev/null/nativeweave-no-passfile";
#endif
        const char* arguments[] = {values["host"].c_str(),
                                   port.c_str(),
                                   values["user"].c_str(),
                                   values["password"].c_str(),
                                   values["dbname"].c_str(),
                                   options.verify_tls ? "verify-full" : "disable",
                                   root.c_str(),
                                   "disable",
                                   no_passfile,
                                   options.application_name.c_str(),
                                   no_passfile,
                                   "disable",
                                   "UTF8",
                                   nullptr};
        state.postgres = PQconnectStartParams(keys, arguments, 0);
        if (!state.postgres)
            throw Error::capacity();
        for (;;)
        {
            connect_context.check();
            const auto status = PQconnectPoll(state.postgres);
            if (status == PGRES_POLLING_OK)
                break;
            if (status == PGRES_POLLING_FAILED)
                throw postgres_error();
            wait_socket(PQsocket(state.postgres), status == PGRES_POLLING_WRITING, connect_context);
        }
        if (PQsetnonblocking(state.postgres, 1) != 0)
            throw postgres_error();
        (void)connection->execute("SET timezone TO 'UTC'", {}, context);
        (void)connection->execute("SET statement_timeout TO " + std::to_string(std::max<std::uint64_t>(
                                                                    1, options.statement_timeout_ns / 1'000'000)),
                                  {}, context);
        const auto extensions = connection->query_text(
            "SELECT oid::bigint, typname::text FROM pg_catalog.pg_type WHERE typname='citext'", context);
        for (const auto& row : extensions)
            if (row.size() == 2 && row[0] && row[1])
            {
                std::uint32_t oid = 0;
                const auto result = std::from_chars(row[0]->data(), row[0]->data() + row[0]->size(), oid);
                if (result.ec == std::errc{})
                    state.extension_types.emplace(oid, *row[1]);
            }
        return connection;
    }
#endif
    throw Error::unsupported();
}

std::unique_ptr<ProviderStatement>
ProviderConnection::query(std::string_view sql, const std::vector<Scalar>& parameters, const CallContext& context)
{
    return prepare(sql, parameters, context, true);
}
std::vector<ProviderColumn> ProviderConnection::describe_sqlite(std::string_view sql, const CallContext& context)
{
    if (profile() != Profile::Sqlite)
        throw Error::unsupported();
    auto statement = prepare(sql, {}, context, false);
    auto columns = statement->columns();
    statement->close();
    return columns;
}
std::unique_ptr<ProviderStatement> ProviderConnection::prepare(std::string_view sql,
                                                               const std::vector<Scalar>& parameters,
                                                               const CallContext& context,
                                                               [[maybe_unused]] bool bind_parameters)
{
    context.check();
    if (!healthy())
        throw Error::closed();
    if (impl_->statement_live)
        throw Error(NW_DOMAIN_ERROR, 7, "physical session already has a command");
    if (sql.empty() || sql.size() > MAX_BYTES || sql.find('\0') != std::string_view::npos ||
        parameters.size() > MAX_NODES)
        throw Error::invalid("invalid SQL command");
    auto statement = std::make_unique<ProviderStatement::Impl>();
    statement->connection = shared_from_this();
    statement->context = context;
    impl_->current = context;
    impl_->interrupted.store(false);
    impl_->statement_live = true;
    auto result = std::unique_ptr<ProviderStatement>(new ProviderStatement(std::move(statement)));
    if (context.observe_cancel && context.cancel)
        context.cancel->install(
            [weak = weak_from_this()]
            {
                if (auto owner = weak.lock())
                    owner->interrupt();
            });
#if NW_ENABLE_SQLITE
    if (impl_->sqlite)
    {
        const char* tail = nullptr;
        int status = sqlite3_prepare_v3(impl_->sqlite, sql.data(), static_cast<int>(sql.size()),
                                        SQLITE_PREPARE_PERSISTENT, &result->impl_->sqlite, &tail);
        if (status != SQLITE_OK)
            throw sqlite_error(impl_->sqlite, status);
        if (!result->impl_->sqlite)
            throw Error::invalid("empty SQL command");
        if (bind_parameters &&
            sqlite3_bind_parameter_count(result->impl_->sqlite) != static_cast<int>(parameters.size()))
            throw Error::invalid("SQL parameter count differs");
        for (std::size_t index = 0; bind_parameters && index < parameters.size(); ++index)
        {
            const auto& value = parameters[index];
            const int parameter = static_cast<int>(index + 1);
            if (!value.value)
                status = sqlite3_bind_null(result->impl_->sqlite, parameter);
            else
            {
                auto storage = sqlite_encode(value.codec, *value.value);
                status = std::visit(
                    [&](const auto& item) -> int
                    {
                        using T = std::decay_t<decltype(item)>;
                        if constexpr (std::is_same_v<T, std::int64_t>)
                            return sqlite3_bind_int64(result->impl_->sqlite, parameter, item);
                        else if constexpr (std::is_same_v<T, double>)
                            return sqlite3_bind_double(result->impl_->sqlite, parameter, item);
                        else if constexpr (std::is_same_v<T, std::string>)
                            return sqlite3_bind_text64(result->impl_->sqlite, parameter, item.data(), item.size(),
                                                       SQLITE_TRANSIENT, SQLITE_UTF8);
                        else
                            return sqlite3_bind_blob64(result->impl_->sqlite, parameter,
                                                       item.empty() ? static_cast<const void*>("") : item.data(),
                                                       item.size(), SQLITE_TRANSIENT);
                    },
                    storage);
            }
            if (status != SQLITE_OK)
                throw sqlite_error(impl_->sqlite, status);
        }
        const int count = sqlite3_column_count(result->impl_->sqlite);
        for (int i = 0; i < count; ++i)
            result->impl_->columns.push_back({sqlite3_column_name(result->impl_->sqlite, i),
                                              sqlite_declared(sqlite3_column_decltype(result->impl_->sqlite, i)), 0});
        return result;
    }
#endif
#if NW_ENABLE_POSTGRES
    if (impl_->postgres)
    {
        std::vector<Oid> types;
        auto& storage = result->impl_->postgres_parameters;
        types.reserve(parameters.size());
        storage.reserve(parameters.size());
        for (const auto& parameter : parameters)
        {
            types.push_back(postgres_oid(parameter.codec));
            storage.push_back(parameter.value ? std::optional(postgres_encode(parameter.codec, *parameter.value))
                                              : std::nullopt);
        }
        const std::string owned_sql(sql);
        // Describe before execution, as the original provider did. Opening a
        // cursor never waits for its first row or executes DML/volatile functions.
        // The unnamed statement has bounded server state and this connection
        // remains exclusively leased until its result is drained or discarded.
        auto prepare = [&]
        {
            if (!PQsendPrepare(impl_->postgres, "", owned_sql.c_str(), static_cast<int>(types.size()), types.data()))
                throw postgres_error();
            result->impl_->postgres_pending = true;
            while (auto* reply = receive(impl_->postgres, context))
            {
                std::unique_ptr<PGresult, decltype(&PQclear)> owner(reply, PQclear);
                if (PQresultStatus(reply) != PGRES_COMMAND_OK)
                    throw postgres_error(reply);
            }
            result->impl_->postgres_pending = false;
        };
        prepare();
        if (!PQsendDescribePrepared(impl_->postgres, ""))
            throw postgres_error();
        result->impl_->postgres_pending = true;
        while (auto* reply = receive(impl_->postgres, context))
        {
            std::unique_ptr<PGresult, decltype(&PQclear)> owner(reply, PQclear);
            if (PQresultStatus(reply) != PGRES_COMMAND_OK)
                throw postgres_error(reply);
            for (int i = 0; i < PQnfields(reply); ++i)
            {
                const auto oid = PQftype(reply, i);
                auto type =
                    impl_->extension_types.contains(oid) ? impl_->extension_types.at(oid) : postgres_type_name(oid);
                result->impl_->columns.push_back({PQfname(reply, i), type, oid});
            }
        }
        result->impl_->postgres_pending = false;
        std::string unknown;
        for (const auto& column : result->impl_->columns)
        {
            if ((!column.type_name || column.type_name->empty()) &&
                !impl_->extension_types.contains(column.native_type))
            {
                if (!unknown.empty())
                    unknown += ',';
                unknown += std::to_string(column.native_type);
            }
        }
        if (!unknown.empty())
        {
            const auto lookup = "SELECT oid::text,typname::text FROM pg_catalog.pg_type WHERE oid IN (" + unknown + ")";
            if (!PQsendQueryParams(impl_->postgres, lookup.c_str(), 0, nullptr, nullptr, nullptr, nullptr, 0))
                throw postgres_error();
            result->impl_->postgres_pending = true;
            while (auto* reply = receive(impl_->postgres, context))
            {
                std::unique_ptr<PGresult, decltype(&PQclear)> owner(reply, PQclear);
                if (PQresultStatus(reply) != PGRES_TUPLES_OK)
                    throw postgres_error(reply);
                if (PQntuples(reply) > static_cast<int>(MAX_NODES))
                    throw Error::capacity();
                for (int i = 0; i < PQntuples(reply); ++i)
                {
                    const auto oid = static_cast<Oid>(std::stoul(PQgetvalue(reply, i, 0)));
                    const std::string type(PQgetvalue(reply, i, 1));
                    if (type == "citext")
                        impl_->extension_types[oid] = type;
                    for (auto& column : result->impl_->columns)
                        if (column.native_type == oid)
                            column.type_name = type;
                }
            }
            result->impl_->postgres_pending = false;
            // The catalog query replaced the unnamed statement.
            prepare();
        }
        result->impl_->update_transaction();
        return result;
    }
#endif
    throw Error::unsupported();
}

const std::vector<ProviderColumn>& ProviderStatement::columns() const noexcept
{
    return impl_->columns;
}
std::int64_t ProviderStatement::affected() const noexcept
{
    return impl_->affected;
}
std::optional<Row> ProviderStatement::next(const std::vector<Codec>& codecs, std::size_t field_bytes,
                                           const CallContext& context, std::size_t row_payload_bytes)
{
    if (codecs.size() != impl_->columns.size())
        throw shape_error();
    if (!impl_->advance(context))
        return {};
    // The original B06 row preflight checks every observed type before scalar
    // decoding. This keeps a late shape error ahead of an earlier field's
    // conversion/size error and avoids partially materializing an invalid row.
    for (std::size_t index = 0; index < codecs.size(); ++index)
    {
#if NW_ENABLE_SQLITE
        if (impl_->sqlite)
        {
            const auto type = sqlite3_column_type(impl_->sqlite, static_cast<int>(index));
            const auto& declared = impl_->columns[index].type_name;
            const auto observed = type == SQLITE_NULL
                                      ? (declared ? std::optional<std::string_view>(*declared) : std::nullopt)
                                      : std::optional(sqlite_storage(type));
            if (!type_matches(Profile::Sqlite, codecs[index].id, observed))
                throw shape_error();
        }
#endif
#if NW_ENABLE_POSTGRES
        if (impl_->postgres)
        {
            const auto& column = impl_->columns[index];
            if (!type_matches(Profile::PostgreSql, codecs[index].id,
                              column.type_name ? std::optional<std::string_view>(*column.type_name) : std::nullopt))
                throw shape_error();
        }
#endif
    }
    std::size_t remaining = row_payload_bytes;
    Row row;
    row.reserve(codecs.size());
    for (std::size_t index = 0; index < codecs.size(); ++index)
    {
        const auto& codec = codecs[index];
        // One byte of decode scratch when no payload remains matches the old
        // staged-row conversion order; publication still requires exact space.
        const auto limit = std::min(field_bytes, std::max<std::size_t>(remaining, 1));
#if NW_ENABLE_SQLITE
        if (impl_->sqlite)
        {
            const int ordinal = static_cast<int>(index);
            const int type = sqlite3_column_type(impl_->sqlite, ordinal);
            if (type == SQLITE_NULL)
            {
                if (!codec.nullable)
                    throw Error(NW_DOMAIN_ERROR, 3, "database scalar conversion failed");
                row.push_back({});
                continue;
            }
            StorageValue value;
            if (type == SQLITE_INTEGER)
                value = static_cast<std::int64_t>(sqlite3_column_int64(impl_->sqlite, ordinal));
            else if (type == SQLITE_FLOAT)
                value = sqlite3_column_double(impl_->sqlite, ordinal);
            else
            {
                const auto length = static_cast<std::size_t>(sqlite3_column_bytes(impl_->sqlite, ordinal));
                if (length > limit)
                    throw large_error();
                if (type == SQLITE_TEXT)
                {
                    const auto* data = sqlite3_column_text(impl_->sqlite, ordinal);
                    value = std::string(
                        reinterpret_cast<const char*>(data ? data : reinterpret_cast<const unsigned char*>("")),
                        length);
                }
                else
                {
                    const auto* data = static_cast<const std::uint8_t*>(sqlite3_column_blob(impl_->sqlite, ordinal));
                    value = length ? Bytes(data, data + length) : Bytes{};
                }
            }
            auto encoded = sqlite_decode(codec, type, value);
            if (encoded.size() > limit || encoded.size() > remaining)
                throw large_error();
            remaining -= encoded.size();
            row.emplace_back(std::move(encoded));
            continue;
        }
#endif
#if NW_ENABLE_POSTGRES
        if (impl_->postgres)
        {
            const auto& column = impl_->columns[index];
            if (PQgetisnull(impl_->postgres, 0, static_cast<int>(index)))
            {
                if (!codec.nullable)
                    throw Error(NW_DOMAIN_ERROR, 3, "database scalar conversion failed");
                row.push_back({});
                continue;
            }
            const auto length = static_cast<std::size_t>(PQgetlength(impl_->postgres, 0, static_cast<int>(index)));
            if (length > limit)
                throw large_error();
            ByteView bytes(
                reinterpret_cast<const std::uint8_t*>(PQgetvalue(impl_->postgres, 0, static_cast<int>(index))), length);
            const auto oid = column.type_name == "citext" ? 25 : column.native_type;
            auto encoded = postgres_decode(codec, oid, bytes);
            if (encoded.size() > limit || encoded.size() > remaining)
                throw large_error();
            remaining -= encoded.size();
            row.emplace_back(std::move(encoded));
        }
#endif
    }
    impl_->consume_row();
    return row;
}
std::optional<TextRow> ProviderStatement::next_text(const CallContext& context)
{
    if (!impl_->advance(context))
        return {};
    TextRow row;
    row.reserve(impl_->columns.size());
    for (std::size_t index = 0; index < impl_->columns.size(); ++index)
    {
#if NW_ENABLE_SQLITE
        if (impl_->sqlite)
        {
            const int ordinal = static_cast<int>(index);
            if (sqlite3_column_type(impl_->sqlite, ordinal) == SQLITE_NULL)
                row.push_back({});
            else
            {
                const auto* value = sqlite3_column_text(impl_->sqlite, ordinal);
                const auto length = static_cast<std::size_t>(sqlite3_column_bytes(impl_->sqlite, ordinal));
                if (length > MAX_BYTES)
                    throw large_error();
                row.emplace_back(std::string(reinterpret_cast<const char*>(value), length));
            }
            continue;
        }
#endif
#if NW_ENABLE_POSTGRES
        if (impl_->postgres)
        {
            if (PQgetisnull(impl_->postgres, 0, static_cast<int>(index)))
                row.push_back({});
            else
            {
                const auto length = static_cast<std::size_t>(PQgetlength(impl_->postgres, 0, static_cast<int>(index)));
                if (length > MAX_BYTES)
                    throw large_error();
                row.emplace_back(text_result(impl_->columns[index].native_type,
                                             ByteView(reinterpret_cast<const std::uint8_t*>(
                                                          PQgetvalue(impl_->postgres, 0, static_cast<int>(index))),
                                                      length)));
            }
        }
#endif
    }
    impl_->consume_row();
    return row;
}
void ProviderStatement::close()
{
    if (impl_->closed)
        return;
    auto& provider = *impl_->connection->impl_;
    if (impl_->context.cancel)
        impl_->context.cancel->detach();
    impl_->closed = true;
#if NW_ENABLE_SQLITE
    if (impl_->sqlite)
    {
        sqlite3_finalize(impl_->sqlite);
        impl_->sqlite = nullptr;
    }
#endif
#if NW_ENABLE_POSTGRES
    if (provider.postgres && impl_->postgres_pending)
    {
        if (impl_->postgres)
        {
            PQclear(impl_->postgres);
            impl_->postgres = nullptr;
        }
        const CallContext cleanup{
            {}, Clock::now() + std::chrono::nanoseconds(provider.options.cleanup_timeout_ns), false};
        try
        {
            // Preserve the original SQLx stop contract: abandon the reader and
            // drain ReadyForQuery within a bounded cleanup budget. A cancel-key
            // request here would abort a surrounding transaction that the old
            // implementation left usable. A failed drain discards the lease.
            while (auto* result = receive(provider.postgres, cleanup))
            {
                const auto status = PQresultStatus(result);
                PQclear(result);
                if (status != PGRES_SINGLE_TUPLE && status != PGRES_TUPLES_OK && status != PGRES_COMMAND_OK)
                    throw postgres_error();
            }
        }
        catch (...)
        {
            provider.usable.store(false);
            // A failed drain is never returned to the pool.
            impl_->connection->close();
        }
    }
#endif
    impl_->update_transaction();
    provider.statement_live = false;
    provider.current = {};
}
std::int64_t ProviderConnection::execute(std::string_view sql, const std::vector<Scalar>& parameters,
                                         const CallContext& context)
{
    auto statement = query(sql, parameters, context);
    while (statement->next_text(context))
    {
    }
    const auto count = statement->affected();
    statement->close();
    return count;
}
std::vector<TextRow> ProviderConnection::query_text(std::string_view sql, const CallContext& context,
                                                    std::size_t maximum_rows)
{
    auto statement = query(sql, {}, context);
    std::vector<TextRow> rows;
    std::size_t bytes = 0;
    while (auto row = statement->next_text(context))
    {
        if (rows.size() >= maximum_rows)
            throw Error::capacity();
        for (const auto& value : *row)
            if (value)
            {
                bytes += value->size();
                if (bytes > MAX_BYTES)
                    throw Error::capacity();
            }
        rows.push_back(std::move(*row));
    }
    statement->close();
    return rows;
}
void ProviderConnection::execute_script(std::string_view sql, const CallContext& context)
{
    context.check();
    if (!healthy())
        throw Error::closed();
    if (impl_->statement_live)
        throw Error(NW_DOMAIN_ERROR, 7, "physical session already has a command");
    if (sql.size() > MAX_BYTES || sql.find('\0') != std::string_view::npos)
        throw Error::invalid("invalid SQL script");
    auto state = std::make_unique<ProviderStatement::Impl>();
    state->connection = shared_from_this();
    state->context = context;
    auto statement = std::unique_ptr<ProviderStatement>(new ProviderStatement(std::move(state)));
    impl_->statement_live = true;
    impl_->current = context;
    if (context.observe_cancel && context.cancel)
        context.cancel->install(
            [weak = weak_from_this()]
            {
                if (auto owner = weak.lock())
                    owner->interrupt();
            });
#if NW_ENABLE_SQLITE
    if (impl_->sqlite)
    {
        const char* position = sql.data();
        const char* end = position + sql.size();
        while (position < end)
        {
            context.check();
            const char* tail = position;
            const int status = sqlite3_prepare_v3(impl_->sqlite, position, static_cast<int>(end - position), 0,
                                                  &statement->impl_->sqlite, &tail);
            if (status != SQLITE_OK)
                throw sqlite_error(impl_->sqlite, status);
            if (tail <= position)
                throw Error::invalid("SQL script parser made no progress");
            position = tail;
            if (!statement->impl_->sqlite)
                continue;
            statement->impl_->ended = false;
            while (statement->impl_->advance(context))
            {
            }
            sqlite3_finalize(statement->impl_->sqlite);
            statement->impl_->sqlite = nullptr;
        }
        statement->close();
        return;
    }
#endif
#if NW_ENABLE_POSTGRES
    if (impl_->postgres)
    {
        const std::string text(sql);
        if (!PQsendQuery(impl_->postgres, text.c_str()) || !PQsetSingleRowMode(impl_->postgres))
            throw postgres_error();
        statement->impl_->postgres_started = true;
        statement->impl_->postgres_pending = true;
        while (statement->impl_->advance(context))
            statement->impl_->consume_row();
        statement->close();
        return;
    }
#endif
    throw Error::unsupported();
}
void ProviderConnection::close() noexcept
{
    std::lock_guard lock(impl_->lifetime);
    impl_->usable.store(false);
#if NW_ENABLE_SQLITE
    if (impl_->sqlite)
    {
        sqlite3_close_v2(impl_->sqlite);
        impl_->sqlite = nullptr;
    }
#endif
#if NW_ENABLE_POSTGRES
    if (impl_->postgres)
    {
        PQfinish(impl_->postgres);
        impl_->postgres = nullptr;
    }
#endif
}
bool ProviderConnection::healthy() const noexcept
{
    return impl_->usable.load();
}
bool ProviderConnection::in_transaction() const noexcept
{
    return impl_->transaction.load();
}
Profile ProviderConnection::profile() const noexcept
{
    return impl_->options.profile;
}
std::uint64_t ProviderConnection::identity() const noexcept
{
    return impl_->id;
}
void ProviderConnection::interrupt() noexcept
{
    impl_->interrupted.store(true);
#if NW_ENABLE_SQLITE
    std::lock_guard lock(impl_->lifetime);
    if (impl_->sqlite)
        sqlite3_interrupt(impl_->sqlite);
#endif
}
bool type_matches(Profile profile, std::uint32_t codec, std::optional<std::string_view> type)
{
    if (profile == Profile::Sqlite)
    {
        if (!type || type == "NULL")
            return codec != 13;
        switch (codec)
        {
            case 1:
            case 2:
            case 3:
            case 4:
            case 10:
            case 15:
            case 17:
                return type == "INTEGER";
            case 5:
            case 8:
            case 9:
            case 14:
            case 16:
                return type == "TEXT";
            case 6:
            case 7:
                return type == "BLOB";
            case 11:
            case 12:
                return type == "REAL";
            default:
                return false;
        }
    }
    switch (codec)
    {
        case 1:
            return type == "INT2";
        case 2:
        case 17:
            return type == "INT4";
        case 3:
        case 15:
            return type == "INT8";
        case 4:
            return type == "BOOL";
        case 5:
            return type == "TEXT" || type == "VARCHAR" || type == "BPCHAR" || type == "NAME" || type == "UNKNOWN" ||
                   type == "citext";
        case 6:
            return type == "BYTEA";
        case 7:
            return type == "UUID";
        case 8:
            return type == "DATE";
        case 9:
            return type == "TIME";
        case 10:
            return type == "TIMESTAMPTZ";
        case 11:
            return type == "FLOAT8";
        case 12:
            return type == "FLOAT4";
        case 13:
            return type == "NUMERIC";
        case 16:
            return type == "JSON" || type == "JSONB";
        default:
            return false;
    }
}
#ifdef NW_HOST_TEST_HOOKS
void* ProviderConnection::test_sqlite_handle() const noexcept
{
#if NW_ENABLE_SQLITE
    return impl_->sqlite;
#else
    return nullptr;
#endif
}
#endif
std::vector<Scalar> decode_parameters(ByteView bytes, const Expectation& expected, std::size_t maximum_parameters,
                                      std::size_t maximum_bytes, std::size_t maximum_field_bytes)
{
    if (maximum_parameters == 0 || maximum_parameters > MAX_NODES || maximum_bytes == 0 || maximum_bytes > MAX_BYTES ||
        maximum_field_bytes == 0 || maximum_field_bytes > maximum_bytes)
        throw Error::capacity();
    if ((expected.kind != 1 && expected.kind != 3 && expected.kind != 4) ||
        (expected.exact_rows && *expected.exact_rows != 1))
        throw Error(NW_DOMAIN_ERROR, 4, "parameters require one admitted value or key row");
    if (bytes.size() > maximum_bytes || expected.descriptors.size() > maximum_parameters)
        throw Error::capacity();
    auto one_row = expected;
    one_row.exact_rows = 1;
    for (const auto& descriptor : expected.descriptors)
    {
        descriptor.codec.validate(expected.profile);
        if (expected.profile == Profile::PostgreSql && descriptor.codec.id == 14)
            throw Error::unsupported();
    }
    const auto batch = Batch::parse(bytes, std::move(one_row));
    std::vector<Scalar> parameters;
    parameters.reserve(expected.descriptors.size());
    std::size_t provider_bytes = 0;
    for (std::size_t index = 0; index < expected.descriptors.size(); ++index)
    {
        const auto value = batch.cell(0, index);
        const auto& codec = expected.descriptors[index].codec;
        auto encoded_bytes = value ? value->size() : 0;
        if (value && expected.profile == Profile::Sqlite)
        {
            switch (codec.id)
            {
                case 1:
                case 2:
                case 3:
                case 4:
                case 10:
                case 11:
                case 12:
                case 15:
                case 17:
                    encoded_bytes = 8;
                    break;
                case 8:
                    encoded_bytes = 10;
                    break;
                case 9:
                    encoded_bytes = 15;
                    break;
                case 14:
                    encoded_bytes = std::get<std::string>(sqlite_encode(codec, *value)).size();
                    break;
                default:
                    break;
            }
        }
        else if (value)
        {
            if (codec.id == 5 && std::find(value->begin(), value->end(), 0) != value->end())
                throw Error(NW_DOMAIN_ERROR, 3, "PostgreSQL text cannot contain NUL");
            if (codec.id == 13)
                encoded_bytes = 24;
            else if (codec.id == 16)
                ++encoded_bytes;
        }
        if ((value && value->size() > maximum_field_bytes) || encoded_bytes > maximum_field_bytes)
            throw Error::capacity();
        // Preserve B07's provider-byte budget, including the four-byte framing
        // of every argument (also typed NULL), before publishing any arguments.
        if (maximum_bytes - provider_bytes < 4 || encoded_bytes > maximum_bytes - provider_bytes - 4)
            throw Error::capacity();
        provider_bytes += encoded_bytes + 4;
        parameters.push_back({codec, value ? std::optional(Bytes(value->begin(), value->end())) : std::nullopt});
    }
    return parameters;
}
} // namespace nw
