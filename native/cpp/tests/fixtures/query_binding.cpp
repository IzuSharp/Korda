// SPDX-License-Identifier: MIT
// Test-only B09 bridge. It exercises the production C++ validators with the
// original synchronous fixture protocol; it is never a product database Host.
#include "nw/mutations.hpp"
#include "support.hpp"

// The canonical C11 declarations are compiled independently by abi-probe.c.
// Keep the same opaque handle representations at this test-only ABI boundary.
struct nw_db_owner;
using nw_db_ref = nw_db_owner*;
struct nw_db_buffer;

#include <algorithm>
#include <cstdlib>
#include <variant>

namespace
{
using namespace nw;
std::atomic<std::uint64_t> next_source{1};
std::atomic<std::uint64_t> owners{0}, operations{0}, buffers{0};

[[noreturn]] void bad()
{
    throw Error(NW_INVALID_ARGUMENT, "fixture argument");
}
[[noreturn]] void wrong()
{
    throw Error(NW_INVALID_STATE, "fixture owner/state");
}
ByteView view(nw_bytes_view_v1 value)
{
    if (value.length > MAX_BYTES || (value.length != 0 && value.data == nullptr))
        bad();
    return {value.data, static_cast<std::size_t>(value.length)};
}
template <class T> void output(T* value)
{
    if (value == nullptr)
        bad();
    *value = T{};
}
template <class F> nw_status finish(nw_error_info_v1* error, F&& action)
{
    nw_status status = NW_OK;
    try
    {
        action();
    }
    catch (const Error& failure)
    {
        status = failure.status;
    }
    catch (const std::bad_alloc&)
    {
        status = NW_OUT_OF_MEMORY;
    }
    catch (...)
    {
        status = NW_NATIVE_PANIC;
    }
    if (error != nullptr)
        *error = {sizeof(nw_error_info_v1), status == NW_OK ? 0u : 9u, static_cast<std::int32_t>(status), 0,
                  status == NW_OK ? 0u : 109u};
    return status;
}

struct Source
{
    SourceIdentity identity;
    std::shared_ptr<CodecBindings> codecs;
    std::shared_ptr<ParameterRegistry> parameters;
    std::mutex mutex;
    std::map<std::uint32_t, ProjectionBinding> projections;
    std::map<Hash, std::shared_ptr<RelationBinding>> relations;
    std::size_t relation_bytes = 0;
};
struct SourceValue
{
    std::shared_ptr<Source> source;
};
struct SessionValue
{
    std::shared_ptr<Source> source;
};
struct Query
{
    explicit Query(std::shared_ptr<NativeQuery> value) : value(std::move(value)) {}
    std::mutex mutex;
    std::shared_ptr<NativeQuery> value;
};
struct Cursor
{
    ProjectionBinding projection;
    Bytes retained;
    RenderedQuery execution;
    std::shared_ptr<const RelationBinding> relation;
    std::shared_ptr<const NativePlan> plan;
    std::shared_ptr<Source> session;
};
struct Buffer
{
    explicit Buffer(Bytes value) : bytes(std::move(value))
    {
        ++buffers;
    }
    ~Buffer()
    {
        --buffers;
    }
    Bytes bytes;
};
struct Owner;
struct BindingOperation
{
    std::mutex mutex;
    bool started = false;
    bool retired = false;
    bool taken = false;
    bool canceled = false;
    bool terminal_canceled = false;
    bool callback_active = false;
    std::uint32_t result_kind = 0;
    std::unique_ptr<Owner> result_owner;
    std::unique_ptr<Buffer> result_buffer;
    Bytes retained;
};
using Value = std::variant<SourceValue, SessionValue, ModelBinding, std::shared_ptr<Query>, std::shared_ptr<NativePlan>,
                           std::shared_ptr<RelationBinding>, std::shared_ptr<NativeCte>, Cursor,
                           std::shared_ptr<BindingOperation>>;
struct Owner
{
    explicit Owner(Value data) : value(std::move(data))
    {
        ++owners;
        if (std::holds_alternative<std::shared_ptr<BindingOperation>>(value))
            ++operations;
    }
    ~Owner()
    {
        if (std::holds_alternative<std::shared_ptr<BindingOperation>>(value))
            --operations;
        --owners;
    }
    Value value;
};
std::unique_ptr<Owner> owned(Value value)
{
    return std::make_unique<Owner>(std::move(value));
}
nw_db_ref release(std::unique_ptr<Owner> owner)
{
    return reinterpret_cast<nw_db_ref>(owner.release());
}
nw_db_ref operation(std::unique_ptr<Owner> result, std::uint32_t kind, Bytes retained)
{
    auto value = std::make_shared<BindingOperation>();
    value->result_owner = std::move(result);
    value->result_kind = kind;
    value->retained = std::move(retained);
    return release(owned(std::move(value)));
}
Owner& reference(nw_db_ref pointer)
{
    if (pointer == nullptr)
        bad();
    return *reinterpret_cast<Owner*>(pointer);
}
template <class T> T& get(nw_db_ref pointer)
{
    auto* value = std::get_if<T>(&reference(pointer).value);
    if (value == nullptr)
        wrong();
    return *value;
}
std::shared_ptr<Source> source(nw_db_ref pointer, bool session)
{
    return session ? get<SessionValue>(pointer).source : get<SourceValue>(pointer).source;
}
template <class F> auto query(nw_db_ref pointer, F&& action)
{
    const auto& owner = get<std::shared_ptr<Query>>(pointer);
    std::unique_lock gate(owner->mutex, std::try_to_lock);
    if (!gate.owns_lock())
        wrong();
    return action(*owner->value);
}
RelationCapabilities relation_capabilities()
{
    return {true, true, true, true, true};
}
Capabilities capabilities(Profile profile)
{
    return Capabilities::from_mask((1ull << 25) - 1, profile);
}
Bytes copy(ByteView value)
{
    return {value.begin(), value.end()};
}
} // namespace

NW_FIXTURE_API nw_status nw_db_source_create_v1(nw_bytes_view_v1 config, nw_db_ref* result, nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      output(result);
                      auto bytes = view(config);
                      if (bytes.size() != 1 || (bytes[0] != 1 && bytes[0] != 2))
                          bad();
                      auto owner = std::make_shared<Source>();
                      const auto profile = static_cast<Profile>(bytes[0]);
                      owner->identity = {0xb09, next_source.fetch_add(1), 1, profile};
                      owner->codecs = std::make_shared<CodecBindings>(profile);
                      owner->parameters = std::make_shared<ParameterRegistry>(owner->identity, 4096);
                      *result = release(owned(SourceValue{std::move(owner)}));
                  });
}
NW_FIXTURE_API nw_status nw_test_session_v1(nw_db_ref owner, nw_db_ref* result, nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      output(result);
                      *result = release(owned(SessionValue{source(owner, false)}));
                  });
}
NW_FIXTURE_API nw_status nw_db_metadata_register_v1(nw_db_ref owner, nw_bytes_view_v1 metadata, nw_db_ref* result,
                                                    nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      output(result);
                      auto origin = source(owner, false);
                      auto fields = Fields::parse(view(metadata), 2, {{1, 5, true}, {2, 5, true}});
                      auto model = std::make_shared<Model>(Model::parse(fields.bytes(1), origin->identity.profile));
                      const auto supplied = fields.bytes(2);
                      const auto hash = model->hash();
                      if (supplied.size() != hash.size() || !std::equal(supplied.begin(), supplied.end(), hash.begin()))
                          bad();
                      *result = release(owned(ModelBinding(origin->identity, std::move(model), origin->codecs)));
                  });
}
NW_FIXTURE_API nw_status nw_db_projection_register_v1(nw_db_ref owner, nw_bytes_view_v1 bytes, std::uint32_t* result,
                                                      nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      output(result);
                      auto origin = source(owner, false);
                      std::lock_guard gate(origin->mutex);
                      const auto id = static_cast<std::uint32_t>(origin->projections.size() + 1);
                      auto projection = projection_record(view(bytes), origin->identity, id, origin->codecs);
                      origin->projections.emplace(id, std::move(projection));
                      *result = id;
                  });
}
NW_FIXTURE_API nw_status nw_db_relation_register_v1(nw_db_ref owner, nw_db_ref parent, nw_db_ref child,
                                                    nw_bytes_view_v1 definition, nw_db_ref* result,
                                                    nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      output(result);
                      auto origin = source(owner, false);
                      auto relation =
                          RelationBinding::create(origin->identity, get<ModelBinding>(parent), get<ModelBinding>(child),
                                                  view(definition), relation_capabilities());
                      std::lock_guard gate(origin->mutex);
                      const auto hash = relation->relation->hash();
                      if (auto it = origin->relations.find(hash); it != origin->relations.end())
                      {
                          if (it->second->relation->bytes() != relation->relation->bytes())
                              bad();
                          relation = it->second;
                      }
                      else
                      {
                          auto size = relation->relation->bytes().size();
                          if (origin->relations.size() >= 4096 || size > MAX_BYTES - origin->relation_bytes)
                              throw Error::capacity();
                          origin->relations.emplace(hash, relation);
                          origin->relation_bytes += size;
                      }
                      *result = release(owned(std::move(relation)));
                  });
}
NW_FIXTURE_API nw_status nw_db_query_create_v1(nw_db_ref session, nw_db_ref model, std::uint32_t table_id,
                                               nw_db_ref* result, nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      output(result);
                      auto origin = source(session, true);
                      auto root = get<ModelBinding>(model);
                      root.ensure_source(origin->identity);
                      auto value =
                          std::make_shared<NativeQuery>(std::move(root), origin->parameters, table_id, QueryLimits{},
                                                        capabilities(origin->identity.profile), 1000000000);
                      *result = release(owned(std::make_shared<Query>(std::move(value))));
                  });
}
NW_FIXTURE_API nw_status nw_db_expr_column_v1(nw_db_ref owner, std::uint64_t alias, std::uint32_t column,
                                              std::uint64_t* result, nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      output(result);
                      *result = query(owner, [&](auto& value) { return value.column(alias, column); });
                  });
}
NW_FIXTURE_API nw_status nw_db_expr_value_v1(nw_db_ref owner, nw_bytes_view_v1 bytes, std::uint64_t* result,
                                             nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      output(result);
                      *result = query(owner, [&](auto& value) { return value.value(view(bytes)); });
                  });
}
NW_FIXTURE_API nw_status nw_db_expr_parameter_v1(nw_db_ref owner, std::uint32_t id, nw_bytes_view_v1 name,
                                                 std::uint32_t codec, std::uint32_t nullable, std::uint64_t* result,
                                                 nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      output(result);
                      if (nullable > 1)
                          bad();
                      auto bytes = view(name);
                      const std::string text(bytes.begin(), bytes.end());
                      *result = query(owner,
                                      [&](auto& value)
                                      {
                                          CodecBindings bindings(value.source().profile);
                                          return value.parameter({id, text, bindings.get(codec, nullable == 1), codec});
                                      });
                  });
}
NW_FIXTURE_API nw_status nw_db_expr_binary_v1(nw_db_ref owner, std::uint32_t opcode, std::uint64_t left,
                                              std::uint64_t right, std::uint64_t* result, nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      output(result);
                      *result = query(owner, [&](auto& value) { return value.binary(opcode, left, right); });
                  });
}
NW_FIXTURE_API nw_status nw_db_expr_unary_v1(nw_db_ref owner, std::uint32_t opcode, std::uint64_t input,
                                             std::uint64_t* result, nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      output(result);
                      *result = query(owner, [&](auto& value) { return value.unary(opcode, input); });
                  });
}
NW_FIXTURE_API nw_status nw_db_expr_list_v1(nw_db_ref owner, std::uint32_t opcode, std::uint64_t input,
                                            nw_bytes_view_v1 bytes, std::uint64_t* result, nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      output(result);
                      *result = query(owner, [&](auto& value) { return value.list(opcode, input, view(bytes)); });
                  });
}
NW_FIXTURE_API nw_status nw_db_query_import_v1(nw_db_ref owner, nw_db_ref dependency, std::uint32_t kind,
                                               std::uint64_t* result, nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      output(result);
                      reference(dependency);
                      *result =
                          query(owner,
                                [&](auto& value) -> std::uint64_t
                                {
                                    switch (kind)
                                    {
                                        case 1:
                                            return value.import_model(get<ModelBinding>(dependency));
                                        case 2:
                                            return value.import_plan(get<std::shared_ptr<NativePlan>>(dependency));
                                        case 3:
                                            return value.import_cte(get<std::shared_ptr<NativeCte>>(dependency));
                                        default:
                                            wrong();
                                    }
                                });
                  });
}
NW_FIXTURE_API nw_status nw_db_query_clause_v1(nw_db_ref owner, std::uint32_t opcode, nw_bytes_view_v1 bytes,
                                               std::uint64_t* result, nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      output(result);
                      *result = query(owner, [&](auto& value) { return value.clause(opcode, view(bytes)); });
                  });
}
NW_FIXTURE_API nw_status nw_db_query_seal_v1(nw_db_ref owner, std::uint32_t terminal, nw_db_ref* result,
                                             nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      output(result);
                      auto plan = query(owner, [&](auto& value) { return value.seal(terminal); });
                      *result = release(owned(std::move(plan)));
                  });
}
NW_FIXTURE_API nw_status nw_db_query_clone_v1(nw_db_ref owner, nw_db_ref* result, nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      output(result);
                      auto value = query(owner, [](const auto& value) { return value.clone_selected(); });
                      *result = release(owned(std::make_shared<Query>(std::move(value))));
                  });
}
NW_FIXTURE_API nw_status nw_db_cte_create_v1(nw_db_ref declared, std::uint32_t table_id, nw_db_ref body,
                                             std::uint32_t recursive, nw_db_ref* result, nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      output(result);
                      auto cte =
                          query(body, [&](const auto& value)
                                { return NativeCte::create(get<ModelBinding>(declared), table_id, value, recursive); });
                      *result = release(owned(std::move(cte)));
                  });
}
NW_FIXTURE_API nw_status nw_db_execute_create_v1(nw_db_ref session, nw_db_ref owner, nw_bytes_view_v1 bytes,
                                                 nw_db_ref* result, nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      output(result);
                      auto origin = source(session, true);
                      auto plan = get<std::shared_ptr<NativePlan>>(owner);
                      auto execution = plan->render(origin->identity, view(bytes));
                      if (!plan->projection)
                          bad();
                      auto retained = copy(view(bytes));
                      auto cursor = owned(Cursor{*plan->projection, retained, std::move(execution), {}, plan, origin});
                      *result = operation(std::move(cursor), 2, std::move(retained));
                  });
}
NW_FIXTURE_API nw_status nw_db_relation_load_create_v1(nw_db_ref session, nw_db_ref owner, nw_bytes_view_v1 key,
                                                       std::uint32_t projection_id, nw_db_ref* result,
                                                       nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      output(result);
                      auto origin = source(session, true);
                      auto relation = get<std::shared_ptr<RelationBinding>>(owner);
                      ProjectionBinding projection;
                      {
                          std::lock_guard gate(origin->mutex);
                          auto it = origin->projections.find(projection_id);
                          if (it == origin->projections.end())
                              bad();
                          projection = it->second;
                      }
                      auto execution = compile_relation(*relation, origin->identity, view(key), projection,
                                                        QueryLimits{}, 1000000000);
                      auto retained = copy(view(key));
                      auto cursor = owned(Cursor{projection, retained, std::move(execution), relation, {}, origin});
                      *result = operation(std::move(cursor), 2, std::move(retained));
                  });
}
NW_FIXTURE_API nw_status nw_db_cursor_fetch_create_v1(nw_db_ref owner, std::uint32_t max_rows, std::uint64_t max_bytes,
                                                      nw_db_ref* result, nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      output(result);
                      if (max_rows == 0 || max_bytes == 0 || max_bytes > MAX_BYTES)
                          bad();
                      const auto& cursor = get<Cursor>(owner);
                      const auto& projection = cursor.projection;
                      Expectation expected{
                          2, projection.source.profile, projection.projection->hash(), 0, projection.id, {}, 0};
                      std::uint32_t id = 0;
                      for (const auto& field : projection.projection->fields())
                          expected.descriptors.push_back({id++, field.codec, projection.codecs->bound(field.codec)});
                      auto bytes = Batch::encode(expected, 0, {});
                      if (bytes.size() > max_bytes)
                          bad();
                      auto pending = std::make_shared<BindingOperation>();
                      pending->result_buffer = std::make_unique<Buffer>(std::move(bytes));
                      pending->retained = cursor.retained;
                      *result = release(owned(std::move(pending)));
                  });
}
NW_FIXTURE_API nw_status nw_db_close_create_v1(nw_db_ref owner, nw_db_ref* result, nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      output(result);
                      reference(owner);
                      *result = operation({}, 0, {});
                  });
}
NW_FIXTURE_API nw_status nw_db_snapshot_v1(nw_db_ref owner, nw_bytes_view_v1, nw_db_buffer** result,
                                           nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      output(result);
                      auto& value = reference(owner).value;
                      Bytes bytes;
                      if (auto* pending = std::get_if<std::shared_ptr<BindingOperation>>(&value))
                      {
                          std::lock_guard gate((*pending)->mutex);
                          bytes = (*pending)->retained;
                      }
                      else if (auto* plan = std::get_if<std::shared_ptr<NativePlan>>(&value))
                          bytes = (*plan)->parameter_schema();
                      else if (auto* q = std::get_if<std::shared_ptr<Query>>(&value))
                      {
                          std::lock_guard gate((*q)->mutex);
                          static constexpr const char* names[] = {"Mutable", "Sealed", "Submitted", "Poisoned",
                                                                  "Closed"};
                          const auto [nodes, retained, expanded] = (*q)->value->counts();
                          const auto text = std::string(names[static_cast<std::size_t>((*q)->value->state())]) + ":(" +
                                            std::to_string(nodes) + ", " + std::to_string(retained) + ", " +
                                            std::to_string(expanded) + ")";
                          bytes.assign(text.begin(), text.end());
                      }
                      else
                          wrong();
                      *result = reinterpret_cast<nw_db_buffer*>(new Buffer(std::move(bytes)));
                  });
}
NW_FIXTURE_API nw_status nw_db_operation_start_v1(nw_db_ref owner, const nw_operation_callbacks_v1* callbacks,
                                                  nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      auto operation = get<std::shared_ptr<BindingOperation>>(owner);
                      if (callbacks == nullptr || callbacks->struct_size != sizeof(nw_operation_callbacks_v1) ||
                          callbacks->flags != 0)
                          bad();
                      nw_status terminal;
                      {
                          std::lock_guard gate(operation->mutex);
                          if (operation->started)
                              wrong();
                          operation->started = true;
                          operation->terminal_canceled = operation->canceled;
                          operation->callback_active = true;
                          terminal = operation->terminal_canceled ? NW_CANCELED : NW_OK;
                      }
                      if (callbacks->on_terminal != nullptr)
                      {
                          const nw_error_info_v1 info{sizeof(nw_error_info_v1), 0, 0, 0, 0};
                          callbacks->on_terminal(callbacks->cookie, terminal, &info);
                      }
                      std::lock_guard gate(operation->mutex);
                      operation->callback_active = false;
                  });
}
NW_FIXTURE_API nw_status nw_db_operation_request_cancel_v1(nw_db_ref owner)
{
    return finish(nullptr,
                  [&]
                  {
                      auto operation = get<std::shared_ptr<BindingOperation>>(owner);
                      std::lock_guard gate(operation->mutex);
                      operation->canceled = true;
                  });
}
NW_FIXTURE_API nw_status nw_db_operation_take_owner_v1(nw_db_ref owner, std::uint32_t kind, nw_db_ref* result,
                                                       nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      output(result);
                      auto operation = get<std::shared_ptr<BindingOperation>>(owner);
                      std::lock_guard gate(operation->mutex);
                      if (!operation->started || operation->taken || operation->terminal_canceled ||
                          !operation->result_owner || kind != operation->result_kind)
                          wrong();
                      operation->taken = true;
                      *result = release(std::move(operation->result_owner));
                  });
}
NW_FIXTURE_API nw_status nw_db_operation_take_buffer_v1(nw_db_ref owner, nw_db_buffer** result, nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      output(result);
                      auto operation = get<std::shared_ptr<BindingOperation>>(owner);
                      std::lock_guard gate(operation->mutex);
                      if (!operation->started || operation->taken || operation->terminal_canceled ||
                          !operation->result_buffer)
                          wrong();
                      operation->taken = true;
                      *result = reinterpret_cast<nw_db_buffer*>(operation->result_buffer.release());
                  });
}
NW_FIXTURE_API nw_status nw_db_operation_try_retire_v1(nw_db_ref owner)
{
    return finish(nullptr,
                  [&]
                  {
                      auto operation = get<std::shared_ptr<BindingOperation>>(owner);
                      std::lock_guard gate(operation->mutex);
                      if (!operation->started || operation->callback_active)
                          throw Error(NW_PENDING, "not started or callback active");
                      operation->retired = true;
                  });
}
NW_FIXTURE_API nw_status nw_db_buffer_view_v1(nw_db_buffer* owner, nw_bytes_view_v1* result, nw_error_info_v1* error)
{
    return finish(error,
                  [&]
                  {
                      output(result);
                      if (owner == nullptr)
                          bad();
                      auto& buffer = *reinterpret_cast<Buffer*>(owner);
                      *result = {buffer.bytes.data(), buffer.bytes.size()};
                  });
}
NW_FIXTURE_API void nw_db_owner_release_v1(nw_db_ref owner)
{
    delete reinterpret_cast<Owner*>(owner);
}
NW_FIXTURE_API void nw_db_operation_release_v1(nw_db_ref owner)
{
    if (owner != nullptr)
    {
        if (auto* operation = std::get_if<std::shared_ptr<BindingOperation>>(&reference(owner).value))
        {
            std::lock_guard gate((*operation)->mutex);
            if ((*operation)->started && !(*operation)->retired)
                std::abort();
        }
        delete reinterpret_cast<Owner*>(owner);
    }
}
NW_FIXTURE_API void nw_db_buffer_release_v1(nw_db_buffer* owner)
{
    delete reinterpret_cast<Buffer*>(owner);
}
NW_FIXTURE_API std::uint64_t nw_test_live_v1(std::uint32_t kind)
{
    switch (kind)
    {
        case 1:
            return owners.load();
        case 2:
            return operations.load();
        case 3:
            return buffers.load();
        default:
            return 0;
    }
}
NW_FIXTURE_API nw_status nw_test_open_v1(nw_db_ref* source_result, nw_db_ref* session_result)
{
    if (source_result == nullptr || session_result == nullptr)
        return NW_INVALID_ARGUMENT;
    *source_result = nullptr;
    *session_result = nullptr;
    const std::uint8_t profile = 1;
    auto status = nw_db_source_create_v1({&profile, 1}, source_result, nullptr);
    if (status == NW_OK)
    {
        status = nw_test_session_v1(*source_result, session_result, nullptr);
        if (status != NW_OK)
        {
            nw_db_owner_release_v1(*source_result);
            *source_result = nullptr;
        }
    }
    return status;
}
