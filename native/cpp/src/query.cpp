// SPDX-License-Identifier: MIT
// Runtime query semantics are migrated from NativeWeave 465cd0f.
// SQL rendering follows the supported SeaQuery 1.0.2 subset at
// f2d5e04d30919c342da1f8845ff1fde46e3308fc (MIT, Copyright 2020 Tsang Hao Fung).
// See THIRD_PARTY_NOTICES.md. No Rust code or runtime is linked by this implementation.
#include "nw/query.hpp"

#include <algorithm>
#include <array>
#include <cctype>
#include <deque>
#include <exception>
#include <limits>
#include <numeric>
#include <utility>

namespace nw
{
namespace
{
// Rust's BindingError -> Host Error conversion assigns domain code 11, including
// failures from the shared contract parser. The function try blocks below keep
// that module boundary; ABI borrow/owner checks remain outside and retain their
// distinct Host codes. The explicit-SQL lexer at the end uses Host codes 0/6.
[[noreturn]] void invalid(std::string message)
{
    throw Error(NW_INVALID_ARGUMENT, std::move(message));
}
[[noreturn]] void state_error(std::string message)
{
    throw Error(NW_INVALID_STATE, std::move(message));
}
[[noreturn]] void unsupported(std::string message)
{
    throw Error(NW_NOT_SUPPORTED, std::move(message));
}
[[noreturn]] void capacity(std::string message)
{
    throw Error(NW_DOMAIN_ERROR, std::move(message));
}
[[noreturn]] void binding_error(const Error& error)
{
    // A compiled plan can retain an exception_ptr for concurrent callers.
    // Convert a copy rather than mutating that shared exception object.
    auto converted = error;
    converted.code = 11;
    throw converted;
}

Bytes bytes_of(std::string_view value)
{
    return Bytes(value.begin(), value.end());
}
bool numeric(std::uint32_t id)
{
    return (id >= 1 && id <= 3) || (id >= 11 && id <= 13) || id == 15;
}
Codec scalar(std::uint32_t id, bool nullable = false)
{
    return Codec{id, std::nullopt, nullable};
}
bool ascii_alpha(unsigned char c)
{
    return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
}
bool ascii_digit(unsigned char c)
{
    return c >= '0' && c <= '9';
}
bool ascii_alnum(unsigned char c)
{
    return ascii_alpha(c) || ascii_digit(c);
}
bool ascii_space(unsigned char c)
{
    return c == ' ' || (c >= '\t' && c <= '\r');
}

std::vector<std::uint32_t> codepoints(std::string_view input)
{
    std::vector<std::uint32_t> result;
    for (std::size_t i = 0; i < input.size();)
    {
        auto c = static_cast<unsigned char>(input[i++]);
        std::uint32_t cp = 0;
        std::size_t extra = 0;
        std::uint32_t minimum = 0;
        if (c < 0x80)
            cp = c;
        else if (c >= 0xC2 && c <= 0xDF)
        {
            cp = c & 0x1F;
            extra = 1;
            minimum = 0x80;
        }
        else if (c >= 0xE0 && c <= 0xEF)
        {
            cp = c & 0x0F;
            extra = 2;
            minimum = 0x800;
        }
        else if (c >= 0xF0 && c <= 0xF4)
        {
            cp = c & 7;
            extra = 3;
            minimum = 0x10000;
        }
        else
            invalid("UTF8 field");
        if (extra > input.size() - i)
            invalid("UTF8 field");
        for (std::size_t n = 0; n < extra; ++n)
        {
            c = static_cast<unsigned char>(input[i++]);
            if ((c & 0xC0) != 0x80)
                invalid("UTF8 field");
            cp = (cp << 6) | (c & 0x3F);
        }
        if (cp < minimum || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
            invalid("UTF8 field");
        result.push_back(cp);
    }
    return result;
}
std::string utf8(std::uint32_t cp)
{
    if (cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
        invalid("Unicode scalar");
    std::string out;
    if (cp < 0x80)
        out.push_back(static_cast<char>(cp));
    else if (cp < 0x800)
    {
        out.push_back(static_cast<char>(0xC0 | (cp >> 6)));
        out.push_back(static_cast<char>(0x80 | (cp & 63)));
    }
    else if (cp < 0x10000)
    {
        out.push_back(static_cast<char>(0xE0 | (cp >> 12)));
        out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 63)));
        out.push_back(static_cast<char>(0x80 | (cp & 63)));
    }
    else
    {
        out.push_back(static_cast<char>(0xF0 | (cp >> 18)));
        out.push_back(static_cast<char>(0x80 | ((cp >> 12) & 63)));
        out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 63)));
        out.push_back(static_cast<char>(0x80 | (cp & 63)));
    }
    return out;
}
void identifier(std::string_view value)
{
    if (value.empty() || value.size() > 128)
        invalid("alias/name bounds");
    for (auto cp : codepoints(value))
        if (cp < 32 || (cp >= 127 && cp <= 159))
            invalid("alias/name bounds");
}
std::string quote_id(std::string_view value)
{
    std::string out = "\"";
    for (char c : value)
    {
        out.push_back(c);
        if (c == '"')
            out.push_back(c);
    }
    return out + '"';
}
std::string table_sql(const Model& model)
{
    return model.schema() ? quote_id(*model.schema()) + "." + quote_id(model.table()) : quote_id(model.table());
}
template <class T> void append_integer(Bytes& target, T value)
{
    for (std::size_t i = 0; i < sizeof(T); ++i)
        target.push_back(static_cast<std::uint8_t>(value >> (i * 8)));
}
void append_bytes(Bytes& target, ByteView value)
{
    append_integer<std::uint64_t>(target, value.size());
    target.insert(target.end(), value.begin(), value.end());
}
void append_text(Bytes& target, std::string_view value)
{
    append_bytes(target, bytes_of(value));
}
void merge_parameter(std::map<std::uint32_t, ParameterIdentity>& target, const ParameterIdentity& identity,
                     std::size_t maximum)
{
    if (auto it = target.find(identity.id); it != target.end())
    {
        if (it->second != identity)
            invalid("parameter ID metadata collision");
        return;
    }
    for (const auto& [id, existing] : target)
    {
        (void)id;
        if (existing.name == identity.name)
            invalid("different parameter IDs share a composed name");
    }
    if (target.size() == maximum)
        capacity("parameter count limit");
    target.emplace(identity.id, identity);
}
std::vector<std::uint64_t> token_vector(ByteView bytes)
{
    if (bytes.empty() || bytes.size() % 8 != 0 || bytes.size() / 8 > MAX_NODES)
        invalid("token vector count");
    std::vector<std::uint64_t> result;
    for (std::size_t i = 0; i < bytes.size(); i += 8)
        result.push_back(read_u64(bytes, i));
    return result;
}
} // namespace

void QueryLimits::validate() const
{
    if (nodes == 0 || nodes > MAX_NODES || depth == 0 || depth > 256 || bytes == 0 || bytes > MAX_BYTES ||
        parameters == 0 || parameters > MAX_NODES)
        invalid("invalid binding limits");
}
Capabilities Capabilities::from_mask(std::uint64_t mask, Profile profile)
{
    if ((mask >> 25) != 0)
        invalid("reserved database capability bits");
    auto has = [mask](unsigned bit) { return (mask & (std::uint64_t{1} << bit)) != 0; };
    return {has(0), has(23), has(17), has(21), has(22),
            has(1), has(12), has(10), has(9),  has(11) && profile == Profile::PostgreSql,
            has(18)};
}
CodecBindings::CodecBindings(Profile profile, const std::vector<std::pair<std::uint32_t, Codec>>& configured)
try : profile_(profile)
{
    if (configured.size() > MAX_NODES)
        invalid("codec binding count");
    for (std::uint32_t id = 1; id <= 17; ++id)
    {
        if (id == 13 || id == 15 || (profile == Profile::PostgreSql && id == 14))
            continue;
        entries_.emplace(id, scalar(id));
    }
    for (const auto& [bound, codec] : configured)
    {
        codec.validate(profile);
        if (bound < 1024 || !codec.configuration || codec.nullable || entries_.contains(bound))
            invalid("invalid configured codec binding");
        for (const auto& [id, old] : entries_)
        {
            (void)id;
            if (old.same_scalar(codec))
                invalid("invalid configured codec binding");
        }
        entries_.emplace(bound, codec);
    }
}
catch (const Error& error)
{
    binding_error(error);
}
Codec CodecBindings::get(std::uint32_t bound, bool nullable) const
{
    auto it = entries_.find(bound);
    if (it == entries_.end())
        invalid("unknown source codec");
    auto value = it->second;
    value.nullable = nullable;
    return value;
}
std::uint32_t CodecBindings::bound(const Codec& codec) const
try
{
    codec.validate(profile_);
    for (const auto& [id, value] : entries_)
        if (value.same_scalar(codec))
            return id;
    invalid("unregistered codec identity");
}
catch (const Error& error)
{
    binding_error(error);
}
ModelBinding::ModelBinding(SourceIdentity identity, std::shared_ptr<const Model> value,
                           std::shared_ptr<const CodecBindings> bindings)
    : source(identity), model(std::move(value)), codecs(std::move(bindings))
{
    source.validate();
    if (!model || !codecs || source.profile != codecs->profile())
        invalid("source codec profile");
    for (const auto& column : model->columns())
        (void)codecs->bound(column.codec);
}
void ModelBinding::ensure_source(const SourceIdentity& expected) const
{
    if (source != expected)
        state_error("model belongs to another source generation");
}
ProjectionBinding::ProjectionBinding(SourceIdentity identity, std::uint32_t identity_id,
                                     std::shared_ptr<const Projection> value,
                                     std::shared_ptr<const CodecBindings> bindings)
    : source(identity), id(identity_id), projection(std::move(value)), codecs(std::move(bindings))
{
    source.validate();
    if (id == 0 || !projection || !codecs || source.profile != codecs->profile())
        invalid("source projection identity");
    for (const auto& field : projection->fields())
        (void)codecs->bound(field.codec);
}
ProjectionBinding projection_record(ByteView bytes, SourceIdentity source, std::uint32_t id,
                                    std::shared_ptr<const CodecBindings> codecs,
                                    const std::vector<std::uint64_t>* inline_tokens)
try
{
    auto fields = Fields::parse(bytes, 13, {{1, 5, true}, {2, 5, true}});
    const auto encoded_records = fields.bytes(1);
    // Binding sequences require one to MAX_NODES records. Host parameter
    // sequences have a different zero/capacity policy; do not reuse that policy.
    if (encoded_records.size() < 4 || read_u32(encoded_records, 0) == 0 || read_u32(encoded_records, 0) > MAX_NODES)
        invalid("record sequence count");
    auto records = sequence(encoded_records);
    if (inline_tokens && inline_tokens->size() != records.size())
        invalid("inline projection arity");
    std::string text = "{\"version\":1,\"fields\":[";
    bool scalar_projection = false;
    for (std::size_t ordinal = 0; ordinal < records.size(); ++ordinal)
    {
        auto f = Fields::parse(records[ordinal], 22,
                               {{1, 1, true}, {2, 4, true}, {3, 1, true}, {4, 1, true}, {5, 2, false}});
        if (f.u32(1) != ordinal)
            invalid("projection ordinal");
        if (f.get(5) && (!inline_tokens || f.u64(5) != inline_tokens->at(ordinal)))
            invalid("projection token mismatch");
        auto name = f.text(2);
        scalar_projection = records.size() == 1 && name.empty();
        auto codec = codecs->get(f.u32(3), f.boolean(4));
        if (ordinal != 0)
            text += ',';
        text += "{\"ordinal\":" + std::to_string(ordinal) + ",\"name\":" + json_quote(name) +
                ",\"codec\":" + std::to_string(codec.id) + ",\"codec_config\":" + codec.configuration.value_or("null") +
                ",\"nullable\":" + (codec.nullable ? "true" : "false") + '}';
    }
    text += "]}";
    if (scalar_projection && inline_tokens)
        invalid("empty alias is reserved for registered scalar projections");
    auto value = scalar_projection ? Projection::parse_scalar(bytes_of(text), source.profile)
                                   : Projection::parse(bytes_of(text), source.profile);
    auto expected = value.hash();
    auto actual = fields.bytes(2);
    if (actual.size() != expected.size() || !std::equal(actual.begin(), actual.end(), expected.begin()))
        invalid("projection logical hash");
    return {source, id, std::make_shared<const Projection>(std::move(value)), std::move(codecs)};
}
catch (const Error& error)
{
    binding_error(error);
}
void ParameterIdentity::validate(const CodecBindings& codecs) const
{
    if (id == 0 || name.empty() || name.size() > 128)
        invalid("invalid prepared parameter identity");
    for (std::size_t i = 0; i < name.size(); ++i)
    {
        auto c = static_cast<unsigned char>(name[i]);
        if (c != '_' && !ascii_alpha(c) && !(i > 0 && ascii_digit(c)))
            invalid("invalid prepared parameter identity");
    }
    if (codecs.get(bound_codec, codec.nullable) != codec)
        invalid("parameter codec mismatch");
}
ParameterRegistry::ParameterRegistry(SourceIdentity identity, std::size_t maximum) : source(identity), maximum_(maximum)
{
    source.validate();
    if (maximum == 0 || maximum > MAX_NODES)
        invalid("parameter registry bound");
}
void ParameterRegistry::register_identity(const ParameterIdentity& identity)
{
    std::lock_guard lock(mutex_);
    if (auto old = identities_.find(identity.id); old != identities_.end())
    {
        if (old->second != identity)
            invalid("source parameter ID reused with different identity");
        return;
    }
    if (identities_.size() == maximum_)
        capacity("source parameter count");
    identities_.emplace(identity.id, identity);
}

namespace query_detail
{
struct Slot
{
    std::uint64_t id = 0;
    Codec codec;
    std::uint32_t bound_codec = 0;
    std::optional<std::uint32_t> parameter;
    std::optional<Bytes> literal;
};
using SlotPtr = std::shared_ptr<const Slot>;
struct Expr;
using ExprPtr = std::shared_ptr<const Expr>;
struct Order
{
    ExprPtr expression;
    std::uint32_t direction = 0;
    std::uint32_t nulls = 0;
};
struct Window
{
    std::vector<ExprPtr> partitions;
    std::vector<Order> orders;
    std::optional<std::pair<std::uint32_t, std::uint32_t>> frame;
    std::size_t nodes = 1;
};
enum class ExprKind
{
    Column,
    Slot,
    Binary,
    Not,
    Function,
    Tuple,
    Constant,
    Case,
    Subquery,
    Over,
    Cast
};
// Internal binary opcodes 30+ represent the small renderer-only operator subset.
enum : std::uint32_t
{
    Is = 30,
    IsNot = 31,
    In = 32,
    NotIn = 33,
    Between = 34,
    Like = 35,
    Escape = 36,
    Collate = 37
};
struct Expr
{
    ExprKind kind = ExprKind::Constant;
    std::uint32_t op = 0;
    std::string text;
    std::string qualifier;
    std::vector<ExprPtr> children;
    SlotPtr slot;
    std::shared_ptr<const NativePlan> plan;
    std::shared_ptr<const Window> window;
};
struct Expression
{
    ExpressionType type;
    ExprPtr ast;
    std::size_t nodes = 1;
    std::optional<std::pair<ExprPtr, std::string>> window_cast;
};
struct Alias
{
    ModelBinding model;
    std::string name;
    std::string marker;
    bool left = false;
    bool provisional = true;
};
enum class NodeKind
{
    Expression,
    Model,
    Alias,
    Window,
    Plan,
    Cte,
    Clause
};
struct Node
{
    NodeKind kind = NodeKind::Clause;
    bool valid = true;
    Expression expression;
    ModelBinding model;
    Alias alias;
    Window window;
    std::shared_ptr<const NativePlan> plan;
    std::shared_ptr<const NativeCte> cte;
};
struct Join
{
    Alias alias;
    ExprPtr condition;
};
struct Selected
{
    ExprPtr expression;
    std::string name;
    std::uint32_t node = 0;
    bool aggregate = false;
};
struct QueryData
{
    std::vector<Node> nodes;
    std::vector<Bytes> values;
    std::size_t bytes = 0;
    std::size_t retained_nodes = 0;
    std::size_t retained_native = 0;
    std::uint32_t imported_depth = 0;
    std::map<std::uint32_t, ParameterIdentity> parameters;
    std::optional<ProjectionBinding> projection;
    std::optional<std::uint32_t> reserved;
    std::uint64_t timeout_ns = 0;
    std::set<std::uint32_t> assigned;
    std::set<std::string> cte_names;
    bool has_join = false;
    bool grouped = false;
    bool has_paging = false;
    bool has_where = false;
    bool advanced_mutation = false;
    bool distinct = false;
    bool requires_transaction = false;
    bool recursive_cte = false;
    std::size_t statement_nodes = 1;
    std::size_t aggregate_nodes = 0;
    std::map<std::uint64_t, SlotPtr> slots;
    std::vector<Selected> selects;
    std::vector<Join> joins;
    std::vector<ExprPtr> predicates;
    std::vector<ExprPtr> having;
    std::vector<ExprPtr> groups;
    std::vector<Order> orders;
    std::optional<std::uint64_t> limit;
    std::optional<std::uint64_t> offset;
    std::optional<std::pair<std::uint32_t, std::uint32_t>> lock;
    std::vector<std::pair<std::uint32_t, std::shared_ptr<const NativePlan>>> sets;
    std::vector<std::shared_ptr<const NativeCte>> with;
    std::vector<std::shared_ptr<const NativePlan>> subqueries;
    std::vector<std::pair<std::string, ExprPtr>> assignments;

    std::size_t expanded() const
    {
        auto result = statement_nodes + retained_native;
        for (const auto& node : nodes)
        {
            if (node.kind == NodeKind::Expression)
                result += node.expression.nodes;
            if (node.valid && node.kind == NodeKind::Window)
                result += node.window.nodes;
        }
        return result;
    }
    std::size_t literal_bytes() const
    {
        std::size_t total = 0;
        for (const auto& [id, slot] : slots)
        {
            (void)id;
            if (!slot->parameter && slot->literal)
                total += slot->literal->size();
        }
        return total;
    }
    void native_quota(std::size_t extra, std::size_t extra_bytes, const QueryLimits& limits) const
    {
        if (expanded() > limits.nodes || extra > limits.nodes - expanded() || literal_bytes() > limits.bytes ||
            extra_bytes > limits.bytes - literal_bytes())
            capacity("expanded native query/value budget exhausted");
    }
    void quota(std::size_t extra_bytes, std::uint32_t depth, const QueryLimits& limits) const
    {
        if (nodes.size() + retained_nodes >= limits.nodes || bytes > limits.bytes ||
            extra_bytes > limits.bytes - bytes || depth > limits.depth)
            capacity("query node/depth/byte limit");
    }
    std::uint64_t append(Node node, std::size_t extra_bytes, std::uint32_t generation, const QueryLimits& limits)
    {
        quota(extra_bytes, node.kind == NodeKind::Expression ? node.expression.type.depth : 0, limits);
        nodes.push_back(std::move(node));
        bytes += extra_bytes;
        return (static_cast<std::uint64_t>(generation) << 32) | nodes.size();
    }
};
struct CompiledPlan
{
    std::shared_ptr<const CompiledShape> shape;
    std::vector<SlotPtr> arguments;
};
} // namespace query_detail

namespace
{
using namespace query_detail;
std::atomic<std::uint32_t> next_arena{1};
std::atomic<std::uint64_t> next_slot{1};
std::uint32_t new_generation()
{
    auto current = next_arena.load(std::memory_order_relaxed);
    do
    {
        if (current == std::numeric_limits<std::uint32_t>::max())
            capacity("arena generation exhausted");
    } while (!next_arena.compare_exchange_weak(current, current + 1, std::memory_order_relaxed));
    return current;
}
SlotPtr make_slot(Codec codec, std::uint32_t bound, std::optional<Bytes> literal,
                  std::optional<std::uint32_t> parameter = std::nullopt)
{
    auto current = next_slot.load(std::memory_order_relaxed);
    do
    {
        if (current >= (std::uint64_t{1} << 63) - 1)
            capacity("native placeholder identity exhausted");
    } while (!next_slot.compare_exchange_weak(current, current + 1, std::memory_order_relaxed));
    return std::make_shared<const Slot>(Slot{current, std::move(codec), bound, parameter, std::move(literal)});
}
ExprPtr expression(ExprKind kind, std::vector<ExprPtr> children = {}, std::uint32_t op = 0, std::string text = {})
{
    auto value = std::make_shared<Expr>();
    value->kind = kind;
    value->children = std::move(children);
    value->op = op;
    value->text = std::move(text);
    return value;
}
ExprPtr slot_expression(SlotPtr slot)
{
    auto value = std::make_shared<Expr>();
    value->kind = ExprKind::Slot;
    value->slot = std::move(slot);
    return value;
}
ExprPtr column_expression(std::string qualifier, std::string name)
{
    auto value = std::make_shared<Expr>();
    value->kind = ExprKind::Column;
    value->qualifier = std::move(qualifier);
    value->text = std::move(name);
    return value;
}
ExprPtr over_expression(ExprPtr input, const Window& window)
{
    auto value = std::make_shared<Expr>();
    value->kind = ExprKind::Over;
    value->children.push_back(std::move(input));
    value->window = std::make_shared<const Window>(window);
    return value;
}
bool same_expression(const ExprPtr& a, const ExprPtr& b)
{
    if (a == b)
        return true;
    if (!a || !b || a->kind != b->kind || a->op != b->op || a->text != b->text || a->qualifier != b->qualifier ||
        a->children.size() != b->children.size())
        return false;
    if (static_cast<bool>(a->slot) != static_cast<bool>(b->slot) || (a->slot && a->slot->id != b->slot->id))
        return false;
    if (a->plan != b->plan)
        return false;
    for (std::size_t i = 0; i < a->children.size(); ++i)
        if (!same_expression(a->children[i], b->children[i]))
            return false;
    if (static_cast<bool>(a->window) != static_cast<bool>(b->window))
        return false;
    if (a->window)
    {
        const auto& aw = *a->window;
        const auto& bw = *b->window;
        if (aw.partitions.size() != bw.partitions.size() || aw.orders.size() != bw.orders.size() ||
            aw.frame != bw.frame)
            return false;
        for (std::size_t i = 0; i < aw.partitions.size(); ++i)
            if (!same_expression(aw.partitions[i], bw.partitions[i]))
                return false;
        for (std::size_t i = 0; i < aw.orders.size(); ++i)
            if (aw.orders[i].direction != bw.orders[i].direction || aw.orders[i].nulls != bw.orders[i].nulls ||
                !same_expression(aw.orders[i].expression, bw.orders[i].expression))
                return false;
    }
    return true;
}
struct DecodedValues
{
    Codec codec;
    std::uint32_t bound;
    std::vector<std::optional<Bytes>> cells;
};
DecodedValues decode_values(const ModelBinding& root, ByteView bytes, std::optional<std::uint32_t> exact,
                            const QueryLimits& limits)
try
{
    if (bytes.size() < 112 || bytes.size() > limits.bytes)
        invalid("expression wire size");
    const auto bound = read_u32(bytes, 104);
    const auto flags = read_u16(bytes, 102);
    if (flags > 1)
        invalid("value flags");
    auto codec = root.codecs->get(bound, flags == 1);
    Expectation expected{3, root.source.profile, {}, 0, 0, {{1, codec, bound}}, exact};
    auto batch = Batch::parse(bytes, expected);
    if (batch.rows() > limits.parameters)
        capacity("expression collection size");
    DecodedValues result{codec, bound, {}};
    for (std::uint32_t row = 0; row < batch.rows(); ++row)
    {
        auto cell = batch.cell(row, 0);
        result.cells.push_back(cell ? std::optional<Bytes>(Bytes(cell->begin(), cell->end())) : std::nullopt);
    }
    return result;
}
catch (const Error& error)
{
    binding_error(error);
}
std::set<std::uint32_t> merged_dependencies(const ExpressionType& a, const ExpressionType& b)
{
    auto value = a.dependencies;
    value.insert(b.dependencies.begin(), b.dependencies.end());
    return value;
}
void direction(std::uint32_t order, std::uint32_t nulls)
{
    if (order > 1 || nulls > 1)
        invalid("order direction/null placement");
}
void import_native(QueryData& data, const NativePlan& plan, const QueryLimits& limits)
{
    std::size_t extra_bytes = 0;
    for (const auto& [id, slot] : plan.data().slots)
        if (!data.slots.contains(id) && !slot->parameter && slot->literal)
            extra_bytes += slot->literal->size();
    data.native_quota(plan.data().expanded(), extra_bytes, limits);
    data.slots.insert(plan.data().slots.begin(), plan.data().slots.end());
    data.retained_native += plan.data().expanded();
}
} // namespace

struct NativeQuery::Impl
{
    ModelBinding root;
    std::shared_ptr<ParameterRegistry> registry;
    QueryLimits limits;
    Capabilities capabilities;
    std::uint32_t generation = 0;
    QueryState state = QueryState::Mutable;
    std::shared_ptr<std::atomic_bool> submitted = std::make_shared<std::atomic_bool>(false);
    query_detail::QueryData data;

    void mutable_check(bool expression_operation) const
    {
        if (state == QueryState::Poisoned)
            throw Error(NW_ENGINE_POISONED, "query binding poisoned");
        if (state != QueryState::Mutable)
            state_error("query is not mutable");
        if (data.reserved && !expression_operation)
            state_error("only expression construction is admitted during JOIN reservation");
    }
    std::size_t index(std::uint64_t token) const
    {
        if (token == 0 || (token >> 32) != generation || static_cast<std::uint32_t>(token) == 0)
            invalid("token belongs to another query generation");
        auto at = static_cast<std::size_t>(static_cast<std::uint32_t>(token) - 1);
        if (at >= data.nodes.size() || !data.nodes[at].valid)
            invalid("stale or wrong arena slot");
        return at;
    }
    const query_detail::Node& node(std::uint64_t token) const
    {
        return data.nodes[index(token)];
    }
    query_detail::Expression get_expression(std::uint64_t token) const
    {
        if (state == QueryState::Closed || state == QueryState::Poisoned)
            state_error("closed/poisoned arena");
        const auto& value = node(token);
        if (value.kind != query_detail::NodeKind::Expression)
            invalid("token is not an expression");
        return value.expression;
    }
    std::uint64_t append_expression(query_detail::QueryData& candidate, query_detail::Expression value,
                                    std::size_t bytes) const
    {
        candidate.quota(bytes, value.type.depth, limits);
        candidate.native_quota(value.nodes, 0, limits);
        query_detail::Node node;
        node.kind = query_detail::NodeKind::Expression;
        node.expression = std::move(value);
        return candidate.append(std::move(node), bytes, generation, limits);
    }
    void dependency_quota(const NativePlan& plan) const
    {
        data.quota(plan.owned_bytes, plan.depth + 1, limits);
        if (plan.nodes >= limits.nodes - data.nodes.size() - data.retained_nodes)
            capacity("imported dependency node bound");
    }
};

NativeQuery::NativeQuery(ModelBinding root, std::shared_ptr<ParameterRegistry> registry, std::uint32_t table_id,
                         QueryLimits limits, Capabilities capabilities, std::uint64_t timeout_ns)
    : impl_(std::make_unique<Impl>())
{
    limits.validate();
    root.source.validate();
    if (!root.model || !root.codecs || !registry || root.model->table_id() != table_id ||
        registry->source != root.source || timeout_ns == 0)
        invalid("query identity/default timeout mismatch");
    if (!capabilities.query || !capabilities.metadata || !capabilities.scalar_codecs)
        unsupported("db.query.v1 required");
    impl_->root = std::move(root);
    impl_->registry = std::move(registry);
    impl_->limits = limits;
    impl_->capabilities = capabilities;
    impl_->generation = new_generation();
    impl_->data.timeout_ns = timeout_ns;
}
NativeQuery::NativeQuery(std::unique_ptr<Impl> value) : impl_(std::move(value)) {}
NativeQuery::~NativeQuery() = default;
NativeQuery::NativeQuery(NativeQuery&&) noexcept = default;
NativeQuery& NativeQuery::operator=(NativeQuery&&) noexcept = default;
SourceIdentity NativeQuery::source() const
{
    return impl_->root.source;
}
const ModelBinding& NativeQuery::root() const
{
    return impl_->root;
}
QueryState NativeQuery::state() const
{
    return impl_->state == QueryState::Sealed && impl_->submitted->load(std::memory_order_acquire)
               ? QueryState::Submitted
               : impl_->state;
}
std::tuple<std::size_t, std::size_t, std::size_t> NativeQuery::counts() const
{
    return {impl_->data.nodes.size() + impl_->data.retained_nodes, impl_->data.bytes, impl_->data.expanded()};
}
ExpressionType NativeQuery::expression_type(std::uint64_t token) const
{
    return impl_->get_expression(token).type;
}
void NativeQuery::poison()
{
    if (impl_->state != QueryState::Closed)
        impl_->state = QueryState::Poisoned;
}
void NativeQuery::close()
{
    impl_->state = QueryState::Closed;
    impl_->data = {};
    impl_->data.statement_nodes = 0;
}
std::shared_ptr<NativeQuery> NativeQuery::clone_selected() const
{
    impl_->mutable_check(false);
    auto copy = std::make_unique<Impl>(*impl_);
    copy->generation = new_generation();
    copy->submitted = std::make_shared<std::atomic_bool>(false);
    copy->state = QueryState::Mutable;
    copy->data.reserved.reset();
    return std::shared_ptr<NativeQuery>(new NativeQuery(std::move(copy)));
}
std::uint64_t NativeQuery::column(std::uint64_t alias, std::uint32_t column_id)
try
{
    impl_->mutable_check(true);
    auto model = impl_->root;
    bool left = false;
    std::string qualifier = model.model->table();
    std::set<std::uint32_t> dependencies;
    if (alias != 0)
    {
        const auto& node = impl_->node(alias);
        if (node.kind != NodeKind::Alias)
            invalid("column requires alias token");
        const auto slot = static_cast<std::uint32_t>(impl_->index(alias) + 1);
        if (node.alias.provisional && impl_->data.reserved != slot)
            state_error("inactive provisional alias");
        model = node.alias.model;
        left = node.alias.left && !node.alias.provisional;
        qualifier = node.alias.name;
        dependencies.insert(slot);
    }
    const auto& column = model.model->column(column_id);
    auto codec = column.codec;
    codec.nullable = codec.nullable || left;
    Expression value{{codec, model.codecs->bound(codec), 1, false, false, std::move(dependencies)},
                     column_expression(qualifier, column.name),
                     1,
                     {}};
    auto candidate = impl_->data;
    auto token = impl_->append_expression(candidate, std::move(value), 0);
    impl_->data = std::move(candidate);
    return token;
}
catch (const Error& error)
{
    binding_error(error);
}
std::uint64_t NativeQuery::value(ByteView one_cell)
{
    impl_->mutable_check(true);
    auto decoded = decode_values(impl_->root, one_cell, 1, impl_->limits);
    auto candidate = impl_->data;
    candidate.native_quota(1, one_cell.size(), impl_->limits);
    auto slot = make_slot(decoded.codec, decoded.bound, std::move(decoded.cells.at(0)));
    Expression value{{decoded.codec, decoded.bound, 1, false, false, {}}, slot_expression(slot), 1, {}};
    auto token = impl_->append_expression(candidate, std::move(value), one_cell.size());
    candidate.slots.emplace(slot->id, slot);
    candidate.values.emplace_back(one_cell.begin(), one_cell.end());
    impl_->data = std::move(candidate);
    return token;
}
std::uint64_t NativeQuery::parameter(ParameterIdentity identity)
{
    impl_->mutable_check(true);
    identity.validate(*impl_->root.codecs);
    auto candidate = impl_->data;
    candidate.quota(identity.name.size(), 1, impl_->limits);
    candidate.native_quota(1, 0, impl_->limits);
    merge_parameter(candidate.parameters, identity, impl_->limits.parameters);
    auto slot = make_slot(identity.codec, identity.bound_codec, {}, identity.id);
    Expression value{{identity.codec, identity.bound_codec, 1, false, false, {}}, slot_expression(slot), 1, {}};
    auto token = impl_->append_expression(candidate, std::move(value), identity.name.size());
    candidate.slots.emplace(slot->id, slot);
    impl_->registry->register_identity(identity);
    impl_->data = std::move(candidate);
    return token;
}
std::uint64_t NativeQuery::binary(std::uint32_t opcode, std::uint64_t left, std::uint64_t right)
{
    impl_->mutable_check(true);
    auto a = impl_->get_expression(left);
    auto b = impl_->get_expression(right);
    if (opcode == 0 || opcode > 14)
        unsupported("unknown required binary opcode");
    if (!a.type.codec.same_scalar(b.type.codec) || a.type.bound_codec != b.type.bound_codec)
        invalid("binary operand codec mismatch");
    auto codec = a.type.codec;
    if (opcode <= 6)
        codec = scalar(4, a.type.codec.nullable || b.type.codec.nullable);
    else if (opcode <= 10 && !numeric(codec.id))
        unsupported("arithmetic codec");
    else if ((opcode == 11 || opcode == 12) && codec.id != 4)
        invalid("boolean operand required");
    else if (opcode == 13)
    {
        if (!impl_->capabilities.null_safe_equal)
            unsupported("null-safe equality capability");
        codec = scalar(4);
    }
    else if (opcode == 14)
        codec.nullable = a.type.codec.nullable && b.type.codec.nullable;
    else
        codec.nullable = a.type.codec.nullable || b.type.codec.nullable;
    Expression value{{codec, codec.id == 4 ? 4 : a.type.bound_codec, std::max(a.type.depth, b.type.depth) + 1,
                      a.type.aggregate || b.type.aggregate, false, merged_dependencies(a.type, b.type)},
                     opcode == 14 ? expression(ExprKind::Function, {a.ast, b.ast}, 0, "COALESCE")
                                  : expression(ExprKind::Binary, {a.ast, b.ast}, opcode),
                     1 + a.nodes + b.nodes,
                     {}};
    auto candidate = impl_->data;
    auto token = impl_->append_expression(candidate, std::move(value), 0);
    impl_->data = std::move(candidate);
    return token;
}
std::uint64_t NativeQuery::unary(std::uint32_t opcode, std::uint64_t input)
{
    impl_->mutable_check(true);
    auto value = impl_->get_expression(input);
    if (opcode == 1 || opcode == 2)
    {
        value.type.codec = scalar(4);
        value.type.bound_codec = 4;
        value.ast = expression(ExprKind::Binary, {value.ast, expression(ExprKind::Constant, {}, 0, "NULL")},
                               opcode == 1 ? Is : IsNot);
    }
    else if (opcode == 3)
    {
        if (value.type.codec.id != 4)
            invalid("NOT requires boolean");
        value.ast = expression(ExprKind::Not, {value.ast});
    }
    else if (opcode == 4)
    {
        if (value.type.codec.nullable)
            invalid("NullableLift requires non-null input");
        value.type.codec.nullable = true;
    }
    else
        unsupported("unknown required unary opcode");
    if (opcode != 4)
    {
        value.type.windowable = false;
        value.window_cast.reset();
    }
    ++value.type.depth;
    ++value.nodes;
    auto candidate = impl_->data;
    auto token = impl_->append_expression(candidate, std::move(value), 0);
    impl_->data = std::move(candidate);
    return token;
}
std::uint64_t NativeQuery::list(std::uint32_t opcode, std::uint64_t input, ByteView cells)
{
    impl_->mutable_check(true);
    if (opcode == 0 || opcode > 3)
        unsupported("unknown required list opcode");
    auto value = impl_->get_expression(input);
    auto decoded =
        decode_values(impl_->root, cells, opcode == 3 ? std::optional<std::uint32_t>(2) : std::nullopt, impl_->limits);
    if (!value.type.codec.same_scalar(decoded.codec) || value.type.bound_codec != decoded.bound)
        invalid("list element codec mismatch");
    auto candidate = impl_->data;
    candidate.native_quota(value.nodes + decoded.cells.size() + 1, cells.size(), impl_->limits);
    std::vector<ExprPtr> arguments;
    for (auto& cell : decoded.cells)
    {
        auto slot = make_slot(decoded.codec, decoded.bound, std::move(cell));
        candidate.slots.emplace(slot->id, slot);
        arguments.push_back(slot_expression(slot));
    }
    auto ast = arguments.empty() ? expression(ExprKind::Constant, {}, 0, opcode == 2 ? "TRUE" : "FALSE")
               : opcode == 3
                   ? expression(ExprKind::Binary, {value.ast, expression(ExprKind::Binary, arguments, 11)}, Between)
                   : expression(ExprKind::Binary, {value.ast, expression(ExprKind::Tuple, arguments)},
                                opcode == 1 ? In : NotIn);
    Expression result{{scalar(4, !arguments.empty() && (decoded.codec.nullable || value.type.codec.nullable)), 4,
                       value.type.depth + 1, value.type.aggregate, false, value.type.dependencies},
                      ast,
                      value.nodes + arguments.size() + 1,
                      {}};
    auto token = impl_->append_expression(candidate, std::move(result), cells.size());
    candidate.values.emplace_back(cells.begin(), cells.end());
    impl_->data = std::move(candidate);
    return token;
}
std::uint64_t NativeQuery::import_model(ModelBinding model)
{
    impl_->mutable_check(false);
    model.ensure_source(source());
    Node node;
    node.kind = NodeKind::Model;
    node.model = std::move(model);
    auto candidate = impl_->data;
    auto token = candidate.append(std::move(node), 0, impl_->generation, impl_->limits);
    impl_->data = std::move(candidate);
    return token;
}
std::uint64_t NativeQuery::import_plan(std::shared_ptr<const NativePlan> plan)
{
    impl_->mutable_check(false);
    if (!plan || plan->source != source())
        state_error("imported plan from another source");
    impl_->dependency_quota(*plan);
    auto candidate = impl_->data;
    import_native(candidate, *plan, impl_->limits);
    for (const auto& [id, identity] : plan->parameters)
    {
        (void)id;
        merge_parameter(candidate.parameters, identity, impl_->limits.parameters);
    }
    Node node;
    node.kind = NodeKind::Plan;
    node.plan = plan;
    auto token = candidate.append(std::move(node), plan->owned_bytes, impl_->generation, impl_->limits);
    candidate.retained_nodes += plan->nodes;
    candidate.imported_depth = std::max(candidate.imported_depth, plan->depth + 1);
    impl_->data = std::move(candidate);
    return token;
}
std::uint64_t NativeQuery::import_cte(std::shared_ptr<const NativeCte> cte)
{
    impl_->mutable_check(false);
    if (!impl_->capabilities.cte)
        unsupported("CTE capability");
    if (!cte || cte->source != source())
        state_error("CTE source generation");
    impl_->dependency_quota(*cte->body);
    auto candidate = impl_->data;
    import_native(candidate, *cte->body, impl_->limits);
    for (const auto& [id, identity] : cte->body->parameters)
    {
        (void)id;
        merge_parameter(candidate.parameters, identity, impl_->limits.parameters);
    }
    Node node;
    node.kind = NodeKind::Cte;
    node.cte = cte;
    auto token = candidate.append(std::move(node), cte->body->owned_bytes, impl_->generation, impl_->limits);
    candidate.retained_nodes += cte->body->nodes;
    candidate.imported_depth = std::max(candidate.imported_depth, cte->body->depth + 1);
    impl_->data = std::move(candidate);
    return token;
}

std::uint64_t NativeQuery::clause(std::uint32_t opcode, ByteView operands)
try
{
    if (opcode == 0 || opcode > 22)
        unsupported("unknown required clause opcode");
    impl_->mutable_check(opcode == 7 || (opcode >= 13 && opcode <= 16) || (opcode >= 19 && opcode <= 21));
    if (opcode != 19)
        impl_->data.quota(operands.size(), 0, impl_->limits);
    auto candidate = impl_->data;
    auto marker = [&] { return candidate.append(Node{}, operands.size(), impl_->generation, impl_->limits); };
    auto emit = [&](Expression value)
    { return impl_->append_expression(candidate, std::move(value), operands.size()); };
    auto imported_plan = [&](std::uint64_t token)
    {
        const auto& node = impl_->node(token);
        if (node.kind != NodeKind::Plan || !node.plan)
            invalid("expected imported plan");
        return node.plan;
    };
    auto window_at = [&](std::uint64_t token) -> std::size_t
    {
        if (!impl_->capabilities.windows)
            unsupported("window capability");
        auto at = impl_->index(token);
        if (impl_->data.nodes[at].kind != NodeKind::Window)
            invalid("window token required");
        return at;
    };
    std::uint64_t result = 0;
    switch (opcode)
    {
        case 1:
        case 2:
        {
            auto f = Fields::parse(operands, 6, {{1, 2, true}});
            auto value = impl_->get_expression(f.u64(1));
            if (value.type.codec.id != 4)
                invalid("predicate must be boolean");
            if (opcode == 1 && value.type.aggregate)
                invalid("aggregate not allowed in WHERE");
            candidate.statement_nodes += value.nodes;
            (opcode == 1 ? candidate.predicates : candidate.having).push_back(value.ast);
            if (opcode == 1)
                candidate.has_where = true;
            (void)marker();
            break;
        }
        case 3:
        {
            auto f = Fields::parse(operands, 6, {{1, 2, true}, {2, 1, true}, {3, 1, true}, {4, 1, true}});
            auto value = impl_->get_expression(f.u64(1));
            direction(f.u32(2), f.u32(3));
            if (f.boolean(4))
                candidate.orders.clear();
            candidate.orders.push_back({value.ast, f.u32(2), f.u32(3)});
            candidate.statement_nodes += value.nodes;
            (void)marker();
            break;
        }
        case 4:
        {
            auto f = Fields::parse(operands, 6, {{1, 2, true}});
            auto value = impl_->get_expression(f.u64(1));
            if (value.type.aggregate)
                invalid("aggregate cannot be a GROUP key");
            candidate.groups.push_back(value.ast);
            candidate.grouped = true;
            candidate.statement_nodes += value.nodes;
            (void)marker();
            break;
        }
        case 5:
        {
            auto f = Fields::parse(operands, 6, {{1, 2, false}, {2, 2, false}});
            if (!f.get(1) && !f.get(2))
                invalid("empty paging operation");
            for (std::uint16_t id = 1; id <= 2; ++id)
                if (f.get(id) && f.u64(id) > static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max()))
                    invalid("paging exceeds signed database range");
            if (f.get(1))
                candidate.limit = f.u64(1);
            if (f.get(2))
                candidate.offset = f.u64(2);
            candidate.has_paging = true;
            (void)marker();
            break;
        }
        case 6:
        {
            auto f = Fields::parse(operands, 6, {{1, 8, true}, {2, 5, true}});
            auto tokens = token_vector(f.bytes(1));
            auto projection = projection_record(f.bytes(2), source(), 1, impl_->root.codecs, &tokens);
            if (tokens.size() != projection.projection->fields().size())
                invalid("projection count");
            candidate.selects.clear();
            for (std::size_t i = 0; i < tokens.size(); ++i)
            {
                auto value = impl_->get_expression(tokens[i]);
                const auto& field = projection.projection->fields()[i];
                if (value.type.codec != field.codec || value.type.bound_codec != projection.codecs->bound(field.codec))
                    invalid("projection expression codec/nullability");
                candidate.selects.push_back(
                    {value.ast, field.name, static_cast<std::uint32_t>(tokens[i]), value.type.aggregate});
                candidate.statement_nodes += value.nodes;
            }
            candidate.projection = std::move(projection);
            (void)marker();
            break;
        }
        case 7:
        case 19:
        {
            auto f = opcode == 7 ? Fields::parse(operands, 6, {{1, 2, true}, {2, 2, true}})
                                 : Fields::parse(operands, 6, {{1, 2, true}});
            const auto at = impl_->index(f.u64(1));
            const auto& node = impl_->data.nodes[at];
            if (node.kind != NodeKind::Alias || !node.alias.provisional || impl_->data.reserved != at + 1)
                invalid("active provisional JOIN alias required");
            if (opcode == 7)
            {
                auto on = impl_->get_expression(f.u64(2));
                if (on.type.codec.id != 4 || on.type.aggregate)
                    invalid("JOIN ON requires scalar boolean");
                auto alias = node.alias;
                alias.provisional = false;
                candidate.joins.push_back({alias, on.ast});
                candidate.nodes[at].alias = alias;
                candidate.statement_nodes += on.nodes + 1;
                if (alias.left)
                    candidate.statement_nodes += alias.model.model->columns().size() + 2;
                candidate.has_join = true;
                (void)marker();
                result = f.u64(1);
            }
            else
                candidate.nodes[at].valid = false;
            for (auto& item : candidate.nodes)
                if (item.kind == NodeKind::Expression &&
                    item.expression.type.dependencies.contains(static_cast<std::uint32_t>(at + 1)))
                    item.valid = false;
            candidate.reserved.reset();
            break;
        }
        case 8:
        {
            (void)Fields::parse(operands, 6, {});
            candidate.distinct = true;
            (void)marker();
            break;
        }
        case 9:
        {
            if (!impl_->capabilities.set_operations)
                unsupported("set-operation capability");
            auto f = Fields::parse(operands, 6, {{1, 2, true}, {2, 1, true}});
            const auto operation = f.u32(2);
            if (operation > 3)
                unsupported("set operation");
            auto plan = imported_plan(f.u64(1));
            if (!candidate.projection || !plan->projection)
                invalid("set selection missing");
            const auto& own = candidate.projection->projection->fields();
            const auto& incoming = plan->projection->projection->fields();
            if (own.size() != incoming.size())
                invalid("set result shape");
            for (std::size_t i = 0; i < own.size(); ++i)
                if (own[i].codec != incoming[i].codec)
                    invalid("set result shape");
            candidate.sets.emplace_back(operation, plan);
            candidate.requires_transaction = candidate.requires_transaction || plan->requires_transaction();
            candidate.statement_nodes += plan->data().statement_nodes;
            candidate.advanced_mutation = true;
            (void)marker();
            break;
        }
        case 10:
        {
            auto f = Fields::parse(operands, 6, {{1, 2, true}});
            const auto& node = impl_->node(f.u64(1));
            if (node.kind != NodeKind::Cte || !node.cte)
                invalid("expected imported CTE");
            if (!candidate.cte_names.insert(node.cte->name).second)
                invalid("duplicate CTE name");
            candidate.with.push_back(node.cte);
            candidate.recursive_cte = candidate.recursive_cte || node.cte->recursive;
            candidate.requires_transaction = candidate.requires_transaction || node.cte->body->requires_transaction();
            candidate.statement_nodes += node.cte->body->data().statement_nodes;
            candidate.advanced_mutation = true;
            (void)marker();
            break;
        }
        case 11:
        {
            if (!impl_->capabilities.windows)
                unsupported("window capability");
            auto f = Fields::parse(operands, 6,
                                   {{1, 1, true},
                                    {2, 2, false},
                                    {3, 2, false},
                                    {4, 1, false},
                                    {5, 1, false},
                                    {6, 1, false},
                                    {7, 1, false}});
            const auto action = f.u32(1);
            if (action == 0)
            {
                f.require_set({1});
                Node node;
                node.kind = NodeKind::Window;
                result = candidate.append(std::move(node), operands.size(), impl_->generation, impl_->limits);
                candidate.advanced_mutation = true;
                break;
            }
            auto at = window_at(f.u64(2));
            auto& window = candidate.nodes[at].window;
            if (action == 1)
            {
                f.require_set({1, 2, 3});
                auto value = impl_->get_expression(f.u64(3));
                window.partitions.push_back(value.ast);
                window.nodes += value.nodes;
            }
            else if (action == 2)
            {
                f.require_set({1, 2, 3, 4, 5});
                auto value = impl_->get_expression(f.u64(3));
                direction(f.u32(4), f.u32(5));
                window.orders.push_back({value.ast, f.u32(4), f.u32(5)});
                window.nodes += value.nodes;
            }
            else if (action == 3)
            {
                f.require_set({1, 2, 6, 7});
                auto start = f.u32(6);
                auto end = f.u32(7);
                if (start > 1 || end < 1 || end > 2 || start > end)
                    invalid("window frame endpoints");
                window.frame = std::pair{start, end};
                ++window.nodes;
            }
            else
                unsupported("window action");
            (void)marker();
            break;
        }
        case 12:
        {
            auto f = Fields::parse(operands, 6, {{1, 1, true}, {2, 1, true}});
            if (source().profile != Profile::PostgreSql || !impl_->capabilities.row_lock)
                unsupported("PostgreSQL row-lock capability");
            if (f.u32(1) > 3 || f.u32(2) > 2)
                unsupported("row-lock mode/wait");
            candidate.lock = std::pair{f.u32(1), f.u32(2)};
            candidate.requires_transaction = true;
            (void)marker();
            break;
        }
        case 13:
        {
            auto f =
                Fields::parse(operands, 6, {{1, 1, true}, {2, 2, false}, {3, 1, true}, {4, 2, false}, {5, 1, true}});
            const auto function = f.u32(1);
            const bool distinct = f.boolean(3);
            if (function > 7)
                unsupported("aggregate function");
            if (!impl_->capabilities.windows && (f.get(4) || function >= 6))
                unsupported("window capability");
            std::optional<Expression> input;
            if (f.get(2))
                input = impl_->get_expression(f.u64(2));
            if (((function == 0 || function >= 6) && (input || distinct)) || (function >= 1 && function <= 5 && !input))
                invalid("aggregate operand set");
            const bool nullable = function >= 2 && function <= 5;
            auto codec = impl_->root.codecs->get(f.u32(5), nullable);
            if ((function <= 1 || function >= 6) && codec.id != 3)
                invalid("count/rank result codec must be Int64");
            if (input)
            {
                if (input->type.aggregate)
                    invalid("nested aggregate requires a subquery");
                if (function == 2 || function == 3)
                {
                    const auto id = input->type.codec.id;
                    if (!numeric(id))
                        unsupported("numeric aggregate codec");
                    const auto expected = id <= 3 ? (function == 2 ? 3U : 11U) : (id == 11 || id == 12 ? 11U : id);
                    if (codec.id != expected ||
                        ((expected == 13 || expected == 15) && !codec.same_scalar(input->type.codec)))
                        invalid("aggregate result widening/configuration");
                }
                if ((function == 4 || function == 5) && !codec.same_scalar(input->type.codec))
                    invalid("min/max result codec");
            }
            static const std::array<std::string, 8> names{"COUNT", "COUNT", "SUM",        "AVG",
                                                          "MIN",   "MAX",   "ROW_NUMBER", "RANK"};
            std::vector<ExprPtr> args;
            if (function == 0)
                args.push_back(expression(ExprKind::Constant, {}, 0, "*"));
            else if (input)
                args.push_back(input->ast);
            auto ast = expression(ExprKind::Function, std::move(args), distinct ? 1U : 0U, names[function]);
            auto nodes = 1 + (input ? input->nodes : 0);
            if (f.get(4))
            {
                const auto& window = impl_->data.nodes[window_at(f.u64(4))].window;
                ast = over_expression(ast, window);
                nodes += window.nodes;
            }
            std::string cast;
            if (source().profile == Profile::PostgreSql && input)
            {
                const auto id = input->type.codec.id;
                if (function == 2 && ((id >= 1 && id <= 3) || id == 15))
                    cast = "bigint";
                else if ((function == 3 && ((id >= 1 && id <= 3) || id == 11 || id == 12)) ||
                         (function == 2 && (id == 11 || id == 12)))
                    cast = "double precision";
            }
            std::optional<std::pair<ExprPtr, std::string>> window_cast;
            if (!cast.empty())
            {
                if (!f.get(4))
                {
                    window_cast = std::pair{ast, cast};
                    nodes += nodes;
                }
                ast = expression(ExprKind::Cast, {ast}, 0, cast);
                ++nodes;
            }
            Expression value{{codec, f.u32(5), input ? input->type.depth + 1 : 1, true, !f.get(4),
                              input ? input->type.dependencies : std::set<std::uint32_t>{}},
                             ast,
                             nodes,
                             window_cast};
            ++candidate.aggregate_nodes;
            result = emit(std::move(value));
            break;
        }
        case 14:
        {
            auto f = Fields::parse(operands, 6, {{1, 2, true}, {2, 1, true}, {3, 2, false}});
            auto plan = imported_plan(f.u64(1));
            if (!plan->projection)
                invalid("subquery projection");
            ExpressionType info{scalar(4), 4, plan->depth + 1, false, false, {}};
            auto ast = std::make_shared<Expr>();
            ast->kind = ExprKind::Subquery;
            ast->plan = plan;
            ast->op = f.u32(2);
            auto nodes = plan->data().statement_nodes + 1;
            if (f.u32(2) == 0)
                f.require_set({1, 2});
            else if (f.u32(2) == 1)
            {
                f.require_set({1, 2, 3});
                auto lhs = impl_->get_expression(f.u64(3));
                const auto& fields = plan->projection->projection->fields();
                if (fields.size() != 1 || !fields.front().codec.same_scalar(lhs.type.codec))
                    invalid("IN subquery scalar shape");
                info.codec.nullable = lhs.type.codec.nullable || fields.front().codec.nullable;
                info.dependencies = lhs.type.dependencies;
                info.depth = std::max(lhs.type.depth, plan->depth) + 1;
                info.aggregate = lhs.type.aggregate;
                ast->children.push_back(lhs.ast);
                nodes += lhs.nodes;
            }
            else
                unsupported("subquery operation");
            candidate.requires_transaction = candidate.requires_transaction || plan->requires_transaction();
            candidate.subqueries.push_back(plan);
            result = emit({info, ast, nodes, {}});
            break;
        }
        case 15:
        {
            auto f = Fields::parse(operands, 6, {{1, 2, true}, {2, 2, true}, {3, 2, true}});
            auto condition = impl_->get_expression(f.u64(1));
            auto a = impl_->get_expression(f.u64(2));
            auto b = impl_->get_expression(f.u64(3));
            if (condition.type.codec.id != 4 || !a.type.codec.same_scalar(b.type.codec) ||
                a.type.bound_codec != b.type.bound_codec)
                invalid("CASE operand types");
            auto codec = a.type.codec;
            codec.nullable = codec.nullable || b.type.codec.nullable;
            auto deps = merged_dependencies(a.type, b.type);
            deps.insert(condition.type.dependencies.begin(), condition.type.dependencies.end());
            Expression value{{codec, a.type.bound_codec,
                              std::max({condition.type.depth, a.type.depth, b.type.depth}) + 1,
                              condition.type.aggregate || a.type.aggregate || b.type.aggregate, false, deps},
                             expression(ExprKind::Case, {condition.ast, a.ast, b.ast}),
                             condition.nodes + a.nodes + b.nodes + 1,
                             {}};
            result = emit(std::move(value));
            break;
        }
        case 16:
        {
            if (operands.size() < 32)
                invalid("missing operation selector");
            const auto count = read_u32(operands, 8);
            const auto offset = read_u64(operands, 24);
            if (count == 0 || count > MAX_NODES || 16 + static_cast<std::size_t>(count) * 16 > operands.size() ||
                read_u16(operands, 16) != 1 || read_u16(operands, 18) != 1 || read_u32(operands, 20) != 4 ||
                offset < 16 + static_cast<std::uint64_t>(count) * 16 || offset > operands.size() ||
                operands.size() - offset < 4)
                invalid("operation selector range");
            const auto operation = read_u32(operands, static_cast<std::size_t>(offset));
            if (operation > 4)
                unsupported("text operation");
            auto f = Fields::parse(operands, 6,
                                   {{1, 1, true},
                                    {2, 2, true},
                                    {3, static_cast<std::uint16_t>(operation == 4 ? 4 : 6), true},
                                    {4, 1, true}});
            auto input = impl_->get_expression(f.u64(2));
            if (input.type.codec.id != 5)
                invalid("text input codec");
            auto escape = f.u32(4);
            if (escape == 0)
                invalid("LIKE escape Unicode scalar");
            auto escaped_character = utf8(escape);
            ExprPtr ast;
            auto nodes = input.nodes + 2;
            if (operation == 4)
            {
                auto name = std::string(f.text(3));
                const bool allowed = source().profile == Profile::Sqlite
                                         ? (name == "BINARY" || name == "NOCASE" || name == "RTRIM")
                                         : (name == "C" || name == "POSIX" || name == "default");
                if (!allowed)
                    unsupported("collation is not allowlisted");
                ast = expression(ExprKind::Binary, {input.ast, column_expression({}, name)}, Collate);
            }
            else
            {
                auto decoded = decode_values(impl_->root, f.bytes(3), 1, impl_->limits);
                if (decoded.codec.id != 5)
                    invalid("text argument codec");
                std::optional<Bytes> transformed;
                if (decoded.cells.front())
                {
                    std::string text(decoded.cells.front()->begin(), decoded.cells.front()->end());
                    std::string pattern;
                    if (operation == 0 || operation == 2)
                        pattern += '%';
                    for (auto cp : codepoints(text))
                    {
                        if (operation != 3 && (cp == escape || cp == '%' || cp == '_'))
                            pattern += escaped_character;
                        pattern += utf8(cp);
                    }
                    if (operation == 0 || operation == 1)
                        pattern += '%';
                    if (pattern.size() > impl_->limits.bytes)
                        capacity("escaped LIKE pattern bound");
                    transformed = bytes_of(pattern);
                }
                candidate.native_quota(input.nodes + 3, transformed ? transformed->size() : 0, impl_->limits);
                auto slot = make_slot(decoded.codec, decoded.bound, std::move(transformed));
                candidate.slots.emplace(slot->id, slot);
                auto rhs = expression(ExprKind::Binary,
                                      {slot_expression(slot), expression(ExprKind::Constant, {}, 1, escaped_character)},
                                      Escape);
                ast = expression(ExprKind::Binary, {input.ast, rhs}, Like);
                nodes = input.nodes + 3;
                input.type.codec = scalar(4, input.type.codec.nullable || decoded.codec.nullable);
                input.type.bound_codec = 4;
            }
            ++input.type.depth;
            input.type.windowable = false;
            result = emit({input.type, ast, nodes, {}});
            candidate.values.emplace_back(f.bytes(3).begin(), f.bytes(3).end());
            break;
        }
        case 17:
        {
            auto f = Fields::parse(operands, 6, {{1, 1, true}, {2, 2, true}});
            const auto id = f.u32(1);
            const auto& column = impl_->root.model->column(id);
            auto value = impl_->get_expression(f.u64(2));
            if (column.key_ordinal || column.generated || candidate.assigned.contains(id) ||
                !column.codec.same_scalar(value.type.codec) || (!column.codec.nullable && value.type.codec.nullable) ||
                value.type.aggregate || !value.type.dependencies.empty())
                invalid("invalid root assignment");
            candidate.assigned.insert(id);
            candidate.assignments.emplace_back(column.name, value.ast);
            candidate.statement_nodes += value.nodes;
            (void)marker();
            break;
        }
        case 18:
        {
            if (!impl_->capabilities.explicit_relationships)
                unsupported("explicit relationship capability");
            auto f = Fields::parse(operands, 6, {{1, 2, true}, {2, 1, true}, {3, 4, true}, {4, 1, true}});
            const auto& model_node = impl_->node(f.u64(1));
            if (model_node.kind != NodeKind::Model)
                invalid("JOIN imported model token");
            auto name = std::string(f.text(3));
            identifier(name);
            if (f.u32(2) != model_node.model.model->table_id() || f.u32(4) > 1)
                invalid("JOIN table/kind");
            for (const auto& item : candidate.nodes)
                if (item.valid && item.kind == NodeKind::Alias && item.alias.name == name)
                    invalid("duplicate JOIN alias");
            std::string match_marker = "_nw_match";
            while (std::any_of(model_node.model.model->columns().begin(), model_node.model.model->columns().end(),
                               [&](const auto& col) { return col.name == match_marker; }))
                match_marker += '_';
            Node node;
            node.kind = NodeKind::Alias;
            node.alias = {model_node.model, name, match_marker, f.u32(4) == 1, true};
            result = candidate.append(std::move(node), operands.size(), impl_->generation, impl_->limits);
            candidate.reserved = static_cast<std::uint32_t>(result);
            break;
        }
        case 20:
        {
            auto f = Fields::parse(operands, 6, {{1, 2, true}});
            const auto at = impl_->index(f.u64(1));
            const auto& node = impl_->data.nodes[at];
            if (node.kind != NodeKind::Alias || !node.alias.left || node.alias.provisional)
                invalid("JOIN MATCH requires committed left alias");
            ExpressionType info{scalar(4), 4, 1, false, false, {static_cast<std::uint32_t>(at + 1)}};
            result = emit({info,
                           expression(ExprKind::Binary,
                                      {column_expression(node.alias.name, node.alias.marker),
                                       expression(ExprKind::Constant, {}, 0, "NULL")},
                                      IsNot),
                           2,
                           {}});
            break;
        }
        case 21:
        {
            if (!impl_->capabilities.windows)
                unsupported("window capability");
            auto f = Fields::parse(operands, 6, {{1, 2, true}, {2, 2, true}});
            auto value = impl_->get_expression(f.u64(1));
            if (!value.type.windowable)
                invalid("OVER requires native aggregate/window expression");
            const auto& window = impl_->data.nodes[window_at(f.u64(2))].window;
            value.ast = value.window_cast
                            ? expression(ExprKind::Cast, {over_expression(value.window_cast->first, window)}, 0,
                                         value.window_cast->second)
                            : over_expression(value.ast, window);
            value.window_cast.reset();
            value.nodes += window.nodes + 1;
            ++value.type.depth;
            value.type.windowable = false;
            result = emit(std::move(value));
            break;
        }
        case 22:
        {
            auto f = Fields::parse(operands, 6, {{1, 3, true}});
            candidate.timeout_ns = f.u64(1);
            (void)marker();
            break;
        }
        default:
            unsupported("clause opcode");
    }
    candidate.native_quota(0, 0, impl_->limits);
    impl_->data = std::move(candidate);
    return result;
}
catch (const Error& error)
{
    binding_error(error);
}

struct NativePlan::Impl
{
    std::shared_ptr<const QueryData> data;
    QueryLimits limits;
    std::shared_ptr<std::atomic_bool> submitted;
    mutable std::once_flag once;
    mutable std::unique_ptr<CompiledPlan> compiled;
    mutable std::exception_ptr compile_error;
};
NativePlan::NativePlan() : impl_(std::make_unique<Impl>()) {}
NativePlan::~NativePlan() = default;
const QueryData& NativePlan::data() const
{
    return *impl_->data;
}
bool NativePlan::requires_transaction() const
{
    return data().requires_transaction;
}
std::pair<std::size_t, std::size_t> NativePlan::retained_counts() const
{
    return {static_cast<std::size_t>(
                std::count_if(data().nodes.begin(), data().nodes.end(), [](const auto& node) { return node.valid; })),
            data().values.size()};
}
std::shared_ptr<NativePlan> NativeQuery::seal(std::uint32_t terminal)
{
    impl_->mutable_check(false);
    if (terminal > 8)
        unsupported("unknown query terminal");
    const auto& data = impl_->data;
    if ((terminal <= 3 || terminal == 8) && !data.projection)
        invalid("selected projection required");
    if ((terminal == 6 || terminal == 7) &&
        (!data.has_where || data.has_join || data.grouped || data.has_paging || data.advanced_mutation))
        unsupported("set mutation requires root-only filter");
    if (terminal == 6 && data.assignments.empty())
        invalid("update assignments required");
    if (data.grouped)
        for (const auto& selected : data.selects)
        {
            if (!selected.aggregate && std::none_of(data.groups.begin(), data.groups.end(), [&](const auto& key)
                                                    { return same_expression(selected.expression, key); }))
                invalid("nonaggregated projection must be a grouping key");
        }
    auto plan = std::shared_ptr<NativePlan>(new NativePlan());
    plan->source = source();
    plan->root = impl_->root;
    plan->terminal = terminal;
    plan->prepared_supported = impl_->capabilities.prepared;
    plan->parameters = data.parameters;
    plan->projection = data.projection;
    plan->timeout_ns = data.timeout_ns;
    plan->nodes = data.nodes.size() + data.retained_nodes;
    plan->depth = std::max(1U, data.imported_depth);
    plan->owned_bytes = data.bytes;
    for (const auto& node : data.nodes)
        if (node.valid && node.kind == NodeKind::Expression)
            plan->depth = std::max(plan->depth, node.expression.type.depth);
    plan->impl_->data = std::make_shared<const QueryData>(data);
    plan->impl_->limits = impl_->limits;
    plan->impl_->submitted = impl_->submitted;
    impl_->state = QueryState::Sealed;
    return plan;
}

namespace
{
std::size_t recursive_references(const ModelBinding& root, const QueryData& data, const Hash& hash)
{
    std::size_t count = root.model->hash() == hash ? 1 : 0;
    for (const auto& node : data.nodes)
    {
        if (!node.valid)
            continue;
        if (node.kind == NodeKind::Alias && node.alias.model.model->hash() == hash)
            ++count;
        if (node.kind == NodeKind::Cte)
            count += recursive_references(node.cte->body->root, node.cte->body->data(), hash);
    }
    for (const auto& plan : data.subqueries)
        count += recursive_references(plan->root, plan->data(), hash);
    for (const auto& [op, plan] : data.sets)
    {
        (void)op;
        count += recursive_references(plan->root, plan->data(), hash);
    }
    return count;
}
bool row_codecs_match(const Model& model, const Projection& projection)
{
    if (model.projection().size() != projection.fields().size())
        return false;
    for (std::size_t i = 0; i < model.projection().size(); ++i)
        if (model.column(model.projection()[i]).codec != projection.fields()[i].codec)
            return false;
    return true;
}
void metadata_hashes(const NativePlan& plan, std::set<Hash>& hashes)
{
    hashes.insert(plan.root.model->hash());
    for (const auto& node : plan.data().nodes)
    {
        if (!node.valid)
            continue;
        if (node.kind == NodeKind::Model)
            hashes.insert(node.model.model->hash());
        else if (node.kind == NodeKind::Alias)
            hashes.insert(node.alias.model.model->hash());
        else if (node.kind == NodeKind::Plan)
            metadata_hashes(*node.plan, hashes);
        else if (node.kind == NodeKind::Cte)
        {
            hashes.insert(node.cte->declared.model->hash());
            metadata_hashes(*node.cte->body, hashes);
        }
    }
}
bool logical(std::uint32_t op)
{
    return op == 11 || op == 12 || op == 50;
}
bool arithmetic(std::uint32_t op)
{
    return op >= 7 && op <= 10;
}
bool comparison(std::uint32_t op)
{
    return op >= 1 && op <= 6;
}
bool membership(std::uint32_t op)
{
    return op == In || op == NotIn;
}
bool higher_precedence(const ExprPtr& inner, std::uint32_t outer, Profile profile)
{
    switch (inner->kind)
    {
        case ExprKind::Column:
        case ExprKind::Slot:
        case ExprKind::Function:
        case ExprKind::Tuple:
        case ExprKind::Constant:
        case ExprKind::Case:
        case ExprKind::Subquery:
        case ExprKind::Cast:
            return true;
        case ExprKind::Binary:
        {
            const auto op = inner->op;
            if (arithmetic(op))
                return comparison(outer) || outer == Between || membership(outer) || outer == Like || logical(outer);
            if (comparison(op) || membership(op) || op == Like || op == Is || op == IsNot ||
                (op == 13 && profile == Profile::Sqlite))
                return logical(outer);
            return false;
        }
        default:
            return false;
    }
}
std::string operator_text(std::uint32_t op, Profile profile)
{
    static const std::array<const char*, 13> operators{
        "", "=", "<>", ">", ">=", "<", "<=", "+", "-", "*", "/", "AND", "OR"};
    if (op >= 1 && op <= 12)
        return operators[op];
    if (op == 13)
        return profile == Profile::Sqlite ? "IS" : "IS NOT DISTINCT FROM";
    switch (op)
    {
        case Is:
            return "IS";
        case IsNot:
            return "IS NOT";
        case In:
            return "IN";
        case NotIn:
            return "NOT IN";
        case Between:
            return "BETWEEN";
        case Like:
            return "LIKE";
        case Escape:
            return "ESCAPE";
        case Collate:
            return "COLLATE";
        default:
            unsupported("renderer binary operator");
    }
}
std::string quoted_constant(std::string_view text, Profile profile)
{
    std::string out;
    if (profile == Profile::PostgreSql)
    {
        bool escaped = std::any_of(text.begin(), text.end(),
                                   [](unsigned char c) { return c < 32 || c == 127 || c == '\\' || c == '\''; });
        out = escaped ? "E'" : "'";
        for (unsigned char c : text)
        {
            switch (c)
            {
                case '\b':
                    out += "\\b";
                    break;
                case '\f':
                    out += "\\f";
                    break;
                case '\n':
                    out += "\\n";
                    break;
                case '\r':
                    out += "\\r";
                    break;
                case '\t':
                    out += "\\t";
                    break;
                case '\\':
                    out += "\\\\";
                    break;
                case '\'':
                    out += "\\'";
                    break;
                case 0:
                    out += "\\0";
                    break;
                default:
                    if (c < 32 || c == 127)
                    {
                        out += '\\';
                        out += static_cast<char>('0' + (c >> 6));
                        out += static_cast<char>('0' + ((c >> 3) & 7));
                        out += static_cast<char>('0' + (c & 7));
                    }
                    else
                        out += static_cast<char>(c);
            }
        }
    }
    else
    {
        out = "'";
        for (char c : text)
        {
            out += c;
            if (c == '\'')
                out += c;
        }
    }
    return out + '\'';
}

class Renderer
{
  public:
    explicit Renderer(Profile value) : profile(value) {}
    Profile profile;
    std::string sql;
    std::vector<SlotPtr> arguments;

    void parameter(const SlotPtr& slot)
    {
        arguments.push_back(slot);
        sql += profile == Profile::Sqlite ? "?" : "$" + std::to_string(arguments.size());
    }
    void child(const ExprPtr& value, std::uint32_t op, bool left)
    {
        bool parentheses = !higher_precedence(value, op, profile);
        if (left && value->kind == ExprKind::Binary && value->op == op &&
            (op == 11 || op == 12 || op == 7 || op == 8 || op == 9))
            parentheses = false;
        if (!left && value->kind == ExprKind::Binary &&
            ((op == Between && value->op == 11) || (op == Like && value->op == Escape)))
            parentheses = false;
        if (parentheses)
            sql += '(';
        expr(value);
        if (parentheses)
            sql += ')';
    }
    void expr(const ExprPtr& value)
    {
        switch (value->kind)
        {
            case ExprKind::Column:
                if (!value->qualifier.empty())
                    sql += quote_id(value->qualifier) + '.';
                sql += quote_id(value->text);
                break;
            case ExprKind::Slot:
                parameter(value->slot);
                break;
            case ExprKind::Constant:
                sql += value->op == 1 ? quoted_constant(value->text, profile) : value->text;
                break;
            case ExprKind::Binary:
                child(value->children.at(0), value->op, true);
                sql += ' ' + operator_text(value->op, profile) + ' ';
                child(value->children.at(1), value->op, false);
                break;
            case ExprKind::Not:
                sql += "NOT ";
                child(value->children.at(0), 50, false);
                break;
            case ExprKind::Tuple:
                sql += '(';
                list(value->children);
                sql += ')';
                break;
            case ExprKind::Function:
                sql += value->text + '(';
                if (value->op == 1)
                    sql += "DISTINCT ";
                list(value->children);
                sql += ')';
                break;
            case ExprKind::Case:
                sql += "(CASE WHEN (";
                expr(value->children.at(0));
                sql += ") THEN ";
                expr(value->children.at(1));
                sql += " ELSE ";
                expr(value->children.at(2));
                sql += " END)";
                break;
            case ExprKind::Subquery:
                if (value->op == 0)
                    sql += "EXISTS(";
                else
                {
                    child(value->children.at(0), In, true);
                    sql += " IN (";
                }
                selection(value->plan->root, value->plan->data());
                sql += ')';
                break;
            case ExprKind::Over:
                sql += '(';
                expr(value->children.at(0));
                sql += " OVER ( ";
                window(*value->window);
                sql += " ))";
                break;
            case ExprKind::Cast:
                sql += "CAST(";
                child(value->children.at(0), 60, true);
                sql += " AS " + value->text + ')';
                break;
        }
    }
    void list(const std::vector<ExprPtr>& expressions)
    {
        for (std::size_t i = 0; i < expressions.size(); ++i)
        {
            if (i)
                sql += ", ";
            expr(expressions[i]);
        }
    }
    void order(const Order& value)
    {
        expr(value.expression);
        sql += value.direction == 0 ? " ASC" : " DESC";
        sql += value.nulls == 0 ? " NULLS FIRST" : " NULLS LAST";
    }
    void orders(const std::vector<Order>& values)
    {
        for (std::size_t i = 0; i < values.size(); ++i)
        {
            if (i)
                sql += ", ";
            order(values[i]);
        }
    }
    void window(const Window& value)
    {
        if (!value.partitions.empty())
        {
            sql += "PARTITION BY ";
            list(value.partitions);
        }
        if (!value.orders.empty())
        {
            sql += " ORDER BY ";
            orders(value.orders);
        }
        if (value.frame)
        {
            static const std::array<const char*, 3> frames{"UNBOUNDED PRECEDING", "CURRENT ROW", "UNBOUNDED FOLLOWING"};
            sql += " ROWS BETWEEN ";
            sql += frames.at(value.frame->first);
            sql += " AND ";
            sql += frames.at(value.frame->second);
        }
    }
    void condition(const std::vector<ExprPtr>& values, std::string_view keyword)
    {
        if (values.empty())
            return;
        auto combined = values.front();
        for (std::size_t i = 1; i < values.size(); ++i)
            combined = expression(ExprKind::Binary, {combined, values[i]}, 11);
        sql += ' ';
        sql += keyword;
        sql += ' ';
        expr(combined);
    }
    void selection(const ModelBinding& root, const QueryData& data, bool force_one = false,
                   std::optional<std::uint64_t> forced_limit = std::nullopt)
    {
        if (!data.with.empty())
        {
            sql += data.recursive_cte ? "WITH RECURSIVE " : "WITH ";
            for (std::size_t i = 0; i < data.with.size(); ++i)
            {
                if (i)
                    sql += ", ";
                const auto& cte = *data.with[i];
                sql += quote_id(cte.name) + " (";
                const auto& ids = cte.declared.model->projection();
                for (std::size_t n = 0; n < ids.size(); ++n)
                {
                    if (n)
                        sql += ", ";
                    sql += quote_id(cte.declared.model->column(ids[n]).name);
                }
                sql += ") AS (";
                selection(cte.body->root, cte.body->data());
                sql += ") ";
            }
        }
        sql += "SELECT ";
        if (data.distinct)
            sql += "DISTINCT ";
        if (force_one && data.selects.empty())
            sql += '1';
        for (std::size_t i = 0; i < data.selects.size(); ++i)
        {
            if (i)
                sql += ", ";
            expr(data.selects[i].expression);
            sql += " AS " + quote_id(data.selects[i].name);
        }
        sql += " FROM " + table_sql(*root.model);
        for (const auto& join : data.joins)
        {
            if (join.alias.left)
            {
                sql += " LEFT JOIN (SELECT ";
                const auto& columns = join.alias.model.model->columns();
                for (std::size_t i = 0; i < columns.size(); ++i)
                {
                    if (i)
                        sql += ", ";
                    sql += quote_id(columns[i].name);
                }
                if (!columns.empty())
                    sql += ", ";
                sql += "1 AS " + quote_id(join.alias.marker) + " FROM " + table_sql(*join.alias.model.model) + ") AS " +
                       quote_id(join.alias.name);
            }
            else
                sql += " INNER JOIN " + table_sql(*join.alias.model.model) + " AS " + quote_id(join.alias.name);
            sql += " ON ";
            expr(join.condition);
        }
        condition(data.predicates, "WHERE");
        if (!data.groups.empty())
        {
            sql += " GROUP BY ";
            list(data.groups);
        }
        condition(data.having, "HAVING");
        static const std::array<const char*, 4> sets{" UNION ", " UNION ALL ", " INTERSECT ", " EXCEPT "};
        for (const auto& [op, plan] : data.sets)
        {
            sql += sets.at(op);
            if (profile == Profile::PostgreSql)
                sql += '(';
            selection(plan->root, plan->data());
            if (profile == Profile::PostgreSql)
                sql += ')';
        }
        if (!data.orders.empty())
        {
            sql += " ORDER BY ";
            orders(data.orders);
        }
        auto limit = forced_limit ? forced_limit : data.limit;
        if (limit)
        {
            sql += " LIMIT ";
            Bytes bytes;
            append_integer<std::uint64_t>(bytes, *limit);
            parameter(make_slot(scalar(3), 3, std::move(bytes)));
        }
        if (data.offset)
        {
            sql += " OFFSET ";
            Bytes bytes;
            append_integer<std::uint64_t>(bytes, *data.offset);
            parameter(make_slot(scalar(3), 3, std::move(bytes)));
        }
        if (data.lock)
        {
            static const std::array<const char*, 4> modes{" FOR UPDATE", " FOR SHARE", " FOR NO KEY UPDATE",
                                                          " FOR KEY SHARE"};
            sql += modes.at(data.lock->first);
            if (data.lock->second == 1)
                sql += " NOWAIT";
            else if (data.lock->second == 2)
                sql += " SKIP LOCKED";
        }
    }
    void terminal(const NativePlan& plan)
    {
        const auto& data = plan.data();
        if (plan.terminal <= 1 || plan.terminal == 8)
            selection(plan.root, data);
        else if (plan.terminal == 2 || plan.terminal == 3)
        {
            const auto bound = plan.terminal == 2 ? 1ULL : 2ULL;
            selection(plan.root, data, false, std::min<std::uint64_t>(data.limit.value_or(bound), bound));
        }
        else if (plan.terminal == 4)
        {
            sql += "SELECT COUNT(*) FROM (";
            selection(plan.root, data, !plan.projection);
            sql += ") AS \"_nw_count\"";
        }
        else if (plan.terminal == 5)
        {
            sql += profile == Profile::PostgreSql ? "SELECT CAST((CASE WHEN (EXISTS(" : "SELECT (CASE WHEN (EXISTS(";
            selection(plan.root, data, !plan.projection);
            sql += ")) THEN 1 ELSE 0 END)";
            if (profile == Profile::PostgreSql)
                sql += " AS bigint)";
        }
        else if (plan.terminal == 6)
        {
            sql += "UPDATE " + table_sql(*plan.root.model) + " SET ";
            for (std::size_t i = 0; i < data.assignments.size(); ++i)
            {
                if (i)
                    sql += ", ";
                sql += quote_id(data.assignments[i].first) + " = ";
                expr(data.assignments[i].second);
            }
            condition(data.predicates, "WHERE");
        }
        else if (plan.terminal == 7)
        {
            sql += "DELETE FROM " + table_sql(*plan.root.model);
            condition(data.predicates, "WHERE");
        }
        else
            unsupported("terminal");
    }
};
} // namespace

std::shared_ptr<NativeCte> NativeCte::create(ModelBinding declared, std::uint32_t table_id, const NativeQuery& body,
                                             std::uint32_t recursive)
{
    const auto& data = body.impl_->data;
    if (recursive == 1)
    {
        const auto hash = declared.model->hash();
        auto direct = [&](const ModelBinding& root, const QueryData& value)
        {
            std::size_t n = root.model->hash() == hash ? 1 : 0;
            for (const auto& node : value.nodes)
                if (node.valid && node.kind == NodeKind::Alias && node.alias.model.model->hash() == hash)
                    ++n;
            return n;
        };
        auto nested = [&](const QueryData& value)
        {
            std::size_t n = 0;
            for (const auto& plan : value.subqueries)
                n += recursive_references(plan->root, plan->data(), hash);
            return n;
        };
        if (data.sets.size() != 1 || data.sets.front().first > 1 || direct(body.root(), data) + nested(data) != 0)
            invalid("recursive CTE requires anchor UNION member");
        const auto& member = *data.sets.front().second;
        const auto& md = member.data();
        const bool has_ctes_or_windows =
            std::any_of(md.nodes.begin(), md.nodes.end(), [](const auto& node)
                        { return node.valid && (node.kind == NodeKind::Cte || node.kind == NodeKind::Window); });
        if (direct(member.root, md) != 1 || nested(md) != 0 || !md.sets.empty() || has_ctes_or_windows || md.grouped ||
            md.aggregate_nodes != 0)
            invalid("recursive CTE requires one supported self-reference");
    }
    if (!body.impl_->capabilities.cte)
        unsupported("CTE capability");
    if (recursive == 1 && !body.impl_->capabilities.recursive_cte)
        unsupported("recursive CTE capability");
    declared.ensure_source(body.source());
    if (declared.model->table_id() != table_id || recursive > 1)
        invalid("CTE declaration/recursive flag");
    auto plan = body.clone_selected()->seal(8);
    if (!plan->projection || !row_codecs_match(*declared.model, *plan->projection->projection))
        invalid("CTE declared/body shape");
    auto result = std::make_shared<NativeCte>();
    result->source = declared.source;
    result->name = declared.model->table();
    result->declared = std::move(declared);
    result->body = std::move(plan);
    result->recursive = recursive == 1;
    return result;
}
Bytes NativePlan::parameter_schema() const
{
    std::string text = "{\"version\":1,\"parameters\":[";
    bool first = true;
    for (const auto& [id, p] : parameters)
    {
        (void)id;
        if (!first)
            text += ',';
        first = false;
        text += "{\"id\":" + std::to_string(p.id) + ",\"name\":" + json_quote(p.name) +
                ",\"codec\":" + std::to_string(p.codec.id) +
                ",\"codec_config\":" + p.codec.configuration.value_or("null") +
                ",\"nullable\":" + (p.codec.nullable ? "true" : "false") + '}';
    }
    return bytes_of(text + "]}");
}
Expectation NativePlan::parameter_expectation() const
{
    Expectation value{1, source.profile, sha256(parameter_schema()), 0, 0, {}, 1};
    for (const auto& [id, p] : parameters)
        value.descriptors.push_back({id, p.codec, p.bound_codec});
    return value;
}
std::pair<std::string, std::vector<SlotPtr>> NativePlan::render_shape() const
{
    Renderer renderer(source.profile);
    renderer.terminal(*this);
    return {std::move(renderer.sql), std::move(renderer.arguments)};
}
const CompiledPlan& NativePlan::compiled() const
{
    std::call_once(impl_->once,
                   [&]
                   {
                       try
                       {
                           auto [sql, arguments] = render_shape();
                           if (sql.size() > impl_->limits.bytes || arguments.size() > impl_->limits.parameters)
                               capacity("compiled query shape bound");
                           auto shape = std::make_shared<CompiledShape>();
                           shape->sql = std::move(sql);
                           auto& identity = shape->identity;
                           identity = bytes_of("NWDB-COMPILED-SHAPE-v1");
                           append_integer(identity, source.engine);
                           append_integer(identity, source.source);
                           append_integer(identity, source.generation);
                           append_integer(identity, static_cast<std::uint32_t>(source.profile));
                           append_integer(identity, terminal);
                           std::set<Hash> hashes;
                           metadata_hashes(*this, hashes);
                           append_integer(identity, static_cast<std::uint32_t>(hashes.size()));
                           for (const auto& hash : hashes)
                               identity.insert(identity.end(), hash.begin(), hash.end());
                           auto projection_hash = projection ? projection->projection->hash() : Hash{};
                           identity.insert(identity.end(), projection_hash.begin(), projection_hash.end());
                           append_text(identity, shape->sql);
                           append_bytes(identity, parameter_schema());
                           std::vector<Descriptor> descriptors;
                           for (std::size_t i = 0; i < arguments.size(); ++i)
                           {
                               const auto& slot = *arguments[i];
                               const auto id = static_cast<std::uint32_t>(i + 1);
                               descriptors.push_back({id, slot.codec, slot.bound_codec});
                               append_integer(identity, id);
                               append_integer(identity, slot.bound_codec);
                               append_integer(identity, slot.codec.id);
                               identity.push_back(slot.codec.nullable ? 1 : 0);
                               append_text(identity, slot.codec.configuration.value_or(""));
                               append_integer(identity, slot.parameter.value_or(0));
                           }
                           shape->expectation = {1, source.profile, sha256(identity), 0, 0, std::move(descriptors), 1};
                           shape->charge = identity.size() + shape->sql.size() + sizeof(CompiledShape);
                           for (const auto& descriptor : shape->expectation.descriptors)
                               shape->charge += sizeof(Descriptor) + descriptor.codec.configuration.value_or("").size();
                           impl_->compiled =
                               std::make_unique<CompiledPlan>(CompiledPlan{std::move(shape), std::move(arguments)});
                       }
                       catch (...)
                       {
                           impl_->compile_error = std::current_exception();
                       }
                   });
    if (impl_->compile_error)
        std::rethrow_exception(impl_->compile_error);
    return *impl_->compiled;
}
std::shared_ptr<const CompiledShape> NativePlan::compiled_shape() const
{
    return compiled().shape;
}
RenderedQuery NativePlan::render(SourceIdentity session, ByteView values, ShapeCache* cache) const
try
{
    const auto& compilation = compiled();
    auto shape = cache ? cache->acquire(*this) : compilation.shape;
    if (terminal == 8 && !prepared_supported)
        unsupported("prepared-shape capability");
    if (source != session)
        state_error("plan/session source mismatch");
    if (values.size() > impl_->limits.bytes || parameters.size() > impl_->limits.parameters)
        capacity("execution parameter bound");
    auto batch = Batch::parse(values, parameter_expectation());
    if (terminal != 8)
    {
        bool expected = false;
        if (!impl_->submitted->compare_exchange_strong(expected, true, std::memory_order_acq_rel))
            state_error("ordinary plan already submitted");
    }
    std::map<std::uint32_t, std::optional<Bytes>> supplied;
    std::size_t column = 0;
    for (const auto& [id, p] : parameters)
    {
        (void)p;
        auto cell = batch.cell(0, column++);
        supplied.emplace(id, cell ? std::optional<Bytes>(Bytes(cell->begin(), cell->end())) : std::nullopt);
    }
    std::vector<std::optional<Bytes>> cells;
    for (const auto& slot : compilation.arguments)
    {
        if (slot->parameter)
        {
            auto it = supplied.find(*slot->parameter);
            if (it == supplied.end())
                invalid("missing execution parameter");
            cells.push_back(it->second);
        }
        else
            cells.push_back(slot->literal);
    }
    auto bytes = Batch::encode(shape->expectation, 1, cells);
    if (bytes.size() > impl_->limits.bytes)
        capacity("rendered command byte bound");
    return {shape->sql, std::move(bytes), shape->expectation, projection, terminal, timeout_ns};
}
catch (const Error& error)
{
    binding_error(error);
}

struct ShapeCache::Impl
{
    struct Entry
    {
        Hash key;
        std::shared_ptr<const CompiledShape> shape;
    };
    Hash abi;
    std::size_t maximum_entries = 0;
    std::size_t maximum_bytes = 0;
    std::size_t bytes = 0;
    std::deque<Entry> entries;
};
ShapeCache::ShapeCache(std::size_t maximum_entries, std::size_t maximum_bytes, Hash abi)
    : impl_(std::make_unique<Impl>())
{
    if (maximum_entries == 0 || maximum_entries > MAX_NODES || maximum_bytes == 0 ||
        maximum_bytes > (std::size_t{1} << 30))
        invalid("compiled shape cache bounds");
    if (abi == Hash{})
        invalid("compiled shape ABI identity");
    impl_->abi = abi;
    impl_->maximum_entries = maximum_entries;
    impl_->maximum_bytes = maximum_bytes;
}
ShapeCache::~ShapeCache() = default;
std::pair<std::size_t, std::size_t> ShapeCache::counts() const
{
    return {impl_->entries.size(), impl_->bytes};
}
void ShapeCache::clear()
{
    impl_->entries.clear();
    impl_->bytes = 0;
}
std::shared_ptr<const CompiledShape> ShapeCache::acquire(const NativePlan& plan)
{
    auto shape = plan.compiled_shape();
    Bytes input(impl_->abi.begin(), impl_->abi.end());
    input.insert(input.end(), shape->identity.begin(), shape->identity.end());
    auto key = sha256(input);
    auto found =
        std::find_if(impl_->entries.begin(), impl_->entries.end(), [&](const auto& entry) { return entry.key == key; });
    if (found != impl_->entries.end())
    {
        auto entry = *found;
        impl_->entries.erase(found);
        if (entry.shape->identity != shape->identity)
        {
            impl_->bytes -= entry.shape->charge;
            invalid("compiled shape hash collision");
        }
        auto result = entry.shape;
        impl_->entries.push_back(std::move(entry));
        return result;
    }
    if (shape->charge > impl_->maximum_bytes)
        return shape;
    while (impl_->entries.size() >= impl_->maximum_entries || shape->charge > impl_->maximum_bytes - impl_->bytes)
    {
        impl_->bytes -= impl_->entries.front().shape->charge;
        impl_->entries.pop_front();
    }
    impl_->bytes += shape->charge;
    impl_->entries.push_back({key, shape});
    return shape;
}

static std::string decode_interpolated_sql_impl(ByteView bytes)
{
    if (bytes.size() < 4)
        invalid("interpolation header");
    const auto count = read_u32(bytes, 0);
    if (count > 8193)
        capacity("interpolation segment count");
    std::size_t at = 4;
    std::string output;
    for (std::uint32_t i = 0; i < count; ++i)
    {
        if (at > bytes.size() || bytes.size() - at < 8)
            invalid("interpolation segment");
        const auto kind = read_u32(bytes, at);
        const auto length = read_u32(bytes, at + 4);
        at += 8;
        if (length > bytes.size() - at)
            invalid("interpolation range");
        auto data = bytes.subspan(at, length);
        at += length;
        if (kind == 0)
        {
            if (std::find(data.begin(), data.end(), 1) != data.end() ||
                std::find(data.begin(), data.end(), 2) != data.end())
                invalid("reserved interpolation marker");
            std::string text(data.begin(), data.end());
            (void)codepoints(text);
            output += text;
        }
        else if (kind == 1 && length == 4)
        {
            const auto id = read_u32(data, 0);
            if (id == 0)
                invalid("interpolation ID");
            output += '\1';
            output += "@p" + std::to_string(id - 1);
            output += '\2';
        }
        else
            invalid("interpolation segment kind");
    }
    if (at != bytes.size())
        invalid("interpolation trailing bytes");
    return output;
}
static SqlTemplateResult render_sql_template_impl(std::string_view input,
                                                  const std::map<std::string, std::uint32_t>& names, Profile profile,
                                                  std::uint32_t command_kind)
{
    if (input.empty() || input.find('\0') != std::string_view::npos)
        invalid("SQL template");
    (void)codepoints(input);
    SqlTemplateResult result;
    result.sql.reserve(input.size());
    std::set<std::uint32_t> used;
    std::optional<std::string> first;
    bool ended = false;
    std::size_t i = 0;
    auto preserve = [&](std::size_t start, std::size_t end)
    {
        auto value = input.substr(start, end - start);
        if (value.find('\1') != std::string_view::npos || value.find('\2') != std::string_view::npos)
            invalid("interpolation inside SQL quotation or comment");
        result.sql += value;
    };
    auto parameter = [&](std::string_view name)
    {
        auto found = names.find(std::string(name));
        if (found == names.end())
            invalid("missing named parameter");
        used.insert(found->second);
        result.parameter_ids.push_back(found->second);
        result.sql += (profile == Profile::PostgreSql ? "$" : "?") + std::to_string(found->second);
    };
    while (i < input.size())
    {
        const auto b = static_cast<unsigned char>(input[i]);
        if (ascii_space(b))
        {
            result.sql += input[i++];
            continue;
        }
        if (input.substr(i, 2) == "--")
        {
            const auto start = i;
            i += 2;
            while (i < input.size() && input[i] != '\n')
                ++i;
            preserve(start, i);
            continue;
        }
        if (input.substr(i, 2) == "/*")
        {
            const auto start = i;
            std::uint32_t depth = 1;
            i += 2;
            while (i < input.size() && depth > 0)
            {
                if (input.substr(i, 2) == "/*")
                {
                    ++depth;
                    i += 2;
                }
                else if (input.substr(i, 2) == "*/")
                {
                    --depth;
                    i += 2;
                }
                else
                    ++i;
                if (depth > 64)
                    capacity("SQL comment nesting");
            }
            if (depth != 0)
                invalid("unterminated SQL comment");
            preserve(start, i);
            continue;
        }
        if (ended)
            invalid("multiple SQL statements");
        if (b == ';')
        {
            ended = true;
            result.sql += input[i++];
            continue;
        }
        if (b == '\'' || b == '"' || b == '`' || b == '[')
        {
            const auto start = i;
            const auto end = b == '[' ? ']' : static_cast<char>(b);
            ++i;
            bool closed = false;
            while (i < input.size())
            {
                if (input[i] == end)
                {
                    if (i + 1 < input.size() && input[i + 1] == end)
                        i += 2;
                    else
                    {
                        ++i;
                        closed = true;
                        break;
                    }
                }
                else if (input[i] == '\\' && b == '\'')
                    invalid("ambiguous backslash string requires standard SQL quoting");
                else
                    ++i;
            }
            if (!closed)
                invalid("unterminated SQL quotation");
            preserve(start, i);
            continue;
        }
        if (b == '$')
        {
            const auto start = i;
            auto end = i + 1;
            while (end < input.size() && (ascii_alnum(static_cast<unsigned char>(input[end])) || input[end] == '_'))
                ++end;
            if (end >= input.size() || input[end] != '$')
                invalid("provider positional parameter is not a named template");
            auto tag = input.substr(start, end - start + 1);
            auto closing = input.find(tag, end + 1);
            if (closing == std::string_view::npos)
                invalid("unterminated dollar quote");
            i = closing + tag.size();
            preserve(start, i);
            continue;
        }
        if (b == 1)
        {
            auto end = input.find('\2', i + 1);
            if (end == std::string_view::npos || i + 1 >= input.size() || input[i + 1] != '@')
                invalid("interpolation parameter");
            parameter(input.substr(i + 2, end - i - 2));
            i = end + 1;
            continue;
        }
        if (b == 2)
            invalid("unmatched interpolation marker");
        if (b == '@')
        {
            const auto start = ++i;
            while (i < input.size() && (ascii_alnum(static_cast<unsigned char>(input[i])) || input[i] == '_'))
                ++i;
            parameter(input.substr(start, i - start));
            continue;
        }
        if (b == '?')
            invalid("untyped positional placeholder");
        if (!first && (ascii_alpha(b) || b == '_'))
        {
            const auto start = i;
            while (i < input.size() && (ascii_alnum(static_cast<unsigned char>(input[i])) || input[i] == '_'))
                ++i;
            first = std::string(input.substr(start, i - start));
            std::transform(first->begin(), first->end(), first->begin(),
                           [](unsigned char c) { return static_cast<char>(c >= 'a' && c <= 'z' ? c - 'a' + 'A' : c); });
            preserve(start, i);
            continue;
        }
        result.sql += input[i++];
    }
    if (used.size() != names.size())
        invalid("unused named parameter or interpolation in quoted text");
    if (!first)
        invalid("empty SQL statement");
    static const std::set<std::string> transaction{"BEGIN", "COMMIT", "END", "ROLLBACK", "SAVEPOINT", "RELEASE"};
    static const std::set<std::string> mutation{"SET",     "RESET",  "PRAGMA",   "ATTACH",  "DETACH",    "VACUUM",
                                                "DISCARD", "LISTEN", "UNLISTEN", "PREPARE", "DEALLOCATE"};
    static const std::set<std::string> rows{"SELECT", "VALUES", "SHOW", "EXPLAIN"};
    if (transaction.contains(*first))
        invalid("use the tracked transaction API");
    if (command_kind != 2 && mutation.contains(*first))
        invalid("session mutation requires explicit classification");
    if (command_kind != 0 && rows.contains(*first))
        invalid("affected-count command has rows");
    return result;
}

std::string decode_interpolated_sql(ByteView bytes)
{
    try
    {
        return decode_interpolated_sql_impl(bytes);
    }
    catch (Error& error)
    {
        error.code = error.status == NW_DOMAIN_ERROR ? 6 : 0;
        throw;
    }
}
SqlTemplateResult render_sql_template(std::string_view input, const std::map<std::string, std::uint32_t>& names,
                                      Profile profile, std::uint32_t command_kind)
{
    try
    {
        return render_sql_template_impl(input, names, profile, command_kind);
    }
    catch (Error& error)
    {
        error.code = error.status == NW_DOMAIN_ERROR ? 6 : 0;
        throw;
    }
}

} // namespace nw
