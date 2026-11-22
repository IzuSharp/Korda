// SPDX-License-Identifier: MIT
// Real PostgreSQL protocol/Host regressions for the fixed Rust explicit drains.
#include "nw/host.hpp"

#include <algorithm>
#include <arpa/inet.h>
#include <array>
#include <cerrno>
#include <cstring>
#include <future>
#include <iostream>
#include <netinet/in.h>
#include <stdexcept>
#include <sys/socket.h>
#include <unistd.h>

namespace nw
{
std::uint64_t module_identity()
{
    return 0x504753594e433031ULL;
}
} // namespace nw

namespace
{
using namespace nw;
std::size_t checks = 0;
void check(bool value, std::string_view message)
{
    ++checks;
    if (!value)
        throw std::runtime_error(std::string(message));
}
void proxy_check(bool value, const char* message)
{
    if (!value)
        throw std::runtime_error(message);
}
template <class F> Error rejected(F&& action)
{
    try
    {
        action();
    }
    catch (const Error& error)
    {
        ++checks;
        return error;
    }
    throw std::runtime_error("expected native rejection");
}
template <class F> void eventually(F&& predicate, std::string_view message)
{
    const auto deadline = Clock::now() + std::chrono::seconds(5);
    while (!predicate())
    {
        if (Clock::now() >= deadline)
            check(false, message);
        std::this_thread::yield();
    }
}
Bytes bytes(std::string_view value)
{
    return {value.begin(), value.end()};
}
struct Snapshot
{
    std::uint32_t state, outcome, cleanup, disposition;
    bool released, deadline;
};
Snapshot snapshot(ByteView value)
{
    const auto fields = Fields::parse(value, 7,
                                      {{1, 1, true},
                                       {2, 1, true},
                                       {3, 1, true},
                                       {4, 1, true},
                                       {5, 1, true},
                                       {6, 1, true},
                                       {7, 1, true},
                                       {8, 2, true},
                                       {9, 4, false}});
    return {fields.u32(1), fields.u32(2), fields.u32(4), fields.u32(5), fields.boolean(6), fields.boolean(7)};
}

// One live proxied connection at a time, accepting a replacement after discard.
// It forwards every complete frame unchanged except one explicitly armed pure
// Sync request. No SQL probe or changed business-fault ordinal is involved.
class SyncProxy
{
    int listener_ = -1;
    std::atomic<int> client_{-1}, server_{-1};
    std::atomic<bool> stopped_{false};
    std::thread worker_;
    std::mutex mutex_;
    std::condition_variable changed_;
    bool armed_ = false, after_commit_ = false, hold_ = false, released_ = false, allow_command_ = false;
    bool pause_initialization_ = false;
    unsigned target_ = 0, ordinal_ = 0;
    std::exception_ptr failure_;

    static bool read_all(int fd, std::span<std::uint8_t> data)
    {
        while (!data.empty())
        {
            const auto count = recv(fd, data.data(), data.size(), 0);
            if (count == 0)
                return false;
            if (count < 0)
            {
                if (errno == EINTR)
                    continue;
                throw std::runtime_error("proxy receive failed");
            }
            data = data.subspan(static_cast<std::size_t>(count));
        }
        return true;
    }
    static void send_all(int fd, ByteView data)
    {
        while (!data.empty())
        {
            const auto count = send(fd, data.data(), data.size(), MSG_NOSIGNAL);
            if (count < 0 && errno == EINTR)
                continue;
            proxy_check(count > 0, "proxy send failed");
            data = data.subspan(static_cast<std::size_t>(count));
        }
    }
    static Bytes frame(int fd, std::size_t header)
    {
        Bytes result(header);
        if (!read_all(fd, result))
            return {};
        const auto offset = header - 4;
        std::uint32_t size = 0;
        for (std::size_t i = offset; i < header; ++i)
            size = (size << 8) | result[i];
        proxy_check(size >= 4 && size <= MAX_BYTES, "bounded proxy frame");
        result.resize(offset + size);
        proxy_check(read_all(fd, std::span(result).subspan(header)), "complete proxy frame");
        return result;
    }
    void relay(int client, std::uint16_t upstream)
    {
        const int server = socket(AF_INET, SOCK_STREAM, 0);
        proxy_check(server >= 0, "proxy upstream socket");
        server_.store(server);
        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        address.sin_port = htons(upstream);
        proxy_check(connect(server, reinterpret_cast<sockaddr*>(&address), sizeof(address)) == 0,
                    "proxy upstream connect");
        const auto startup = frame(client, 4);
        proxy_check(!startup.empty(), "proxy startup");
        send_all(server, startup);
        std::atomic<bool> committed{false}, ending{false};
        std::exception_ptr request_failure;
        std::thread requests(
            [&]
            {
                try
                {
                    bool has_command = false;
                    while (!stopped_.load())
                    {
                        auto data = frame(client, 5);
                        if (data.empty())
                            break;
                        const auto tag = data[0];
                        if (tag == 'P')
                        {
                            const auto body = ByteView(data).subspan(5);
                            const auto separator = std::find(body.begin(), body.end(), 0);
                            if (separator != body.end())
                            {
                                const auto offset = static_cast<std::size_t>(separator - body.begin()) + 1;
                                const auto sql = std::string_view(reinterpret_cast<const char*>(body.data() + offset),
                                                                  body.size() - offset);
                                std::unique_lock lock(mutex_);
                                if (pause_initialization_ && sql.starts_with("SET statement_timeout TO "))
                                {
                                    pause_initialization_ = false;
                                    initialization_paused.store(true);
                                    changed_.notify_all();
                                    changed_.wait(lock, [&] { return released_ || stopped_.load(); });
                                }
                            }
                        }
                        const bool standalone = tag == 'S' && !has_command;
                        if (tag == 'S')
                            has_command = false;
                        else
                            has_command = true;
                        bool fault = false;
                        {
                            std::unique_lock lock(mutex_);
                            if (armed_ && (!after_commit_ || committed.load()))
                            {
                                if (!standalone && !allow_command_)
                                    unexpected_command.store(true);
                                if (standalone && ++ordinal_ == target_)
                                {
                                    armed_ = false;
                                    fault = true;
                                    faulted.store(true);
                                    changed_.notify_all();
                                    if (hold_)
                                        changed_.wait(lock, [&] { return released_ || stopped_.load(); });
                                }
                            }
                        }
                        if (fault)
                            break;
                        send_all(server, data);
                    }
                }
                catch (...)
                {
                    if (!ending.load() && !stopped_.load() && !faulted.load())
                        request_failure = std::current_exception();
                }
                ending.store(true);
                shutdown(client, SHUT_RDWR);
                shutdown(server, SHUT_RDWR);
            });
        try
        {
            bool commit_tag = false;
            while (!stopped_.load())
            {
                auto data = frame(server, 5);
                if (data.empty())
                    break;
                const auto body = std::string_view(reinterpret_cast<const char*>(data.data() + 5), data.size() - 5);
                if (data[0] == 'C')
                    commit_tag = body == std::string_view("COMMIT\0", 7);
                if (data[0] == 'Z' && commit_tag && body == "I")
                    committed.store(true);
                send_all(client, data);
            }
        }
        catch (...)
        {
            if (!ending.load() && !stopped_.load() && !faulted.load())
                failure_ = std::current_exception();
        }
        ending.store(true);
        shutdown(client, SHUT_RDWR);
        shutdown(server, SHUT_RDWR);
        requests.join();
        if (request_failure && !failure_)
            failure_ = request_failure;
        ::close(server);
        server_.store(-1);
    }

  public:
    std::atomic<bool> faulted{false}, unexpected_command{false}, initialization_paused{false};
    std::uint16_t port{};
    explicit SyncProxy(std::uint16_t upstream)
    {
        listener_ = socket(AF_INET, SOCK_STREAM, 0);
        check(listener_ >= 0, "proxy listener");
        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        check(bind(listener_, reinterpret_cast<sockaddr*>(&address), sizeof(address)) == 0 && listen(listener_, 4) == 0,
              "proxy bind/listen");
        socklen_t size = sizeof(address);
        check(getsockname(listener_, reinterpret_cast<sockaddr*>(&address), &size) == 0, "proxy port");
        port = ntohs(address.sin_port);
        worker_ = std::thread(
            [this, upstream]
            {
                try
                {
                    while (!stopped_.load())
                    {
                        const int client = accept(listener_, nullptr, nullptr);
                        if (client < 0)
                        {
                            if (stopped_.load())
                                return;
                            if (errno == EINTR)
                                continue;
                            throw std::runtime_error("proxy accept");
                        }
                        client_.store(client);
                        relay(client, upstream);
                        ::close(client);
                        client_.store(-1);
                    }
                }
                catch (...)
                {
                    failure_ = std::current_exception();
                }
            });
    }
    ~SyncProxy()
    {
        stop();
    }
    void arm(unsigned ordinal, bool after_commit, bool hold = false, bool allow_command = false)
    {
        std::lock_guard lock(mutex_);
        target_ = ordinal;
        after_commit_ = after_commit;
        hold_ = hold;
        allow_command_ = allow_command;
        released_ = false;
        ordinal_ = 0;
        faulted.store(false);
        unexpected_command.store(false);
        armed_ = true;
    }
    void pause_initialization()
    {
        std::lock_guard lock(mutex_);
        released_ = false;
        pause_initialization_ = true;
    }
    void release()
    {
        std::lock_guard lock(mutex_);
        released_ = true;
        changed_.notify_all();
    }
    void stop()
    {
        if (stopped_.exchange(true))
            return;
        release();
        shutdown(listener_, SHUT_RDWR);
        if (client_.load() >= 0)
            shutdown(client_.load(), SHUT_RDWR);
        if (server_.load() >= 0)
            shutdown(server_.load(), SHUT_RDWR);
        if (worker_.joinable())
            worker_.join();
        ::close(listener_);
    }
    void verify(bool expect_fault = true)
    {
        stop();
        if (failure_)
            std::rethrow_exception(failure_);
        check(!expect_fault || faulted.load(), "real pure Sync fault was reached");
        check(!unexpected_command.load(), "fault targeted a standalone Sync, not a later business command");
    }
};

std::int64_t scalar(const std::shared_ptr<ProviderConnection>& connection, std::string_view sql)
{
    const auto rows = connection->query_text(sql, {}, 1);
    check(rows.size() == 1 && rows[0].size() == 1 && rows[0][0], "scalar shape");
    return std::stoll(*rows[0][0]);
}
HostConfig configuration(const std::string& uri)
{
    HostConfig config{};
    config.provider.profile = Profile::PostgreSql;
    config.provider.connection = uri;
    config.provider.verify_tls = false;
    config.provider.cleanup_timeout_ns = 250'000'000;
    config.name = "protocol-sync-regression";
    config.limits = {1,    16,        64, 65536,     64, 65536,         65536,        4096,
                     4096, MAX_BYTES, 64, MAX_BYTES, 64, 2'000'000'000, 5'000'000'000};
    return config;
}
std::shared_ptr<Session> open(const std::shared_ptr<Source>& source,
                              std::shared_ptr<Cancel> cancel = std::make_shared<Cancel>())
{
    {
        std::lock_guard lock(source->mutex);
        ++source->leased;
    }
    return Session::open_reserved(source, std::move(cancel));
}
std::shared_ptr<SqlPlan> plan(const std::shared_ptr<Source>& source, std::string sql, bool rows = false)
{
    auto result = std::make_shared<SqlPlan>();
    result->source = source;
    result->sql = std::move(sql);
    const auto schema = bytes("{\"version\":1,\"parameters\":[]}");
    result->expectation = {1, Profile::PostgreSql, sha256(schema), 0, 0, {}, 1};
    result->parameters = Batch::encode(result->expectation, 1, {});
    result->timeout_ns = 5'000'000'000;
    result->result_kind = rows ? 3 : 0;
    if (rows)
    {
        auto projection = std::make_shared<Projection>(
            Projection::parse(bytes("{\"version\":1,\"fields\":[{\"ordinal\":0,\"name\":\"v\",\"codec\":3,"
                                    "\"codec_config\":null,\"nullable\":false}]}"),
                              Profile::PostgreSql));
        result->projection = source->resolve_projection({source->identity, 1, projection, source->codecs()});
    }
    return result;
}
Value execute(const std::shared_ptr<Session>& session, std::string sql, bool rows = false)
{
    return session->execute(plan(session->source, std::move(sql), rows), std::make_shared<Cancel>(),
                            session->admit(false));
}
void close(const std::shared_ptr<Session>& session)
{
    session->request_close();
    session->join();
}
void released(const std::shared_ptr<Source>& source)
{
    std::lock_guard lock(source->mutex);
    check(source->leased == 0 && source->idle.empty(), "discarded physical lease completed before publication");
}
void host_permit_retained(const std::shared_ptr<Source>& source)
{
    std::lock_guard lock(source->mutex);
    check(source->leased == 1 && source->idle.empty(),
          "physically discarded provider must retain its separate Host Session admission permit");
}
nw_status NW_CDECL completed(std::uint64_t, nw_status, const nw_error_info_v1*)
{
    return NW_OK;
}
void retained_admission_bound(const std::shared_ptr<Source>& source)
{
    auto waiting = DbOperation::create(
        source, 1, false, [source](std::shared_ptr<Cancel> cancel) -> Value
        { return std::make_shared<Owner>(source, Session::open_reserved(source, std::move(cancel))); },
        DbOperation::Dispatch::Admission);
    check(waiting->start({sizeof(nw_operation_callbacks_v1), 0, 1, completed}) == NW_OK,
          "real replacement Session admission starts");
    eventually([&] { return waiting->core->snapshot().terminal_status.has_value(); },
               "replacement admission reaches its original finite deadline");
    check(waiting->core->snapshot().terminal_status == NW_DOMAIN_ERROR,
          "bad but live Host Session still consumes MaxConnections");
    {
        std::lock_guard lock(waiting->mutex);
        check(waiting->error && waiting->error->code == 6,
              "replacement Session retains original capacity refusal before Host close");
    }
    eventually([&] { return waiting->core->try_retire() == NW_OK; }, "replacement admission operation retires");
    waiting.reset();
    host_permit_retained(source);
}
std::uint16_t port_of(const std::string& uri)
{
    const auto slash = uri.find('/', uri.find('@'));
    const auto colon = uri.rfind(':', slash);
    check(slash != std::string::npos && colon != std::string::npos, "isolated URI has explicit port");
    return static_cast<std::uint16_t>(std::stoul(uri.substr(colon + 1, slash - colon - 1)));
}
std::string proxy_uri(const std::string& uri, std::uint16_t port)
{
    const auto slash = uri.find('/', uri.find('@'));
    const auto colon = uri.rfind(':', slash);
    return uri.substr(0, colon + 1) + std::to_string(port) + uri.substr(slash);
}
void protocol_states(const std::string& uri)
{
    auto connection = ProviderConnection::open(configuration(uri).provider, {});
    const auto pid = scalar(connection, "SELECT pg_backend_pid()");
    connection->synchronize_protocol();
    check(scalar(connection, "SELECT 7::bigint") == 7, "idle Sync exits pipeline mode");
    (void)connection->execute("BEGIN", {}, {});
    connection->synchronize_protocol();
    check(connection->in_transaction(), "Sync preserves active transaction");
    check(rejected([&] { (void)connection->execute("SELECT 1/0", {}, {}); }).provider == "22012",
          "original failed transaction setup");
    connection->synchronize_protocol();
    check(connection->in_transaction() && connection->healthy(), "Sync confirms protocol idle inside failed tx");
    check(rejected([&] { (void)connection->execute("SELECT 1", {}, {}); }).provider == "25P02",
          "pure Sync does not execute SQL or heal failed transaction");
    (void)connection->execute("ROLLBACK", {}, {});
    connection->synchronize_protocol();
    check(!connection->in_transaction() && scalar(connection, "SELECT pg_backend_pid()") == pid,
          "same connection remains usable in ordinary nonpipeline mode");
    connection->close();
}
void commit_fault(const std::string& uri, unsigned ordinal, bool hold)
{
    SyncProxy proxy(port_of(uri));
    auto source = Source::create(configuration(proxy_uri(uri, proxy.port)));
    auto session = open(source);
    auto observer = ProviderConnection::open(configuration(uri).provider, {});
    const auto pid = scalar(session->connection, "SELECT pg_backend_pid()");
    const auto table = "nw_sync_commit_" + std::to_string(pid);
    (void)observer->execute("CREATE TABLE " + table + "(id bigint primary key)", {}, {});
    TransactionOptions options{};
    options.commit_timeout_ns = 2'000'000'000;
    options.cleanup_timeout_ns = hold ? 30'000'000 : 2'000'000'000;
    auto tx = Transaction::begin(session, options, std::make_shared<Cancel>(), session->admit(true));
    (void)execute(session, "INSERT INTO " + table + " VALUES(1)");
    proxy.arm(ordinal, true, hold);
    if (ordinal == 1)
        check(rejected([&] { (void)tx->finish(true, std::make_shared<Cancel>(), session->admit(true)); }).code == 9,
              "first post-COMMIT confirmation failure remains CommitUnknown");
    else
    {
        (void)tx->finish(true, std::make_shared<Cancel>(), session->admit(true));
        check(snapshot(tx->snapshot()).outcome == 1, "Committed primary survives separate recovery failure");
    }
    if (hold)
    {
        eventually([&] { return proxy.faulted.load(); }, "recovery Sync hold reached");
        eventually([&] { return snapshot(tx->snapshot()).deadline; }, "bounded cleanup waiter published deadline");
        const auto pending = snapshot(tx->snapshot());
        check(pending.outcome == 1 && !pending.released, "deadline retains committed owner before native cleanup");
        {
            std::lock_guard lock(source->mutex);
            check(source->leased == 1, "native cleanup deadline cannot return the physical permit early");
        }
    }
    tx->join();
    proxy.release();
    const auto receipt = snapshot(tx->snapshot());
    check(receipt.state == 4 && receipt.outcome == (ordinal == 1 ? 3U : 1U) && receipt.cleanup == 4 &&
              receipt.disposition == 3 && receipt.released,
          "real transaction primary/cleanup/disposition matches fixed Rust oracle");
    host_permit_retained(source);
    if (ordinal == 2 && !hold)
        retained_admission_bound(source);
    check(scalar(observer, "SELECT count(*) FROM " + table) == 1,
          "independent SQL confirms durable write without rewriting Unknown");
    close(session);
    released(source);
    const auto parent = snapshot(session->snapshot());
    check(parent.cleanup == (ordinal == 1 ? 4U : 2U) && parent.disposition == 3 && parent.released,
          "Session retains rollback-cleanup failure separately from local retirement");
    auto replacement = open(source);
    check(scalar(replacement->connection, "SELECT pg_backend_pid()") != pid, "discarded backend is never reused");
    close(replacement);
    source->join();
    proxy.verify();
    (void)observer->execute("DROP TABLE " + table, {}, {});
    observer->close();
}
void checkin_fault(const std::string& uri, unsigned ordinal)
{
    SyncProxy proxy(port_of(uri));
    auto source = Source::create(configuration(proxy_uri(uri, proxy.port)));
    auto session = open(source);
    const auto pid = scalar(session->connection, "SELECT pg_backend_pid()");
    TransactionOptions options{};
    options.commit_timeout_ns = options.cleanup_timeout_ns = 2'000'000'000;
    auto tx = Transaction::begin(session, options, std::make_shared<Cancel>(), session->admit(true));
    (void)tx->finish(true, std::make_shared<Cancel>(), session->admit(true));
    tx->join();
    const auto transaction = snapshot(tx->snapshot());
    check(transaction.outcome == 1 && transaction.cleanup == 2 && transaction.disposition == 0 && transaction.released,
          "healthy commit and recover complete before check-in fault");
    proxy.arm(ordinal, false);
    close(session);
    const auto receipt = snapshot(session->snapshot());
    check(receipt.cleanup == 2 && receipt.disposition == (ordinal == 1 ? 3U : 0U) && receipt.released,
          "Session receipt preserves the distinct explicit and hidden pool-return failure stages");
    released(source);
    auto replacement = open(source);
    check(scalar(replacement->connection, "SELECT pg_backend_pid()") != pid, "check-in failure replaced backend");
    close(replacement);
    source->join();
    proxy.verify();
}
void idle_reuse(const std::string& uri)
{
    auto source = Source::create(configuration(uri));
    auto session = open(source);
    auto observer = ProviderConnection::open(configuration(uri).provider, {});
    const auto pid = scalar(session->connection, "SELECT pg_backend_pid()");
    const auto table = "nw_sync_idle_" + std::to_string(pid);
    (void)observer->execute("CREATE TABLE " + table + "(id bigint primary key)", {}, {});
    (void)execute(session, "INSERT INTO " + table + " VALUES(1)");
    close(session);
    auto healthy = open(source);
    check(scalar(healthy->connection, "SELECT pg_backend_pid()") == pid,
          "successful pool test-before-acquire keeps the same healthy backend");
    close(healthy);
    check(scalar(observer, "SELECT pg_terminate_backend(" + std::to_string(pid) + ",2000)::int") == 1,
          "independent connection terminates and observes the idle backend exit");
    auto replacement = open(source);
    (void)execute(replacement, "INSERT INTO " + table + " VALUES(2)");
    check(scalar(replacement->connection, "SELECT pg_backend_pid()") != pid,
          "a dead idle connection is replaced before publishing the next Host Session");
    check(scalar(observer, "SELECT count(*) FROM " + table) == 2,
          "the first business write after idle replacement succeeds and persists");
    close(replacement);
    source->join();
    (void)observer->execute("DROP TABLE " + table, {}, {});
    observer->close();
}
void idle_acquisition_bound(const std::string& uri, bool canceled)
{
    SyncProxy proxy(port_of(uri));
    auto source = Source::create(configuration(proxy_uri(uri, proxy.port)));
    auto session = open(source);
    close(session);
    auto cancel = std::make_shared<Cancel>();
    proxy.arm(1, false, true);
    const auto started = Clock::now();
    auto acquisition = std::async(std::launch::async,
                                  [&]() -> Error
                                  {
                                      try
                                      {
                                          (void)open(source, cancel);
                                      }
                                      catch (const Error& error)
                                      {
                                          return error;
                                      }
                                      throw std::runtime_error("held idle acquisition unexpectedly succeeded");
                                  });
    eventually([&] { return proxy.faulted.load(); }, "idle test reaches its real standalone protocol Sync");
    if (canceled)
        cancel->request();
    if (acquisition.wait_for(std::chrono::seconds(4)) != std::future_status::ready)
    {
        proxy.release();
        acquisition.wait();
        check(false, "idle acquisition exceeded its independent finite pool deadline");
    }
    const auto error = acquisition.get();
    const auto elapsed = Clock::now() - started;
    proxy.release();
    check(error.status == (canceled ? NW_CANCELED : NW_DOMAIN_ERROR) && error.code == (canceled ? 8U : 1U) &&
              !error.provider,
          "pool timeout remains provider category 1 while explicit cancellation keeps its original category");
    if (!canceled)
        check(elapsed >= std::chrono::milliseconds(1500),
              "idle acquisition uses its two-second pool budget, not the 250ms provider cleanup budget");
    released(source);
    source->join();
    proxy.verify();
}
void source_close_receipt(const std::string& uri)
{
    auto source = Source::create(configuration(uri));
    auto session = open(source);
    source->request_close();
    session->join();
    source->join();
    const auto receipt = snapshot(session->snapshot());
    check(receipt.cleanup == 2 && receipt.disposition == 0 && receipt.released,
          "Source shutdown preserves the original pool-return receipt despite physically closing the lease");
    released(source);
}
void initialization_budget(const std::string& uri)
{
    SyncProxy proxy(port_of(uri));
    auto source = Source::create(configuration(proxy_uri(uri, proxy.port)));
    proxy.pause_initialization();
    auto opening = std::async(std::launch::async, [&] { return open(source); });
    eventually([&] { return proxy.initialization_paused.load(); },
               "actual from-lease statement timeout initialization reached the wire gate");
    const bool still_initializing = opening.wait_for(std::chrono::milliseconds(2100)) == std::future_status::timeout;
    proxy.release();
    check(still_initializing, "from-lease initialization remains owned after the pool-acquire deadline passes");
    auto session = opening.get();
    (void)execute(session, "SELECT 1");
    close(session);
    source->join();
    proxy.verify(false);
}
void stream_fault(const std::string& uri, bool eof)
{
    SyncProxy proxy(port_of(uri));
    auto source = Source::create(configuration(proxy_uri(uri, proxy.port)));
    auto session = open(source);
    auto value = execute(session, "SELECT n::bigint AS v FROM generate_series(1,3) AS n", true);
    auto owner = std::get<std::shared_ptr<Owner>>(value);
    auto cursor = std::get<std::shared_ptr<Cursor>>(owner->data);
    std::shared_ptr<Buffer> rows;
    if (!eof)
        rows = cursor->fetch(std::make_shared<Cancel>(), 1, 65536);
    proxy.arm(1, false, false, eof);
    if (eof)
        rows = cursor->fetch(std::make_shared<Cancel>(), 8, 65536);
    cursor->request_close();
    cursor->join();
    check(Batch::parse(rows->bytes, cursor->expectation).rows() == (eof ? 3U : 1U),
          "retirement failure does not manufacture a business row error");
    const auto receipt = snapshot(cursor->snapshot());
    check(receipt.cleanup == 2 && receipt.disposition == 2 && receipt.released,
          "stream retains the original native-retired Quarantined receipt");
    host_permit_retained(source);
    rows.reset();
    cursor.reset();
    owner.reset();
    value = std::shared_ptr<Owner>{};
    close(session);
    released(source);
    source->join();
    proxy.verify();
}
void command_fault(const std::string& uri, bool describe, bool sql_error)
{
    SyncProxy proxy(port_of(uri));
    auto source = Source::create(configuration(proxy_uri(uri, proxy.port)));
    auto session = open(source);
    auto observer = ProviderConnection::open(configuration(uri).provider, {});
    const auto pid = scalar(session->connection, "SELECT pg_backend_pid()");
    const auto table = "nw_sync_command_" + std::to_string(pid);
    (void)observer->execute("CREATE TABLE " + table + "(id bigint primary key)", {}, {});
    const auto sql = describe
                         ? (sql_error ? "SELECT missing::bigint AS v FROM absent_sync_table" : "SELECT 1::bigint AS v")
                         : (sql_error ? "SELECT 1/0" : "INSERT INTO " + table + " VALUES(1)");
    // Actual execution/description precedes the confirmation being faulted.
    proxy.arm(1, false, false, true);
    const auto error = rejected([&] { (void)execute(session, sql, describe); });
    check(error.status == NW_DOMAIN_ERROR && error.code == 1,
          "execute/describe protocol failure retains public provider category");
    if (sql_error)
        check(error.provider == std::optional<std::string>(describe ? "42P01" : "22012"),
              "SQL error retains priority over following failed confirmation");
    else
        check(!error.provider, "successful SQL followed by failed confirmation reports cleanup error");
    host_permit_retained(source);
    check(scalar(observer, "SELECT count(*) FROM " + table) == (!describe && !sql_error ? 1 : 0),
          "independent SQL distinguishes executed write from failed/metadata-only operations");
    check(rejected([&] { (void)execute(session, "SELECT 1"); }).status == NW_DOMAIN_ERROR,
          "live Host owner reports its lower retired PostgreSQL provider category");
    close(session);
    released(source);
    const auto receipt = snapshot(session->snapshot());
    check(receipt.cleanup == 2 && receipt.disposition == 3 && receipt.released,
          "ordinary command cleanup does not invent failed transaction rollback");
    source->join();
    proxy.verify();
    (void)observer->execute("DROP TABLE " + table, {}, {});
    observer->close();
}
} // namespace

int main(int argc, char** argv)
{
    try
    {
        check(argc == 2, "postgres Sync tests require isolated PostgreSQL URI");
        const std::string uri = argv[1];
        std::cout << "CASE protocol states\n" << std::flush;
        protocol_states(uri);
        std::cout << "CASE first commit confirmation\n" << std::flush;
        commit_fault(uri, 1, false);
        std::cout << "CASE recovery confirmation\n" << std::flush;
        commit_fault(uri, 2, false);
        std::cout << "CASE bounded recovery owner\n" << std::flush;
        commit_fault(uri, 2, true);
        std::cout << "CASE check-in confirmation\n" << std::flush;
        checkin_fault(uri, 1);
        std::cout << "CASE internal pool-return confirmation\n" << std::flush;
        checkin_fault(uri, 2);
        std::cout << "CASE healthy and terminated idle reuse\n" << std::flush;
        idle_reuse(uri);
        std::cout << "CASE idle pool acquisition deadline\n" << std::flush;
        idle_acquisition_bound(uri, false);
        std::cout << "CASE idle pool acquisition cancellation\n" << std::flush;
        idle_acquisition_bound(uri, true);
        std::cout << "CASE Source close pool receipt\n" << std::flush;
        source_close_receipt(uri);
        std::cout << "CASE independent from-lease initialization\n" << std::flush;
        initialization_budget(uri);
        std::cout << "CASE partial stream retirement\n" << std::flush;
        stream_fault(uri, false);
        std::cout << "CASE EOF retirement\n" << std::flush;
        stream_fault(uri, true);
        for (bool describe : {false, true})
            for (bool sql_error : {false, true})
            {
                std::cout << "CASE " << (describe ? "describe" : "execute")
                          << (sql_error ? " error priority" : " confirmation") << '\n'
                          << std::flush;
                command_fault(uri, describe, sql_error);
            }
        eventually(
            []
            {
                return source_count.load() == 0 && session_count.load() == 0 && operation_count.load() == 0 &&
                       cursor_count.load() == 0 && buffer_count.load() == 0;
            },
            "all native resources retire after immediate Session/Source joins");
        std::cout << "PASS PostgreSQL protocol confirmations: " << checks << " checks\n";
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << "FAIL PostgreSQL protocol confirmations: " << error.what() << '\n';
        return 1;
    }
}
