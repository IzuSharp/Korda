# Data descriptors and dynamic metadata boundaries (B01)

> **DB-CPP-01 implementation update.** The public C# examples, signatures and ownership contracts in this guide remain the compatibility inputs. The current maintained native implementation is C++20; its module mapping, direct libpq/sqlite3 execution, Clang/CMake commands and package production are documented in [C++ native implementation](cpp-native.md). Descriptions below of Rust, SQLx, SeaQuery or Tokio internals are retained as the fixed `465cd0f` behavioral reference, not current build dependencies. The named verification entry points now build the ported C++ fixtures and retain their original managed assertions.

`NativeWeave.Data` provides real, strongly typed metadata, value snapshots, row contracts and errors without initializing a native runtime. B01 also owns the shared Rust metadata validator, bounded registry, db-wire/1 representation and kind23 relation definition. These contracts are the single boundary consumed by the later Database tasks; this component does not connect to a database or implement SQL semantics.

The implementation follows Plan task B01 revision 0.4.0 and Design commit `c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9`, especially `docs/api/database.md` sections 4, 9.1.1, 9.1.2 and 9.2.1. The [compiled API reference](../../src/Data.Abstractions/api-reference.md) is generated from the product assembly and XML. Its 132 symbols are compared to the exact pinned declaration inventory, including generic constraints, nullability, defaults, parameter names and generated record/delegate members.

## Minimal consumer

```csharp
using NativeWeave.Data;

var builder = new TableBuilder<Student>("Students");
var id = builder.Column("Id", Codecs.Int64,
    new ColumnOptions { IsPrimaryKey = true });
var name = builder.Column("Name", Codecs.String);
var students = builder.Build(
    row => new Student(row.Get(id), row.Get(name)),
    (row, student) => { row.Set(id, student.Id); row.Set(name, student.Name); });

var key = students.Key(id, 7L);
var changes = new Changes<Student>().Set(name, "Ada");
var command = SqlCommand.Create(
    "SELECT Id, Name FROM Students WHERE Id = @id",
    new SqlParameters().Add("id", 7L, Codecs.Int64));

public sealed record Student(long Id, string Name);
```

These are usable metadata and parameter values; execution is provided by the owning native Database host in B04 and its provider/stream/query tasks. This sample performs no database operation. The executable [minimal and advanced consumer](../../samples/api/data-abstractions/Program.cs) also declares an optional relationship and an independent DTO projection. Run it with ordinary `dotnet run --project samples/api/data-abstractions` after restoring the fixed SDK dependencies.

The component is packed as `NativeWeave.Data`; it has no provider, native asset, reflection serializer, SQL renderer, Server dependency or consumer Cargo target. All implemented APIs use BCL types. The broader planned Data/Core dependency composition remains an A13/A16 integration responsibility when those Core types are actually consumed; this task introduces no fake dependency package.

## Exact identities and metadata

Table and column IDs are the first four SHA-256 bytes interpreted as little-endian u32 over the exact canonical identity arrays from the design. Zero and collisions reject; IDs are never reassigned by registration order. The known closed entity type contributes only its assembly simple name and exact CLR `FullName`. No member scan, reflection setter or dynamic constructor is used.

Store names preserve case and Unicode. `Name` and `name` are different managed descriptor lookups. A generator must pass the actual CLR property name unless an explicit store-name override is declared. The native parser additionally applies the ordinary provider identifier policy (ASCII-insensitive duplicates for SQLite, exact quoted names for PostgreSQL). There is no implicit lowercasing, pluralization, Unicode normalization or snake-case conversion.

The single schema source is [`schema-v1.json`](../../native/database/interfaces/schema-v1.json). Its generator owns shared C#/Rust wire sizes and canonical property sequences. Canonical model JSON sorts columns by ID, preserves constructor/projection order separately, preserves contiguous primary-key order, and has an empty reserved `relations` array. Optional values are explicit `null`. Control characters use lowercase six-byte `\u00xx` escapes; other Unicode scalars remain strict UTF-8. SHA-256 hashes identify business schema content independently from ABI/package versions.

`Build` validates and consumes its single-writer builder. An immutable table owns its column definitions and direct materializer/writer delegates. Equivalent handwritten and generated-shape calls have identical bytes; the current tests hand-author both forms and do **not** claim that the B10 generator has executed. An additional C# entity is accepted by the same already-built Rust verifier, with no entity-specific Rust compilation.

## Values, codecs and rows

`ValueCodec<T>.Id` is the logical scalar registry ID. Decimal precision/scale and fixed-money scale participate in exact configuration identity; source-bound configured IDs start at 1024. Nullable wrappers retain the logical ID and add explicit SQL NULL permission. Unsupported required codecs/configurations/profiles fail before registration or database traffic. The native ordinary SQLite profile rejects numeric decimal; exact decimal-text storage and fixed-scale values have their separate declared policies.

B01 implements CLR↔db-wire representation so snapshots and row contracts are executable. B07 owns actual provider conversions and scalar capability binding. These responsibilities share the same logical IDs and configuration schema. Integer widths are exact, GUIDs use RFC 4122 byte order, floating values are finite, UTC/time values reject submicrosecond loss, enum values use checked Int32 representation, and decimals use canonical coefficient/scale bytes. JSON requires explicit `JsonTypeInfo<T>`. JSON `null` is a non-null JSON payload, distinct from SQL NULL. Native JSON admission uses serde_json's syntax-only borrowed `RawValue`, preserving valid exponent ranges and arbitrary object keys without numeric conversion; a narrow string-aware guard limits container depth to 64. The original owned wire bytes remain unchanged.

Strings and byte arrays become owned values. `SqlParameters.Add`, `Changes.Set`, `ConcurrencyCheck.Expect` and key construction snapshot input immediately. Immutable keys and relation extensions return new objects. Command construction snapshots the parameter collection; later changes cannot alter a submitted command. Typed paths never use `object[]`. The optional `FormattableString` path has its normal boxing/argument-array cost and parses only composite-format literal/hole segments, not SQL. Untyped null, unused arguments, alignment and format specifiers reject. Rust's later scanner owns provider placeholder translation and verifies named/positional holes against actual SQL token boundaries.

An explicit zero command timeout is retained as expiration before dispatch. A null timeout is resolved by the later source owner. Negative or nanosecond-overflowing durations reject at descriptor construction.

`RowReader` and `RowWriter` are stack-only. A row's complete count, ordered aliases, codecs/configurations and nullable flags are checked before calling the materializer. A missing nullable column never becomes SQL NULL. Reads check that the lease is still live and return owned CLR values. Column/result tokens must belong to the exact table/projection. The writer rejects repeated assignments and incomplete required fields; generated insert fields are omitted by policy. Exceptions publish no partial row to a native provider.

Ordinary projections require nonempty exact aliases. The internal scalar-only context emits the designed one-field empty-alias sentinel and ignores only the provider column name; count, codec, configuration and nullability remain mandatory. Native `Projection::parse_scalar` admits that canonical shape explicitly, while ordinary `Projection::parse` rejects it. The later scalar operation owner supplies the exact-one-row expectation before interpreting a result batch.

## Relation and parent-key boundary

A relationship is created after both immutable tables. Its ordered pairs must cover the entire parent primary key before registration, use distinct child IDs, and agree in logical codec, canonical configuration and declared collation. Optional methods require nullable child columns; ordinary methods require non-null child columns. Generic compatibility alone does not replace metadata checks.

The canonical relation identity includes both table hashes and IDs, ordered parent and child column IDs, and child nullability. Kind23 carries those fields and its independent SHA-256. Registering or extending a relation never modifies either table's hash. The parser retains both validated model owners only after all fields, ranges, hashes and pair rules pass. Parent-key expectations require exactly one kind4 record with the parent's complete ordered key shape; a general mutation batch is not accepted as a one-parent key.

The native `MetadataRegistry` is an engine-local component with explicit entry/byte limits. Registration validates and owns canonical bytes before synchronous return, deduplicates byte-identical metadata, and rejects hash/ID collisions or budget overflow without publishing a partial entry. It has no process-global service or runtime. The later source owner composes its model/projection/relation caches under the shared source budget and supplies source-generation identity.

## Ownership and downstream implementation

| Boundary | B01 contribution | Owning later implementation |
| --- | --- | --- |
| Data descriptors, keys, values, row leases and errors | Canonical declarations plus implemented metadata/snapshot behavior | B06/B07/B09/B12 consume the same definitions |
| Value and result bytes | One 96-byte-header db-wire schema and independent C#/Rust validation/encoding | B07 provider codecs; B06 actual streamed native row owners |
| Relation definition and key shape | Canonical kind23, pair/configuration validation and exact one-parent key envelope | B09 binding; B14 registration/lifetime and the actual child query |
| Physical session | Shared Rust `SessionAdmission` declaration with separate command/cleanup entry, protocol disposition and local lifetime | B02/B03 execute; B04 owns host; B05 coordinates transaction outcomes |
| DatabaseProvider/Limits/Capabilities, DataSourceBuilder/source/session concrete APIs | Exact declaration inputs remain pinned; no duplicate fake owner is published in Data | B04 composes `NativeWeave.Database`; B13 adds query facade |
| Generated mapping | Same manual runtime API and deterministic identity oracle | B10 generator and diagnostics |

The packet's related-type list does not transfer the native host or query assembly into this component. Publishing a throwing `DataSourceBuilder.Build`, a managed fallback, or duplicate Query primitives would violate that ownership. No downstream host success, source disposal, provider transaction effect or end-to-end query is claimed here. Independent `TransactionOutcome`, `ConnectionDisposition` and `LocalLifetime` native contracts keep protocol outcome separate from reuse and retirement.

## Costs and threading

Startup metadata creation allocates ordered column definitions, canonical UTF-8 bytes and SHA-256 results. Work is bounded by 4,096 descriptors and the ordinary 4 MiB encoded envelope limit; an actual source may impose tighter limits. These bounds are contract fixture maxima, not a process RSS guarantee. Builders, changes and parameter collections are single-writer. Published tables/projections/keys/commands/relations are immutable; user-supplied delegates must follow their advertised synchronous/threading behavior.

Scalar snapshots allocate their owned wire payload. Numeric encoders do not allocate an extra closure per call. Byte/string/JSON copies and decimal normalization work are explicit costs; nullable reads do not add an intermediate payload copy. The harness does not claim near-Rust performance or provider speed from these component checks. A21/B23 own comparable measured workloads.

## Reproduce Linux x64 verification

With the fixed .NET SDK 11.0.100 and LLVM 23.1.5 on PATH:

```sh
python3 tests/Data.Abstractions/verify.py --output artifacts/B01/run-1
```

For a network-isolated run, supply an existing cache/feed of the .NET 11.0.0 AOT and runtime packs:

```sh
python3 tests/Data.Abstractions/verify.py \
  --offline-packages /absolute/path/to/cached/packages \
  --output artifacts/B01/run-offline
```

Offline mode clears NuGet remote sources and records that vulnerability auditing was unavailable. It does not alter repository NuGet policy. Cargo uses the committed component lock with `--locked --offline`; restore the reviewed crates first when setting up a new maintainer environment. The verifier builds an exact temporary copy of the owned crate with a local workspace marker, leaving shared Cargo integration to the batch owner.

The Data library and contract console keep the ordinary `packages.lock.json` separate from the explicit-RID `packages.linux-x64.aot.lock.json`. AOT's compiler/runtime dependencies therefore do not rewrite the ordinary consumer lock. The conditional lock path follows the selected runtime identifier; this delivery generates and verifies only Linux x64.

The verifier executes:

- Exact compiled 132-symbol API comparison, complete explicit XML and generated reference checks.
- Managed positive/failure cases for canonical identity, mutable input snapshots, generic ownership, NULL/shape/lease behavior, relations and every scalar representation.
- Rust/C# independent goldens, malformed metadata/wire/control rejection, registry deduplication and atomic-budget failure checks.
- Wrong entity, wrong value, value-type entity and row-across-await compilation negatives.
- A separately packed, outside-checkout `PackageReference` consumer under a Rust invocation trap, ordinary JIT execution and actual Linux x64 Native AOT execution.
- Actual Native AOT execution of the contract test suite and byte equality against JIT/native goldens.

It retains commands, raw logs, source/lock identities, dependency licenses and package/native/executable SHA-256 hashes. A Rust PATH trap is not a pristine-machine proof; A16/B24 own that later gate. Windows verification is explicitly deferred by this round's user instruction. Component success does not establish all-platform or assembled Database acceptance.
