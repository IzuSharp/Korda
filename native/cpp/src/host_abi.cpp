// SPDX-License-Identifier: MIT
#include "nw/host.hpp"

// CMake derives this private C++ view from the byte-frozen C header, changing
// only its `template` parameter name. Every definition is type-checked against
// the normative declarations; applications still consume the original C ABI.
#include "nw_database_declarations.hpp"

#include <algorithm>
#include <unordered_map>

#ifdef _WIN32
#define NW_DATABASE_API extern "C" __declspec(dllexport)
#else
#define NW_DATABASE_API extern "C" __attribute__((visibility("default")))
#endif

namespace nw
{
namespace
{
using StoredValue = std::variant<std::shared_ptr<Owner>, std::shared_ptr<DbOperation>, std::shared_ptr<Buffer>>;
struct Stored
{
    StoredValue value;
};
std::mutex handles_mutex;
std::unordered_map<std::uintptr_t, std::unique_ptr<Stored>> handles;
template <class T> T get(void* raw)
{
    std::lock_guard lock(handles_mutex);
    const auto found = handles.find(reinterpret_cast<std::uintptr_t>(raw));
    if (found == handles.end() || !std::holds_alternative<T>(found->second->value))
        throw Error::state("invalid or retired database owner");
    return std::get<T>(found->second->value);
}
template <class T> void* publish(T value)
{
    auto allocation = std::make_unique<Stored>(Stored{std::move(value)});
    void* address = allocation.get();
    std::lock_guard lock(handles_mutex);
    handles.emplace(reinterpret_cast<std::uintptr_t>(address), std::move(allocation));
    return address;
}
template <class T> void release(void* raw)
{
    std::unique_ptr<Stored> allocation;
    {
        std::lock_guard lock(handles_mutex);
        const auto found = handles.find(reinterpret_cast<std::uintptr_t>(raw));
        if (found != handles.end() && std::holds_alternative<T>(found->second->value))
        {
            allocation = std::move(found->second);
            handles.erase(found);
        }
    }
}
template <class F> nw_status boundary(nw_error_info_v1* output, F&& action) noexcept
{
    try
    {
        action();
        return NW_OK;
    }
    catch (const Error& error)
    {
        return interop::write_error(error.status, output, error.fields());
    }
    catch (const std::bad_alloc&)
    {
        return interop::write_error(NW_OUT_OF_MEMORY, output, {0x4442, 8, 0});
    }
    catch (...)
    {
        return interop::write_error(NW_NATIVE_PANIC, output, {0x4442, 8, 0});
    }
}
template <class T> void clear(T* pointer)
{
    if (!pointer || reinterpret_cast<std::uintptr_t>(pointer) % alignof(T))
        throw Error::invalid("invalid output slot");
    *pointer = {};
}
ByteView borrow(nw_bytes_view_v1 value)
{
    if (value.length > MAX_BYTES)
        throw Error::capacity();
    if (value.length && !value.data)
        throw Error::invalid("null byte view");
    return {value.data, static_cast<std::size_t>(value.length)};
}
std::shared_ptr<Owner> owner(nw_db_ref raw)
{
    return get<std::shared_ptr<Owner>>(raw);
}
std::shared_ptr<Source> source_owner(nw_db_ref raw)
{
    auto value = owner(raw);
    if (!std::holds_alternative<std::monostate>(value->data))
        throw Error::state("source required");
    return value->source;
}
template <class T> T typed(nw_db_ref raw)
{
    auto value = owner(raw);
    if (auto pointer = std::get_if<T>(&value->data))
        return *pointer;
    throw Error::state("database owner kind differs");
}
void output_owner(nw_db_ref* output, std::shared_ptr<Source> source, OwnerData data = {})
{
    *output = reinterpret_cast<nw_db_ref>(publish(std::make_shared<Owner>(std::move(source), std::move(data))));
}
void output_operation(nw_db_ref* output, std::shared_ptr<DbOperation> operation)
{
    *output = reinterpret_cast<nw_db_ref>(publish(std::move(operation)));
}
void output_buffer(nw_db_buffer** output, std::shared_ptr<Source> source, Bytes bytes)
{
    *output = reinterpret_cast<nw_db_buffer*>(publish(std::make_shared<Buffer>(std::move(source), std::move(bytes))));
}
Value take(std::shared_ptr<DbOperation> operation, std::uint32_t kind)
{
    if (operation->result_kind != kind)
        throw Error::state("wrong operation result kind");
    auto [status, value] = operation->core->take_result();
    if (status != NW_OK || !value)
        throw Error(status, 8, "operation result unavailable");
    return std::move(*value);
}
template <class F> auto with_query(nw_db_ref raw, F&& action)
{
    auto value = owner(raw);
    auto query = std::get_if<std::shared_ptr<QueryOwner>>(&value->data);
    if (!query)
        throw Error::state("query required");
    value->source->admit();
    std::unique_lock lock((*query)->mutex, std::try_to_lock);
    if (!lock.owns_lock())
        throw Error(NW_DOMAIN_ERROR, 7, "concurrent native query call");
    try
    {
        return action(value->source, *(*query)->query);
    }
    catch (const Error& error)
    {
        throw error;
    }
    catch (...)
    {
        value->source->poisoned.store(true);
        (*query)->query->poison();
        throw Error(NW_NATIVE_PANIC, 8, "native query exception contained");
    }
}
std::shared_ptr<Buffer> snapshot_buffer(const std::shared_ptr<Owner>& value)
{
    Bytes bytes;
    if (std::holds_alternative<std::monostate>(value->data))
        bytes = value->source->snapshot();
    else if (auto session = std::get_if<std::shared_ptr<Session>>(&value->data))
        bytes = (*session)->snapshot();
    else if (auto cursor = std::get_if<std::shared_ptr<Cursor>>(&value->data))
        bytes = (*cursor)->snapshot();
    else if (auto transaction = std::get_if<std::shared_ptr<Transaction>>(&value->data))
        bytes = (*transaction)->snapshot();
    else
        throw Error::state("owner has no snapshot");
    return std::make_shared<Buffer>(value->source, std::move(bytes));
}
} // namespace
std::uint64_t module_identity()
{
    return reinterpret_cast<std::uintptr_t>(&handles);
}
} // namespace nw

using namespace nw;

NW_DATABASE_API nw_status NW_CDECL nw_database_get_abi_v1(std::uint32_t major, std::uint32_t capacity,
                                                          nw_abi_info_v1* output)
{
    return interop::write_abi(major, capacity, output, 0, capability_mask());
}
#if NW_ENABLE_SQLITE
NW_DATABASE_API nw_status NW_CDECL nw_database_sqlite_get_abi_v1(std::uint32_t major, std::uint32_t capacity,
                                                                 nw_abi_info_v1* output)
{
    return nw_database_get_abi_v1(major, capacity, output);
}
#endif
#if NW_ENABLE_POSTGRES
NW_DATABASE_API nw_status NW_CDECL nw_database_postgresql_get_abi_v1(std::uint32_t major, std::uint32_t capacity,
                                                                     nw_abi_info_v1* output)
{
    return nw_database_get_abi_v1(major, capacity, output);
}
#endif

NW_DATABASE_API nw_status NW_CDECL nw_db_source_create_v1(nw_bytes_view_v1 config, nw_db_ref* output,
                                                          nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        output_owner(output, Source::create(HostConfig::parse(borrow(config))));
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_session_open_create_v1(nw_db_ref raw, nw_db_ref* output,
                                                                nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto source = source_owner(raw);
                        auto operation = DbOperation::create(
                            source, 1, false, [source](auto cancel) -> Value
                            { return std::make_shared<Owner>(source, Session::open_reserved(source, cancel)); },
                            DbOperation::Dispatch::Admission);
                        output_operation(output, std::move(operation));
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_operation_start_v1(nw_db_ref raw, const nw_operation_callbacks_v1* callbacks,
                                                            nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        if (!callbacks ||
                            reinterpret_cast<std::uintptr_t>(callbacks) % alignof(nw_operation_callbacks_v1))
                            throw Error::invalid("callback record");
                        auto operation = get<std::shared_ptr<DbOperation>>(raw);
                        const auto status = operation->start(*callbacks);
                        if (status != NW_OK)
                            throw Error(status, 8, "operation start rejected");
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_operation_request_cancel_v1(nw_db_ref raw)
{
    return boundary(nullptr,
                    [&]
                    {
                        auto operation = get<std::shared_ptr<DbOperation>>(raw);
                        operation->cancel->request();
                        (void)operation->core->request_cancel();
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_operation_try_retire_v1(nw_db_ref raw)
{
    try
    {
        return get<std::shared_ptr<DbOperation>>(raw)->core->try_retire();
    }
    catch (const Error& error)
    {
        return error.status;
    }
    catch (...)
    {
        return NW_NATIVE_PANIC;
    }
}
NW_DATABASE_API void NW_CDECL nw_db_operation_release_v1(nw_db_ref raw)
{
    try
    {
        auto operation = get<std::shared_ptr<DbOperation>>(raw);
        if (operation->core->can_release())
            release<std::shared_ptr<DbOperation>>(raw);
        else
            operation->source->poisoned.store(true);
    }
    catch (...)
    {
    }
}
NW_DATABASE_API nw_status NW_CDECL nw_db_operation_take_owner_v1(nw_db_ref raw, std::uint32_t kind, nw_db_ref* output,
                                                                 nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        if (kind < 1 || kind > 4)
                            throw Error::state("wrong result owner kind");
                        auto value = take(get<std::shared_ptr<DbOperation>>(raw), kind);
                        if (!std::holds_alternative<std::shared_ptr<Owner>>(value))
                            throw Error::state("owner result required");
                        *output =
                            reinterpret_cast<nw_db_ref>(publish(std::get<std::shared_ptr<Owner>>(std::move(value))));
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_operation_take_count_v1(nw_db_ref raw, std::int64_t* output,
                                                                 nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto value = take(get<std::shared_ptr<DbOperation>>(raw), 100);
                        if (!std::holds_alternative<std::int64_t>(value))
                            throw Error::state("count result required");
                        *output = std::get<std::int64_t>(value);
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_operation_take_buffer_v1(nw_db_ref raw, nw_db_buffer** output,
                                                                  nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto value = take(get<std::shared_ptr<DbOperation>>(raw), 101);
                        if (!std::holds_alternative<std::shared_ptr<Buffer>>(value))
                            throw Error::state("buffer result required");
                        auto buffer = std::get<std::shared_ptr<Buffer>>(std::move(value));
                        buffer->mark_taken();
                        *output = reinterpret_cast<nw_db_buffer*>(publish(std::move(buffer)));
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_operation_take_error_v1(nw_db_ref raw, nw_db_buffer** output,
                                                                 nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto operation = get<std::shared_ptr<DbOperation>>(raw);
                        std::optional<Error> failure;
                        {
                            std::lock_guard lock(operation->mutex);
                            failure = operation->error;
                        }
                        if (!failure)
                            throw Error::state("no rich error");
                        if (operation->error_taken.exchange(true))
                            throw Error::state("rich error already taken");
                        auto bytes =
                            snapshot_record(0, 0, false, failure->cleanup ? 4 : 0, failure->cleanup ? 3 : 0,
                                            bool(failure->cleanup), false, failure->correlation, failure->provider);
                        if (failure->bulk)
                        {
                            auto bulk = failure->bulk->encode();
                            bytes.insert(bytes.end(), bulk.begin(), bulk.end());
                        }
                        output_buffer(output, operation->source, std::move(bytes));
                    });
}

NW_DATABASE_API nw_status NW_CDECL nw_db_codec_register_v1(nw_db_ref raw, std::uint32_t logical,
                                                           nw_bytes_view_v1 configuration, std::uint32_t* output,
                                                           nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto source = source_owner(raw);
                        auto bytes = borrow(configuration);
                        if (!valid_utf8(bytes))
                            throw Error::invalid("codec UTF8");
                        std::optional<std::string> value;
                        if (!bytes.empty())
                            value = std::string(reinterpret_cast<const char*>(bytes.data()), bytes.size());
                        *output = source->register_codec(logical, std::move(value));
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_metadata_register_v1(nw_db_ref raw, nw_bytes_view_v1 metadata,
                                                              nw_db_ref* output, nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto source = source_owner(raw);
                        output_owner(output, source, source->register_model(borrow(metadata)));
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_projection_register_v1(nw_db_ref raw, nw_bytes_view_v1 projection,
                                                                std::uint32_t* output, nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        *output = source_owner(raw)->register_projection(borrow(projection)).id;
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_relation_register_v1(nw_db_ref raw, nw_db_ref parent, nw_db_ref child,
                                                              nw_bytes_view_v1 definition, nw_db_ref* output,
                                                              nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto source = source_owner(raw);
                        auto parent_model = typed<ModelBinding>(parent);
                        auto child_model = typed<ModelBinding>(child);
                        if (parent_model.source != source->identity || child_model.source != source->identity)
                            throw Error(NW_INVALID_STATE, 13, "mixed relation source");
                        output_owner(output, source,
                                     source->register_relation(std::move(parent_model), std::move(child_model),
                                                               borrow(definition)));
                    });
}

NW_DATABASE_API nw_status NW_CDECL nw_db_query_create_v1(nw_db_ref raw, nw_db_ref model, std::uint32_t table,
                                                         nw_db_ref* output, nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto session = typed<std::shared_ptr<Session>>(raw);
                        auto binding = typed<ModelBinding>(model);
                        session->source->admit();
                        if (session->closing.load() || binding.source != session->source->identity)
                            throw Error::state("closed or mixed query session");
                        auto query = std::make_shared<QueryOwner>();
                        query->query = std::make_shared<NativeQuery>(
                            std::move(binding), session->source->parameters, table,
                            session->source->config.limits.binding(),
                            Capabilities::from_mask(capability_mask(), session->source->config.provider.profile),
                            session->source->config.limits.command_ns);
                        output_owner(output, session->source, std::move(query));
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_expr_column_v1(nw_db_ref raw, nw_db_token alias, std::uint32_t column,
                                                        nw_db_token* output, nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        *output =
                            with_query(raw, [&](const auto&, auto& query) { return query.column(alias, column); });
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_expr_value_v1(nw_db_ref raw, nw_bytes_view_v1 cell, nw_db_token* output,
                                                       nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        *output = with_query(raw, [&](const auto&, auto& query) { return query.value(borrow(cell)); });
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_expr_parameter_v1(nw_db_ref raw, std::uint32_t id, nw_bytes_view_v1 name,
                                                           std::uint32_t codec, std::uint32_t nullable,
                                                           nw_db_token* output, nw_error_info_v1* error)
{
    return boundary(
        error,
        [&]
        {
            clear(output);
            if (nullable > 1)
                throw Error::invalid("parameter nullability");
            auto bytes = borrow(name);
            if (!valid_utf8(bytes))
                throw Error::invalid("parameter UTF8");
            std::string text(reinterpret_cast<const char*>(bytes.data()), bytes.size());
            *output =
                with_query(raw, [&](const auto& source, auto& query)
                           { return query.parameter({id, text, source->codecs()->get(codec, nullable == 1), codec}); });
        });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_expr_binary_v1(nw_db_ref raw, std::uint32_t code, nw_db_token left,
                                                        nw_db_token right, nw_db_token* output, nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        *output =
                            with_query(raw, [&](const auto&, auto& query) { return query.binary(code, left, right); });
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_expr_unary_v1(nw_db_ref raw, std::uint32_t code, nw_db_token input,
                                                       nw_db_token* output, nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        *output = with_query(raw, [&](const auto&, auto& query) { return query.unary(code, input); });
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_expr_list_v1(nw_db_ref raw, std::uint32_t code, nw_db_token input,
                                                      nw_bytes_view_v1 cells, nw_db_token* output,
                                                      nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        *output = with_query(raw, [&](const auto&, auto& query)
                                             { return query.list(code, input, borrow(cells)); });
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_query_import_v1(nw_db_ref raw, nw_db_ref dependency, std::uint32_t kind,
                                                         nw_db_token* output, nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto value = owner(dependency);
                        *output = with_query(
                            raw,
                            [&](const auto& source, auto& query) -> std::uint64_t
                            {
                                if (value->source->identity != source->identity)
                                    throw Error::state("mixed query dependency");
                                if (kind == 1 && std::holds_alternative<ModelBinding>(value->data))
                                    return query.import_model(std::get<ModelBinding>(value->data));
                                if (kind == 2 && std::holds_alternative<std::shared_ptr<NativePlan>>(value->data))
                                    return query.import_plan(std::get<std::shared_ptr<NativePlan>>(value->data));
                                if (kind == 3 && std::holds_alternative<std::shared_ptr<NativeCte>>(value->data))
                                    return query.import_cte(std::get<std::shared_ptr<NativeCte>>(value->data));
                                throw Error::state("query dependency kind");
                            });
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_query_clause_v1(nw_db_ref raw, std::uint32_t code, nw_bytes_view_v1 operands,
                                                         nw_db_token* output, nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        *output = with_query(raw, [&](const auto&, auto& query)
                                             { return query.clause(code, borrow(operands)); });
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_query_seal_v1(nw_db_ref raw, std::uint32_t terminal, nw_db_ref* output,
                                                       nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        with_query(raw, [&](const auto& source, auto& query)
                                   { output_owner(output, source, query.seal(terminal)); });
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_query_clone_v1(nw_db_ref raw, nw_db_ref* output, nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        with_query(raw,
                                   [&](const auto& source, auto& query)
                                   {
                                       auto clone = std::make_shared<QueryOwner>();
                                       clone->query = query.clone_selected();
                                       output_owner(output, source, std::move(clone));
                                   });
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_query_render_v1(nw_db_ref raw, nw_db_buffer** output, nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        with_query(raw,
                                   [&](const auto& source, auto& query)
                                   {
                                       auto clone = query.clone_selected();
                                       auto sql = clone->seal(8)->render_shape().first;
                                       output_buffer(output, source, Bytes(sql.begin(), sql.end()));
                                   });
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_cte_create_v1(nw_db_ref declared, std::uint32_t table, nw_db_ref body,
                                                       std::uint32_t recursive, nw_db_ref* output,
                                                       nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto model = typed<ModelBinding>(declared);
                        with_query(body,
                                   [&](const auto& source, auto& query)
                                   {
                                       if (model.source != source->identity)
                                           throw Error::state("mixed CTE source");
                                       output_owner(output, source, NativeCte::create(model, table, query, recursive));
                                   });
                    });
}

NW_DATABASE_API nw_status NW_CDECL nw_db_sql_create_v1(nw_db_ref raw, nw_bytes_view_v1 sql, nw_bytes_view_v1 parameters,
                                                       nw_bytes_view_v1 options, nw_db_ref* output,
                                                       nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto session = typed<std::shared_ptr<Session>>(raw);
                        output_owner(output, session->source,
                                     SqlPlan::parse(session->source, borrow(sql), borrow(parameters), borrow(options)));
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_execute_create_v1(nw_db_ref raw, nw_db_ref input, nw_bytes_view_v1 arguments,
                                                           nw_db_ref* output, nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto session = typed<std::shared_ptr<Session>>(raw);
                        auto value = owner(input);
                        auto bytes = borrow(arguments);
                        auto gate = session->admit(false);
                        std::shared_ptr<SqlPlan> plan;
                        if (auto sql = std::get_if<std::shared_ptr<SqlPlan>>(&value->data))
                        {
                            plan = *sql;
                            if (!bytes.empty() && (bytes.size() != plan->parameters.size() ||
                                                   !std::equal(bytes.begin(), bytes.end(), plan->parameters.begin())))
                                throw Error::invalid("raw command arguments changed");
                            if (plan->kind == 2)
                            {
                                std::lock_guard lock(session->mutex);
                                if (auto tx = session->transaction.lock())
                                {
                                    std::lock_guard state(tx->mutex);
                                    if (!tx->retired)
                                        throw Error::state("session mutation in a transaction");
                                }
                            }
                            plan->submit(*session->source);
                        }
                        else if (auto native = std::get_if<std::shared_ptr<NativePlan>>(&value->data))
                        {
                            if (value->source->identity != session->source->identity)
                                throw Error::state("mixed plan source");
                            if ((*native)->requires_transaction())
                            {
                                std::shared_ptr<Transaction> tx;
                                {
                                    std::lock_guard lock(session->mutex);
                                    tx = session->transaction.lock();
                                }
                                if (!tx || !tx->active())
                                    throw Error::state("row locking requires an active explicit transaction");
                            }
                            RenderedQuery rendered;
                            {
                                std::lock_guard lock(session->source->shape_mutex);
                                rendered =
                                    (*native)->render(session->source->identity, bytes, &session->source->shapes);
                            }
                            plan = SqlPlan::rendered(session->source, std::move(rendered));
                        }
                        else
                            throw Error::state("execution plan required");
                        const auto kind = plan->result_kind == 0 || plan->scalar_count ? 100 : 2;
                        output_operation(output, DbOperation::create(session->source, kind, false,
                                                                     [session, plan, gate](auto cancel)
                                                                     { return session->execute(plan, cancel, gate); }));
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_cursor_fetch_create_v1(nw_db_ref raw, std::uint32_t rows, std::uint64_t bytes,
                                                                nw_db_ref* output, nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto cursor = typed<std::shared_ptr<Cursor>>(raw);
                        const auto& limits = cursor->session->source->config.limits;
                        if (!rows || rows > limits.batch_rows || !bytes || bytes > limits.batch_bytes)
                            throw Error::invalid("fetch capacity");
                        output_operation(
                            output, DbOperation::create(
                                        cursor->session->source, 101, false, [cursor, rows, bytes](auto cancel) -> Value
                                        { return cursor->fetch(cancel, rows, static_cast<std::size_t>(bytes)); }));
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_relation_load_create_v1(nw_db_ref raw, nw_db_ref input, nw_bytes_view_v1 key,
                                                                 std::uint32_t projection, nw_db_ref* output,
                                                                 nw_error_info_v1* error)
{
    return boundary(
        error,
        [&]
        {
            clear(output);
            auto session = typed<std::shared_ptr<Session>>(raw);
            auto relation = typed<std::shared_ptr<RelationBinding>>(input);
            auto source = session->source;
            if (relation->source != source->identity)
                throw Error(NW_INVALID_STATE, 13, "mixed relation load source");
            auto rendered = compile_relation(*relation, source->identity, borrow(key), source->projection(projection),
                                             source->config.limits.binding(), source->config.limits.command_ns);
            auto plan = SqlPlan::rendered(source, std::move(rendered));
            plan->relation = relation;
            auto gate = session->admit(false);
            output_operation(output, DbOperation::create(source, 2, false, [session, plan, gate](auto cancel)
                                                         { return session->execute(plan, cancel, gate); }));
        });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_mutation_create_v1(nw_db_ref raw, nw_db_ref input, std::uint32_t kind,
                                                            nw_bytes_view_v1 payload, nw_db_ref* output,
                                                            nw_error_info_v1* error)
{
    return boundary(
        error,
        [&]
        {
            clear(output);
            auto session = typed<std::shared_ptr<Session>>(raw);
            auto model = typed<ModelBinding>(input);
            auto source = session->source;
            if (model.source != source->identity)
                throw Error(NW_INVALID_STATE, 13, "mixed mutation source");
            source->admit();
            auto bytes = borrow(payload);
            std::optional<ProjectionBinding> projection;
            if (kind == 1)
            {
                auto fields = Fields::parse(bytes, 4, {{1, 1, true}, {2, 5, true}, {7, 1, true}});
                projection = source->projection(fields.u32(7));
            }
            auto mutation = compile_mutation(model, kind, bytes, projection, source->config.limits.binding(),
                                             source->config.limits.command_ns);
            if (mutation.input_rows > source->config.limits.batch_rows)
                throw Error::capacity();
            auto plan = SqlPlan::rendered(source, std::move(mutation.command));
            plan->kind = 1;
            auto gate = session->admit(false);
            output_operation(output, DbOperation::create(source, kind == 1 ? 2 : 100, false,
                                                         [session, plan, gate, mutation, kind](auto cancel) -> Value
                                                         {
                                                             try
                                                             {
                                                                 auto value = session->execute(plan, cancel, gate);
                                                                 if (auto count = std::get_if<std::int64_t>(&value))
                                                                     mutation.validate_affected(*count);
                                                                 return value;
                                                             }
                                                             catch (Error& error)
                                                             {
                                                                 if (kind == 0)
                                                                 {
                                                                     std::uint32_t outcome = 0;
                                                                     {
                                                                         std::lock_guard lock(session->mutex);
                                                                         if (auto tx = session->transaction.lock())
                                                                         {
                                                                             std::lock_guard state(tx->mutex);
                                                                             outcome = tx->outcome;
                                                                         }
                                                                     }
                                                                     error.bulk =
                                                                         NativeBulkProgress{0, 0, false, outcome};
                                                                 }
                                                                 throw;
                                                             }
                                                         }));
        });
}

NW_DATABASE_API nw_status NW_CDECL nw_db_tx_begin_create_v1(nw_db_ref raw, nw_bytes_view_v1 bytes, nw_db_ref* output,
                                                            nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto session = typed<std::shared_ptr<Session>>(raw);
                        auto options = TransactionOptions::parse(borrow(bytes));
                        auto gate = session->admit(true);
                        output_operation(
                            output, DbOperation::create(session->source, 3, false,
                                                        [session, options, gate](auto cancel) -> Value
                                                        {
                                                            return std::make_shared<Owner>(
                                                                session->source,
                                                                Transaction::begin(session, options, cancel, gate));
                                                        }));
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_tx_finish_create_v1(nw_db_ref raw, std::uint32_t action, nw_db_ref* output,
                                                             nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        if (action > 1)
                            throw Error::invalid("transaction finish action");
                        auto tx = typed<std::shared_ptr<Transaction>>(raw);
                        auto gate = tx->session->admit(true);
                        output_operation(output, DbOperation::create(tx->session->source, 101, false,
                                                                     [tx, action, gate](auto cancel) -> Value
                                                                     {
                                                                         return std::make_shared<Buffer>(
                                                                             tx->session->source,
                                                                             tx->finish(action == 0, cancel, gate));
                                                                     }));
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_savepoint_create_v1(nw_db_ref raw, nw_db_ref* output, nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto tx = typed<std::shared_ptr<Transaction>>(raw);
                        auto gate = tx->session->admit(true);
                        output_operation(output, DbOperation::create(tx->session->source, 4, false,
                                                                     [tx, gate](auto cancel) -> Value
                                                                     {
                                                                         return std::make_shared<Owner>(
                                                                             tx->session->source,
                                                                             tx->create_savepoint(cancel, gate));
                                                                     }));
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_savepoint_action_create_v1(nw_db_ref raw, std::uint32_t action,
                                                                    nw_db_ref* output, nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        if (action > 1)
                            throw Error::invalid("savepoint action");
                        auto point = typed<std::shared_ptr<Savepoint>>(raw);
                        auto gate = point->transaction->session->admit(true);
                        output_operation(output, DbOperation::create(point->transaction->session->source, 101, false,
                                                                     [point, action, gate](auto cancel) -> Value
                                                                     {
                                                                         (void)point->transaction->savepoint_action(
                                                                             point->token, action, cancel, gate);
                                                                         return std::make_shared<Buffer>(
                                                                             point->transaction->session->source,
                                                                             point->transaction->snapshot());
                                                                     }));
                    });
}

NW_DATABASE_API nw_status NW_CDECL nw_db_admin_create_v1(nw_db_ref raw, std::uint32_t action, nw_bytes_view_v1 payload,
                                                         nw_db_ref* output, nw_error_info_v1* error)
{
    return boundary(
        error,
        [&]
        {
            clear(output);
            auto source = source_owner(raw);
            source->admit();
            auto limits = source->config.limits.administration();
            auto request = parse_administration(source->config.provider.profile, action, borrow(payload), limits);
            if (source->config.provider.profile == Profile::Sqlite &&
                (source->config.provider.sqlite_mode == 3 || source->config.provider.connection == ":memory:"))
                throw Error::unsupported();
            output_operation(output, DbOperation::create(
                                         source, 101, false,
                                         [source, request, limits](auto cancel) -> Value
                                         {
                                             auto options = source->config.provider;
                                             if (options.profile == Profile::Sqlite)
                                                 options.sqlite_busy_timeout_ns = request.lock_timeout_ns;
                                             auto connection = ProviderConnection::open(
                                                 options,
                                                 {cancel, Clock::now() + std::chrono::nanoseconds(
                                                                             source->config.limits.admission_ns)});
                                             try
                                             {
                                                 auto bytes = administer(request, *connection, limits, {cancel});
                                                 connection->close();
                                                 return std::make_shared<Buffer>(source, std::move(bytes));
                                             }
                                             catch (...)
                                             {
                                                 connection->close();
                                                 throw;
                                             }
                                         },
                                         DbOperation::Dispatch::Administration));
        });
}

NW_DATABASE_API nw_status NW_CDECL nw_db_close_create_v1(nw_db_ref raw, nw_db_ref* output, nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto value = owner(raw);
                        auto source = value->source;
                        std::function<Value(std::shared_ptr<Cancel>)> action;
                        auto dispatch = DbOperation::Dispatch::Control;
                        if (std::holds_alternative<std::monostate>(value->data))
                        {
                            dispatch = DbOperation::Dispatch::SourceClose;
                            action = [source](auto) -> Value
                            {
                                source->join();
                                return std::make_shared<Buffer>(source, source->snapshot());
                            };
                        }
                        else if (auto session = std::get_if<std::shared_ptr<Session>>(&value->data))
                        {
                            action = [session = *session, source](auto) -> Value
                            {
                                session->request_close();
                                session->join();
                                return std::make_shared<Buffer>(source, session->snapshot());
                            };
                        }
                        else if (auto cursor = std::get_if<std::shared_ptr<Cursor>>(&value->data))
                        {
                            action = [cursor = *cursor, source](auto) -> Value
                            {
                                cursor->request_close();
                                cursor->join();
                                return std::make_shared<Buffer>(source, cursor->snapshot());
                            };
                        }
                        else if (auto tx = std::get_if<std::shared_ptr<Transaction>>(&value->data))
                        {
                            action = [tx = *tx, source](auto) -> Value
                            {
                                tx->request_close();
                                tx->join();
                                return std::make_shared<Buffer>(source, tx->snapshot());
                            };
                        }
                        else if (auto point = std::get_if<std::shared_ptr<Savepoint>>(&value->data))
                        {
                            action = [point = *point, source](auto) -> Value
                            {
                                auto bytes = point->transaction->dispose_savepoint(point->token);
                                point->cleanup_requested.store(true);
                                return std::make_shared<Buffer>(source, std::move(bytes));
                            };
                        }
                        else
                            throw Error::state("owner has no close operation");
                        output_operation(output, DbOperation::create(source, 101, true, std::move(action), dispatch));
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_snapshot_v1(nw_db_ref raw, nw_bytes_view_v1 query, nw_db_buffer** output,
                                                     nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        if (!borrow(query).empty())
                            throw Error::invalid("snapshot query");
                        *output = reinterpret_cast<nw_db_buffer*>(publish(snapshot_buffer(owner(raw))));
                    });
}
NW_DATABASE_API void NW_CDECL nw_db_owner_release_v1(nw_db_ref raw)
{
    try
    {
        release<std::shared_ptr<Owner>>(raw);
    }
    catch (...)
    {
    }
}
NW_DATABASE_API nw_status NW_CDECL nw_db_buffer_view_v1(nw_db_buffer* raw, nw_bytes_view_v1* output,
                                                        nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto buffer = get<std::shared_ptr<Buffer>>(raw);
                        *output = {buffer->bytes.data(), buffer->bytes.size()};
                    });
}
NW_DATABASE_API void NW_CDECL nw_db_buffer_release_v1(nw_db_buffer* raw)
{
    try
    {
        release<std::shared_ptr<Buffer>>(raw);
    }
    catch (...)
    {
    }
}
NW_DATABASE_API std::uint64_t NW_CDECL nw_db_diagnostic_count_v1(std::uint32_t kind)
{
    switch (kind)
    {
        case 0:
            return source_count.load();
        case 1:
            return session_count.load();
        case 2:
            return operation_count.load();
        case 3:
            return cursor_count.load();
        case 4:
            return buffer_count.load();
        case 5:
        {
            std::lock_guard lock(handles_mutex);
            return handles.size();
        }
        default:
            return 0;
    }
}
