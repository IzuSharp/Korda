# Static materialization consumers

`Examples.cs` is the compiled manual path for B12. It uses only public
`Korda.Data` and `Korda.Database` APIs. The source defines an owned
record, an explicitly closed generic JSON record, and a static `JsonTypeInfo`
context. `ReadAsync` materializes a bounded list; `SumAsync` consumes a bounded
stream. Both are executed against PostgreSQL18 and SQLite by
`tests/Data.Materialization` in ordinary .NET and Linux x64 Native AOT.

The example requires the ordered columns `id BIGINT`, `name TEXT`, `bytes
BYTEA/BLOB`, `optional BIGINT NULL` and `settings JSONB/TEXT`. The test consumer
creates an isolated table and binds all values through explicit codecs. No
connection string, secret, source checkout or Cargo step belongs to the
application source. The caller owns and asynchronously disposes its source and
session.

`tests/Data.Materialization/consumer` packages this same public source as an
isolated NuGet consumer. Exact package/native/executable identities and actual
execution results are retained in the Linux evidence run. See
`docs/guides/data-materialization.md` for failures, ownership and nullability.
