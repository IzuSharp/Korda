# Korda.Data explicit relationship and mutation API

Generated from the compiled product assembly and XML by `src/Build.Integration/ApiReference`.

Compiled product declarations compared to Design commit c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9, the DB-API-01 additions and the explicit DB-Rename-01 Attribute name mapping. B14 owns explicit relation loading, native CRUD, optimistic concurrency and bounded bulk; docs/guides/orm-relations.md explains ownership, limits and cost.

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

**Inherited type policy:** This is assignment data, not a SQL AST. Unset removes an assignment; Set with a nullable value assigns SQL NULL. Native execution snapshots the current changes independently and rejects an empty set.

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

**Inherited type policy:** This is assignment data, not a SQL AST. Unset removes an assignment; Set with a nullable value assigns SQL NULL. Native execution snapshots the current changes independently and rejects an empty set.

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

**Inherited type policy:** This is assignment data, not a SQL AST. Unset removes an assignment; Set with a nullable value assigns SQL NULL. Native execution snapshots the current changes independently and rejects an empty set.

## `M:Korda.Data.Codecs.Convert``2(Korda.Data.ValueCodec{``1},System.Func{``0,``1},System.Func{``1,``0})`

```csharp
public static Korda.Data.ValueCodec<T> Korda.Data.Codecs.Convert<T, TStore>(Korda.Data.ValueCodec<TStore> storage, System.Func<T, TStore> toStore, System.Func<TStore, T> fromStore)
```

**summary:** Maps a business value through an existing nonnullable store codec without adding a native scalar type.

**typeparam T:** The closed business value type, such as an application identifier.

**typeparam TStore:** The selected store codec's CLR value type.

**param storage:** The nonnullable codec whose identity, configuration and encoding bounds are preserved.

**param toStore:** A direct synchronous conversion used before native admission.

**param fromStore:** A direct synchronous conversion used while materializing an owned value.

**returns:** An immutable codec retaining the supplied delegates and the original native scalar identity.

**exception:** A required argument is null.

**exception:** The storage codec already permits SQL NULL.

**remarks:** Nullable and NullableReference can wrap the result. Bounded writes delegate to the storage codec before byte allocation, preserving string, binary and JSON limits. User conversions are managed application code; their failures follow normal snapshot or materialization cleanup and never cross a native callback boundary.

**Inherited type policy:** Selections are immutable. Encoding snapshots strings, byte arrays and JSON into owned storage. No provider, native loader, reflection serializer or SQL semantics are installed by these factories.

## `M:Korda.Data.Codecs.Decimal(System.Byte,System.Byte)`

```csharp
public static Korda.Data.ValueCodec<decimal> Korda.Data.Codecs.Decimal(byte precision, byte scale)
```

**summary:** Selects exact decimal numeric precision and scale; ordinary SQLite does not support this provider profile.

**param precision:** Total decimal precision from one through 29.

**param scale:** Scale from zero through the smaller of 28 and precision.

**returns:** An immutable configured logical codec.

**exception:** The precision or scale is outside the declared range.

**Inherited type policy:** Selections are immutable. Encoding snapshots strings, byte arrays and JSON into owned storage. No provider, native loader, reflection serializer or SQL semantics are installed by these factories.

## `M:Korda.Data.Codecs.EnumInt32``1`

```csharp
public static Korda.Data.ValueCodec<TEnum> Korda.Data.Codecs.EnumInt32<TEnum>() where TEnum : struct, System.Enum
```

**summary:** Selects a checked signed Int32 enum representation, preserving undefined numeric values.

**typeparam TEnum:** A statically known CLR enum.

**returns:** An immutable enum codec; values outside Int32 reject at snapshot.

**Inherited type policy:** Selections are immutable. Encoding snapshots strings, byte arrays and JSON into owned storage. No provider, native loader, reflection serializer or SQL semantics are installed by these factories.

## `M:Korda.Data.Codecs.FixedScaleMoney(System.Byte)`

```csharp
public static Korda.Data.ValueCodec<decimal> Korda.Data.Codecs.FixedScaleMoney(byte scale)
```

**summary:** Selects exact signed Int64 fixed-scale storage, without rounding.

**param scale:** The decimal scale from zero through 18.

**returns:** An immutable configured money codec.

**exception:** exceeds 18.

**Inherited type policy:** Selections are immutable. Encoding snapshots strings, byte arrays and JSON into owned storage. No provider, native loader, reflection serializer or SQL semantics are installed by these factories.

## `M:Korda.Data.Codecs.Json``1(System.Text.Json.Serialization.Metadata.JsonTypeInfo{``0})`

```csharp
public static Korda.Data.ValueCodec<T> Korda.Data.Codecs.Json<T>(System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
```

**summary:** Selects JSON serialization using explicit static metadata; JSON null is distinct from SQL NULL.

**typeparam T:** The statically serializable value.

**param typeInfo:** Explicit source-generated or manually authored JSON metadata.

**returns:** An immutable typed JSON codec that retains the supplied metadata.

**exception:** is null.

**Inherited type policy:** Selections are immutable. Encoding snapshots strings, byte arrays and JSON into owned storage. No provider, native loader, reflection serializer or SQL semantics are installed by these factories.

## `M:Korda.Data.Codecs.NullableReference``1(Korda.Data.ValueCodec{``0})`

```csharp
public static Korda.Data.ValueCodec<T?> Korda.Data.Codecs.NullableReference<T>(Korda.Data.ValueCodec<T> codec) where T : class
```

**summary:** Adds SQL NULL support to a reference codec while preserving its logical scalar identity.

**typeparam T:** The reference type.

**param codec:** The selected non-null scalar codec.

**returns:** A nullable reference codec.

**exception:** is null.

**Inherited type policy:** Selections are immutable. Encoding snapshots strings, byte arrays and JSON into owned storage. No provider, native loader, reflection serializer or SQL semantics are installed by these factories.

## `M:Korda.Data.Codecs.Nullable``1(Korda.Data.ValueCodec{``0})`

```csharp
public static Korda.Data.ValueCodec<T?> Korda.Data.Codecs.Nullable<T>(Korda.Data.ValueCodec<T> codec) where T : struct
```

**summary:** Adds SQL NULL support to a value codec without changing its logical scalar identity.

**typeparam T:** A non-nullable value type.

**param codec:** The selected non-null scalar codec.

**returns:** A typed nullable codec with the same configuration.

**exception:** is null.

**Inherited type policy:** Selections are immutable. Encoding snapshots strings, byte arrays and JSON into owned storage. No provider, native loader, reflection serializer or SQL semantics are installed by these factories.

## `M:Korda.Data.ColumnOptions.#ctor`

```csharp
public Korda.Data.ColumnOptions.ColumnOptions()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Null optional settings remain distinct from defaults. MaxLength is a positive byte capacity. The builder snapshots this record and validates key nullability before publishing a table.

## `M:Korda.Data.ColumnOptions.Equals(Korda.Data.ColumnOptions)`

```csharp
public bool Korda.Data.ColumnOptions.Equals(Korda.Data.ColumnOptions? other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Null optional settings remain distinct from defaults. MaxLength is a positive byte capacity. The builder snapshots this record and validates key nullability before publishing a table.

## `M:Korda.Data.ColumnOptions.Equals(System.Object)`

```csharp
public override bool Korda.Data.ColumnOptions.Equals(object? obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Null optional settings remain distinct from defaults. MaxLength is a positive byte capacity. The builder snapshots this record and validates key nullability before publishing a table.

## `M:Korda.Data.ColumnOptions.GetHashCode`

```csharp
public override int Korda.Data.ColumnOptions.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Null optional settings remain distinct from defaults. MaxLength is a positive byte capacity. The builder snapshots this record and validates key nullability before publishing a table.

## `M:Korda.Data.ColumnOptions.ToString`

```csharp
public override string Korda.Data.ColumnOptions.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Null optional settings remain distinct from defaults. MaxLength is a positive byte capacity. The builder snapshots this record and validates key nullability before publishing a table.

## `M:Korda.Data.ColumnOptions.op_Equality(Korda.Data.ColumnOptions,Korda.Data.ColumnOptions)`

```csharp
public static bool Korda.Data.ColumnOptions.operator ==(Korda.Data.ColumnOptions? left, Korda.Data.ColumnOptions? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Null optional settings remain distinct from defaults. MaxLength is a positive byte capacity. The builder snapshots this record and validates key nullability before publishing a table.

## `M:Korda.Data.ColumnOptions.op_Inequality(Korda.Data.ColumnOptions,Korda.Data.ColumnOptions)`

```csharp
public static bool Korda.Data.ColumnOptions.operator !=(Korda.Data.ColumnOptions? left, Korda.Data.ColumnOptions? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Null optional settings remain distinct from defaults. MaxLength is a positive byte capacity. The builder snapshots this record and validates key nullability before publishing a table.

## `M:Korda.Data.ColumnOptions.{Clone}$`

```csharp
public Korda.Data.ColumnOptions Korda.Data.ColumnOptions.<Clone>$()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Null optional settings remain distinct from defaults. MaxLength is a positive byte capacity. The builder snapshots this record and validates key nullability before publishing a table.

## `M:Korda.Data.ConcurrencyCheck`1.#ctor`

```csharp
public Korda.Data.ConcurrencyCheck<TEntity>.ConcurrencyCheck()
```

**summary:** Creates an empty check; execution requires all declared concurrency tokens.

**Inherited type policy:** Values are copied immediately. There is no identity map, implicit version increment or managed compare-and-swap.

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

**Inherited type policy:** Values are copied immediately. There is no identity map, implicit version increment or managed compare-and-swap.

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

**Inherited type policy:** The original key and expected token predicate ran once in the database. This exception never authorizes a write replay.

## `M:Korda.Data.DatabaseCapabilityException.#ctor(System.String,System.String)`

```csharp
public Korda.Data.DatabaseCapabilityException.DatabaseCapabilityException(string capability, string provider)
```

**summary:** Creates a failure that must be reported before a native business side effect.

**param capability:** The nonempty stable capability name.

**param provider:** The nonempty provider identity.

**exception:** An identity is empty or contains invalid Unicode.

**exception:** An identity is null.

**Inherited type policy:** The owned exception retains validated managed identity strings and no native handle. Constructing or reading it performs no provider call; capability admission rejects before business dispatch.

## `M:Korda.Data.DatabaseErrorKind.#ctor`

```csharp
public Korda.Data.DatabaseErrorKind.DatabaseErrorKind()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

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

**Inherited type policy:** Diagnostic values are immutable and remain readable after cleanup. Construction loads no native engine. A transient cause is not permission to retry a write; outcome and application idempotency remain separate.

## `M:Korda.Data.DbCodecAttribute.#ctor(System.Type,System.Type,System.String)`

```csharp
public Korda.Data.DbCodecAttribute.DbCodecAttribute(System.Type valueType, System.Type providerType, string memberName)
```

**summary:** Declares a reusable static codec policy.

**param valueType:** The closed, nonnullable CLR type whose properties use this default.

**param providerType:** The accessible type declaring the static codec field or getter.

**param memberName:** The exact accessible static member name, normally supplied with nameof.

**exception:** A type is null.

**exception:** The member name is empty or whitespace.

**inheritdoc:**

**Inherited type policy:** The generator reads an accessible static field or getter of exactly ValueCodec<T> from the supplied provider type. The value is captured when each generated descriptor initializes. Nullable properties wrap this base policy. Duplicate defaults and incompatible members fail compilation; no runtime type registry is created.

## `M:Korda.Data.DbColumnAttribute.#ctor(System.String)`

```csharp
public Korda.Data.DbColumnAttribute.DbColumnAttribute(string name)
```

**summary:** Declares the exact store column name.

**param name:** A nonempty store identifier.

**exception:** The name is empty or whitespace.

**inheritdoc:**

**Inherited type policy:** Without this attribute the CLR property symbol name is used unchanged. The generated column member keeps its CLR spelling.

## `M:Korda.Data.DbConstructorAttribute.#ctor`

```csharp
public Korda.Data.DbConstructorAttribute.DbConstructorAttribute()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Exactly one constructor may be selected. Every parameter must map to one compatible property and remaining properties must have accessible setters or init accessors. Without this attribute the existing unique-constructor rule applies.

## `M:Korda.Data.DbIgnoreAttribute.#ctor`

```csharp
public Korda.Data.DbIgnoreAttribute.DbIgnoreAttribute()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Combining Ignore with another mapping annotation is inconsistent and rejected. Required constructor inputs still need a supported static mapping.

## `M:Korda.Data.DbKeyAttribute.#ctor`

```csharp
public Korda.Data.DbKeyAttribute.DbKeyAttribute()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Nullable keys are rejected at compilation and again by the static descriptor builder. Explicit keys take precedence over the opt-in Id convention.

## `M:Korda.Data.DbParameterAttribute.#ctor(System.String)`

```csharp
public Korda.Data.DbParameterAttribute.DbParameterAttribute(string propertyName)
```

**summary:** Declares the exact CLR property supplying this parameter.

**param propertyName:** A nonempty, case-sensitive CLR property name.

**exception:** The name is empty or whitespace.

**inheritdoc:**

**Inherited type policy:** Use nameof(Property) when a constructor parameter has a different name. This does not rename the store column or result alias. Duplicate, missing and type-incompatible associations fail compilation.

## `M:Korda.Data.DbProjectionAttribute.#ctor`

```csharp
public Korda.Data.DbProjectionAttribute.DbProjectionAttribute()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The attributed ordinary class or record is an owned DTO, not a table. Its descriptor exposes Row for parameterized SQL and Bind for exactly selected Fluent expressions. Explicit column attributes supply result aliases; no reflection or native recompilation is required.

## `M:Korda.Data.DbTableAttribute.#ctor`

```csharp
public Korda.Data.DbTableAttribute.DbTableAttribute()
```

**summary:** Uses the exact CLR type name and infers a unique non-null Id primary key.

**remarks:** There is no pluralization or casing conversion. Explicit keys take precedence; set InferPrimaryKey to false to declare a keyless model.

**Inherited type policy:** The generator reads source symbols and emits the same TableBuilder calls as manual metadata. It never opens a database, loads a native module, compiles native code or discovers runtime model types.

## `M:Korda.Data.DbTableAttribute.#ctor(System.String)`

```csharp
public Korda.Data.DbTableAttribute.DbTableAttribute(string name)
```

**summary:** Declares the exact store table name.

**param name:** A nonempty table identifier; casing is preserved.

**exception:** The name is empty or whitespace.

**inheritdoc:**

**Inherited type policy:** The generator reads source symbols and emits the same TableBuilder calls as manual metadata. It never opens a database, loads a native module, compiles native code or discovers runtime model types.

## `M:Korda.Data.DbUseCodecAttribute.#ctor(System.Type,System.String)`

```csharp
public Korda.Data.DbUseCodecAttribute.DbUseCodecAttribute(System.Type providerType, string memberName)
```

**summary:** Selects an accessible static codec field or getter.

**param providerType:** The accessible declaring type.

**param memberName:** The exact static member name, normally supplied with nameof.

**exception:** The provider type is null.

**exception:** The member name is empty or whitespace.

**inheritdoc:**

**Inherited type policy:** The member must expose ValueCodec<T> for the nonnullable property type; the generator adds the nullable wrapper when needed. The same policy is used by writes, generated readers and Fluent projections.

## `M:Korda.Data.DbVersionAttribute.#ctor`

```csharp
public Korda.Data.DbVersionAttribute.DbVersionAttribute()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Generated metadata sets IsConcurrencyToken. The application supplies expected and changed values; no automatic version increment occurs.

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

**Inherited type policy:** Incomplete prefixes are construction values only; registration or execution requires the complete key.

## `M:Korda.Data.EntityWriter`1.#ctor(System.Object,System.IntPtr)`

```csharp
public Korda.Data.EntityWriter<T>.EntityWriter(object @object, nint method)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The callback runs synchronously during a finite input snapshot. Typed writes copy encoded values into owned buffers before native dispatch; the stack-only writer must not be retained.

## `M:Korda.Data.EntityWriter`1.BeginInvoke(Korda.Data.RowWriter,`0,System.AsyncCallback,System.Object)`

```csharp
public virtual System.IAsyncResult Korda.Data.EntityWriter<T>.BeginInvoke(Korda.Data.RowWriter row, T value, System.AsyncCallback callback, object @object)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The callback runs synchronously during a finite input snapshot. Typed writes copy encoded values into owned buffers before native dispatch; the stack-only writer must not be retained.

## `M:Korda.Data.EntityWriter`1.EndInvoke(System.IAsyncResult)`

```csharp
public virtual void Korda.Data.EntityWriter<T>.EndInvoke(System.IAsyncResult result)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The callback runs synchronously during a finite input snapshot. Typed writes copy encoded values into owned buffers before native dispatch; the stack-only writer must not be retained.

## `M:Korda.Data.EntityWriter`1.Invoke(Korda.Data.RowWriter,`0)`

```csharp
public virtual void Korda.Data.EntityWriter<T>.Invoke(Korda.Data.RowWriter row, T value)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The callback runs synchronously during a finite input snapshot. Typed writes copy encoded values into owned buffers before native dispatch; the stack-only writer must not be retained.

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

**Inherited type policy:** Creation copies the field sequence and hashes canonical metadata. Materializers stay managed; no CLR delegate or function pointer is encoded in metadata. Instances are shareable if their delegates are.

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

**Inherited type policy:** Creation loads no native engine. And methods return new descriptors and never alter either table hash. A complete key prefix is required for native registration; lazy loading and alternate-key inference are excluded.

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

**Inherited type policy:** Creation loads no native engine. And methods return new descriptors and never alter either table hash. A complete key prefix is required for native registration; lazy loading and alternate-key inference are excluded.

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

**Inherited type policy:** Creation loads no native engine. And methods return new descriptors and never alter either table hash. A complete key prefix is required for native registration; lazy loading and alternate-key inference are excluded.

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

**Inherited type policy:** Creation loads no native engine. And methods return new descriptors and never alter either table hash. A complete key prefix is required for native registration; lazy loading and alternate-key inference are excluded.

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

**Inherited type policy:** Creation loads no native engine. And methods return new descriptors and never alter either table hash. A complete key prefix is required for native registration; lazy loading and alternate-key inference are excluded.

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

**Inherited type policy:** Creation loads no native engine. And methods return new descriptors and never alter either table hash. A complete key prefix is required for native registration; lazy loading and alternate-key inference are excluded.

## `M:Korda.Data.ResultColumn`1.#ctor`

```csharp
public Korda.Data.ResultColumn<T>.ResultColumn()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The default token is invalid; no public constructor can forge projection ownership.

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

**Inherited type policy:** Creating a field allocates one managed metadata object with no native resource. It can be retained in a static projection; source-specific codec binding occurs separately at registration.

## `M:Korda.Data.RowMaterializer`1.#ctor(System.Object,System.IntPtr)`

```csharp
public Korda.Data.RowMaterializer<T>.RowMaterializer(object @object, nint method)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The callback runs synchronously while its batch lease is live. Decoding and the application constructor create owned CLR values; borrowed reader state must not escape or cross an await.

## `M:Korda.Data.RowMaterializer`1.BeginInvoke(Korda.Data.RowReader,System.AsyncCallback,System.Object)`

```csharp
public virtual System.IAsyncResult Korda.Data.RowMaterializer<T>.BeginInvoke(Korda.Data.RowReader row, System.AsyncCallback callback, object @object)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The callback runs synchronously while its batch lease is live. Decoding and the application constructor create owned CLR values; borrowed reader state must not escape or cross an await.

## `M:Korda.Data.RowMaterializer`1.EndInvoke(System.IAsyncResult)`

```csharp
public virtual T Korda.Data.RowMaterializer<T>.EndInvoke(System.IAsyncResult result)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The callback runs synchronously while its batch lease is live. Decoding and the application constructor create owned CLR values; borrowed reader state must not escape or cross an await.

## `M:Korda.Data.RowMaterializer`1.Invoke(Korda.Data.RowReader)`

```csharp
public virtual T Korda.Data.RowMaterializer<T>.Invoke(Korda.Data.RowReader row)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The callback runs synchronously while its batch lease is live. Decoding and the application constructor create owned CLR values; borrowed reader state must not escape or cross an await.

## `M:Korda.Data.RowReader.#ctor`

```csharp
public Korda.Data.RowReader.RowReader()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Shape validation precedes NULL interpretation. Every read checks the lease; default readers reject. Values are owned CLR copies. Readers cannot be retained across await or stored in an ordinary heap object.

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

**Inherited type policy:** Shape validation precedes NULL interpretation. Every read checks the lease; default readers reject. Values are owned CLR copies. Readers cannot be retained across await or stored in an ordinary heap object.

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

**Inherited type policy:** Shape validation precedes NULL interpretation. Every read checks the lease; default readers reject. Values are owned CLR copies. Readers cannot be retained across await or stored in an ordinary heap object.

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

**Inherited type policy:** Shape validation precedes NULL interpretation. Every read checks the lease; default readers reject. Values are owned CLR copies. Readers cannot be retained across await or stored in an ordinary heap object.

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

**Inherited type policy:** Shape validation precedes NULL interpretation. Every read checks the lease; default readers reject. Values are owned CLR copies. Readers cannot be retained across await or stored in an ordinary heap object.

## `M:Korda.Data.RowWriter.#ctor`

```csharp
public Korda.Data.RowWriter.RowWriter()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** A writer is valid only during EntityWriter invocation. Repeated assignment rejects. Insert policy omits generated columns. An exception publishes no partial row to a provider.

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

**Inherited type policy:** A writer is valid only during EntityWriter invocation. Repeated assignment rejects. Insert policy omits generated columns. An exception publishes no partial row to a provider.

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

**Inherited type policy:** This type does not parse SQL, choose dialects, prove safety or contact a provider. The native engine transports placeholders and validates actual capability/effect policy before dispatch. Interpolated convenience has boxing costs.

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

**Inherited type policy:** This type does not parse SQL, choose dialects, prove safety or contact a provider. The native engine transports placeholders and validates actual capability/effect policy before dispatch. Interpolated convenience has boxing costs.

## `M:Korda.Data.SqlCommandKind.#ctor`

```csharp
public Korda.Data.SqlCommandKind.SqlCommandKind()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:Korda.Data.SqlParameters.#ctor`

```csharp
public Korda.Data.SqlParameters.SqlParameters()
```

**summary:** Creates an empty ordered parameter collection.

**Inherited type policy:** Names are case-sensitive ASCII without @. Add copies mutable values immediately; command construction snapshots the collection. Provider placeholder scanning and missing/unused-hole checks remain native B02/B03 work.

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

**Inherited type policy:** Names are case-sensitive ASCII without @. Add copies mutable values immediately; command construction snapshots the collection. Provider placeholder scanning and missing/unused-hole checks remain native B02/B03 work.

## `M:Korda.Data.TableBuilder`1.#ctor(System.String,System.String)`

```csharp
public Korda.Data.TableBuilder<TEntity>.TableBuilder(string tableName, string? schema = null)
```

**summary:** Starts a declaration with exact store names.

**param tableName:** A nonempty strict-Unicode table name.

**param schema:** A nonempty explicit schema or null; null is not an empty string.

**exception:** A name or closed type identity is invalid.

**exception:** is null.

**Inherited type policy:** This single-writer startup builder is consumed by successful Build. It performs O(column count) managed work and loads no native engine. The known closed type is used only for its assembly/name identity.

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

**Inherited type policy:** This single-writer startup builder is consumed by successful Build. It performs O(column count) managed work and loads no native engine. The known closed type is used only for its assembly/name identity.

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

**Inherited type policy:** This single-writer startup builder is consumed by successful Build. It performs O(column count) managed work and loads no native engine. The known closed type is used only for its assembly/name identity.

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

**Inherited type policy:** Canonical metadata hashes are business schema identities, independent of ABI and package versions.

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

**Inherited type policy:** Canonical metadata hashes are business schema identities, independent of ABI and package versions.

## `P:Korda.Data.Codecs.Boolean`

```csharp
public static Korda.Data.ValueCodec<bool> Korda.Data.Codecs.Boolean { get; }
```

**summary:** Gets the Boolean codec whose only wire bytes are zero and one.

**Inherited type policy:** Selections are immutable. Encoding snapshots strings, byte arrays and JSON into owned storage. No provider, native loader, reflection serializer or SQL semantics are installed by these factories.

## `P:Korda.Data.Codecs.Bytes`

```csharp
public static Korda.Data.ValueCodec<byte[]> Korda.Data.Codecs.Bytes { get; }
```

**summary:** Gets binary values; input and output arrays are copied at a snapshot boundary.

**Inherited type policy:** Selections are immutable. Encoding snapshots strings, byte arrays and JSON into owned storage. No provider, native loader, reflection serializer or SQL semantics are installed by these factories.

## `P:Korda.Data.Codecs.Date`

```csharp
public static Korda.Data.ValueCodec<System.DateOnly> Korda.Data.Codecs.Date { get; }
```

**summary:** Gets signed days since 1970-01-01 within the DateOnly domain.

**Inherited type policy:** Selections are immutable. Encoding snapshots strings, byte arrays and JSON into owned storage. No provider, native loader, reflection serializer or SQL semantics are installed by these factories.

## `P:Korda.Data.Codecs.DecimalText`

```csharp
public static Korda.Data.ValueCodec<decimal> Korda.Data.Codecs.DecimalText { get; }
```

**summary:** Gets exact decimal text storage; provider arithmetic, range ordering and aggregation are not implied.

**Inherited type policy:** Selections are immutable. Encoding snapshots strings, byte arrays and JSON into owned storage. No provider, native loader, reflection serializer or SQL semantics are installed by these factories.

## `P:Korda.Data.Codecs.Double`

```csharp
public static Korda.Data.ValueCodec<double> Korda.Data.Codecs.Double { get; }
```

**summary:** Gets finite IEEE 754 binary64 values, preserving signed zero in the wire.

**Inherited type policy:** Selections are immutable. Encoding snapshots strings, byte arrays and JSON into owned storage. No provider, native loader, reflection serializer or SQL semantics are installed by these factories.

## `P:Korda.Data.Codecs.Guid`

```csharp
public static Korda.Data.ValueCodec<System.Guid> Korda.Data.Codecs.Guid { get; }
```

**summary:** Gets RFC 4122 network-order UUID encoding.

**Inherited type policy:** Selections are immutable. Encoding snapshots strings, byte arrays and JSON into owned storage. No provider, native loader, reflection serializer or SQL semantics are installed by these factories.

## `P:Korda.Data.Codecs.Int16`

```csharp
public static Korda.Data.ValueCodec<short> Korda.Data.Codecs.Int16 { get; }
```

**summary:** Gets the exact signed 16-bit codec.

**Inherited type policy:** Selections are immutable. Encoding snapshots strings, byte arrays and JSON into owned storage. No provider, native loader, reflection serializer or SQL semantics are installed by these factories.

## `P:Korda.Data.Codecs.Int32`

```csharp
public static Korda.Data.ValueCodec<int> Korda.Data.Codecs.Int32 { get; }
```

**summary:** Gets the exact signed 32-bit codec.

**Inherited type policy:** Selections are immutable. Encoding snapshots strings, byte arrays and JSON into owned storage. No provider, native loader, reflection serializer or SQL semantics are installed by these factories.

## `P:Korda.Data.Codecs.Int64`

```csharp
public static Korda.Data.ValueCodec<long> Korda.Data.Codecs.Int64 { get; }
```

**summary:** Gets the exact signed 64-bit codec.

**Inherited type policy:** Selections are immutable. Encoding snapshots strings, byte arrays and JSON into owned storage. No provider, native loader, reflection serializer or SQL semantics are installed by these factories.

## `P:Korda.Data.Codecs.Single`

```csharp
public static Korda.Data.ValueCodec<float> Korda.Data.Codecs.Single { get; }
```

**summary:** Gets finite IEEE 754 binary32 values, preserving signed zero in the wire.

**Inherited type policy:** Selections are immutable. Encoding snapshots strings, byte arrays and JSON into owned storage. No provider, native loader, reflection serializer or SQL semantics are installed by these factories.

## `P:Korda.Data.Codecs.String`

```csharp
public static Korda.Data.ValueCodec<string> Korda.Data.Codecs.String { get; }
```

**summary:** Gets strict UTF-8 text with no normalization or implicit SQL collation.

**Inherited type policy:** Selections are immutable. Encoding snapshots strings, byte arrays and JSON into owned storage. No provider, native loader, reflection serializer or SQL semantics are installed by these factories.

## `P:Korda.Data.Codecs.Time`

```csharp
public static Korda.Data.ValueCodec<System.TimeOnly> Korda.Data.Codecs.Time { get; }
```

**summary:** Gets microseconds since midnight; submicrosecond precision rejects.

**Inherited type policy:** Selections are immutable. Encoding snapshots strings, byte arrays and JSON into owned storage. No provider, native loader, reflection serializer or SQL semantics are installed by these factories.

## `P:Korda.Data.Codecs.UtcDateTime`

```csharp
public static Korda.Data.ValueCodec<System.DateTime> Korda.Data.Codecs.UtcDateTime { get; }
```

**summary:** Gets UTC-only DateTime values; Local and Unspecified values reject.

**Inherited type policy:** Selections are immutable. Encoding snapshots strings, byte arrays and JSON into owned storage. No provider, native loader, reflection serializer or SQL semantics are installed by these factories.

## `P:Korda.Data.Codecs.UtcInstant`

```csharp
public static Korda.Data.ValueCodec<System.DateTimeOffset> Korda.Data.Codecs.UtcInstant { get; }
```

**summary:** Gets UTC-normalized instants in Unix microseconds; no precision loss is allowed.

**Inherited type policy:** Selections are immutable. Encoding snapshots strings, byte arrays and JSON into owned storage. No provider, native loader, reflection serializer or SQL semantics are installed by these factories.

## `P:Korda.Data.ColumnOptions.Collation`

```csharp
public string? Korda.Data.ColumnOptions.Collation { get; init; }
```

**summary:** Gets the exact provider collation name, or no explicit collation.

**Inherited type policy:** Null optional settings remain distinct from defaults. MaxLength is a positive byte capacity. The builder snapshots this record and validates key nullability before publishing a table.

## `P:Korda.Data.ColumnOptions.IsConcurrencyToken`

```csharp
public bool Korda.Data.ColumnOptions.IsConcurrencyToken { get; init; }
```

**summary:** Gets whether the column is an explicit optimistic-concurrency token.

**Inherited type policy:** Null optional settings remain distinct from defaults. MaxLength is a positive byte capacity. The builder snapshots this record and validates key nullability before publishing a table.

## `P:Korda.Data.ColumnOptions.IsGenerated`

```csharp
public bool Korda.Data.ColumnOptions.IsGenerated { get; init; }
```

**summary:** Gets whether the database generates the value on insert.

**Inherited type policy:** Null optional settings remain distinct from defaults. MaxLength is a positive byte capacity. The builder snapshots this record and validates key nullability before publishing a table.

## `P:Korda.Data.ColumnOptions.IsPrimaryKey`

```csharp
public bool Korda.Data.ColumnOptions.IsPrimaryKey { get; init; }
```

**summary:** Gets whether the column participates in the primary key, ordered by declaration.

**Inherited type policy:** Null optional settings remain distinct from defaults. MaxLength is a positive byte capacity. The builder snapshots this record and validates key nullability before publishing a table.

## `P:Korda.Data.ColumnOptions.MaxLength`

```csharp
public int? Korda.Data.ColumnOptions.MaxLength { get; init; }
```

**summary:** Gets the positive maximum byte capacity, or no declared capacity.

**Inherited type policy:** Null optional settings remain distinct from defaults. MaxLength is a positive byte capacity. The builder snapshots this record and validates key nullability before publishing a table.

## `P:Korda.Data.Column`2.Codec`

```csharp
public Korda.Data.ValueCodec<T> Korda.Data.Column<TEntity, T>.Codec { get; }
```

**summary:** Gets the exact logical codec and its configuration.

**Inherited type policy:** Tokens expose no native pointer. Equivalent metadata in another builder does not authorize using its token.

## `P:Korda.Data.Column`2.Id`

```csharp
public uint Korda.Data.Column<TEntity, T>.Id { get; }
```

**summary:** Gets the stable nonzero little-endian SHA-256-derived column ID.

**Inherited type policy:** Tokens expose no native pointer. Equivalent metadata in another builder does not authorize using its token.

## `P:Korda.Data.Column`2.Name`

```csharp
public string Korda.Data.Column<TEntity, T>.Name { get; }
```

**summary:** Gets the exact store name, preserving case and Unicode.

**Inherited type policy:** Tokens expose no native pointer. Equivalent metadata in another builder does not authorize using its token.

## `P:Korda.Data.ConcurrencyConflictException.ActualRows`

```csharp
public long Korda.Data.ConcurrencyConflictException.ActualRows { get; }
```

**summary:** Gets the actual affected-row count.

**Inherited type policy:** The original key and expected token predicate ran once in the database. This exception never authorizes a write replay.

## `P:Korda.Data.ConcurrencyConflictException.ExpectedRows`

```csharp
public long Korda.Data.ConcurrencyConflictException.ExpectedRows { get; }
```

**summary:** Gets the expected affected-row count.

**Inherited type policy:** The original key and expected token predicate ran once in the database. This exception never authorizes a write replay.

## `P:Korda.Data.DatabaseCapabilityException.Capability`

```csharp
public string Korda.Data.DatabaseCapabilityException.Capability { get; }
```

**summary:** Gets the exact required capability name.

**Inherited type policy:** The owned exception retains validated managed identity strings and no native handle. Constructing or reading it performs no provider call; capability admission rejects before business dispatch.

## `P:Korda.Data.DatabaseCapabilityException.Provider`

```csharp
public string Korda.Data.DatabaseCapabilityException.Provider { get; }
```

**summary:** Gets the provider identity.

**Inherited type policy:** The owned exception retains validated managed identity strings and no native handle. Constructing or reading it performs no provider call; capability admission rejects before business dispatch.

## `P:Korda.Data.DatabaseException.CorrelationId`

```csharp
public ulong Korda.Data.DatabaseException.CorrelationId { get; }
```

**summary:** Gets the native correlation identity, or zero when absent.

**Inherited type policy:** Diagnostic values are immutable and remain readable after cleanup. Construction loads no native engine. A transient cause is not permission to retry a write; outcome and application idempotency remain separate.

## `P:Korda.Data.DatabaseException.IsTransient`

```csharp
public bool Korda.Data.DatabaseException.IsTransient { get; }
```

**summary:** Gets the provider's explicit classification; unknown outcomes are always nontransient.

**Inherited type policy:** Diagnostic values are immutable and remain readable after cleanup. Construction loads no native engine. A transient cause is not permission to retry a write; outcome and application idempotency remain separate.

## `P:Korda.Data.DatabaseException.Kind`

```csharp
public Korda.Data.DatabaseErrorKind Korda.Data.DatabaseException.Kind { get; }
```

**summary:** Gets the immutable provider-neutral category.

**Inherited type policy:** Diagnostic values are immutable and remain readable after cleanup. Construction loads no native engine. A transient cause is not permission to retry a write; outcome and application idempotency remain separate.

## `P:Korda.Data.DatabaseException.ProviderCode`

```csharp
public string? Korda.Data.DatabaseException.ProviderCode { get; }
```

**summary:** Gets the optional provider code without interpreting it as retry authorization.

**Inherited type policy:** Diagnostic values are immutable and remain readable after cleanup. Construction loads no native engine. A transient cause is not permission to retry a write; outcome and application idempotency remain separate.

## `P:Korda.Data.DbColumnAttribute.Generated`

```csharp
public bool Korda.Data.DbColumnAttribute.Generated { get; init; }
```

**summary:** Gets or initializes whether the database supplies the value on insertion.

**inheritdoc:**

**Inherited type policy:** Without this attribute the CLR property symbol name is used unchanged. The generated column member keeps its CLR spelling.

## `P:Korda.Data.DbProjectionAttribute.DescriptorName`

```csharp
public string? Korda.Data.DbProjectionAttribute.DescriptorName { get; init; }
```

**summary:** Gets or initializes the generated descriptor class name; default is DtoNameMapping.

**inheritdoc:**

**Inherited type policy:** The attributed ordinary class or record is an owned DTO, not a table. Its descriptor exposes Row for parameterized SQL and Bind for exactly selected Fluent expressions. Explicit column attributes supply result aliases; no reflection or native recompilation is required.

## `P:Korda.Data.DbTableAttribute.DescriptorName`

```csharp
public string? Korda.Data.DbTableAttribute.DescriptorName { get; init; }
```

**summary:** Gets or initializes the generated static descriptor class name; default is EntityNameMapping.

**inheritdoc:**

**Inherited type policy:** The generator reads source symbols and emits the same TableBuilder calls as manual metadata. It never opens a database, loads a native module, compiles native code or discovers runtime model types.

## `P:Korda.Data.DbTableAttribute.InferPrimaryKey`

```csharp
public bool Korda.Data.DbTableAttribute.InferPrimaryKey { get; init; }
```

**summary:** Gets or initializes whether a unique Id property supplies the key when no explicit key is declared.

**remarks:** The parameterless constructor enables this convention. The existing named-table constructor keeps it disabled by default. A nullable or ambiguous inferred key is a compilation error.

**Inherited type policy:** The generator reads source symbols and emits the same TableBuilder calls as manual metadata. It never opens a database, loads a native module, compiles native code or discovers runtime model types.

## `P:Korda.Data.DbTableAttribute.Schema`

```csharp
public string? Korda.Data.DbTableAttribute.Schema { get; init; }
```

**summary:** Gets or initializes the optional exact schema name.

**inheritdoc:**

**Inherited type policy:** The generator reads source symbols and emits the same TableBuilder calls as manual metadata. It never opens a database, loads a native module, compiles native code or discovers runtime model types.

## `P:Korda.Data.Projection`1.ShapeHash`

```csharp
public string Korda.Data.Projection<T>.ShapeHash { get; }
```

**summary:** Gets lowercase SHA-256 of the exact logical shape, excluding managed delegate and native IDs.

**Inherited type policy:** Creation copies the field sequence and hashes canonical metadata. Materializers stay managed; no CLR delegate or function pointer is encoded in metadata. Instances are shareable if their delegates are.

## `P:Korda.Data.ResultColumn`1.Name`

```csharp
public string Korda.Data.ResultColumn<T>.Name { get; }
```

**summary:** Gets the exact result alias; the default token rejects.

**exception:** This token is default.

**Inherited type policy:** The default token is invalid; no public constructor can forge projection ownership.

## `P:Korda.Data.ResultColumn`1.Ordinal`

```csharp
public int Korda.Data.ResultColumn<T>.Ordinal { get; }
```

**summary:** Gets the zero-based ordinal; the default token rejects.

**exception:** This token is default.

**Inherited type policy:** The default token is invalid; no public constructor can forge projection ownership.

## `P:Korda.Data.ResultField.CodecId`

```csharp
public uint Korda.Data.ResultField.CodecId { get; }
```

**summary:** Gets the logical codec ID, independent of source-bound configured IDs.

**Inherited type policy:** Creating a field allocates one managed metadata object with no native resource. It can be retained in a static projection; source-specific codec binding occurs separately at registration.

## `P:Korda.Data.ResultField.IsNullable`

```csharp
public bool Korda.Data.ResultField.IsNullable { get; }
```

**summary:** Gets whether the typed codec permits SQL NULL.

**Inherited type policy:** Creating a field allocates one managed metadata object with no native resource. It can be retained in a static projection; source-specific codec binding occurs separately at registration.

## `P:Korda.Data.ResultField.Name`

```csharp
public string Korda.Data.ResultField.Name { get; }
```

**summary:** Gets the exact result alias.

**Inherited type policy:** Creating a field allocates one managed metadata object with no native resource. It can be retained in a static projection; source-specific codec binding occurs separately at registration.

## `P:Korda.Data.Table`1.MetadataHash`

```csharp
public string Korda.Data.Table<TEntity>.MetadataHash { get; }
```

**summary:** Gets lowercase hexadecimal SHA-256 of the canonical model bytes.

**Inherited type policy:** Canonical metadata hashes are business schema identities, independent of ABI and package versions.

## `P:Korda.Data.Table`1.Name`

```csharp
public string Korda.Data.Table<TEntity>.Name { get; }
```

**summary:** Gets the exact table name.

**Inherited type policy:** Canonical metadata hashes are business schema identities, independent of ABI and package versions.

## `P:Korda.Data.Table`1.Row`

```csharp
public Korda.Data.Projection<TEntity> Korda.Data.Table<TEntity>.Row { get; }
```

**summary:** Gets the immutable projection in declared constructor order.

**Inherited type policy:** Canonical metadata hashes are business schema identities, independent of ABI and package versions.

## `P:Korda.Data.Table`1.Schema`

```csharp
public string? Korda.Data.Table<TEntity>.Schema { get; }
```

**summary:** Gets the explicit schema or null.

**Inherited type policy:** Canonical metadata hashes are business schema identities, independent of ABI and package versions.

## `P:Korda.Data.ValueCodec`1.Id`

```csharp
public uint Korda.Data.ValueCodec<T>.Id { get; }
```

**summary:** Gets the logical db-wire scalar ID, independent of source-bound configured IDs.

**Inherited type policy:** Instances are immutable and shareable. IDs are logical metadata, not source-owned native handles. Nullable wrappers preserve the logical ID. Configured decimal identities include precision and scale. No native runtime is loaded; provider conversions and capability enforcement belong to B07.

## `P:Korda.Data.ValueCodec`1.Name`

```csharp
public string Korda.Data.ValueCodec<T>.Name { get; }
```

**summary:** Gets the descriptive scalar codec name.

**Inherited type policy:** Instances are immutable and shareable. IDs are logical metadata, not source-owned native handles. Nullable wrappers preserve the logical ID. Configured decimal identities include precision and scale. No native runtime is loaded; provider conversions and capability enforcement belong to B07.

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

## `T:Korda.Data.DbCodecAttribute`

```csharp
[System.AttributeUsageAttribute(System.AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class Korda.Data.DbCodecAttribute : System.Attribute
```

**summary:** Declares one compilation-wide default codec for a closed non-null CLR value type.

**remarks:** The generator reads an accessible static field or getter of exactly ValueCodec<T> from the supplied provider type. The value is captured when each generated descriptor initializes. Nullable properties wrap this base policy. Duplicate defaults and incompatible members fail compilation; no runtime type registry is created.

## `T:Korda.Data.DbColumnAttribute`

```csharp
[System.AttributeUsageAttribute(System.AttributeTargets.Property, Inherited = false)]
public sealed class Korda.Data.DbColumnAttribute : System.Attribute
```

**summary:** Overrides a property's exact store name and generated-value policy.

**remarks:** Without this attribute the CLR property symbol name is used unchanged. The generated column member keeps its CLR spelling.

## `T:Korda.Data.DbConstructorAttribute`

```csharp
[System.AttributeUsageAttribute(System.AttributeTargets.Constructor, Inherited = false)]
public sealed class Korda.Data.DbConstructorAttribute : System.Attribute
```

**summary:** Selects the accessible constructor used by generated table and DTO readers.

**remarks:** Exactly one constructor may be selected. Every parameter must map to one compatible property and remaining properties must have accessible setters or init accessors. Without this attribute the existing unique-constructor rule applies.

## `T:Korda.Data.DbIgnoreAttribute`

```csharp
[System.AttributeUsageAttribute(System.AttributeTargets.Property, Inherited = false)]
public sealed class Korda.Data.DbIgnoreAttribute : System.Attribute
```

**summary:** Excludes a property from generated metadata and row serialization.

**remarks:** Combining Ignore with another mapping annotation is inconsistent and rejected. Required constructor inputs still need a supported static mapping.

## `T:Korda.Data.DbKeyAttribute`

```csharp
[System.AttributeUsageAttribute(System.AttributeTargets.Property, Inherited = false)]
public sealed class Korda.Data.DbKeyAttribute : System.Attribute
```

**summary:** Marks a non-null primary-key property in deterministic projection order.

**remarks:** Nullable keys are rejected at compilation and again by the static descriptor builder. Explicit keys take precedence over the opt-in Id convention.

## `T:Korda.Data.DbParameterAttribute`

```csharp
[System.AttributeUsageAttribute(System.AttributeTargets.Parameter, Inherited = false)]
public sealed class Korda.Data.DbParameterAttribute : System.Attribute
```

**summary:** Associates a generated reader's constructor parameter with an exact CLR property name.

**remarks:** Use nameof(Property) when a constructor parameter has a different name. This does not rename the store column or result alias. Duplicate, missing and type-incompatible associations fail compilation.

## `T:Korda.Data.DbProjectionAttribute`

```csharp
[System.AttributeUsageAttribute(System.AttributeTargets.Class, Inherited = false)]
public sealed class Korda.Data.DbProjectionAttribute : System.Attribute
```

**summary:** Generates a reusable static SQL row projection and a typed Fluent expression binder.

**remarks:** The attributed ordinary class or record is an owned DTO, not a table. Its descriptor exposes Row for parameterized SQL and Bind for exactly selected Fluent expressions. Explicit column attributes supply result aliases; no reflection or native recompilation is required.

## `T:Korda.Data.DbTableAttribute`

```csharp
[System.AttributeUsageAttribute(System.AttributeTargets.Class, Inherited = false)]
public sealed class Korda.Data.DbTableAttribute : System.Attribute
```

**summary:** Opts a model into deterministic, static table and DTO mapping generation.

**remarks:** The generator reads source symbols and emits the same TableBuilder calls as manual metadata. It never opens a database, loads a native module, compiles native code or discovers runtime model types.

## `T:Korda.Data.DbUseCodecAttribute`

```csharp
[System.AttributeUsageAttribute(System.AttributeTargets.Property, Inherited = false)]
public sealed class Korda.Data.DbUseCodecAttribute : System.Attribute
```

**summary:** Overrides a property's automatic or compilation-wide codec with one exact static policy.

**remarks:** The member must expose ValueCodec<T> for the nonnullable property type; the generator adds the nullable wrapper when needed. The same policy is used by writes, generated readers and Fluent projections.

## `T:Korda.Data.DbVersionAttribute`

```csharp
[System.AttributeUsageAttribute(System.AttributeTargets.Property, Inherited = false)]
public sealed class Korda.Data.DbVersionAttribute : System.Attribute
```

**summary:** Marks an explicit optimistic-concurrency token.

**remarks:** Generated metadata sets IsConcurrencyToken. The application supplies expected and changed values; no automatic version increment occurs.

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
