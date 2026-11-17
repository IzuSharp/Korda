# Static model generation

The database facade brings `NativeWeave.Query.Generator` transitively. An ordinary
C# model produces immutable `Table<T>`, typed `Column<T,TValue>` fields and a direct
row constructor/writer. SQL construction and execution remain in the same
maintainer-built Rust SeaQuery/SQLx assets. Adding or changing a supported model
requires only a C# rebuild; generation performs no network, database, Cargo or
native-module operation.

## Minimal model

```csharp
using NativeWeave.Data;

[WeaveTable("students", DescriptorName = "Students")]
public sealed partial record Student(
    [property: WeaveKey, WeaveColumn("id")] long Id,
    string Name,
    [property: WeaveColumn("email")] string? Email,
    [property: WeaveVersion, WeaveColumn("version")] long Version);
```

The store names are exactly `id`, `Name`, `email`, and `version`. CLR casing is
preserved when `WeaveColumn` is absent. `Students.Table` is immutable and
`Students.Row` is the same static projection contract used by handwritten
`TableBuilder<Student>`. `Students.Id`, `Students.Name`, `Students.Email` and
`Students.Version` retain their property spelling and generic value types.
A table has no native owner until a datasource consumes its descriptor.

Use the generated descriptors with the normal Fluent surface:

```csharp
using var query = session.From(Students.Table);
var rows = await query.WhereEqual(Students.Id, 7L).SelectAll().ToListAsync(token);
```

See [the executable generated/manual consumer](../../samples/api/query-generator/)
for actual database setup, source/session disposal and a second model.

## Constructor and codec policy

Top-level, concrete, closed classes and records can use one accessible constructor
covering their ordinary public properties, plus accessible set/init assignment for
remaining members. Public and internal accessibility are supported; ambiguous
constructors fail at compilation. Partial declarations are resolved as one symbol.
Constructor parameter order comes first; other properties follow ordinal CLR name
order. Required ignored members cannot be silently left unset.

Automatic scalar mappings cover Int16/Int32/Int64, Boolean, UTF-8 strings, byte
arrays, Guid, DateOnly, TimeOnly, UTC DateTime/DateTimeOffset, Single, Double,
Int32 enum codecs and their supported nullable forms. Decimal/JSON configurations
that need an explicit scale or static `JsonTypeInfo` use the handwritten codec
path; there is no implicit representation choice. UTC and enum-range validation
remain the existing codec's responsibility. A nullable key or nullable property
bound to a non-null constructor parameter fails at compilation.

`WeaveColumn(Generated = true)` records a database-generated column.
`WeaveVersion` records an optimistic-concurrency token; it does not increment a
value or enable tracking. `WeaveIgnore` excludes a property from both readers and
writers, and cannot be combined with other mapping annotations.

## Diagnostics and incremental behavior

| Diagnostic | Cause and action |
| --- | --- |
| NWQ0101 | No automatic enabled codec; select a supported scalar or an explicit handwritten mapping. |
| NWQ0102 | Nullable storage conflicts with a non-null constructor/key; make the static policy consistent. |
| NWQ0103 | Constructor/member assignment is ambiguous or incomplete; provide one accessible construction path. |
| NWQ0104 | Invalid, duplicate or conflicting table/column/key/descriptor metadata; use distinct exact names. |
| NWQ0105 | Selected compile-time native codec profile omits a required codec; select a compatible package/profile. |
| NWQ0106 | Unsupported source/dependency shape, including open generic/nested/inherited models; use an explicit closed static descriptor. |
| NWQ0107 | Mapping would require dynamic code or unbounded discovery, including object/dynamic/runtime Type; supply statically known members. |

The historic NWQ0001–NWQ0016 family is not repurposed. Errors point at the relevant
source symbol or constructor parameter. A maintainer profile can set the comma
separated `NativeWeaveEnabledNativeCodecs` IDs; runtime native capability admission
is still mandatory. Generated source ordering and names are deterministic. The
incremental output stage compares resolved source content, reusing it after an
unrelated file edit and invalidating it after a relevant model edit. Diagnostic
locations are refreshed with their source trees. The generator reads declared
input symbols directly, without relying on another generator's output.

## Explicit manual path

Set `<NativeWeaveExcludeGenerators>true</NativeWeaveExcludeGenerators>` in the
consumer project and provide equivalent `TableBuilder` declarations, direct row
constructors and typed writers. No `rd.xml`, dynamic-code switch, runtime
reflection, metadata scan or Cargo invocation is required. The sample's
`ManualMappings=true` configuration selects this path and compares the same
canonical table and projection hashes/results. Both paths use the same bounded
B12 materializer and B13 native query execution.

Static descriptor construction allocates managed metadata once per generated
class. Each DTO has the normal owned CLR strings/arrays/object allocation; this is
not a zero-copy mapping claim. Constructor failures use the existing stream/batch
cleanup path, which joins native cursor retirement separately from caller task
completion. NativeAOT evidence applies to the produced application; the Roslyn
compiler plugin itself targets netstandard2.0 and runs in the compiler host.

The exact attribute API and XML reference are checked in
[the generated API reference](../../src/Query.Generator/api-reference.md). Linux
x64 acceptance and package/native hashes are recorded by Task-05; other platforms
are separate acceptance rows.
