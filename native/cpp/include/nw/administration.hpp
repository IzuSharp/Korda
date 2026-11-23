#ifndef NW_ADMINISTRATION_HPP
#define NW_ADMINISTRATION_HPP

#include "nw/provider.hpp"

namespace nw
{

struct AdministrationLimits
{
    std::size_t rows = 1000;
    std::size_t bytes = 1024 * 1024;
    std::size_t field_bytes = MAX_BYTES;
    std::size_t parameter_bytes = MAX_BYTES;
    std::size_t nodes = MAX_NODES;
    std::uint64_t command_timeout_ns = 30'000'000'000;
};

struct MigrationEntry
{
    std::int64_t version = 0;
    std::string description;
    std::string sql;
    bool non_transactional = false;
    Hash sql_hash{};
    Bytes checksum;
};

struct MigrationBundle
{
    std::uint32_t provider = 0;
    std::vector<MigrationEntry> migrations;
    Hash content_hash{};
};

struct AdministrationRequest
{
    Profile profile = Profile::Sqlite;
    std::uint32_t action = 0;
    std::optional<MigrationBundle> bundle;
    std::optional<std::int64_t> target;
    std::uint64_t lock_timeout_ns = 30'000'000'000;
    bool allow_non_transactional = false;
    std::optional<std::string> schema;
    std::vector<Model> previous_models;
    std::vector<Model> current_models;
};

// Parsing owns every byte before the asynchronous operation is published.
[[nodiscard]] AdministrationRequest parse_administration(Profile profile, std::uint32_t action, ByteView bytes,
                                                         const AdministrationLimits& limits);

// Draft compilation is pure: no provider connection, database metadata read or SQL execution.
[[nodiscard]] Bytes draft_schema(const AdministrationRequest& request, const AdministrationLimits& limits,
                                 const CallContext& context);

// Host grants a dedicated administrative connection (with SQLite busy timeout
// equal to request.lock_timeout_ns) and always closes it after this call,
// releasing PostgreSQL session advisory locks even on failure.
[[nodiscard]] Bytes administer(const AdministrationRequest& request, ProviderConnection& connection,
                               const AdministrationLimits& limits, const CallContext& context);

} // namespace nw

#endif
