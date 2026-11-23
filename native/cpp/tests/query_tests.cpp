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
#include <iostream>
#include <limits>
#include <stdexcept>

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
    return Capabilities::from_mask((1ULL << 25) - 1, profile);
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
void select(NativeQuery& query, std::initializer_list<Selection> values)
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
        records.push_back(encode_control(
            22, {num(1, i), {2, 4, bytes(value.name)}, num(3, value.codec.id), num(4, value.codec.nullable)}));
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
    query.clause(6, record({{1, 8, tokens}, {2, 5, projection}}));
}
void where(NativeQuery& query, std::uint64_t expression)
{
    query.clause(1, record({token(1, expression)}));
}
void order(NativeQuery& query, std::uint64_t expression)
{
    query.clause(3, record({token(1, expression), num(2, 0), num(3, 1), num(4, 1)}));
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
        auto batch = Batch::parse(rendered.parameter_bytes, rendered.parameter_expectation);
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
        auto b = Batch::parse(rendered.parameter_bytes, rendered.parameter_expectation);
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
        check(a.sql == second.sql && a.parameter_bytes != second.parameter_bytes,
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
        check(a.sql == second.sql && a.parameter_bytes != second.parameter_bytes, "cache never captures values");
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
            auto batch = Batch::parse(result.command.parameter_bytes, result.command.parameter_expectation);
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
        auto batch = Batch::parse(command.parameter_bytes, command.parameter_expectation);
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
