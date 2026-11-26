#ifndef NW_PARAMETER_TEST_SUPPORT_HPP
#define NW_PARAMETER_TEST_SUPPORT_HPP

#include "nw/provider.hpp"

namespace nw::test
{

inline std::vector<Scalar> decode_parameters(ByteView bytes, const Expectation& expected,
                                             std::size_t maximum_parameters, std::size_t maximum_bytes,
                                             std::size_t maximum_field_bytes)
{
    // Retained codec fixtures keep their original admission/validation order.
    // Product execution only accepts and validates its owned typed arguments.
    if (maximum_parameters == 0 || maximum_parameters > MAX_NODES || maximum_bytes == 0 || maximum_bytes > MAX_BYTES ||
        maximum_field_bytes == 0 || maximum_field_bytes > maximum_bytes)
        throw Error::capacity();
    if ((expected.kind != 1 && expected.kind != 3 && expected.kind != 4) ||
        (expected.exact_rows && *expected.exact_rows != 1))
        throw Error(NW_DOMAIN_ERROR, 4, "parameters require one admitted value or key row");
    if (bytes.size() > maximum_bytes || expected.descriptors.size() > maximum_parameters)
        throw Error::capacity();
    auto one_row = expected;
    one_row.exact_rows = 1;
    for (const auto& descriptor : expected.descriptors)
    {
        descriptor.codec.validate(expected.profile);
        if (expected.profile == Profile::PostgreSql && descriptor.codec.id == 14)
            throw Error::unsupported();
    }
    const auto batch = BatchView::parse(bytes, one_row);
    std::vector<Scalar> parameters;
    parameters.reserve(expected.descriptors.size());
    for (std::size_t index = 0; index < expected.descriptors.size(); ++index)
    {
        const auto value = batch.cell(0, index);
        parameters.push_back({expected.descriptors[index].codec,
                              value ? std::optional<Bytes>(Bytes(value->begin(), value->end())) : std::nullopt});
    }
    validate_parameters(parameters, expected, bytes.size(), maximum_parameters, maximum_bytes, maximum_field_bytes);
    return parameters;
}

} // namespace nw::test

#endif
