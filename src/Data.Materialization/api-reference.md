# Static materialization API reference contribution

Design input: `NativeWeave-Design` commit
`c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9`, `docs/api/database.md`
sections 4 and 6, `docs/generators/query-generation.md` section 4, and the
corresponding exact database symbol policies.

The public types are the existing `NativeWeave.Data` definitions. This module is
source-composed into `NativeWeave.Database`; it adds no public CLR types, no
native ABI export and no independent package. The build produces the original
Data XML alongside the Database facade XML. B13 owns the public Fluent
`ProjectionBuilder<T>` and `QuerySelection<T>` wrappers over this implementation.

| Existing public contract | Behavior exercised by B12 |
| --- | --- |
| `Projection<T>.Create(ReadOnlySpan<ResultField>, RowMaterializer<T>)` | Copies ordered fields, rejects empty/duplicate aliases, retains a direct static materializer and hashes only canonical logical shape. |
| `ResultField.Create<T>(string, ValueCodec<T>)` | Selects an exact alias, codec configuration and SQL nullability. |
| `RowMaterializer<T>(RowReader)` | Synchronously constructs one owned CLR value. Closed generics and source-generated `JsonTypeInfo<T>` remain statically visible. |
| `RowReader.Get<T>(ResultColumn<T>)` | Checks the exact projection token identity, then the complete field codec/nullability. |
| `RowReader.Get<TEntity,T>(Column<TEntity,T>)` | Requires the exact table identity retained by its table projection. |
| `RowReader.Read<T>(int, ValueCodec<T>)` | Reads an explicitly selected ordinal and exact codec; it does not narrow, stringify or find fields dynamically. |
| `RowReader.IsNull(int)` | Inspects SQL NULL only after complete projection validation. Missing fields are shape failures. |
| `ResultColumn<T>.Ordinal / Name` | Reads a frozen projection token; default tokens reject. |
| `EntityWriter<T>` / `RowWriter.Set` | Existing B01 snapshot contract; B12 does not replace entity extraction or native CRUD. |

`ProjectionDefinition<T>` is internal. `Add` validates exact aliases and
same-query native references under B09 query admission. `Build` freezes fields,
references, the materializer and one token identity that was created before the
first `Add`. A direct constructor can capture those typed tokens before Build.
The registration callback publishes immutable facade metadata and must not
re-enter query admission or call FFI while this gate is held. It never runs a
materializer or emits SQL. Native SELECT validation still checks the source,
actual expression codec/nullability and projection shape.

B01 validates complete count, order, aliases, scalar identity, codec
configuration and nullability before invoking any constructor. B06 owns the
batch lease, bounded result collection, cancellation and native cleanup join.
Exceptions thrown by user constructors/setters retain their original identity;
codec format/JSON conversion failures preserve the existing `DatabaseException`
conversion classification and inner exception. A failed list does not publish a
partial collection. Successfully yielded values already belong to the caller.

Strings, arrays and JSON objects are managed-owned values. Buffer accounting in
component tests counts encoded batch bytes and native owner references; it is
not a claim about total managed heap allocation, process RSS or zero-copy DTOs.
