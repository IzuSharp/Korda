# Use Korda from C#

When its preview is available on nuget.org, install the provider for your .NET 11 Linux x64 application:

```sh
dotnet add package Korda.Database.Sqlite --prerelease
# For PostgreSQL:
dotnet add package Korda.Database.PostgreSql --prerelease
```

Each provider contains the shared managed assemblies, generator, build integration and its precompiled native library in one package. PostgreSQL applications need a reachable database server. For an unpublished development build, the candidate feed is `artifacts/public/feed` and its manifest records the exact versions.

## Typed queries and DTOs

The executable [database package sample](../samples/api/packaging-domain/README.md) contains static table/model declarations, typed CRUD, a strongly typed Fluent projection into a DTO and actual returned-data checks for each provider. Its added-model template shows an ordinary C# entity/DTO edit without rebuilding native code.

`DataSource` owns the provider pool; open and dispose a `DataSession` for business work. Typed queries bind parameters and project through static mapping. Dispose sources, sessions, transactions and stream enumerators according to their API contracts. The component guides document provider-specific capabilities, transaction outcomes and cancellation semantics.

A business method can be called directly by a console app, a standard ASP.NET Core endpoint or a standard C# gRPC method. Install and configure the chosen host independently.

## Build and publish

```sh
dotnet restore
dotnet publish -c Release -r linux-x64 -p:PublishAot=true -o publish
./publish/YourApplication
```

Native AOT needs the platform C linker prerequisites. Applications use the precompiled provider library restored from NuGet. The strict package targets validate the exact restored graph and selected native bytes. Do not suppress a closure error or substitute a native binary.

Each public package has its own CI job that restores the exact package and runs a compiled Native AOT consumer against its database. The internal compatibility suite also exercises combined graphs. See [packaging](guides/packaging-domain.md) for reproducing those checks.
