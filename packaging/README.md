# Korda database packages

Choose `Korda.Database.Sqlite` for a SQLite database or `Korda.Database.PostgreSql` for a PostgreSQL server. Each provider includes the strongly typed C# Fluent ORM, static model and DTO support, and the selected precompiled native driver. Referencing both providers is supported.

Publish a .NET 11 application for Linux x64 with `dotnet publish -c Release -r linux-x64 -p:PublishAot=true`. A normal application build uses the package assets directly.

See [getting started](https://github.com/IzuSharp/Korda/blob/main/docs/getting-started.md), the [package guide](https://github.com/IzuSharp/Korda/blob/main/docs/guides/packaging-domain.md) and the [security policy](https://github.com/IzuSharp/Korda/blob/main/SECURITY.md).
