#include "nw/scalar.hpp"

#include <bit>
#include <iostream>
#include <limits>
#include <stdexcept>

namespace
{
using namespace nw;

void require(bool value, const char* message)
{
    if (!value)
        throw std::runtime_error(message);
}

std::size_t rejected = 0;
template <typename F> void rejects(std::uint32_t code, F&& action)
{
    try
    {
        action();
    }
    catch (const Error& error)
    {
        require(error.status == NW_DOMAIN_ERROR && error.code == code, "provider error classification");
        ++rejected;
        return;
    }
    throw std::runtime_error("lossy provider conversion accepted");
}

template <typename T> Bytes number(T value, bool big = false)
{
    using U = std::make_unsigned_t<T>;
    const auto bits = std::bit_cast<U>(value);
    Bytes bytes(sizeof(T));
    for (std::size_t i = 0; i < sizeof(T); ++i)
        bytes[big ? sizeof(T) - 1 - i : i] = static_cast<std::uint8_t>(bits >> (i * 8));
    return bytes;
}

Bytes bytes(std::string_view text)
{
    return Bytes(text.begin(), text.end());
}
Codec codec(std::uint32_t id)
{
    Codec result{id, std::nullopt, false};
    if (id == 13)
        result.configuration = "{\"precision\":29,\"scale\":0}";
    if (id == 15)
        result.configuration = "{\"scale\":2}";
    return result;
}

Bytes numeric(std::int16_t weight, std::uint16_t sign, std::uint16_t scale, std::initializer_list<std::uint16_t> groups)
{
    auto result = number(static_cast<std::uint16_t>(groups.size()), true);
    for (const auto& part : {number(weight, true), number(sign, true), number(scale, true)})
        result.insert(result.end(), part.begin(), part.end());
    for (const auto group : groups)
    {
        const auto part = number(group, true);
        result.insert(result.end(), part.begin(), part.end());
    }
    return result;
}

Bytes decimal(std::uint32_t coefficient, std::uint8_t scale = 0, bool negative = false)
{
    Bytes result(16);
    put_u32(result, 0, coefficient);
    result[12] = scale;
    result[13] = negative;
    return result;
}

void roundtrip_sqlite(std::uint32_t id, const Bytes& value, int type)
{
    const auto c = codec(id);
    require(sqlite_decode(c, type, sqlite_encode(c, value)) == value, "SQLite scalar roundtrip");
}
} // namespace

int main()
{
    try
    {
        roundtrip_sqlite(1, number(std::int16_t{-7}), 1);
        roundtrip_sqlite(2, number(std::int32_t{-2147483647}), 1);
        roundtrip_sqlite(3, number(std::numeric_limits<std::int64_t>::min()), 1);
        roundtrip_sqlite(4, {1}, 1);
        roundtrip_sqlite(5, bytes("e\u0301 🦀"), 3);
        roundtrip_sqlite(5, {'a', 0, 'b'}, 3);
        roundtrip_sqlite(6, {0, 1, 255}, 4);
        roundtrip_sqlite(6, {}, 4);
        roundtrip_sqlite(
            7, {0, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff}, 4);
        for (const auto date : {-719162, -1, 0, 11016, 2932896})
            roundtrip_sqlite(8, number(std::int32_t{date}), 3);
        for (const auto time : {std::uint64_t{0}, std::uint64_t{1}, std::uint64_t{86399999999}})
            roundtrip_sqlite(9, number(time), 3);
        for (const auto instant :
             {std::int64_t{-62135596800000000}, std::int64_t{-1}, std::int64_t{0}, std::int64_t{253402300799999999}})
            roundtrip_sqlite(10, number(instant), 1);
        roundtrip_sqlite(11, number(std::bit_cast<std::uint64_t>(-0.0)), 2);
        roundtrip_sqlite(11, number(std::bit_cast<std::uint64_t>(std::numeric_limits<double>::max())), 2);
        roundtrip_sqlite(12, number(std::bit_cast<std::uint32_t>(1.5F)), 2);
        roundtrip_sqlite(12, number(std::bit_cast<std::uint32_t>(-0.0F)), 2);
        roundtrip_sqlite(14, decimal(123, 2, true), 3);
        roundtrip_sqlite(14, decimal(1, 28, true), 3);
        Bytes maximum(16, 255);
        maximum[12] = maximum[13] = maximum[14] = maximum[15] = 0;
        roundtrip_sqlite(14, maximum, 3);
        roundtrip_sqlite(15, number(std::int64_t{-123456}), 1);
        roundtrip_sqlite(16, bytes(" {\"x\":1e400} \n"), 3);
        roundtrip_sqlite(17, number(std::int32_t{-42}), 1);
        require(std::get<std::string>(sqlite_encode(codec(8), number(std::int32_t{-719162}))) == "0001-01-01",
                "date minimum text");
        require(std::get<std::string>(sqlite_encode(codec(8), number(std::int32_t{2932896}))) == "9999-12-31",
                "date maximum text");
        require(std::get<std::string>(sqlite_encode(codec(9), number(std::uint64_t{123456}))) == "00:00:00.123456",
                "time text");
        rejects(4, [] { (void)sqlite_decode(codec(5), 1, std::int64_t{42}); });
        rejects(4, [] { (void)sqlite_decode(codec(3), 1, std::string("42")); });
        rejects(3, [] { (void)sqlite_decode(codec(1), 1, std::int64_t{32768}); });
        rejects(3, [] { (void)sqlite_decode(codec(2), 1, std::int64_t{2147483648}); });
        rejects(3, [] { (void)sqlite_decode(codec(4), 1, std::int64_t{2}); });
        rejects(3, [] { (void)sqlite_decode(codec(5), 3, std::string("\xff")); });
        rejects(3, [] { (void)sqlite_decode(codec(7), 4, Bytes{1}); });
        for (const auto* text : {"0000-01-01", "1900-02-29", "2020-2-29", "2020-02-30", "2020-01-01 "})
            rejects(3, [&] { (void)sqlite_decode(codec(8), 3, std::string(text)); });
        for (const auto* text :
             {"24:00:00.000000", "23:59:60.000000", "00:00:00.0000010", "00:00:00", "00:00:00.abcdef"})
            rejects(3, [&] { (void)sqlite_decode(codec(9), 3, std::string(text)); });
        for (const auto* text : {"-0", "0.0", "01", "1.", ".1", "1.20", "+1", "1e2", "79228162514264337593543950336",
                                 "0.00000000000000000000000000001"})
            rejects(3, [&] { (void)sqlite_decode(codec(14), 3, std::string(text)); });
        rejects(3, [] { (void)sqlite_decode(codec(12), 2, 0.1); });
        rejects(3, [] { (void)sqlite_decode(codec(11), 2, std::numeric_limits<double>::quiet_NaN()); });
        rejects(3, [] { (void)sqlite_decode(codec(11), 2, std::numeric_limits<double>::infinity()); });
        rejects(3, [] { (void)sqlite_encode(codec(13), decimal(1)); });
        rejects(5, [] { (void)sqlite_decode(codec(13), 3, std::string("1")); });

        require(postgres_oid(codec(1)) == 21 && postgres_oid(codec(2)) == 23 && postgres_oid(codec(3)) == 20 &&
                    postgres_oid(codec(16)) == 3802,
                "PostgreSQL exact parameter OIDs");
        require(postgres_encode(codec(6), {Bytes{0, 0xff}}) == "\\x00ff", "bytea text");
        require(postgres_encode(codec(7), Bytes{0, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb,
                                                0xcc, 0xdd, 0xee, 0xff}) == "00112233-4455-6677-8899-aabbccddeeff",
                "UUID text");
        require(postgres_encode(codec(10), number(std::int64_t{-1})) == "1969-12-31T23:59:59.999999+00:00",
                "negative epoch microsecond text");
        require(postgres_encode(codec(11), number(std::bit_cast<std::uint64_t>(-0.0))) == "-0",
                "floating negative zero");
        require(postgres_decode(codec(1), 21, number(std::int16_t{-7}, true)) == number(std::int16_t{-7}),
                "PG Int16 binary");
        require(postgres_decode(codec(2), 23, number(std::int32_t{-42}, true)) == number(std::int32_t{-42}),
                "PG Int32 binary");
        require(postgres_decode(codec(3), 20, number(std::numeric_limits<std::int64_t>::min(), true)) ==
                    number(std::numeric_limits<std::int64_t>::min()),
                "PG Int64 binary");
        require(postgres_decode(codec(8), 1082, number(std::int32_t{0}, true)) == number(std::int32_t{10957}),
                "PG date epoch");
        require(postgres_decode(codec(8), 1082, number(std::int32_t{-730119}, true)) == number(std::int32_t{-719162}),
                "PG date min");
        require(postgres_decode(codec(8), 1082, number(std::int32_t{2921939}, true)) == number(std::int32_t{2932896}),
                "PG date max");
        require(postgres_decode(codec(10), 1184, number(std::int64_t{0}, true)) ==
                    number(std::int64_t{946684800000000}),
                "PG instant epoch");
        require(postgres_decode(codec(10), 1184, number(std::int64_t{-63082281600000000}, true)) ==
                    number(std::int64_t{-62135596800000000}),
                "PG instant minimum");
        for (const auto oid : {25U, 1042U, 1043U, 19U, 705U})
            require(postgres_decode(codec(5), oid, bytes("hello")) == bytes("hello"), "PG compatible text type");
        require(postgres_decode(codec(16), 114, bytes(" \t{\"v\":1e400}\r\n")) == bytes("{\"v\":1e400}"),
                "PG JSON RawValue whitespace");
        auto jsonb = bytes("{\"v\":1}");
        jsonb.insert(jsonb.begin(), 1);
        require(postgres_decode(codec(16), 3802, jsonb) == bytes("{\"v\":1}"), "PG JSONB binary");
        rejects(4, [] { (void)postgres_decode(codec(3), 23, number(std::int32_t{1}, true)); });
        rejects(4, [] { (void)postgres_decode(codec(5), 9999, bytes("unknown")); });
        rejects(3, [] { (void)postgres_encode(codec(5), Bytes{'a', 0}); });
        rejects(3, [] { (void)postgres_encode(codec(14), decimal(1)); });
        rejects(5, [] { (void)postgres_decode(codec(14), 25, bytes("1")); });
        rejects(3, [] { (void)postgres_decode(codec(8), 1082, number(std::int32_t{-730120}, true)); });
        rejects(3,
                [] { (void)postgres_decode(codec(8), 1082, number(std::numeric_limits<std::int32_t>::max(), true)); });
        rejects(3, [] { (void)postgres_decode(codec(9), 1083, number(std::int64_t{86400000000}, true)); });
        rejects(3,
                [] { (void)postgres_decode(codec(10), 1184, number(std::numeric_limits<std::int64_t>::max(), true)); });
        rejects(3,
                []
                {
                    (void)postgres_decode(
                        codec(11), 701,
                        number(std::bit_cast<std::uint64_t>(std::numeric_limits<double>::quiet_NaN()), true));
                });
        rejects(3, [] { (void)postgres_decode(codec(16), 3802, Bytes{2, '{', '}'}); });

        auto scaled = codec(13);
        scaled.configuration = "{\"precision\":18,\"scale\":2}";
        require(postgres_decode(scaled, 1700, numeric(0, 0, 2, {12, 3400})) == decimal(1234, 2),
                "PG base10000 decimal");
        require(postgres_decode(scaled, 1700, numeric(1, 0x4000, 8, {0, 12, 3400, 0})) == decimal(1234, 2, true),
                "PG redundant numeric groups exact normalization");
        require(postgres_decode(scaled, 1700, numeric(4, 0x4000, 10, {0, 0})) == decimal(0), "PG zero normalization");
        require(postgres_decode(codec(13), 1700, numeric(7, 0, 0, {7, 9228, 1625, 1426, 4337, 5935, 4395, 335})) ==
                    maximum,
                "PG maximum 96-bit coefficient");
        require(postgres_encode(codec(13), maximum) == "79228162514264337593543950335",
                "PG exact maximum decimal text");
        auto tiny = codec(13);
        tiny.configuration = "{\"precision\":29,\"scale\":28}";
        require(postgres_decode(tiny, 1700, numeric(-7, 0, 28, {1})) == decimal(1, 28), "PG smallest positive decimal");
        rejects(3,
                [&]
                {
                    (void)postgres_decode(codec(13), 1700,
                                          numeric(7, 0, 0, {7, 9228, 1625, 1426, 4337, 5935, 4395, 336}));
                });
        rejects(3, [&] { (void)postgres_decode(tiny, 1700, numeric(-8, 0, 29, {1000})); });
        rejects(3, [&] { (void)postgres_decode(scaled, 1700, numeric(0, 0, 3, {12, 3450})); });
        rejects(3, [&] { (void)postgres_decode(scaled, 1700, numeric(0, 0xc000, 0, {})); });
        rejects(3, [&] { (void)postgres_decode(scaled, 1700, numeric(0, 0, 0, {10000})); });
        rejects(3, [&] { (void)postgres_decode(scaled, 1700, numeric(0, 0, 16384, {})); });
        rejects(3, [&] { (void)postgres_decode(scaled, 1700, Bytes{0}); });
        std::cout
            << "{\"status\":\"passed\",\"scalar_kinds\":17,\"rejected_lossy_or_invalid_values\":" << rejected
            << ",\"numeric_96bit_exact\":true,\"sqlite_canonical_storage\":true,\"postgres_binary_protocol\":true}\n";
    }
    catch (const std::exception& error)
    {
        std::cerr << "FAIL " << error.what() << '\n';
        return 1;
    }
}
