namespace NativeWeave.Data;

/// <summary>An immutable ordered snapshot of a table's non-null primary-key values.</summary>
/// <typeparam name="TEntity">The application entity class.</typeparam>
/// <remarks>Incomplete prefixes are construction values only; registration or execution requires the complete key.</remarks>
public sealed class EntityKey<TEntity> where TEntity : class
{
    internal EntityKey(Table<TEntity> table, ColumnValue[] values) { Table = table; Values = values; }
    internal Table<TEntity> Table { get; }
    internal ColumnValue[] Values { get; }
    /// <summary>Returns a new snapshot with the next primary-key component appended.</summary>
    /// <typeparam name="T">The next component's value type.</typeparam>
    /// <param name="column">The next column in this table's primary-key declaration order.</param>
    /// <param name="value">The non-null value, copied before return.</param>
    /// <returns>A new immutable key; the current instance is unchanged.</returns>
    /// <exception cref="ArgumentNullException">A required column or non-null value is null.</exception>
    /// <exception cref="ArgumentException">The column is foreign, repeated, nullable, nonkey or out of key order.</exception>
    public EntityKey<TEntity> And<T>(Column<TEntity, T> column, T value)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (!ReferenceEquals(column.Identity, Table.Identity) || column.Definition.KeyOrdinal != Values.Length || column.Codec.Descriptor.Nullable)
            throw new ArgumentException("Expected the next non-null primary-key component of this table.", nameof(column));
        return new(Table, [.. Values, new ColumnValue(column.Binding, column.Codec.Encode(value))]);
    }
    internal void RequireComplete()
    { if (Values.Length == 0 || Values.Length != Table.KeyColumns.Length) throw new ArgumentException("The complete primary key is required."); }
}

/// <summary>A single-writer explicit changeset containing owned value snapshots.</summary>
/// <typeparam name="TEntity">The application entity class.</typeparam>
/// <remarks>This is assignment data, not a SQL AST. Unset removes an assignment; Set with a nullable value assigns SQL NULL.
/// Native execution snapshots the current changes independently and rejects an empty set.</remarks>
public sealed class Changes<TEntity> where TEntity : class
{
    private TableIdentity? _table;
    private readonly Dictionary<uint, ColumnValue> _values = [];
    /// <summary>Creates an empty changeset; execution requires at least one assignment.</summary>
    public Changes() { }
    /// <summary>Copies a value and adds or replaces one nonkey, nongenerated column assignment.</summary>
    /// <typeparam name="T">The exact column value type.</typeparam>
    /// <param name="column">A column from the changeset's single table.</param>
    /// <param name="value">The value to snapshot.</param>
    /// <returns>This changeset for fluent configuration.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The column is foreign, a key, generated, unbuilt or the value is invalid.</exception>
    public Changes<TEntity> Set<T>(Column<TEntity, T> column, T value)
    {
        ArgumentNullException.ThrowIfNull(column); Check(column.Binding);
        var bytes = column.Codec.Encode(value);
        if (bytes is not null && column.Definition.Options.MaxLength is { } maximum && bytes.Length > maximum) throw new ArgumentException("The value exceeds its column byte capacity.", nameof(value));
        _table ??= column.Identity; _values[column.Id] = new(column.Binding, bytes); return this;
    }
    /// <summary>Removes an assignment without assigning SQL NULL.</summary>
    /// <typeparam name="T">The exact column value type.</typeparam>
    /// <param name="column">A nonkey, nongenerated column from the same table.</param>
    /// <returns>This changeset.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    /// <exception cref="ArgumentException">The column is foreign, a key, generated or unbuilt.</exception>
    public Changes<TEntity> Unset<T>(Column<TEntity, T> column)
    { ArgumentNullException.ThrowIfNull(column); Check(column.Binding); _values.Remove(column.Id); return this; }
    private void Check(IColumn column)
    {
        if (!column.Identity.Built || (_table is not null && !ReferenceEquals(_table, column.Identity)) || column.Definition.Options.IsPrimaryKey || column.Definition.Options.IsGenerated)
            throw new ArgumentException("Changes require nonkey, nongenerated columns of one built table.", nameof(column));
    }
    internal ColumnValue[] Snapshot(Table<TEntity> table)
    {
        if (_values.Count == 0 || !ReferenceEquals(_table, table.Identity)) throw new ArgumentException("A nonempty matching changeset is required.");
        return _values.Values.OrderBy(v => v.Column.Definition.Ordinal).ToArray();
    }
}

/// <summary>A single-writer explicit set of expected concurrency-token values.</summary>
/// <typeparam name="TEntity">The application entity class.</typeparam>
/// <remarks>Values are copied immediately. There is no identity map, implicit version increment or managed compare-and-swap.</remarks>
public sealed class ConcurrencyCheck<TEntity> where TEntity : class
{
    private TableIdentity? _table;
    private readonly Dictionary<uint, ColumnValue> _values = [];
    /// <summary>Creates an empty check; execution requires all declared concurrency tokens.</summary>
    public ConcurrencyCheck() { }
    /// <summary>Snapshots one declared expected token.</summary>
    /// <typeparam name="T">The token value type.</typeparam>
    /// <param name="column">A declared concurrency token from one built table.</param>
    /// <param name="value">The expected value.</param>
    /// <returns>This check.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The token is foreign, repeated, unbuilt or not marked for concurrency.</exception>
    public ConcurrencyCheck<TEntity> Expect<T>(Column<TEntity, T> column, T value)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (!column.Identity.Built || !column.Definition.Options.IsConcurrencyToken || (_table is not null && !ReferenceEquals(_table, column.Identity)) || _values.ContainsKey(column.Id))
            throw new ArgumentException("Expected a unique concurrency token of one built table.", nameof(column));
        var encoded = column.Codec.Encode(value); _table ??= column.Identity; _values.Add(column.Id, new(column.Binding, encoded)); return this;
    }
    internal ColumnValue[] Snapshot(Table<TEntity> table)
    {
        if (_values.Count == 0 || !ReferenceEquals(_table, table.Identity) || table.Columns.Any(c => c.Definition.Options.IsConcurrencyToken && !_values.ContainsKey(c.Definition.Id)))
            throw new ArgumentException("Every declared concurrency token must be supplied.");
        return _values.Values.OrderBy(v => v.Column.Definition.Ordinal).ToArray();
    }
}

/// <summary>Immutable ordered primary-key/foreign-key metadata over two already-built tables.</summary>
/// <typeparam name="TParent">The parent entity class.</typeparam>
/// <typeparam name="TChild">The child entity class.</typeparam>
/// <remarks>Creation loads no native engine. And methods return new descriptors and never alter either table hash.
/// A complete key prefix is required for native registration; lazy loading and alternate-key inference are excluded.</remarks>
public sealed class Relation<TParent, TChild> where TParent : class where TChild : class
{
    private Relation(Table<TParent> parent, Table<TChild> child, IColumn[] parents, IColumn[] children)
    { Parent = parent; Child = child; Parents = parents; Children = children; }
    internal Table<TParent> Parent { get; }
    internal Table<TChild> Child { get; }
    internal IColumn[] Parents { get; }
    internal IColumn[] Children { get; }
    /// <summary>Starts a relation at key ordinal zero with a non-null foreign key.</summary>
    /// <typeparam name="T">The common scalar value type.</typeparam>
    /// <param name="parent">The built parent table.</param>
    /// <param name="parentKey">Its first non-null primary-key column.</param>
    /// <param name="child">The built child table.</param>
    /// <param name="foreignKey">A compatible non-null child column.</param>
    /// <returns>An immutable relation prefix.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">Identity, key order, codec, configuration, collation or nullability differs.</exception>
    public static Relation<TParent, TChild> Create<T>(Table<TParent> parent, Column<TParent, T> parentKey, Table<TChild> child, Column<TChild, T> foreignKey)
        => Start(parent, child).Append(parentKey, foreignKey, false);
    /// <summary>Returns a new relation with the next non-null key pair appended.</summary>
    /// <typeparam name="T">The common scalar value type.</typeparam>
    /// <param name="parentKey">The next parent primary-key component.</param>
    /// <param name="foreignKey">A distinct compatible non-null child column.</param>
    /// <returns>A new immutable relation.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The pair violates identity, ordering or codec rules.</exception>
    public Relation<TParent, TChild> And<T>(Column<TParent, T> parentKey, Column<TChild, T> foreignKey) => Append(parentKey, foreignKey, false);
    /// <summary>Starts a relation from a value key to a nullable child foreign key.</summary>
    /// <typeparam name="T">The non-null parent value type.</typeparam>
    /// <param name="parent">The built parent table.</param>
    /// <param name="parentKey">Its first primary-key component.</param>
    /// <param name="child">The built child table.</param>
    /// <param name="foreignKey">The compatible nullable child column.</param>
    /// <returns>An immutable relation prefix.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The pair violates identity, ordering or codec rules.</exception>
    public static Relation<TParent, TChild> CreateOptional<T>(Table<TParent> parent, Column<TParent, T> parentKey, Table<TChild> child, Column<TChild, T?> foreignKey) where T : struct
        => Start(parent, child).Append(parentKey, foreignKey, true);
    /// <summary>Starts a relation from a non-null reference key to a nullable child reference.</summary>
    /// <typeparam name="T">The parent reference type.</typeparam>
    /// <param name="parent">The built parent table.</param>
    /// <param name="parentKey">Its first primary-key component.</param>
    /// <param name="child">The built child table.</param>
    /// <param name="foreignKey">The compatible nullable child column.</param>
    /// <returns>An immutable relation prefix.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The pair violates identity, ordering or codec rules.</exception>
    public static Relation<TParent, TChild> CreateOptionalReference<T>(Table<TParent> parent, Column<TParent, T> parentKey, Table<TChild> child, Column<TChild, T?> foreignKey) where T : class
        => Start(parent, child).Append(parentKey, foreignKey, true);
    /// <summary>Returns a new relation with the next optional value-key pair.</summary>
    /// <typeparam name="T">The non-null parent value type.</typeparam>
    /// <param name="parentKey">The next parent key.</param>
    /// <param name="foreignKey">The matching nullable child column.</param>
    /// <returns>A new immutable relation.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The pair violates identity, ordering or codec rules.</exception>
    public Relation<TParent, TChild> AndOptional<T>(Column<TParent, T> parentKey, Column<TChild, T?> foreignKey) where T : struct => Append(parentKey, foreignKey, true);
    /// <summary>Returns a new relation with the next optional reference-key pair.</summary>
    /// <typeparam name="T">The parent reference type.</typeparam>
    /// <param name="parentKey">The next parent key.</param>
    /// <param name="foreignKey">The matching nullable child column.</param>
    /// <returns>A new immutable relation.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The pair violates identity, ordering or codec rules.</exception>
    public Relation<TParent, TChild> AndOptionalReference<T>(Column<TParent, T> parentKey, Column<TChild, T?> foreignKey) where T : class => Append(parentKey, foreignKey, true);
    private static Relation<TParent, TChild> Start(Table<TParent> parent, Table<TChild> child)
    { ArgumentNullException.ThrowIfNull(parent); ArgumentNullException.ThrowIfNull(child); return new(parent, child, [], []); }
    private Relation<TParent, TChild> Append<TLeft, TRight>(Column<TParent, TLeft> parent, Column<TChild, TRight> child, bool optional)
    {
        ArgumentNullException.ThrowIfNull(parent); ArgumentNullException.ThrowIfNull(child);
        return AppendCore(parent.Binding, child.Binding, optional);
    }
    private Relation<TParent, TChild> AppendCore(IColumn parent, IColumn child, bool optional)
    {
        ArgumentNullException.ThrowIfNull(parent); ArgumentNullException.ThrowIfNull(child);
        if (!ReferenceEquals(parent.Identity, Parent.Identity) || !ReferenceEquals(child.Identity, Child.Identity)
            || parent.Definition.KeyOrdinal != Parents.Length || parent.Definition.Codec.Nullable || child.Definition.Codec.Nullable != optional
            || !parent.Definition.Codec.SameScalar(child.Definition.Codec) || parent.Definition.Options.Collation != child.Definition.Options.Collation
            || Children.Any(c => c.Definition.Id == child.Definition.Id))
            throw new ArgumentException("Relation pairs must match ordered primary keys, table identities, scalar configuration, collation and exact optionality.");
        return new(Parent, Child, [.. Parents, parent], [.. Children, child]);
    }
    internal byte[] CanonicalBytes()
    {
        RequireComplete();
        return Validation.Utf8.GetBytes("{\"version\":1,\"parent_model\":" + Canonical.Quote(Parent.MetadataHash)
            + ",\"parent_table\":" + Canonical.UInt(Parent.Identity.Id) + ",\"child_model\":" + Canonical.Quote(Child.MetadataHash)
            + ",\"child_table\":" + Canonical.UInt(Child.Identity.Id) + ",\"parent_columns\":[" + string.Join(',', Parents.Select(c => Canonical.UInt(c.Definition.Id)))
            + "],\"child_columns\":[" + string.Join(',', Children.Select(c => Canonical.UInt(c.Definition.Id)))
            + "],\"child_nullable\":[" + string.Join(',', Children.Select(c => Canonical.Bool(c.Definition.Codec.Nullable))) + "]}");
    }
    internal void RequireComplete()
    { if (Parents.Length == 0 || Parents.Length != Parent.KeyColumns.Length) throw new ArgumentException("A relation must cover the entire parent primary key before registration."); }
}
