using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace NativeWeave.Data;

internal sealed record ParameterValue(uint Id, string Name, CodecDescriptor Codec, byte[]? Bytes);
internal sealed record TemplateSegment(uint Kind, byte[] Bytes);

/// <summary>A single-writer ordered collection of typed owned parameter snapshots.</summary>
/// <remarks>Names are case-sensitive ASCII without @. Add copies mutable values immediately; command construction
/// snapshots the collection. Provider placeholder scanning and missing/unused-hole checks remain native B02/B03 work.</remarks>
public sealed class SqlParameters
{
    private readonly List<ParameterValue> _values = [];
    /// <summary>Creates an empty ordered parameter collection.</summary>
    public SqlParameters() { }
    /// <summary>Copies one value using an explicit scalar codec and a unique logical name.</summary>
    /// <typeparam name="T">The statically known value type.</typeparam>
    /// <param name="name">An ASCII name matching [A-Za-z_][A-Za-z0-9_]*, at most 128 bytes.</param>
    /// <param name="value">The value; SQL NULL requires an explicit nullable codec.</param>
    /// <param name="codec">The scalar codec and configuration.</param>
    /// <returns>This collection.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The name is invalid/duplicate or the value cannot be encoded exactly.</exception>
    public SqlParameters Add<T>(string name, T value, ValueCodec<T> codec)
    {
        Validation.Parameter(name); ArgumentNullException.ThrowIfNull(codec);
        if (_values.Any(p => p.Name == name)) throw new ArgumentException("Duplicate parameter name.", nameof(name));
        var bytes = codec.Encode(value);
        _values.Add(new(checked((uint)_values.Count + 1), name, codec.Descriptor, bytes));
        return this;
    }
    internal ParameterValue[] Snapshot() => _values.ToArray();
}

/// <summary>The explicit trusted command effect classification.</summary>
public enum SqlCommandKind
{
    /// <summary>A result-bearing query.</summary>
    Query,
    /// <summary>A mutation whose affected-row count must be observed.</summary>
    Mutation,
    /// <summary>Expert session-changing SQL requiring independent admission and connection retirement policy.</summary>
    SessionMutating
}

/// <summary>An immutable, shareable trusted SQL template and owned typed parameter snapshot.</summary>
/// <remarks>This type does not parse SQL, choose dialects, prove safety or contact a provider. The native engine transports
/// placeholders and validates actual capability/effect policy before dispatch. Interpolated convenience has boxing costs.</remarks>
public sealed class SqlCommand
{
    private SqlCommand(string? text, ParameterValue[] parameters, TemplateSegment[]? segments, SqlCommandKind kind, TimeSpan? timeout)
    {
        Text = text; Parameters = parameters; Segments = segments; Kind = kind; Timeout = timeout;
        ParameterSchema = EncodeParameterSchema(parameters); ParameterHash = Canonical.Hash(ParameterSchema);
    }
    internal string? Text { get; }
    internal ParameterValue[] Parameters { get; }
    internal TemplateSegment[]? Segments { get; }
    internal SqlCommandKind Kind { get; }
    internal TimeSpan? Timeout { get; }
    internal byte[] ParameterSchema { get; }
    internal string ParameterHash { get; }
    /// <summary>Snapshots built-in scalar interpolation values and literal segments, without building SQL.</summary>
    /// <param name="sql">A composite format with unformatted, unaligned typed non-null scalar arguments.</param>
    /// <param name="kind">The explicit effect classification.</param>
    /// <param name="timeout">A nonnegative finite timeout, or the source's configured default; zero expires before dispatch.</param>
    /// <returns>An immutable template whose value holes will be bound by the native engine.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sql"/> is null.</exception>
    /// <exception cref="ArgumentException">An argument is untyped null, requires an explicit codec, is unused, or a format/alignment is supplied.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The kind or timeout is invalid.</exception>
    public static SqlCommand Parameterized(FormattableString sql, SqlCommandKind kind = SqlCommandKind.Query, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(sql); ValidateOptions(kind, timeout);
        var format = sql.Format; Validation.Name(format, nameof(sql));
        var parameters = new ParameterValue[sql.ArgumentCount];
        for (var i = 0; i < parameters.Length; i++) parameters[i] = BuiltIn(checked((uint)i + 1), "p" + i.ToString(CultureInfo.InvariantCulture), sql.GetArgument(i));
        return new(null, parameters, ParseFormat(format, parameters.Length), kind, timeout);
    }
    /// <summary>Snapshots trusted named SQL and typed parameters without provider work.</summary>
    /// <param name="sql">Nonempty strict-Unicode trusted SQL with logical @name placeholders.</param>
    /// <param name="parameters">Typed parameters, or no parameters.</param>
    /// <param name="kind">The explicit effect classification.</param>
    /// <param name="timeout">A nonnegative finite timeout, or the source's configured default; zero expires before dispatch.</param>
    /// <returns>An immutable command; subsequent collection changes do not affect it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sql"/> is null.</exception>
    /// <exception cref="ArgumentException">The SQL is empty or invalid Unicode.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The kind or timeout is invalid.</exception>
    public static SqlCommand Create(string sql, SqlParameters? parameters = null, SqlCommandKind kind = SqlCommandKind.Query, TimeSpan? timeout = null)
    { Validation.Name(sql, nameof(sql)); ValidateOptions(kind, timeout); return new(sql, parameters?.Snapshot() ?? [], null, kind, timeout); }
    private static void ValidateOptions(SqlCommandKind kind, TimeSpan? timeout)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (timeout is { } value && (value < TimeSpan.Zero || value.Ticks > (long)(ulong.MaxValue / 100))) throw new ArgumentOutOfRangeException(nameof(timeout));
    }
    private static ParameterValue Value<T>(uint id, string name, T value, ValueCodec<T> codec) => new(id, name, codec.Descriptor, codec.Encode(value));
    private static ParameterValue BuiltIn(uint id, string name, object? value) => value switch
    {
        short v => Value(id, name, v, Codecs.Int16),
        int v => Value(id, name, v, Codecs.Int32),
        long v => Value(id, name, v, Codecs.Int64),
        bool v => Value(id, name, v, Codecs.Boolean),
        string v => Value(id, name, v, Codecs.String),
        byte[] v => Value(id, name, v, Codecs.Bytes),
        Guid v => Value(id, name, v, Codecs.Guid),
        DateOnly v => Value(id, name, v, Codecs.Date),
        TimeOnly v => Value(id, name, v, Codecs.Time),
        DateTimeOffset v => Value(id, name, v, Codecs.UtcInstant),
        DateTime v => Value(id, name, v, Codecs.UtcDateTime),
        double v => Value(id, name, v, Codecs.Double),
        float v => Value(id, name, v, Codecs.Single),
        _ => throw new ArgumentException("Interpolation requires a non-null built-in scalar; use typed SqlParameters for null, decimal, JSON or enum values.")
    };
    private static TemplateSegment[] ParseFormat(string format, int argumentCount)
    {
        var result = new List<TemplateSegment>(); var literal = new StringBuilder(); var used = new bool[argumentCount];
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c is '{' or '}')
            {
                if (i + 1 < format.Length && format[i + 1] == c) { literal.Append(c); i++; continue; }
                if (c == '}') throw new ArgumentException("Unmatched composite-format brace.", nameof(format));
                result.Add(new(0, Validation.Utf8.GetBytes(literal.ToString()))); literal.Clear();
                var start = ++i;
                while (i < format.Length && char.IsAsciiDigit(format[i])) i++;
                if (i == start || i >= format.Length || format[i] != '}' || !int.TryParse(format.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out var index) || (uint)index >= (uint)argumentCount)
                    throw new ArgumentException("Only plain, in-range interpolation holes are supported; formats and alignment reject.", nameof(format));
                used[index] = true; var bytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint)index + 1)); result.Add(new(1, bytes));
            }
            else literal.Append(c);
        }
        result.Add(new(0, Validation.Utf8.GetBytes(literal.ToString())));
        if (used.Any(u => !u)) throw new ArgumentException("Every interpolation argument must occur in the template.", nameof(format));
        return result.ToArray();
    }
    internal byte[] EncodeTemplate()
    {
        if (Segments is null) return Validation.Utf8.GetBytes(Text!);
        var length = checked(4 + Segments.Sum(s => checked(8 + s.Bytes.Length))); var bytes = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)Segments.Length); var offset = 4;
        foreach (var s in Segments)
        { BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), s.Kind); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4), (uint)s.Bytes.Length); s.Bytes.CopyTo(bytes, offset + 8); offset += 8 + s.Bytes.Length; }
        return bytes;
    }
    private static byte[] EncodeParameterSchema(ParameterValue[] parameters)
    {
        var text = new StringBuilder("{\"version\":1,\"parameters\":[");
        for (var i = 0; i < parameters.Length; i++)
        {
            if (i > 0) text.Append(','); var p = parameters[i];
            text.Append("{\"id\":").Append(Canonical.UInt(p.Id)).Append(",\"name\":").Append(Canonical.Quote(p.Name))
                .Append(",\"codec\":").Append(Canonical.UInt(p.Codec.Id)).Append(",\"codec_config\":").Append(p.Codec.Configuration ?? "null")
                .Append(",\"nullable\":").Append(Canonical.Bool(p.Codec.Nullable)).Append('}');
        }
        return Validation.Utf8.GetBytes(text.Append("]}").ToString());
    }
}
