# Project scope and history

Korda provides strongly typed C# libraries for capabilities that benefit from better .NET Native AOT support. Each product chooses its implementation language and ships independently. The [README](../README.md) describes the current products and future direction.

The database product grew out of the NativeWeave project. DB-Scope-01 retained the PostgreSQL and SQLite ORM, and retired the earlier server, protocol and demonstration products. DB-CPP-01 migrated its database execution layer from Rust to C++20, libpq and SQLite. Application code supplies its own host. The database engine manages pools, sessions, transactions, migrations, compiled queries and streaming results.

Historical Plan evidence and the pinned API design retain the names and hashes of the artifacts they describe. The database migration journal retains its existing on-disk name so applications can resume an existing migration history after upgrading packages.

See the [database migration record](https://github.com/IzuSharp/Plan/tree/main/ledger/evidence/batches/DB-CPP-01), [database design](https://github.com/IzuSharp/Korda-Design/blob/main/docs/database/current-product.md) and [native build guide](guides/cpp-native.md).
