namespace Korda.Data;

/// <summary>Opts a model into deterministic, static table and DTO mapping generation.</summary>
/// <remarks>The generator reads source symbols and emits the same TableBuilder calls as manual metadata.
/// It never opens a database, loads a native module, compiles native code or discovers runtime model types.</remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DbTableAttribute : Attribute
{
    /// <summary>Uses the exact CLR type name and infers a unique non-null Id primary key.</summary>
    /// <remarks>There is no pluralization or casing conversion. Explicit keys take precedence; set InferPrimaryKey to false to declare a keyless model.</remarks>
    public DbTableAttribute() { InferPrimaryKey = true; }
    /// <summary>Declares the exact store table name.</summary>
    /// <param name="name">A nonempty table identifier; casing is preserved.</param>
    /// <exception cref="ArgumentException">The name is empty or whitespace.</exception>
    /// <inheritdoc cref="DbTableAttribute" path="/member/remarks" />
    public DbTableAttribute(string name) { ArgumentException.ThrowIfNullOrWhiteSpace(name); }
    /// <summary>Gets or initializes the optional exact schema name.</summary>
    /// <inheritdoc cref="DbTableAttribute" path="/member/remarks" />
    public string? Schema { get; init; }
    /// <summary>Gets or initializes the generated static descriptor class name; default is EntityNameMapping.</summary>
    /// <inheritdoc cref="DbTableAttribute" path="/member/remarks" />
    public string? DescriptorName { get; init; }
    /// <summary>Gets or initializes whether a unique Id property supplies the key when no explicit key is declared.</summary>
    /// <remarks>The parameterless constructor enables this convention. The existing named-table constructor keeps it disabled by default. A nullable or ambiguous inferred key is a compilation error.</remarks>
    public bool InferPrimaryKey { get; init; }
}
/// <summary>Overrides a property's exact store name and generated-value policy.</summary>
/// <remarks>Without this attribute the CLR property symbol name is used unchanged. The generated column member keeps its CLR spelling.</remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class DbColumnAttribute : Attribute
{
    /// <summary>Declares the exact store column name.</summary>
    /// <param name="name">A nonempty store identifier.</param>
    /// <exception cref="ArgumentException">The name is empty or whitespace.</exception>
    /// <inheritdoc cref="DbColumnAttribute" path="/member/remarks" />
    public DbColumnAttribute(string name) { ArgumentException.ThrowIfNullOrWhiteSpace(name); }
    /// <summary>Gets or initializes whether the database supplies the value on insertion.</summary>
    /// <inheritdoc cref="DbColumnAttribute" path="/member/remarks" />
    public bool Generated { get; init; }
}
/// <summary>Marks a non-null primary-key property in deterministic projection order.</summary>
/// <remarks>Nullable keys are rejected at compilation and again by the static descriptor builder. Explicit keys take precedence over the opt-in Id convention.</remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class DbKeyAttribute : Attribute { }
/// <summary>Marks an explicit optimistic-concurrency token.</summary>
/// <remarks>Generated metadata sets IsConcurrencyToken. The application supplies expected and changed values; no automatic version increment occurs.</remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class DbVersionAttribute : Attribute { }
/// <summary>Excludes a property from generated metadata and row serialization.</summary>
/// <remarks>Combining Ignore with another mapping annotation is inconsistent and rejected. Required constructor inputs still need a supported static mapping.</remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class DbIgnoreAttribute : Attribute { }

/// <summary>Generates a reusable static SQL row projection and a typed Fluent expression binder.</summary>
/// <remarks>The attributed ordinary class or record is an owned DTO, not a table. Its descriptor exposes Row for parameterized SQL and Bind for exactly selected Fluent expressions. Explicit column attributes supply result aliases; no reflection or native recompilation is required.</remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DbProjectionAttribute : Attribute
{
    /// <summary>Gets or initializes the generated descriptor class name; default is DtoNameMapping.</summary>
    /// <inheritdoc cref="DbProjectionAttribute" path="/member/remarks" />
    public string? DescriptorName { get; init; }
}

/// <summary>Selects the accessible constructor used by generated table and DTO readers.</summary>
/// <remarks>Exactly one constructor may be selected. Every parameter must map to one compatible property and remaining properties must have accessible setters or init accessors. Without this attribute the existing unique-constructor rule applies.</remarks>
[AttributeUsage(AttributeTargets.Constructor, Inherited = false)]
public sealed class DbConstructorAttribute : Attribute { }

/// <summary>Associates a generated reader's constructor parameter with an exact CLR property name.</summary>
/// <remarks>Use nameof(Property) when a constructor parameter has a different name. This does not rename the store column or result alias. Duplicate, missing and type-incompatible associations fail compilation.</remarks>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
public sealed class DbParameterAttribute : Attribute
{
    /// <summary>Declares the exact CLR property supplying this parameter.</summary>
    /// <param name="propertyName">A nonempty, case-sensitive CLR property name.</param>
    /// <exception cref="ArgumentException">The name is empty or whitespace.</exception>
    /// <inheritdoc cref="DbParameterAttribute" path="/member/remarks" />
    public DbParameterAttribute(string propertyName) { ArgumentException.ThrowIfNullOrWhiteSpace(propertyName); }
}

/// <summary>Declares one compilation-wide default codec for a closed non-null CLR value type.</summary>
/// <remarks>The generator reads an accessible static field or getter of exactly ValueCodec&lt;T&gt; from the supplied provider type. The value is captured when each generated descriptor initializes. Nullable properties wrap this base policy. Duplicate defaults and incompatible members fail compilation; no runtime type registry is created.</remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class DbCodecAttribute : Attribute
{
    /// <summary>Declares a reusable static codec policy.</summary>
    /// <param name="valueType">The closed, nonnullable CLR type whose properties use this default.</param>
    /// <param name="providerType">The accessible type declaring the static codec field or getter.</param>
    /// <param name="memberName">The exact accessible static member name, normally supplied with nameof.</param>
    /// <exception cref="ArgumentNullException">A type is null.</exception>
    /// <exception cref="ArgumentException">The member name is empty or whitespace.</exception>
    /// <inheritdoc cref="DbCodecAttribute" path="/member/remarks" />
    public DbCodecAttribute(Type valueType, Type providerType, string memberName)
    { ArgumentNullException.ThrowIfNull(valueType); ArgumentNullException.ThrowIfNull(providerType); ArgumentException.ThrowIfNullOrWhiteSpace(memberName); }
}

/// <summary>Overrides a property's automatic or compilation-wide codec with one exact static policy.</summary>
/// <remarks>The member must expose ValueCodec&lt;T&gt; for the nonnullable property type; the generator adds the nullable wrapper when needed. The same policy is used by writes, generated readers and Fluent projections.</remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class DbUseCodecAttribute : Attribute
{
    /// <summary>Selects an accessible static codec field or getter.</summary>
    /// <param name="providerType">The accessible declaring type.</param>
    /// <param name="memberName">The exact static member name, normally supplied with nameof.</param>
    /// <exception cref="ArgumentNullException">The provider type is null.</exception>
    /// <exception cref="ArgumentException">The member name is empty or whitespace.</exception>
    /// <inheritdoc cref="DbUseCodecAttribute" path="/member/remarks" />
    public DbUseCodecAttribute(Type providerType, string memberName)
    { ArgumentNullException.ThrowIfNull(providerType); ArgumentException.ThrowIfNullOrWhiteSpace(memberName); }
}
