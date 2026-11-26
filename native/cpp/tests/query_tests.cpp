// SPDX-License-Identifier: MIT
// DB-CPP-01 ports the binding/native assertion families and the 26 SeaQuery 1.0.2
// SQL/argument goldens per provider. Database execution is verified by managed regressions.
#include "nw/mutations.hpp"
#include "nw/query.hpp"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <bit>
#include <filesystem>
#include <fstream>
#include <functional>
#include <future>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <thread>

using namespace nw;
namespace
{
std::filesystem::path repository;
std::size_t checks = 0;
void check(bool value, std::string_view message)
{
    ++checks;
    if (!value)
        throw std::runtime_error(std::string(message));
}
template <class F> void rejects(std::uint32_t status, F&& action)
{
    try
    {
        action();
    }
    catch (const Error& error)
    {
        check(error.status == status, "unexpected status " + std::to_string(error.status) + ": " + error.message);
        return;
    }
    throw std::runtime_error("operation unexpectedly accepted");
}
Bytes bytes(std::string_view text)
{
    return {text.begin(), text.end()};
}
Bytes u32(std::uint32_t value)
{
    Bytes data(4);
    put_u32(data, 0, value);
    return data;
}
Bytes u64(std::uint64_t value)
{
    Bytes data(8);
    put_u64(data, 0, value);
    return data;
}
ControlField num(std::uint16_t id, std::uint32_t value)
{
    return {id, 1, u32(value)};
}
ControlField token(std::uint16_t id, std::uint64_t value)
{
    return {id, 2, u64(value)};
}
Bytes record(std::initializer_list<ControlField> fields)
{
    return encode_control(6, fields);
}
Bytes record(const std::vector<ControlField>& fields)
{
    return encode_control(6, fields);
}
Bytes fixture(std::string_view name)
{
    auto file = repository / "tests/Query.Native/golden" / name;
    std::ifstream input(file, std::ios::binary);
    if (!input)
        throw std::runtime_error("missing fixture " + file.string());
    return {std::istreambuf_iterator<char>(input), {}};
}
Codec scalar(std::uint32_t id, bool nullable = false)
{
    return {id, {}, nullable};
}
Capabilities all_caps(Profile profile)
{
    return Capabilities::from_mask((1ULL << 29) - 1, profile);
}
Bytes cells(Profile profile, Codec codec, const std::vector<std::optional<Bytes>>& values)
{
    Expectation expected{3, profile, {}, 0, 0, {{1, codec, codec.id}}, {}};
    return Batch::encode(expected, static_cast<std::uint32_t>(values.size()), values);
}
std::uint32_t column(const ModelBinding& model, std::string_view name)
{
    for (const auto& item : model.model->columns())
        if (item.name == name)
            return item.id;
    throw std::runtime_error("missing model column");
}
struct Selection
{
    std::uint64_t expression;
    std::string name;
    Codec codec;
};
Bytes selection_record(std::initializer_list<Selection> values, bool embedded_tokens = false)
{
    std::string canonical = "{\"version\":1,\"fields\":[";
    std::vector<Bytes> records;
    Bytes tokens;
    for (const auto& value : values)
    {
        auto i = static_cast<std::uint32_t>(records.size());
        if (i)
            canonical += ',';
        canonical += "{\"ordinal\":" + std::to_string(i) + ",\"name\":" + json_quote(value.name) +
                     ",\"codec\":" + std::to_string(value.codec.id) +
                     ",\"codec_config\":null,\"nullable\":" + (value.codec.nullable ? "true" : "false") + '}';
        std::vector<ControlField> fields{
            num(1, i), {2, 4, bytes(value.name)}, num(3, value.codec.id), num(4, value.codec.nullable)};
        if (embedded_tokens)
            fields.push_back(token(5, value.expression));
        records.push_back(encode_control(22, fields));
        auto encoded = u64(value.expression);
        tokens.insert(tokens.end(), encoded.begin(), encoded.end());
    }
    canonical += "]}";
    auto sequence = u32(static_cast<std::uint32_t>(records.size()));
    for (const auto& item : records)
    {
        auto length = u32(static_cast<std::uint32_t>(item.size()));
        sequence.insert(sequence.end(), length.begin(), length.end());
        sequence.insert(sequence.end(), item.begin(), item.end());
    }
    auto hash = sha256(bytes(canonical));
    auto projection = encode_control(13, {{1, 5, sequence}, {2, 5, {hash.begin(), hash.end()}}});
    return record({{1, 8, tokens}, {2, 5, projection}});
}
void select(NativeQuery& query, std::initializer_list<Selection> values)
{
    query.clause(6, selection_record(values));
}
void where(NativeQuery& query, std::uint64_t expression)
{
    query.clause(1, record({token(1, expression)}));
}
void order(NativeQuery& query, std::uint64_t expression)
{
    query.clause(3, record({token(1, expression), num(2, 0), num(3, 1), num(4, 1)}));
}
Bytes parameter_wire(const RenderedQuery& command)
{
    std::vector<std::optional<ByteView>> values;
    values.reserve(command.arguments.size());
    for (const auto& argument : command.arguments)
        values.push_back(argument.value ? std::optional<ByteView>(*argument.value) : std::nullopt);
    return Batch::encode_views(command.parameter_expectation, 1, values);
}
RenderedQuery execute(const std::shared_ptr<NativePlan>& plan, const std::vector<std::optional<Bytes>>& values = {},
                      ShapeCache* cache = nullptr)
{
    return plan->render(plan->source, Batch::encode(plan->parameter_expectation(), 1, values), cache);
}
struct Context
{
    SourceIdentity source;
    std::shared_ptr<const CodecBindings> codecs;
    ModelBinding root, child;
    std::shared_ptr<ParameterRegistry> registry;
    explicit Context(Profile profile)
        : source{9, 1, 1, profile}, codecs(std::make_shared<CodecBindings>(profile)),
          root(source, std::make_shared<Model>(Model::parse(fixture("parent-model.json"), profile)), codecs),
          child(source, std::make_shared<Model>(Model::parse(fixture("child-model.json"), profile)), codecs),
          registry(std::make_shared<ParameterRegistry>(source, 4096))
    {
    }
    NativeQuery query(QueryLimits limits = {}) const
    {
        return {root, registry, root.model->table_id(), limits, all_caps(source.profile), 10'000'000'000};
    }
    NativeQuery from_model(ModelBinding model) const
    {
        auto id = model.model->table_id();
        return {std::move(model), registry, id, {}, all_caps(source.profile), 10'000'000'000};
    }
    std::uint64_t value(NativeQuery& query, std::int64_t value) const
    {
        return query.value(cells(source.profile, scalar(3), {u64(static_cast<std::uint64_t>(value))}));
    }
    void select_all(NativeQuery& query) const
    {
        auto a = query.column(0, column(root, "Id"));
        auto b = query.column(0, column(root, "Name"));
        select(query, {{a, "Id", scalar(3)}, {b, "Name", scalar(5)}});
    }
    NativeQuery selected(std::uint32_t op, std::int64_t threshold) const
    {
        auto query_ = query();
        auto id = query_.column(0, column(root, "Id"));
        auto value_ = value(query_, threshold);
        where(query_, query_.binary(op, id, value_));
        select(query_, {{id, "Id", scalar(3)}});
        return query_;
    }
    ModelBinding declared(std::string_view name) const
    {
        auto logical = "Korda.Query.Native.Tests:" + std::string(name);
        auto table = stable_id({"NWDB-TABLE-ID-v1", logical, {}, name});
        auto id = stable_id({"NWDB-COLUMN-ID-v1", logical, {}, name, "Id"});
        auto json =
            "{\"version\":1,\"logical_id\":" + json_quote(logical) + ",\"table_id\":" + std::to_string(table) +
            ",\"schema\":null,\"table\":" + json_quote(name) + ",\"columns\":[{\"id\":" + std::to_string(id) +
            ",\"name\":\"Id\",\"codec\":3,\"codec_config\":null,\"nullable\":false,\"key_ordinal\":null,\"generated\":"
            "false,\"concurrency\":false,\"collation\":null,\"max_length\":null}],\"projection\":[" +
            std::to_string(id) + "],\"relations\":[]}";
        return {source, std::make_shared<Model>(Model::parse(bytes(json), source.profile)), codecs};
    }
};

Bytes pair32(std::uint32_t a, std::uint32_t b)
{
    auto result = u32(a);
    const auto tail = u32(b);
    result.insert(result.end(), tail.begin(), tail.end());
    return result;
}
Bytes command_parameter(std::uint32_t id, std::string_view name, bool nullable = false)
{
    Bytes result(16);
    put_u32(result, 0, id);
    put_u32(result, 4, 3);
    put_u32(result, 8, nullable);
    put_u32(result, 12, static_cast<std::uint32_t>(name.size()));
    result.insert(result.end(), name.begin(), name.end());
    return result;
}
struct CommandBlock
{
    std::uint32_t owner;
    std::uint32_t table;
    std::uint32_t slots = 0;
    std::vector<Bytes> records;
    CommandBlock(std::uint32_t owner_index, std::uint32_t table_id) : owner(owner_index), table(table_id) {}
    std::uint32_t add(std::uint16_t kind, std::uint16_t opcode, ByteView payload, bool result = true,
                      std::uint32_t origin = 0)
    {
        Bytes value(16);
        put_u32(value, 0, static_cast<std::uint32_t>(16 + payload.size()));
        put_u16(value, 4, kind);
        put_u16(value, 6, opcode);
        const auto slot = result ? ++slots : 0;
        put_u32(value, 8, slot);
        put_u32(value, 12, origin);
        value.insert(value.end(), payload.begin(), payload.end());
        records.push_back(std::move(value));
        return slot;
    }
    std::uint32_t clause(std::uint16_t opcode, ByteView payload, bool result = false, std::uint32_t origin = 0)
    {
        return add(8, opcode, payload, result, origin);
    }
    Bytes encode() const
    {
        Bytes value(24);
        put_u32(value, 4, owner);
        put_u32(value, 8, table);
        put_u32(value, 12, static_cast<std::uint32_t>(records.size()));
        put_u32(value, 16, slots);
        for (const auto& record : records)
            value.insert(value.end(), record.begin(), record.end());
        put_u32(value, 0, static_cast<std::uint32_t>(value.size()));
        return value;
    }
};
Bytes query_command(std::initializer_list<CommandBlock> blocks,
                    std::initializer_list<std::array<std::uint32_t, 3>> bindings = {})
{
    Bytes result(32);
    put_u32(result, 0, 0x4351574e);
    put_u32(result, 4, 1);
    put_u32(result, 12, static_cast<std::uint32_t>(blocks.size()));
    put_u32(result, 16, static_cast<std::uint32_t>(blocks.size() - 1));
    put_u32(result, 24, static_cast<std::uint32_t>(bindings.size()));
    for (const auto& binding : bindings)
        for (auto value : binding)
        {
            const auto encoded = u32(value);
            result.insert(result.end(), encoded.begin(), encoded.end());
        }
    std::uint32_t count = 0;
    for (const auto& block : blocks)
    {
        auto encoded = block.encode();
        result.insert(result.end(), encoded.begin(), encoded.end());
        count += static_cast<std::uint32_t>(block.records.size());
    }
    put_u32(result, 8, static_cast<std::uint32_t>(result.size()));
    put_u32(result, 20, count);
    return result;
}
CommandBlock selected_command(const Context& c, std::int64_t threshold = 7, std::uint32_t origin = 1)
{
    CommandBlock block{0, c.root.model->table_id()};
    const auto id = block.add(1, 0, pair32(0, column(c.root, "Id")));
    const auto value = block.add(2, 0, cells(c.source.profile, scalar(3), {u64(threshold)}), true, origin);
    const auto predicate = block.add(4, 4, pair32(id, value));
    block.clause(1, record({token(1, predicate)}));
    block.clause(6, selection_record({{id, "Id", scalar(3)}}, true));
    return block;
}
void same_command(const std::shared_ptr<NativePlan>& actual, const std::shared_ptr<NativePlan>& expected,
                  const std::vector<std::optional<Bytes>>& parameters = {})
{
    const auto a = execute(actual, parameters);
    const auto b = execute(expected, parameters);
    check(a.sql == b.sql, "complete command SQL differs: " + a.sql + " / " + b.sql);
    check(parameter_wire(a) == parameter_wire(b), "complete command ordered parameters differ");
    check(actual->parameter_schema() == expected->parameter_schema(), "complete command parameter schema differs");
    check(actual->nodes == expected->nodes && actual->depth == expected->depth,
          "complete command native node/depth accounting differs");
}
std::vector<std::pair<std::string, RenderedQuery>> oracle_cases(const Context& c);
void complete_query_commands()
{
    for (auto profile : {Profile::Sqlite, Profile::PostgreSql})
    {
        Context c(profile);
        const QueryCommandContext context{c.registry, {}, all_caps(profile), 10'000'000'000};
        const std::vector<QueryCommandDependency> dependencies{c.root, c.child};
        auto compile = [&](const Bytes& bytes, std::uint32_t terminal = 8)
        { return compile_query_command(bytes, dependencies, context, terminal); };
        {
            CommandBlock block{0, c.root.model->table_id()};
            const auto id = block.add(1, 0, pair32(0, column(c.root, "Id")));
            const auto parameter = block.add(3, 0, command_parameter(301, "minimum"), true, 1);
            const auto predicate = block.add(4, 4, pair32(id, parameter));
            block.clause(1, record({token(1, predicate)}));
            block.clause(3, record({token(1, id), num(2, 0), num(3, 1), num(4, 1)}));
            block.clause(5, record({token(1, 7), token(2, 2)}));
            block.clause(22, record({{1, 3, u64(99'000'000)}}));
            block.clause(6, selection_record({{id, "Id", scalar(3)}}, true));
            auto encoded = query_command({block});
            auto plan = compile(encoded);
            auto direct = c.query();
            const auto did = direct.column(0, column(c.root, "Id"));
            const auto dp = direct.parameter({301, "minimum", scalar(3), 3});
            where(direct, direct.binary(4, did, dp));
            order(direct, did);
            direct.clause(5, record({token(1, 7), token(2, 2)}));
            direct.clause(22, record({{1, 3, u64(99'000'000)}}));
            select(direct, {{did, "Id", scalar(3)}});
            auto expected = direct.seal(8);
            same_command(plan, expected, {u64(1)});
            same_command(plan, expected, {u64(9)});
            check(plan->timeout_ns == 99'000'000, "command timeout remains numeric");
            const auto sql = render_query_command(encoded, dependencies, context);
            check(std::string(sql.begin(), sql.end()) == plan->compiled_shape()->sql,
                  "render and explicit compile agree");
            auto ordinary = compile(encoded, 0);
            (void)execute(ordinary, {u64(1)});
            rejects(NW_INVALID_STATE, [&] { (void)execute(ordinary, {u64(2)}); });
        }
        {
            auto encoded = query_command({selected_command(c)});
            const auto original = encoded;
            auto plan = compile(encoded);
            check(encoded == original, "complete command decoding must not rewrite caller input");
            std::fill(encoded.begin(), encoded.end(), 0);
            auto direct = c.selected(4, 7);
            const auto counts = direct.counts();
            auto expected = direct.seal(8);
            check(direct.counts() == counts, "public seal retains its independently owned query arena");
            direct.close();
            same_command(plan, expected);
        }
        {
            // Clause references are decoded directly from the input. Neither a
            // native token nor a zero/self/forward local index may bypass the
            // block's earlier-result bound, including nested projection tokens.
            for (auto reference : {std::uint64_t{0}, std::uint64_t{2}, std::uint64_t{1} << 32,
                                   std::numeric_limits<std::uint64_t>::max()})
            {
                CommandBlock invalid{0, c.root.model->table_id()};
                invalid.add(1, 0, pair32(0, column(c.root, "Id")));
                invalid.clause(6, selection_record({{reference, "Id", scalar(3)}}, true));
                rejects(NW_INVALID_ARGUMENT, [&] { (void)compile(query_command({invalid})); });
            }
            CommandBlock root{0, c.root.model->table_id()};
            const auto id = root.add(1, 0, pair32(0, column(c.root, "Id")));
            root.add(1, 0, pair32(0, column(c.root, "Id")));
            auto selection = selection_record({{id, "Id", scalar(3)}}, true);
            const auto selected = Fields::parse(selection, 6, {{1, 8, true}, {2, 5, true}});
            const auto projection = Fields::parse(selected.bytes(2), 13, {{1, 5, true}, {2, 5, true}});
            const auto item = Fields::parse(sequence(projection.bytes(1)).front(), 22,
                                            {{1, 1, true}, {2, 4, true}, {3, 1, true}, {4, 1, true}, {5, 2, true}});
            put_u64(selection, static_cast<std::size_t>(item.bytes(5).data() - selection.data()), 2);
            root.clause(6, selection);
            rejects(NW_INVALID_ARGUMENT, [&] { (void)compile(query_command({root})); });
        }
        {
            // Optional fields still carry references even when the operation
            // does not use them. Valid extra references keep their old behavior.
            for (std::uint16_t field : {2, 3})
            {
                CommandBlock root{0, c.root.model->table_id()};
                const auto id = root.add(1, 0, pair32(0, column(c.root, "Id")));
                root.clause(11, record({num(1, 0), token(field, id)}), true);
                root.clause(6, selection_record({{id, "Id", scalar(3)}}, true));
                check(!execute(compile(query_command({root}))).sql.empty(), "valid unused command reference");
                auto& record = root.records[1];
                const auto f = Fields::parse(ByteView(record).subspan(16), 6, {{1, 1, true}, {field, 2, true}});
                put_u64(record, static_cast<std::size_t>(f.bytes(field).data() - record.data()), 2);
                rejects(NW_INVALID_ARGUMENT, [&] { (void)compile(query_command({root})); });
            }
        }
        {
            auto root = selected_command(c);
            const auto unknown = record({token(1, 3), num(2, 0)});
            root.records[3].resize(16);
            root.records[3].insert(root.records[3].end(), unknown.begin(), unknown.end());
            put_u32(root.records[3], 0, static_cast<std::uint32_t>(root.records[3].size()));
            rejects(NW_INVALID_ARGUMENT, [&] { (void)compile(query_command({root})); });
        }
        {
            auto snapshot = selected_command(c);
            auto root = snapshot;
            const auto imported = root.add(7, 4, u32(0));
            root.clause(9, record({token(1, imported), num(2, 1)}));
            auto direct = c.selected(4, 7);
            const auto old = direct.import_plan(direct.clone_selected()->seal(8));
            direct.clause(9, record({token(1, old), num(2, 1)}));
            same_command(compile(query_command({snapshot, root})), direct.seal(8));
            auto changed = selected_command(c, 8);
            const auto changed_import = changed.add(7, 4, u32(0));
            changed.clause(9, record({token(1, changed_import), num(2, 1)}));
            rejects(NW_INVALID_ARGUMENT, [&] { (void)compile(query_command({snapshot, changed})); });
            rejects(NW_INVALID_ARGUMENT, [&] { (void)compile(query_command({snapshot, snapshot})); });
        }
        for (std::uint32_t operation = 0; operation < 2; ++operation)
        {
            auto snapshot = selected_command(c);
            CommandBlock root{0, c.root.model->table_id()};
            auto id = root.add(1, 0, pair32(0, column(c.root, "Id")));
            auto imported = root.add(7, 4, u32(0));
            std::vector<ControlField> fields{token(1, imported), num(2, operation)};
            if (operation)
                fields.push_back(token(3, id));
            auto predicate = root.clause(14, record(fields), true);
            root.clause(1, record({token(1, predicate)}));
            root.clause(6, selection_record({{id, "Id", scalar(3)}}, true));
            auto body = c.selected(4, 7);
            auto direct = c.query();
            auto did = direct.column(0, column(c.root, "Id"));
            auto dep = body.seal(8);
            auto dimport = direct.import_plan(dep);
            fields = {token(1, dimport), num(2, operation)};
            if (operation)
                fields.push_back(token(3, did));
            where(direct, direct.clause(14, record(fields)));
            select(direct, {{did, "Id", scalar(3)}});
            auto expected = direct.seal(8);
            same_command(compile(query_command({snapshot, root})), expected);
            if (operation == 0)
            {
                auto invalid = root;
                const auto extra = record({token(1, imported), num(2, operation), token(3, predicate)});
                invalid.records[2].resize(16);
                invalid.records[2].insert(invalid.records[2].end(), extra.begin(), extra.end());
                put_u32(invalid.records[2], 0, static_cast<std::uint32_t>(invalid.records[2].size()));
                rejects(NW_INVALID_ARGUMENT, [&] { (void)compile(query_command({snapshot, invalid})); });
            }
            // The same complete root can import an existing immutable owner.
            put_u16(root.records[1], 6, 2);
            put_u32(root.records[1], 16, 2);
            auto external = dependencies;
            external.emplace_back(dep);
            same_command(compile_query_command(query_command({root}), external, context, 8), expected);
        }
        {
            CommandBlock root{0, c.root.model->table_id()};
            auto id = root.add(1, 0, pair32(0, column(c.root, "Id")));
            auto model = root.add(7, 1, u32(1));
            auto alias = root.clause(
                18, record({token(1, model), num(2, c.child.model->table_id()), {3, 4, bytes("c")}, num(4, 1)}), true);
            auto child = root.add(1, 0, pair32(alias, column(c.child, "ParentId")));
            auto lifted = root.add(5, 4, u32(id));
            auto predicate = root.add(4, 1, pair32(lifted, child));
            root.clause(7, record({token(1, alias), token(2, predicate)}));
            auto child_id = root.add(1, 0, pair32(alias, column(c.child, "Id")));
            auto matched = root.clause(20, record({token(1, alias)}), true);
            root.clause(3, record({token(1, id), num(2, 0), num(3, 1), num(4, 1)}));
            root.clause(
                6, selection_record(
                       {{id, "Id", scalar(3)}, {child_id, "child", scalar(3, true)}, {matched, "matched", scalar(4)}},
                       true));
            const auto result = execute(compile(query_command({root}), 0));
            const auto oracle = oracle_cases(c);
            const auto expected = std::find_if(oracle.begin(), oracle.end(),
                                               [](const auto& value) { return value.first == "nullable-left-join"; });
            check(result.sql == expected->second.sql && parameter_wire(result) == parameter_wire(expected->second),
                  "complete command JOIN reserve/commit/nullable projection");
            // A provisional column captured before commit is invalid afterwards.
            root.add(5, 1, u32(child));
            rejects(NW_INVALID_ARGUMENT, [&] { (void)compile(query_command({root})); });
        }
        {
            CommandBlock root{0, c.root.model->table_id()};
            auto id = root.add(1, 0, pair32(0, column(c.root, "Id")));
            auto window = root.clause(11, record({num(1, 0)}), true);
            root.clause(11, record({num(1, 2), token(2, window), token(3, id), num(4, 0), num(5, 1)}));
            auto rank = root.clause(13, record({num(1, 6), num(3, 0), num(5, 3)}), true);
            rank = root.clause(21, record({token(1, rank), token(2, window)}), true);
            root.clause(11, record({num(1, 1), token(2, window), token(3, id)}));
            root.clause(3, record({token(1, id), num(2, 0), num(3, 1), num(4, 1)}));
            root.clause(6, selection_record({{id, "Id", scalar(3)}, {rank, "rank", scalar(3)}}, true));
            const auto result = execute(compile(query_command({root})));
            const auto oracle = oracle_cases(c);
            const auto expected = std::find_if(oracle.begin(), oracle.end(),
                                               [](const auto& value) { return value.first == "window-snapshot"; });
            check(result.sql == expected->second.sql, "complete command snapshots window at OVER");
        }
        for (std::uint16_t operation : {1, 2, 3})
        {
            CommandBlock root{0, c.root.model->table_id()};
            auto id = root.add(1, 0, pair32(0, column(c.root, "Id")));
            auto values = cells(profile, scalar(3), {u64(1), u64(2)});
            auto input = u32(id);
            input.insert(input.end(), values.begin(), values.end());
            auto list = root.add(6, operation, input, true, 1);
            root.clause(1, record({token(1, list)}));
            root.clause(6, selection_record({{id, "Id", scalar(3)}}, true));
            auto snapshot = root;
            auto imported = root.add(7, 4, u32(0));
            root.clause(9, record({token(1, imported), num(2, 1)}));
            auto direct = c.query();
            auto did = direct.column(0, column(c.root, "Id"));
            where(direct, direct.list(operation, did, values));
            select(direct, {{did, "Id", scalar(3)}});
            auto dimport = direct.import_plan(direct.clone_selected()->seal(8));
            direct.clause(9, record({token(1, dimport), num(2, 1)}));
            auto encoded = query_command({snapshot, root});
            auto plan = compile(encoded);
            // Both the direct literal decoder and the complete-command block
            // must own their cells after the synchronous input borrow ends.
            std::fill(values.begin(), values.end(), 0);
            std::fill(encoded.begin(), encoded.end(), 0);
            same_command(plan, direct.seal(8));
        }
        for (std::uint32_t operation = 0; operation < 4; ++operation)
        {
            CommandBlock root{0, c.root.model->table_id()};
            auto id = root.add(1, 0, pair32(0, column(c.root, "Id")));
            auto name = root.add(1, 0, pair32(0, column(c.root, "Name")));
            const auto literal = cells(profile, scalar(5), {bytes("a_%'")});
            auto predicate =
                root.clause(16, record({num(1, operation), token(2, name), {3, 6, literal}, num(4, 92)}), true, 1);
            root.clause(1, record({token(1, predicate)}));
            root.clause(6, selection_record({{id, "Id", scalar(3)}}, true));
            const auto snapshot = root;
            auto imported = root.add(7, 4, u32(0));
            root.clause(9, record({token(1, imported), num(2, 1)}));
            auto direct = c.query();
            auto did = direct.column(0, column(c.root, "Id"));
            auto dname = direct.column(0, column(c.root, "Name"));
            where(direct, direct.clause(16, record({num(1, operation), token(2, dname), {3, 6, literal}, num(4, 92)})));
            select(direct, {{did, "Id", scalar(3)}});
            auto dimport = direct.import_plan(direct.clone_selected()->seal(8));
            direct.clause(9, record({token(1, dimport), num(2, 1)}));
            same_command(compile(query_command({snapshot, root})), direct.seal(8));
        }
        {
            const auto literal = cells(profile, scalar(5), {bytes("a_%'")});
            auto command = [&](std::uint32_t operation, std::uint32_t escape, const Bytes& repeated_literal)
            {
                CommandBlock root{0, c.root.model->table_id()};
                const auto id = root.add(1, 0, pair32(0, column(c.root, "Id")));
                const auto first = root.add(1, 0, pair32(0, column(c.root, "Name")));
                const auto second = root.add(1, 0, pair32(0, column(c.root, "Name")));
                root.clause(16, record({num(1, 0), token(2, first), {3, 6, literal}, num(4, 92)}), true, 1);
                const auto predicate = root.clause(
                    16, record({num(1, operation), token(2, second), {3, 6, repeated_literal}, num(4, escape)}), true,
                    1);
                root.clause(1, record({token(1, predicate)}));
                root.clause(6, selection_record({{id, "Id", scalar(3)}}, true));
                return query_command({root});
            };
            // Origin identity excludes the expression token, but includes all
            // text-transform inputs. Reusing one origin with another expression
            // preserves the literal snapshot after the command buffer is freed.
            auto encoded = command(0, 92, literal);
            auto plan = compile(encoded);
            std::fill(encoded.begin(), encoded.end(), 0);
            const auto result = execute(plan);
            check(result.arguments.size() == 1 && result.arguments[0].value == bytes("%a\\_\\%'%"),
                  "text origin shares transform inputs while retaining its own scalar snapshot");
            rejects(NW_INVALID_ARGUMENT, [&] { (void)compile(command(1, 92, literal)); });
            rejects(NW_INVALID_ARGUMENT, [&] { (void)compile(command(0, 33, literal)); });
            rejects(NW_INVALID_ARGUMENT,
                    [&] { (void)compile(command(0, 92, cells(profile, scalar(5), {bytes("a_")}))); });
        }
        {
            CommandBlock root{0, c.root.model->table_id()};
            auto id = root.add(1, 0, pair32(0, column(c.root, "Id")));
            auto parameter = root.add(3, 0, command_parameter(301, "minimum"), true, 1);
            auto predicate = root.add(4, 4, pair32(id, parameter));
            root.clause(1, record({token(1, predicate)}));
            root.clause(6, selection_record({{id, "Id", scalar(3)}}, true));
            auto snapshot = root;
            auto imported = root.add(7, 4, u32(0));
            root.clause(9, record({token(1, imported), num(2, 1)}));
            auto direct = c.query();
            auto did = direct.column(0, column(c.root, "Id"));
            auto dp = direct.parameter({301, "minimum", scalar(3), 3});
            where(direct, direct.binary(4, did, dp));
            select(direct, {{did, "Id", scalar(3)}});
            auto dimport = direct.import_plan(direct.clone_selected()->seal(8));
            direct.clause(9, record({token(1, dimport), num(2, 1)}));
            same_command(compile(query_command({snapshot, root})), direct.seal(8), {u64(3)});
        }
        {
            auto body = selected_command(c, 1);
            auto declared = c.declared("SelectedParents");
            auto encoded = query_command({body});
            auto cte =
                create_query_command_cte(encoded, dependencies, context, declared, declared.model->table_id(), 0);
            std::fill(encoded.begin(), encoded.end(), 0);
            const std::vector<QueryCommandDependency> external{declared, cte};
            CommandBlock root{0, declared.model->table_id()};
            auto imported = root.add(7, 3, u32(1));
            root.clause(10, record({token(1, imported)}));
            auto id = root.add(1, 0, pair32(0, column(declared, "Id")));
            root.clause(3, record({token(1, id), num(2, 0), num(3, 1), num(4, 1)}));
            root.clause(6, selection_record({{id, "Id", scalar(3)}}, true));
            const auto result = execute(compile_query_command(query_command({root}), external, context, 0));
            const auto oracle = oracle_cases(c);
            const auto expected = std::find_if(oracle.begin(), oracle.end(),
                                               [](const auto& value) { return value.first == "cte-snapshot"; });
            check(result.sql == expected->second.sql && parameter_wire(result) == parameter_wire(expected->second),
                  "complete command CTE compilation and immutable owner import");
            rejects(NW_INVALID_ARGUMENT,
                    [&]
                    {
                        (void)create_query_command_cte(query_command({body}), dependencies, context, declared,
                                                       declared.model->table_id(), 1);
                    });
        }
        {
            auto declared = c.declared("Numbers");
            const std::vector<QueryCommandDependency> recursive_dependencies{c.root, declared};
            CommandBlock member{1, declared.model->table_id()};
            auto id = member.add(1, 0, pair32(0, column(declared, "Id")));
            auto one = member.add(2, 0, cells(profile, scalar(3), {u64(1)}), true, 1);
            auto three = member.add(2, 0, cells(profile, scalar(3), {u64(3)}), true, 2);
            auto next = member.add(4, 7, pair32(id, one));
            auto predicate = member.add(4, 5, pair32(id, three));
            member.clause(1, record({token(1, predicate)}));
            member.clause(6, selection_record({{next, "Id", scalar(3)}}, true));
            auto anchor = selected_command(c, 0, 3);
            put_u16(anchor.records[2], 6, 1);
            auto imported = anchor.add(7, 4, u32(0));
            anchor.clause(9, record({token(1, imported), num(2, 1)}));
            auto cte = create_query_command_cte(query_command({member, anchor}), recursive_dependencies, context,
                                                declared, declared.model->table_id(), 1);
            const std::vector<QueryCommandDependency> external{declared, cte};
            CommandBlock root{0, declared.model->table_id()};
            imported = root.add(7, 3, u32(1));
            root.clause(10, record({token(1, imported)}));
            id = root.add(1, 0, pair32(0, column(declared, "Id")));
            root.clause(3, record({token(1, id), num(2, 0), num(3, 1), num(4, 1)}));
            root.clause(6, selection_record({{id, "Id", scalar(3)}}, true));
            const auto result = execute(compile_query_command(query_command({root}), external, context, 0));
            const auto oracle = oracle_cases(c);
            const auto expected = std::find_if(oracle.begin(), oracle.end(),
                                               [](const auto& value) { return value.first == "recursive-cte"; });
            check(result.sql == expected->second.sql && parameter_wire(result) == parameter_wire(expected->second),
                  "complete command recursive CTE anchor/member validation");
        }
        {
            // An optional predicate may be constructed but not selected. Its
            // escaped native literal is retained and still subject to budgets.
            QueryLimits limits;
            limits.bytes = 9000;
            const QueryCommandContext bounded{c.registry, limits, all_caps(profile), 10'000'000'000};
            const auto literal = cells(profile, scalar(5), {bytes(std::string(3000, '%'))});
            auto declared = c.declared("UnusedFilter");
            auto direct = c.query(limits);
            auto did = direct.column(0, column(c.root, "Id"));
            auto dname = direct.column(0, column(c.root, "Name"));
            direct.clause(16, record({num(1, 0), token(2, dname), {3, 6, literal}, num(4, 92)}));
            direct.clause(6, selection_record({{did, "Id", scalar(3)}}, true));
            auto dcte = NativeCte::create(declared, declared.model->table_id(), direct, 0);
            auto dclone = direct.clone_selected();
            auto dimport = dclone->import_cte(dcte);
            dclone->clause(10, record({token(1, dimport)}));
            auto expected = dclone->seal(8);
            check(execute(expected).parameter_expectation.descriptors.empty(),
                  "unused CTE filter emits no parameters and fits the original literal budget");

            CommandBlock body{0, c.root.model->table_id()};
            auto id = body.add(1, 0, pair32(0, column(c.root, "Id")));
            auto name = body.add(1, 0, pair32(0, column(c.root, "Name")));
            body.clause(16, record({num(1, 0), token(2, name), {3, 6, literal}, num(4, 92)}), true, 1);
            body.clause(6, selection_record({{id, "Id", scalar(3)}}, true));
            auto cte = create_query_command_cte(query_command({body}), dependencies, bounded, declared,
                                                declared.model->table_id(), 0);
            const auto original = body;
            auto external = dependencies;
            external.emplace_back(cte);
            auto imported = body.add(7, 3, u32(2));
            body.clause(10, record({token(1, imported)}));
            auto seeded = compile_query_command(query_command({body}, {{1, 2, 1}}), external, bounded, 8);
            same_command(seeded, expected);
            check(seeded->retained_literal_bytes() == expected->retained_literal_bytes(),
                  "CTE origin seed preserves actual native literal-byte accounting");
            check(seeded->owned_bytes == expected->owned_bytes,
                  "CTE origin comparison uses the same projection record bytes");
            rejects(NW_DOMAIN_ERROR, [&] { (void)compile_query_command(query_command({body}), external, bounded, 8); });
            auto wide = bounded;
            wide.limits.bytes = 20000;
            auto independent = compile_query_command(query_command({body}), external, wide, 8);
            std::cout << "origin-budget provider=" << static_cast<std::uint32_t>(profile)
                      << " limit=9000 direct_status=0 unseeded_status=13 seeded_status=0"
                      << " direct_owned=" << expected->owned_bytes << " direct_expanded=" << expected->expanded_nodes()
                      << " direct_literal=" << expected->retained_literal_bytes()
                      << " unseeded_owned=" << independent->owned_bytes
                      << " unseeded_expanded=" << independent->expanded_nodes()
                      << " unseeded_literal=" << independent->retained_literal_bytes()
                      << " seeded_literal=" << seeded->retained_literal_bytes() << '\n';
            for (const auto& binding :
                 std::vector<std::array<std::uint32_t, 3>>{{0, 2, 1}, {1, 0, 1}, {1, 2, 0}, {1, 2, 2}, {2, 2, 1}})
                rejects(NW_INVALID_ARGUMENT, [&]
                        { (void)compile_query_command(query_command({original}, {binding}), external, bounded, 8); });
            rejects(NW_INVALID_ARGUMENT,
                    [&]
                    {
                        (void)compile_query_command(query_command({original}, {{1, 2, 1}, {1, 2, 1}}), external,
                                                    bounded, 8);
                    });
            auto altered = original;
            auto changed_literal = cells(profile, scalar(5), {bytes(std::string(3000, '_'))});
            const auto changed = record({num(1, 0), token(2, name), {3, 6, changed_literal}, num(4, 92)});
            altered.records[2].resize(16);
            altered.records[2].insert(altered.records[2].end(), changed.begin(), changed.end());
            put_u32(altered.records[2], 0, static_cast<std::uint32_t>(altered.records[2].size()));
            rejects(NW_INVALID_ARGUMENT,
                    [&] { (void)compile_query_command(query_command({altered}, {{1, 2, 1}}), external, bounded, 8); });

            auto declared2 = c.declared("SecondUnusedFilter");
            auto cte2 = create_query_command_cte(query_command({original}, {{1, 2, 1}}), external, bounded, declared2,
                                                 declared2.model->table_id(), 0);
            auto direct2 = NativeCte::create(declared2, declared2.model->table_id(), direct, 0);
            auto douter = c.query(limits);
            auto outer_id = douter.column(0, column(c.root, "Id"));
            select(douter, {{outer_id, "Id", scalar(3)}});
            douter.clause(10, record({token(1, douter.import_cte(dcte))}));
            douter.clause(10, record({token(1, douter.import_cte(direct2))}));
            CommandBlock outer{0, c.root.model->table_id()};
            outer_id = outer.add(1, 0, pair32(0, column(c.root, "Id")));
            outer.clause(6, selection_record({{outer_id, "Id", scalar(3)}}, true));
            outer.clause(10, record({token(1, outer.add(7, 3, u32(1)))}));
            outer.clause(10, record({token(1, outer.add(7, 3, u32(2)))}));
            std::vector<QueryCommandDependency> two_ctes{c.root, cte, cte2};
            same_command(compile_query_command(query_command({outer}), two_ctes, bounded, 8), douter.seal(8));
            // Equal literals from independently constructed bodies retain distinct
            // identity; omitting a seed must not silently merge them by value.
            two_ctes[2] = create_query_command_cte(query_command({original}), dependencies, bounded, declared2,
                                                   declared2.model->table_id(), 0);
            rejects(NW_DOMAIN_ERROR,
                    [&] { (void)compile_query_command(query_command({outer}), two_ctes, bounded, 8); });
            cte.reset();
            external.clear();
            two_ctes.clear();
            // Origin scalar ownership outlives release of the first CTE owner.
            const std::vector<QueryCommandDependency> retained{c.root, cte2};
            same_command(compile_query_command(query_command({original}, {{1, 1, 1}}), retained, bounded, 8),
                         direct.clone_selected()->seal(8));
        }
        {
            const auto valid = query_command({selected_command(c)});
            const std::vector<std::pair<std::size_t, std::uint32_t>> invalid32{
                {0, 0},  {8, 31},    {12, 0}, {16, 1}, {20, 0},  {24, 1}, {28, 1}, {32, 23}, {36, 99},
                {40, 0}, {44, 9999}, {48, 0}, {52, 1}, {56, 15}, {64, 2}, {68, 1}, {72, 1}};
            for (auto [offset, value] : invalid32)
            {
                auto bad = valid;
                put_u32(bad, offset, value);
                rejects(NW_INVALID_ARGUMENT, [&] { (void)compile(bad); });
            }
            auto version = valid;
            put_u32(version, 4, 2);
            rejects(NW_NOT_SUPPORTED, [&] { (void)compile(version); });
            auto unknown = valid;
            put_u16(unknown, 60, 99);
            rejects(NW_NOT_SUPPORTED, [&] { (void)compile(unknown); });
            for (auto size : {0U, 4U, 31U, 32U, 55U, 56U, 71U, 72U})
            {
                auto truncated = Bytes(valid.begin(), valid.begin() + size);
                if (size >= 12)
                    put_u32(truncated, 8, size);
                rejects(NW_INVALID_ARGUMENT, [&] { (void)compile(truncated); });
            }
            auto no_capability = context;
            no_capability.capabilities.command = false;
            rejects(NW_NOT_SUPPORTED, [&] { (void)compile_query_command(valid, dependencies, no_capability, 8); });
            auto mixed = dependencies;
            auto wrong = c.root;
            ++wrong.source.generation;
            mixed[1] = wrong;
            rejects(NW_INVALID_STATE, [&] { (void)compile_query_command(valid, mixed, context, 8); });
        }
        {
            CommandBlock broken{0, c.root.model->table_id()};
            broken.add(3, 0, command_parameter(401, "failed_command"), true, 1);
            broken.add(99, 0, {}, false);
            rejects(NW_NOT_SUPPORTED, [&] { (void)compile(query_command({broken})); });
            auto direct = c.query();
            direct.parameter({401, "after_failed_command", scalar(3), 3});
            check(true, "failed command does not register parameter IDs");
            direct.parameter({402, "existing_identity", scalar(3), 3});
            CommandBlock collision{0, c.root.model->table_id()};
            auto id = collision.add(1, 0, pair32(0, column(c.root, "Id")));
            collision.add(3, 0, command_parameter(403, "would_be_new"), true, 1);
            collision.add(3, 0, command_parameter(402, "conflicting_identity"), true, 2);
            collision.clause(6, selection_record({{id, "Id", scalar(3)}}, true));
            rejects(NW_INVALID_ARGUMENT, [&] { (void)compile(query_command({collision})); });
            direct.parameter({403, "after_atomic_rejection", scalar(3), 3});
            check(true, "source parameter batch registration rejects atomically");

            // This fails after WHERE has appended its AST and marker to the
            // private arena. The entire command and its shadow identities must
            // be discarded, including a parameter that was already admitted.
            CommandBlock overflow{0, c.root.model->table_id()};
            id = overflow.add(1, 0, pair32(0, column(c.root, "Id")));
            const auto parameter = overflow.add(3, 0, command_parameter(404, "private_before_failure"), true, 1);
            const auto predicate = overflow.add(4, 1, pair32(id, parameter));
            overflow.clause(6, selection_record({{id, "Id", scalar(3)}}, true));
            overflow.clause(1, record({token(1, predicate)}));
            auto bounded = context;
            bounded.limits.nodes = 9;
            rejects(NW_DOMAIN_ERROR,
                    [&] { (void)compile_query_command(query_command({overflow}), dependencies, bounded, 8); });
            direct.parameter({404, "after_private_failure", scalar(3), 3});
            check(true, "failed private arena does not publish its already-admitted parameter identity");
        }
    }
}

std::vector<std::pair<std::string, RenderedQuery>> oracle_cases(const Context& c)
{
    std::vector<std::pair<std::string, RenderedQuery>> cases;
    auto add = [&](std::string label, NativeQuery& query)
    { cases.emplace_back(std::move(label), execute(query.seal(0))); };
    {
        auto q = c.query();
        auto id = q.column(0, column(c.root, "Id"));
        auto one = c.value(q, 1);
        where(q, q.binary(4, id, one));
        order(q, id);
        c.select_all(q);
        add("filter-order", q);
    }
    for (std::uint32_t op : {1U, 2U, 3U})
    {
        auto q = c.query();
        auto id = q.column(0, column(c.root, "Id"));
        auto payload =
            cells(c.source.profile, scalar(3),
                  op == 2 ? std::vector<std::optional<Bytes>>{} : std::vector<std::optional<Bytes>>{u64(1), u64(2)});
        where(q, q.list(op, id, payload));
        order(q, id);
        select(q, {{id, "Id", scalar(3)}});
        add("membership", q);
    }
    {
        auto q = c.query();
        auto root = q.column(0, column(c.root, "Id"));
        auto model = q.import_model(c.child);
        auto alias =
            q.clause(18, record({token(1, model), num(2, c.child.model->table_id()), {3, 4, bytes("c")}, num(4, 1)}));
        auto child = q.column(alias, column(c.child, "ParentId"));
        auto lifted = q.unary(4, root);
        auto on = q.binary(1, lifted, child);
        q.clause(7, record({token(1, alias), token(2, on)}));
        rejects(NW_INVALID_ARGUMENT, [&] { q.expression_type(child); });
        auto child_id = q.column(alias, column(c.child, "Id"));
        auto matched = q.clause(20, record({token(1, alias)}));
        order(q, root);
        select(q, {{root, "Id", scalar(3)}, {child_id, "child", scalar(3, true)}, {matched, "matched", scalar(4)}});
        add("nullable-left-join", q);
    }
    {
        auto q = c.query();
        auto count = q.clause(13, record({num(1, 0), num(3, 0), num(5, 3)}));
        auto id = q.column(0, column(c.root, "Id"));
        auto sum = q.clause(13, record({num(1, 2), token(2, id), num(3, 0), num(5, 3)}));
        select(q, {{count, "count", scalar(3)}, {sum, "sum", scalar(3, true)}});
        add("aggregate", q);
    }
    {
        auto q = c.query();
        auto id = q.column(0, column(c.root, "Id"));
        auto window = q.clause(11, record({num(1, 0)}));
        q.clause(11, record({num(1, 2), token(2, window), token(3, id), num(4, 0), num(5, 1)}));
        auto rank = q.clause(13, record({num(1, 6), num(3, 0), num(5, 3)}));
        rank = q.clause(21, record({token(1, rank), token(2, window)}));
        q.clause(11, record({num(1, 1), token(2, window), token(3, id)}));
        order(q, id);
        select(q, {{id, "Id", scalar(3)}, {rank, "rank", scalar(3)}});
        add("window-snapshot", q);
    }
    for (std::uint32_t op = 0; op < 4; ++op)
    {
        auto a = c.selected(4, 1);
        auto b = c.selected(1, 0);
        auto imported = a.import_plan(b.seal(8));
        a.clause(9, record({token(1, imported), num(2, op)}));
        add("set-operation", a);
    }
    for (std::uint32_t op = 0; op < 2; ++op)
    {
        auto source = c.selected(1, 1);
        auto snapshot = source.clone_selected()->seal(8);
        c.value(source, 2);
        auto q = c.query();
        auto id = q.column(0, column(c.root, "Id"));
        auto imported = q.import_plan(snapshot);
        std::vector<ControlField> fields{token(1, imported), num(2, op)};
        if (op)
            fields.push_back(token(3, id));
        where(q, q.clause(14, record(fields)));
        order(q, id);
        select(q, {{id, "Id", scalar(3)}});
        add("subquery", q);
    }
    {
        auto body = c.selected(4, 1);
        auto declared = c.declared("SelectedParents");
        auto cte = NativeCte::create(declared, declared.model->table_id(), body, 0);
        auto zero = c.value(body, 0);
        auto body_id = body.column(0, column(c.root, "Id"));
        where(body, body.binary(1, body_id, zero));
        auto q = c.from_model(declared);
        auto imported = q.import_cte(cte);
        q.clause(10, record({token(1, imported)}));
        auto id = q.column(0, column(declared, "Id"));
        order(q, id);
        select(q, {{id, "Id", scalar(3)}});
        add("cte-snapshot", q);
    }
    {
        auto declared = c.declared("Numbers");
        auto anchor = c.selected(1, 0);
        rejects(NW_INVALID_ARGUMENT, [&] { NativeCte::create(declared, declared.model->table_id(), anchor, 1); });
        auto member = c.from_model(declared);
        auto id = member.column(0, column(declared, "Id"));
        auto one = c.value(member, 1);
        auto three = c.value(member, 3);
        auto next = member.binary(7, id, one);
        where(member, member.binary(5, id, three));
        select(member, {{next, "Id", scalar(3)}});
        auto imported = anchor.import_plan(member.seal(8));
        anchor.clause(9, record({token(1, imported), num(2, 1)}));
        auto recursive = NativeCte::create(declared, declared.model->table_id(), anchor, 1);
        auto q = c.from_model(declared);
        imported = q.import_cte(recursive);
        q.clause(10, record({token(1, imported)}));
        id = q.column(0, column(declared, "Id"));
        order(q, id);
        select(q, {{id, "Id", scalar(3)}});
        add("recursive-cte", q);
    }
    for (bool direct : {false, true})
    {
        auto q = c.query();
        auto id = q.column(0, column(c.root, "Id"));
        auto window = q.clause(11, record({num(1, 0)}));
        q.clause(11, record({num(1, 2), token(2, window), token(3, id), num(4, 0), num(5, 1)}));
        q.clause(11, record({num(1, 3), token(2, window), num(6, 0), num(7, 1)}));
        std::vector<ControlField> fields{num(1, 2), token(2, id), num(3, 0)};
        if (direct)
            fields.push_back(token(4, window));
        fields.push_back(num(5, 3));
        auto sum = q.clause(13, record(fields));
        if (!direct)
            sum = q.clause(21, record({token(1, sum), token(2, window)}));
        order(q, id);
        select(q, {{sum, "sum", scalar(3, true)}});
        add("window-aggregate-cast", q);
    }
    for (const auto& [op, text] : std::vector<std::pair<std::uint32_t, std::string>>{
             {0, "Reilly"}, {1, "Ali"}, {2, "🦀"}, {3, "O%"}, {0, "%"}, {0, "_"}, {0, "';DROP"}})
    {
        auto q = c.query();
        auto id = q.column(0, column(c.root, "Id"));
        auto name = q.column(0, column(c.root, "Name"));
        auto payload = cells(c.source.profile, scalar(5), {bytes(text)});
        auto predicate = q.clause(16, record({num(1, op), token(2, name), {3, 6, payload}, num(4, 92)}));
        where(q, predicate);
        order(q, id);
        select(q, {{id, "Id", scalar(3)}});
        add("text-escaped-values", q);
    }
    {
        auto q = c.query();
        auto id = q.column(0, column(c.root, "Id"));
        auto one = c.value(q, 1);
        auto zero = c.value(q, 0);
        auto condition = q.binary(3, id, one);
        auto value = q.clause(15, record({token(1, condition), token(2, one), token(3, zero)}));
        order(q, id);
        select(q, {{value, "case", scalar(3)}});
        add("case", q);
    }
    {
        auto q = c.query();
        auto id = q.column(0, column(c.root, "Id"));
        q.clause(4, record({token(1, id)}));
        auto count = q.clause(13, record({num(1, 0), num(3, 0), num(5, 3)}));
        auto zero = c.value(q, 0);
        auto predicate = q.binary(3, count, zero);
        q.clause(2, record({token(1, predicate)}));
        order(q, id);
        select(q, {{id, "Id", scalar(3)}});
        add("group-having", q);
    }
    return cases;
}

// SQL and argument snapshots below were copied without alteration from the fixed
// Rust baseline's DB-B11 execution oracle (SeaQuery 1.0.2). Generated rows are
// independently exercised by the existing PostgreSQL and SQLite managed suites.
const char* sqlite_oracle =
    R"oracle([{"label":"filter-order","ordered_arguments":["0100000000000000"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\", \"Parents\".\"Name\" AS \"Name\" FROM \"Parents\" WHERE \"Parents\".\"Id\" >= ? ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"membership","ordered_arguments":["0100000000000000","0200000000000000"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" IN (?, ?) ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"membership","ordered_arguments":[],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE TRUE ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"membership","ordered_arguments":["0100000000000000","0200000000000000"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" BETWEEN ? AND ? ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"nullable-left-join","ordered_arguments":[],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\", \"c\".\"Id\" AS \"child\", \"c\".\"_nw_match\" IS NOT NULL AS \"matched\" FROM \"Parents\" LEFT JOIN (SELECT \"ParentId\", \"Id\", 1 AS \"_nw_match\" FROM \"Children\") AS \"c\" ON \"Parents\".\"Id\" = \"c\".\"ParentId\" ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"aggregate","ordered_arguments":[],"sql":"SELECT COUNT(*) AS \"count\", SUM(\"Parents\".\"Id\") AS \"sum\" FROM \"Parents\""},{"label":"window-snapshot","ordered_arguments":[],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\", (ROW_NUMBER() OVER (  ORDER BY \"Parents\".\"Id\" ASC NULLS LAST )) AS \"rank\" FROM \"Parents\" ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"set-operation","ordered_arguments":["0100000000000000","0000000000000000"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" >= ? UNION SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" = ?"},{"label":"set-operation","ordered_arguments":["0100000000000000","0000000000000000"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" >= ? UNION ALL SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" = ?"},{"label":"set-operation","ordered_arguments":["0100000000000000","0000000000000000"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" >= ? INTERSECT SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" = ?"},{"label":"set-operation","ordered_arguments":["0100000000000000","0000000000000000"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" >= ? EXCEPT SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" = ?"},{"label":"subquery","ordered_arguments":["0100000000000000"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE EXISTS(SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" = ?) ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"subquery","ordered_arguments":["0100000000000000"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" IN (SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" = ?) ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"cte-snapshot","ordered_arguments":["0100000000000000"],"sql":"WITH \"SelectedParents\" (\"Id\") AS (SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" >= ?) SELECT \"SelectedParents\".\"Id\" AS \"Id\" FROM \"SelectedParents\" ORDER BY \"SelectedParents\".\"Id\" ASC NULLS LAST"},{"label":"recursive-cte","ordered_arguments":["0000000000000000","0100000000000000","0300000000000000"],"sql":"WITH RECURSIVE \"Numbers\" (\"Id\") AS (SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" = ? UNION ALL SELECT \"Numbers\".\"Id\" + ? AS \"Id\" FROM \"Numbers\" WHERE \"Numbers\".\"Id\" < ?) SELECT \"Numbers\".\"Id\" AS \"Id\" FROM \"Numbers\" ORDER BY \"Numbers\".\"Id\" ASC NULLS LAST"},{"label":"window-aggregate-cast","ordered_arguments":[],"sql":"SELECT (SUM(\"Parents\".\"Id\") OVER (  ORDER BY \"Parents\".\"Id\" ASC NULLS LAST ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW )) AS \"sum\" FROM \"Parents\" ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"window-aggregate-cast","ordered_arguments":[],"sql":"SELECT (SUM(\"Parents\".\"Id\") OVER (  ORDER BY \"Parents\".\"Id\" ASC NULLS LAST ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW )) AS \"sum\" FROM \"Parents\" ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"text-escaped-values","ordered_arguments":["255265696c6c7925"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Name\" LIKE ? ESCAPE '\\' ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"text-escaped-values","ordered_arguments":["416c6925"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Name\" LIKE ? ESCAPE '\\' ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"text-escaped-values","ordered_arguments":["25f09fa680"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Name\" LIKE ? ESCAPE '\\' ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"text-escaped-values","ordered_arguments":["4f25"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Name\" LIKE ? ESCAPE '\\' ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"text-escaped-values","ordered_arguments":["255c2525"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Name\" LIKE ? ESCAPE '\\' ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"text-escaped-values","ordered_arguments":["255c5f25"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Name\" LIKE ? ESCAPE '\\' ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"text-escaped-values","ordered_arguments":["25273b44524f5025"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Name\" LIKE ? ESCAPE '\\' ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"case","ordered_arguments":["0100000000000000","0100000000000000","0000000000000000"],"sql":"SELECT (CASE WHEN (\"Parents\".\"Id\" > ?) THEN ? ELSE ? END) AS \"case\" FROM \"Parents\" ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"group-having","ordered_arguments":["0000000000000000"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" GROUP BY \"Parents\".\"Id\" HAVING COUNT(*) > ? ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"}])oracle";
const char* postgres_oracle =
    R"oracle([{"label":"filter-order","ordered_arguments":["0100000000000000"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\", \"Parents\".\"Name\" AS \"Name\" FROM \"Parents\" WHERE \"Parents\".\"Id\" >= $1 ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"membership","ordered_arguments":["0100000000000000","0200000000000000"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" IN ($1, $2) ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"membership","ordered_arguments":[],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE TRUE ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"membership","ordered_arguments":["0100000000000000","0200000000000000"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" BETWEEN $1 AND $2 ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"nullable-left-join","ordered_arguments":[],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\", \"c\".\"Id\" AS \"child\", \"c\".\"_nw_match\" IS NOT NULL AS \"matched\" FROM \"Parents\" LEFT JOIN (SELECT \"ParentId\", \"Id\", 1 AS \"_nw_match\" FROM \"Children\") AS \"c\" ON \"Parents\".\"Id\" = \"c\".\"ParentId\" ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"aggregate","ordered_arguments":[],"sql":"SELECT COUNT(*) AS \"count\", CAST(SUM(\"Parents\".\"Id\") AS bigint) AS \"sum\" FROM \"Parents\""},{"label":"window-snapshot","ordered_arguments":[],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\", (ROW_NUMBER() OVER (  ORDER BY \"Parents\".\"Id\" ASC NULLS LAST )) AS \"rank\" FROM \"Parents\" ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"set-operation","ordered_arguments":["0100000000000000","0000000000000000"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" >= $1 UNION (SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" = $2)"},{"label":"set-operation","ordered_arguments":["0100000000000000","0000000000000000"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" >= $1 UNION ALL (SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" = $2)"},{"label":"set-operation","ordered_arguments":["0100000000000000","0000000000000000"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" >= $1 INTERSECT (SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" = $2)"},{"label":"set-operation","ordered_arguments":["0100000000000000","0000000000000000"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" >= $1 EXCEPT (SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" = $2)"},{"label":"subquery","ordered_arguments":["0100000000000000"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE EXISTS(SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" = $1) ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"subquery","ordered_arguments":["0100000000000000"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" IN (SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" = $1) ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"cte-snapshot","ordered_arguments":["0100000000000000"],"sql":"WITH \"SelectedParents\" (\"Id\") AS (SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" >= $1) SELECT \"SelectedParents\".\"Id\" AS \"Id\" FROM \"SelectedParents\" ORDER BY \"SelectedParents\".\"Id\" ASC NULLS LAST"},{"label":"recursive-cte","ordered_arguments":["0000000000000000","0100000000000000","0300000000000000"],"sql":"WITH RECURSIVE \"Numbers\" (\"Id\") AS (SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Id\" = $1 UNION ALL (SELECT \"Numbers\".\"Id\" + $2 AS \"Id\" FROM \"Numbers\" WHERE \"Numbers\".\"Id\" < $3)) SELECT \"Numbers\".\"Id\" AS \"Id\" FROM \"Numbers\" ORDER BY \"Numbers\".\"Id\" ASC NULLS LAST"},{"label":"window-aggregate-cast","ordered_arguments":[],"sql":"SELECT CAST(((SUM(\"Parents\".\"Id\") OVER (  ORDER BY \"Parents\".\"Id\" ASC NULLS LAST ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW ))) AS bigint) AS \"sum\" FROM \"Parents\" ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"window-aggregate-cast","ordered_arguments":[],"sql":"SELECT CAST(((SUM(\"Parents\".\"Id\") OVER (  ORDER BY \"Parents\".\"Id\" ASC NULLS LAST ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW ))) AS bigint) AS \"sum\" FROM \"Parents\" ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"text-escaped-values","ordered_arguments":["255265696c6c7925"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Name\" LIKE $1 ESCAPE E'\\\\' ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"text-escaped-values","ordered_arguments":["416c6925"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Name\" LIKE $1 ESCAPE E'\\\\' ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"text-escaped-values","ordered_arguments":["25f09fa680"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Name\" LIKE $1 ESCAPE E'\\\\' ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"text-escaped-values","ordered_arguments":["4f25"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Name\" LIKE $1 ESCAPE E'\\\\' ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"text-escaped-values","ordered_arguments":["255c2525"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Name\" LIKE $1 ESCAPE E'\\\\' ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"text-escaped-values","ordered_arguments":["255c5f25"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Name\" LIKE $1 ESCAPE E'\\\\' ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"text-escaped-values","ordered_arguments":["25273b44524f5025"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" WHERE \"Parents\".\"Name\" LIKE $1 ESCAPE E'\\\\' ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"case","ordered_arguments":["0100000000000000","0100000000000000","0000000000000000"],"sql":"SELECT (CASE WHEN (\"Parents\".\"Id\" > $1) THEN $2 ELSE $3 END) AS \"case\" FROM \"Parents\" ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"},{"label":"group-having","ordered_arguments":["0000000000000000"],"sql":"SELECT \"Parents\".\"Id\" AS \"Id\" FROM \"Parents\" GROUP BY \"Parents\".\"Id\" HAVING COUNT(*) > $1 ORDER BY \"Parents\".\"Id\" ASC NULLS LAST"}])oracle";

void assert_oracle(Profile profile)
{
    Context c(profile);
    auto actual = oracle_cases(c);
    auto expected = nlohmann::json::parse(profile == Profile::Sqlite ? sqlite_oracle : postgres_oracle);
    check(actual.size() == expected.size(), "oracle case count");
    for (std::size_t i = 0; i < actual.size(); ++i)
    {
        const auto& [label, rendered] = actual[i];
        const auto& want = expected[i];
        check(label == want.at("label").get<std::string>(), "oracle label");
        check(rendered.sql == want.at("sql").get<std::string>(),
              "SQL golden " + label + "\nactual: " + rendered.sql + "\nwanted: " + want.at("sql").get<std::string>());
        auto batch = Batch::parse(parameter_wire(rendered), rendered.parameter_expectation);
        const auto& args = want.at("ordered_arguments");
        check(args.size() == rendered.parameter_expectation.descriptors.size(), "argument count " + label);
        for (std::size_t k = 0; k < args.size(); ++k)
        {
            auto cell = batch.cell(0, k);
            auto hex_value = cell ? hex(*cell) : "null";
            check(hex_value == args[k].get<std::string>(), "argument order/value " + label);
        }
    }
}

void binding_and_safety()
{
    const auto capabilities = all_caps(Profile::Sqlite);
    check(capabilities.query && capabilities.metadata && capabilities.scalar_codecs,
          "schema draft capability leaves existing query capabilities available");
    check(Capabilities::from_mask(1ULL << 26, Profile::Sqlite).command, "complete query capability bit");
    check(!Capabilities::from_mask(1ULL << 27, Profile::Sqlite).query,
          "cursor pull capability does not authorize query composition");
    check(!Capabilities::from_mask(1ULL << 28, Profile::Sqlite).query,
          "one-shot command completion does not authorize query composition");
    rejects(NW_INVALID_ARGUMENT, [] { (void)Capabilities::from_mask(1ULL << 29, Profile::Sqlite); });
    rejects(NW_INVALID_ARGUMENT, [] { (void)Capabilities::from_mask(1ULL << 63, Profile::PostgreSql); });
    Context c(Profile::Sqlite);
    auto q = c.query();
    auto integer = q.column(0, column(c.root, "Id"));
    auto text = q.column(0, column(c.root, "Name"));
    for (std::uint32_t op = 1; op <= 10; ++op)
        check(q.expression_type(q.binary(op, integer, integer)).codec.id == (op <= 6 ? 4U : 3U), "binary domains");
    auto predicate = q.binary(1, integer, integer);
    for (auto op : {11U, 12U})
    {
        q.binary(op, predicate, predicate);
        rejects(NW_INVALID_ARGUMENT, [&] { q.binary(op, integer, integer); });
    }
    for (auto op : {13U, 14U})
        q.binary(op, integer, integer);
    rejects(NW_INVALID_ARGUMENT, [&] { q.binary(1, integer, text); });
    rejects(NW_NOT_SUPPORTED, [&] { q.binary(15, integer, integer); });
    rejects(NW_INVALID_ARGUMENT, [&] { q.unary(3, integer); });
    rejects(NW_NOT_SUPPORTED, [&] { q.unary(9, integer); });
    auto lifted = q.unary(4, integer);
    check(q.expression_type(lifted).codec.nullable, "nullable lift");
    rejects(NW_INVALID_ARGUMENT, [&] { q.unary(4, lifted); });
    for (auto op : {1U, 2U})
        check(!q.expression_type(q.unary(op, lifted)).codec.nullable, "null tests return nonnullable bool");
    auto foreign = c.query();
    rejects(NW_INVALID_ARGUMENT, [&] { foreign.expression_type(integer); });
    auto before = q.counts();
    for (std::size_t size = 0; size < 128; ++size)
        rejects(NW_INVALID_ARGUMENT, [&] { q.clause(1, Bytes(size)); });
    rejects(NW_INVALID_ARGUMENT, [&] { q.clause(5, record({token(1, UINT64_MAX)})); });
    check(before == q.counts(), "rejected calls are atomic");
    auto invalid_utf8 = cells(c.source.profile, scalar(5), {bytes("x")});
    invalid_utf8.back() = 255;
    rejects(NW_INVALID_ARGUMENT, [&] { q.value(invalid_utf8); });
    auto nonfinite = cells(c.source.profile, scalar(11), {u64(std::bit_cast<std::uint64_t>(1.0))});
    put_u64(nonfinite, nonfinite.size() - 8, std::bit_cast<std::uint64_t>(std::numeric_limits<double>::quiet_NaN()));
    rejects(NW_INVALID_ARGUMENT, [&] { q.value(nonfinite); });
    check(before == q.counts(), "malformed values do not mutate arena");
    auto limited = c.query({128, 64, 4 * 1024 * 1024, 4096});
    auto expression = limited.column(0, column(c.root, "Id"));
    bool rejected = false;
    for (unsigned i = 0; i < 20; ++i)
    {
        auto snapshot = limited.counts();
        try
        {
            expression = limited.binary(7, expression, expression);
        }
        catch (const Error& error)
        {
            check(error.status == NW_DOMAIN_ERROR, "expanded budget status");
            check(snapshot == limited.counts(), "expanded budget atomic");
            rejected = true;
            break;
        }
    }
    check(rejected && std::get<2>(limited.counts()) <= 128, "expanded tree is bounded");
    {
        // WHERE mutates its candidate before the final expanded-node check.
        // Public ABI edits must still roll that late failure back and remain
        // usable; only command-private arenas may be abandoned in place.
        auto atomic = c.query({8, 64, 4 * 1024 * 1024, 4096});
        const auto id = atomic.column(0, column(c.root, "Id"));
        const auto predicate = atomic.binary(1, id, id);
        select(atomic, {{id, "Id", scalar(3)}});
        const auto snapshot = atomic.counts();
        const auto sql = atomic.clone_selected()->seal(8)->compiled_shape()->sql;
        rejects(NW_DOMAIN_ERROR, [&] { where(atomic, predicate); });
        check(atomic.state() == QueryState::Mutable && atomic.counts() == snapshot,
              "late public clause failure preserves mutable arena and counters");
        check(atomic.clone_selected()->seal(8)->compiled_shape()->sql == sql,
              "late public clause failure does not retain its predicate");
        order(atomic, id);
        check(atomic.seal(8)->compiled_shape()->sql.find("ORDER BY") != std::string::npos,
              "public arena remains usable after a rolled-back clause");
    }
    auto closed = c.query();
    closed.close();
    rejects(NW_INVALID_STATE, [&] { closed.column(0, column(c.root, "Id")); });
    auto poisoned = c.query();
    poisoned.poison();
    rejects(NW_ENGINE_POISONED, [&] { poisoned.column(0, column(c.root, "Id")); });
    auto empty = c.query();
    rejects(NW_INVALID_ARGUMENT, [&] { empty.seal(0); });
}

void plans_and_cache()
{
    for (auto profile : {Profile::Sqlite, Profile::PostgreSql})
    {
        Context c(profile);
        auto q = c.selected(4, 7);
        auto id = q.column(0, column(c.root, "Id"));
        order(q, id);
        q.clause(5, record({token(1, 5), token(2, 2)}));
        auto rendered = execute(q.seal(0));
        auto b = Batch::parse(parameter_wire(rendered), rendered.parameter_expectation);
        for (std::size_t i = 0; i < 3; ++i)
            check(read_u64(*b.cell(0, i), 0) == std::array<std::uint64_t, 3>{7, 5, 2}[i],
                  "filter/limit/offset argument order");
        auto param_query = c.query();
        auto id2 = param_query.column(0, column(c.root, "Id"));
        auto p = param_query.parameter({7, "threshold", scalar(3), 3});
        where(param_query, param_query.binary(4, id2, p));
        select(param_query, {{id2, "Id", scalar(3)}});
        auto prepared = param_query.seal(8);
        auto a = execute(prepared, {u64(1)});
        auto second = execute(prepared, {u64(2)});
        check(a.sql == second.sql && parameter_wire(a) != parameter_wire(second),
              "prepared parameter snapshots isolated");
        auto wrong_source = c.source;
        ++wrong_source.source;
        rejects(NW_INVALID_STATE,
                [&] { prepared->render(wrong_source, Batch::encode(prepared->parameter_expectation(), 1, {u64(1)})); });
        auto ordinary_q = c.selected(1, 1);
        auto ordinary = ordinary_q.seal(0);
        execute(ordinary);
        rejects(NW_INVALID_STATE, [&] { execute(ordinary); });
        rejects(NW_INVALID_STATE, [&] { ordinary_q.column(0, column(c.root, "Id")); });
        auto first = c.selected(4, 1).seal(8);
        auto next = c.selected(4, 2).seal(8);
        auto different = c.selected(5, 2).seal(8);
        Hash abi;
        abi.fill(11);
        ShapeCache cache(1, 8192, abi);
        auto retained = cache.acquire(*first);
        auto reused = cache.acquire(*next);
        check(retained == reused, "cache excludes literal values");
        auto shape = cache.acquire(*different);
        check(shape != retained && cache.counts().first == 1, "cache bounded eviction");
        a = execute(first, {}, &cache);
        second = execute(next, {}, &cache);
        check(a.sql == second.sql && parameter_wire(a) != parameter_wire(second), "cache never captures values");
        cache.clear();
        check(cache.counts() == std::pair<std::size_t, std::size_t>{0, 0} && !retained->sql.empty(),
              "retained shape outlives clear");
        ShapeCache tiny(1, 1, abi);
        tiny.acquire(*first);
        check(tiny.counts().first == 0, "oversized shape executes uncached");
        rejects(NW_INVALID_ARGUMENT, [&] { ShapeCache invalid(0, 100, abi); });
        rejects(NW_INVALID_ARGUMENT, [&] { ShapeCache invalid(1, 100, {}); });
        auto timeout = c.selected(1, 1);
        timeout.clause(22, record({{1, 3, u64(0)}}));
        check(timeout.seal(0)->timeout_ns == 0, "explicit zero timeout");
        auto group = c.query();
        auto key = group.column(0, column(c.root, "Id"));
        group.clause(4, record({token(1, key)}));
        auto same = group.column(0, column(c.root, "Id"));
        select(group, {{same, "Id", scalar(3)}});
        execute(group.seal(0));
        auto unsafe = c.query();
        auto count = unsafe.clause(13, record({num(1, 0), num(3, 0), num(5, 3)}));
        auto list = unsafe.list(1, count, cells(profile, scalar(3), {u64(1)}));
        rejects(NW_INVALID_ARGUMENT, [&] { where(unsafe, list); });
    }
}

void concurrent_shape_cache()
{
    for (auto profile : {Profile::Sqlite, Profile::PostgreSql})
    {
        Context c(profile);
        std::array<std::shared_ptr<NativePlan>, 3> plans;
        std::array<std::string, 3> sql;
        for (std::size_t i = 0; i < plans.size(); ++i)
        {
            auto query = c.query();
            auto id = query.column(0, column(c.root, "Id"));
            auto parameter = query.parameter({7, "threshold", scalar(3), 3});
            where(query, query.binary(std::array<std::uint32_t, 3>{1, 4, 5}[i], id, parameter));
            select(query, {{id, "Id", scalar(3)}});
            plans[i] = query.seal(8);
            sql[i] = plans[i]->render_shape().first;
        }
        Hash abi;
        abi.fill(11);
        ShapeCache cache(2, 8192, abi);
        std::vector<std::future<bool>> workers;
        for (std::size_t worker = 0; worker < 4; ++worker)
            workers.push_back(std::async(std::launch::async,
                                         [&, worker]
                                         {
                                             for (std::size_t i = 0; i < 256; ++i)
                                             {
                                                 const auto selected = (worker + i) % plans.size();
                                                 const auto value = worker * 1000 + i + 1;
                                                 auto rendered = execute(plans[selected], {u64(value)}, &cache);
                                                 if (rendered.sql != sql[selected])
                                                     return false;
                                                 auto batch = Batch::parse(parameter_wire(rendered),
                                                                           rendered.parameter_expectation);
                                                 auto cell = batch.cell(0, 0);
                                                 if (!cell || read_u64(*cell, 0) != value)
                                                     return false;
                                                 auto retained = cache.acquire(*plans[selected]);
                                                 auto [entries, bytes] = cache.counts();
                                                 if (entries > 2 || bytes > 8192 || (entries == 0 && bytes != 0))
                                                     return false;
                                                 std::this_thread::yield();
                                                 if (retained->sql != sql[selected])
                                                     return false;
                                             }
                                             return true;
                                         }));
        bool clear_retains_shape = true;
        for (std::size_t i = 0; i < 256; ++i)
        {
            const auto selected = i % plans.size();
            auto retained = cache.acquire(*plans[selected]);
            cache.clear();
            std::this_thread::yield();
            clear_retains_shape &= retained->sql == sql[selected];
        }
        for (auto& worker : workers)
            check(worker.get(), "concurrent cache renders keep SQL, parameter values and bounds isolated");
        check(clear_retains_shape, "concurrent cache clear preserves acquired shape lifetime");
        cache.clear();
        check(cache.counts() == std::pair<std::size_t, std::size_t>{0, 0}, "concurrent cache releases bookkeeping");
    }
}

void sql_lexer()
{
    std::map<std::string, std::uint32_t> names{{"id", 1}, {"value", 2}};
    for (auto profile : {Profile::Sqlite, Profile::PostgreSql})
    {
        auto value =
            render_sql_template("SELECT @id, @id, @value, '@missing', \"@quoted\" -- @comment\n", names, profile, 0);
        check(value.parameter_ids == std::vector<std::uint32_t>{1, 1, 2},
              "explicit SQL repeated IDs preserve binding identity");
        check(value.sql.find(profile == Profile::Sqlite ? "SELECT ?1, ?1, ?2" : "SELECT $1, $1, $2") == 0,
              "explicit SQL parameter indexing");
        rejects(NW_INVALID_ARGUMENT, [&] { render_sql_template("SELECT @unknown", names, profile, 0); });
        rejects(NW_INVALID_ARGUMENT, [&] { render_sql_template("SELECT 1; SELECT 2", names, profile, 0); });
        rejects(NW_INVALID_ARGUMENT, [&] { render_sql_template("SELECT ?", names, profile, 0); });
        rejects(NW_INVALID_ARGUMENT, [&] { render_sql_template("SELECT $1", names, profile, 0); });
        rejects(NW_INVALID_ARGUMENT, [&] { render_sql_template("SELECT 'unterminated", names, profile, 0); });
        rejects(NW_INVALID_ARGUMENT, [&] { render_sql_template("COMMIT", {}, profile, 2); });
        auto nested = render_sql_template("/* outer /* inner */ end */ SELECT @id; -- done", {{"id", 1}}, profile, 0);
        check(nested.parameter_ids.size() == 1, "nested comments and trailing terminator");
    }
    auto dollar = render_sql_template("SELECT $tag$@not_parameter$tag$, @id", {{"id", 1}}, Profile::PostgreSql, 0);
    check(dollar.parameter_ids == std::vector<std::uint32_t>{1}, "dollar quoted contents opaque");
}

ModelBinding mutation_model(Profile profile)
{
    using J = nlohmann::ordered_json;
    const std::string logical = "Korda.Orm.Relations.Tests:Fixture";
    const std::string table = "b14_items";
    auto table_id = stable_id({"NWDB-TABLE-ID-v1", logical, {}, table});
    std::vector<std::uint32_t> ids;
    std::vector<J> columns;
    std::vector<std::string> names{"Tenant", "Id", "Name", "Version"};
    for (std::size_t i = 0; i < names.size(); ++i)
    {
        auto id = stable_id({"NWDB-COLUMN-ID-v1", logical, {}, table, names[i]});
        ids.push_back(id);
        columns.push_back(J{{"id", id},
                            {"name", names[i]},
                            {"codec", i == 2 ? 5 : 3},
                            {"codec_config", nullptr},
                            {"nullable", i == 2},
                            {"key_ordinal", i < 2 ? J(i) : J(nullptr)},
                            {"generated", false},
                            {"concurrency", i == 3},
                            {"collation", nullptr},
                            {"max_length", nullptr}});
    }
    std::sort(columns.begin(), columns.end(), [](const auto& a, const auto& b)
              { return a.at("id").template get<std::uint32_t>() < b.at("id").template get<std::uint32_t>(); });
    J json{{"version", 1},   {"logical_id", logical}, {"table_id", table_id}, {"schema", nullptr},
           {"table", table}, {"columns", columns},    {"projection", ids},    {"relations", J::array()}};
    return {{1, 1, 1, profile},
            std::make_shared<Model>(Model::parse(bytes(json.dump()), profile)),
            std::make_shared<CodecBindings>(profile)};
}
Bytes model_values(const ModelBinding& model, const std::vector<std::string>& names,
                   const std::vector<std::optional<Bytes>>& values, std::uint32_t rows = 1)
{
    Expectation expectation{4, model.source.profile, model.model->hash(), model.model->table_id(), 0, {}, rows};
    for (const auto& name : names)
    {
        const auto& col = model.model->column(column(model, name));
        expectation.descriptors.push_back({col.id, col.codec, model.codecs->bound(col.codec)});
    }
    return Batch::encode(expectation, rows, values);
}
ProjectionBinding model_projection(const ModelBinding& model, const std::vector<std::string>& names)
{
    using J = nlohmann::ordered_json;
    J fields = J::array();
    for (const auto& name : names)
    {
        const auto& col = model.model->column(column(model, name));
        fields.push_back(J{{"ordinal", fields.size()},
                           {"name", name},
                           {"codec", col.codec.id},
                           {"codec_config", col.codec.configuration ? J(*col.codec.configuration) : J(nullptr)},
                           {"nullable", col.codec.nullable}});
    }
    J json{{"version", 1}, {"fields", fields}};
    return {model.source, 1, std::make_shared<Projection>(Projection::parse(bytes(json.dump()), model.source.profile)),
            model.codecs};
}
std::vector<ControlField> mutation_fields(const ModelBinding& model, std::uint32_t kind)
{
    auto insert = model_values(model, {"Tenant", "Id", "Name", "Version"}, {u64(7), u64(1), bytes("insert"), u64(4)});
    auto update = model_values(model, {"Name", "Version"}, {{}, u64(9)});
    auto key = model_values(model, {"Tenant", "Id"}, {u64(7), u64(1)});
    auto checked = model_values(model, {"Version"}, {u64(4)});
    std::vector<ControlField> fields{num(1, model.model->table_id())};
    switch (kind)
    {
        case 0:
            fields.push_back({2, 5, insert});
            break;
        case 1:
            fields.insert(fields.end(), {{2, 5, insert}, num(7, 1)});
            break;
        case 2:
            fields.insert(fields.end(), {{2, 5, update}, {3, 5, key}});
            break;
        case 3:
            fields.insert(fields.end(), {{2, 5, update}, {3, 5, key}, {4, 5, checked}});
            break;
        case 4:
            fields.push_back({3, 5, key});
            break;
        case 5:
            fields.insert(fields.end(), {{3, 5, key}, {4, 5, checked}});
            break;
        case 6:
        {
            Bytes ids;
            for (const auto* col : model.model->keys())
            {
                auto part = u32(col->id);
                ids.insert(ids.end(), part.begin(), part.end());
            }
            fields.insert(fields.end(), {{2, 5, insert}, {5, 7, ids}, {8, 5, update}});
            break;
        }
        default:
            throw std::runtime_error("test kind");
    }
    return fields;
}
void mutations_and_relations()
{
    for (auto profile : {Profile::Sqlite, Profile::PostgreSql})
    {
        auto model = mutation_model(profile);
        for (std::uint32_t kind = 0; kind <= 6; ++kind)
        {
            auto fields = mutation_fields(model, kind);
            std::optional<ProjectionBinding> projection;
            if (kind == 1)
                projection = model_projection(model, {"Id"});
            auto result = compile_mutation(model, kind, encode_control(4, fields), projection, {}, 1000);
            check(result.checked == (kind == 3 || kind == 5), "checked mutation flag");
            check(result.key_bound == (kind >= 2 && kind <= 5), "key-bound mutation flag");
            check(result.input_rows == 1, "one-row mutation input");
            result.validate_affected(1);
            if (result.key_bound)
            {
                try
                {
                    result.validate_affected(2);
                    throw std::runtime_error("multirow declared key accepted");
                }
                catch (const Error& error)
                {
                    check(error.status == NW_DOMAIN_ERROR && error.code == 2, "key constraint failure taxonomy");
                }
            }
            if (result.checked)
            {
                try
                {
                    result.validate_affected(0);
                    throw std::runtime_error("zero-row CAS accepted");
                }
                catch (const Error& error)
                {
                    check(error.status == NW_DOMAIN_ERROR && error.code == 16, "concurrency failure taxonomy");
                }
            }
            for (std::size_t removed = 0; removed < fields.size(); ++removed)
            {
                auto invalid = fields;
                invalid.erase(invalid.begin() + static_cast<std::ptrdiff_t>(removed));
                rejects(NW_INVALID_ARGUMENT,
                        [&] { (void)compile_mutation(model, kind, encode_control(4, invalid), projection, {}, 1); });
            }
            for (std::uint16_t extra = 1; extra <= 8; ++extra)
            {
                if (std::any_of(fields.begin(), fields.end(), [&](const auto& f) { return f.id == extra; }))
                    continue;
                auto invalid = fields;
                invalid.push_back(num(extra, 1));
                std::sort(invalid.begin(), invalid.end(), [](const auto& a, const auto& b) { return a.id < b.id; });
                rejects(NW_INVALID_ARGUMENT,
                        [&] { (void)compile_mutation(model, kind, encode_control(4, invalid), projection, {}, 1); });
            }
            auto batch = Batch::parse(parameter_wire(result.command), result.command.parameter_expectation);
            if (kind == 6)
            {
                check(result.command.sql.find("DO UPDATE SET") != std::string::npos &&
                          result.command.sql.find("excluded") == std::string::npos,
                      "upsert independent update expressions");
                check(hex(*batch.cell(0, 2)) == hex(bytes("insert")) && !batch.cell(0, 4) &&
                          read_u64(*batch.cell(0, 5), 0) == 9,
                      "upsert input/update parameter order");
            }
            if (kind == 3)
            {
                check(result.command.sql.find("\"Tenant\" =") != std::string::npos &&
                          result.command.sql.find("\"Id\" =") != std::string::npos &&
                          result.command.sql.find("\"Version\" =") != std::string::npos,
                      "composite key and concurrency predicates");
                check(read_u64(*batch.cell(0, 1), 0) == 9 && read_u64(*batch.cell(0, 2), 0) == 7 &&
                          read_u64(*batch.cell(0, 4), 0) == 4,
                      "caller version retained");
                auto reordered = fields;
                reordered[2].bytes = model_values(model, {"Id", "Tenant"}, {u64(1), u64(7)});
                rejects(NW_INVALID_ARGUMENT,
                        [&] { (void)compile_mutation(model, kind, encode_control(4, reordered), {}, {}, 1); });
                auto limited = QueryLimits{};
                limited.parameters = 2;
                rejects(NW_DOMAIN_ERROR,
                        [&] { (void)compile_mutation(model, kind, encode_control(4, fields), {}, limited, 1); });
                auto generated = fields;
                generated[1].bytes = model_values(model, {"Id"}, {u64(1)});
                rejects(NW_INVALID_ARGUMENT,
                        [&] { (void)compile_mutation(model, kind, encode_control(4, generated), {}, {}, 1); });
            }
        }
        auto bulk = mutation_fields(model, 0);
        bulk[1].bytes = model_values(model, {"Tenant", "Id", "Name", "Version"},
                                     {u64(7), u64(1), bytes("a"), u64(4), u64(7), u64(2), {}, u64(5)}, 2);
        auto result = compile_mutation(model, 0, encode_control(4, bulk), {}, {}, 1);
        check(result.input_rows == 2 && result.command.parameter_expectation.descriptors.size() == 8,
              "bulk row-major cells");
        auto named = mutation_fields(model, 6);
        named[2] = {6, 4, bytes("b14_items_pkey")};
        if (profile == Profile::Sqlite)
            rejects(NW_NOT_SUPPORTED, [&] { (void)compile_mutation(model, 6, encode_control(4, named), {}, {}, 1); });
        else
        {
            result = compile_mutation(model, 6, encode_control(4, named), {}, {}, 1);
            check(result.command.sql.find("ON CONSTRAINT \"b14_items_pkey\"") != std::string::npos,
                  "PostgreSQL named conflict constraint");
            named[2].bytes = bytes("a\"b");
            result = compile_mutation(model, 6, encode_control(4, named), {}, {}, 1);
            check(result.command.sql.find("ON CONSTRAINT \"a\"b\"") != std::string::npos,
                  "inherited SeaQuery quoted-constraint defect is explicit");
        }
        auto read = [&](std::string_view name) -> Bytes
        {
            std::ifstream input(repository / "tests/Query.Binding/golden" / name, std::ios::binary);
            if (!input)
                throw std::runtime_error("missing relation fixture");
            return {std::istreambuf_iterator<char>(input), {}};
        };
        SourceIdentity source{9, 1, 1, profile};
        auto codecs = std::make_shared<CodecBindings>(profile);
        ModelBinding parent{source, std::make_shared<Model>(Model::parse(read("parent-model.json"), profile)), codecs};
        ModelBinding child{source, std::make_shared<Model>(Model::parse(read("child-model.json"), profile)), codecs};
        auto definition = read("relation.bin");
        auto relation = RelationBinding::create(source, parent, child, definition, {true, true, true, true, true});
        auto key = model_values(parent, {"Id"}, {u64(7)});
        auto projection = model_projection(child, {"Id", "ParentId"});
        auto command = compile_relation(*relation, source, key, projection, {}, 1000);
        check(command.sql == "SELECT \"Id\", \"ParentId\" FROM \"Children\" WHERE \"ParentId\" = " +
                                 std::string(profile == Profile::Sqlite ? "?" : "$1"),
              "relation ordinary child SELECT");
        auto batch = Batch::parse(parameter_wire(command), command.parameter_expectation);
        check(read_u64(*batch.cell(0, 0), 0) == 7, "relation copied parent key");
        auto other = source;
        ++other.source;
        rejects(NW_INVALID_STATE, [&] { (void)compile_relation(*relation, other, key, projection, {}, 1000); });
        rejects(NW_INVALID_ARGUMENT,
                [&] { (void)compile_relation(*relation, source, key, model_projection(child, {"Id"}), {}, 1000); });
        rejects(NW_INVALID_ARGUMENT, [&] { (void)compile_relation(*relation, source, key, projection, {}, 0); });
        rejects(NW_NOT_SUPPORTED, [&]
                { (void)RelationBinding::create(source, parent, child, definition, {true, true, true, false, true}); });
    }
}
} // namespace

int main(int argc, char** argv)
{
    try
    {
        repository = argc > 1 ? argv[1] : std::filesystem::current_path();
        assert_oracle(Profile::Sqlite);
        assert_oracle(Profile::PostgreSql);
        binding_and_safety();
        plans_and_cache();
        concurrent_shape_cache();
        complete_query_commands();
        sql_lexer();
        mutations_and_relations();
        std::cout << "query: " << checks << " checks passed; 52 SQL/ordered-argument goldens\n";
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << "query test failure: " << error.what() << '\n';
        return 1;
    }
}
