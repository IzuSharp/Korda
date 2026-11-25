#ifndef NW_PROVIDER_TEST_SUPPORT_HPP
#define NW_PROVIDER_TEST_SUPPORT_HPP

#include "nw/provider.hpp"

namespace nw::test
{

using Row = std::vector<std::optional<Bytes>>;

// The retained low-level assertions want an owning row for comparison. Obtain
// it through the production arena path; no second provider read/decode loop is
// compiled for these tests or retained in the product.
inline std::optional<Row> next_row(ProviderStatement& statement, Profile profile, const std::vector<Codec>& codecs,
                                   std::size_t field_bytes, const CallContext& context)
{
    Expectation expected{2, profile, {}, 0, 1, {}, {}};
    for (std::size_t ordinal = 0; ordinal < codecs.size(); ++ordinal)
    {
        const auto& codec = codecs[ordinal];
        expected.descriptors.push_back({static_cast<std::uint32_t>(ordinal), codec,
                                        codec.configuration ? 1024U + static_cast<std::uint32_t>(ordinal) : codec.id});
    }
    BatchWriter writer(expected, 1, MAX_BYTES);
    const auto result = statement.append_next(writer, codecs, field_bytes, context);
    if (result.state == RowAppendState::End)
        return std::nullopt;
    const auto bytes = writer.finish();
    const auto batch = BatchView::parse(bytes, expected);
    Row row;
    row.reserve(codecs.size());
    for (std::size_t ordinal = 0; ordinal < codecs.size(); ++ordinal)
    {
        const auto value = batch.cell(0, ordinal);
        row.push_back(value ? std::optional<Bytes>(Bytes(value->begin(), value->end())) : std::nullopt);
    }
    return row;
}

} // namespace nw::test

#endif
