# SQLite component consumers

`SqliteExamples.cs` is compiled with both the sample project and the actual JIT/
Native AOT verifier. It demonstrates the exact default profile, an explicit
file-backed Immediate/DELETE/Normal profile, private memory, and immutable Engine
metadata. The verifier snapshots these values with B01's canonical kind9 record
and compares independently observed SQLite PRAGMAs on real connections.

The component asset is built once by the maintainer before ordinary .NET builds.
The project generates its profile and A02 output-location record under `obj` from
that actual binary; ordinary consumer build/run never calls Cargo. The profile
advertises `db.sqlite.profile.v1`, not the complete B04 host ABI.

The exact `SqliteDataSource.Create` and `DataSourceBuilder.UseSqlite` facade
declarations are recorded in
`src/Data.Sqlite/host-composition.expected.json`. They compose with B04's owned
host types. This sample does not introduce placeholder host types or run Design
stubs. See `docs/guides/data-sqlite.md` for limits, cancellation, worker shutdown,
savepoints, native operation mapping and Linux component verification.
