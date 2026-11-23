using System.Text;
using Korda.Data;

namespace Korda.Database;

/// <summary>An explicit CLR/codec interpretation for one catalog column.</summary>
/// <param name="ColumnName">The exact catalog column to map.</param>
/// <param name="ClrType">An unqualified built-in or qualified CLR type name, without nullability syntax.</param>
/// <param name="CodecDeclaringType">The optional qualified type exposing the configured nonnullable ValueCodec.</param>
/// <param name="CodecMember">Its accessible static field/property; required together with CodecDeclaringType.</param>
/// <param name="PropertyName">An optional valid C# member name; null retains a valid column name or a deterministic fallback.</param>
/// <remarks>Configured decimal, JSON, custom IDs and ambiguous provider types need an explicit codec member.
/// The consuming compiler checks its exact type and accessibility. Nullability comes from the catalog.</remarks>
public sealed record SchemaColumnHint(string ColumnName, string ClrType, string? CodecDeclaringType = null, string? CodecMember = null, string? PropertyName = null);

/// <summary>Explicit decisions for generating one ordinary C# entity from catalog v1.</summary>
/// <param name="Namespace">The namespace for emitted source, using ordinary identifier segments.</param>
/// <param name="TypeName">The entity class name.</param>
/// <remarks>Catalog v1 omits ordered composite keys, generated flags and defaults. Supply the exact generated
/// column list, including an explicit empty list when none are generated, and a complete composite-key order.</remarks>
public sealed record SchemaScaffoldOptions(string Namespace, string TypeName)
{
    /// <summary>Gets the complete ordered primary-key names; required for a composite key.</summary>
    public IReadOnlyList<string>? PrimaryKeyOrder { get; init; }
    /// <summary>Gets the explicitly reviewed generated column names; null is rejected because catalog v1 cannot infer them.</summary>
    public IReadOnlyList<string>? GeneratedColumns { get; init; }
    /// <summary>Gets explicit overrides for ambiguous CLR/codec interpretations; defaults to an empty list.</summary>
    public IReadOnlyList<SchemaColumnHint> ColumnHints { get; init; } = [];
}

/// <summary>Detached generated C# source and the catalog information it intentionally cannot recover.</summary>
/// <remarks>Source is ready to include in a project with the provider's bundled generator. A successful consuming
/// compilation remains the authority for user-supplied custom type/codec members.</remarks>
public sealed class SchemaScaffoldResult
{
    internal SchemaScaffoldResult(string source, IReadOnlyList<string> notes) { Source = source; Notes = notes; }
    /// <summary>Gets the ordinary attributed entity source with explicit store names.</summary>
    public string Source { get; }
    /// <summary>Gets immutable limitations about defaults, relationships, indexes and catalog ambiguity.</summary>
    public IReadOnlyList<string> Notes { get; }
}

/// <summary>Emits AOT-visible entity source from explicit provider catalog data and developer decisions.</summary>
/// <remarks>This synchronous pure tool performs no database I/O, compilation, native loading or schema alteration.
/// It does not reconstruct migrations or infer absent catalog metadata. Generated source must be reviewed and compiled.</remarks>
public static class SchemaScaffolder
{
    /// <summary>Generates one ordinary class using the public static-mapping attributes.</summary>
    /// <param name="provider">The provider that produced the catalog record.</param>
    /// <param name="table">The detached catalog table, including its exact columns.</param>
    /// <param name="options">Explicit names, generated flags, key order and type/codec hints.</param>
    /// <returns>Compilable source after supplied custom symbols are resolved, plus explicit catalog limitations.</returns>
    /// <exception cref="ArgumentNullException">A required argument or collection is null.</exception>
    /// <exception cref="ArgumentException">Names, keys, hints, missing generated decisions or finite budgets are invalid.</exception>
    /// <remarks>Nullable primary keys reject because the static model contract cannot represent them. SQLite storage
    /// classes map to their literal CLR storage types by default; JSON, dates, custom IDs and money require hints.</remarks>
    public static SchemaScaffoldResult Generate(DatabaseProvider provider, SchemaTable table, SchemaScaffoldOptions options)
    {
        if (!Enum.IsDefined(provider)) throw new ArgumentOutOfRangeException(nameof(provider));
        ArgumentNullException.ThrowIfNull(table); ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(table.Columns); ArgumentNullException.ThrowIfNull(options.ColumnHints);
        if (options.GeneratedColumns is null) throw new ArgumentException("Catalog v1 omits generated flags: explicitly supply GeneratedColumns, including an empty list when none are generated.", nameof(options));
        QualifiedName(options.Namespace, nameof(options.Namespace)); Identifier(options.TypeName, nameof(options.TypeName));
        if (options.Namespace.StartsWith("global::", StringComparison.Ordinal)) throw new ArgumentException("A namespace declaration cannot use the global alias.", nameof(options));
        StoreName(table.Name); if (table.Schema is not null) StoreName(table.Schema);
        if (table.Columns.Count is < 1 or > 4096) throw new ArgumentException("The table must have between one and 4096 columns.", nameof(table));
        var columns = table.Columns.ToArray(); var names = new HashSet<string>(StringComparer.Ordinal);
        var providerNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            ArgumentNullException.ThrowIfNull(column); StoreName(column.Name); StoreName(column.StoreType, allowEmpty: true);
            if (!names.Add(column.Name)) throw new ArgumentException("Repeated exact catalog column.", nameof(table));
            var providerName = provider == DatabaseProvider.Sqlite ? string.Concat(column.Name.Select(ch => ch is >= 'A' and <= 'Z' ? (char)(ch + ('a' - 'A')) : ch)) : column.Name;
            if (!providerNames.Add(providerName)) throw new ArgumentException("Catalog columns collide under the provider identifier rules.", nameof(table));
            if (column.IsPrimaryKey && column.IsNullable) throw new ArgumentException("Nullable primary key cannot be represented: " + column.Name, nameof(table));
        }
        var keySet = columns.Where(column => column.IsPrimaryKey).Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        var keys = options.PrimaryKeyOrder?.ToArray();
        if (keys is null && keySet.Count > 1) throw new ArgumentException("Catalog v1 omits composite primary-key order; supply PrimaryKeyOrder.", nameof(options));
        keys ??= keySet.ToArray();
        if (keys.Length != keySet.Count || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length || !keySet.SetEquals(keys))
            throw new ArgumentException("PrimaryKeyOrder must exactly cover the catalog key once.", nameof(options));
        var generated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in options.GeneratedColumns)
            if (name is null || !names.Contains(name) || !generated.Add(name)) throw new ArgumentException("GeneratedColumns contains an unknown or repeated name.", nameof(options));
        var hints = new Dictionary<string, SchemaColumnHint>(StringComparer.Ordinal);
        foreach (var hint in options.ColumnHints)
        {
            ArgumentNullException.ThrowIfNull(hint);
            if (hint.ColumnName is null || !names.Contains(hint.ColumnName) || !hints.TryAdd(hint.ColumnName, hint)) throw new ArgumentException("A column hint is unknown or repeated.", nameof(options));
            TypeName(hint.ClrType);
            if ((hint.CodecDeclaringType is null) != (hint.CodecMember is null)) throw new ArgumentException("A codec hint needs both declaring type and member.", nameof(options));
            if (hint.CodecDeclaringType is not null) { QualifiedName(hint.CodecDeclaringType, nameof(hint.CodecDeclaringType)); Identifier(hint.CodecMember!, nameof(hint.CodecMember)); }
            if (hint.PropertyName is not null) Identifier(hint.PropertyName, nameof(hint.PropertyName));
            if (!DefaultTypes.Contains(hint.ClrType) && hint.CodecMember is null) throw new ArgumentException("An ambiguous/custom type requires an explicit codec member: " + hint.ColumnName, nameof(options));
        }
        // A selected constructor establishes mapping/key order. A parameterless
        // class alone would retain the generator's alphabetical property order.
        var ordered = keys.Select(key => columns.Single(column => column.Name == key)).Concat(columns.Where(column => !column.IsPrimaryKey)).ToArray();
        var output = new StringBuilder("#nullable enable\nnamespace ").Append(options.Namespace).Append(";\n\n[global::Korda.Data.WeaveTableAttribute(").Append(Literal(table.Name));
        if (table.Schema is not null) output.Append(", Schema = ").Append(Literal(table.Schema));
        output.Append(")]\npublic sealed class ").Append(options.TypeName).Append("\n{\n");
        var properties = new HashSet<string>(StringComparer.Ordinal);
        var parameters = new List<(string Type, string Property)>();
        bool Reserved(string name) => name is "Table" or "Row" || name == options.TypeName || name == options.TypeName + "Mapping";
        for (var index = 0; index < ordered.Length; index++)
        {
            var column = ordered[index]; hints.TryGetValue(column.Name, out var hint);
            var property = hint?.PropertyName ?? (IsIdentifier(column.Name) && !Reserved(column.Name) ? column.Name : "Column" + (index + 1));
            if (Reserved(property) || !properties.Add(property)) throw new ArgumentException("Generated property names collide or use a reserved descriptor member; supply explicit PropertyName hints.", nameof(options));
            var type = hint?.ClrType ?? Infer(provider, column);
            output.Append("    [global::Korda.Data.WeaveColumnAttribute(").Append(Literal(column.Name));
            if (generated.Contains(column.Name)) output.Append(", Generated = true");
            output.Append(")]\n");
            if (column.IsPrimaryKey) output.Append("    [global::Korda.Data.WeaveKeyAttribute]\n");
            if (hint?.CodecDeclaringType is { } codecType)
                output.Append("    [global::Korda.Data.WeaveUseCodecAttribute(typeof(").Append(codecType).Append("), ").Append(Literal(hint.CodecMember!)).Append(")]\n");
            output.Append("    public ").Append(type).Append(column.IsNullable ? "?" : "").Append(' ').Append(property).Append(" { get; init; } = default!;\n\n");
            parameters.Add((type + (column.IsNullable ? "?" : ""), property));
            if (output.Length > DbWire.MaxEncodedBytes) throw new ArgumentException("Generated source exceeds the finite byte budget.", nameof(table));
        }
        output.Append("    [global::Korda.Data.WeaveConstructorAttribute]\n    public ").Append(options.TypeName).Append('(');
        for (var index = 0; index < parameters.Count; index++)
        {
            if (index != 0) output.Append(", ");
            var parameter = parameters[index];
            output.Append("[global::Korda.Data.WeaveParameterAttribute(nameof(").Append(parameter.Property).Append("))] ")
                .Append(parameter.Type).Append(" value").Append(index);
        }
        output.Append(")\n    {\n");
        for (var index = 0; index < parameters.Count; index++)
            output.Append("        this.").Append(parameters[index].Property).Append(" = value").Append(index).Append(";\n");
        output.Append("    }\n");
        var source = output.Append("}\n").ToString();
        if (HostEncoding.Utf8.GetByteCount(source) > DbWire.MaxEncodedBytes) throw new ArgumentException("Generated source exceeds the finite byte budget.", nameof(table));
        return new(source, Array.AsReadOnly(new[] {
            "Catalog v1 does not expose defaults, indexes, foreign keys, generated expressions or ordered composite keys; reviewed options supply only the explicit mapping decisions.",
            "This is entity mapping source, not a reconstructed migration. Review provider-specific CLR/codec meanings and compile the output before using it."
        }));
    }
    private static readonly HashSet<string> DefaultTypes = new(StringComparer.Ordinal) { "short", "int", "long", "bool", "string", "byte[]", "float", "double", "global::System.Guid", "global::System.DateOnly", "global::System.TimeOnly", "global::System.DateTimeOffset", "global::System.DateTime" };
    private static string Infer(DatabaseProvider provider, SchemaColumn column)
    {
        var type = column.StoreType.Trim().ToUpperInvariant();
        if (provider == DatabaseProvider.Sqlite)
            return type switch { "INTEGER" => "long", "TEXT" => "string", "BLOB" => "byte[]", "REAL" => "double", _ => throw Ambiguous(column) };
        if ((type.StartsWith("CHARACTER VARYING(", StringComparison.Ordinal) || type.StartsWith("CHARACTER(", StringComparison.Ordinal)) && type.EndsWith(')')) return "string";
        return type switch
        {
            "SMALLINT" or "INT2" => "short",
            "INTEGER" or "INT4" => "int",
            "BIGINT" or "INT8" => "long",
            "BOOLEAN" or "BOOL" => "bool",
            "TEXT" or "CHARACTER VARYING" => "string",
            "BYTEA" => "byte[]",
            "UUID" => "global::System.Guid",
            "DATE" => "global::System.DateOnly",
            "TIME WITHOUT TIME ZONE" => "global::System.TimeOnly",
            "TIMESTAMP WITH TIME ZONE" => "global::System.DateTimeOffset",
            "REAL" => "float",
            "DOUBLE PRECISION" => "double",
            _ => throw Ambiguous(column)
        };
    }
    private static ArgumentException Ambiguous(SchemaColumn column) => new("Supply an explicit CLR/codec hint for column " + column.Name + " with store type " + column.StoreType + '.');
    private static void StoreName(string value, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!allowEmpty && value.Length == 0 || value.Contains('\0') || HostEncoding.Utf8.GetByteCount(value) > 4096) throw new ArgumentException("A catalog name/type is empty, contains NUL or exceeds the finite source profile.");
    }
    private static string Literal(string value) => Canonical.Quote(value).Replace("\u0085", "\\u0085", StringComparison.Ordinal)
        .Replace("\u2028", "\\u2028", StringComparison.Ordinal).Replace("\u2029", "\\u2029", StringComparison.Ordinal);
    private static readonly HashSet<string> Keywords = new(("abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while record required init file scoped __arglist __makeref __reftype __refvalue").Split(' '), StringComparer.Ordinal);
    private static bool IsIdentifier(string value) => value.Length is > 0 and <= 128 && !Keywords.Contains(value) &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') && value.Skip(1).All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');
    private static void Identifier(string value, string name)
    { ArgumentNullException.ThrowIfNull(value, name); if (!IsIdentifier(value)) throw new ArgumentException("An ordinary nonkeyword C# identifier is required.", name); }
    private static void QualifiedName(string value, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        var plain = value.StartsWith("global::", StringComparison.Ordinal) ? value[8..] : value;
        if (plain.Length > 1024 || !plain.Split('.').All(IsIdentifier)) throw new ArgumentException("A qualified nonkeyword C# type/namespace name is required.", name);
    }
    private static void TypeName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (DefaultTypes.Contains(value) || value == "decimal") return;
        QualifiedName(value, nameof(value));
    }
}
