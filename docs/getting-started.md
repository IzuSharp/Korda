# Use the database from ordinary C#

The accepted database product targets Linux x64 and .NET11, with JIT and Native AOT. Choose `NativeWeave.Database.Sqlite`, `NativeWeave.Database.PostgreSql`, or both. These are provider entry points for one database product; their shared components are restored automatically. No NativeWeave server or protocol package is required.

## Restore outside the source tree

The authorized public release uses provider version `0.1.0-db-cpp.1.2`; shared dependencies are pinned by the provider package. [Publication receipts](releasing.md) distinguish an actual NuGet delivery from a candidate-only build. Once that release completes, ordinary consumers restore directly from `https://api.nuget.org/v3/index.json`; they do not download CI archives or build NativeWeave's source.

Create an ordinary executable project outside the NativeWeave checkout:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="NativeWeave.Database.Sqlite" Version="[0.1.0-db-cpp.1.2]" />
  </ItemGroup>
</Project>
```

Use nuget.org as the package source. Add the PostgreSQL provider PackageReference at the same exact provider version when needed. No separate manual installation of Core, ABI or native leaf packages is required.

Maintainers testing an unpublished candidate can still use the local feed at `artifacts/db-scope/prepared/candidate/feed`, with `NativeWeave.*` mapped to that exact feed in their local NuGet.Config. The candidate manifest supplies that build's versions. This development path does not publish packages.

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
