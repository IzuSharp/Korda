// SPDX-License-Identifier: MIT
// B14 Host adapter regressions use the real product C ABI, not Host test hooks.
// Fixed Rust reference: native/database/relations/host.rs at 465cd0f.
#include "../../database/binding/korda_result_batch_v1.h"
#include "nw/contracts.hpp"
#include "nw_database_declarations.hpp"

#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <filesystem>
#include <iostream>
#include <memory>
#include <mutex>
#include <sqlite3.h>
#include <stdexcept>
#include <thread>

extern "C" std::uint64_t NW_CDECL nw_db_diagnostic_count_v1(std::uint32_t);

namespace
{
using namespace nw;
using Clock = std::chrono::steady_clock;
std::size_t assertions = 0;
void check(bool value, std::string_view message)
{
    ++assertions;
    if (!value)
        throw std::runtime_error(std::string(message));
}
Bytes bytes(std::string_view value)
{
    return {value.begin(), value.end()};
}
Bytes u32_bytes(std::uint32_t value)
{
    Bytes result(4);
    put_u32(result, 0, value);
    return result;
}
Bytes u64_bytes(std::uint64_t value)
{
    Bytes result(8);
    put_u64(result, 0, value);
    return result;
}
nw_bytes_view_v1 view(const Bytes& value)
{
    return {value.data(), value.size()};
}
nw_error_info_v1 error_slot()
{
    nw_error_info_v1 result{};
    result.struct_size = sizeof(result);
    return result;
}
void ok(nw_status status, const nw_error_info_v1& error, std::string_view message)
{
    check(status == NW_OK,
          std::string(message) + " status=" + std::to_string(status) + " code=" + std::to_string(error.code));
}
template <typename Predicate> void eventually(Predicate&& predicate, std::string_view message)
{
    const auto deadline = Clock::now() + std::chrono::seconds(5);
    while (!predicate())
    {
        if (Clock::now() >= deadline)
            check(false, message);
        std::this_thread::yield();
    }
}
ControlField u32(std::uint16_t id, std::uint32_t value)
{
    return {id, 1, u32_bytes(value)};
}
ControlField u64(std::uint16_t id, std::uint64_t value, std::uint16_t type = 2)
{
    return {id, type, u64_bytes(value)};
}
Bytes records_bytes(const std::vector<Bytes>& records)
{
    auto result = u32_bytes(static_cast<std::uint32_t>(records.size()));
    for (const auto& record : records)
    {
        const auto length = u32_bytes(static_cast<std::uint32_t>(record.size()));
        result.insert(result.end(), length.begin(), length.end());
        result.insert(result.end(), record.begin(), record.end());
    }
    return result;
}
struct Owner
{
    nw_db_ref value{};
    ~Owner()
    {
        nw_db_owner_release_v1(value);
    }
    void release()
    {
        nw_db_owner_release_v1(value);
        value = nullptr;
    }
};
Bytes copy_buffer(nw_db_buffer* buffer)
{
    const auto release = [](nw_db_buffer* value) { nw_db_buffer_release_v1(value); };
    std::unique_ptr<nw_db_buffer, decltype(release)> owner(buffer, release);
    nw_bytes_view_v1 data{};
    auto error = error_slot();
    ok(nw_db_buffer_view_v1(buffer, &data, &error), error, "actual buffer view");
    return {data.data, data.data + data.length};
}
struct Operation
{
    nw_db_ref value{};
    std::mutex mutex;
    std::condition_variable changed;
    bool started = false;
    bool done = false;
    nw_status terminal = NW_PENDING;
    nw_error_info_v1 failure{};
    ~Operation()
    {
        if (started)
        {
            // Keep the actual callback cookie alive through retirement even
            // when an assertion fails; a failed process must not create a UAF.
            (void)nw_db_operation_request_cancel_v1(value);
            const auto deadline = Clock::now() + std::chrono::seconds(10);
            while (nw_db_operation_try_retire_v1(value) == NW_PENDING)
            {
                if (Clock::now() >= deadline)
                    std::terminate();
                std::this_thread::yield();
            }
        }
        nw_db_operation_release_v1(value);
    }
    static nw_status NW_CDECL callback(std::uint64_t cookie, nw_status status, const nw_error_info_v1* error)
    {
        auto& operation = *reinterpret_cast<Operation*>(static_cast<std::uintptr_t>(cookie));
        {
            std::lock_guard lock(operation.mutex);
            operation.terminal = status;
            if (error)
                operation.failure = *error;
            operation.done = true;
        }
        operation.changed.notify_all();
        return NW_OK;
    }
    void run(nw_status expected = NW_OK, std::int32_t code = 0)
    {
        nw_operation_callbacks_v1 callbacks{sizeof(callbacks), 0, reinterpret_cast<std::uintptr_t>(this), callback};
        auto error = error_slot();
        ok(nw_db_operation_start_v1(value, &callbacks, &error), error, "actual operation start");
        started = true;
        {
            std::unique_lock lock(mutex);
            check(changed.wait_for(lock, std::chrono::seconds(5), [&] { return done; }), "actual terminal callback");
        }
        eventually([&] { return nw_db_operation_try_retire_v1(value) == NW_OK; }, "actual operation retirement");
        check(terminal == expected, "B14 terminal status differs: observed=" + std::to_string(terminal) +
                                        " expected=" + std::to_string(expected));
        if (expected != NW_OK)
            check(failure.domain == 0x4442 && failure.code == code && failure.correlation_id != 0,
                  "B14 terminal domain/code/correlation differs");
    }
    nw_db_ref take_owner(std::uint32_t kind)
    {
        nw_db_ref result{};
        auto error = error_slot();
        nw_db_batch_result_v1 wrong{};
        check(nw_db_operation_take_batch_v1(value, &wrong, &error) == NW_INVALID_STATE && !wrong.owner &&
                  !wrong.bytes.data && !wrong.bytes.length,
              "a wrong batch take must leave an owner result available");
        ok(nw_db_operation_take_owner_v1(value, kind, &result, &error), error, "actual owner transfer");
        return result;
    }
    std::int64_t take_count()
    {
        std::int64_t result = -1;
        auto error = error_slot();
        ok(nw_db_operation_take_count_v1(value, &result, &error), error, "actual affected count");
        return result;
    }
    Bytes take_buffer(bool failed = false)
    {
        nw_db_buffer* result{};
        auto error = error_slot();
        ok(failed ? nw_db_operation_take_error_v1(value, &result, &error)
                  : nw_db_operation_take_buffer_v1(value, &result, &error),
           error, "actual buffer transfer");
        if (!failed)
        {
            nw_db_batch_result_v1 twice{};
            check(nw_db_operation_take_batch_v1(value, &twice, &error) == NW_INVALID_STATE && !twice.owner &&
                      !twice.bytes.data && !twice.bytes.length,
                  "a legacy buffer take must consume the same additive batch result");
        }
        return copy_buffer(result);
    }
    Bytes take_batch()
    {
        nw_db_batch_result_v1 result{};
        auto error = error_slot();
        ok(nw_db_operation_take_batch_v1(value, &result, &error), error, "actual batch result transfer");
        std::unique_ptr<nw_db_buffer, decltype(&nw_db_buffer_release_v1)> owner(result.owner, nw_db_buffer_release_v1);
        check(result.owner && result.bytes.data && result.bytes.length >= 96 && result.known_end == 1 &&
                  result.reserved == 0,
              "one-row relation batch must carry its already observed EOF");
        nw_bytes_view_v1 same{};
        ok(nw_db_buffer_view_v1(owner.get(), &same, &error), error, "legacy view of transferred batch");
        check(same.data == result.bytes.data && same.length == result.bytes.length,
              "batch result must borrow the creating buffer allocation");
        auto twice = result;
        check(nw_db_operation_take_batch_v1(value, &twice, &error) == NW_INVALID_STATE && !twice.owner &&
                  !twice.bytes.data && !twice.bytes.length && !twice.known_end && !twice.reserved,
              "second batch take must reject and clear every output");
        auto legacy = result.owner;
        check(nw_db_operation_take_buffer_v1(value, &legacy, &error) == NW_INVALID_STATE && !legacy,
              "new and old buffer takes must share one transfer gate");
        return {result.bytes.data, result.bytes.data + result.bytes.length};
    }
};
void close_owner(Owner& owner)
{
    Operation operation;
    auto error = error_slot();
    ok(nw_db_close_create_v1(owner.value, &operation.value, &error), error, "actual close creation");
    operation.run();
    (void)operation.take_buffer();
    owner.release();
}
struct Sqlite
{
    sqlite3* value{};
    explicit Sqlite(const std::filesystem::path& path)
    {
        check(sqlite3_open(path.string().c_str(), &value) == SQLITE_OK, "independent SQLite open");
    }
    ~Sqlite()
    {
        sqlite3_close(value);
    }
    void execute(const char* sql)
    {
        check(sqlite3_exec(value, sql, nullptr, nullptr, nullptr) == SQLITE_OK, "independent SQLite setup");
    }
    std::int64_t count()
    {
        sqlite3_stmt* raw{};
        check(sqlite3_prepare_v2(value, "SELECT count(*) FROM b14_adapter_parent", -1, &raw, nullptr) == SQLITE_OK,
              "independent SQL prepare");
        const auto release = [](sqlite3_stmt* statement) { sqlite3_finalize(statement); };
        std::unique_ptr<sqlite3_stmt, decltype(release)> statement(raw, release);
        check(sqlite3_step(raw) == SQLITE_ROW, "independent SQL count");
        return sqlite3_column_int64(raw, 0);
    }
};
std::string column(std::uint32_t id, std::string_view name, std::uint32_t codec, std::string_view key)
{
    return "{\"id\":" + std::to_string(id) + ",\"name\":" + json_quote(name) + ",\"codec\":" + std::to_string(codec) +
           ",\"codec_config\":null,\"nullable\":false,\"key_ordinal\":" + std::string(key) +
           ",\"generated\":false,\"concurrency\":false,\"collation\":null,\"max_length\":null}";
}
std::shared_ptr<Model> model(bool parent)
{
    const auto logical = "Korda.B14.Adapter:" + std::string(parent ? "Parent" : "Child");
    const auto table = "b14_adapter_" + std::string(parent ? "parent" : "child");
    std::vector<std::pair<std::uint32_t, std::string>> columns;
    std::string projection;
    const auto add = [&](std::string_view name, std::uint32_t codec, std::string_view key)
    {
        const auto id = stable_id({"NWDB-COLUMN-ID-v1", logical, {}, table, name});
        columns.emplace_back(id, column(id, name, codec, key));
        if (!projection.empty())
            projection += ',';
        projection += std::to_string(id);
    };
    if (parent)
    {
        add("Tenant", 5, "0");
        add("Id", 5, "1");
    }
    else
    {
        add("Id", 3, "0");
        add("Tenant", 5, "null");
        add("ParentId", 5, "null");
    }
    std::sort(columns.begin(), columns.end());
    std::string fields;
    for (const auto& [id, encoded] : columns)
    {
        (void)id;
        if (!fields.empty())
            fields += ',';
        fields += encoded;
    }
    const auto table_id = stable_id({"NWDB-TABLE-ID-v1", logical, {}, table});
    const auto text = "{\"version\":1,\"logical_id\":" + json_quote(logical) +
                      ",\"table_id\":" + std::to_string(table_id) + ",\"schema\":null,\"table\":" + json_quote(table) +
                      ",\"columns\":[" + fields + "],\"projection\":[" + projection + "],\"relations\":[]}";
    return std::make_shared<Model>(Model::parse(bytes(text), Profile::Sqlite));
}
struct Limits
{
    std::uint64_t parameters = 4096;
    std::uint64_t nodes = 4096;
    std::uint64_t depth = 64;
    std::uint64_t parameter_bytes = 8192;
    std::uint64_t timeout_ns = 5'000'000'000;
};
struct Fixture
{
    Limits limits;
    std::filesystem::path path;
    std::shared_ptr<Model> parent = model(true);
    std::shared_ptr<Model> child = model(false);
    std::shared_ptr<Projection> projection;
    Owner source, session, parent_owner, child_owner, relation;
    std::uint32_t projection_id{};
    bool finished = false;
    explicit Fixture(Limits settings = {}) : limits(settings)
    {
        static std::uint64_t serial = 0;
        path = std::filesystem::temp_directory_path() /
               ("nw-b14-abi-" + std::to_string(Clock::now().time_since_epoch().count()) + '-' +
                std::to_string(++serial) + ".sqlite");
        {
            Sqlite independent(path);
            independent.execute("CREATE TABLE b14_adapter_parent(\"Tenant\" TEXT NOT NULL,\"Id\" TEXT NOT NULL,"
                                "PRIMARY KEY(\"Tenant\",\"Id\"));"
                                "CREATE TABLE b14_adapter_child(\"Id\" BIGINT PRIMARY KEY,\"Tenant\" TEXT NOT NULL,"
                                "\"ParentId\" TEXT NOT NULL);INSERT INTO b14_adapter_child VALUES(1,'t','p');");
        }
        const auto encoded_limits = encode_control(
            10, {u64(1, 1), u64(2, 8), u64(3, 5'000'000'000, 3), u64(4, 64), u64(5, 65536), u64(6, 64), u64(7, 65536),
                 u64(8, 65536), u64(9, limits.nodes), u64(10, limits.depth), u64(11, limits.parameters),
                 u64(12, limits.parameter_bytes), u64(13, 32), u64(14, MAX_BYTES), u64(15, limits.timeout_ns, 3)});
        const auto options =
            encode_control(9, {u32(1, 0), u32(2, 1), u32(3, 1), u32(4, 0), u64(5, 1'000'000), u32(6, 1)});
        const auto config = encode_control(1, {u32(1, 1),
                                               {2, 4, bytes(path.string())},
                                               {3, 5, encoded_limits},
                                               {4, 5, options},
                                               {5, 4, bytes("b14-actual-abi")}});
        auto error = error_slot();
        ok(nw_db_source_create_v1(view(config), &source.value, &error), error, "actual source create");
        {
            Operation open;
            ok(nw_db_session_open_create_v1(source.value, &open.value, &error), error, "actual session create");
            open.run();
            session.value = open.take_owner(1);
        }
        register_model(parent, parent_owner);
        register_model(child, child_owner);
        const auto parent_hash = parent->hash();
        const auto child_hash = child->hash();
        const std::array parent_ids{parent->projection()[0], parent->projection()[1]};
        const std::array child_ids{child->projection()[1], child->projection()[2]};
        const auto identity =
            bytes("{\"version\":1,\"parent_model\":" + json_quote(hex(parent_hash)) + ",\"parent_table\":" +
                  std::to_string(parent->table_id()) + ",\"child_model\":" + json_quote(hex(child_hash)) +
                  ",\"child_table\":" + std::to_string(child->table_id()) + ",\"parent_columns\":[" +
                  std::to_string(parent_ids[0]) + ',' + std::to_string(parent_ids[1]) + "],\"child_columns\":[" +
                  std::to_string(child_ids[0]) + ',' + std::to_string(child_ids[1]) +
                  "],"
                  "\"child_nullable\":[false,false]}");
        const auto hash = sha256(identity);
        const auto definition = encode_control(23, {u32(1, parent->table_id()),
                                                    {2, 5, {parent_hash.begin(), parent_hash.end()}},
                                                    u32(3, child->table_id()),
                                                    {4, 5, {child_hash.begin(), child_hash.end()}},
                                                    {5, 7, encode_u32_vector(parent_ids)},
                                                    {6, 7, encode_u32_vector(child_ids)},
                                                    {7, 7, encode_u32_vector(std::array<std::uint32_t, 2>{0, 0})},
                                                    {8, 5, {hash.begin(), hash.end()}}});
        ok(nw_db_relation_register_v1(source.value, parent_owner.value, child_owner.value, view(definition),
                                      &relation.value, &error),
           error, "actual relation register");
        std::vector<Bytes> records;
        std::string text = "{\"version\":1,\"fields\":[";
        for (std::size_t i = 0; i < child->projection().size(); ++i)
        {
            const auto& col = child->column(child->projection()[i]);
            records.push_back(encode_control(
                22, {u32(1, static_cast<std::uint32_t>(i)), {2, 4, bytes(col.name)}, u32(3, col.codec.id), u32(4, 0)}));
            if (i)
                text += ',';
            text += "{\"ordinal\":" + std::to_string(i) + ",\"name\":" + json_quote(col.name) +
                    ",\"codec\":" + std::to_string(col.codec.id) + ",\"codec_config\":null,\"nullable\":false}";
        }
        projection = std::make_shared<Projection>(Projection::parse(bytes(text + "]}"), Profile::Sqlite));
        const auto projection_hash = projection->hash();
        const auto record = encode_control(
            13, {{1, 5, records_bytes(records)}, {2, 5, {projection_hash.begin(), projection_hash.end()}}});
        ok(nw_db_projection_register_v1(source.value, view(record), &projection_id, &error), error,
           "actual projection register");
    }
    ~Fixture()
    {
        try
        {
            if (!finished)
                finish();
        }
        catch (...)
        {
        }
        std::error_code error;
        std::filesystem::remove(path, error);
        std::filesystem::remove(path.string() + "-wal", error);
        std::filesystem::remove(path.string() + "-shm", error);
    }
    void register_model(const std::shared_ptr<Model>& value, Owner& owner)
    {
        const auto hash = value->hash();
        const auto metadata = encode_control(2, {{1, 5, value->bytes()}, {2, 5, {hash.begin(), hash.end()}}});
        auto error = error_slot();
        ok(nw_db_metadata_register_v1(source.value, view(metadata), &owner.value, &error), error,
           "actual model register parameters=" + std::to_string(limits.parameters) +
               " nodes=" + std::to_string(limits.nodes) + " depth=" + std::to_string(limits.depth) +
               " bytes=" + std::to_string(limits.parameter_bytes));
    }
    Bytes key(std::string_view id = "p") const
    {
        Expectation expected{
            4,
            Profile::Sqlite,
            parent->hash(),
            parent->table_id(),
            0,
            {{parent->projection()[0], {5, {}, false}, 5}, {parent->projection()[1], {5, {}, false}, 5}},
            1};
        return Batch::encode(expected, 1, {bytes("t"), bytes(id)});
    }
    std::unique_ptr<Operation> load(const Bytes& key_bytes)
    {
        auto operation = std::make_unique<Operation>();
        auto error = error_slot();
        ok(nw_db_relation_load_create_v1(session.value, relation.value, view(key_bytes), projection_id,
                                         &operation->value, &error),
           error, "actual relation load create");
        return operation;
    }
    std::unique_ptr<Operation> insert()
    {
        auto operation = std::make_unique<Operation>();
        auto error = error_slot();
        const auto payload = encode_control(4, {u32(1, parent->table_id()), {2, 5, key()}});
        ok(nw_db_mutation_create_v1(session.value, parent_owner.value, 0, view(payload), &operation->value, &error),
           error, "actual mutation create");
        return operation;
    }
    void read(std::unique_ptr<Operation> operation)
    {
        operation->run();
        Owner cursor;
        cursor.value = operation->take_owner(2);
        operation.reset();
        {
            Operation fetch;
            auto error = error_slot();
            ok(nw_db_cursor_fetch_create_v1(cursor.value, 2, 8192, &fetch.value, &error), error,
               "actual relation fetch");
            fetch.run();
            const auto payload = fetch.take_batch();
            Expectation expected{
                2, Profile::Sqlite, projection->hash(),
                0, projection_id,   {{0, {3, {}, false}, 3}, {1, {5, {}, false}, 5}, {2, {5, {}, false}, 5}},
                {}};
            const auto batch = Batch::parse(payload, expected);
            check(batch.rows() == 1 && read_u64(*batch.cell(0, 0), 0) == 1, "actual relation row unchanged");
        }
        auto error = error_slot();
        const auto operations = nw_db_diagnostic_count_v1(2);
        const auto executions = nw_db_diagnostic_count_v1(6);
        const auto submissions = nw_db_diagnostic_count_v1(7);
        ok(nw_db_cursor_check_read_admission_v1(cursor.value, &error), error, "actual known EOF read admission");
        check(nw_db_diagnostic_count_v1(2) == operations && nw_db_diagnostic_count_v1(6) == executions &&
                  nw_db_diagnostic_count_v1(7) == submissions,
              "EOF admission check creates no operation, SQL execution or protocol submission");
        check(nw_db_cursor_check_read_admission_v1(session.value, &error) == NW_INVALID_STATE,
              "EOF admission rejects a noncursor owner");
        close_owner(cursor);
    }
    void reject(const Bytes& key_bytes, bool wrong_projection = false)
    {
        nw_db_ref output = reinterpret_cast<nw_db_ref>(static_cast<std::uintptr_t>(1));
        auto error = error_slot();
        const auto previous = nw_db_diagnostic_count_v1(2);
        const auto status = nw_db_relation_load_create_v1(session.value, relation.value, view(key_bytes),
                                                          wrong_projection ? 999999 : projection_id, &output, &error);
        if (status == NW_OK)
            nw_db_operation_release_v1(output);
        check(status == NW_DOMAIN_ERROR && error.domain == 0x4442 && error.code == 6 && output == nullptr &&
                  error.correlation_id != 0,
              "Host relation capacity must precede binding/projection: status=" + std::to_string(status) +
                  " code=" + std::to_string(error.code));
        check(nw_db_diagnostic_count_v1(2) == previous, "relation preflight admits no operation");
        check(Sqlite(path).count() == 0, "relation preflight has no SQL side effect");
    }
    void finish()
    {
        close_owner(session);
        relation.release();
        parent_owner.release();
        child_owner.release();
        close_owner(source);
        eventually(
            []
            {
                for (std::uint32_t i = 0; i < 6; ++i)
                    if (nw_db_diagnostic_count_v1(i) != 0)
                        return false;
                return true;
            },
            "all six actual product owner counters return to zero");
        finished = true;
    }
};
void delayed_deadlines()
{
    for (bool mutation : {true, false})
    {
        Fixture fixture({.timeout_ns = 100'000'000});
        const auto before = Clock::now();
        auto operation = mutation ? fixture.insert() : fixture.load(fixture.key());
        check(nw_db_diagnostic_count_v1(2) == 1, "actual Created operation retained before Start");
        std::this_thread::sleep_until(before + std::chrono::milliseconds(200));
        operation->run(NW_TIMEOUT, 8);
        if (mutation)
        {
            const auto payload = operation->take_buffer(true);
            const auto first = read_u32(payload, 4);
            check(first < payload.size(), "failed insert retains native bulk progress");
            const auto progress = Fields::parse(ByteView(payload).subspan(first), 15,
                                                {{1, 2, true}, {2, 1, true}, {3, 1, true}, {4, 1, true}});
            check(progress.u64(1) == 0 && progress.u32(2) == 0 && !progress.boolean(3) && progress.u32(4) == 0,
                  "predispatch timeout retains exact zero bulk progress");
        }
        operation.reset();
        check(Sqlite(fixture.path).count() == 0, "expired B14 operation does not write");
        // The expired operation must release the same real session gate.
        auto immediate = fixture.insert();
        immediate->run();
        check(immediate->take_count() == 1, "immediate mutation remains valid after timeout");
        immediate.reset();
        fixture.read(fixture.load(fixture.key()));
        check(Sqlite(fixture.path).count() == 1, "independent connection observes only the immediate write");
        fixture.finish();
    }
}
void relation_capacity()
{
    {
        Fixture fixture;
        const auto overhead = fixture.key("").size();
        const auto exact = fixture.key(std::string(fixture.limits.parameter_bytes - overhead, 'x'));
        check(exact.size() == fixture.limits.parameter_bytes, "exact canonical-key byte boundary");
        fixture.load(exact).reset(); // Admission only; physical streaming has its separate retained input budget.
        fixture.reject(fixture.key(std::string(fixture.limits.parameter_bytes - overhead + 1, 'x')));
        fixture.reject(fixture.key(std::string(fixture.limits.parameter_bytes - overhead + 1, 'x')), true);
        fixture.finish();
    }
    for (const auto limits : {Limits{.parameters = 1}, Limits{.nodes = 5}, Limits{.depth = 1}})
    {
        Fixture fixture(limits);
        fixture.reject(fixture.key());
        fixture.reject(fixture.key(), true);
        fixture.finish();
    }
    Fixture exact({.parameters = 2, .nodes = 6, .depth = 2});
    exact.read(exact.load(exact.key()));
    exact.finish();
}
void raw_sql_keeps_start_deadline()
{
    Fixture fixture;
    const auto schema = bytes("{\"version\":1,\"parameters\":[]}");
    Expectation expected{1, Profile::Sqlite, sha256(schema), 0, 0, {}, 1};
    const auto values = Batch::encode(expected, 1, {});
    const auto options =
        encode_control(20, {u32(1, 1), u32(2, 0), u64(3, 100'000'000, 3), u32(4, 0), {5, 5, u32_bytes(0)}, u32(6, 0)});
    const auto sql = bytes("INSERT INTO b14_adapter_parent VALUES('raw','delayed')");
    Owner plan;
    auto error = error_slot();
    ok(nw_db_sql_create_v1(fixture.session.value, view(sql), view(values), view(options), &plan.value, &error), error,
       "ordinary explicit SQL plan create");
    {
        Operation operation;
        ok(nw_db_execute_create_v1(fixture.session.value, plan.value, {}, &operation.value, &error), error,
           "ordinary explicit SQL operation create");
        std::this_thread::sleep_for(std::chrono::milliseconds(200));
        operation.run();
        check(operation.take_count() == 1, "raw SQL keeps its original execution-time timeout origin");
    }
    plan.release();
    check(Sqlite(fixture.path).count() == 1, "raw SQL positive control actually wrote");
    fixture.finish();
}
} // namespace

int main(int argc, char** argv)
{
    try
    {
        const std::string mode = argc > 1 ? argv[1] : "all";
        check(mode == "all" || mode == "deadline" || mode == "capacity" || mode == "raw-sql", "test mode");
        if (mode == "all" || mode == "deadline")
            delayed_deadlines();
        if (mode == "all" || mode == "capacity")
            relation_capacity();
        if (mode == "all" || mode == "raw-sql")
            raw_sql_keeps_start_deadline();
        std::cout << "PASS B14 actual C ABI adapters: " << assertions << " checks; six owner counters zero\n";
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << "FAIL B14 actual C ABI adapters: " << error.what() << '\n';
        return 1;
    }
}
