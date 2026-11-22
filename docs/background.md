# Current implementation boundary

DB-Scope-01 narrowed NativeWeave to one database product: strongly typed Fluent ORM in ordinary C# applications, with PostgreSQL, SQLite, Native AOT and precompiled native execution. DB-CPP-01 migrates that native implementation to C++20, libpq and SQLite while preserving the accepted C# and ABI behavior.

The database facade composes the existing native query compiler, provider pools, sessions, transactions, migrations, static model/DTO mapping, relation/CRUD and streaming implementations. Database Host owns database execution and remains essential. Core supplies the used ABI, lifetime, memory, error, callback, async and execution contracts.

Application hosting is chosen by the caller. Standard ASP.NET Core HTTP and standard C# gRPC methods can invoke the same business query method. NativeWeave's own HTTP/gRPC server, routing and protocol products, MCP, independent compression/proof engines and HelloWorld demo are retired from current code, build, test and package graphs.

## Scope and history

The owner terminated Perf-01. This batch begins from fresh main commits, updates Plan before Design and implementation, and ends after migration, actual same-source Rust/C++ regression/consumer execution, independent reviews, merges and merged-main CI checks. No performance work, new ORM feature or replacement host wrapper is included.

The historical Task-01 through Task-07 Plan evidence remains tied to its original source and artifact identities. Unchanged API signature checks read their original pinned Design snapshot. That history neither authorizes old products nor proves the new database candidate passed.

See the [current Plan batch](https://github.com/IzuSharp/Plan/tree/main/ledger/evidence/batches/DB-CPP-01) and [current Design](https://github.com/IzuSharp/NativeWeave-Design/blob/main/docs/database/current-product.md).

The current native source and maintainer workflow are described in [cpp-native](guides/cpp-native.md). The former Rust source is retained only in Git history and the fixed acceptance reference.
