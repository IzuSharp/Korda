// SPDX-License-Identifier: MIT
#pragma once

#include "nw/contracts.hpp"

#include <atomic>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <set>
#include <string>
#include <string_view>
#include <tuple>
#include <variant>
#include <vector>

namespace nw
{

struct QueryLimits
{
    std::size_t nodes = 4096;
    std::uint32_t depth = 64;
    std::size_t bytes = 4 * 1024 * 1024;
    std::size_t parameters = 4096;
    void validate() const;
};

struct Capabilities
{
    bool metadata = false;
    bool scalar_codecs = false;
    bool explicit_relationships = false;
    bool recursive_cte = false;
    bool set_operations = false;
    bool query = false;
    bool null_safe_equal = false;
    bool windows = false;
    bool cte = false;
    bool row_lock = false;
    bool prepared = false;
    bool command = false;
    static Capabilities from_mask(std::uint64_t mask, Profile profile);
};

class CodecBindings
{
  public:
    explicit CodecBindings(Profile profile, const std::vector<std::pair<std::uint32_t, Codec>>& configured = {});
    Codec get(std::uint32_t bound, bool nullable) const;
    std::uint32_t bound(const Codec& codec) const;
    Profile profile() const noexcept
    {
        return profile_;
    }

  private:
    Profile profile_;
    std::map<std::uint32_t, Codec> entries_;
};

struct ModelBinding
{
    SourceIdentity source;
    std::shared_ptr<const Model> model;
    std::shared_ptr<const CodecBindings> codecs;
    ModelBinding() = default;
    ModelBinding(SourceIdentity source, std::shared_ptr<const Model> model,
                 std::shared_ptr<const CodecBindings> codecs);
    void ensure_source(const SourceIdentity& expected) const;
};

struct ProjectionBinding
{
    SourceIdentity source;
    std::uint32_t id = 0;
    std::shared_ptr<const Projection> projection;
    std::shared_ptr<const CodecBindings> codecs;
    ProjectionBinding() = default;
    ProjectionBinding(SourceIdentity source, std::uint32_t id, std::shared_ptr<const Projection> projection,
                      std::shared_ptr<const CodecBindings> codecs);
};

ProjectionBinding projection_record(ByteView bytes, SourceIdentity source, std::uint32_t id,
                                    std::shared_ptr<const CodecBindings> codecs,
                                    const std::vector<std::uint64_t>* inline_tokens = nullptr);

struct ParameterIdentity
{
    std::uint32_t id = 0;
    std::string name;
    Codec codec;
    std::uint32_t bound_codec = 0;
    void validate(const CodecBindings& codecs) const;
    bool operator==(const ParameterIdentity&) const = default;
};

class ParameterRegistry
{
  public:
    const SourceIdentity source;
    ParameterRegistry(SourceIdentity source, std::size_t maximum);
    void register_identity(const ParameterIdentity& identity);
    void register_identities(const std::map<std::uint32_t, ParameterIdentity>& identities);

  private:
    std::size_t maximum_;
    std::mutex mutex_;
    std::map<std::uint32_t, ParameterIdentity> identities_;
};

enum class QueryState
{
    Mutable,
    Sealed,
    Submitted,
    Poisoned,
    Closed
};

struct ExpressionType
{
    Codec codec;
    std::uint32_t bound_codec = 0;
    std::uint32_t depth = 1;
    bool aggregate = false;
    bool windowable = false;
    std::set<std::uint32_t> dependencies;
};

namespace query_detail
{
struct QueryData;
struct CompiledPlan;
struct Slot;
} // namespace query_detail

class NativePlan;
struct NativeCte;
class ShapeCache;

struct RenderedQuery
{
    std::string sql;
    std::vector<Scalar> arguments;
    Expectation parameter_expectation;
    std::optional<ProjectionBinding> projection;
    std::uint32_t terminal = 0;
    std::uint64_t timeout_ns = 0;
    std::size_t parameter_wire_bytes = 0;
};

// This object owns no runtime or connection. The Host supplies fail-fast single-writer admission.
class NativeQuery
{
  public:
    NativeQuery(ModelBinding root, std::shared_ptr<ParameterRegistry> registry, std::uint32_t table_id,
                QueryLimits limits, Capabilities capabilities, std::uint64_t timeout_ns);
    ~NativeQuery();
    NativeQuery(NativeQuery&&) noexcept;
    NativeQuery& operator=(NativeQuery&&) noexcept;
    NativeQuery(const NativeQuery&) = delete;
    NativeQuery& operator=(const NativeQuery&) = delete;

    SourceIdentity source() const;
    const ModelBinding& root() const;
    QueryState state() const;
    std::tuple<std::size_t, std::size_t, std::size_t> counts() const;
    ExpressionType expression_type(std::uint64_t token) const;
    void poison();
    void close();
    std::shared_ptr<NativeQuery> clone_selected() const;
    std::uint64_t column(std::uint64_t alias, std::uint32_t column_id);
    std::uint64_t value(ByteView one_cell);
    std::uint64_t parameter(ParameterIdentity identity);
    std::uint64_t binary(std::uint32_t opcode, std::uint64_t left, std::uint64_t right);
    std::uint64_t unary(std::uint32_t opcode, std::uint64_t input);
    std::uint64_t list(std::uint32_t opcode, std::uint64_t input, ByteView cells);
    std::uint64_t import_model(ModelBinding model);
    std::uint64_t import_plan(std::shared_ptr<const NativePlan> plan);
    std::uint64_t import_cte(std::shared_ptr<const NativeCte> cte);
    std::uint64_t clause(std::uint32_t opcode, ByteView operands);
    std::shared_ptr<NativePlan> seal(std::uint32_t terminal);

  private:
    friend struct NativeCte;
    friend class QueryCommandReplay;
    struct Impl;
    std::unique_ptr<Impl> impl_;
    explicit NativeQuery(std::unique_ptr<Impl> implementation);
    std::uint64_t clause(std::uint32_t opcode, ByteView operands, std::span<const std::uint64_t> command_slots,
                         const Fields* text_fields = nullptr);
};

class CompiledShape
{
  public:
    std::string sql;
    Expectation expectation;
    Bytes identity;
    std::size_t charge = 0;
    std::size_t retained_bytes() const noexcept
    {
        return charge;
    }
};

// Published once by seal; public metadata is immutable by convention inside the native module.
class NativePlan
{
  public:
    SourceIdentity source;
    ModelBinding root;
    std::uint32_t terminal = 0;
    bool prepared_supported = false;
    std::map<std::uint32_t, ParameterIdentity> parameters;
    std::optional<ProjectionBinding> projection;
    std::uint64_t timeout_ns = 0;
    std::size_t nodes = 0;
    std::uint32_t depth = 0;
    std::size_t owned_bytes = 0;

    ~NativePlan();
    bool requires_transaction() const;
    Bytes parameter_schema() const;
    Expectation parameter_expectation() const;
    RenderedQuery render(SourceIdentity session, ByteView values, ShapeCache* cache = nullptr) const;
    std::pair<std::string, std::vector<std::shared_ptr<const query_detail::Slot>>> render_shape() const;
    std::shared_ptr<const CompiledShape> compiled_shape() const;
    std::size_t retained_literal_bytes() const;
    std::size_t expanded_nodes() const;
    const query_detail::QueryData& data() const;

  private:
    friend class NativeQuery;
    friend class QueryCommandReplay;
    friend struct NativeCte;
    struct Impl;
    std::unique_ptr<Impl> impl_;
    NativePlan();
    const query_detail::CompiledPlan& compiled() const;
};

struct NativeCte
{
    SourceIdentity source;
    ModelBinding declared;
    std::shared_ptr<const NativePlan> body;
    bool recursive = false;
    std::string name;
    static std::shared_ptr<NativeCte> create(ModelBinding declared, std::uint32_t table_id, const NativeQuery& body,
                                             std::uint32_t recursive);
};

// The sidecar is resolved by the Host handle registry before decoding. The
// command contains only bounded local indexes, never native pointers/tokens.
using QueryCommandDependency =
    std::variant<ModelBinding, std::shared_ptr<const NativePlan>, std::shared_ptr<const NativeCte>>;
struct QueryCommandContext
{
    std::shared_ptr<ParameterRegistry> parameters;
    QueryLimits limits;
    Capabilities capabilities;
    std::uint64_t timeout_ns = 0;
};
std::shared_ptr<NativePlan> compile_query_command(ByteView command,
                                                  std::span<const QueryCommandDependency> dependencies,
                                                  const QueryCommandContext& context, std::uint32_t terminal);
Bytes render_query_command(ByteView command, std::span<const QueryCommandDependency> dependencies,
                           const QueryCommandContext& context);
std::shared_ptr<NativeCte> create_query_command_cte(ByteView command,
                                                    std::span<const QueryCommandDependency> dependencies,
                                                    const QueryCommandContext& context, ModelBinding declared,
                                                    std::uint32_t table_id, std::uint32_t recursive);

// Source-owned LRU. Only cache bookkeeping is serialized; compilation and
// execution parameter processing remain outside this lock.
class ShapeCache
{
  public:
    ShapeCache(std::size_t maximum_entries, std::size_t maximum_bytes, Hash abi);
    ~ShapeCache();
    std::shared_ptr<const CompiledShape> acquire(const NativePlan& plan);
    std::pair<std::size_t, std::size_t> counts() const;
    void clear();

  private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

struct SqlTemplateResult
{
    std::string sql;
    // Original declaration IDs in occurrence order; repeated names retain the same index.
    std::vector<std::uint32_t> parameter_ids;
};

std::string decode_interpolated_sql(ByteView bytes);
SqlTemplateResult render_sql_template(std::string_view sql, const std::map<std::string, std::uint32_t>& names,
                                      Profile profile, std::uint32_t command_kind);

} // namespace nw
