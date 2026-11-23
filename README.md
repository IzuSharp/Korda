# Korda

Korda extends the C#/.NET ecosystem where mature Native AOT support is missing. Each product exposes a strongly typed C# API and ships as an independently installable NuGet package. Products can reuse proven implementations in C++, Go, Rust, Python or another language when their dependencies and deployment model support Native AOT. Ordinary managed objects and garbage collection are part of the C# programming model.

## Available implementation

The Fluent ORM supports PostgreSQL and SQLite. Write queries, map entities and DTOs, stream results, and manage transactions and migrations in C#. The C++20 Database Host uses libpq and SQLite and owns native resources. Application hosting is independent, so an ASP.NET Core endpoint and a gRPC method can call the same database code.

The validated target is **Linux x64 with .NET 11 and Native AOT**. Each provider bundles the shared ORM, query generator, build integration and its precompiled native library. Entity and DTO changes require only a C# rebuild.

Install the provider for your database once its Korda preview is available on nuget.org. Applications can reference both.

```sh
dotnet add package Korda.Database.Sqlite --prerelease
dotnet add package Korda.Database.PostgreSql --prerelease
```

Korda was formerly NativeWeave. When upgrading existing applications, replace the old provider references and namespaces together, then rebuild. Existing database files and migration history remain usable.

See [getting started](docs/getting-started.md), the [package guide](docs/guides/packaging-domain.md) and the [API index](docs/api-reference.md).

## Future direction

AI agent frameworks and Qt/QML integration are potential areas for future work. The database ORM is the implemented product today.

## Development

See the [native build guide](docs/guides/cpp-native.md), [verification guide](docs/verification.md), [release workflow](docs/releasing.md), [contributing guide](CONTRIBUTING.md) and [security policy](SECURITY.md). Korda uses the [MIT license](LICENSE). The packages include [third-party notices](THIRD_PARTY_NOTICES.md).
