namespace Korda.Data;

/// <summary>Synchronously materializes one validated row into an owned CLR value.</summary>
/// <typeparam name="T">The result value or entity type.</typeparam>
/// <param name="row">A borrowed stack-only reader valid only for the call.</param>
/// <returns>The owned result; no row borrow may escape.</returns>
/// <remarks>The callback runs synchronously while its batch lease is live. Decoding and the application
/// constructor create owned CLR values; borrowed reader state must not escape or cross an await.</remarks>
public delegate T RowMaterializer<T>(RowReader row);

/// <summary>Synchronously extracts typed entity values before native dispatch.</summary>
/// <typeparam name="T">The application entity type.</typeparam>
/// <param name="row">A stack-only writer valid only for this snapshot.</param>
/// <param name="value">The application entity.</param>
/// <remarks>The callback runs synchronously during a finite input snapshot. Typed writes copy encoded
/// values into owned buffers before native dispatch; the stack-only writer must not be retained.</remarks>
public delegate void EntityWriter<T>(RowWriter row, T value);

internal interface IRowLease
{
    int Count { get; }
    string Name(int ordinal);
    CodecDescriptor Codec(int ordinal);
    ReadOnlyMemory<byte>? Value(int ordinal);
    void AssertAlive();
}

/// <summary>A stack-only reader over a validated live row lease.</summary>
/// <remarks>Shape validation precedes NULL interpretation. Every read checks the lease; default readers reject.
/// Values are owned CLR copies. Readers cannot be retained across await or stored in an ordinary heap object.</remarks>
public readonly ref struct RowReader
{
    private readonly IRowLease? _lease;
    private readonly object? _projection;
    private readonly TableIdentity? _table;
    private readonly ResultField[]? _validatedFields;
    internal RowReader(IRowLease lease, object projection, TableIdentity? table, ResultField[]? validatedFields = null)
    { _lease = lease; _projection = projection; _table = table; _validatedFields = validatedFields; }
    /// <summary>Reads a typed token belonging to this exact projection.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="column">The projection-bound result token.</param>
    /// <returns>An owned value, possibly SQL NULL when declared nullable.</returns>
    /// <exception cref="ArgumentException">The token belongs to another projection or is default.</exception>
    /// <exception cref="DatabaseException">The shape or value is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The reader is default.</exception>
    /// <exception cref="ObjectDisposedException">The row lease has retired.</exception>
    public T Get<T>(ResultColumn<T> column)
    {
        if (column.Identity is null || !ReferenceEquals(column.Identity, _projection)) throw new ArgumentException("The token belongs to another projection.", nameof(column));
        return Read(column.Ordinal, column.Codec!);
    }
    /// <summary>Reads a typed token from this table's declared row projection.</summary>
    /// <typeparam name="TEntity">The application entity type.</typeparam>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="column">The column from the exact table declaration.</param>
    /// <returns>An owned value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    /// <exception cref="ArgumentException">The token belongs to another table.</exception>
    /// <exception cref="DatabaseException">The shape or value is incompatible.</exception>
    /// <exception cref="ObjectDisposedException">The row lease has retired.</exception>
    public T Get<TEntity, T>(Column<TEntity, T> column) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(column);
        if (!ReferenceEquals(column.Identity, _table)) throw new ArgumentException("The token belongs to another table.", nameof(column));
        return Read(column.Definition.Ordinal, column.Codec);
    }
    /// <summary>Tests SQL NULL after complete shape validation; missing fields never mean NULL.</summary>
    /// <param name="ordinal">A zero-based ordinal in the complete declared shape.</param>
    /// <returns>True for a declared SQL NULL cell.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The ordinal is outside the shape.</exception>
    /// <exception cref="InvalidOperationException">The reader is default.</exception>
    /// <exception cref="ObjectDisposedException">The lease has retired.</exception>
    public bool IsNull(int ordinal) => Lease(ordinal).Value(ordinal) is null;
    /// <summary>Reads an explicit ordinal with the exact declared scalar configuration and nullability.</summary>
    /// <typeparam name="T">The statically known value type.</typeparam>
    /// <param name="ordinal">A zero-based ordinal.</param>
    /// <param name="codec">The exact scalar selection.</param>
    /// <returns>An owned CLR value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="codec"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The ordinal is outside the shape.</exception>
    /// <exception cref="DatabaseException">The codec or value does not match the validated shape.</exception>
    /// <exception cref="ObjectDisposedException">The lease has retired.</exception>
    public T Read<T>(int ordinal, ValueCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec); var lease = Lease(ordinal);
        var actual = _validatedFields is { } fields ? fields[ordinal].Descriptor : lease.Codec(ordinal);
        if (!ReferenceEquals(actual, codec.Descriptor) && (!actual.SameScalar(codec.Descriptor) || actual.Nullable != codec.Descriptor.Nullable))
            throw new DatabaseException("The requested codec differs from the row shape.", DatabaseErrorKind.ShapeMismatch);
        return codec.Decode(lease.Value(ordinal));
    }
    private IRowLease Lease(int ordinal)
    {
        var lease = _lease ?? throw new InvalidOperationException("The default RowReader has no lease.");
        lease.AssertAlive();
        if ((uint)ordinal >= (uint)(_validatedFields?.Length ?? lease.Count)) throw new ArgumentOutOfRangeException(nameof(ordinal));
        return lease;
    }
}

/// <summary>A stack-only writer that snapshots typed entity fields without invoking native code.</summary>
/// <remarks>A writer is valid only during EntityWriter invocation. Repeated assignment rejects.
/// Insert policy omits generated columns. An exception publishes no partial row to a provider.</remarks>
public readonly ref struct RowWriter
{
    private readonly RowSnapshot? _snapshot;
    internal RowWriter(RowSnapshot snapshot) => _snapshot = snapshot;
    /// <summary>Copies one typed value into the current row snapshot.</summary>
    /// <typeparam name="TEntity">The application entity type.</typeparam>
    /// <typeparam name="T">The exact column value type.</typeparam>
    /// <param name="column">The matching table's column.</param>
    /// <param name="value">The value to snapshot; SQL NULL requires a nullable codec.</param>
    /// <exception cref="ArgumentNullException">A required argument or non-null value is null.</exception>
    /// <exception cref="ArgumentException">The column belongs elsewhere or was assigned twice.</exception>
    /// <exception cref="InvalidOperationException">The writer is default or the snapshot has completed.</exception>
    public void Set<TEntity, T>(Column<TEntity, T> column, T value) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(column);
        var snapshot = _snapshot ?? throw new InvalidOperationException("The default RowWriter has no snapshot.");
        snapshot.Add(column, value);
    }
}

/// <summary>An opaque result-column token created by the owning query projection.</summary>
/// <typeparam name="T">The exact result type.</typeparam>
/// <remarks>The default token is invalid; no public constructor can forge projection ownership.</remarks>
public readonly struct ResultColumn<T>
{
    internal ResultColumn(object identity, int ordinal, string name, ValueCodec<T> codec) { Identity = identity; _ordinal = ordinal; _name = name; Codec = codec; }
    internal object? Identity { get; }
    internal ValueCodec<T>? Codec { get; }
    private readonly int _ordinal;
    private readonly string? _name;
    /// <summary>Gets the zero-based ordinal; the default token rejects.</summary>
    /// <exception cref="InvalidOperationException">This token is default.</exception>
    public int Ordinal => Identity is not null ? _ordinal : throw new InvalidOperationException("The default result column is invalid.");
    /// <summary>Gets the exact result alias; the default token rejects.</summary>
    /// <exception cref="InvalidOperationException">This token is default.</exception>
    public string Name => _name ?? throw new InvalidOperationException("The default result column is invalid.");
}

/// <summary>An immutable exact result alias, logical codec configuration and SQL nullability.</summary>
/// <remarks>Creating a field allocates one managed metadata object with no native resource. It can be
/// retained in a static projection; source-specific codec binding occurs separately at registration.</remarks>
public sealed class ResultField
{
    internal ResultField(string name, CodecDescriptor descriptor) { Name = name; Descriptor = descriptor; }
    internal CodecDescriptor Descriptor { get; }
    /// <summary>Creates a field for a static projection.</summary>
    /// <typeparam name="T">The statically known CLR value type.</typeparam>
    /// <param name="name">The nonempty exact result alias.</param>
    /// <param name="codec">The selected scalar codec, configuration and nullability.</param>
    /// <returns>An immutable result-field definition.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The alias is empty or invalid Unicode.</exception>
    public static ResultField Create<T>(string name, ValueCodec<T> codec)
    { Validation.Name(name, nameof(name)); ArgumentNullException.ThrowIfNull(codec); return new(name, codec.Descriptor); }
    /// <summary>Gets the exact result alias.</summary>
    public string Name { get; }
    /// <summary>Gets the logical codec ID, independent of source-bound configured IDs.</summary>
    public uint CodecId => Descriptor.Id;
    /// <summary>Gets whether the typed codec permits SQL NULL.</summary>
    public bool IsNullable => Descriptor.Nullable;
}

/// <summary>An immutable ordered static row shape and direct materializer.</summary>
/// <typeparam name="T">The owned result type; value types and reference types are supported.</typeparam>
/// <remarks>Creation copies the field sequence and hashes canonical metadata. Materializers stay managed;
/// no CLR delegate or function pointer is encoded in metadata. Instances are shareable if their delegates are.</remarks>
public sealed class Projection<T>
{
    internal Projection(ResultField[] fields, RowMaterializer<T> materializer, TableIdentity? table = null, bool scalar = false, object? tokenIdentity = null)
    {
        Fields = fields; Materializer = materializer; Table = table; IsScalar = scalar; TokenIdentity = tokenIdentity ?? this;
        CanonicalBytes = Canonical.Projection(fields); ShapeBytes = System.Security.Cryptography.SHA256.HashData(CanonicalBytes);
        ShapeHash = Convert.ToHexStringLower(ShapeBytes);
    }
    private object TokenIdentity { get; }
    internal ResultField[] Fields { get; }
    internal RowMaterializer<T> Materializer { get; }
    internal TableIdentity? Table { get; }
    internal bool IsScalar { get; }
    internal byte[] CanonicalBytes { get; }
    internal byte[] ShapeBytes { get; }
    /// <summary>Gets lowercase SHA-256 of the exact logical shape, excluding managed delegate and native IDs.</summary>
    public string ShapeHash { get; }
    /// <summary>Copies a unique ordered field sequence and retains an AOT-visible materializer.</summary>
    /// <param name="fields">At least one non-null field with a unique exact alias.</param>
    /// <param name="materializer">A synchronous typed constructor.</param>
    /// <returns>An immutable projection.</returns>
    /// <exception cref="ArgumentNullException">The materializer is null.</exception>
    /// <exception cref="ArgumentException">Fields are empty, contain nulls or duplicate aliases.</exception>
    public static Projection<T> Create(ReadOnlySpan<ResultField> fields, RowMaterializer<T> materializer)
    {
        ArgumentNullException.ThrowIfNull(materializer);
        if (fields.Length > DbWire.MaxDescriptors) throw new ArgumentException("Projection field limit exceeded.", nameof(fields));
        if (fields.IsEmpty) throw new ArgumentException("A projection requires at least one field.", nameof(fields));
        var copy = fields.ToArray(); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in copy) if (field is null || string.IsNullOrEmpty(field.Name) || !names.Add(field.Name)) throw new ArgumentException("Fields require unique nonempty aliases.", nameof(fields));
        return new(copy, materializer);
    }
    internal static Projection<T> Scalar(ValueCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        return new([new ResultField("", codec.Descriptor)], row => row.Read(0, codec), scalar: true);
    }
    internal T Read(IRowLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease); lease.AssertAlive();
        if (lease.Count != Fields.Length) throw new DatabaseException("The row column count differs from its projection.", DatabaseErrorKind.ShapeMismatch);
        for (var i = 0; i < Fields.Length; i++)
        {
            var expected = Fields[i]; var actual = lease.Codec(i);
            if (!IsScalar && lease.Name(i) != expected.Name || !actual.SameScalar(expected.Descriptor) || actual.Nullable != expected.IsNullable)
                throw new DatabaseException("The complete ordered row shape differs from its projection.", DatabaseErrorKind.ShapeMismatch);
            if (lease.Value(i) is null && !expected.IsNullable) throw new DatabaseException("A non-null column contains SQL NULL.", DatabaseErrorKind.Conversion);
        }
        return Materializer(new RowReader(lease, TokenIdentity, Table));
    }

    // A retained immutable batch validates every cell, including NULL and scalar
    // representation, before it exposes any row. Bind that proof to this exact
    // projection once; ordinary leases retain the full per-row validation above.
    internal bool MatchesValidatedBatch(ResultField[] fields, WireExpectation expected)
    {
        if (!ReferenceEquals(fields, Fields) || expected.Kind != PayloadKind.Rows
            || expected.Descriptors.Length != fields.Length || !expected.MetadataHash.AsSpan().SequenceEqual(ShapeBytes)) return false;
        for (var i = 0; i < fields.Length; i++)
        {
            var actual = expected.Descriptors[i]; var field = fields[i];
            if (actual.Id != (uint)i || !actual.Codec.SameScalar(field.Descriptor) || actual.Codec.Nullable != field.IsNullable) return false;
        }
        return true;
    }

    internal T ReadValidatedBatch(IRowLease lease)
    {
        // Called only by the internal batch lease after MatchesValidatedBatch.
        // The public reader still checks live ownership, token identity, ordinal
        // and the requested codec on every access; no unchecked API is exposed.
        lease.AssertAlive();
        return Materializer(new RowReader(lease, TokenIdentity, Table, Fields));
    }
}

internal sealed record ColumnValue(IColumn Column, byte[]? Bytes);
internal sealed class RowSnapshot(TableIdentity table, int? remainingBytes = null)
{
    private readonly Dictionary<uint, ColumnValue> _values = [];
    private bool _complete;
    internal void Add<TEntity, T>(Column<TEntity, T> column, T value) where TEntity : class
    {
        if (_complete) throw new InvalidOperationException("The row snapshot has completed.");
        if (!ReferenceEquals(table, column.Identity)) throw new ArgumentException("The column belongs to another table.", nameof(column));
        if (_values.ContainsKey(column.Id)) throw new ArgumentException("The writer assigned a column twice.", nameof(column));
        if (column.Definition.Options.IsGenerated) { _values.Add(column.Id, new(column.Binding, null)); return; }
        var bytes = remainingBytes is { } available ? column.Codec.EncodeBounded(value, available) : column.Snapshot(value);
        if (remainingBytes is { } remaining)
        {
            if (bytes is not null && column.Definition.Options.MaxLength is { } maximum && bytes.Length > maximum)
                throw new ArgumentException("The value exceeds its declared byte capacity.", nameof(value));
            remainingBytes = checked(remaining - (bytes?.Length ?? 0));
        }
        _values.Add(column.Id, new(column.Binding, bytes));
    }
    internal ColumnValue[] Complete(IColumn[] columns)
    {
        _complete = true;
        if (columns.Any(c => !c.Definition.Options.IsGenerated && !_values.ContainsKey(c.Definition.Id))) throw new ArgumentException("The writer omitted a required column.");
        return columns.Where(c => !c.Definition.Options.IsGenerated).Select(c => _values[c.Definition.Id]).ToArray();
    }
    internal static ColumnValue[] Capture<TEntity>(Table<TEntity> table, TEntity value) where TEntity : class => CaptureCore(table, value, null);
    internal static ColumnValue[] Capture<TEntity>(Table<TEntity> table, TEntity value, int maximumBytes) where TEntity : class
    {
        if (maximumBytes < 0 || maximumBytes > DbWire.MaxEncodedBytes) throw ValueCodec<byte>.Capacity();
        return CaptureCore(table, value, maximumBytes);
    }
    private static ColumnValue[] CaptureCore<TEntity>(Table<TEntity> table, TEntity value, int? maximumBytes) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(value); var snapshot = new RowSnapshot(table.Identity, maximumBytes);
        try { table.Writer(new RowWriter(snapshot), value); return snapshot.Complete(table.Columns); }
        finally { snapshot._complete = true; }
    }
}
