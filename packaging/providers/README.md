# $package

$description

```sh
dotnet add package $package --version $version
```

Includes the ORM, query generator and precompiled database provider. See the [getting started guide](https://github.com/IzuSharp/Korda/blob/main/docs/getting-started.md).

## Define a model and DTO

```csharp
using Korda.Data;

[DbTable("orders", DescriptorName = "Orders")]
public sealed record Order([property: DbKey] long Id, string? Note);

[DbProjection(DescriptorName = "OrderCards")]
public sealed record OrderCard(long Id, string? Note);
```

Database annotations use `Db` names. Assembly-level `[assembly: DbCodec(...)]` registers a shared conversion policy; `[DbUseCodec(...)]` selects a policy for one property. JSON policies continue to use `System.Text.Json` source-generated metadata. See the [model and codec guide](https://github.com/IzuSharp/Korda/blob/main/docs/guides/query-generator.md) and the [annotation upgrade guide](https://github.com/IzuSharp/Korda/blob/main/docs/guides/db-attribute-upgrade.md).
