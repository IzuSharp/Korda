// SPDX-License-Identifier: MIT
#include "nw/host.hpp"

// CMake derives this private C++ view from the byte-frozen C header, changing
// only its `template` parameter name. Every definition is type-checked against
// the normative declarations; applications still consume the original C ABI.
#include "../../database/binding/korda_command_completion_v1.h"
#include "../../database/binding/korda_cursor_pull_execution_v1.h"
#include "../../database/binding/korda_cursor_pull_v1.h"
#include "../../database/binding/korda_find_shape_v1.h"
#include "../../database/binding/korda_query_command_v1.h"
#include "nw_database_declarations.hpp"

#include <algorithm>
#include <type_traits>
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
template <class T>
void* take_command_handle(const std::shared_ptr<CommandCompletion>& command, std::uint32_t expected_kind)
{
    // Both registry allocations precede consumption. If either fails, the
    // command still owns its returned child/buffer for a later take or actual
    // cold join. No generic operation behavior or public ABI is changed.
    auto allocation = std::make_unique<Stored>(Stored{T{}});
    void* address = allocation.get();
    std::unique_lock lock(handles_mutex);
    auto [slot, inserted] = handles.emplace(reinterpret_cast<std::uintptr_t>(address), std::move(allocation));
    (void)inserted;
    try
    {
        auto value = command->take(expected_kind);
        auto result = std::get<T>(std::move(value));
        if constexpr (std::is_same_v<T, std::shared_ptr<Buffer>>)
            result->mark_taken();
        slot->second->value = std::move(result);
    }
    catch (...)
    {
        auto rollback = std::move(slot->second);
        handles.erase(slot);
        lock.unlock();
        rollback.reset();
        throw;
    }
    return address;
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
ByteView borrow_command(nw_bytes_view_v1 value, const QueryLimits& limits)
{
    if (value.length > limits.bytes + 128 * limits.nodes + 64)
        throw Error::capacity();
    if (value.length && !value.data)
        throw Error::invalid("null query command byte view");
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
std::shared_ptr<Source> command_source(nw_db_ref raw)
{
    auto session = typed<std::shared_ptr<Session>>(raw);
    session->source->admit();
    if (session->closing.load())
        throw Error::state("closed query command session");
    return session->source;
}
std::vector<QueryCommandDependency> command_dependencies(const std::shared_ptr<Source>& source,
                                                         const nw_db_query_dependency_v1* dependencies,
                                                         std::uint32_t count)
{
    if (count > 2 * source->config.limits.nodes + 1)
        throw Error::capacity();
    if (count != 0 &&
        (!dependencies || reinterpret_cast<std::uintptr_t>(dependencies) % alignof(nw_db_query_dependency_v1)))
        throw Error::invalid("invalid query command dependency sidecar");
    std::vector<QueryCommandDependency> result;
    result.reserve(count);
    for (std::uint32_t i = 0; i < count; ++i)
    {
        const auto& dependency = dependencies[i];
        if (dependency.reserved != 0)
            throw Error::invalid("query command dependency reserved field");
        auto value = owner(dependency.owner);
        if (value->source->identity != source->identity)
            throw Error::state("mixed query command dependency source");
        if (dependency.kind == 1 && std::holds_alternative<ModelBinding>(value->data))
            result.emplace_back(std::get<ModelBinding>(value->data));
        else if (dependency.kind == 2 && std::holds_alternative<std::shared_ptr<NativePlan>>(value->data))
            result.emplace_back(std::get<std::shared_ptr<NativePlan>>(value->data));
        else if (dependency.kind == 3 && std::holds_alternative<std::shared_ptr<NativeCte>>(value->data))
            result.emplace_back(std::get<std::shared_ptr<NativeCte>>(value->data));
        else
            throw Error::state("query command dependency kind differs");
    }
    return result;
}
QueryCommandContext command_context(const std::shared_ptr<Source>& source)
{
    return {source->parameters, source->config.limits.binding(),
            Capabilities::from_mask(capability_mask(), source->config.provider.profile),
            source->config.limits.command_ns};
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
std::uint64_t remaining_command_time(std::uint64_t timeout_ns, Clock::time_point admitted)
{
    const auto elapsed = std::chrono::duration_cast<std::chrono::nanoseconds>(Clock::now() - admitted);
    return elapsed >= std::chrono::nanoseconds(timeout_ns) ? 0
                                                           : timeout_ns - static_cast<std::uint64_t>(elapsed.count());
}
struct CapturedExecution
{
    std::shared_ptr<SqlPlan> plan;
    std::shared_ptr<CommandGate> gate;
};
CapturedExecution capture_execution(const std::shared_ptr<Session>& session, nw_db_ref input,
                                    nw_bytes_view_v1 arguments)
{
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
        auto rendered = (*native)->render(session->source->identity, bytes, &session->source->shapes);
        plan = SqlPlan::rendered(session->source, std::move(rendered));
    }
    else
        throw Error::state("execution plan required");
    return {std::move(plan), std::move(gate)};
}
struct CapturedTimedExecution
{
    std::shared_ptr<SqlPlan> plan;
    std::shared_ptr<CommandGate> gate;
    std::uint64_t timeout;
    Clock::time_point admitted;
    Mutation mutation{};
};
CapturedTimedExecution capture_relation(const std::shared_ptr<Session>& session, nw_db_ref input, nw_bytes_view_v1 key,
                                        std::uint32_t projection)
{
    auto relation = typed<std::shared_ptr<RelationBinding>>(input);
    auto source = session->source;
    if (relation->source != source->identity)
        throw Error(NW_INVALID_STATE, 13, "mixed relation load source");
    const auto limits = source->config.limits.binding();
    const auto timeout = source->config.limits.command_ns;
    const auto admitted = Clock::now();
    if (!timeout)
        throw Error(NW_TIMEOUT, 8, "relation deadline expired");
    const auto bytes = borrow(key);
    const auto arity = relation->parent.model->keys().size();
    // The B14 Host applies these capacity checks before projection
    // lookup and the binding compiler's own argument validation.
    if (bytes.size() > limits.bytes || arity > limits.parameters || arity > limits.nodes / 3 || limits.depth < 2)
        throw Error::capacity();
    auto rendered =
        compile_relation(*relation, source->identity, bytes, source->projection(projection), limits, timeout);
    auto plan = SqlPlan::rendered(source, std::move(rendered));
    plan->relation = relation;
    auto gate = session->admit(false);
    return {std::move(plan), std::move(gate), timeout, admitted, {}};
}
CapturedTimedExecution capture_mutation(const std::shared_ptr<Session>& session, nw_db_ref input, std::uint32_t kind,
                                        nw_bytes_view_v1 payload)
{
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
    const auto timeout = source->config.limits.command_ns;
    const auto admitted = Clock::now();
    auto mutation = compile_mutation(model, kind, bytes, projection, source->config.limits.binding(), timeout);
    if (mutation.input_rows > source->config.limits.batch_rows)
        throw Error::capacity();
    auto plan = SqlPlan::rendered(source, std::move(mutation.command));
    plan->kind = 1;
    auto gate = session->admit(false);
    return {std::move(plan), std::move(gate), timeout, admitted, std::move(mutation)};
}
struct CapturedCommand
{
    std::shared_ptr<Source> source;
    std::uint32_t result_kind;
    bool control = false;
    std::function<Value(std::shared_ptr<Cancel>)> action;
    std::shared_ptr<CommandGate> gate;
};
CapturedCommand capture_command(const nw_db_command_request_args_v1& request)
{
    if (request.struct_size != sizeof(request) || request.reserved || request.kind < 1 || request.kind > 7)
        throw Error::invalid("command request record");
    if (request.kind >= 3 && request.input)
        throw Error::invalid("unexpected command input");
    if (request.kind >= 4 && (request.payload.data || request.payload.length))
        throw Error::invalid("unexpected command payload");
    if ((request.kind == 1 || request.kind == 3 || request.kind == 5 || request.kind == 7) && request.action)
        throw Error::invalid("unexpected command action");
    if ((request.kind == 4 || request.kind == 6) && request.action > 1)
        throw Error::invalid("command action");

    switch (request.kind)
    {
        case 1:
        {
            auto session = typed<std::shared_ptr<Session>>(request.target);
            auto captured = capture_execution(session, request.input, request.payload);
            if (captured.plan->result_kind != 0 && !captured.plan->scalar_count)
                throw Error::invalid("one-shot command cannot return rows");
            auto source = session->source;
            auto gate = captured.gate;
            return {std::move(source), 100, false,
                    [session = std::move(session), plan = std::move(captured.plan),
                     gate = std::move(captured.gate)](auto cancel) -> Value
                    {
                        if (plan->scalar_count)
                            return session->execute_scalar_count(plan, std::move(cancel), gate);
                        return session->execute(plan, std::move(cancel), gate);
                    },
                    std::move(gate)};
        }
        case 2:
        {
            if (request.action == 1)
                throw Error::invalid("one-shot mutation cannot return rows");
            auto session = typed<std::shared_ptr<Session>>(request.target);
            auto captured = capture_mutation(session, request.input, request.action, request.payload);
            auto source = session->source;
            auto gate = captured.gate;
            return {std::move(source), 100, false,
                    [session = std::move(session), plan = std::move(captured.plan), gate = std::move(captured.gate),
                     mutation = std::move(captured.mutation), timeout = captured.timeout, admitted = captured.admitted,
                     kind = request.action](auto cancel) -> Value
                    {
                        try
                        {
                            plan->timeout_ns = remaining_command_time(timeout, admitted);
                            auto value = session->execute(plan, std::move(cancel), gate);
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
                                error.bulk = NativeBulkProgress{0, 0, false, outcome};
                            }
                            throw;
                        }
                    },
                    std::move(gate)};
        }
        case 3:
        {
            auto session = typed<std::shared_ptr<Session>>(request.target);
            auto options = TransactionOptions::parse(borrow(request.payload));
            auto gate = session->admit(true);
            auto source = session->source;
            return {std::move(source), 3, false,
                    [session = std::move(session), options, gate](auto cancel) -> Value
                    {
                        return std::make_shared<Owner>(session->source,
                                                       Transaction::begin(session, options, std::move(cancel), gate));
                    },
                    std::move(gate)};
        }
        case 4:
        {
            auto tx = typed<std::shared_ptr<Transaction>>(request.target);
            auto gate = tx->session->admit(true);
            auto source = tx->session->source;
            return {std::move(source), 101, false,
                    [tx = std::move(tx), action = request.action, gate](auto cancel) -> Value
                    {
                        return std::make_shared<Buffer>(tx->session->source,
                                                        tx->finish(action == 0, std::move(cancel), gate));
                    },
                    std::move(gate)};
        }
        case 5:
        {
            auto tx = typed<std::shared_ptr<Transaction>>(request.target);
            auto gate = tx->session->admit(true);
            auto source = tx->session->source;
            return {
                std::move(source), 4, false, [tx = std::move(tx), gate](auto cancel) -> Value
                { return std::make_shared<Owner>(tx->session->source, tx->create_savepoint(std::move(cancel), gate)); },
                std::move(gate)};
        }
        case 6:
        {
            auto point = typed<std::shared_ptr<Savepoint>>(request.target);
            auto gate = point->transaction->session->admit(true);
            auto source = point->transaction->session->source;
            return {std::move(source), 101, false,
                    [point = std::move(point), action = request.action, gate](auto cancel) -> Value
                    {
                        (void)point->transaction->savepoint_action(point->token, action, std::move(cancel), gate);
                        return std::make_shared<Buffer>(point->transaction->session->source,
                                                        point->transaction->snapshot());
                    },
                    std::move(gate)};
        }
        case 7:
        {
            auto value = owner(request.target);
            auto source = value->source;
            if (auto session = std::get_if<std::shared_ptr<Session>>(&value->data))
                return {std::move(source),
                        101,
                        true,
                        [session = *session](auto) -> Value
                        {
                            session->request_close();
                            session->join();
                            return std::make_shared<Buffer>(session->source, session->snapshot());
                        },
                        {}};
            if (auto tx = std::get_if<std::shared_ptr<Transaction>>(&value->data))
                return {std::move(source),
                        101,
                        true,
                        [tx = *tx](auto) -> Value
                        {
                            tx->request_close();
                            tx->join();
                            return std::make_shared<Buffer>(tx->session->source, tx->snapshot());
                        },
                        {}};
            if (auto point = std::get_if<std::shared_ptr<Savepoint>>(&value->data))
                return {std::move(source),
                        101,
                        true,
                        [point = *point](auto) -> Value
                        {
                            auto bytes = point->transaction->dispose_savepoint(point->token);
                            point->cleanup_requested.store(true);
                            return std::make_shared<Buffer>(point->transaction->session->source, std::move(bytes));
                        },
                        {}};
            throw Error::invalid("one-shot close requires session, transaction or savepoint");
        }
        default:
            throw Error::invalid("command request kind");
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
    else if (auto pull = std::get_if<std::shared_ptr<CursorPull>>(&value->data))
        bytes = (*pull)->snapshot();
    else if (auto command = std::get_if<std::shared_ptr<CommandCompletion>>(&value->data))
        bytes = (*command)->snapshot();
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

NW_DATABASE_API nw_status NW_CDECL nw_db_find_plan_create_v1(nw_db_ref raw, nw_db_ref model_raw, std::uint32_t table,
                                                             std::uint32_t projection, nw_db_ref* output,
                                                             std::uint64_t* charge, std::uint64_t* logical_bytes,
                                                             nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        clear(charge);
                        clear(logical_bytes);
                        auto source = source_owner(raw);
                        source->admit();
                        auto model = typed<ModelBinding>(model_raw);
                        model.ensure_source(source->identity);
                        if (model.model->table_id() != table)
                            throw Error::invalid("Find root table identity differs");
                        auto context = command_context(source);
                        auto plan = NativeQuery::find(std::move(model), source->projection(projection), context.limits,
                                                      context.capabilities, context.timeout_ns);
                        const auto bytes = plan->find_cache_charge();
                        const auto logical = plan->owned_bytes;
                        output_owner(output, source, std::move(plan));
                        *charge = bytes;
                        *logical_bytes = logical;
                    });
}

NW_DATABASE_API nw_status NW_CDECL nw_db_query_command_compile_v1(nw_db_ref raw, nw_bytes_view_v1 command,
                                                                  const nw_db_query_dependency_v1* dependencies,
                                                                  std::uint32_t dependency_count,
                                                                  std::uint32_t terminal, nw_db_ref* output,
                                                                  nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto source = command_source(raw);
                        auto context = command_context(source);
                        auto resolved = command_dependencies(source, dependencies, dependency_count);
                        auto plan =
                            compile_query_command(borrow_command(command, context.limits), resolved, context, terminal);
                        output_owner(output, source, std::move(plan));
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_query_command_render_v1(nw_db_ref raw, nw_bytes_view_v1 command,
                                                                 const nw_db_query_dependency_v1* dependencies,
                                                                 std::uint32_t dependency_count, nw_db_buffer** output,
                                                                 nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto source = command_source(raw);
                        auto context = command_context(source);
                        auto resolved = command_dependencies(source, dependencies, dependency_count);
                        auto sql = render_query_command(borrow_command(command, context.limits), resolved, context);
                        output_buffer(output, source, std::move(sql));
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_query_command_cte_v1(nw_db_ref raw, nw_bytes_view_v1 command,
                                                              const nw_db_query_dependency_v1* dependencies,
                                                              std::uint32_t dependency_count, nw_db_ref declared,
                                                              std::uint32_t table, std::uint32_t recursive,
                                                              nw_db_ref* output, nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto source = command_source(raw);
                        auto model = typed<ModelBinding>(declared);
                        model.ensure_source(source->identity);
                        auto context = command_context(source);
                        auto resolved = command_dependencies(source, dependencies, dependency_count);
                        auto cte = create_query_command_cte(borrow_command(command, context.limits), resolved, context,
                                                            std::move(model), table, recursive);
                        output_owner(output, source, std::move(cte));
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
NW_DATABASE_API nw_status NW_CDECL nw_db_command_request_v1(const nw_db_command_request_args_v1* request,
                                                            const nw_db_cursor_callbacks_v1* callbacks,
                                                            nw_db_ref* output, nw_error_info_v1* error)
{
    return boundary(
        error,
        [&]
        {
            clear(output);
            if (!request || reinterpret_cast<std::uintptr_t>(request) % alignof(nw_db_command_request_args_v1))
                throw Error::invalid("command request record address");
            if (!callbacks || reinterpret_cast<std::uintptr_t>(callbacks) % alignof(nw_db_cursor_callbacks_v1) ||
                callbacks->struct_size != sizeof(*callbacks) || callbacks->reserved || !callbacks->ready)
                throw Error::invalid("command callback record");
            std::shared_ptr<CommandCompletion> command;
            {
                auto captured = capture_command(*request);
                command = CommandCompletion::create(captured.source, captured.result_kind, captured.control,
                                                    std::move(captured.action), *callbacks, std::move(captured.gate));
                output_owner(output, std::move(captured.source), command);
            }
            // Captured Session/plan/gate/input owners are gone from
            // this stack before the worker can notify. The handle
            // slot is already written for an early ready callback.
            command->start();
        });
}

NW_DATABASE_API nw_status NW_CDECL nw_db_command_take_v1(nw_db_ref raw, std::uint32_t expected_kind,
                                                         nw_db_command_result_v1* output, nw_error_info_v1* error)
{
    return boundary(
        error,
        [&]
        {
            clear(output);
            auto command = typed<std::shared_ptr<CommandCompletion>>(raw);
            nw_db_command_result_v1 result{sizeof(nw_db_command_result_v1), expected_kind, 0, nullptr, nullptr};
            if (expected_kind == 100)
            {
                result.count = std::get<std::int64_t>(command->take(expected_kind));
            }
            else if (expected_kind == 101)
            {
                result.buffer = reinterpret_cast<nw_db_buffer*>(
                    take_command_handle<std::shared_ptr<Buffer>>(command, expected_kind));
            }
            else
            {
                result.owner =
                    reinterpret_cast<nw_db_ref>(take_command_handle<std::shared_ptr<Owner>>(command, expected_kind));
            }
            *output = result;
        });
}

NW_DATABASE_API nw_status NW_CDECL nw_db_command_take_error_v1(nw_db_ref raw, nw_db_buffer** output,
                                                               nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto value = owner(raw);
                        auto command = std::get_if<std::shared_ptr<CommandCompletion>>(&value->data);
                        if (!command)
                            throw Error::state("command owner required");
                        auto failure = (*command)->take_error();
                        auto bytes = snapshot_record(0, 0, false, failure.cleanup ? 4 : 0, failure.cleanup ? 2 : 0,
                                                     false, false, failure.correlation, failure.provider);
                        if (failure.bulk)
                        {
                            auto bulk = failure.bulk->encode();
                            bytes.insert(bytes.end(), bulk.begin(), bulk.end());
                        }
                        output_buffer(output, value->source, std::move(bytes));
                    });
}

NW_DATABASE_API nw_status NW_CDECL nw_db_command_cancel_v1(nw_db_ref raw)
{
    return boundary(nullptr, [&] { typed<std::shared_ptr<CommandCompletion>>(raw)->request_close(); });
}

NW_DATABASE_API nw_status NW_CDECL nw_db_execute_create_v1(nw_db_ref raw, nw_db_ref input, nw_bytes_view_v1 arguments,
                                                           nw_db_ref* output, nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto session = typed<std::shared_ptr<Session>>(raw);
                        auto [plan, gate] = capture_execution(session, input, arguments);
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
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto session = typed<std::shared_ptr<Session>>(raw);
                        auto captured = capture_relation(session, input, key, projection);
                        auto source = session->source;
                        auto plan = std::move(captured.plan);
                        auto gate = std::move(captured.gate);
                        const auto timeout = captured.timeout;
                        const auto admitted = captured.admitted;
                        output_operation(output,
                                         DbOperation::create(source, 2, false,
                                                             [session, plan, gate, timeout, admitted](auto cancel)
                                                             {
                                                                 // Preserve B14's create-time total deadline, including
                                                                 // copied input, delayed Start and worker queue time.
                                                                 plan->timeout_ns =
                                                                     remaining_command_time(timeout, admitted);
                                                                 return session->execute(plan, cancel, gate);
                                                             }));
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_mutation_create_v1(nw_db_ref raw, nw_db_ref input, std::uint32_t kind,
                                                            nw_bytes_view_v1 payload, nw_db_ref* output,
                                                            nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto session = typed<std::shared_ptr<Session>>(raw);
                        auto captured = capture_mutation(session, input, kind, payload);
                        auto source = session->source;
                        auto plan = std::move(captured.plan);
                        auto gate = std::move(captured.gate);
                        auto mutation = std::move(captured.mutation);
                        const auto timeout = captured.timeout;
                        const auto admitted = captured.admitted;
                        output_operation(
                            output, DbOperation::create(
                                        source, kind == 1 ? 2 : 100, false,
                                        [session, plan, gate, mutation, kind, timeout, admitted](auto cancel) -> Value
                                        {
                                            try
                                            {
                                                plan->timeout_ns = remaining_command_time(timeout, admitted);
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
                                                    error.bulk = NativeBulkProgress{0, 0, false, outcome};
                                                }
                                                throw;
                                            }
                                        }));
                    });
}

NW_DATABASE_API nw_status NW_CDECL nw_db_cursor_pull_create_v1(nw_db_ref raw, nw_db_ref input,
                                                               nw_bytes_view_v1 arguments, nw_db_ref* output,
                                                               nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto session = typed<std::shared_ptr<Session>>(raw);
                        auto [plan, gate] = capture_execution(session, input, arguments);
                        auto pull = CursorPull::create(session, std::move(plan), std::move(gate));
                        output_owner(output, session->source, std::move(pull));
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_cursor_pull_relation_create_v1(nw_db_ref raw, nw_db_ref input,
                                                                        nw_bytes_view_v1 key, std::uint32_t projection,
                                                                        nw_db_ref* output, nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto session = typed<std::shared_ptr<Session>>(raw);
                        auto captured = capture_relation(session, input, key, projection);
                        auto pull = CursorPull::create(session, std::move(captured.plan), std::move(captured.gate),
                                                       captured.admitted);
                        output_owner(output, session->source, std::move(pull));
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_cursor_pull_mutation_create_v1(nw_db_ref raw, nw_db_ref input,
                                                                        std::uint32_t kind, nw_bytes_view_v1 payload,
                                                                        nw_db_ref* output, nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto session = typed<std::shared_ptr<Session>>(raw);
                        auto captured = capture_mutation(session, input, kind, payload);
                        auto pull = CursorPull::create(session, std::move(captured.plan), std::move(captured.gate),
                                                       captured.admitted);
                        output_owner(output, session->source, std::move(pull));
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_cursor_pull_request_v1(nw_db_ref raw, std::uint64_t generation,
                                                                std::uint32_t rows, std::uint64_t bytes,
                                                                const nw_db_cursor_callbacks_v1* callbacks,
                                                                nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        auto pull = typed<std::shared_ptr<CursorPull>>(raw);
                        if (!callbacks ||
                            reinterpret_cast<std::uintptr_t>(callbacks) % alignof(nw_db_cursor_callbacks_v1))
                            throw Error::invalid("cursor callback record address");
                        if (bytes > MAX_BYTES)
                            throw Error::invalid("fetch capacity");
                        pull->request(generation, rows, static_cast<std::size_t>(bytes), *callbacks);
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_cursor_pull_prepare_v1(nw_db_ref raw, std::uint64_t generation,
                                                                std::uint32_t rows, std::uint64_t bytes,
                                                                const nw_db_cursor_callbacks_v1* callbacks,
                                                                nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        auto pull = typed<std::shared_ptr<CursorPull>>(raw);
                        if (!callbacks ||
                            reinterpret_cast<std::uintptr_t>(callbacks) % alignof(nw_db_cursor_callbacks_v1))
                            throw Error::invalid("cursor callback record address");
                        if (bytes > MAX_BYTES)
                            throw Error::invalid("fetch capacity");
                        pull->prepare(generation, rows, static_cast<std::size_t>(bytes), *callbacks);
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_cursor_pull_run_v1(nw_db_ref raw, std::uint64_t generation,
                                                            nw_error_info_v1* error)
{
    // Pass the temporary owner directly. The consumed Worker's tickets cover
    // the static execution helper after it releases this channel accessor.
    return boundary(error, [&] { CursorPull::run_prepared(typed<std::shared_ptr<CursorPull>>(raw), generation); });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_cursor_pull_abandon_v1(nw_db_ref raw, std::uint64_t generation,
                                                                nw_error_info_v1* error)
{
    return boundary(error, [&] { CursorPull::abandon_prepared(typed<std::shared_ptr<CursorPull>>(raw), generation); });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_cursor_pull_take_v1(nw_db_ref raw, std::uint64_t generation,
                                                             nw_db_batch_result_v1* output, nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto buffer = typed<std::shared_ptr<CursorPull>>(raw)->take(generation);
                        const nw_bytes_view_v1 bytes{buffer->bytes.data(), buffer->bytes.size()};
                        const auto known_end = buffer->known_end ? 1u : 0u;
                        buffer->mark_taken();
                        auto owner = reinterpret_cast<nw_db_buffer*>(publish(std::move(buffer)));
                        *output = {owner, bytes, known_end, 0};
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_cursor_pull_take_error_v1(nw_db_ref raw, std::uint64_t generation,
                                                                   nw_db_buffer** output, nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto pull = typed<std::shared_ptr<CursorPull>>(raw);
                        auto failure = pull->take_error(generation);
                        auto bytes = snapshot_record(0, 0, false, failure.cleanup ? 4 : 0, failure.cleanup ? 2 : 0,
                                                     false, false, failure.correlation, failure.provider);
                        if (failure.bulk)
                        {
                            auto bulk = failure.bulk->encode();
                            bytes.insert(bytes.end(), bulk.begin(), bulk.end());
                        }
                        output_buffer(output, pull->session->source, std::move(bytes));
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_cursor_pull_check_read_v1(nw_db_ref raw, nw_error_info_v1* error)
{
    return boundary(error, [&] { typed<std::shared_ptr<CursorPull>>(raw)->check_read_admission(); });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_cursor_pull_stop_v1(nw_db_ref raw)
{
    return boundary(nullptr, [&] { typed<std::shared_ptr<CursorPull>>(raw)->request_close(); });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_cursor_pull_close_request_v1(nw_db_ref raw,
                                                                      const nw_db_cursor_callbacks_v1* callbacks,
                                                                      nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        auto pull = typed<std::shared_ptr<CursorPull>>(raw);
                        if (!callbacks ||
                            reinterpret_cast<std::uintptr_t>(callbacks) % alignof(nw_db_cursor_callbacks_v1))
                            throw Error::invalid("cursor close callback record address");
                        pull->close_request(*callbacks);
                    });
}
NW_DATABASE_API nw_status NW_CDECL nw_db_cursor_pull_close_take_v1(nw_db_ref raw, nw_db_buffer** output,
                                                                   nw_error_info_v1* error)
{
    return boundary(error,
                    [&]
                    {
                        clear(output);
                        auto result = typed<std::shared_ptr<CursorPull>>(raw)->close_take();
                        *output = reinterpret_cast<nw_db_buffer*>(publish(std::move(result)));
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
            if (action == 4)
            {
                output_operation(
                    output, DbOperation::create(
                                source, 101, false, [source, request, limits](auto cancel) -> Value
                                { return std::make_shared<Buffer>(source, draft_schema(request, limits, {cancel})); }));
                return;
            }
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
                        else if (auto pull = std::get_if<std::shared_ptr<CursorPull>>(&value->data))
                        {
                            action = [pull = *pull, source](auto) -> Value
                            {
                                pull->join();
                                return std::make_shared<Buffer>(source, pull->snapshot());
                            };
                        }
                        else if (auto command = std::get_if<std::shared_ptr<CommandCompletion>>(&value->data))
                        {
                            action = [command = *command, source](auto) -> Value
                            {
                                command->join();
                                return std::make_shared<Buffer>(source, command->snapshot());
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
        case 6:
            return provider_dispatch_count();
        case 7:
            return provider_protocol_count();
        case 8:
            return pull_count.load();
        case 9:
            return command_count.load();
        default:
            return 0;
    }
}
