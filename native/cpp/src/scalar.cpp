#include "nw/scalar.hpp"

#include <algorithm>
#include <array>
#include <bit>
#include <charconv>
#include <chrono>
#include <cmath>
#include <limits>
#include <string_view>
#include <type_traits>

namespace nw
{
namespace
{

[[noreturn]] void conversion(const char* message)
{
    throw Error(NW_DOMAIN_ERROR, 3, message);
}
[[noreturn]] void shape(const char* message)
{
    throw Error(NW_DOMAIN_ERROR, 4, message);
}

void check_profile(const Codec& codec, Profile profile, bool decoding)
{
    if ((profile == Profile::Sqlite && codec.id == 13) || (profile == Profile::PostgreSql && codec.id == 14))
        throw Error(NW_DOMAIN_ERROR, decoding ? 5 : 3, "unsupported provider scalar profile");
    try
    {
        codec.validate(profile);
    }
    catch (const Error&)
    {
        conversion("invalid provider scalar codec");
    }
}

void checked_scalar(const Codec& codec, ByteView bytes, Profile profile)
{
    try
    {
        validate_scalar(codec, bytes, profile);
    }
    catch (const Error&)
    {
        conversion("invalid canonical provider scalar");
    }
}

template <typename T> T integer(ByteView bytes, bool big_endian = false)
{
    static_assert(std::is_integral_v<T>);
    if (bytes.size() != sizeof(T))
        conversion("scalar width mismatch");
    using U = std::make_unsigned_t<T>;
    U bits = 0;
    for (std::size_t i = 0; i < bytes.size(); ++i)
    {
        const auto index = big_endian ? bytes.size() - 1 - i : i;
        bits |= static_cast<U>(bytes[index]) << (i * 8);
    }
    return std::bit_cast<T>(bits);
}

template <typename T> Bytes little(T value)
{
    static_assert(std::is_integral_v<T>);
    const auto bits = std::bit_cast<std::make_unsigned_t<T>>(value);
    Bytes result(sizeof(T));
    for (std::size_t i = 0; i < result.size(); ++i)
        result[i] = static_cast<std::uint8_t>(bits >> (i * 8));
    return result;
}

Bytes as_bytes(std::string_view text)
{
    return Bytes(text.begin(), text.end());
}
std::string as_text(ByteView bytes)
{
    return std::string(bytes.begin(), bytes.end());
}

// CLR Decimal has a 96-bit coefficient. Three limbs keep this portable to
// clang-cl without relying on a compiler-specific 128-bit integer type.
struct Decimal
{
    std::array<std::uint32_t, 3> coefficient{};
    std::uint8_t scale = 0;
    bool negative = false;

    bool zero() const
    {
        return coefficient == std::array<std::uint32_t, 3>{};
    }

    void append_digit(unsigned digit)
    {
        std::uint64_t carry = digit;
        for (auto& word : coefficient)
        {
            const std::uint64_t value = static_cast<std::uint64_t>(word) * 10 + carry;
            word = static_cast<std::uint32_t>(value);
            carry = value >> 32;
        }
        if (carry != 0)
            conversion("decimal coefficient outside CLR domain");
    }

    unsigned divide_ten()
    {
        std::uint64_t remainder = 0;
        for (std::size_t i = coefficient.size(); i-- > 0;)
        {
            const auto value = (remainder << 32) | coefficient[i];
            coefficient[i] = static_cast<std::uint32_t>(value / 10);
            remainder = value % 10;
        }
        return static_cast<unsigned>(remainder);
    }

    Bytes encode() const
    {
        Bytes result(16);
        for (std::size_t i = 0; i < coefficient.size(); ++i)
            put_u32(result, i * 4, coefficient[i]);
        result[12] = scale;
        result[13] = negative;
        return result;
    }

    std::string text() const
    {
        if (zero())
            return "0";
        auto copy = *this;
        std::string digits;
        while (!copy.zero())
            digits.push_back(static_cast<char>('0' + copy.divide_ten()));
        std::reverse(digits.begin(), digits.end());
        if (scale > 0)
        {
            if (digits.size() <= scale)
                digits.insert(0, static_cast<std::size_t>(scale) + 1 - digits.size(), '0');
            digits.insert(digits.size() - scale, 1, '.');
        }
        if (negative)
            digits.insert(0, 1, '-');
        return digits;
    }
};

Decimal decimal(ByteView bytes)
{
    if (bytes.size() != 16)
        conversion("decimal width mismatch");
    return {{read_u32(bytes, 0), read_u32(bytes, 4), read_u32(bytes, 8)}, bytes[12], bytes[13] == 1};
}

Decimal decimal_text(std::string_view text)
{
    if (text.empty() || text.size() > 31)
        conversion("SQLite decimal text range");
    Decimal value;
    std::size_t i = 0;
    if (text.front() == '-')
    {
        value.negative = true;
        ++i;
    }
    bool point = false;
    std::size_t digits = 0;
    for (; i < text.size(); ++i)
    {
        const char ch = text[i];
        if (ch == '.' && !point && digits > 0)
        {
            point = true;
            continue;
        }
        if (ch < '0' || ch > '9')
            conversion("SQLite decimal is not canonical fixed-point text");
        value.append_digit(static_cast<unsigned>(ch - '0'));
        ++digits;
        if (point && ++value.scale > 28)
            conversion("SQLite decimal scale outside CLR domain");
    }
    if (digits == 0 || text.back() == '.')
        conversion("SQLite decimal is not canonical fixed-point text");
    // Equality with the normalized representation rejects +, redundant zeros,
    // negative zero, exponents, whitespace, and fractional trailing zeros.
    if (value.zero())
    {
        value.scale = 0;
        value.negative = false;
    }
    else
    {
        while (value.scale > 0)
        {
            auto next = value;
            if (next.divide_ten() != 0)
                break;
            --next.scale;
            value = next;
        }
    }
    if (value.text() != text)
        conversion("SQLite decimal is not canonical fixed-point text");
    return value;
}

std::string padded(unsigned value, std::size_t count)
{
    auto text = std::to_string(value);
    if (text.size() > count)
        conversion("date or time component outside range");
    text.insert(0, count - text.size(), '0');
    return text;
}

std::string date_text(std::int32_t days)
{
    const std::chrono::year_month_day date{std::chrono::sys_days{std::chrono::days{days}}};
    const int year = static_cast<int>(date.year());
    if (!date.ok() || year < 1 || year > 9999)
        conversion("date outside CLR domain");
    return padded(static_cast<unsigned>(year), 4) + '-' + padded(static_cast<unsigned>(date.month()), 2) + '-' +
           padded(static_cast<unsigned>(date.day()), 2);
}

unsigned digits(std::string_view text, std::size_t offset, std::size_t count)
{
    unsigned result = 0;
    for (std::size_t i = offset; i < offset + count; ++i)
    {
        if (text[i] < '0' || text[i] > '9')
            conversion("noncanonical date or time text");
        result = result * 10 + static_cast<unsigned>(text[i] - '0');
    }
    return result;
}

std::int32_t parse_date(std::string_view text)
{
    if (text.size() != 10 || text[4] != '-' || text[7] != '-')
        conversion("SQLite date is not canonical ISO text");
    const auto year = digits(text, 0, 4);
    const std::chrono::year_month_day date{std::chrono::year{static_cast<int>(year)},
                                           std::chrono::month{digits(text, 5, 2)},
                                           std::chrono::day{digits(text, 8, 2)}};
    if (year == 0 || !date.ok())
        conversion("SQLite date outside CLR domain");
    return static_cast<std::int32_t>(std::chrono::sys_days{date}.time_since_epoch().count());
}

std::string time_text(std::uint64_t micros)
{
    if (micros > 86'399'999'999ULL)
        conversion("time outside CLR domain");
    const auto seconds = micros / 1'000'000;
    return padded(static_cast<unsigned>(seconds / 3600), 2) + ':' +
           padded(static_cast<unsigned>(seconds / 60 % 60), 2) + ':' + padded(static_cast<unsigned>(seconds % 60), 2) +
           '.' + padded(static_cast<unsigned>(micros % 1'000'000), 6);
}

std::uint64_t parse_time(std::string_view text)
{
    if (text.size() != 15 || text[2] != ':' || text[5] != ':' || text[8] != '.')
        conversion("SQLite time is not canonical microsecond text");
    const auto hour = digits(text, 0, 2), minute = digits(text, 3, 2), second = digits(text, 6, 2);
    if (hour > 23 || minute > 59 || second > 59)
        conversion("time outside CLR domain");
    return (static_cast<std::uint64_t>(hour) * 3600 + minute * 60 + second) * 1'000'000 + digits(text, 9, 6);
}

std::string instant_text(std::int64_t micros)
{
    constexpr std::int64_t per_day = 86'400'000'000;
    auto days = micros / per_day;
    auto remaining = micros % per_day;
    if (remaining < 0)
    {
        --days;
        remaining += per_day;
    }
    return date_text(static_cast<std::int32_t>(days)) + 'T' + time_text(static_cast<std::uint64_t>(remaining)) +
           "+00:00";
}

template <typename T> std::string float_text(T value)
{
    if (!std::isfinite(value))
        conversion("nonfinite provider float");
    std::array<char, 64> buffer{};
    const auto result = std::to_chars(buffer.data(), buffer.data() + buffer.size(), value, std::chars_format::general,
                                      std::numeric_limits<T>::max_digits10);
    if (result.ec != std::errc{})
        conversion("provider float text encoding failed");
    return std::string(buffer.data(), result.ptr);
}

Decimal numeric(ByteView bytes)
{
    if (bytes.size() < 8)
        conversion("truncated PG numeric");
    const auto count = integer<std::uint16_t>(bytes.first(2), true);
    if (bytes.size() != 8 + static_cast<std::size_t>(count) * 2)
        conversion("invalid PG numeric length");
    const auto weight = integer<std::int16_t>(bytes.subspan(2, 2), true);
    const auto sign = integer<std::uint16_t>(bytes.subspan(4, 2), true);
    const auto scale = integer<std::uint16_t>(bytes.subspan(6, 2), true);
    if ((sign != 0 && sign != 0x4000) || scale > 16383)
        conversion("nonfinite or invalid PG numeric");
    std::vector<std::uint16_t> groups;
    groups.reserve(count);
    for (std::size_t i = 0; i < count; ++i)
    {
        const auto digit = integer<std::uint16_t>(bytes.subspan(8 + i * 2, 2), true);
        if (digit > 9999)
            conversion("invalid PG numeric digit");
        groups.push_back(digit);
    }
    while (!groups.empty() && groups.back() == 0)
        groups.pop_back();
    if (groups.empty())
        return {};
    std::size_t leading = 0;
    while (groups[leading] == 0)
        ++leading;
    const auto last = groups.size() - 1;
    // Each group is four exact decimal digits. Remove only value-preserving
    // trailing zeros; overflow or a scale above 28 is rejected, never rounded.
    std::string coefficient = std::to_string(groups[leading]);
    for (std::size_t i = leading + 1; i <= last; ++i)
        coefficient += padded(groups[i], 4);
    auto exponent = 4 * (static_cast<std::int32_t>(weight) - static_cast<std::int32_t>(last));
    while (coefficient.back() == '0')
    {
        coefficient.pop_back();
        ++exponent;
    }
    if (exponent < -28 || (exponent < 0 && -exponent > scale) || coefficient.size() > 29 ||
        (exponent > 0 && static_cast<std::size_t>(exponent) + coefficient.size() > 29))
        conversion("numeric precision loss");
    if (exponent > 0)
        coefficient.append(static_cast<std::size_t>(exponent), '0');
    Decimal value;
    value.negative = sign == 0x4000;
    value.scale = exponent < 0 ? static_cast<std::uint8_t>(-exponent) : 0;
    for (const char ch : coefficient)
        value.append_digit(static_cast<unsigned>(ch - '0'));
    return value;
}

bool postgres_compatible(const Codec& codec, std::uint32_t oid)
{
    if (codec.id == 5)
        return oid == 25 || oid == 1043 || oid == 1042 || oid == 19 || oid == 705;
    if (codec.id == 16)
        return oid == 114 || oid == 3802;
    return postgres_oid(codec) == oid;
}

} // namespace

StorageValue sqlite_encode(const Codec& codec, ByteView bytes)
{
    check_profile(codec, Profile::Sqlite, false);
    checked_scalar(codec, bytes, Profile::Sqlite);
    switch (codec.id)
    {
        case 1:
            return static_cast<std::int64_t>(integer<std::int16_t>(bytes));
        case 2:
        case 17:
            return static_cast<std::int64_t>(integer<std::int32_t>(bytes));
        case 3:
        case 10:
        case 15:
            return integer<std::int64_t>(bytes);
        case 4:
            return static_cast<std::int64_t>(bytes[0]);
        case 5:
        case 16:
            return as_text(bytes);
        case 6:
        case 7:
            return Bytes(bytes.begin(), bytes.end());
        case 8:
            return date_text(integer<std::int32_t>(bytes));
        case 9:
            return time_text(integer<std::uint64_t>(bytes));
        case 11:
            return std::bit_cast<double>(integer<std::uint64_t>(bytes));
        case 12:
            return static_cast<double>(std::bit_cast<float>(integer<std::uint32_t>(bytes)));
        case 14:
            return decimal(bytes).text();
        default:
            conversion("unsupported SQLite logical codec");
    }
}

Bytes sqlite_decode(const Codec& codec, int sqlite_type, const StorageValue& value)
{
    check_profile(codec, Profile::Sqlite, true);
    int expected_type = 0;
    switch (codec.id)
    {
        case 1:
        case 2:
        case 3:
        case 4:
        case 10:
        case 15:
        case 17:
            expected_type = 1;
            break;
        case 11:
        case 12:
            expected_type = 2;
            break;
        case 5:
        case 8:
        case 9:
        case 14:
        case 16:
            expected_type = 3;
            break;
        case 6:
        case 7:
            expected_type = 4;
            break;
        default:
            conversion("unsupported SQLite logical codec");
    }
    if (sqlite_type != expected_type || value.index() != static_cast<std::size_t>(expected_type - 1))
        shape("SQLite runtime storage type mismatch");
    Bytes result;
    switch (codec.id)
    {
        case 1:
        {
            const auto number = std::get<std::int64_t>(value);
            if (number < std::numeric_limits<std::int16_t>::min() || number > std::numeric_limits<std::int16_t>::max())
                conversion("SQLite Int16 outside CLR domain");
            result = little(static_cast<std::int16_t>(number));
            break;
        }
        case 2:
        case 17:
        {
            const auto number = std::get<std::int64_t>(value);
            if (number < std::numeric_limits<std::int32_t>::min() || number > std::numeric_limits<std::int32_t>::max())
                conversion("SQLite Int32 outside CLR domain");
            result = little(static_cast<std::int32_t>(number));
            break;
        }
        case 3:
        case 10:
        case 15:
            result = little(std::get<std::int64_t>(value));
            break;
        case 4:
        {
            const auto number = std::get<std::int64_t>(value);
            if (number != 0 && number != 1)
                conversion("SQLite Boolean must be zero or one");
            result = {static_cast<std::uint8_t>(number)};
            break;
        }
        case 5:
        case 16:
            result = as_bytes(std::get<std::string>(value));
            break;
        case 6:
        case 7:
            result = std::get<Bytes>(value);
            break;
        case 8:
            result = little(parse_date(std::get<std::string>(value)));
            break;
        case 9:
            result = little(parse_time(std::get<std::string>(value)));
            break;
        case 11:
            result = little(std::bit_cast<std::uint64_t>(std::get<double>(value)));
            break;
        case 12:
        {
            const auto number = std::get<double>(value);
            const auto narrow = static_cast<float>(number);
            if (!std::isfinite(narrow) || static_cast<double>(narrow) != number)
                conversion("SQLite Single conversion would lose precision");
            result = little(std::bit_cast<std::uint32_t>(narrow));
            break;
        }
        case 14:
            result = decimal_text(std::get<std::string>(value)).encode();
            break;
        default:
            conversion("unsupported SQLite logical codec");
    }
    checked_scalar(codec, result, Profile::Sqlite);
    return result;
}

std::uint32_t postgres_oid(const Codec& codec)
{
    check_profile(codec, Profile::PostgreSql, false);
    switch (codec.id)
    {
        case 1:
            return 21;
        case 2:
        case 17:
            return 23;
        case 3:
        case 15:
            return 20;
        case 4:
            return 16;
        case 5:
            return 25;
        case 6:
            return 17;
        case 7:
            return 2950;
        case 8:
            return 1082;
        case 9:
            return 1083;
        case 10:
            return 1184;
        case 11:
            return 701;
        case 12:
            return 700;
        case 13:
            return 1700;
        case 16:
            return 3802;
        default:
            conversion("unsupported PostgreSQL logical codec");
    }
}

std::string postgres_type_name(std::uint32_t oid)
{
    switch (oid)
    {
        case 16:
            return "BOOL";
        case 17:
            return "BYTEA";
        case 19:
            return "NAME";
        case 20:
            return "INT8";
        case 21:
            return "INT2";
        case 23:
            return "INT4";
        case 25:
            return "TEXT";
        case 114:
            return "JSON";
        case 700:
            return "FLOAT4";
        case 701:
            return "FLOAT8";
        case 705:
            return "UNKNOWN";
        case 1042:
            return "BPCHAR";
        case 1043:
            return "VARCHAR";
        case 1082:
            return "DATE";
        case 1083:
            return "TIME";
        case 1114:
            return "TIMESTAMP";
        case 1184:
            return "TIMESTAMPTZ";
        case 1700:
            return "NUMERIC";
        case 2950:
            return "UUID";
        case 3802:
            return "JSONB";
        default:
            return {};
    }
}

std::string postgres_encode(const Codec& codec, ByteView bytes)
{
    check_profile(codec, Profile::PostgreSql, false);
    checked_scalar(codec, bytes, Profile::PostgreSql);
    switch (codec.id)
    {
        case 1:
            return std::to_string(integer<std::int16_t>(bytes));
        case 2:
        case 17:
            return std::to_string(integer<std::int32_t>(bytes));
        case 3:
        case 15:
            return std::to_string(integer<std::int64_t>(bytes));
        case 4:
            return bytes[0] == 1 ? "true" : "false";
        case 5:
            if (std::find(bytes.begin(), bytes.end(), 0) != bytes.end())
                conversion("PostgreSQL text cannot contain NUL");
            return as_text(bytes);
        case 6:
            return "\\x" + hex(bytes);
        case 7:
        {
            auto text = hex(bytes);
            for (const auto position : {20U, 16U, 12U, 8U})
                text.insert(position, 1, '-');
            return text;
        }
        case 8:
            return date_text(integer<std::int32_t>(bytes));
        case 9:
            return time_text(integer<std::uint64_t>(bytes));
        case 10:
            return instant_text(integer<std::int64_t>(bytes));
        case 11:
            return float_text(std::bit_cast<double>(integer<std::uint64_t>(bytes)));
        case 12:
            return float_text(std::bit_cast<float>(integer<std::uint32_t>(bytes)));
        case 13:
            return decimal(bytes).text();
        case 16:
            return as_text(bytes);
        default:
            conversion("unsupported PostgreSQL logical codec");
    }
}

Bytes postgres_decode(const Codec& codec, std::uint32_t oid, ByteView bytes)
{
    check_profile(codec, Profile::PostgreSql, true);
    if (!postgres_compatible(codec, oid))
        shape("PostgreSQL result type mismatch");
    Bytes result;
    switch (codec.id)
    {
        case 1:
            result = little(integer<std::int16_t>(bytes, true));
            break;
        case 2:
        case 17:
            result = little(integer<std::int32_t>(bytes, true));
            break;
        case 3:
        case 15:
            result = little(integer<std::int64_t>(bytes, true));
            break;
        case 4:
            if (bytes.size() != 1 || bytes[0] > 1)
                conversion("invalid PostgreSQL Boolean");
            result = {bytes[0]};
            break;
        case 5:
        case 6:
        case 7:
            result = Bytes(bytes.begin(), bytes.end());
            break;
        case 8:
        {
            const auto days = integer<std::int32_t>(bytes, true);
            if (days < -730119 || days > 2921939)
                conversion("PostgreSQL date outside CLR domain");
            result = little(static_cast<std::int32_t>(days + 10957));
            break;
        }
        case 9:
        {
            const auto micros = integer<std::int64_t>(bytes, true);
            if (micros < 0 || micros > 86'399'999'999LL)
                conversion("PostgreSQL time outside CLR domain");
            result = little(static_cast<std::uint64_t>(micros));
            break;
        }
        case 10:
        {
            const auto micros = integer<std::int64_t>(bytes, true);
            if (micros < -63'082'281'600'000'000LL || micros > 252'455'615'999'999'999LL)
                conversion("PostgreSQL instant outside CLR domain");
            result = little(micros + 946'684'800'000'000LL);
            break;
        }
        case 11:
            result = little(integer<std::uint64_t>(bytes, true));
            break;
        case 12:
            result = little(integer<std::uint32_t>(bytes, true));
            break;
        case 13:
            result = numeric(bytes).encode();
            break;
        case 16:
            if (oid == 3802)
            {
                if (bytes.empty() || bytes[0] != 1)
                    conversion("invalid PostgreSQL JSONB version");
                bytes = bytes.subspan(1);
            }
            // SQLx's Json<RawValue> publishes the JSON token, excluding surrounding
            // RFC 8259 whitespace; interior bytes and duplicate keys are retained.
            while (!bytes.empty() &&
                   (bytes.front() == ' ' || bytes.front() == '\t' || bytes.front() == '\r' || bytes.front() == '\n'))
                bytes = bytes.subspan(1);
            while (!bytes.empty() &&
                   (bytes.back() == ' ' || bytes.back() == '\t' || bytes.back() == '\r' || bytes.back() == '\n'))
                bytes = bytes.first(bytes.size() - 1);
            result = Bytes(bytes.begin(), bytes.end());
            break;
        default:
            conversion("unsupported PostgreSQL logical codec");
    }
    checked_scalar(codec, result, Profile::PostgreSql);
    return result;
}

} // namespace nw
