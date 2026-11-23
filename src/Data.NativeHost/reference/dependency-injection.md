# Korda.Database.DependencyInjection compiled API

Generated from the compiled product assembly and XML by `src/Build.Integration/ApiReference`.

Current product declarations compared with Design commit c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9. Ownership, costs and Linux applicability are described in docs/guides/data-nativehost.md.

## `M:Microsoft.Extensions.DependencyInjection.KordaDatabaseServiceCollectionExtensions.AddKeyedKordaDatabase(Microsoft.Extensions.DependencyInjection.IServiceCollection,System.String,System.Action{Korda.Database.DataSourceBuilder})`

```csharp
public static Microsoft.Extensions.DependencyInjection.IServiceCollection Microsoft.Extensions.DependencyInjection.KordaDatabaseServiceCollectionExtensions.AddKeyedKordaDatabase(this Microsoft.Extensions.DependencyInjection.IServiceCollection services, string name, System.Action<Korda.Database.DataSourceBuilder> configure)
```

**summary:** Registers one keyed container-owned singleton for standard keyed service resolution.

**param services:** The service collection to extend.

**param name:** The nonempty ordinal source key.

**param configure:** The configuration evaluated once at singleton construction.

**returns:** The supplied service collection.

**exception:** The collection, key or configuration is null.

**exception:** The key is empty.

**exception:** A source with this key is already registered.

**Inherited type policy:** Configuration runs once when the singleton is constructed. A container owns and disposes the created source; dispose the container asynchronously to await physical retirement. Sessions are never registered implicitly.

## `M:Microsoft.Extensions.DependencyInjection.KordaDatabaseServiceCollectionExtensions.AddKordaDatabase(Microsoft.Extensions.DependencyInjection.IServiceCollection,System.Action{Korda.Database.DataSourceBuilder})`

```csharp
public static Microsoft.Extensions.DependencyInjection.IServiceCollection Microsoft.Extensions.DependencyInjection.KordaDatabaseServiceCollectionExtensions.AddKordaDatabase(this Microsoft.Extensions.DependencyInjection.IServiceCollection services, System.Action<Korda.Database.DataSourceBuilder> configure)
```

**summary:** Registers one unkeyed container-owned singleton source using the ordinary builder.

**param services:** The service collection to extend.

**param configure:** The configuration evaluated once at singleton construction.

**returns:** The supplied service collection.

**exception:** The collection or configuration is null.

**exception:** An unkeyed source is already registered.

**Inherited type policy:** Configuration runs once when the singleton is constructed. A container owns and disposes the created source; dispose the container asynchronously to await physical retirement. Sessions are never registered implicitly.

## `T:Microsoft.Extensions.DependencyInjection.KordaDatabaseServiceCollectionExtensions`

```csharp
public static class Microsoft.Extensions.DependencyInjection.KordaDatabaseServiceCollectionExtensions
```

**summary:** Registers independently owned singleton sources through the optional database DI package.

**remarks:** Configuration runs once when the singleton is constructed. A container owns and disposes the created source; dispose the container asynchronously to await physical retirement. Sessions are never registered implicitly.
