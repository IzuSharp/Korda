// SPDX-License-Identifier: MIT
// Retained B02 provider oracles use the production C++ Host and libpq adapter.
#include "nw/host.hpp"
#include "support.hpp"

#include <nlohmann/json.hpp>

#include <arpa/inet.h>
#include <sys/socket.h>
#include <unistd.h>

#include <bit>
#include <cerrno>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <future>
#include <iostream>
#include <stdexcept>
#include <system_error>

namespace nw
{
std::uint64_t module_identity()
{
    return 0x4442435050423032ULL;
}
} // namespace nw
namespace
{
using namespace nw;
using Json = nlohmann::ordered_json;
using namespace std::chrono_literals;
void check(bool value, std::string_view message)
{
    if (!value)
        throw std::runtime_error(std::string(message));
}
std::string environment(const char* name)
{
    // Fixture inputs are immutable while any Host is active; C++11 permits concurrent reads.
    // NOLINTNEXTLINE(concurrency-mt-unsafe)
    const auto* value = std::getenv(name);
    if (!value)
        throw std::runtime_error(std::string("required fixture input missing: ") + name);
    return value;
}
class IgnoredEnvironment
{
  public:
    // The serial fixture calls this only after all existing Host workers have
    // joined. Its synchronous provider connection is closed before restoration.
    IgnoredEnvironment()
    {
        for (const auto& [name, value] :
             std::initializer_list<std::pair<const char*, const char*>>{{"PGSERVICE", "korda-nonexistent-service"},
                                                                        {"PGCHANNELBINDING", "require"},
                                                                        {"PGDATESTYLE", "SQL,DMY"}})
        {
            // NOLINTNEXTLINE(concurrency-mt-unsafe) -- isolated serial setup, no active Host.
            const auto* prior = std::getenv(name);
            values_.push_back({name, prior ? std::optional<std::string>(prior) : std::nullopt});
            // NOLINTNEXTLINE(concurrency-mt-unsafe) -- isolated serial setup, no active Host.
            check(setenv(name, value, 1) == 0, "isolated fixture environment setup");
        }
    }
    ~IgnoredEnvironment()
    {
        for (const auto& [name, value] : values_)
        {
            if (value)
                // NOLINTNEXTLINE(concurrency-mt-unsafe) -- joined Host; synchronous connection already closed.
                (void)setenv(name.c_str(), value->c_str(), 1);
            else
                // NOLINTNEXTLINE(concurrency-mt-unsafe) -- joined Host; synchronous connection already closed.
                (void)unsetenv(name.c_str());
        }
    }

  private:
    std::vector<std::pair<std::string, std::optional<std::string>>> values_;
};
Bytes text(std::string_view value)
{
    return {value.begin(), value.end()};
}
std::shared_ptr<Cancel> token(bool canceled = false)
{
    auto result = std::make_shared<Cancel>();
    if (canceled)
        result->request();
    return result;
}
template <class Action> Error rejected(Action action)
{
    try
    {
        action();
    }
    catch (const Error& error)
    {
        return error;
    }
    throw std::runtime_error("expected product rejection");
}
template <class Predicate> void eventually(Predicate predicate, std::string_view message)
{
    const auto limit = Clock::now() + 5s;
    while (!predicate())
    {
        check(Clock::now() < limit, message);
        std::this_thread::sleep_for(1ms);
    }
}
HostLimits limits(std::size_t connections = 1)
{
    return {connections, 16,        64, 4 * MAX_BYTES, 2,  65536,         65536,         4096,
            4096,        MAX_BYTES, 64, MAX_BYTES,     64, 2'000'000'000, 30'000'000'000};
}
Bytes limits_record()
{
    const auto value = limits();
    return encode_control(10, {{1, 2, u64_bytes(value.connections)},
                               {2, 2, u64_bytes(value.waiters)},
                               {3, 3, u64_bytes(value.admission_ns)},
                               {4, 2, u64_bytes(value.buffered_rows)},
                               {5, 2, u64_bytes(value.buffered_bytes)},
                               {6, 2, u64_bytes(value.batch_rows)},
                               {7, 2, u64_bytes(value.batch_bytes)},
                               {8, 2, u64_bytes(value.field_bytes)},
                               {9, 2, u64_bytes(value.nodes)},
                               {10, 2, u64_bytes(value.depth)},
                               {11, 2, u64_bytes(value.parameters)},
                               {12, 2, u64_bytes(value.parameter_bytes)},
                               {13, 2, u64_bytes(value.prepared_plans)},
                               {14, 2, u64_bytes(value.prepared_bytes)},
                               {15, 3, u64_bytes(value.command_ns)}});
}
HostConfig from_record(ByteView record)
{
    return HostConfig::parse(encode_control(1, {{1, 1, u32_bytes(0)},
                                                {2, 4, text(environment("NW_PG_URL"))},
                                                {3, 5, limits_record()},
                                                {4, 5, Bytes(record.begin(), record.end())},
                                                {5, 4, text("B02-provider-oracles")}}));
}
HostConfig configuration(std::size_t connections = 1, bool plain = false)
{
    HostConfig value{};
    // Fixture inputs stay immutable during Host execution; C++11 permits concurrent reads.
    // NOLINTNEXTLINE(concurrency-mt-unsafe)
    if (const auto* path = std::getenv("NW_PG_OPTION_RECORD"); path && std::filesystem::exists(path))
    {
        std::ifstream input(path, std::ios::binary);
        const Bytes record(std::istreambuf_iterator<char>(input), {});
        value = from_record(record);
    }
    value.provider.profile = Profile::PostgreSql;
    value.provider.connection = environment(plain ? "NW_PG_PLAIN_URL" : "NW_PG_URL");
    value.provider.verify_tls = !plain;
    value.provider.root_certificate = environment("NW_PG_ROOT");
    value.provider.connect_timeout_ns = 2'000'000'000;
    value.name = "B02-provider-oracles";
    value.limits = limits(connections);
    return value;
}
struct Scope
{
    std::shared_ptr<Source> source;
    std::vector<std::shared_ptr<Session>> sessions;
    explicit Scope(HostConfig value = configuration()) : source(Source::create(std::move(value))) {}
    ~Scope()
    {
        for (const auto& session : sessions)
            session->request_close();
        for (const auto& session : sessions)
            session->join();
        source->join();
    }
    std::shared_ptr<Session> open()
    {
        {
            std::lock_guard lock(source->mutex);
            check(source->leased < source->config.limits.connections, "fixture pool admission capacity");
            ++source->leased;
        }
        auto session = Session::open_reserved(source, token());
        sessions.push_back(session);
        return session;
    }
};
void close(const std::shared_ptr<Session>& session)
{
    session->request_close();
    session->join();
}
void execute(ProviderConnection& connection, std::string_view sql)
{
    (void)connection.execute(sql, {}, {{}, Clock::now() + 10s, false});
}
void execute(const std::shared_ptr<Session>& session, std::string_view sql)
{
    auto gate = session->admit(false);
    gate->dispatched = true;
    try
    {
        execute(*session->connection, sql);
        gate->finish();
    }
    catch (...)
    {
        gate->finish(true);
        throw;
    }
}
std::vector<TextRow> rows(const std::shared_ptr<Session>& session, std::string_view sql)
{
    auto gate = session->admit(false);
    gate->dispatched = true;
    try
    {
        auto result = session->connection->query_text(sql, {{}, Clock::now() + 10s, false});
        gate->finish();
        return result;
    }
    catch (...)
    {
        gate->finish(true);
        throw;
    }
}
std::int64_t scalar(const std::shared_ptr<Session>& session, std::string_view sql)
{
    auto result = rows(session, sql);
    check(result.size() == 1 && result[0].size() == 1 && result[0][0], "scalar shape");
    return std::stoll(*result[0][0]);
}
std::shared_ptr<Transaction> begin(const std::shared_ptr<Session>& session)
{
    return Transaction::begin(session, {0, false, 5'000'000'000, 5'000'000'000, {}}, token(), session->admit(true));
}
void finish(const std::shared_ptr<Transaction>& tx, bool commit)
{
    (void)tx->finish(commit, token(), tx->session->admit(true));
    tx->join();
}
std::shared_ptr<SqlPlan> plan(const std::shared_ptr<Session>& session, std::string_view sql, std::uint32_t kind = 1)
{
    auto result = std::make_shared<SqlPlan>();
    result->source = session->source;
    result->sql = sql;
    result->kind = kind;
    result->result_kind = 0;
    result->expectation = {1, Profile::PostgreSql, {}, 0, 0, {}, 1};
    result->parameters = Batch::encode(result->expectation, 1, {});
    result->timeout_ns = 5'000'000'000;
    return result;
}

Json transaction_parent_retirement()
{
    Scope scope;
    auto session = scope.open();
    auto owner = std::make_shared<Owner>(scope.source, session);
    auto driver = begin(session);
    finish(driver, false);
    check(!session->retired.load(), "independent parent stays open after transaction retirement");
    owner.reset();
    session->join();
    check(session->retired.load() && session->disposition == 0,
          "retired transaction cannot retain a physical pool lease");
    (void)rejected([&] { (void)driver->finish(true, token(), std::make_shared<CommandGate>(session)); });
    driver.reset();
    auto second = scope.open();
    auto second_owner = std::make_shared<Owner>(scope.source, second);
    auto retained = begin(second);
    second_owner.reset();
    retained->join();
    check(second->retired.load(), "last parent close joins before transaction retirement receipt");
    auto replacement = scope.open();
    check(scalar(replacement, "SELECT 9::bigint") == 9, "parent retirement restored pool permits");
    return {{"case", "transaction-parent-retirement"},
            {"driver_parent_released_before_return", true},
            {"already_last_parent_joined", true},
            {"retired_driver_rejects_operations", true},
            {"pool_permits_restored", true}};
}
Json tls()
{
    auto good = configuration();
    Scope scope(good);
    auto session = scope.open();
    auto identity = rows(session, "SELECT version(),ssl,version,cipher FROM pg_stat_ssl WHERE pid=pg_backend_pid()");
    check(identity.size() == 1 && identity[0].size() == 4 && identity[0][0] &&
              identity[0][0]->find("PostgreSQL 18") != std::string::npos && identity[0][1] == "t",
          "actual PostgreSQL18 verified TLS");
    const auto app = rows(session, "SELECT current_setting('application_name')")[0][0];
    check(app == good.provider.application_name, "application name crossed option record");
    auto wrong = good.provider;
    const auto host = wrong.connection.find("localhost");
    check(host != std::string::npos, "fixture localhost URI");
    wrong.connection.replace(host, 9, "127.0.0.1");
    (void)rejected([&] { (void)ProviderConnection::open(wrong, {}); });
    auto untrusted = good.provider;
    untrusted.root_certificate = environment("NW_PG_UNTRUSTED_ROOT");
    (void)rejected([&] { (void)ProviderConnection::open(untrusted, {}); });
    auto expired = good.provider;
    expired.connection = environment("NW_PG_EXPIRED_URL");
    (void)rejected([&] { (void)ProviderConnection::open(expired, {}); });
    auto plain = good.provider;
    plain.connection = environment("NW_PG_PLAIN_URL");
    (void)rejected([&] { (void)ProviderConnection::open(plain, {}); });
    plain.verify_tls = false;
    auto local = ProviderConnection::open(plain, {});
    check(local->query_text("SELECT ssl FROM pg_stat_ssl WHERE pid=pg_backend_pid()", {})[0][0] == "f",
          "plaintext requires explicit opt-in");
    local->close();
    auto overridden = good.provider;
    overridden.connection += "?sslmode=disable";
    (void)rejected([&] { (void)ProviderConnection::open(overridden, {}); });
    const auto original_date_style = rows(session, "SELECT current_setting('datestyle')")[0][0];
    close(session);
    scope.source->join();
    {
        // The old SQLx provider rejected its explicit policy-override list and
        // did not consult these other libpq environment defaults. The pinned
        // libpq configuration patch must retain that behavior without changing
        // the product process environment.
        IgnoredEnvironment ignored;
        auto explicit_connection = ProviderConnection::open(good.provider, {});
        const auto settings = explicit_connection->query_text(
            "SELECT current_setting('application_name'),current_setting('datestyle')", {});
        check(settings.size() == 1 && settings[0][0] == app && settings[0][1] == original_date_style,
              "unrelated libpq environment cannot override explicit product policy");
        explicit_connection->close();
    }
    return {{"case", "tls"},
            {"server_identity", *identity[0][0]},
            {"application_name", *app},
            {"tls_version", *identity[0][2]},
            {"cipher", *identity[0][3]},
            {"wrong_hostname_rejected", true},
            {"untrusted_root_rejected", true},
            {"expired_certificate_rejected", true},
            {"plaintext_requires_explicit_opt_in", true},
            {"connection_string_tls_override_rejected", true},
            {"unrelated_pg_environment_ignored", true}};
}
Json timeout_rollback()
{
    auto config = configuration();
    config.provider.statement_timeout_ns = 60'000'000;
    Scope scope(config);
    auto session = scope.open();
    auto pid = scalar(session, "SELECT pg_backend_pid()");
    auto tx = begin(session);
    auto timed = rejected([&] { execute(*session->connection, "SELECT pg_sleep(2)"); });
    check(timed.provider == "57014", "statement timeout SQLSTATE");
    auto failed = rejected([&] { execute(*session->connection, "SELECT 1"); });
    check(failed.provider == "25P02", "failed transaction SQLSTATE");
    finish(tx, false);
    check(scalar(session, "SELECT pg_backend_pid()") == pid && scalar(session, "SELECT 7::bigint") == 7,
          "timeout rollback same backend reuse");
    tx = begin(session);
    (void)rejected([&] { execute(*session->connection, "SELECT 1/0"); });
    (void)rejected([&] { (void)tx->finish(true, token(), session->admit(true)); });
    tx->join();
    check(tx->outcome == 2, "server rollback cannot become committed outcome");
    close(session);
    auto reused = scope.open();
    check(scalar(reused, "SELECT pg_backend_pid()") == pid, "safe session returned to pool");
    return {{"case", "timeout-rollback"}, {"timeout_sqlstate", "57014"}, {"failed_sqlstate", "25P02"},
            {"original_pid", pid},        {"reused_pid", pid},           {"failed_commit", "RolledBack"}};
}
nw_status NW_CDECL notify(std::uint64_t, std::uint32_t, const nw_error_info_v1*)
{
    return NW_OK;
}
Json stream_and_admission()
{
    Scope scope;
    auto session = scope.open();
    auto pid = scalar(session, "SELECT pg_backend_pid()");
    auto stream = session->connection->query("SELECT n::bigint FROM generate_series(1,20000) n", {}, {});
    auto row = stream->next({{3, {}, false}}, 4096, {});
    check(row.has_value(), "first stream row");
    check(rejected([&] { execute(session, "SELECT 1"); }).code == 7, "one provider command at a time");
    auto admission = DbOperation::create(
        scope.source, 1, false, [source = scope.source](std::shared_ptr<Cancel> cancel) -> Value
        { return std::make_shared<Owner>(source, Session::open_reserved(source, std::move(cancel))); },
        DbOperation::Dispatch::Admission);
    check(admission->start({24, 0, 1, notify}) == NW_OK, "start bounded admission");
    std::this_thread::sleep_for(20ms);
    check(!admission->core->snapshot().terminal_status, "pool waiter retains pending state");
    admission->cancel->request();
    eventually([&] { return admission->core->snapshot().terminal_status.has_value(); }, "pool cancel terminal");
    check(admission->core->snapshot().terminal_status == NW_CANCELED, "pool admission cancel category");
    check(admission->core->take_result().first == NW_INVALID_STATE, "failed admission transfers no session");
    eventually([&] { return admission->core->try_retire() == NW_OK; }, "pool cancel retirement");
    admission.reset();
    stream->close();
    stream.reset();
    check(scalar(session, "SELECT pg_backend_pid()") == pid, "early stream same backend reuse");
    auto empty = session->connection->query("SELECT 1::bigint AS value WHERE false", {}, {});
    check(empty->columns().size() == 1 && empty->columns()[0].name == "value" &&
              empty->columns()[0].type_name == "INT8",
          "empty result complete metadata");
    check(!empty->next({{3, {}, false}}, 4096, {}), "empty result EOF");
    empty->close();
    close(session);
    auto reused = scope.open();
    check(scalar(reused, "SELECT pg_backend_pid()") == pid, "canceled waiter restores permit");
    return {{"case", "early-stream-drop"},
            {"same_backend_pid", pid},
            {"provider_rows_peak", 1},
            {"acquire_cancel_permit_restored", true},
            {"empty_result_metadata", true}};
}
Json codecs()
{
    Scope scope;
    auto session = scope.open();
    Bytes uuid(16);
    for (std::uint8_t i = 0; i < 16; ++i)
        uuid[i] = i;
    Bytes decimal(16);
    put_u32(decimal, 0, 123456125);
    decimal[12] = 3;
    struct Test
    {
        Codec codec;
        std::optional<Bytes> value;
        const char* sql;
    };
    const std::vector<Test> tests{
        {{7, {}, false}, uuid, "SELECT $1::uuid AS value"},
        {{13, "{\"precision\":12,\"scale\":3}", false}, decimal, "SELECT $1::numeric AS value"},
        {{10, {}, false}, u64_bytes(1'700'000'000'123'456), "SELECT $1::timestamptz AS value"},
        {{5, {}, false}, text("PostgreSQL λ"), "SELECT $1::text AS value"},
        {{3, {}, true}, {}, "SELECT $1::bigint"}};
    for (const auto& test : tests)
    {
        Expectation expected{
            3, Profile::PostgreSql, {}, 0, 0, {{1, test.codec, test.codec.configuration ? 1024U : test.codec.id}}, 1};
        const auto encoded = Batch::encode(expected, 1, {test.value});
        auto parameters = decode_parameters(encoded, expected, 4096, MAX_BYTES, 4096);
        auto stream = session->connection->query(test.sql, parameters, {});
        auto row = stream->next({test.codec}, 4096, {});
        check(row && row->size() == 1 && (*row)[0] == test.value, "actual B07 scalar parameter/result roundtrip");
        stream->close();
    }
    auto wide = session->connection->query("SELECT 1e100::numeric", {}, {});
    (void)rejected([&] { (void)wide->next({tests[1].codec}, 4096, {}); });
    wide->close();
    return {{"case", "codecs"}, {"actual_b07_roundtrips", 5}, {"wide_numeric_rejected", true}};
}
Json savepoints()
{
    Scope scope;
    auto session = scope.open();
    execute(session, "CREATE TEMP TABLE b02_savepoint(value int) ON COMMIT PRESERVE ROWS");
    auto tx = begin(session);
    execute(session, "INSERT INTO b02_savepoint VALUES(1)");
    auto point = tx->create_savepoint(token(), session->admit(true));
    execute(session, "INSERT INTO b02_savepoint VALUES(2)");
    (void)tx->savepoint_action(point->token, 0, token(), session->admit(true));
    check(tx->savepoints.size() == 1, "rollback-to retains tracked depth");
    (void)tx->savepoint_action(point->token, 1, token(), session->admit(true));
    finish(tx, true);
    check(tx->outcome == 1 && tx->retired && scalar(session, "SELECT count(*) FROM b02_savepoint") == 1,
          "savepoint durable rows and retirement");
    tx = begin(session);
    check(rejected([&] { (void)tx->finish(true, token(true), session->admit(true)); }).status == NW_CANCELED,
          "pre-dispatch cancellation");
    check(!tx->attempted, "canceled commit never dispatched");
    finish(tx, false);
    check(tx->outcome == 2, "explicit rollback outcome");
    auto unsupported = encode_control(3, {{1, 1, u32_bytes(4)},
                                          {2, 1, u32_bytes(0)},
                                          {3, 3, u64_bytes(1'000'000'000)},
                                          {4, 3, u64_bytes(1'000'000'000)}});
    (void)rejected([&] { (void)TransactionOptions::parse(unsupported); });
    check(!session->connection->in_transaction(), "unsupported isolation sent no BEGIN");
    return {{"case", "b05-savepoints"},
            {"rollback_to_keeps_tracked_depth", true},
            {"durable_rows", 1},
            {"pre_dispatch_cancel_no_commit", true},
            {"b05_native_retired", true}};
}

// This isolated proxy reads actual protocol frames. It forwards COMMIT to the
// server and discards its CommandComplete/ReadyForQuery, never forges a reply.
class CommitLossProxy
{
    int listener_ = -1;
    std::atomic<int> client_{-1}, server_{-1};
    std::thread worker_;
    std::exception_ptr failure_;
    static bool read_all(int fd, std::span<std::uint8_t> bytes)
    {
        while (!bytes.empty())
        {
            auto count = recv(fd, bytes.data(), bytes.size(), 0);
            if (count == 0)
                return false;
            if (count < 0)
            {
                if (errno == EINTR)
                    continue;
                throw std::system_error(errno, std::generic_category(), "proxy read");
            }
            bytes = bytes.subspan(static_cast<std::size_t>(count));
        }
        return true;
    }
    static void write_all(int fd, ByteView bytes)
    {
        while (!bytes.empty())
        {
            auto count = send(fd, bytes.data(), bytes.size(), MSG_NOSIGNAL);
            if (count < 0)
            {
                if (errno == EINTR)
                    continue;
                throw std::system_error(errno, std::generic_category(), "proxy write");
            }
            check(count != 0, "proxy write progress");
            bytes = bytes.subspan(static_cast<std::size_t>(count));
        }
    }
    static std::uint32_t size(ByteView bytes)
    {
        return (std::uint32_t(bytes[0]) << 24) | (std::uint32_t(bytes[1]) << 16) | (std::uint32_t(bytes[2]) << 8) |
               bytes[3];
    }
    static Bytes frame(int fd, std::size_t header)
    {
        Bytes result(header);
        if (!read_all(fd, result))
            return {};
        const auto length = size(ByteView(result).last(4));
        check(length >= 4 && length <= 1'048'576, "bounded proxy frame");
        result.resize(header + length - 4);
        check(read_all(fd, std::span(result).subspan(header)), "complete proxy frame");
        return result;
    }
    static void socket_timeout(int fd)
    {
        const timeval value{10, 0};
        check(setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &value, sizeof(value)) == 0, "proxy timeout");
        check(setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &value, sizeof(value)) == 0, "proxy timeout");
    }
    void run(std::uint16_t upstream)
    {
        int client = accept(listener_, nullptr, nullptr);
        check(client >= 0, "proxy accept");
        client_ = client;
        socket_timeout(client);
        int server = socket(AF_INET, SOCK_STREAM, 0);
        check(server >= 0, "proxy upstream socket");
        server_ = server;
        socket_timeout(server);
        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        address.sin_port = htons(upstream);
        check(connect(server, reinterpret_cast<sockaddr*>(&address), sizeof(address)) == 0, "proxy upstream connect");
        auto startup = frame(client, 4);
        check(startup.size() >= 8, "PostgreSQL startup frame");
        write_all(server, startup);
        std::atomic<bool> armed{false};
        std::exception_ptr request_failure;
        std::thread request(
            [&]
            {
                try
                {
                    bool parsed_commit = false, bound_commit = false;
                    for (;;)
                    {
                        auto bytes = frame(client, 5);
                        if (bytes.empty())
                            break;
                        const auto tag = bytes[0];
                        const std::string body(reinterpret_cast<const char*>(bytes.data() + 5), bytes.size() - 5);
                        if (tag == 'P')
                        {
                            auto end = body.find('\0');
                            parsed_commit =
                                end != std::string::npos && body.substr(end + 1, 7) == std::string("COMMIT\0", 7);
                        }
                        if (tag == 'B')
                            bound_commit = parsed_commit;
                        if ((tag == 'Q' && body == std::string("COMMIT\0", 7)) || (tag == 'E' && bound_commit))
                            armed.store(true);
                        write_all(server, bytes);
                    }
                }
                catch (...)
                {
                    request_failure = std::current_exception();
                }
            });
        try
        {
            bool command = false;
            for (;;)
            {
                auto bytes = frame(server, 5);
                check(!bytes.empty(), "proxy server response");
                if (armed.load())
                {
                    const std::string body(reinterpret_cast<const char*>(bytes.data() + 5), bytes.size() - 5);
                    if (bytes[0] == 'C')
                        command = body == std::string("COMMIT\0", 7);
                    if (bytes[0] == 'Z')
                    {
                        check(command && body == "I", "server confirmed durable COMMIT/Idle");
                        observed.store(true);
                        break;
                    }
                }
                else
                    write_all(client, bytes);
            }
        }
        catch (...)
        {
            failure_ = std::current_exception();
        }
        shutdown(client, SHUT_RDWR);
        shutdown(server, SHUT_RDWR);
        request.join();
        if (!observed.load() && request_failure && !failure_)
            failure_ = request_failure;
    }

  public:
    std::atomic<bool> observed{false};
    std::uint16_t port = 0;
    explicit CommitLossProxy(std::uint16_t upstream)
    {
        listener_ = socket(AF_INET, SOCK_STREAM, 0);
        check(listener_ >= 0, "proxy listener");
        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        address.sin_port = 0;
        check(bind(listener_, reinterpret_cast<sockaddr*>(&address), sizeof(address)) == 0 && listen(listener_, 1) == 0,
              "proxy bind/listen");
        socklen_t length = sizeof(address);
        check(getsockname(listener_, reinterpret_cast<sockaddr*>(&address), &length) == 0, "proxy port");
        port = ntohs(address.sin_port);
        worker_ = std::thread(
            [this, upstream]
            {
                try
                {
                    run(upstream);
                }
                catch (...)
                {
                    failure_ = std::current_exception();
                }
            });
    }
    ~CommitLossProxy()
    {
        shutdown(listener_, SHUT_RDWR);
        if (client_.load() >= 0)
            shutdown(client_.load(), SHUT_RDWR);
        if (server_.load() >= 0)
            shutdown(server_.load(), SHUT_RDWR);
        if (worker_.joinable())
            worker_.join();
        if (client_.load() >= 0)
            ::close(client_.load());
        if (server_.load() >= 0)
            ::close(server_.load());
        ::close(listener_);
    }
    void join()
    {
        if (worker_.joinable())
            worker_.join();
        if (failure_)
            std::rethrow_exception(failure_);
    }
};
Json lost_commit()
{
    auto plain = configuration(1, true);
    Scope observer_scope(plain);
    auto observer = observer_scope.open();
    execute(observer, "DROP TABLE IF EXISTS b02_commit_loss");
    execute(observer, "CREATE TABLE b02_commit_loss(id int PRIMARY KEY,result text NOT NULL)");
    const auto slash = plain.provider.connection.find('/', plain.provider.connection.find('@'));
    const auto colon = plain.provider.connection.rfind(':', slash);
    check(colon != std::string::npos && slash != std::string::npos, "explicit test PostgreSQL port");
    const auto port =
        static_cast<std::uint16_t>(std::stoul(plain.provider.connection.substr(colon + 1, slash - colon - 1)));
    CommitLossProxy proxy(port);
    plain.provider.connection = "postgresql://korda@127.0.0.1:" + std::to_string(proxy.port) + "/postgres";
    Scope client_scope(plain);
    auto client = client_scope.open();
    auto tx = begin(client);
    execute(client, "INSERT INTO b02_commit_loss VALUES(1,'durable')");
    check(rejected([&] { (void)tx->finish(true, token(), client->admit(true)); }).code == 9,
          "commit response loss Unknown category");
    proxy.join();
    tx->join();
    check(proxy.observed.load() && tx->outcome == 3 && tx->retired, "Unknown preserved after joined cleanup");
    check(scalar(observer, "SELECT count(*) FROM b02_commit_loss WHERE result='durable'") == 1,
          "independent durable COMMIT oracle");
    check(tx->outcome == 3, "observer must not rewrite client uncertainty");
    return {{"case", "commit-response-loss"},    {"proxy_saw_commit_and_idle", true},
            {"client_outcome", "Unknown"},       {"durable_rows", 1},
            {"cleanup_preserved_unknown", true}, {"native_retired", tx->retired}};
}
Json conflict_snapshot()
{
    Scope scope(configuration(3));
    auto observer = scope.open(), holder = scope.open(), contender = scope.open();
    execute(observer, "DROP TABLE IF EXISTS b02_conflict_snapshot");
    execute(observer, "CREATE TABLE b02_conflict_snapshot(key text PRIMARY KEY,operation_id bigint)");
    auto pid = scalar(contender, "SELECT pg_backend_pid()");
    auto tx = begin(holder);
    execute(holder, "INSERT INTO b02_conflict_snapshot VALUES('key',1)");
    auto pending = std::async(
        std::launch::async,
        [&]
        {
            return rows(contender, "WITH inserted AS (INSERT INTO b02_conflict_snapshot VALUES('key',2) ON "
                                   "CONFLICT(key) DO NOTHING RETURNING operation_id) SELECT operation_id FROM inserted "
                                   "UNION ALL SELECT operation_id FROM b02_conflict_snapshot WHERE key='key' LIMIT 1");
        });
    eventually(
        [&]
        {
            return scalar(observer,
                          "SELECT count(*) FROM pg_locks WHERE pid=" + std::to_string(pid) + " AND NOT granted") > 0;
        },
        "contender actual lock wait");
    finish(tx, true);
    check(pending.get().empty(), "same statement snapshot yields no fallback row");
    check(scalar(contender, "SELECT operation_id FROM b02_conflict_snapshot WHERE key='key'") == 1,
          "separate statement sees winner");
    return {{"case", "conflict-cte-snapshot"},
            {"synchronized_on_server_lock", true},
            {"same_statement_rows", 0},
            {"separate_statement_operation", 1}};
}
Json quarantine()
{
    auto config = configuration(2);
    config.provider.statement_timeout_ns = 2'000'000'000;
    config.provider.cleanup_timeout_ns = 20'000'000;
    Scope scope(config);
    auto observer = scope.open(), session = scope.open();
    auto pid = scalar(session, "SELECT pg_backend_pid()");
    auto stream = session->connection->query("SELECT pg_sleep(1),1", {}, {});
    auto stopped = token();
    auto pending = std::async(std::launch::async, [&] { return stream->next_text({stopped}); });
    eventually(
        [&]
        {
            return scalar(observer, "SELECT count(*) FROM pg_stat_activity WHERE pid=" + std::to_string(pid) +
                                        " AND wait_event='PgSleep'") == 1;
        },
        "actual sleeping backend before stream stop");
    stopped->request();
    (void)rejected([&] { (void)pending.get(); });
    stream->close();
    check(!session->connection->healthy(), "20ms protocol drain timeout quarantines physical connection");
    close(session);
    check(session->retired.load() && session->disposition == 3, "quarantined owner retired");
    (void)rejected([&] { execute(session, "SELECT 1"); });
    auto replacement = scope.open();
    check(scalar(replacement, "SELECT pg_backend_pid()") != pid, "drain timeout backend cannot be reused");
    close(replacement);
    auto victim = scope.open();
    auto victim_pid = scalar(victim, "SELECT pg_backend_pid()");
    execute(observer, "SELECT pg_terminate_backend(" + std::to_string(victim_pid) + ")");
    (void)rejected([&] { execute(victim, "SELECT 1"); });
    close(victim);
    check(victim->disposition == 3, "broken transport physically discarded");
    return {{"case", "quarantine"},     {"drain_timeout_pid", pid},     {"replacement_different_pid", true},
            {"broken_pid", victim_pid}, {"native_owners_joined", true}, {"pool_permits_restored", true}};
}
Json migration_lock()
{
    Scope scope(configuration(2));
    auto observer = scope.open(), admin = scope.open();
    auto pid = scalar(admin, "SELECT pg_backend_pid()");
    execute(admin, "SELECT pg_advisory_lock(90202001)");
    check(scalar(observer,
                 "SELECT count(*) FROM pg_locks WHERE pid=" + std::to_string(pid) + " AND locktype='advisory'") == 1,
          "dirty dedicated lease negative control");
    // SQLx 0.9's mismatched-checksum path left this lock held. The frozen Rust
    // evidence retains that defect; the reusable contract is dedicated lease
    // quarantine after an audited session-mutating command.
    auto expert = plan(admin, "SELECT 1", 2);
    (void)admin->execute(expert, token(), admin->admit(false));
    check(admin->retired.load() && admin->disposition == 3, "expert dedicated owner physically closed");
    eventually(
        [&]
        {
            return scalar(observer, "SELECT count(*) FROM pg_locks WHERE pid=" + std::to_string(pid) +
                                        " AND locktype='advisory'") == 0;
        },
        "physical close released advisory lock");
    return {{"case", "migration-lock-cleanup"},
            {"negative_control_locks", 1},
            {"after_product_dedicated_retirement_locks", 0},
            {"dedicated_pid", pid},
            {"native_retired", true},
            {"legacy_defect", "SQLx checksum-mismatch lock retention is isolated as explicit contaminated-lease setup; "
                              "migration history/checksum contracts run in native.administration"}};
}
Json close_with_admitted_operation()
{
    Scope scope;
    auto session = scope.open();
    auto pid = scalar(session, "SELECT pg_backend_pid()");
    auto gate = session->admit(false);
    auto command = plan(session, "SELECT pg_sleep(0.15)");
    auto pending = std::async(std::launch::async, [&] { return session->execute(command, token(), gate); });
    // The admitted gate precedes request_close, independently of thread dispatch.
    session->request_close();
    std::this_thread::sleep_for(10ms);
    check(!session->retired.load(), "pending admitted command prevents close retirement");
    {
        std::lock_guard lock(scope.source->mutex);
        check(scope.source->leased == 1, "pending close retains pool permit");
    }
    (void)pending.get();
    session->join();
    check(session->disposition == 0, "admitted operation drained before reusable close");
    auto replacement = scope.open();
    check(scalar(replacement, "SELECT pg_backend_pid()") == pid, "admitted close safely reuses backend");
    return {{"case", "admitted-operation-close"},
            {"admitted_before_close", true},
            {"pending_close_keeps_pool_permit", true},
            {"retired_after_operation", true},
            {"same_backend_pid", pid}};
}
void run()
{
    Json cases = Json::array();
    for (auto function :
         {transaction_parent_retirement, tls, timeout_rollback, stream_and_admission, codecs, savepoints, lost_commit,
          conflict_snapshot, quarantine, migration_lock, close_with_admitted_operation})
    {
        auto result = function();
        std::cout << "PASS " << result["case"] << std::endl;
        cases.push_back(std::move(result));
    }
    eventually(
        []
        {
            return source_count.load() == 0 && session_count.load() == 0 && operation_count.load() == 0 &&
                   cursor_count.load() == 0 && buffer_count.load() == 0;
        },
        "all native owners retired after B02 suite");
    std::ofstream output(environment("NW_PG_REPORT"));
    check(output.good(), "B02 report output");
    output << Json{{"schema_version", 1},
                   {"platform", "linux-x64"},
                   {"cases", cases},
                   {"component_only", true},
                   {"full_b04_host", false}}
                  .dump(2)
           << '\n';
}
} // namespace
NW_FIXTURE_API std::uint32_t NW_CDECL nw_postgres_fixture_run_v1() noexcept
{
    try
    {
        run();
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << "B02 fixture failure: " << error.what() << std::endl;
        return 1;
    }
    catch (...)
    {
        return 2;
    }
}
NW_FIXTURE_API std::uint32_t NW_CDECL nw_postgres_fixture_options_v1(const std::uint8_t* record,
                                                                     std::uint32_t length) noexcept
{
    if (record == nullptr || length > 8192)
        return 1;
    // Called during serial option setup before the fixture starts any Host.
    // NOLINTNEXTLINE(concurrency-mt-unsafe)
    if (std::getenv("NW_PG_URL") == nullptr)
        return 2;
    try
    {
        (void)from_record({record, length});
        return 0;
    }
    catch (...)
    {
        return 3;
    }
}
