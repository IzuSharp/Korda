namespace NativeWeave.Data;

/// <summary>Opts a model into deterministic, static table and DTO mapping generation.</summary>
/// <remarks>The generator reads source symbols and emits the same TableBuilder calls as manual metadata.
/// It never opens a database, loads a native module, runs Cargo or discovers runtime model types.</remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class WeaveTableAttribute : Attribute
{
    /// <summary>Declares the exact store table name.</summary>
    /// <param name="name">A nonempty table identifier; casing is preserved.</param>
    /// <exception cref="ArgumentException">The name is empty or whitespace.</exception>
    public WeaveTableAttribute(string name) { ArgumentException.ThrowIfNullOrWhiteSpace(name); }
    /// <summary>Gets or initializes the optional exact schema name.</summary>
    public string? Schema { get; init; }
    /// <summary>Gets or initializes the generated static descriptor class name; default is EntityNameMapping.</summary>
    public string? DescriptorName { get; init; }
}
/// <summary>Overrides a property's exact store name and generated-value policy.</summary>
/// <remarks>Without this attribute the CLR property symbol name is used unchanged. The generated column member keeps its CLR spelling.</remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class WeaveColumnAttribute : Attribute
{
    /// <summary>Declares the exact store column name.</summary>
    /// <param name="name">A nonempty store identifier.</param>
    /// <exception cref="ArgumentException">The name is empty or whitespace.</exception>
    public WeaveColumnAttribute(string name) { ArgumentException.ThrowIfNullOrWhiteSpace(name); }
    /// <summary>Gets or initializes whether the database supplies the value on insertion.</summary>
    public bool Generated { get; init; }
}
/// <summary>Marks a non-null primary-key property in deterministic projection order.</summary>
/// <remarks>Nullable keys are rejected at compilation and again by the static descriptor builder; there is no inferred key convention.</remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class WeaveKeyAttribute : Attribute { }
/// <summary>Marks an explicit optimistic-concurrency token.</summary>
/// <remarks>Generated metadata sets IsConcurrencyToken. The application supplies expected and changed values; no automatic version increment occurs.</remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class WeaveVersionAttribute : Attribute { }
/// <summary>Excludes a property from generated metadata and row serialization.</summary>
/// <remarks>Combining Ignore with another mapping annotation is inconsistent and rejected. Required constructor inputs still need a supported static mapping.</remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class WeaveIgnoreAttribute : Attribute { }
