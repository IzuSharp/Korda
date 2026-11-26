// SPDX-License-Identifier: MIT
// Migrated from the fixed Korda B14 implementation. See query.cpp and
// THIRD_PARTY_NOTICES.md for the SeaQuery 1.0.2 rendering provenance.
#include "nw/mutations.hpp"

#include <algorithm>
#include <limits>
#include <set>

namespace nw
{
namespace
{
[[noreturn]] void invalid(std::string message)
{
    throw Error(NW_INVALID_ARGUMENT, 11, std::move(message));
}
[[noreturn]] void capacity(std::string message)
{
    throw Error(NW_DOMAIN_ERROR, 6, std::move(message));
}
[[noreturn]] void unsupported(std::string message)
{
    throw Error(NW_NOT_SUPPORTED, 5, std::move(message));
}
std::string quote(std::string_view name)
{
    std::string result = "\"";
    for (char ch : name)
    {
        if (ch == '"')
            result += '"';
        result += ch;
    }
    return result + '"';
}
std::string table(const Model& model)
{
    return (model.schema() ? quote(*model.schema()) + '.' : "") + quote(model.table());
}
void check_limits(const QueryLimits& limits, std::size_t bytes, std::size_t parameters, std::size_t nodes)
{
    limits.validate();
    if (bytes > limits.bytes || parameters > limits.parameters || nodes > limits.nodes)
        capacity("mutation/relation bounded demand");
    if (limits.depth < 2)
        capacity("mutation/relation depth");
}
enum class Role
{
    Insert,
    Assignments,
    Key,
    Checks
};
struct RowValues
{
    std::vector<const Column*> columns;
    std::vector<Descriptor> descriptors;
    std::vector<std::vector<std::optional<Bytes>>> rows;
};
RowValues row_values(const ModelBinding& model, ByteView bytes, Role role, bool multiple, const QueryLimits& limits)
{
    if (bytes.size() < 96 || bytes.size() > limits.bytes)
        capacity("mutation value size");
    auto count = read_u32(bytes, 60);
    if (count > limits.parameters || count > limits.nodes || count > (bytes.size() - 96) / 16)
        capacity("mutation descriptors");
    RowValues result;
    std::set<std::uint32_t> seen;
    std::vector<std::uint32_t> actual;
    for (std::uint32_t index = 0; index < count; ++index)
    {
        auto id = read_u32(bytes, 96 + index * 16);
        const auto& column = model.model->column(id);
        if (!seen.insert(id).second)
            invalid("duplicate mutation column");
        switch (role)
        {
            case Role::Insert:
                if (column.generated)
                    invalid("generated insert column");
                break;
            case Role::Assignments:
                if (column.generated || column.key_ordinal)
                    invalid("key/generated assignment");
                break;
            case Role::Key:
                if (column.key_ordinal != index || column.codec.nullable)
                    invalid("complete ordered non-null key required");
                break;
            case Role::Checks:
                if (!column.concurrency)
                    invalid("undeclared concurrency column");
                break;
        }
        result.columns.push_back(&column);
        actual.push_back(id);
    }
    std::vector<std::uint32_t> required;
    if (role == Role::Key)
    {
        for (const auto* column : model.model->keys())
            required.push_back(column->id);
    }
    else
    {
        for (auto id : model.model->projection())
        {
            const auto& column = model.model->column(id);
            if ((role == Role::Insert && !column.generated) || (role == Role::Checks && column.concurrency) ||
                (role == Role::Assignments && seen.contains(id)))
                required.push_back(id);
        }
    }
    if (actual != required || (role != Role::Insert && actual.empty()))
        invalid("mutation role shape/order");
    for (const auto* column : result.columns)
        result.descriptors.push_back({column->id, column->codec, model.codecs->bound(column->codec)});
    Expectation expected{4,
                         model.source.profile,
                         model.model->hash(),
                         model.model->table_id(),
                         0,
                         result.descriptors,
                         multiple ? std::nullopt : std::optional<std::uint32_t>(1)};
    auto batch = Batch::parse(bytes, expected);
    const std::size_t cells = static_cast<std::size_t>(batch.rows()) * count;
    check_limits(limits, bytes.size(), cells, cells * 2 + 1);
    for (std::uint32_t row = 0; row < batch.rows(); ++row)
    {
        std::vector<std::optional<Bytes>> values;
        for (std::size_t col = 0; col < count; ++col)
        {
            auto value = batch.cell(row, col);
            if (value && result.columns[col]->max_length && value->size() > *result.columns[col]->max_length)
                invalid("column byte capacity");
            values.push_back(value ? std::optional<Bytes>(Bytes(value->begin(), value->end())) : std::nullopt);
        }
        result.rows.push_back(std::move(values));
    }
    return result;
}
struct Parameters
{
    Profile profile;
    std::vector<Descriptor> descriptors;
    std::vector<std::optional<Bytes>> cells;
    std::string value(const Descriptor& descriptor, const std::optional<Bytes>& bytes)
    {
        auto next = descriptor;
        next.id = static_cast<std::uint32_t>(descriptors.size() + 1);
        descriptors.push_back(next);
        cells.push_back(bytes);
        return profile == Profile::PostgreSql ? "$" + std::to_string(next.id) : "?";
    }
    RenderedQuery bind(const ModelBinding& model, std::string sql, std::optional<ProjectionBinding> projection,
                       std::uint32_t terminal, std::uint64_t timeout_ns, const QueryLimits& limits)
    {
        check_limits(limits, sql.size(), descriptors.size(), descriptors.size() * 2 + 1);
        auto hash = model.model->hash();
        Bytes identity(hash.begin(), hash.end());
        identity.insert(identity.end(), sql.begin(), sql.end());
        for (const auto& descriptor : descriptors)
        {
            const auto at = identity.size();
            identity.resize(at + 9);
            put_u32(identity, at, descriptor.id);
            put_u32(identity, at + 4, descriptor.bound_codec);
            identity[at + 8] = static_cast<std::uint8_t>(descriptor.codec.nullable);
        }
        Expectation expected{1, profile, sha256(identity), 0, 0, std::move(descriptors), 1};
        expected.validate(1);
        std::vector<Scalar> arguments;
        arguments.reserve(cells.size());
        for (std::size_t i = 0; i < cells.size(); ++i)
            arguments.push_back({expected.descriptors[i].codec, std::move(cells[i])});
        const auto charged_bytes = parameter_frame_size(expected, arguments);
        check_limits(limits, charged_bytes, expected.descriptors.size(), expected.descriptors.size() * 2 + 1);
        return {std::move(sql), std::move(arguments), std::move(expected), std::move(projection),
                terminal,       timeout_ns,           charged_bytes};
    }
};
std::string conditions(const RowValues& values, Parameters& parameters)
{
    std::string result;
    for (std::size_t i = 0; i < values.columns.size(); ++i)
    {
        if (i)
            result += " AND ";
        result += quote(values.columns[i]->name);
        result += values.rows.front()[i] ? " = " + parameters.value(values.descriptors[i], values.rows.front()[i])
                                         : " IS NULL";
    }
    return result;
}
std::string assignments(const RowValues& values, Parameters& parameters)
{
    std::string result;
    for (std::size_t i = 0; i < values.columns.size(); ++i)
    {
        if (i)
            result += ", ";
        result +=
            quote(values.columns[i]->name) + " = " + parameters.value(values.descriptors[i], values.rows.front()[i]);
    }
    return result;
}
bool has_control(std::string_view value)
{
    for (std::size_t i = 0; i < value.size(); ++i)
    {
        auto ch = static_cast<unsigned char>(value[i]);
        if (ch < 32 || ch == 127 ||
            (ch == 194 && i + 1 < value.size() && static_cast<unsigned char>(value[i + 1]) >= 128 &&
             static_cast<unsigned char>(value[i + 1]) <= 159))
            return true;
    }
    return false;
}
Mutation compile_mutation_impl(const ModelBinding& model, std::uint32_t kind, ByteView payload,
                               std::optional<ProjectionBinding> projection, QueryLimits limits,
                               std::uint64_t timeout_ns)
{
    check_limits(limits, payload.size(), 0, 1);
    std::vector<FieldSpec> schema{{1, 1, true}};
    switch (kind)
    {
        case 0:
            schema.push_back({2, 5, true});
            break;
        case 1:
            schema.insert(schema.end(), {{2, 5, true}, {7, 1, true}});
            break;
        case 2:
            schema.insert(schema.end(), {{2, 5, true}, {3, 5, true}});
            break;
        case 3:
            schema.insert(schema.end(), {{2, 5, true}, {3, 5, true}, {4, 5, true}});
            break;
        case 4:
            schema.push_back({3, 5, true});
            break;
        case 5:
            schema.insert(schema.end(), {{3, 5, true}, {4, 5, true}});
            break;
        case 6:
        {
            if (payload.size() < 16)
                invalid("mutation envelope");
            const auto count = read_u32(payload, 8);
            if (count != 4 || payload.size() < 16 + count * 16)
                invalid("upsert field inventory");
            auto target = read_u16(payload, 48);
            if (target != 5 && target != 6)
                invalid("upsert conflict target");
            schema.insert(
                schema.end(),
                {{2, 5, true}, {target, static_cast<std::uint16_t>(target == 5 ? 7 : 4), true}, {8, 5, true}});
            break;
        }
        default:
            invalid("mutation kind");
    }
    auto fields = Fields::parse(payload, 4, schema);
    if (fields.u32(1) != model.model->table_id())
        invalid("mutation table identity");
    if ((kind == 1) != projection.has_value())
        invalid("mutation projection inventory");
    Parameters parameters{model.source.profile, {}, {}};
    std::size_t input_rows = 1;
    std::string sql;
    if (kind == 0 || kind == 1 || kind == 6)
    {
        auto insert = row_values(model, fields.bytes(2), Role::Insert, kind == 0, limits);
        input_rows = insert.rows.size();
        sql = "INSERT INTO " + table(*model.model);
        if (insert.columns.empty())
        {
            if (input_rows != 1)
                unsupported("multirow generated-only insert");
            sql += " DEFAULT VALUES";
        }
        else
        {
            sql += " (";
            for (std::size_t i = 0; i < insert.columns.size(); ++i)
            {
                if (i)
                    sql += ", ";
                sql += quote(insert.columns[i]->name);
            }
            sql += ") VALUES ";
            for (std::size_t row = 0; row < input_rows; ++row)
            {
                if (row)
                    sql += ", ";
                sql += '(';
                for (std::size_t col = 0; col < insert.columns.size(); ++col)
                {
                    if (col)
                        sql += ", ";
                    sql += parameters.value(insert.descriptors[col], insert.rows[row][col]);
                }
                sql += ')';
            }
        }
        if (kind == 6)
        {
            auto update = row_values(model, fields.bytes(8), Role::Assignments, false, limits);
            sql += " ON CONFLICT ";
            if (schema[2].id == 5)
            {
                auto target = fields.bytes(5);
                if (target.empty() || target.size() % 4 != 0 || target.size() / 4 > limits.parameters)
                    invalid("conflict columns");
                std::set<std::uint32_t> ids;
                sql += '(';
                for (std::size_t i = 0; i < target.size(); i += 4)
                {
                    auto id = read_u32(target, i);
                    if (!ids.insert(id).second)
                        invalid("duplicate conflict column");
                    if (i)
                        sql += ", ";
                    sql += quote(model.model->column(id).name);
                }
                sql += ')';
            }
            else
            {
                if (model.source.profile != Profile::PostgreSql)
                    unsupported("named constraint conflict target");
                auto name = fields.text(6);
                if (name.empty() || name.size() > 256 || has_control(name))
                    invalid("constraint name");
                // Inherited SeaQuery 1.0.2 behavior: unlike ordinary identifiers,
                // named conflict constraints do not escape embedded quotes.
                // DB-CPP-01 records this existing defect without changing the contract.
                sql += "ON CONSTRAINT \"" + std::string(name) + '"';
            }
            sql += " DO UPDATE SET " + assignments(update, parameters);
        }
        if (kind == 1)
        {
            const auto& returned = *projection;
            if (fields.u32(7) != returned.id || returned.source != model.source)
                invalid("returning source/projection");
            sql += " RETURNING ";
            for (std::size_t i = 0; i < returned.projection->fields().size(); ++i)
            {
                const auto& field = returned.projection->fields()[i];
                auto found = std::find_if(model.model->columns().begin(), model.model->columns().end(),
                                          [&](const auto& c) { return c.name == field.name; });
                if (found == model.model->columns().end())
                    invalid("RETURNING column");
                if (found->codec != field.codec)
                    invalid("RETURNING codec/nullability");
                if (i)
                    sql += ", ";
                sql += quote(found->name);
            }
        }
    }
    else if (kind == 2 || kind == 3)
    {
        auto update = row_values(model, fields.bytes(2), Role::Assignments, false, limits);
        auto key = row_values(model, fields.bytes(3), Role::Key, false, limits);
        sql = "UPDATE " + table(*model.model) + " SET " + assignments(update, parameters);
        sql += " WHERE " + conditions(key, parameters);
        if (kind == 3)
            sql += " AND " + conditions(row_values(model, fields.bytes(4), Role::Checks, false, limits), parameters);
    }
    else
    {
        auto key = row_values(model, fields.bytes(3), Role::Key, false, limits);
        sql = "DELETE FROM " + table(*model.model) + " WHERE " + conditions(key, parameters);
        if (kind == 5)
            sql += " AND " + conditions(row_values(model, fields.bytes(4), Role::Checks, false, limits), parameters);
    }
    return {parameters.bind(model, std::move(sql), std::move(projection), kind == 1 ? 0 : 6, timeout_ns, limits),
            kind == 3 || kind == 5, kind >= 2 && kind <= 5, input_rows};
}
void remap_binding_error(Error& error)
{
    error.code = error.status == NW_DOMAIN_ERROR ? 6 : error.status == NW_NOT_SUPPORTED ? 5 : 11;
}
} // namespace

Mutation compile_mutation(const ModelBinding& model, std::uint32_t kind, ByteView payload,
                          std::optional<ProjectionBinding> projection, QueryLimits limits, std::uint64_t timeout_ns)
{
    try
    {
        return compile_mutation_impl(model, kind, payload, std::move(projection), limits, timeout_ns);
    }
    catch (Error& error)
    {
        remap_binding_error(error);
        throw;
    }
}
void Mutation::validate_affected(std::int64_t rows) const
{
    if (key_bound && rows > 1)
        throw Error(NW_DOMAIN_ERROR, 2, "declared key matched multiple rows");
    if (checked && rows == 0)
        throw Error(NW_DOMAIN_ERROR, 16, "optimistic concurrency conflict");
}
void RelationCapabilities::validate() const
{
    if (!metadata || !scalar_codecs || !query || !streaming || !explicit_relationships)
        unsupported("relation metadata/codecs/query/streaming/explicit-relationships capabilities");
}
std::shared_ptr<RelationBinding> RelationBinding::create(SourceIdentity source, ModelBinding parent, ModelBinding child,
                                                         ByteView definition, RelationCapabilities capabilities)
{
    try
    {
        capabilities.validate();
        parent.ensure_source(source);
        child.ensure_source(source);
        auto relation = std::make_shared<Relation>(Relation::parse(definition, parent.model, child.model));
        std::vector<std::uint32_t> key_codecs;
        for (std::size_t i = 0; i < relation->parent_columns().size(); ++i)
        {
            const auto& p = parent.model->column(relation->parent_columns()[i]);
            const auto& c = child.model->column(relation->child_columns()[i]);
            const auto bound = parent.codecs->bound(p.codec);
            if (bound != child.codecs->bound(c.codec))
                invalid("relation bound codec identity");
            key_codecs.push_back(bound);
        }
        return std::make_shared<RelationBinding>(RelationBinding{
            source, std::move(relation), std::move(parent), std::move(child), std::move(key_codecs), capabilities});
    }
    catch (Error& error)
    {
        remap_binding_error(error);
        throw;
    }
}
RenderedQuery compile_relation(const RelationBinding& relation, SourceIdentity session_source, ByteView key,
                               ProjectionBinding projection, QueryLimits limits, std::uint64_t timeout_ns)
{
    try
    {
        relation.capabilities.validate();
        limits.validate();
        if (session_source != relation.source || projection.source != relation.source)
            throw Error(NW_INVALID_STATE, 11, "relation load source generation");
        if (timeout_ns == 0 || key.size() > limits.bytes || relation.key_codecs.size() > limits.parameters ||
            relation.key_codecs.size() * 3 > limits.nodes || limits.depth < 2)
            invalid("relation load limits/deadline");
        const auto& columns = relation.child.model->projection();
        const auto& fields = projection.projection->fields();
        if (columns.size() != fields.size())
            invalid("child Table.Row projection mismatch");
        for (std::size_t i = 0; i < columns.size(); ++i)
        {
            const auto& column = relation.child.model->column(columns[i]);
            if (fields[i].name != column.name || fields[i].codec != column.codec)
                invalid("child Table.Row projection mismatch");
        }
        (void)Batch::parse(key,
                           relation.relation->parent_key_expectation(relation.source.profile, relation.key_codecs));
        auto values = row_values(relation.parent, key, Role::Key, false, limits);
        if (relation.relation->child_columns().size() != values.columns.size())
            invalid("relation arity");
        Parameters parameters{relation.source.profile, {}, {}};
        std::string sql = "SELECT ";
        for (std::size_t i = 0; i < columns.size(); ++i)
        {
            if (i)
                sql += ", ";
            sql += quote(relation.child.model->column(columns[i]).name);
        }
        sql += " FROM " + table(*relation.child.model) + " WHERE ";
        for (std::size_t i = 0; i < values.columns.size(); ++i)
        {
            if (i)
                sql += " AND ";
            const auto& column = relation.child.model->column(relation.relation->child_columns()[i]);
            sql += quote(column.name) + " = " + parameters.value(values.descriptors[i], values.rows.front()[i]);
        }
        return parameters.bind(relation.child, std::move(sql), std::move(projection), 0, timeout_ns, limits);
    }
    catch (Error& error)
    {
        remap_binding_error(error);
        throw;
    }
}

} // namespace nw
