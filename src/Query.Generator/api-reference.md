# Model generation API reference

Generated from the actual compiled public API and XML; compared against the exact pinned design declarations. B10 owns these source attributes and static generation; B01/B09 descriptor APIs retain their existing ownership.

## `M:NativeWeave.Data.WeaveColumnAttribute.#ctor(System.String)`

```csharp
public NativeWeave.Data.WeaveColumnAttribute.WeaveColumnAttribute(System.String name)
```

Declares the exact store column name.

Without this attribute the CLR property symbol name is used unchanged. The generated column member keeps its CLR spelling.

- **name**: A nonempty store identifier.
- **T:System.ArgumentException**: The name is empty or whitespace.

## `M:NativeWeave.Data.WeaveTableAttribute.#ctor(System.String)`

```csharp
public NativeWeave.Data.WeaveTableAttribute.WeaveTableAttribute(System.String name)
```

Declares the exact store table name.

The generator reads source symbols and emits the same TableBuilder calls as manual metadata. It never opens a database, loads a native module, compiles native code or discovers runtime model types.

- **name**: A nonempty table identifier; casing is preserved.
- **T:System.ArgumentException**: The name is empty or whitespace.

## `P:NativeWeave.Data.WeaveColumnAttribute.Generated`

```csharp
public System.Boolean NativeWeave.Data.WeaveColumnAttribute.Generated | getter=Public;setter=Public;init=True
```

Gets or initializes whether the database supplies the value on insertion.

Without this attribute the CLR property symbol name is used unchanged. The generated column member keeps its CLR spelling.


## `P:NativeWeave.Data.WeaveTableAttribute.DescriptorName`

```csharp
public System.String? NativeWeave.Data.WeaveTableAttribute.DescriptorName | getter=Public;setter=Public;init=True
```

Gets or initializes the generated static descriptor class name; default is EntityNameMapping.

The generator reads source symbols and emits the same TableBuilder calls as manual metadata. It never opens a database, loads a native module, compiles native code or discovers runtime model types.


## `P:NativeWeave.Data.WeaveTableAttribute.Schema`

```csharp
public System.String? NativeWeave.Data.WeaveTableAttribute.Schema | getter=Public;setter=Public;init=True
```

Gets or initializes the optional exact schema name.

The generator reads source symbols and emits the same TableBuilder calls as manual metadata. It never opens a database, loads a native module, compiles native code or discovers runtime model types.


## `T:NativeWeave.Data.WeaveColumnAttribute`

```csharp
NativeWeave.Data.WeaveColumnAttribute | kind=Class;static=False;sealed=True;abstract=False;base=System.Attribute;interfaces=
```

Overrides a property's exact store name and generated-value policy.

Without this attribute the CLR property symbol name is used unchanged. The generated column member keeps its CLR spelling.


## `T:NativeWeave.Data.WeaveIgnoreAttribute`

```csharp
NativeWeave.Data.WeaveIgnoreAttribute | kind=Class;static=False;sealed=True;abstract=False;base=System.Attribute;interfaces=
```

Excludes a property from generated metadata and row serialization.

Combining Ignore with another mapping annotation is inconsistent and rejected. Required constructor inputs still need a supported static mapping.


## `T:NativeWeave.Data.WeaveKeyAttribute`

```csharp
NativeWeave.Data.WeaveKeyAttribute | kind=Class;static=False;sealed=True;abstract=False;base=System.Attribute;interfaces=
```

Marks a non-null primary-key property in deterministic projection order.

Nullable keys are rejected at compilation and again by the static descriptor builder; there is no inferred key convention.


## `T:NativeWeave.Data.WeaveTableAttribute`

```csharp
NativeWeave.Data.WeaveTableAttribute | kind=Class;static=False;sealed=True;abstract=False;base=System.Attribute;interfaces=
```

Opts a model into deterministic, static table and DTO mapping generation.

The generator reads source symbols and emits the same TableBuilder calls as manual metadata. It never opens a database, loads a native module, compiles native code or discovers runtime model types.


## `T:NativeWeave.Data.WeaveVersionAttribute`

```csharp
NativeWeave.Data.WeaveVersionAttribute | kind=Class;static=False;sealed=True;abstract=False;base=System.Attribute;interfaces=
```

Marks an explicit optimistic-concurrency token.

Generated metadata sets IsConcurrencyToken. The application supplies expected and changed values; no automatic version increment occurs.


