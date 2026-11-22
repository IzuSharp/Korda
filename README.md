# NativeWeave

**Strongly typed C# Fluent ORM for PostgreSQL and SQLite, backed by C++20, libpq and SQLite.**

NativeWeave lets ordinary C# applications use data sources, connection pools, sessions, transactions, migrations, parameter binding, typed Fluent queries, static model/DTO mappings, relations and CRUD, and streaming results. The C++ Database Host executes database operations and owns their native resources. Core and the required interop bindings connect the two languages with explicit cancellation, errors and disposal.

The current validation target is **Linux x64, .NET11/C#15, LLVM23.1.5/C++20 and Native AOT**. The NuGet packages contain precompiled provider libraries. Application builds use normal PackageReference, restore, build and publish operations; ordinary entity/DTO changes require no native compilation.

## Application hosting

A database business method is ordinary C#. A standard ASP.NET Core HTTP endpoint or a standard C# gRPC method can call that same method. The application chooses and installs its host independently.

## Install a database provider

Choose the provider for your database. From a .NET 11 application on Linux x64, run one of:

```sh
dotnet add package NativeWeave.Database.Sqlite --prerelease
dotnet add package NativeWeave.Database.PostgreSql --prerelease
```

Each provider includes the shared ORM assemblies, query generator, MSBuild validation and its Linux x64 native library. Applications using both databases can reference both providers. The twelve-component graph is retained for internal compatibility tests.

See [getting started](docs/getting-started.md), the [package guide](docs/guides/packaging-domain.md), and the [API index](docs/api-reference.md).

## Maintainer validation

Use the pinned SDK11.0.100, LLVM23.1.5 (clang/clang++, clang-format, clang-tidy), CMake4.3.3, Ninja1.13.2, Python3.11+, zlib/OpenSSL development files and PostgreSQL18 tools. The database account must be able to create/drop disposable databases and roles. Regression tests deliberately exercise failures and recovery; use an isolated test server.

First follow [C++ native build setup and shared presets](docs/guides/cpp-native.md). Run `pre-commit install` for the local fixed-version formatting hook; PR CI checks the same rules.

Obtain the unchanged historical API inventory used by existing declaration checks:

```sh
git clone https://github.com/IzuSharp/NativeWeave-Design artifacts/frozen-design
git -C artifacts/frozen-design checkout c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9
git clone https://github.com/IzuSharp/Plan artifacts/frozen-plan
git -C artifacts/frozen-plan checkout 8afa0311f45ab8a8157fc45c51e95a0b17dd09f2
export NW_TEST_POSTGRES=postgresql://nativeweave@127.0.0.1:5432/postgres
export NW_POSTGRES_BIN=/path/to/postgresql18/bin
python3 eng/build.py verify
```

Run from a committed clean source tree. Each candidate output is immutable; use a fresh checkout/output for a new identity. The gate builds/tests retained workspaces, checks compiled APIs, runs real PostgreSQL/SQLite and migrated database failure scenarios, packs the database candidate, and executes out-of-tree strong typed Fluent/DTO consumers in JIT and Native AOT. Logs and source/package/native identities are in `artifacts/db-scope/`.

Linux CI also runs the retained Core packaging/minimum-CPU checks, ASan/UBSan with leak scanning, a fresh Linux native build/package/consumer image without Rust, a pinned SDK container with no Rust installed, immutable database release/recovery tests, and a runtime-only transfer. Security checks remain required. Configuration is not evidence of a passing run; actual results are recorded in the [DB-CPP-01 Plan handoff](https://github.com/IzuSharp/Plan/blob/main/ledger/evidence/batches/DB-CPP-01/handoff-linux.md).

## Status and history

After a pull request merges into `main`, [CI](https://github.com/IzuSharp/NativeWeave/actions/workflows/ci.yml) tests the Linux x64 packages and publishes a database preview to nuget.org. The final job installs both providers from the public feed and runs each in JIT and Native AOT. See [release and retry details](docs/releasing.md).

- [Background](docs/background.md) and [verification](docs/verification.md)
- [Contributing](CONTRIBUTING.md) and [security policy](SECURITY.md)
- [Current product design](https://github.com/IzuSharp/NativeWeave-Design/blob/main/docs/database/current-product.md)
- [Plan and acceptance](https://github.com/IzuSharp/Plan)

The existing [MIT license](LICENSE) is unchanged. Both public packages include their actual dependency license inventory and notices; see [third-party notices](THIRD_PARTY_NOTICES.md).
