# Model generation API reference

Generated from all ten compiled public database attributes and their XML comments. The B10 declarations retain the exact pinned Design contract with the explicit DB-Rename-01 name mapping; DB-API-01 additions are checked by the shared compiled-assembly gate. B01/B09 descriptor APIs retain their existing ownership.

## `M:Korda.Data.DbCodecAttribute.#ctor(System.Type,System.Type,System.String)`

```csharp
public Korda.Data.DbCodecAttribute.DbCodecAttribute(System.Type valueType, System.Type providerType, System.String memberName)
```

Declares a reusable static codec policy.

The generator reads an accessible static field or getter of exactly ValueCodec<T> from the supplied provider type. The value is captured when each generated descriptor initializes. Nullable properties wrap this base policy. Duplicate defaults and incompatible members fail compilation; no runtime type registry is created.

- **valueType**: The closed, nonnullable CLR type whose properties use this default.
- **providerType**: The accessible type declaring the static codec field or getter.
- **memberName**: The exact accessible static member name, normally supplied with nameof.
- **T:System.ArgumentNullException**: A type is null.
- **T:System.ArgumentException**: The member name is empty or whitespace.

## `M:Korda.Data.DbColumnAttribute.#ctor(System.String)`

```csharp
public Korda.Data.DbColumnAttribute.DbColumnAttribute(System.String name)
```

Declares the exact store column name.

Without this attribute the CLR property symbol name is used unchanged. The generated column member keeps its CLR spelling.

- **name**: A nonempty store identifier.
- **T:System.ArgumentException**: The name is empty or whitespace.

## `M:Korda.Data.DbParameterAttribute.#ctor(System.String)`

```csharp
public Korda.Data.DbParameterAttribute.DbParameterAttribute(System.String propertyName)
```

Declares the exact CLR property supplying this parameter.

Use nameof(Property) when a constructor parameter has a different name. This does not rename the store column or result alias. Duplicate, missing and type-incompatible associations fail compilation.

- **propertyName**: A nonempty, case-sensitive CLR property name.
- **T:System.ArgumentException**: The name is empty or whitespace.

## `M:Korda.Data.DbTableAttribute.#ctor`

```csharp
public Korda.Data.DbTableAttribute.DbTableAttribute()
```

Uses the exact CLR type name and infers a unique non-null Id primary key.

There is no pluralization or casing conversion. Explicit keys take precedence; set InferPrimaryKey to false to declare a keyless model.


## `M:Korda.Data.DbTableAttribute.#ctor(System.String)`

```csharp
public Korda.Data.DbTableAttribute.DbTableAttribute(System.String name)
```

Declares the exact store table name.

The generator reads source symbols and emits the same TableBuilder calls as manual metadata. It never opens a database, loads a native module, compiles native code or discovers runtime model types.

- **name**: A nonempty table identifier; casing is preserved.
- **T:System.ArgumentException**: The name is empty or whitespace.

## `M:Korda.Data.DbUseCodecAttribute.#ctor(System.Type,System.String)`

```csharp
public Korda.Data.DbUseCodecAttribute.DbUseCodecAttribute(System.Type providerType, System.String memberName)
```

Selects an accessible static codec field or getter.

The member must expose ValueCodec<T> for the nonnullable property type; the generator adds the nullable wrapper when needed. The same policy is used by writes, generated readers and Fluent projections.

- **providerType**: The accessible declaring type.
- **memberName**: The exact static member name, normally supplied with nameof.
- **T:System.ArgumentNullException**: The provider type is null.
- **T:System.ArgumentException**: The member name is empty or whitespace.

## `P:Korda.Data.DbColumnAttribute.Generated`

```csharp
public System.Boolean Korda.Data.DbColumnAttribute.Generated | getter=Public;setter=Public;init=True
```

Gets or initializes whether the database supplies the value on insertion.

Without this attribute the CLR property symbol name is used unchanged. The generated column member keeps its CLR spelling.


## `P:Korda.Data.DbProjectionAttribute.DescriptorName`

```csharp
public System.String? Korda.Data.DbProjectionAttribute.DescriptorName | getter=Public;setter=Public;init=True
```

Gets or initializes the generated descriptor class name; default is DtoNameMapping.

The attributed ordinary class or record is an owned DTO, not a table. Its descriptor exposes Row for parameterized SQL and Bind for exactly selected Fluent expressions. Explicit column attributes supply result aliases; no reflection or native recompilation is required.


## `P:Korda.Data.DbTableAttribute.DescriptorName`

```csharp
public System.String? Korda.Data.DbTableAttribute.DescriptorName | getter=Public;setter=Public;init=True
```

Gets or initializes the generated static descriptor class name; default is EntityNameMapping.

The generator reads source symbols and emits the same TableBuilder calls as manual metadata. It never opens a database, loads a native module, compiles native code or discovers runtime model types.


## `P:Korda.Data.DbTableAttribute.InferPrimaryKey`

```csharp
public System.Boolean Korda.Data.DbTableAttribute.InferPrimaryKey | getter=Public;setter=Public;init=True
```

Gets or initializes whether a unique Id property supplies the key when no explicit key is declared.

The parameterless constructor enables this convention. The existing named-table constructor keeps it disabled by default. A nullable or ambiguous inferred key is a compilation error.


## `P:Korda.Data.DbTableAttribute.Schema`

```csharp
public System.String? Korda.Data.DbTableAttribute.Schema | getter=Public;setter=Public;init=True
```

Gets or initializes the optional exact schema name.

The generator reads source symbols and emits the same TableBuilder calls as manual metadata. It never opens a database, loads a native module, compiles native code or discovers runtime model types.


## `T:Korda.Data.DbCodecAttribute`

```csharp
Korda.Data.DbCodecAttribute | kind=Class;static=False;sealed=True;abstract=False;base=System.Attribute;interfaces=
```

Declares one compilation-wide default codec for a closed non-null CLR value type.

The generator reads an accessible static field or getter of exactly ValueCodec<T> from the supplied provider type. The value is captured when each generated descriptor initializes. Nullable properties wrap this base policy. Duplicate defaults and incompatible members fail compilation; no runtime type registry is created.


## `T:Korda.Data.DbColumnAttribute`

```csharp
Korda.Data.DbColumnAttribute | kind=Class;static=False;sealed=True;abstract=False;base=System.Attribute;interfaces=
```

Overrides a property's exact store name and generated-value policy.

Without this attribute the CLR property symbol name is used unchanged. The generated column member keeps its CLR spelling.


## `T:Korda.Data.DbConstructorAttribute`

```csharp
Korda.Data.DbConstructorAttribute | kind=Class;static=False;sealed=True;abstract=False;base=System.Attribute;interfaces=
```

Selects the accessible constructor used by generated table and DTO readers.

Exactly one constructor may be selected. Every parameter must map to one compatible property and remaining properties must have accessible setters or init accessors. Without this attribute the existing unique-constructor rule applies.


## `T:Korda.Data.DbIgnoreAttribute`

```csharp
Korda.Data.DbIgnoreAttribute | kind=Class;static=False;sealed=True;abstract=False;base=System.Attribute;interfaces=
```

Excludes a property from generated metadata and row serialization.

Combining Ignore with another mapping annotation is inconsistent and rejected. Required constructor inputs still need a supported static mapping.


## `T:Korda.Data.DbKeyAttribute`

```csharp
Korda.Data.DbKeyAttribute | kind=Class;static=False;sealed=True;abstract=False;base=System.Attribute;interfaces=
```

Marks a non-null primary-key property in deterministic projection order.

Nullable keys are rejected at compilation and again by the static descriptor builder. Explicit keys take precedence over the opt-in Id convention.


## `T:Korda.Data.DbParameterAttribute`

```csharp
Korda.Data.DbParameterAttribute | kind=Class;static=False;sealed=True;abstract=False;base=System.Attribute;interfaces=
```

Associates a generated reader's constructor parameter with an exact CLR property name.

Use nameof(Property) when a constructor parameter has a different name. This does not rename the store column or result alias. Duplicate, missing and type-incompatible associations fail compilation.


## `T:Korda.Data.DbProjectionAttribute`

```csharp
Korda.Data.DbProjectionAttribute | kind=Class;static=False;sealed=True;abstract=False;base=System.Attribute;interfaces=
```

Generates a reusable static SQL row projection and a typed Fluent expression binder.

The attributed ordinary class or record is an owned DTO, not a table. Its descriptor exposes Row for parameterized SQL and Bind for exactly selected Fluent expressions. Explicit column attributes supply result aliases; no reflection or native recompilation is required.


## `T:Korda.Data.DbTableAttribute`

```csharp
Korda.Data.DbTableAttribute | kind=Class;static=False;sealed=True;abstract=False;base=System.Attribute;interfaces=
```

Opts a model into deterministic, static table and DTO mapping generation.

The generator reads source symbols and emits the same TableBuilder calls as manual metadata. It never opens a database, loads a native module, compiles native code or discovers runtime model types.


## `T:Korda.Data.DbUseCodecAttribute`

```csharp
Korda.Data.DbUseCodecAttribute | kind=Class;static=False;sealed=True;abstract=False;base=System.Attribute;interfaces=
```

Overrides a property's automatic or compilation-wide codec with one exact static policy.

The member must expose ValueCodec<T> for the nonnullable property type; the generator adds the nullable wrapper when needed. The same policy is used by writes, generated readers and Fluent projections.


## `T:Korda.Data.DbVersionAttribute`

```csharp
Korda.Data.DbVersionAttribute | kind=Class;static=False;sealed=True;abstract=False;base=System.Attribute;interfaces=
```

Marks an explicit optimistic-concurrency token.

Generated metadata sets IsConcurrencyToken. The application supplies expected and changed values; no automatic version increment occurs.


