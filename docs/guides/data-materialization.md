# Static DTO materialization

A NativeWeave database query has an explicit ordered result shape and a direct
C# constructor. Rust validates provider types and emits the bounded typed row
batch. C# validates the complete shape and constructs your DTO through the
selected codecs. The same descriptors work in ordinary .NET and Native AOT.

## Minimal static projection

Reference `NativeWeave.Database` and the selected provider package. The
`NativeWeave.Data` namespace contains projections and codecs; the
`NativeWeave.Database` namespace contains source/session execution. Use an
asynchronously disposed source and session so cursor and connection retirement
can complete before shutdown.

```csharp
sealed record NameRow(long Id, string Name);

var projection = Projection<NameRow>.Create(
    [ResultField.Create("id", Codecs.Int64),
     ResultField.Create("name", Codecs.String)],
    row => new NameRow(row.Read(0, Codecs.Int64), row.Read(1, Codecs.String)));

await using var source = SqliteDataSource.Create("application.db");
await using var session = await source.OpenSessionAsync();
var rows = await session.QueryAsync(
    SqlCommand.Create("SELECT id,name FROM customers ORDER BY id"), projection);
```

The command is an explicit expert SQL boundary. The Fluent API uses the same
projection/runtime contracts and binds typed native expressions to query-scoped
`ResultColumn<T>` tokens. Neither path needs reflection setters, a managed SQL
AST, runtime generic construction or a Rust rebuild when a DTO changes. Manual
static projections remain available when the optional model generator is
excluded.

The compiled minimal/advanced example in
`samples/api/data-materialization/Examples.cs` is exercised by the component
consumer. It includes bounded async enumeration, SQL NULL, owned byte arrays
and static JSON for `Envelope<Settings>`.

## SQL NULL and complete shape checks

A nullable field must still exist at its declared ordinal with its exact alias
and codec. Select `Codecs.Nullable(Codecs.Int64)` for `long?` or
`Codecs.NullableReference(Codecs.String)` for `string?`. A non-null value in a
nullable column still uses that nullable codec when read. Missing fields,
duplicate aliases, wrong order or wrong types are `ShapeMismatch`; they never
become null. SQL NULL in a declared non-null field is a conversion failure.

A projection has at least one field and finite descriptor limits. Native
provider metadata is validated even for an empty result. The managed row reader
checks all fields before the materializer executes. A `ResultColumn<T>` belongs
to exactly one frozen projection; default and foreign tokens reject.

## Static JSON and owned values

Provide an explicit source-generated `JsonTypeInfo<T>` to `Codecs.Json`:

```csharp
var codec = Codecs.Json(SampleJson.Default.EnvelopeSettings);
```

The JSON context and closed generic type are ordinary application source. There
is no serializer discovery fallback. A JSON decoding error is explicit, and
static callbacks such as `IJsonOnDeserialized` may fail through the same cleanup
path as constructors and property setters.

The `RowReader` is a stack-only borrow valid only during the constructor call.
Strings and byte arrays returned by codecs are copies owned by the result; they
remain valid after the native batch, cursor and source have retired. Do not
attempt to retain the reader or a native span. The default DTO API is not a
zero-copy API.

## Failures, streaming and disposal

`QueryAsync` returns a bounded owned list. If a later constructor, setter or JSON
reader fails, the task fails and publishes no partial list. Values already
observed through an explicit stream are owned by the caller. The original user
exception is retained; codec conversion failures retain their typed database
classification. No reflection, stringification or default-value fallback runs.

The B06 stream owner releases its current batch in cleanup and asynchronously
joins native cursor retirement. The same cleanup applies to early `await
foreach` exit, cancellation and cardinality failures. Dispose the enumerator
when using it directly. Cancellation after execution begins requests native
cleanup and does not pretend the native cursor has already retired.

Buffered row/encoded-byte limits and batch limits are separate from managed
object allocation and process memory. This module adds no identity map and does
not perform relationship fix-up. It does not claim a performance or memory
improvement without a relevant measured baseline.

## Component acceptance

`tests/Data.Materialization` uses a handwritten `IRowLease` oracle to test
complete shape validation independently from db-wire production. It also runs
actual PostgreSQL18 and SQLite queries through B04/B06, including true nullable
parameters, missing/duplicate/wrong fields, constructor/setter/static-JSON
failures, early stop, cancellation, first/single cardinality and retained owned
DTOs after source shutdown. Test wrappers count actual managed batch owners and
encoded bytes; native diagnostics independently observe operation/cursor/buffer
retirement after each case and all six owner classes after shutdown.

The source composes into the existing Database facade. The isolated package
consumer references the normal Database/provider NuGets and executes the
compiled sample without a source-project reference. Linux x64 JIT and actual
Native AOT results, immutable input hashes and raw failures belong to the B12
Plan evidence. Windows acceptance and final pristine release consumption remain
with their designated platform/task rows. The generated and handwritten readers
consume the same independent shape oracle, and both execute real two-provider
rows and throwing constructors through the same counted cleanup path.

Run `tests/Data.Materialization/verify.py` with the pinned Design checkout,
PostgreSQL18 binaries, the two maintainer-built provider modules and their
committed Host source. The runner records immutable source/native inputs, builds
and executes JIT and Linux x64 AOT, packs the normal dependency graph, then copies
the public consumer to a directory outside the repository with a fresh NuGet
cache. Package, lockfile and executable hashes remain in the evidence directory.
Consumer compilation has no source-project or Rust build step.
