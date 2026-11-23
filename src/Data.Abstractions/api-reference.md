# Korda.Data API reference

Generated from the compiled product assembly and its XML comments by `tests/Data.Abstractions/ApiCheck`. Covers data descriptors and transport, static model annotations, and shared ORM errors. Database provider execution remains in the database assemblies. Exact declarations retain the pinned Design identity in `public-api.expected.json`.

## `F:Korda.Data.DatabaseErrorKind.ConcurrentUse`

```csharp
Korda.Data.DatabaseErrorKind.ConcurrentUse = 6
```

**summary:** A single-command owner already admits another operation.

## `F:Korda.Data.DatabaseErrorKind.Constraint`

```csharp
Korda.Data.DatabaseErrorKind.Constraint = 1
```

**summary:** A database constraint or expected affected-row condition failed.

## `F:Korda.Data.DatabaseErrorKind.Conversion`

```csharp
Korda.Data.DatabaseErrorKind.Conversion = 2
```

**summary:** A value cannot be represented exactly by its declared codec.

## `F:Korda.Data.DatabaseErrorKind.InvalidState`

```csharp
Korda.Data.DatabaseErrorKind.InvalidState = 7
```

**summary:** The operation is invalid for the owner's current state or metadata.

## `F:Korda.Data.DatabaseErrorKind.Provider`

```csharp
Korda.Data.DatabaseErrorKind.Provider = 0
```

**summary:** The provider reported a failure.

## `F:Korda.Data.DatabaseErrorKind.ResourceLimit`

```csharp
Korda.Data.DatabaseErrorKind.ResourceLimit = 5
```

**summary:** A configured finite resource limit was exceeded.

## `F:Korda.Data.DatabaseErrorKind.ShapeMismatch`

```csharp
Korda.Data.DatabaseErrorKind.ShapeMismatch = 3
```

**summary:** The complete result shape differs from the declared projection.

## `F:Korda.Data.DatabaseErrorKind.UnknownOutcome`

```csharp
Korda.Data.DatabaseErrorKind.UnknownOutcome = 8
```

**summary:** The external outcome cannot be established; automatic retry is unsafe.

## `F:Korda.Data.DatabaseErrorKind.UnsupportedCapability`

```csharp
Korda.Data.DatabaseErrorKind.UnsupportedCapability = 4
```

**summary:** The selected engine or provider cannot perform the requested operation.

## `F:Korda.Data.SqlCommandKind.Mutation`

```csharp
Korda.Data.SqlCommandKind.Mutation = 1
```

**summary:** A mutation whose affected-row count must be observed.

## `F:Korda.Data.SqlCommandKind.Query`

```csharp
Korda.Data.SqlCommandKind.Query = 0
```

**summary:** A result-bearing query.

## `F:Korda.Data.SqlCommandKind.SessionMutating`

```csharp
Korda.Data.SqlCommandKind.SessionMutating = 2
```

**summary:** Expert session-changing SQL requiring independent admission and connection retirement policy.

## `M:Korda.Data.Changes`1.#ctor`

```csharp
public Korda.Data.Changes<TEntity>.Changes()
```

**summary:** Creates an empty changeset; execution requires at least one assignment.

## `M:Korda.Data.Changes`1.Set``1(Korda.Data.Column{`0,``0},``0)`

```csharp
public Korda.Data.Changes<TEntity> Korda.Data.Changes<TEntity>.Set<T>(Korda.Data.Column<TEntity, T> column, T value)
```

**summary:** Copies a value and adds or replaces one nonkey, nongenerated column assignment.

**typeparam T:** The exact column value type.

**param column:** A column from the changeset's single table.

**param value:** The value to snapshot.

**returns:** This changeset for fluent configuration.

**exception:** A required argument is null.

**exception:** The column is foreign, a key, generated, unbuilt or the value is invalid.

## `M:Korda.Data.Changes`1.Unset``1(Korda.Data.Column{`0,``0})`

```csharp
public Korda.Data.Changes<TEntity> Korda.Data.Changes<TEntity>.Unset<T>(Korda.Data.Column<TEntity, T> column)
```

**summary:** Removes an assignment without assigning SQL NULL.

**typeparam T:** The exact column value type.

**param column:** A nonkey, nongenerated column from the same table.

**returns:** This changeset.

**exception:** is null.

**exception:** The column is foreign, a key, generated or unbuilt.

## `M:Korda.Data.Codecs.Decimal(System.Byte,System.Byte)`

```csharp
public static Korda.Data.ValueCodec<decimal> Korda.Data.Codecs.Decimal(byte precision, byte scale)
```

**summary:** Selects exact decimal numeric precision and scale; ordinary SQLite does not support this provider profile.

**param precision:** Total decimal precision from one through 29.

**param scale:** Scale from zero through the smaller of 28 and precision.

**returns:** An immutable configured logical codec.

**exception:** The precision or scale is outside the declared range.

## `M:Korda.Data.Codecs.EnumInt32``1`

```csharp
public static Korda.Data.ValueCodec<TEnum> Korda.Data.Codecs.EnumInt32<TEnum>() where TEnum : struct, System.Enum
```

**summary:** Selects a checked signed Int32 enum representation, preserving undefined numeric values.

**typeparam TEnum:** A statically known CLR enum.

**returns:** An immutable enum codec; values outside Int32 reject at snapshot.

## `M:Korda.Data.Codecs.FixedScaleMoney(System.Byte)`

```csharp
public static Korda.Data.ValueCodec<decimal> Korda.Data.Codecs.FixedScaleMoney(byte scale)
```

**summary:** Selects exact signed Int64 fixed-scale storage, without rounding.

**param scale:** The decimal scale from zero through 18.

**returns:** An immutable configured money codec.

**exception:** exceeds 18.

## `M:Korda.Data.Codecs.Json``1(System.Text.Json.Serialization.Metadata.JsonTypeInfo{``0})`

```csharp
public static Korda.Data.ValueCodec<T> Korda.Data.Codecs.Json<T>(System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
```

**summary:** Selects JSON serialization using explicit static metadata; JSON null is distinct from SQL NULL.

**typeparam T:** The statically serializable value.

**param typeInfo:** Explicit source-generated or manually authored JSON metadata.

**returns:** An immutable typed JSON codec that retains the supplied metadata.

**exception:** is null.

## `M:Korda.Data.Codecs.NullableReference``1(Korda.Data.ValueCodec{``0})`

```csharp
public static Korda.Data.ValueCodec<T?> Korda.Data.Codecs.NullableReference<T>(Korda.Data.ValueCodec<T> codec) where T : class
```

**summary:** Adds SQL NULL support to a reference codec while preserving its logical scalar identity.

**typeparam T:** The reference type.

**param codec:** The selected non-null scalar codec.

**returns:** A nullable reference codec.

**exception:** is null.

## `M:Korda.Data.Codecs.Nullable``1(Korda.Data.ValueCodec{``0})`

```csharp
public static Korda.Data.ValueCodec<T?> Korda.Data.Codecs.Nullable<T>(Korda.Data.ValueCodec<T> codec) where T : struct
```

**summary:** Adds SQL NULL support to a value codec without changing its logical scalar identity.

**typeparam T:** A non-nullable value type.

**param codec:** The selected non-null scalar codec.

**returns:** A typed nullable codec with the same configuration.

**exception:** is null.

## `M:Korda.Data.ColumnOptions.#ctor`

```csharp
public Korda.Data.ColumnOptions.ColumnOptions()
```

Compiler-generated member; inherits the documented type semantics.

## `M:Korda.Data.ColumnOptions.Equals(Korda.Data.ColumnOptions)`

```csharp
public bool Korda.Data.ColumnOptions.Equals(Korda.Data.ColumnOptions? other)
```

Compiler-generated member; inherits the documented type semantics.

## `M:Korda.Data.ColumnOptions.Equals(System.Object)`

```csharp
public override bool Korda.Data.ColumnOptions.Equals(object? obj)
```

Compiler-generated member; inherits the documented type semantics.

## `M:Korda.Data.ColumnOptions.GetHashCode`

```csharp
public override int Korda.Data.ColumnOptions.GetHashCode()
```

Compiler-generated member; inherits the documented type semantics.

## `M:Korda.Data.ColumnOptions.ToString`

```csharp
public override string Korda.Data.ColumnOptions.ToString()
```

Compiler-generated member; inherits the documented type semantics.

## `M:Korda.Data.ColumnOptions.op_Equality(Korda.Data.ColumnOptions,Korda.Data.ColumnOptions)`

```csharp
public static bool Korda.Data.ColumnOptions.operator ==(Korda.Data.ColumnOptions? left, Korda.Data.ColumnOptions? right)
```

Compiler-generated member; inherits the documented type semantics.

## `M:Korda.Data.ColumnOptions.op_Inequality(Korda.Data.ColumnOptions,Korda.Data.ColumnOptions)`

```csharp
public static bool Korda.Data.ColumnOptions.operator !=(Korda.Data.ColumnOptions? left, Korda.Data.ColumnOptions? right)
```

Compiler-generated member; inherits the documented type semantics.

## `M:Korda.Data.ColumnOptions.{Clone}$`

```csharp
public Korda.Data.ColumnOptions Korda.Data.ColumnOptions.<Clone>$()
```

Compiler-generated member; inherits the documented type semantics.

## `M:Korda.Data.ConcurrencyCheck`1.#ctor`

```csharp
public Korda.Data.ConcurrencyCheck<TEntity>.ConcurrencyCheck()
```

**summary:** Creates an empty check; execution requires all declared concurrency tokens.

## `M:Korda.Data.ConcurrencyCheck`1.Expect``1(Korda.Data.Column{`0,``0},``0)`

```csharp
public Korda.Data.ConcurrencyCheck<TEntity> Korda.Data.ConcurrencyCheck<TEntity>.Expect<T>(Korda.Data.Column<TEntity, T> column, T value)
```

**summary:** Snapshots one declared expected token.

**typeparam T:** The token value type.

**param column:** A declared concurrency token from one built table.

**param value:** The expected value.

**returns:** This check.

**exception:** A required argument is null.

**exception:** The token is foreign, repeated, unbuilt or not marked for concurrency.

## `M:Korda.Data.ConcurrencyConflictException.#ctor(System.String,System.Int64,System.Int64)`

```csharp
public Korda.Data.ConcurrencyConflictException.ConcurrencyConflictException(string message, long expectedRows, long actualRows)
```

**summary:** Creates an immutable expected/actual count diagnostic.

**param message:** The non-null diagnostic message.

**param expectedRows:** The nonnegative expected affected-row count.

**param actualRows:** The nonnegative actual affected-row count.

**exception:** is null.

**exception:** A count is negative.

## `M:Korda.Data.DatabaseCapabilityException.#ctor(System.String,System.String)`

```csharp
public Korda.Data.DatabaseCapabilityException.DatabaseCapabilityException(string capability, string provider)
```

**summary:** Creates a failure that must be reported before a native business side effect.

**param capability:** The nonempty stable capability name.

**param provider:** The nonempty provider identity.

**exception:** An identity is empty or contains invalid Unicode.

**exception:** An identity is null.

## `M:Korda.Data.DatabaseErrorKind.#ctor`

```csharp
public Korda.Data.DatabaseErrorKind.DatabaseErrorKind()
```

Compiler-generated member; inherits the documented type semantics.

## `M:Korda.Data.DatabaseException.#ctor(System.String,Korda.Data.DatabaseErrorKind,System.String,System.UInt64,System.Exception)`

```csharp
public Korda.Data.DatabaseException.DatabaseException(string message, Korda.Data.DatabaseErrorKind kind, string? providerCode = null, ulong correlationId = 0, System.Exception? innerException = null)
```

**summary:** Creates a database failure without inferring provider transience.

**param message:** The diagnostic message, without credentials or parameter values.

**param kind:** The provider-neutral category.

**param providerCode:** An optional provider error code.

**param correlationId:** The native diagnostic correlation, or zero when absent.

**param innerException:** The original cause, if available.

**exception:** is null.

**exception:** is undefined.

## `M:Korda.Data.EntityKey`1.And``1(Korda.Data.Column{`0,``0},``0)`

```csharp
public Korda.Data.EntityKey<TEntity> Korda.Data.EntityKey<TEntity>.And<T>(Korda.Data.Column<TEntity, T> column, T value)
```

**summary:** Returns a new snapshot with the next primary-key component appended.

**typeparam T:** The next component's value type.

**param column:** The next column in this table's primary-key declaration order.

**param value:** The non-null value, copied before return.

**returns:** A new immutable key; the current instance is unchanged.

**exception:** A required column or non-null value is null.

**exception:** The column is foreign, repeated, nullable, nonkey or out of key order.

## `M:Korda.Data.EntityWriter`1.#ctor(System.Object,System.IntPtr)`

```csharp
public Korda.Data.EntityWriter<T>.EntityWriter(object @object, nint method)
```

Compiler-generated member; inherits the documented type semantics.

## `M:Korda.Data.EntityWriter`1.BeginInvoke(Korda.Data.RowWriter,`0,System.AsyncCallback,System.Object)`

```csharp
public virtual System.IAsyncResult Korda.Data.EntityWriter<T>.BeginInvoke(Korda.Data.RowWriter row, T value, System.AsyncCallback callback, object @object)
```

Compiler-generated member; inherits the documented type semantics.

## `M:Korda.Data.EntityWriter`1.EndInvoke(System.IAsyncResult)`

```csharp
public virtual void Korda.Data.EntityWriter<T>.EndInvoke(System.IAsyncResult result)
```

Compiler-generated member; inherits the documented type semantics.

## `M:Korda.Data.EntityWriter`1.Invoke(Korda.Data.RowWriter,`0)`

```csharp
public virtual void Korda.Data.EntityWriter<T>.Invoke(Korda.Data.RowWriter row, T value)
```

Compiler-generated member; inherits the documented type semantics.

## `M:Korda.Data.Projection`1.Create(System.ReadOnlySpan{Korda.Data.ResultField},Korda.Data.RowMaterializer{`0})`

```csharp
public static Korda.Data.Projection<T> Korda.Data.Projection<T>.Create(System.ReadOnlySpan<Korda.Data.ResultField> fields, Korda.Data.RowMaterializer<T> materializer)
```

**summary:** Copies a unique ordered field sequence and retains an AOT-visible materializer.

**param fields:** At least one non-null field with a unique exact alias.

**param materializer:** A synchronous typed constructor.

**returns:** An immutable projection.

**exception:** The materializer is null.

**exception:** Fields are empty, contain nulls or duplicate aliases.

## `M:Korda.Data.Relation`2.AndOptionalReference``1(Korda.Data.Column{`0,``0},Korda.Data.Column{`1,``0})`

```csharp
public Korda.Data.Relation<TParent, TChild> Korda.Data.Relation<TParent, TChild>.AndOptionalReference<T>(Korda.Data.Column<TParent, T> parentKey, Korda.Data.Column<TChild, T?> foreignKey) where T : class
```

**summary:** Returns a new relation with the next optional reference-key pair.

**typeparam T:** The parent reference type.

**param parentKey:** The next parent key.

**param foreignKey:** The matching nullable child column.

**returns:** A new immutable relation.

**exception:** An argument is null.

**exception:** The pair violates identity, ordering or codec rules.

## `M:Korda.Data.Relation`2.AndOptional``1(Korda.Data.Column{`0,``0},Korda.Data.Column{`1,System.Nullable{``0}})`

```csharp
public Korda.Data.Relation<TParent, TChild> Korda.Data.Relation<TParent, TChild>.AndOptional<T>(Korda.Data.Column<TParent, T> parentKey, Korda.Data.Column<TChild, T?> foreignKey) where T : struct
```

**summary:** Returns a new relation with the next optional value-key pair.

**typeparam T:** The non-null parent value type.

**param parentKey:** The next parent key.

**param foreignKey:** The matching nullable child column.

**returns:** A new immutable relation.

**exception:** An argument is null.

**exception:** The pair violates identity, ordering or codec rules.

## `M:Korda.Data.Relation`2.And``1(Korda.Data.Column{`0,``0},Korda.Data.Column{`1,``0})`

```csharp
public Korda.Data.Relation<TParent, TChild> Korda.Data.Relation<TParent, TChild>.And<T>(Korda.Data.Column<TParent, T> parentKey, Korda.Data.Column<TChild, T> foreignKey)
```

**summary:** Returns a new relation with the next non-null key pair appended.

**typeparam T:** The common scalar value type.

**param parentKey:** The next parent primary-key component.

**param foreignKey:** A distinct compatible non-null child column.

**returns:** A new immutable relation.

**exception:** An argument is null.

**exception:** The pair violates identity, ordering or codec rules.

## `M:Korda.Data.Relation`2.CreateOptionalReference``1(Korda.Data.Table{`0},Korda.Data.Column{`0,``0},Korda.Data.Table{`1},Korda.Data.Column{`1,``0})`

```csharp
public static Korda.Data.Relation<TParent, TChild> Korda.Data.Relation<TParent, TChild>.CreateOptionalReference<T>(Korda.Data.Table<TParent> parent, Korda.Data.Column<TParent, T> parentKey, Korda.Data.Table<TChild> child, Korda.Data.Column<TChild, T?> foreignKey) where T : class
```

**summary:** Starts a relation from a non-null reference key to a nullable child reference.

**typeparam T:** The parent reference type.

**param parent:** The built parent table.

**param parentKey:** Its first primary-key component.

**param child:** The built child table.

**param foreignKey:** The compatible nullable child column.

**returns:** An immutable relation prefix.

**exception:** An argument is null.

**exception:** The pair violates identity, ordering or codec rules.

## `M:Korda.Data.Relation`2.CreateOptional``1(Korda.Data.Table{`0},Korda.Data.Column{`0,``0},Korda.Data.Table{`1},Korda.Data.Column{`1,System.Nullable{``0}})`

```csharp
public static Korda.Data.Relation<TParent, TChild> Korda.Data.Relation<TParent, TChild>.CreateOptional<T>(Korda.Data.Table<TParent> parent, Korda.Data.Column<TParent, T> parentKey, Korda.Data.Table<TChild> child, Korda.Data.Column<TChild, T?> foreignKey) where T : struct
```

**summary:** Starts a relation from a value key to a nullable child foreign key.

**typeparam T:** The non-null parent value type.

**param parent:** The built parent table.

**param parentKey:** Its first primary-key component.

**param child:** The built child table.

**param foreignKey:** The compatible nullable child column.

**returns:** An immutable relation prefix.

**exception:** An argument is null.

**exception:** The pair violates identity, ordering or codec rules.

## `M:Korda.Data.Relation`2.Create``1(Korda.Data.Table{`0},Korda.Data.Column{`0,``0},Korda.Data.Table{`1},Korda.Data.Column{`1,``0})`

```csharp
public static Korda.Data.Relation<TParent, TChild> Korda.Data.Relation<TParent, TChild>.Create<T>(Korda.Data.Table<TParent> parent, Korda.Data.Column<TParent, T> parentKey, Korda.Data.Table<TChild> child, Korda.Data.Column<TChild, T> foreignKey)
```

**summary:** Starts a relation at key ordinal zero with a non-null foreign key.

**typeparam T:** The common scalar value type.

**param parent:** The built parent table.

**param parentKey:** Its first non-null primary-key column.

**param child:** The built child table.

**param foreignKey:** A compatible non-null child column.

**returns:** An immutable relation prefix.

**exception:** An argument is null.

**exception:** Identity, key order, codec, configuration, collation or nullability differs.

## `M:Korda.Data.ResultColumn`1.#ctor`

```csharp
public Korda.Data.ResultColumn<T>.ResultColumn()
```

Compiler-generated member; inherits the documented type semantics.

## `M:Korda.Data.ResultField.Create``1(System.String,Korda.Data.ValueCodec{``0})`

```csharp
public static Korda.Data.ResultField Korda.Data.ResultField.Create<T>(string name, Korda.Data.ValueCodec<T> codec)
```

**summary:** Creates a field for a static projection.

**typeparam T:** The statically known CLR value type.

**param name:** The nonempty exact result alias.

**param codec:** The selected scalar codec, configuration and nullability.

**returns:** An immutable result-field definition.

**exception:** A required argument is null.

**exception:** The alias is empty or invalid Unicode.

## `M:Korda.Data.RowMaterializer`1.#ctor(System.Object,System.IntPtr)`

```csharp
public Korda.Data.RowMaterializer<T>.RowMaterializer(object @object, nint method)
```

Compiler-generated member; inherits the documented type semantics.

## `M:Korda.Data.RowMaterializer`1.BeginInvoke(Korda.Data.RowReader,System.AsyncCallback,System.Object)`

```csharp
public virtual System.IAsyncResult Korda.Data.RowMaterializer<T>.BeginInvoke(Korda.Data.RowReader row, System.AsyncCallback callback, object @object)
```

Compiler-generated member; inherits the documented type semantics.

## `M:Korda.Data.RowMaterializer`1.EndInvoke(System.IAsyncResult)`

```csharp
public virtual T Korda.Data.RowMaterializer<T>.EndInvoke(System.IAsyncResult result)
```

Compiler-generated member; inherits the documented type semantics.

## `M:Korda.Data.RowMaterializer`1.Invoke(Korda.Data.RowReader)`

```csharp
public virtual T Korda.Data.RowMaterializer<T>.Invoke(Korda.Data.RowReader row)
```

Compiler-generated member; inherits the documented type semantics.

## `M:Korda.Data.RowReader.#ctor`

```csharp
public Korda.Data.RowReader.RowReader()
```

Compiler-generated member; inherits the documented type semantics.

## `M:Korda.Data.RowReader.Get``1(Korda.Data.ResultColumn{``0})`

```csharp
public T Korda.Data.RowReader.Get<T>(Korda.Data.ResultColumn<T> column)
```

**summary:** Reads a typed token belonging to this exact projection.

**typeparam T:** The result value type.

**param column:** The projection-bound result token.

**returns:** An owned value, possibly SQL NULL when declared nullable.

**exception:** The token belongs to another projection or is default.

**exception:** The shape or value is incompatible.

**exception:** The reader is default.

**exception:** The row lease has retired.

## `M:Korda.Data.RowReader.Get``2(Korda.Data.Column{``0,``1})`

```csharp
public T Korda.Data.RowReader.Get<TEntity, T>(Korda.Data.Column<TEntity, T> column) where TEntity : class
```

**summary:** Reads a typed token from this table's declared row projection.

**typeparam TEntity:** The application entity type.

**typeparam T:** The value type.

**param column:** The column from the exact table declaration.

**returns:** An owned value.

**exception:** is null.

**exception:** The token belongs to another table.

**exception:** The shape or value is incompatible.

**exception:** The row lease has retired.

## `M:Korda.Data.RowReader.IsNull(System.Int32)`

```csharp
public bool Korda.Data.RowReader.IsNull(int ordinal)
```

**summary:** Tests SQL NULL after complete shape validation; missing fields never mean NULL.

**param ordinal:** A zero-based ordinal in the complete declared shape.

**returns:** True for a declared SQL NULL cell.

**exception:** The ordinal is outside the shape.

**exception:** The reader is default.

**exception:** The lease has retired.

## `M:Korda.Data.RowReader.Read``1(System.Int32,Korda.Data.ValueCodec{``0})`

```csharp
public T Korda.Data.RowReader.Read<T>(int ordinal, Korda.Data.ValueCodec<T> codec)
```

**summary:** Reads an explicit ordinal with the exact declared scalar configuration and nullability.

**typeparam T:** The statically known value type.

**param ordinal:** A zero-based ordinal.

**param codec:** The exact scalar selection.

**returns:** An owned CLR value.

**exception:** is null.

**exception:** The ordinal is outside the shape.

**exception:** The codec or value does not match the validated shape.

**exception:** The lease has retired.

## `M:Korda.Data.RowWriter.#ctor`

```csharp
public Korda.Data.RowWriter.RowWriter()
```

Compiler-generated member; inherits the documented type semantics.

## `M:Korda.Data.RowWriter.Set``2(Korda.Data.Column{``0,``1},``1)`

```csharp
public void Korda.Data.RowWriter.Set<TEntity, T>(Korda.Data.Column<TEntity, T> column, T value) where TEntity : class
```

**summary:** Copies one typed value into the current row snapshot.

**typeparam TEntity:** The application entity type.

**typeparam T:** The exact column value type.

**param column:** The matching table's column.

**param value:** The value to snapshot; SQL NULL requires a nullable codec.

**exception:** A required argument or non-null value is null.

**exception:** The column belongs elsewhere or was assigned twice.

**exception:** The writer is default or the snapshot has completed.

## `M:Korda.Data.SqlCommand.Create(System.String,Korda.Data.SqlParameters,Korda.Data.SqlCommandKind,System.Nullable{System.TimeSpan})`

```csharp
public static Korda.Data.SqlCommand Korda.Data.SqlCommand.Create(string sql, Korda.Data.SqlParameters? parameters = null, Korda.Data.SqlCommandKind kind = Korda.Data.SqlCommandKind.Query, System.TimeSpan? timeout = null)
```

**summary:** Snapshots trusted named SQL and typed parameters without provider work.

**param sql:** Nonempty strict-Unicode trusted SQL with logical @name placeholders.

**param parameters:** Typed parameters, or no parameters.

**param kind:** The explicit effect classification.

**param timeout:** A nonnegative finite timeout, or the source's configured default; zero expires before dispatch.

**returns:** An immutable command; subsequent collection changes do not affect it.

**exception:** is null.

**exception:** The SQL is empty or invalid Unicode.

**exception:** The kind or timeout is invalid.

## `M:Korda.Data.SqlCommand.Parameterized(System.FormattableString,Korda.Data.SqlCommandKind,System.Nullable{System.TimeSpan})`

```csharp
public static Korda.Data.SqlCommand Korda.Data.SqlCommand.Parameterized(System.FormattableString sql, Korda.Data.SqlCommandKind kind = Korda.Data.SqlCommandKind.Query, System.TimeSpan? timeout = null)
```

**summary:** Snapshots built-in scalar interpolation values and literal segments, without building SQL.

**param sql:** A composite format with unformatted, unaligned typed non-null scalar arguments.

**param kind:** The explicit effect classification.

**param timeout:** A nonnegative finite timeout, or the source's configured default; zero expires before dispatch.

**returns:** An immutable template whose value holes will be bound by the native engine.

**exception:** is null.

**exception:** An argument is untyped null, requires an explicit codec, is unused, or a format/alignment is supplied.

**exception:** The kind or timeout is invalid.

## `M:Korda.Data.SqlCommandKind.#ctor`

```csharp
public Korda.Data.SqlCommandKind.SqlCommandKind()
```

Compiler-generated member; inherits the documented type semantics.

## `M:Korda.Data.SqlParameters.#ctor`

```csharp
public Korda.Data.SqlParameters.SqlParameters()
```

**summary:** Creates an empty ordered parameter collection.

## `M:Korda.Data.SqlParameters.Add``1(System.String,``0,Korda.Data.ValueCodec{``0})`

```csharp
public Korda.Data.SqlParameters Korda.Data.SqlParameters.Add<T>(string name, T value, Korda.Data.ValueCodec<T> codec)
```

**summary:** Copies one value using an explicit scalar codec and a unique logical name.

**typeparam T:** The statically known value type.

**param name:** An ASCII name matching [A-Za-z_][A-Za-z0-9_]*, at most 128 bytes.

**param value:** The value; SQL NULL requires an explicit nullable codec.

**param codec:** The scalar codec and configuration.

**returns:** This collection.

**exception:** A required argument is null.

**exception:** The name is invalid/duplicate or the value cannot be encoded exactly.

## `M:Korda.Data.TableBuilder`1.#ctor(System.String,System.String)`

```csharp
public Korda.Data.TableBuilder<TEntity>.TableBuilder(string tableName, string? schema = null)
```

**summary:** Starts a declaration with exact store names.

**param tableName:** A nonempty strict-Unicode table name.

**param schema:** A nonempty explicit schema or null; null is not an empty string.

**exception:** A name or closed type identity is invalid.

**exception:** is null.

## `M:Korda.Data.TableBuilder`1.Build(Korda.Data.RowMaterializer{`0},Korda.Data.EntityWriter{`0})`

```csharp
public Korda.Data.Table<TEntity> Korda.Data.TableBuilder<TEntity>.Build(Korda.Data.RowMaterializer<TEntity> materializer, Korda.Data.EntityWriter<TEntity> writer)
```

**summary:** Validates and consumes the builder, retaining direct managed mapping delegates.

**param materializer:** The synchronous typed row constructor.

**param writer:** The synchronous typed entity writer.

**returns:** An immutable table with canonical metadata and row projection.

**exception:** A delegate is null.

**exception:** There are no columns or the builder was already consumed.

## `M:Korda.Data.TableBuilder`1.Column``1(System.String,Korda.Data.ValueCodec{``0},Korda.Data.ColumnOptions)`

```csharp
public Korda.Data.Column<TEntity, T> Korda.Data.TableBuilder<TEntity>.Column<T>(string name, Korda.Data.ValueCodec<T> codec, Korda.Data.ColumnOptions? options = null)
```

**summary:** Adds a typed column in projection and primary-key declaration order.

**typeparam T:** The exact CLR value type.

**param name:** The exact store column name.

**param codec:** The non-null supported scalar selection.

**param options:** Immutable flags, or default flags.

**returns:** A token tied to this declaration.

**exception:** A duplicate name/ID, nullable key or invalid option is supplied.

**exception:** A required argument is null.

**exception:** The builder was consumed.

## `M:Korda.Data.Table`1.Key``1(Korda.Data.Column{`0,``0},``0)`

```csharp
public Korda.Data.EntityKey<TEntity> Korda.Data.Table<TEntity>.Key<T>(Korda.Data.Column<TEntity, T> column, T value)
```

**summary:** Starts a primary-key snapshot with key ordinal zero.

**typeparam T:** The key component's value type.

**param column:** This table's first non-null primary-key column.

**param value:** The typed value, copied into owned wire storage.

**returns:** An immutable key; append subsequent components with And.

**exception:** The column belongs elsewhere, is not ordinal zero, or the value is invalid.

**exception:** or a non-null value is null.

## `M:Korda.Data.Table`1.TryGetColumn``1(System.String,Korda.Data.ValueCodec{``0},Korda.Data.Column{`0,``0}@)`

```csharp
public bool Korda.Data.Table<TEntity>.TryGetColumn<T>(string name, Korda.Data.ValueCodec<T> codec, [System.Diagnostics.CodeAnalysis.NotNullWhenAttribute(true)] out Korda.Data.Column<TEntity, T>? column)
```

**summary:** Resolves a declared exact store name and codec without inspecting entity members.

**typeparam T:** The requested value type.

**param name:** The exact case-sensitive store name.

**param codec:** The required logical codec, configuration and nullability.

**param column:** The matching token or null.

**returns:** True only for a complete typed and metadata match.

**exception:** is invalid.

**exception:** A required argument is null.

## `M:Korda.Data.WeaveColumnAttribute.#ctor(System.String)`

```csharp
public Korda.Data.WeaveColumnAttribute.WeaveColumnAttribute(string name)
```

**summary:** Declares the exact store column name.

**param name:** A nonempty store identifier.

**exception:** The name is empty or whitespace.

**inheritdoc:**

## `M:Korda.Data.WeaveIgnoreAttribute.#ctor`

```csharp
public Korda.Data.WeaveIgnoreAttribute.WeaveIgnoreAttribute()
```

Compiler-generated member; inherits the documented type semantics.

## `M:Korda.Data.WeaveKeyAttribute.#ctor`

```csharp
public Korda.Data.WeaveKeyAttribute.WeaveKeyAttribute()
```

Compiler-generated member; inherits the documented type semantics.

## `M:Korda.Data.WeaveTableAttribute.#ctor(System.String)`

```csharp
public Korda.Data.WeaveTableAttribute.WeaveTableAttribute(string name)
```

**summary:** Declares the exact store table name.

**param name:** A nonempty table identifier; casing is preserved.

**exception:** The name is empty or whitespace.

**inheritdoc:**

## `M:Korda.Data.WeaveVersionAttribute.#ctor`

```csharp
public Korda.Data.WeaveVersionAttribute.WeaveVersionAttribute()
```

Compiler-generated member; inherits the documented type semantics.

## `P:Korda.Data.Codecs.Boolean`

```csharp
public static Korda.Data.ValueCodec<bool> Korda.Data.Codecs.Boolean { get; }
```

**summary:** Gets the Boolean codec whose only wire bytes are zero and one.

## `P:Korda.Data.Codecs.Bytes`

```csharp
public static Korda.Data.ValueCodec<byte[]> Korda.Data.Codecs.Bytes { get; }
```

**summary:** Gets binary values; input and output arrays are copied at a snapshot boundary.

## `P:Korda.Data.Codecs.Date`

```csharp
public static Korda.Data.ValueCodec<System.DateOnly> Korda.Data.Codecs.Date { get; }
```

**summary:** Gets signed days since 1970-01-01 within the DateOnly domain.

## `P:Korda.Data.Codecs.DecimalText`

```csharp
public static Korda.Data.ValueCodec<decimal> Korda.Data.Codecs.DecimalText { get; }
```

**summary:** Gets exact decimal text storage; provider arithmetic, range ordering and aggregation are not implied.

## `P:Korda.Data.Codecs.Double`

```csharp
public static Korda.Data.ValueCodec<double> Korda.Data.Codecs.Double { get; }
```

**summary:** Gets finite IEEE 754 binary64 values, preserving signed zero in the wire.

## `P:Korda.Data.Codecs.Guid`

```csharp
public static Korda.Data.ValueCodec<System.Guid> Korda.Data.Codecs.Guid { get; }
```

**summary:** Gets RFC 4122 network-order UUID encoding.

## `P:Korda.Data.Codecs.Int16`

```csharp
public static Korda.Data.ValueCodec<short> Korda.Data.Codecs.Int16 { get; }
```

**summary:** Gets the exact signed 16-bit codec.

## `P:Korda.Data.Codecs.Int32`

```csharp
public static Korda.Data.ValueCodec<int> Korda.Data.Codecs.Int32 { get; }
```

**summary:** Gets the exact signed 32-bit codec.

## `P:Korda.Data.Codecs.Int64`

```csharp
public static Korda.Data.ValueCodec<long> Korda.Data.Codecs.Int64 { get; }
```

**summary:** Gets the exact signed 64-bit codec.

## `P:Korda.Data.Codecs.Single`

```csharp
public static Korda.Data.ValueCodec<float> Korda.Data.Codecs.Single { get; }
```

**summary:** Gets finite IEEE 754 binary32 values, preserving signed zero in the wire.

## `P:Korda.Data.Codecs.String`

```csharp
public static Korda.Data.ValueCodec<string> Korda.Data.Codecs.String { get; }
```

**summary:** Gets strict UTF-8 text with no normalization or implicit SQL collation.

## `P:Korda.Data.Codecs.Time`

```csharp
public static Korda.Data.ValueCodec<System.TimeOnly> Korda.Data.Codecs.Time { get; }
```

**summary:** Gets microseconds since midnight; submicrosecond precision rejects.

## `P:Korda.Data.Codecs.UtcDateTime`

```csharp
public static Korda.Data.ValueCodec<System.DateTime> Korda.Data.Codecs.UtcDateTime { get; }
```

**summary:** Gets UTC-only DateTime values; Local and Unspecified values reject.

## `P:Korda.Data.Codecs.UtcInstant`

```csharp
public static Korda.Data.ValueCodec<System.DateTimeOffset> Korda.Data.Codecs.UtcInstant { get; }
```

**summary:** Gets UTC-normalized instants in Unix microseconds; no precision loss is allowed.

## `P:Korda.Data.ColumnOptions.Collation`

```csharp
public string? Korda.Data.ColumnOptions.Collation { get; init; }
```

**summary:** Gets the exact provider collation name, or no explicit collation.

## `P:Korda.Data.ColumnOptions.IsConcurrencyToken`

```csharp
public bool Korda.Data.ColumnOptions.IsConcurrencyToken { get; init; }
```

**summary:** Gets whether the column is an explicit optimistic-concurrency token.

## `P:Korda.Data.ColumnOptions.IsGenerated`

```csharp
public bool Korda.Data.ColumnOptions.IsGenerated { get; init; }
```

**summary:** Gets whether the database generates the value on insert.

## `P:Korda.Data.ColumnOptions.IsPrimaryKey`

```csharp
public bool Korda.Data.ColumnOptions.IsPrimaryKey { get; init; }
```

**summary:** Gets whether the column participates in the primary key, ordered by declaration.

## `P:Korda.Data.ColumnOptions.MaxLength`

```csharp
public int? Korda.Data.ColumnOptions.MaxLength { get; init; }
```

**summary:** Gets the positive maximum byte capacity, or no declared capacity.

## `P:Korda.Data.Column`2.Codec`

```csharp
public Korda.Data.ValueCodec<T> Korda.Data.Column<TEntity, T>.Codec { get; }
```

**summary:** Gets the exact logical codec and its configuration.

## `P:Korda.Data.Column`2.Id`

```csharp
public uint Korda.Data.Column<TEntity, T>.Id { get; }
```

**summary:** Gets the stable nonzero little-endian SHA-256-derived column ID.

## `P:Korda.Data.Column`2.Name`

```csharp
public string Korda.Data.Column<TEntity, T>.Name { get; }
```

**summary:** Gets the exact store name, preserving case and Unicode.

## `P:Korda.Data.ConcurrencyConflictException.ActualRows`

```csharp
public long Korda.Data.ConcurrencyConflictException.ActualRows { get; }
```

**summary:** Gets the actual affected-row count.

## `P:Korda.Data.ConcurrencyConflictException.ExpectedRows`

```csharp
public long Korda.Data.ConcurrencyConflictException.ExpectedRows { get; }
```

**summary:** Gets the expected affected-row count.

## `P:Korda.Data.DatabaseCapabilityException.Capability`

```csharp
public string Korda.Data.DatabaseCapabilityException.Capability { get; }
```

**summary:** Gets the exact required capability name.

## `P:Korda.Data.DatabaseCapabilityException.Provider`

```csharp
public string Korda.Data.DatabaseCapabilityException.Provider { get; }
```

**summary:** Gets the provider identity.

## `P:Korda.Data.DatabaseException.CorrelationId`

```csharp
public ulong Korda.Data.DatabaseException.CorrelationId { get; }
```

**summary:** Gets the native correlation identity, or zero when absent.

## `P:Korda.Data.DatabaseException.IsTransient`

```csharp
public bool Korda.Data.DatabaseException.IsTransient { get; }
```

**summary:** Gets the provider's explicit classification; unknown outcomes are always nontransient.

## `P:Korda.Data.DatabaseException.Kind`

```csharp
public Korda.Data.DatabaseErrorKind Korda.Data.DatabaseException.Kind { get; }
```

**summary:** Gets the immutable provider-neutral category.

## `P:Korda.Data.DatabaseException.ProviderCode`

```csharp
public string? Korda.Data.DatabaseException.ProviderCode { get; }
```

**summary:** Gets the optional provider code without interpreting it as retry authorization.

## `P:Korda.Data.Projection`1.ShapeHash`

```csharp
public string Korda.Data.Projection<T>.ShapeHash { get; }
```

**summary:** Gets lowercase SHA-256 of the exact logical shape, excluding managed delegate and native IDs.

## `P:Korda.Data.ResultColumn`1.Name`

```csharp
public string Korda.Data.ResultColumn<T>.Name { get; }
```

**summary:** Gets the exact result alias; the default token rejects.

**exception:** This token is default.

## `P:Korda.Data.ResultColumn`1.Ordinal`

```csharp
public int Korda.Data.ResultColumn<T>.Ordinal { get; }
```

**summary:** Gets the zero-based ordinal; the default token rejects.

**exception:** This token is default.

## `P:Korda.Data.ResultField.CodecId`

```csharp
public uint Korda.Data.ResultField.CodecId { get; }
```

**summary:** Gets the logical codec ID, independent of source-bound configured IDs.

## `P:Korda.Data.ResultField.IsNullable`

```csharp
public bool Korda.Data.ResultField.IsNullable { get; }
```

**summary:** Gets whether the typed codec permits SQL NULL.

## `P:Korda.Data.ResultField.Name`

```csharp
public string Korda.Data.ResultField.Name { get; }
```

**summary:** Gets the exact result alias.

## `P:Korda.Data.Table`1.MetadataHash`

```csharp
public string Korda.Data.Table<TEntity>.MetadataHash { get; }
```

**summary:** Gets lowercase hexadecimal SHA-256 of the canonical model bytes.

## `P:Korda.Data.Table`1.Name`

```csharp
public string Korda.Data.Table<TEntity>.Name { get; }
```

**summary:** Gets the exact table name.

## `P:Korda.Data.Table`1.Row`

```csharp
public Korda.Data.Projection<TEntity> Korda.Data.Table<TEntity>.Row { get; }
```

**summary:** Gets the immutable projection in declared constructor order.

## `P:Korda.Data.Table`1.Schema`

```csharp
public string? Korda.Data.Table<TEntity>.Schema { get; }
```

**summary:** Gets the explicit schema or null.

## `P:Korda.Data.ValueCodec`1.Id`

```csharp
public uint Korda.Data.ValueCodec<T>.Id { get; }
```

**summary:** Gets the logical db-wire scalar ID, independent of source-bound configured IDs.

## `P:Korda.Data.ValueCodec`1.Name`

```csharp
public string Korda.Data.ValueCodec<T>.Name { get; }
```

**summary:** Gets the descriptive scalar codec name.

## `P:Korda.Data.WeaveColumnAttribute.Generated`

```csharp
public bool Korda.Data.WeaveColumnAttribute.Generated { get; init; }
```

**summary:** Gets or initializes whether the database supplies the value on insertion.

**inheritdoc:**

## `P:Korda.Data.WeaveTableAttribute.DescriptorName`

```csharp
public string? Korda.Data.WeaveTableAttribute.DescriptorName { get; init; }
```

**summary:** Gets or initializes the generated static descriptor class name; default is EntityNameMapping.

**inheritdoc:**

## `P:Korda.Data.WeaveTableAttribute.Schema`

```csharp
public string? Korda.Data.WeaveTableAttribute.Schema { get; init; }
```

**summary:** Gets or initializes the optional exact schema name.

**inheritdoc:**

## `T:Korda.Data.Changes`1`

```csharp
public sealed class Korda.Data.Changes<TEntity> where TEntity : class
```

**summary:** A single-writer explicit changeset containing owned value snapshots.

**typeparam TEntity:** The application entity class.

**remarks:** This is assignment data, not a SQL AST. Unset removes an assignment; Set with a nullable value assigns SQL NULL. Native execution snapshots the current changes independently and rejects an empty set.

## `T:Korda.Data.Codecs`

```csharp
public static class Korda.Data.Codecs
```

**summary:** Factories for the fixed db-wire/1 logical scalar registry.

**remarks:** Selections are immutable. Encoding snapshots strings, byte arrays and JSON into owned storage. No provider, native loader, reflection serializer or SQL semantics are installed by these factories.

## `T:Korda.Data.ColumnOptions`

```csharp
public sealed record class Korda.Data.ColumnOptions : System.IEquatable<Korda.Data.ColumnOptions>
```

**summary:** Immutable declaration flags for one table column.

**remarks:** Null optional settings remain distinct from defaults. MaxLength is a positive byte capacity. The builder snapshots this record and validates key nullability before publishing a table.

## `T:Korda.Data.Column`2`

```csharp
public sealed class Korda.Data.Column<TEntity, T> where TEntity : class
```

**summary:** An immutable entity- and value-typed column token belonging to one table declaration.

**typeparam TEntity:** The application entity class.

**typeparam T:** The exact value type selected by the codec.

**remarks:** Tokens expose no native pointer. Equivalent metadata in another builder does not authorize using its token.

## `T:Korda.Data.ConcurrencyCheck`1`

```csharp
public sealed class Korda.Data.ConcurrencyCheck<TEntity> where TEntity : class
```

**summary:** A single-writer explicit set of expected concurrency-token values.

**typeparam TEntity:** The application entity class.

**remarks:** Values are copied immediately. There is no identity map, implicit version increment or managed compare-and-swap.

## `T:Korda.Data.ConcurrencyConflictException`

```csharp
public sealed class Korda.Data.ConcurrencyConflictException : Korda.Data.DatabaseException
```

**summary:** A native optimistic write did not match its exact expected affected-row count.

**remarks:** The original key and expected token predicate ran once in the database. This exception never authorizes a write replay.

## `T:Korda.Data.DatabaseCapabilityException`

```csharp
public sealed class Korda.Data.DatabaseCapabilityException : Korda.Data.DatabaseException
```

**summary:** Identifies a required capability missing from an engine or provider profile.

**remarks:** The owned exception retains validated managed identity strings and no native handle. Constructing or reading it performs no provider call; capability admission rejects before business dispatch.

## `T:Korda.Data.DatabaseErrorKind`

```csharp
public enum Korda.Data.DatabaseErrorKind : int
```

**summary:** Provider-neutral database failure categories; cancellation and timeouts use their standard CLR exceptions.

## `T:Korda.Data.DatabaseException`

```csharp
public class Korda.Data.DatabaseException : System.Exception
```

**summary:** Preserves a bounded provider-neutral failure and correlation identity.

**remarks:** Diagnostic values are immutable and remain readable after cleanup. Construction loads no native engine. A transient cause is not permission to retry a write; outcome and application idempotency remain separate.

## `T:Korda.Data.EntityKey`1`

```csharp
public sealed class Korda.Data.EntityKey<TEntity> where TEntity : class
```

**summary:** An immutable ordered snapshot of a table's non-null primary-key values.

**typeparam TEntity:** The application entity class.

**remarks:** Incomplete prefixes are construction values only; registration or execution requires the complete key.

## `T:Korda.Data.EntityWriter`1`

```csharp
public delegate void Korda.Data.EntityWriter<T>(Korda.Data.RowWriter row, T value)
```

**summary:** Synchronously extracts typed entity values before native dispatch.

**typeparam T:** The application entity type.

**param row:** A stack-only writer valid only for this snapshot.

**param value:** The application entity.

**remarks:** The callback runs synchronously during a finite input snapshot. Typed writes copy encoded values into owned buffers before native dispatch; the stack-only writer must not be retained.

## `T:Korda.Data.Projection`1`

```csharp
public sealed class Korda.Data.Projection<T>
```

**summary:** An immutable ordered static row shape and direct materializer.

**typeparam T:** The owned result type; value types and reference types are supported.

**remarks:** Creation copies the field sequence and hashes canonical metadata. Materializers stay managed; no CLR delegate or function pointer is encoded in metadata. Instances are shareable if their delegates are.

## `T:Korda.Data.Relation`2`

```csharp
public sealed class Korda.Data.Relation<TParent, TChild> where TParent : class where TChild : class
```

**summary:** Immutable ordered primary-key/foreign-key metadata over two already-built tables.

**typeparam TParent:** The parent entity class.

**typeparam TChild:** The child entity class.

**remarks:** Creation loads no native engine. And methods return new descriptors and never alter either table hash. A complete key prefix is required for native registration; lazy loading and alternate-key inference are excluded.

## `T:Korda.Data.ResultColumn`1`

```csharp
public readonly struct Korda.Data.ResultColumn<T>
```

**summary:** An opaque result-column token created by the owning query projection.

**typeparam T:** The exact result type.

**remarks:** The default token is invalid; no public constructor can forge projection ownership.

## `T:Korda.Data.ResultField`

```csharp
public sealed class Korda.Data.ResultField
```

**summary:** An immutable exact result alias, logical codec configuration and SQL nullability.

**remarks:** Creating a field allocates one managed metadata object with no native resource. It can be retained in a static projection; source-specific codec binding occurs separately at registration.

## `T:Korda.Data.RowMaterializer`1`

```csharp
public delegate T Korda.Data.RowMaterializer<T>(Korda.Data.RowReader row)
```

**summary:** Synchronously materializes one validated row into an owned CLR value.

**typeparam T:** The result value or entity type.

**param row:** A borrowed stack-only reader valid only for the call.

**returns:** The owned result; no row borrow may escape.

**remarks:** The callback runs synchronously while its batch lease is live. Decoding and the application constructor create owned CLR values; borrowed reader state must not escape or cross an await.

## `T:Korda.Data.RowReader`

```csharp
public readonly ref struct Korda.Data.RowReader
```

**summary:** A stack-only reader over a validated live row lease.

**remarks:** Shape validation precedes NULL interpretation. Every read checks the lease; default readers reject. Values are owned CLR copies. Readers cannot be retained across await or stored in an ordinary heap object.

## `T:Korda.Data.RowWriter`

```csharp
public readonly ref struct Korda.Data.RowWriter
```

**summary:** A stack-only writer that snapshots typed entity fields without invoking native code.

**remarks:** A writer is valid only during EntityWriter invocation. Repeated assignment rejects. Insert policy omits generated columns. An exception publishes no partial row to a provider.

## `T:Korda.Data.SqlCommand`

```csharp
public sealed class Korda.Data.SqlCommand
```

**summary:** An immutable, shareable trusted SQL template and owned typed parameter snapshot.

**remarks:** This type does not parse SQL, choose dialects, prove safety or contact a provider. The native engine transports placeholders and validates actual capability/effect policy before dispatch. Interpolated convenience has boxing costs.

## `T:Korda.Data.SqlCommandKind`

```csharp
public enum Korda.Data.SqlCommandKind : int
```

**summary:** The explicit trusted command effect classification.

## `T:Korda.Data.SqlParameters`

```csharp
public sealed class Korda.Data.SqlParameters
```

**summary:** A single-writer ordered collection of typed owned parameter snapshots.

**remarks:** Names are case-sensitive ASCII without @. Add copies mutable values immediately; command construction snapshots the collection. Provider placeholder scanning and missing/unused-hole checks remain native B02/B03 work.

## `T:Korda.Data.TableBuilder`1`

```csharp
public sealed class Korda.Data.TableBuilder<TEntity> where TEntity : class
```

**summary:** Constructs one immutable table declaration using explicit AOT-visible delegates.

**typeparam TEntity:** The application entity class.

**remarks:** This single-writer startup builder is consumed by successful Build. It performs O(column count) managed work and loads no native engine. The known closed type is used only for its assembly/name identity.

## `T:Korda.Data.Table`1`

```csharp
public sealed class Korda.Data.Table<TEntity> where TEntity : class
```

**summary:** An immutable, shareable table declaration; first native registration belongs to the source owner.

**typeparam TEntity:** The application entity class.

**remarks:** Canonical metadata hashes are business schema identities, independent of ABI and package versions.

## `T:Korda.Data.ValueCodec`1`

```csharp
public sealed class Korda.Data.ValueCodec<T>
```

**summary:** A closed typed selection of one precompiled logical scalar codec.

**typeparam T:** The statically known CLR value type.

**remarks:** Instances are immutable and shareable. IDs are logical metadata, not source-owned native handles. Nullable wrappers preserve the logical ID. Configured decimal identities include precision and scale. No native runtime is loaded; provider conversions and capability enforcement belong to B07.

## `T:Korda.Data.WeaveColumnAttribute`

```csharp
[System.AttributeUsageAttribute(System.AttributeTargets.Property, Inherited = false)]
public sealed class Korda.Data.WeaveColumnAttribute : System.Attribute
```

**summary:** Overrides a property's exact store name and generated-value policy.

**remarks:** Without this attribute the CLR property symbol name is used unchanged. The generated column member keeps its CLR spelling.

## `T:Korda.Data.WeaveIgnoreAttribute`

```csharp
[System.AttributeUsageAttribute(System.AttributeTargets.Property, Inherited = false)]
public sealed class Korda.Data.WeaveIgnoreAttribute : System.Attribute
```

**summary:** Excludes a property from generated metadata and row serialization.

**remarks:** Combining Ignore with another mapping annotation is inconsistent and rejected. Required constructor inputs still need a supported static mapping.

## `T:Korda.Data.WeaveKeyAttribute`

```csharp
[System.AttributeUsageAttribute(System.AttributeTargets.Property, Inherited = false)]
public sealed class Korda.Data.WeaveKeyAttribute : System.Attribute
```

**summary:** Marks a non-null primary-key property in deterministic projection order.

**remarks:** Nullable keys are rejected at compilation and again by the static descriptor builder; there is no inferred key convention.

## `T:Korda.Data.WeaveTableAttribute`

```csharp
[System.AttributeUsageAttribute(System.AttributeTargets.Class, Inherited = false)]
public sealed class Korda.Data.WeaveTableAttribute : System.Attribute
```

**summary:** Opts a model into deterministic, static table and DTO mapping generation.

**remarks:** The generator reads source symbols and emits the same TableBuilder calls as manual metadata. It never opens a database, loads a native module, compiles native code or discovers runtime model types.

## `T:Korda.Data.WeaveVersionAttribute`

```csharp
[System.AttributeUsageAttribute(System.AttributeTargets.Property, Inherited = false)]
public sealed class Korda.Data.WeaveVersionAttribute : System.Attribute
```

**summary:** Marks an explicit optimistic-concurrency token.

**remarks:** Generated metadata sets IsConcurrencyToken. The application supplies expected and changed values; no automatic version increment occurs.

