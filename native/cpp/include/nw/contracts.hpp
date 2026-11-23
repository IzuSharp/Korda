#ifndef NW_CONTRACTS_HPP
#define NW_CONTRACTS_HPP

#include "korda_abi_v1.h"

#include <array>
#include <cstddef>
#include <cstdint>
#include <exception>
#include <initializer_list>
#include <map>
#include <memory>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace nw
{

using Bytes = std::vector<std::uint8_t>;
using ByteView = std::span<const std::uint8_t>;
using Hash = std::array<std::uint8_t, 32>;

inline constexpr std::size_t MAX_BYTES = 4 * 1024 * 1024;
inline constexpr std::size_t MAX_NODES = 4096;

struct ErrorFields
{
    std::uint32_t domain = 0;
    std::int32_t code = 0;
    std::uint64_t correlation_id = 0;
};

struct NativeBulkProgress
{
    std::uint64_t rows = 0;
    std::uint32_t batches = 0;
    bool committed = false;
    std::uint32_t outcome = 0;
    [[nodiscard]] Bytes encode() const;
};

class Error : public std::exception
{
  public:
    std::uint32_t status;
    std::uint32_t code;
    std::string message;
    std::optional<std::string> provider;
    std::uint64_t correlation;
    std::shared_ptr<Error> cleanup;
    std::optional<NativeBulkProgress> bulk;

    // The two-argument form is the existing binding failure category.
    Error(std::uint32_t status, std::string message);
    Error(std::uint32_t status, std::uint32_t code, std::string message);
    [[nodiscard]] const char* what() const noexcept override;
    [[nodiscard]] ErrorFields fields() const noexcept;
    [[nodiscard]] static Error invalid(std::string message);
    [[nodiscard]] static Error state(std::string message);
    [[nodiscard]] static Error closed();
    [[nodiscard]] static Error capacity();
    [[nodiscard]] static Error canceled();
    [[nodiscard]] static Error unsupported();
};

enum class Profile : std::uint32_t
{
    Sqlite = 1,
    PostgreSql = 2
};
[[nodiscard]] Profile profile_from(std::uint32_t value);

struct SourceIdentity
{
    std::uint64_t engine = 0;
    std::uint64_t source = 0;
    std::uint32_t generation = 0;
    Profile profile = Profile::Sqlite;
    void validate() const;
    bool operator==(const SourceIdentity&) const = default;
};

struct Codec
{
    std::uint32_t id = 0;
    std::optional<std::string> configuration;
    bool nullable = false;
    void validate(Profile profile) const;
    [[nodiscard]] bool same_scalar(const Codec& other) const noexcept;
    bool operator==(const Codec&) const = default;
};

struct Column
{
    std::uint32_t id = 0;
    std::string name;
    Codec codec;
    std::optional<std::uint32_t> key_ordinal;
    bool generated = false;
    bool concurrency = false;
    std::optional<std::string> collation;
    std::optional<std::uint32_t> max_length;
};

// Metadata owners retain canonical bytes; no input span survives parsing.
class Model
{
  public:
    [[nodiscard]] static Model parse(ByteView bytes, Profile profile);
    [[nodiscard]] const Bytes& bytes() const noexcept;
    [[nodiscard]] Hash hash() const noexcept;
    [[nodiscard]] std::uint32_t table_id() const noexcept;
    [[nodiscard]] const std::vector<Column>& columns() const noexcept;
    [[nodiscard]] const Column& column(std::uint32_t id) const;
    [[nodiscard]] std::vector<const Column*> keys() const;
    [[nodiscard]] Bytes encode() const;
    [[nodiscard]] const std::string& logical_id() const noexcept;
    [[nodiscard]] const std::optional<std::string>& schema() const noexcept;
    [[nodiscard]] const std::string& table() const noexcept;
    [[nodiscard]] const std::vector<std::uint32_t>& projection() const noexcept;

  private:
    std::string logical_id_;
    std::uint32_t table_id_ = 0;
    std::optional<std::string> schema_;
    std::string table_;
    std::vector<Column> columns_;
    std::vector<std::uint32_t> projection_;
    Bytes canonical_;
    Hash hash_{};
};

struct ResultField
{
    std::string name;
    Codec codec;
};

class Projection
{
  public:
    [[nodiscard]] static Projection parse(ByteView bytes, Profile profile);
    [[nodiscard]] static Projection parse_scalar(ByteView bytes, Profile profile);
    [[nodiscard]] const Bytes& bytes() const noexcept;
    [[nodiscard]] Hash hash() const noexcept;
    [[nodiscard]] const std::vector<ResultField>& fields() const noexcept;
    [[nodiscard]] Bytes encode() const;

  private:
    [[nodiscard]] static Projection parse_context(ByteView bytes, Profile profile, bool scalar);
    std::vector<ResultField> fields_;
    Bytes canonical_;
    Hash hash_{};
};

class MetadataRegistry
{
  public:
    MetadataRegistry(Profile profile, std::size_t max_entries, std::size_t max_bytes);
    [[nodiscard]] std::shared_ptr<const Model> register_model(ByteView bytes, const Hash& expected_hash);
    [[nodiscard]] std::pair<std::size_t, std::size_t> counts() const noexcept;

  private:
    Profile profile_;
    std::size_t max_entries_;
    std::size_t max_bytes_;
    std::size_t bytes_ = 0;
    std::map<Hash, std::shared_ptr<const Model>> models_;
    std::map<std::uint32_t, std::string> table_ids_;
    std::map<std::uint32_t, std::string> column_ids_;
};

struct Descriptor
{
    std::uint32_t id = 0;
    Codec codec;
    std::uint32_t bound_codec = 0;
};

struct Expectation
{
    std::uint16_t kind = 0;
    Profile profile = Profile::Sqlite;
    Hash hash{};
    std::uint32_t entity_id = 0;
    std::uint32_t projection_id = 0;
    std::vector<Descriptor> descriptors;
    std::optional<std::uint32_t> exact_rows;
    void validate(std::uint32_t rows) const;
};

class Batch
{
  public:
    [[nodiscard]] static Batch parse(ByteView bytes, Expectation expected);
    [[nodiscard]] static Bytes encode(const Expectation& expected, std::uint32_t rows,
                                      const std::vector<std::optional<Bytes>>& values);
    [[nodiscard]] static Bytes encode_views(const Expectation& expected, std::uint32_t rows,
                                            std::span<const std::optional<ByteView>> values);
    [[nodiscard]] const Bytes& bytes() const noexcept;
    [[nodiscard]] std::uint32_t rows() const noexcept;
    [[nodiscard]] const Expectation& expectation() const noexcept;
    [[nodiscard]] std::optional<ByteView> cell(std::uint32_t row, std::size_t column) const;

  private:
    Bytes bytes_;
    Expectation expectation_;
    std::uint32_t rows_ = 0;
};

void validate_scalar(const Codec& codec, ByteView bytes, Profile profile);

class Relation
{
  public:
    [[nodiscard]] static Relation parse(ByteView bytes, std::shared_ptr<const Model> parent,
                                        std::shared_ptr<const Model> child);
    [[nodiscard]] const Bytes& bytes() const noexcept;
    [[nodiscard]] Hash hash() const noexcept;
    [[nodiscard]] const Model& parent() const noexcept;
    [[nodiscard]] const Model& child() const noexcept;
    [[nodiscard]] const std::vector<std::uint32_t>& parent_columns() const noexcept;
    [[nodiscard]] const std::vector<std::uint32_t>& child_columns() const noexcept;
    [[nodiscard]] const std::vector<bool>& child_nullable() const noexcept;
    [[nodiscard]] Bytes encode_identity() const;
    [[nodiscard]] Bytes encode_record() const;
    [[nodiscard]] Expectation parent_key_expectation(Profile profile,
                                                     std::span<const std::uint32_t> bound_codecs) const;

  private:
    std::shared_ptr<const Model> parent_;
    std::shared_ptr<const Model> child_;
    std::vector<std::uint32_t> parent_columns_;
    std::vector<std::uint32_t> child_columns_;
    std::vector<bool> child_nullable_;
    Bytes canonical_;
    Hash hash_{};
};

struct FieldSpec
{
    std::uint16_t id;
    std::uint16_t type;
    bool required;
};

struct ControlField
{
    std::uint16_t id;
    std::uint16_t type;
    Bytes bytes;
};

// Fields borrows only for the synchronous call that owns the input bytes.
class Fields
{
  public:
    [[nodiscard]] static Fields parse(ByteView bytes, std::uint16_t kind, std::span<const FieldSpec> allowed);
    [[nodiscard]] static Fields parse(ByteView bytes, std::uint16_t kind, std::initializer_list<FieldSpec> allowed);
    [[nodiscard]] std::optional<ByteView> get(std::uint16_t id) const noexcept;
    [[nodiscard]] ByteView bytes(std::uint16_t id) const;
    [[nodiscard]] std::uint32_t u32(std::uint16_t id) const;
    [[nodiscard]] std::uint64_t u64(std::uint16_t id) const;
    [[nodiscard]] std::string_view text(std::uint16_t id) const;
    [[nodiscard]] bool boolean(std::uint16_t id) const;
    [[nodiscard]] bool flag(std::uint16_t id) const;
    void require_set(std::initializer_list<std::uint16_t> ids) const;

  private:
    std::vector<std::pair<std::uint16_t, ByteView>> values_;
};

[[nodiscard]] std::vector<ByteView> control_fields(ByteView bytes, std::uint16_t kind,
                                                   std::span<const std::pair<std::uint16_t, std::uint16_t>> fields);
[[nodiscard]] Bytes encode_control(std::uint16_t kind, std::span<const ControlField> fields);
[[nodiscard]] Bytes encode_control(std::uint16_t kind, std::initializer_list<ControlField> fields);
[[nodiscard]] std::vector<ByteView> sequence(ByteView bytes);
[[nodiscard]] std::vector<std::uint32_t> decode_u32_vector(ByteView bytes);
[[nodiscard]] Bytes encode_u32_vector(std::span<const std::uint32_t> values);

[[nodiscard]] Hash sha256(ByteView bytes);
[[nodiscard]] std::string hex(ByteView bytes);
[[nodiscard]] std::string json_quote(std::string_view value);
[[nodiscard]] std::uint32_t stable_id(std::span<const std::optional<std::string_view>> parts);
[[nodiscard]] std::uint32_t stable_id(std::initializer_list<std::optional<std::string_view>> parts);
[[nodiscard]] bool valid_utf8(ByteView bytes) noexcept;
[[nodiscard]] std::uint16_t read_u16(ByteView bytes, std::size_t offset);
[[nodiscard]] std::uint32_t read_u32(ByteView bytes, std::size_t offset);
[[nodiscard]] std::uint64_t read_u64(ByteView bytes, std::size_t offset);
void put_u16(Bytes& bytes, std::size_t offset, std::uint16_t value);
void put_u32(Bytes& bytes, std::size_t offset, std::uint32_t value);
void put_u64(Bytes& bytes, std::size_t offset, std::uint64_t value);

enum class TransactionOutcome
{
    InProgress,
    Committed,
    RolledBack,
    Unknown
};
enum class ConnectionDisposition
{
    Usable,
    ProviderResetPending,
    Quarantined,
    Discarded
};
enum class LocalLifetime
{
    Open,
    Closing,
    NativeRetired,
    Released
};

} // namespace nw

#endif
