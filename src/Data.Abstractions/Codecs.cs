using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Korda.Data;

internal sealed record CodecDescriptor(uint Id, string Name, string? Configuration, bool Nullable)
{
    internal bool SameScalar(CodecDescriptor other) => Id == other.Id && Configuration == other.Configuration;
}

internal delegate T ScalarDecoder<T>(ReadOnlySpan<byte> bytes);

/// <summary>A closed typed selection of one precompiled logical scalar codec.</summary>
/// <typeparam name="T">The statically known CLR value type.</typeparam>
/// <remarks>Instances are immutable and shareable. IDs are logical metadata, not source-owned native handles.
/// Nullable wrappers preserve the logical ID. Configured decimal identities include precision and scale.
/// No native runtime is loaded; provider conversions and capability enforcement belong to B07.</remarks>
public sealed class ValueCodec<T>
{
    internal ValueCodec(CodecDescriptor descriptor, Func<T, byte[]> encode, ScalarDecoder<T> decode, Func<T, int, byte[]>? boundedEncode = null)
    {
        Descriptor = descriptor;
        _encode = encode;
        _decode = decode;
        _boundedEncode = boundedEncode;
    }
    private readonly Func<T, byte[]> _encode;
    private readonly ScalarDecoder<T> _decode;
    private readonly Func<T, int, byte[]>? _boundedEncode;
    internal CodecDescriptor Descriptor { get; }
    /// <summary>Gets the logical db-wire scalar ID, independent of source-bound configured IDs.</summary>
    public uint Id => Descriptor.Id;
    /// <summary>Gets the descriptive scalar codec name.</summary>
    public string Name => Descriptor.Name;
    internal byte[]? Encode(T value)
    {
        if (value is null && (Descriptor.Nullable || Id != 16))
        {
            if (!Descriptor.Nullable) throw new ArgumentNullException(nameof(value), "The codec does not allow SQL NULL.");
            return null;
        }
        return _encode(value);
    }
    // B14 requests a finite snapshot before allocating/copying each value.
    // Keep Encode unchanged for existing B01 callers.
    internal byte[]? EncodeBounded(T value, int maximumBytes)
    {
        if (maximumBytes < 0 || maximumBytes > DbWire.MaxEncodedBytes) throw Capacity();
        if (value is null && (Descriptor.Nullable || Id != 16)) return Encode(value);
        if (_boundedEncode is not null) return _boundedEncode(value, maximumBytes);
        var width = Id switch
        {
            1 => 2,
            2 or 8 or 12 or 17 => 4,
            3 or 9 or 10 or 11 or 15 => 8,
            4 => 1,
            7 or 13 or 14 => 16,
            5 => Validation.Utf8.GetByteCount((string)(object)value!),
            6 => ((byte[])(object)value!).Length,
            _ => throw new InvalidOperationException("The selected codec has no bounded snapshot encoder.")
        };
        if (width > maximumBytes) throw Capacity();
        var bytes = _encode(value);
        if (bytes.Length > maximumBytes) throw Capacity();
        return bytes;
    }
    internal static DatabaseException Capacity() => new("The value exceeds the finite snapshot byte budget.", DatabaseErrorKind.ResourceLimit);
    internal T DecodeScalar(ReadOnlySpan<byte> bytes) => _decode(bytes);
    internal T Decode(ReadOnlyMemory<byte>? value)
        => Decode(value.GetValueOrDefault().Span, value is null);
    internal T Decode(ReadOnlySpan<byte> value, bool isNull)
    {
        if (isNull)
        {
            if (!Descriptor.Nullable) throw new DatabaseException("SQL NULL is not allowed by this codec.", DatabaseErrorKind.Conversion);
            return default!;
        }
        try { return _decode(value); }
        catch (Exception e) when (e is ArgumentException or OverflowException or FormatException or JsonException)
        { throw new DatabaseException("The wire value does not match its declared codec.", DatabaseErrorKind.Conversion, innerException: e); }
    }
}

/// <summary>Factories for the fixed db-wire/1 logical scalar registry.</summary>
/// <remarks>Selections are immutable. Encoding snapshots strings, byte arrays and JSON into owned storage.
/// No provider, native loader, reflection serializer or SQL semantics are installed by these factories.</remarks>
public static class Codecs
{
    private static ValueCodec<T> Make<T>(uint id, string name, Func<T, byte[]> encode, ScalarDecoder<T> decode, string? config = null)
        => new(new(id, name, config, false), encode, decode);
    private static void Width(ReadOnlySpan<byte> bytes, int width) { if (bytes.Length != width) throw new ArgumentException("Wrong fixed width."); }
    /// <summary>Gets the exact signed 16-bit codec.</summary>
    public static ValueCodec<short> Int16 { get; } = Make<short>(1, "Int16", v => { var b = new byte[2]; BinaryPrimitives.WriteInt16LittleEndian(b, v); return b; }, b => { Width(b, 2); return BinaryPrimitives.ReadInt16LittleEndian(b); });
    /// <summary>Gets the exact signed 32-bit codec.</summary>
    public static ValueCodec<int> Int32 { get; } = Make<int>(2, "Int32", v => { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); return b; }, b => { Width(b, 4); return BinaryPrimitives.ReadInt32LittleEndian(b); });
    /// <summary>Gets the exact signed 64-bit codec.</summary>
    public static ValueCodec<long> Int64 { get; } = Make<long>(3, "Int64", v => { var b = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, v); return b; }, b => { Width(b, 8); return BinaryPrimitives.ReadInt64LittleEndian(b); });
    /// <summary>Gets the Boolean codec whose only wire bytes are zero and one.</summary>
    public static ValueCodec<bool> Boolean { get; } = Make<bool>(4, "Boolean", v => [v ? (byte)1 : (byte)0], b => { Width(b, 1); return b[0] switch { 0 => false, 1 => true, _ => throw new ArgumentException("Invalid Boolean.") }; });
    /// <summary>Gets strict UTF-8 text with no normalization or implicit SQL collation.</summary>
    public static ValueCodec<string> String { get; } = Make<string>(5, "String", Validation.Utf8.GetBytes, b => Validation.Utf8.GetString(b));
    /// <summary>Gets binary values; input and output arrays are copied at a snapshot boundary.</summary>
    public static ValueCodec<byte[]> Bytes { get; } = Make<byte[]>(6, "Bytes", v => (byte[])v.Clone(), b => b.ToArray());
    /// <summary>Gets RFC 4122 network-order UUID encoding.</summary>
    public static ValueCodec<Guid> Guid { get; } = Make<Guid>(7, "Guid", v => v.ToByteArray(bigEndian: true), b => { Width(b, 16); return new Guid(b, bigEndian: true); });
    /// <summary>Gets signed days since 1970-01-01 within the DateOnly domain.</summary>
    public static ValueCodec<DateOnly> Date { get; } = Make<DateOnly>(8, "Date", v => Int32.Encode(v.DayNumber - EpochDay)!, b => DateOnly.FromDayNumber(checked(BinaryInt32(b) + EpochDay)));
    /// <summary>Gets microseconds since midnight; submicrosecond precision rejects.</summary>
    public static ValueCodec<TimeOnly> Time { get; } = Make<TimeOnly>(9, "Time", v => { ExactTicks(v.Ticks); var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, (ulong)(v.Ticks / 10)); return b; }, b => { Width(b, 8); var n = BinaryPrimitives.ReadUInt64LittleEndian(b); if (n > 86399999999) throw new ArgumentOutOfRangeException(nameof(b)); return new TimeOnly(checked((long)n * 10)); });
    /// <summary>Gets UTC-normalized instants in Unix microseconds; no precision loss is allowed.</summary>
    public static ValueCodec<DateTimeOffset> UtcInstant { get; } = Make<DateTimeOffset>(10, "UtcInstant", v => Instant(v.UtcTicks), b => new DateTimeOffset(new DateTime(InstantTicks(b), DateTimeKind.Utc)));
    /// <summary>Gets UTC-only DateTime values; Local and Unspecified values reject.</summary>
    public static ValueCodec<DateTime> UtcDateTime { get; } = Make<DateTime>(10, "UtcDateTime", v => { if (v.Kind != DateTimeKind.Utc) throw new ArgumentException("DateTime.Kind must be Utc."); return Instant(v.Ticks); }, b => new DateTime(InstantTicks(b), DateTimeKind.Utc));
    /// <summary>Gets finite IEEE 754 binary64 values, preserving signed zero in the wire.</summary>
    public static ValueCodec<double> Double { get; } = Make<double>(11, "Double", v => { if (!double.IsFinite(v)) throw new ArgumentOutOfRangeException(nameof(v)); var b = new byte[8]; BinaryPrimitives.WriteDoubleLittleEndian(b, v); return b; }, b => { Width(b, 8); var v = BinaryPrimitives.ReadDoubleLittleEndian(b); if (!double.IsFinite(v)) throw new ArgumentException("Nonfinite value."); return v; });
    /// <summary>Gets finite IEEE 754 binary32 values, preserving signed zero in the wire.</summary>
    public static ValueCodec<float> Single { get; } = Make<float>(12, "Single", v => { if (!float.IsFinite(v)) throw new ArgumentOutOfRangeException(nameof(v)); var b = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); return b; }, b => { Width(b, 4); var v = BinaryPrimitives.ReadSingleLittleEndian(b); if (!float.IsFinite(v)) throw new ArgumentException("Nonfinite value."); return v; });
    /// <summary>Selects exact decimal numeric precision and scale; ordinary SQLite does not support this provider profile.</summary>
    /// <param name="precision">Total decimal precision from one through 29.</param>
    /// <param name="scale">Scale from zero through the smaller of 28 and precision.</param>
    /// <returns>An immutable configured logical codec.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The precision or scale is outside the declared range.</exception>
    public static ValueCodec<decimal> Decimal(byte precision, byte scale)
    {
        if (precision is < 1 or > 29) throw new ArgumentOutOfRangeException(nameof(precision));
        if (scale > Math.Min((byte)28, precision)) throw new ArgumentOutOfRangeException(nameof(scale));
        return Make<decimal>(13, "Decimal", v => { DecimalBytes.CheckFit(v, precision, scale); return DecimalBytes.Encode(v); }, b => { var v = DecimalBytes.Decode(b); DecimalBytes.CheckFit(v, precision, scale); return v; }, $"{{\"precision\":{precision},\"scale\":{scale}}}");
    }
    /// <summary>Gets exact decimal text storage; provider arithmetic, range ordering and aggregation are not implied.</summary>
    public static ValueCodec<decimal> DecimalText { get; } = Make<decimal>(14, "DecimalText", DecimalBytes.Encode, DecimalBytes.Decode);
    /// <summary>Selects exact signed Int64 fixed-scale storage, without rounding.</summary>
    /// <param name="scale">The decimal scale from zero through 18.</param>
    /// <returns>An immutable configured money codec.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scale"/> exceeds 18.</exception>
    public static ValueCodec<decimal> FixedScaleMoney(byte scale)
    {
        if (scale > 18) throw new ArgumentOutOfRangeException(nameof(scale));
        return Make<decimal>(15, "FixedScaleMoney", v => Int64.Encode(checked((long)DecimalBytes.Scaled(v, scale)))!, b => BinaryInt64(b) / DecimalBytes.Power10(scale), $"{{\"scale\":{scale}}}");
    }
    /// <summary>Maps a business value through an existing nonnullable store codec without adding a native scalar type.</summary>
    /// <typeparam name="T">The closed business value type, such as an application identifier.</typeparam>
    /// <typeparam name="TStore">The selected store codec's CLR value type.</typeparam>
    /// <param name="storage">The nonnullable codec whose identity, configuration and encoding bounds are preserved.</param>
    /// <param name="toStore">A direct synchronous conversion used before native admission.</param>
    /// <param name="fromStore">A direct synchronous conversion used while materializing an owned value.</param>
    /// <returns>An immutable codec retaining the supplied delegates and the original native scalar identity.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The storage codec already permits SQL NULL.</exception>
    /// <remarks>Nullable and NullableReference can wrap the result. Bounded writes delegate to the storage codec before byte allocation, preserving string, binary and JSON limits. User conversions are managed application code; their failures follow normal snapshot or materialization cleanup and never cross a native callback boundary.</remarks>
    public static ValueCodec<T> Convert<T, TStore>(ValueCodec<TStore> storage, Func<T, TStore> toStore, Func<TStore, T> fromStore)
    {
        ArgumentNullException.ThrowIfNull(storage); ArgumentNullException.ThrowIfNull(toStore); ArgumentNullException.ThrowIfNull(fromStore);
        if (storage.Descriptor.Nullable) throw new ArgumentException("Convert requires a nonnullable storage codec; wrap the converted codec to allow SQL NULL.", nameof(storage));
        return new(storage.Descriptor, value => storage.Encode(toStore(value))!, bytes => fromStore(storage.DecodeScalar(bytes)),
            (value, maximum) => storage.EncodeBounded(toStore(value), maximum)!);
    }
    /// <summary>Adds SQL NULL support to a value codec without changing its logical scalar identity.</summary>
    /// <typeparam name="T">A non-nullable value type.</typeparam>
    /// <param name="codec">The selected non-null scalar codec.</param>
    /// <returns>A typed nullable codec with the same configuration.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="codec"/> is null.</exception>
    public static ValueCodec<T?> Nullable<T>(ValueCodec<T> codec) where T : struct
    {
        ArgumentNullException.ThrowIfNull(codec);
        return new(codec.Descriptor with { Nullable = true }, v => codec.Encode(v!.Value)!, b => codec.DecodeScalar(b), (v, maximum) => codec.EncodeBounded(v!.Value, maximum)!);
    }
    /// <summary>Adds SQL NULL support to a reference codec while preserving its logical scalar identity.</summary>
    /// <typeparam name="T">The reference type.</typeparam>
    /// <param name="codec">The selected non-null scalar codec.</param>
    /// <returns>A nullable reference codec.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="codec"/> is null.</exception>
    public static ValueCodec<T?> NullableReference<T>(ValueCodec<T> codec) where T : class
    {
        ArgumentNullException.ThrowIfNull(codec);
        return new(codec.Descriptor with { Nullable = true }, v => codec.Encode(v!)!, b => codec.DecodeScalar(b), (v, maximum) => codec.EncodeBounded(v!, maximum)!);
    }
    /// <summary>Selects JSON serialization using explicit static metadata; JSON null is distinct from SQL NULL.</summary>
    /// <typeparam name="T">The statically serializable value.</typeparam>
    /// <param name="typeInfo">Explicit source-generated or manually authored JSON metadata.</param>
    /// <returns>An immutable typed JSON codec that retains the supplied metadata.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="typeInfo"/> is null.</exception>
    public static ValueCodec<T> Json<T>(JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return new(new(16, "Json", null, false), v => JsonSerializer.SerializeToUtf8Bytes(v, typeInfo), b => JsonSerializer.Deserialize(b, typeInfo)!, (v, maximum) => BoundedJsonEncoding.Encode(v, typeInfo, maximum));
    }
    /// <summary>Selects a checked signed Int32 enum representation, preserving undefined numeric values.</summary>
    /// <typeparam name="TEnum">A statically known CLR enum.</typeparam>
    /// <returns>An immutable enum codec; values outside Int32 reject at snapshot.</returns>
    public static ValueCodec<TEnum> EnumInt32<TEnum>() where TEnum : struct, Enum
        => Make<TEnum>(17, "EnumInt32", v => Int32.Encode(EnumValue<TEnum>.ToInt32(v))!, b => EnumValue<TEnum>.FromInt32(BinaryInt32(b)));

    private const int EpochDay = 719162;
    private const long EpochTicks = 621355968000000000;
    private static int BinaryInt32(ReadOnlySpan<byte> b) { Width(b, 4); return BinaryPrimitives.ReadInt32LittleEndian(b); }
    private static long BinaryInt64(ReadOnlySpan<byte> b) { Width(b, 8); return BinaryPrimitives.ReadInt64LittleEndian(b); }
    private static void ExactTicks(long ticks) { if (ticks % 10 != 0) throw new ArgumentException("Submicrosecond precision is not supported."); }
    private static byte[] Instant(long ticks) { ExactTicks(ticks); return Int64.Encode((ticks - EpochTicks) / 10)!; }
    private static long InstantTicks(ReadOnlySpan<byte> bytes) => checked(BinaryInt64(bytes) * 10 + EpochTicks);
}

internal static class EnumValue<T> where T : struct, Enum
{
    private static readonly TypeCode Code = Type.GetTypeCode(Enum.GetUnderlyingType(typeof(T)));
    internal static int ToInt32(T value) => Code switch
    {
        TypeCode.SByte => Unsafe.As<T, sbyte>(ref value),
        TypeCode.Byte => Unsafe.As<T, byte>(ref value),
        TypeCode.Int16 => Unsafe.As<T, short>(ref value),
        TypeCode.UInt16 => Unsafe.As<T, ushort>(ref value),
        TypeCode.Int32 => Unsafe.As<T, int>(ref value),
        TypeCode.UInt32 => checked((int)Unsafe.As<T, uint>(ref value)),
        TypeCode.Int64 => checked((int)Unsafe.As<T, long>(ref value)),
        TypeCode.UInt64 => checked((int)Unsafe.As<T, ulong>(ref value)),
        _ => throw new ArgumentException("Unsupported enum storage.")
    };
    internal static T FromInt32(int value)
    {
        switch (Code)
        {
            case TypeCode.SByte: var a = checked((sbyte)value); return Unsafe.As<sbyte, T>(ref a);
            case TypeCode.Byte: var b = checked((byte)value); return Unsafe.As<byte, T>(ref b);
            case TypeCode.Int16: var c = checked((short)value); return Unsafe.As<short, T>(ref c);
            case TypeCode.UInt16: var d = checked((ushort)value); return Unsafe.As<ushort, T>(ref d);
            case TypeCode.Int32: return Unsafe.As<int, T>(ref value);
            case TypeCode.UInt32: var e = checked((uint)value); return Unsafe.As<uint, T>(ref e);
            case TypeCode.Int64: long f = value; return Unsafe.As<long, T>(ref f);
            case TypeCode.UInt64: var g = checked((ulong)value); return Unsafe.As<ulong, T>(ref g);
            default: throw new ArgumentException("Unsupported enum storage.");
        }
    }
}

internal static class DecimalBytes
{
    internal static (BigInteger Coefficient, byte Scale, bool Negative) Parts(decimal value)
    {
        Span<int> bits = stackalloc int[4]; decimal.GetBits(value, bits);
        var coefficient = (BigInteger)(uint)bits[0] | ((BigInteger)(uint)bits[1] << 32) | ((BigInteger)(uint)bits[2] << 64);
        var scale = (byte)((bits[3] >> 16) & 255); var negative = bits[3] < 0;
        if (coefficient.IsZero) return (BigInteger.Zero, 0, false);
        while (scale > 0 && coefficient % 10 == 0) { coefficient /= 10; scale--; }
        return (coefficient, scale, negative);
    }
    internal static byte[] Encode(decimal value)
    {
        var (coefficient, scale, negative) = Parts(value); var bytes = new byte[16];
        coefficient.TryWriteBytes(bytes.AsSpan(0, 12), out _, isUnsigned: true, isBigEndian: false);
        bytes[12] = scale; bytes[13] = negative ? (byte)1 : (byte)0; return bytes;
    }
    internal static decimal Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 16 || bytes[12] > 28 || bytes[13] > 1 || bytes[14] != 0 || bytes[15] != 0) throw new ArgumentException("Invalid decimal encoding.");
        var low = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var middle = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        var high = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        // The wire coefficient is only 96 bits. Checking its canonical form does
        // not require materializing and re-encoding a decimal through BigInteger.
        var coefficient = (UInt128)low | (UInt128)middle << 32 | (UInt128)high << 64;
        if (coefficient == 0 ? bytes[12] != 0 || bytes[13] != 0 : bytes[12] != 0 && coefficient % 10 == 0)
            throw new ArgumentException("Noncanonical decimal encoding.");
        return new decimal(unchecked((int)low), unchecked((int)middle), unchecked((int)high), bytes[13] == 1, bytes[12]);
    }
    internal static BigInteger Scaled(decimal value, byte targetScale)
    {
        var (coefficient, scale, negative) = Parts(value);
        if (scale > targetScale) throw new ArgumentException("Conversion would round the value.");
        var scaled = coefficient * BigInteger.Pow(10, targetScale - scale);
        return negative ? -scaled : scaled;
    }
    internal static void CheckFit(decimal value, byte precision, byte scale)
    {
        if (precision is < 1 or > 29 || scale > 28 || scale > precision)
        {
            // Public configured codecs reject these bounds before this call.
            // Retain the prior behavior for any other internal caller.
            if (BigInteger.Abs(Scaled(value, scale)) >= BigInteger.Pow(10, precision)) throw new OverflowException("Decimal exceeds configured precision.");
            return;
        }
        Span<int> bits = stackalloc int[4]; decimal.GetBits(value, bits);
        var coefficient = (UInt128)(uint)bits[0] | (UInt128)(uint)bits[1] << 32 | (UInt128)(uint)bits[2] << 64;
        if (coefficient == 0) return;
        var valueScale = (bits[3] >> 16) & 255;
        while (valueScale > 0 && coefficient % 10 == 0) { coefficient /= 10; valueScale--; }
        if (valueScale > scale) throw new ArgumentException("Conversion would round the value.");
        // coefficient * 10^(scale-valueScale) < 10^precision is equivalent
        // to this bounded comparison; both powers fit in UInt128.
        UInt128 limit = 1;
        for (var i = 0; i < precision - (scale - valueScale); i++) limit *= 10;
        if (coefficient >= limit) throw new OverflowException("Decimal exceeds configured precision.");
    }
    internal static decimal Power10(byte scale) { decimal result = 1; for (var i = 0; i < scale; i++) result *= 10; return result; }
}

internal static class Validation
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static string Name(string value, string parameter)
    {
        ArgumentNullException.ThrowIfNull(value, parameter);
        if (value.Length == 0) throw new ArgumentException("The identifier cannot be empty.", parameter);
        try { _ = Utf8.GetByteCount(value); }
        catch (EncoderFallbackException e) { throw new ArgumentException("The identifier contains invalid UTF-16.", parameter, e); }
        return value;
    }
    internal static string Parameter(string name)
    {
        Name(name, nameof(name));
        if (name.Length > 128 || !(char.IsAsciiLetter(name[0]) || name[0] == '_') || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new ArgumentException("Parameter names must match [A-Za-z_][A-Za-z0-9_]* and be at most 128 bytes.", nameof(name));
        return name;
    }
}
