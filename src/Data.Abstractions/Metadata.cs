using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Korda.Data;

/// <summary>Immutable declaration flags for one table column.</summary>
/// <remarks>Null optional settings remain distinct from defaults. MaxLength is a positive byte capacity.
/// The builder snapshots this record and validates key nullability before publishing a table.</remarks>
public sealed record ColumnOptions
{
    /// <summary>Gets whether the column participates in the primary key, ordered by declaration.</summary>
    public bool IsPrimaryKey { get; init; }
    /// <summary>Gets whether the database generates the value on insert.</summary>
    public bool IsGenerated { get; init; }
    /// <summary>Gets whether the column is an explicit optimistic-concurrency token.</summary>
    public bool IsConcurrencyToken { get; init; }
    /// <summary>Gets the exact provider collation name, or no explicit collation.</summary>
    public string? Collation { get; init; }
    /// <summary>Gets the positive maximum byte capacity, or no declared capacity.</summary>
    public int? MaxLength { get; init; }
}

internal sealed class TableIdentity(string logicalId, string table, string? schema)
{
    internal string LogicalId { get; } = logicalId;
    internal string Table { get; } = table;
    internal string? Schema { get; } = schema;
    internal uint Id { get; } = Canonical.Id("NWDB-TABLE-ID-v1", logicalId, schema, table);
    internal bool Built { get; set; }
}

internal sealed record ColumnDefinition(uint Id, string Name, CodecDescriptor Codec, ColumnOptions Options, int Ordinal, int? KeyOrdinal);
internal interface IColumn
{
    TableIdentity Identity { get; }
    ColumnDefinition Definition { get; }
    object Token { get; }
}
internal sealed record ColumnBinding(TableIdentity Identity, ColumnDefinition Definition, object Token) : IColumn;

/// <summary>An immutable entity- and value-typed column token belonging to one table declaration.</summary>
/// <typeparam name="TEntity">The application entity class.</typeparam>
/// <typeparam name="T">The exact value type selected by the codec.</typeparam>
/// <remarks>Tokens expose no native pointer. Equivalent metadata in another builder does not authorize using its token.</remarks>
public sealed class Column<TEntity, T> where TEntity : class
{
    internal Column(TableIdentity identity, ColumnDefinition definition, ValueCodec<T> codec) { Identity = identity; Definition = definition; Codec = codec; Binding = new ColumnBinding(identity, definition, this); }
    internal TableIdentity Identity { get; }
    internal ColumnDefinition Definition { get; }
    internal IColumn Binding { get; }
    internal byte[]? Snapshot(T value)
    {
        var bytes = Codec.Encode(value);
        if (bytes is not null && Definition.Options.MaxLength is { } maximum && bytes.Length > maximum)
            throw new ArgumentException("The value exceeds its declared byte capacity.", nameof(value));
        return bytes;
    }
    /// <summary>Gets the exact store name, preserving case and Unicode.</summary>
    public string Name => Definition.Name;
    /// <summary>Gets the stable nonzero little-endian SHA-256-derived column ID.</summary>
    public uint Id => Definition.Id;
    /// <summary>Gets the exact logical codec and its configuration.</summary>
    public ValueCodec<T> Codec { get; }
}

/// <summary>Constructs one immutable table declaration using explicit AOT-visible delegates.</summary>
/// <typeparam name="TEntity">The application entity class.</typeparam>
/// <remarks>This single-writer startup builder is consumed by successful Build. It performs O(column count)
/// managed work and loads no native engine. The known closed type is used only for its assembly/name identity.</remarks>
public sealed class TableBuilder<TEntity> where TEntity : class
{
    private readonly TableIdentity _identity;
    private readonly List<IColumn> _columns = [];
    private int _keyCount;
    /// <summary>Starts a declaration with exact store names.</summary>
    /// <param name="tableName">A nonempty strict-Unicode table name.</param>
    /// <param name="schema">A nonempty explicit schema or null; null is not an empty string.</param>
    /// <exception cref="ArgumentException">A name or closed type identity is invalid.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="tableName"/> is null.</exception>
    public TableBuilder(string tableName, string? schema = null)
    {
        Validation.Name(tableName, nameof(tableName));
        if (schema is not null) Validation.Name(schema, nameof(schema));
        var type = typeof(TEntity);
        var assembly = Validation.Name(type.Assembly.GetName().Name!, "entityAssembly");
        var fullName = Validation.Name(type.FullName!, "entityFullName");
        _identity = new(assembly + ":" + fullName, tableName, schema);
    }
    /// <summary>Adds a typed column in projection and primary-key declaration order.</summary>
    /// <typeparam name="T">The exact CLR value type.</typeparam>
    /// <param name="name">The exact store column name.</param>
    /// <param name="codec">The non-null supported scalar selection.</param>
    /// <param name="options">Immutable flags, or default flags.</param>
    /// <returns>A token tied to this declaration.</returns>
    /// <exception cref="ArgumentException">A duplicate name/ID, nullable key or invalid option is supplied.</exception>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="InvalidOperationException">The builder was consumed.</exception>
    public Column<TEntity, T> Column<T>(string name, ValueCodec<T> codec, ColumnOptions? options = null)
    {
        EnsureMutable(); Validation.Name(name, nameof(name)); ArgumentNullException.ThrowIfNull(codec);
        if (_columns.Count >= DbWire.MaxDescriptors) throw new ArgumentException("The table exceeds the descriptor limit.");
        options ??= new();
        if (options.MaxLength is <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxLength must be positive.");
        if (options.Collation is not null) Validation.Name(options.Collation, nameof(options));
        if (options.IsPrimaryKey && codec.Descriptor.Nullable) throw new ArgumentException("A primary key cannot allow SQL NULL.", nameof(options));
        if (_columns.Any(c => string.Equals(c.Definition.Name, name, StringComparison.Ordinal))) throw new ArgumentException("Duplicate exact column name; native registration also checks the provider identifier policy.", nameof(name));
        var id = Canonical.Id("NWDB-COLUMN-ID-v1", _identity.LogicalId, _identity.Schema, _identity.Table, name);
        if (_columns.Any(c => c.Definition.Id == id)) throw new ArgumentException("Column ID collision; IDs are never reassigned.", nameof(name));
        var definition = new ColumnDefinition(id, name, codec.Descriptor, options with { }, _columns.Count, options.IsPrimaryKey ? _keyCount++ : null);
        var column = new Column<TEntity, T>(_identity, definition, codec); _columns.Add(column.Binding); return column;
    }
    /// <summary>Validates and consumes the builder, retaining direct managed mapping delegates.</summary>
    /// <param name="materializer">The synchronous typed row constructor.</param>
    /// <param name="writer">The synchronous typed entity writer.</param>
    /// <returns>An immutable table with canonical metadata and row projection.</returns>
    /// <exception cref="ArgumentNullException">A delegate is null.</exception>
    /// <exception cref="InvalidOperationException">There are no columns or the builder was already consumed.</exception>
    public Table<TEntity> Build(RowMaterializer<TEntity> materializer, EntityWriter<TEntity> writer)
    {
        EnsureMutable(); ArgumentNullException.ThrowIfNull(materializer); ArgumentNullException.ThrowIfNull(writer);
        if (_columns.Count == 0) throw new InvalidOperationException("A table requires at least one column.");
        var table = new Table<TEntity>(_identity, _columns.ToArray(), materializer, writer);
        _identity.Built = true;
        return table;
    }
    private void EnsureMutable() { if (_identity.Built) throw new InvalidOperationException("Build already consumed this table builder."); }
}

/// <summary>An immutable, shareable table declaration; first native registration belongs to the source owner.</summary>
/// <typeparam name="TEntity">The application entity class.</typeparam>
/// <remarks>Canonical metadata hashes are business schema identities, independent of ABI and package versions.</remarks>
public sealed class Table<TEntity> where TEntity : class
{
    internal Table(TableIdentity identity, IColumn[] columns, RowMaterializer<TEntity> materializer, EntityWriter<TEntity> writer)
    {
        Identity = identity; Columns = columns; Writer = writer;
        CanonicalBytes = Canonical.Model(identity, columns.Select(c => c.Definition).ToArray());
        if (CanonicalBytes.Length > DbWire.MaxEncodedBytes) throw new ArgumentException("The model exceeds the encoded byte limit.");
        MetadataHash = Canonical.Hash(CanonicalBytes);
        KeyColumns = columns.Where(c => c.Definition.KeyOrdinal is not null).ToArray();
        Row = new Projection<TEntity>(columns.Select(c => new ResultField(c.Definition.Name, c.Definition.Codec)).ToArray(), materializer, identity);
    }
    internal TableIdentity Identity { get; }
    internal IColumn[] Columns { get; }
    internal IColumn[] KeyColumns { get; }
    internal EntityWriter<TEntity> Writer { get; }
    internal byte[] CanonicalBytes { get; }
    /// <summary>Gets the exact table name.</summary>
    public string Name => Identity.Table;
    /// <summary>Gets the explicit schema or null.</summary>
    public string? Schema => Identity.Schema;
    /// <summary>Gets lowercase hexadecimal SHA-256 of the canonical model bytes.</summary>
    public string MetadataHash { get; }
    /// <summary>Gets the immutable projection in declared constructor order.</summary>
    public Projection<TEntity> Row { get; }
    /// <summary>Starts a primary-key snapshot with key ordinal zero.</summary>
    /// <typeparam name="T">The key component's value type.</typeparam>
    /// <param name="column">This table's first non-null primary-key column.</param>
    /// <param name="value">The typed value, copied into owned wire storage.</param>
    /// <returns>An immutable key; append subsequent components with And.</returns>
    /// <exception cref="ArgumentException">The column belongs elsewhere, is not ordinal zero, or the value is invalid.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> or a non-null value is null.</exception>
    public EntityKey<TEntity> Key<T>(Column<TEntity, T> column, T value) => new EntityKey<TEntity>(this, []).And(column, value);
    /// <summary>Resolves a declared exact store name and codec without inspecting entity members.</summary>
    /// <typeparam name="T">The requested value type.</typeparam>
    /// <param name="name">The exact case-sensitive store name.</param>
    /// <param name="codec">The required logical codec, configuration and nullability.</param>
    /// <param name="column">The matching token or null.</param>
    /// <returns>True only for a complete typed and metadata match.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is invalid.</exception>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public bool TryGetColumn<T>(string name, ValueCodec<T> codec, [NotNullWhen(true)] out Column<TEntity, T>? column)
    {
        Validation.Name(name, nameof(name)); ArgumentNullException.ThrowIfNull(codec);
        column = Columns.Select(c => c.Token).OfType<Column<TEntity, T>>().FirstOrDefault(c => c.Name == name && c.Codec.Descriptor == codec.Descriptor);
        return column is not null;
    }
}

internal static class Canonical
{
    internal static string Quote(string value)
    {
        Validation.Utf8.GetByteCount(value);
        var result = new StringBuilder(value.Length + 2); result.Append('"');
        foreach (var c in value)
        {
            if (c is '"' or '\\') result.Append('\\').Append(c);
            else if (c < ' ') result.Append("\\u00").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
            else result.Append(c);
        }
        return result.Append('"').ToString();
    }
    internal static string Text(string? value) => value is null ? "null" : Quote(value);
    internal static string Bool(bool value) => value ? "true" : "false";
    internal static string UInt(uint value) => value.ToString(CultureInfo.InvariantCulture);
    internal static string Optional(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "null";
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static uint Id(params string?[] identity)
    {
        var bytes = Validation.Utf8.GetBytes("[" + string.Join(',', identity.Select(Text)) + "]");
        var id = BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(bytes));
        return id != 0 ? id : throw new ArgumentException("The canonical identity has a reserved zero ID.");
    }
    internal static byte[] Model(TableIdentity table, IReadOnlyList<ColumnDefinition> columns)
    {
        var text = new StringBuilder();
        text.Append("{\"version\":1,\"logical_id\":").Append(Quote(table.LogicalId)).Append(",\"table_id\":").Append(UInt(table.Id))
            .Append(",\"schema\":").Append(Text(table.Schema)).Append(",\"table\":").Append(Quote(table.Table)).Append(",\"columns\":[");
        var first = true;
        foreach (var c in columns.OrderBy(c => c.Id))
        {
            if (!first) text.Append(','); first = false;
            text.Append("{\"id\":").Append(UInt(c.Id)).Append(",\"name\":").Append(Quote(c.Name)).Append(",\"codec\":").Append(UInt(c.Codec.Id))
                .Append(",\"codec_config\":").Append(c.Codec.Configuration ?? "null").Append(",\"nullable\":").Append(Bool(c.Codec.Nullable))
                .Append(",\"key_ordinal\":").Append(Optional(c.KeyOrdinal)).Append(",\"generated\":").Append(Bool(c.Options.IsGenerated))
                .Append(",\"concurrency\":").Append(Bool(c.Options.IsConcurrencyToken)).Append(",\"collation\":").Append(Text(c.Options.Collation))
                .Append(",\"max_length\":").Append(Optional(c.Options.MaxLength)).Append('}');
        }
        text.Append("],\"projection\":[").Append(string.Join(',', columns.OrderBy(c => c.Ordinal).Select(c => UInt(c.Id)))).Append("],\"relations\":[]}");
        return Validation.Utf8.GetBytes(text.ToString());
    }
    internal static byte[] Projection(IReadOnlyList<ResultField> fields)
    {
        var text = new StringBuilder("{\"version\":1,\"fields\":[");
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0) text.Append(','); var f = fields[i];
            text.Append("{\"ordinal\":").Append(i.ToString(CultureInfo.InvariantCulture)).Append(",\"name\":").Append(Quote(f.Name))
                .Append(",\"codec\":").Append(UInt(f.CodecId)).Append(",\"codec_config\":").Append(f.Descriptor.Configuration ?? "null")
                .Append(",\"nullable\":").Append(Bool(f.IsNullable)).Append('}');
        }
        return Validation.Utf8.GetBytes(text.Append("]}").ToString());
    }
}
