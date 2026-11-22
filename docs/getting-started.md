# Use NativeWeave from C#

In your .NET 11 application, install the Linux x64 provider for your database:

```sh
dotnet add package NativeWeave.Database.Sqlite --prerelease
# For PostgreSQL:
dotnet add package NativeWeave.Database.PostgreSql --prerelease
```

Each provider contains the shared managed assemblies, generator, build integration and its precompiled native library in one package. PostgreSQL applications need a reachable database server. For an unpublished development build, the candidate feed is `artifacts/db-scope/prepared/candidate/feed` and its manifest records the exact versions.

## Typed queries and DTOs

The executable [database package sample](../samples/api/packaging-domain/README.md) contains static table/model declarations, typed CRUD, a strongly typed Fluent projection into a DTO and actual returned-data checks for each provider. Its added-model template shows an ordinary C# entity/DTO edit without rebuilding native code.

`DataSource` owns the provider pool; open and dispose a `DataSession` for business work. Typed queries bind parameters and project through static mapping. Dispose sources, sessions, transactions and stream enumerators according to their API contracts. The component guides document provider-specific capabilities, transaction outcomes and cancellation semantics.

A business method can be called directly by a console app, a standard ASP.NET Core endpoint or a standard C# gRPC method. Install and configure the chosen host independently.

## Build and publish

```sh
dotnet restore
dotnet run -c Release
dotnet publish -c Release -r linux-x64 -p:PublishAot=true -o publish
./publish/YourApplication
```

Native AOT needs the platform C linker prerequisites. Applications use the restored precompiled provider library and do not install or invoke Rust. The strict package targets validate the exact restored graph and selected native bytes. Do not suppress a closure error or substitute a native binary.

The maintained consumer verifier runs SQLite-only, PostgreSQL-only and combined dependency graphs; it checks the actual mapped native modules and hashes as well as the package and publish directory. See [packaging](guides/packaging-domain.md) for reproducing those checks.
