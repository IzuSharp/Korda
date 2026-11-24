# Upgrade database annotation names

DB-Rename-01 gives every database annotation the `Db` domain prefix. They remain
in `Korda.Data` and are included in each independent provider package,
`Korda.Database.Sqlite` and `Korda.Database.PostgreSql`.

## Rename the annotations

The old column lists the previous public types for upgrade reference only. Use
the current short forms in model declarations and tutorials.

| Previous type | Current type | Current C# usage |
| --- | --- | --- |
| `WeaveTableAttribute` | `DbTableAttribute` | `[DbTable]` |
| `WeaveColumnAttribute` | `DbColumnAttribute` | `[DbColumn("column_name")]` |
| `WeaveKeyAttribute` | `DbKeyAttribute` | `[DbKey]` |
| `WeaveVersionAttribute` | `DbVersionAttribute` | `[DbVersion]` |
| `WeaveIgnoreAttribute` | `DbIgnoreAttribute` | `[DbIgnore]` |
| `WeaveProjectionAttribute` | `DbProjectionAttribute` | `[DbProjection]` |
| `WeaveConstructorAttribute` | `DbConstructorAttribute` | `[DbConstructor]` |
| `WeaveParameterAttribute` | `DbParameterAttribute` | `[DbParameter(nameof(Id))]` |
| `WeaveCodecAttribute` | `DbCodecAttribute` | `[assembly: DbCodec(...)]` |
| `WeaveUseCodecAttribute` | `DbUseCodecAttribute` | `[DbUseCodec(...)]` |

For example, this previous declaration:

```csharp
using Korda.Data;

[WeaveTable("orders", DescriptorName = "Orders")]
public sealed record Order(
    [property: WeaveKey] long Id,
    string? Note,
    [property: WeaveVersion] long Version);
```

becomes:

```csharp
using Korda.Data;

[DbTable("orders", DescriptorName = "Orders")]
public sealed record Order(
    [property: DbKey] long Id,
    string? Note,
    [property: DbVersion] long Version);
```

The namespace, constructor arguments, named arguments, default values, allowed
annotation targets and behavior stay the same. This includes the distinction
between the parameterless table convention and an explicitly named table.
Keep `property:`, `assembly:` and other existing annotation targets when
changing the name. The generator and database scaffolder emit the current
names; regenerate previously scaffolded source or update it with this table.

## Preserve conversion and JSON policies

`[assembly: DbCodec(...)]` still registers a shared conversion for a CLR type.
`[DbUseCodec(...)]` still selects a conversion for one property. Codec members,
`Codecs.Convert`, decimal configuration and null handling keep their existing
contracts. A JSON codec still receives source-generated `JsonTypeInfo<T>` through
the existing `System.Text.Json` context; the rename adds no runtime discovery or
reflection fallback. See the [complete codec example](query-generator.md#declare-custom-id-decimal-and-json-policies-once).

## Rebuild consumers together

This is a source and binary breaking public API rename. The old attribute types
are removed; there are no obsolete aliases or type forwarders. Update every
model, DTO, assembly registration and property override, then rebuild the
application and any libraries compiled against the old attribute types with
the matching candidate package and generator. A stale generated file or a
previously compiled library referencing an old attribute does not become
compatible by replacing the provider package alone.

Model and DTO generation, constructor selection, reusable policies and negative
diagnostics retain their behavior. Existing diagnostic IDs retain their meaning;
diagnostic text names the current annotations. Applications keep using the
precompiled native provider and need no native rebuild for this rename.

This change does not rename physical tables, columns or migration history.
The `_nativeweave_migration_phases` table remains the existing on-disk contract.
Historical Design snapshots, migration evidence and actual repository URLs keep
their recorded names. Current tutorials, XML comments and API references use
the `Db` names.

The [order example](../../samples/api/orm-experience/) is the executable
provider-package example. It covers generated entities, immutable DTOs and
compiled reverse mappings on SQLite and PostgreSQL with Native AOT. Follow
the [public API naming conventions](../../CONTRIBUTING.md#public-api-naming)
when introducing future public names.
