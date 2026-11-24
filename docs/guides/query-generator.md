# Static models, DTOs and reusable codecs

Each independent database provider package includes the C# source generator. It
turns ordinary classes and records into static table metadata, typed column
tokens and direct row constructors. SQL composition and execution remain in the
existing C++20 engine, with libpq for PostgreSQL and sqlite3 for SQLite. Changing
an entity or DTO rebuilds C# only; the generator performs no database, network or
native compiler operation.

The [order business sample](../../samples/api/orm-experience/) combines these
features with joins, cursor paging, batched relations and checked writes. The
[earlier generator sample](../../samples/api/query-generator/) retains the
equivalent handwritten mapping path.

All database annotations use the `Db` prefix in `Korda.Data`. See the
[upgrade guide](db-attribute-upgrade.md) for the complete rename and compatibility
requirements.

## Conventions are explicit

```csharp
[DbTable]
public sealed record Customer(long Id, string Name, string? Email);
```

This generates `CustomerMapping.Table` for the exact table name `Customer`, the
three typed columns and `CustomerMapping.Row`. The parameterless attribute
infers a unique nonnullable `Id` property as the primary key. It does not
pluralize names, change casing or infer generated values. Explicit `[DbKey]`
properties take precedence and support composite keys. Use
`[DbTable(InferPrimaryKey = false)]` for a keyless model.

Existing named-table declarations preserve their behavior. To opt a named table
into key inference, write:

```csharp
[DbTable("customers", DescriptorName = "Customers", InferPrimaryKey = true)]
public sealed class Customer
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string? Email { get; init; }
}
```

`[DbColumn("store_name")]` supplies an exact column name;
`[DbColumn("id", Generated = true)]` declares an insert-generated column.
`[DbVersion]` marks an optimistic concurrency token without incrementing it.
`[DbIgnore]` excludes a property from both reader and writer. No attribute
requires the entity to inherit a framework base class or be partial.

## Select only the DTO's columns

Use `[DbProjection]` when a DTO has no table identity. Its generated descriptor
exposes `Row` for parameterized SQL and `Bind` for Fluent expressions. A binder's
arguments have the exact types and names of the DTO's mapped properties.

Add this DTO in `OrderRow.cs` beside the [order sample's models and codec
registrations](../../samples/api/orm-experience/Models.cs):

```csharp
using Korda.Data;

[DbProjection(DescriptorName = "OrderRows")]
public sealed record OrderRow(
    [property: DbColumn("order_id")] OrderId Id,
    [property: DbColumn("customer_name")] string? Customer,
    decimal Total);
```

Add the query method in a separate `OrderQueries.cs` file. It uses the sample's
`Orders` and `Customers` descriptors and the supplied session:

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Korda.Database;
using Korda.Query;

internal static class OrderQueries
{
    internal static async Task<IReadOnlyList<OrderRow>> ReadAsync(
        DataSession session, CancellationToken cancellationToken)
    {
        using var query = session.From(Orders.Table);
        var customer = query.LeftJoin(Customers.Table, "customer",
            on => on.Left(Orders.CustomerId).Equal(on.Right(Customers.Id).AsNullable()));
        var projection = OrderRows.Bind(query,
            Id: query.Column(Orders.Id),
            Customer: query.NullableReference(customer, Customers.Name),
            Total: query.Column(Orders.Total));
        return await query.OrderBy(Orders.TenantId).OrderBy(Orders.Id).Take(20)
            .Select(projection).ToListAsync(cancellationToken);
    }
}
```

The query selects those three expressions. Other entity fields, including large
JSON values or private application notes, are not selected. A computed or joined
expression can fill any compatible argument. Repeated store column names are
disambiguated by the DTO's explicit output aliases. Duplicate aliases are
compilation errors. Expression type errors are C# compilation errors;
cross-query ownership and configured codec differences reject during binding.

The same DTO and codec policy work with parameterized SQL. The SQL must return
the exact declared aliases in constructor order:

```csharp
var parameters = new SqlParameters().Add("id", orderId, OrderCodecs.Id);
var command = SqlCommand.Create("""
    SELECT o."Id" AS order_id, c."Name" AS customer_name, o."Total" AS "Total"
    FROM orders AS o LEFT JOIN customers AS c ON c."Id" = o."CustomerId"
    WHERE o."Id" = @id
    """, parameters);
var fromSql = await session.QueryAsync(command, OrderRows.Row, cancellationToken);
```

Projection fields follow the chosen constructor's parameter order, then ordinal
CLR property-name order for remaining writable properties. SQL NULL requires a
nullable member. Left joins use `NullableValue` or `NullableReference`; a missing
row is not replaced with a default scalar sentinel. `Bind` retains the existing
query ownership rules, so keep the query alive until execution and disposal end.

## Choose a constructor or parameter association

Classes and records can use one accessible constructor covering their mapped
properties, plus accessible set/init assignment for remaining members. When
several constructors qualify, select one explicitly:

```csharp
[DbProjection]
public sealed class OrderLabel
{
    public long Id { get; init; }
    public string? Label { get; init; }

    public OrderLabel() { }

    [DbConstructor]
    public OrderLabel([DbParameter(nameof(Id))] long key, string? label)
    {
        Id = key;
        Label = label;
    }
}
```

`[DbParameter]` names the CLR property, while `[DbColumn]` names the database
column or result alias. Duplicate constructor inputs, missing associations,
inaccessible constructors and nullable-to-nonnullable assignments fail at
compilation. Models must be concrete, closed top-level classes or records without
a model base class. Ignored required members and required fields cannot be
silently left unset.

## Declare custom ID, decimal and JSON policies once

Assembly-level defaults apply to every generated model and DTO in that
compilation. A default supplies an accessible static field or getter of exactly
`ValueCodec<T>` for the nonnullable base type. Generated descriptors capture the
value on static initialization; keep policy members stable and share their
immutable codec instances.

```csharp
[assembly: DbCodec(typeof(OrderId), typeof(OrderCodecs), nameof(OrderCodecs.Id))]
[assembly: DbCodec(typeof(decimal), typeof(OrderCodecs), nameof(OrderCodecs.Money))]
[assembly: DbCodec(typeof(OrderSettings), typeof(OrderCodecs), nameof(OrderCodecs.Settings))]

public readonly record struct OrderId(long Value);
public sealed record OrderSettings(string Channel, bool Gift);

public static class OrderCodecs
{
    public static ValueCodec<OrderId> Id { get; } =
        Codecs.Convert<OrderId, long>(Codecs.Int64,
            static id => id.Value, static value => new(value));
    public static ValueCodec<decimal> Money { get; } = Codecs.FixedScaleMoney(2);
    public static ValueCodec<OrderSettings> Settings { get; } =
        Codecs.Json(OrderJson.Default.OrderSettings);
}

[JsonSerializable(typeof(OrderSettings))]
internal partial class OrderJson : JsonSerializerContext { }
```

JSON requires the shown static `System.Text.Json` metadata. The generator reads
the declared codec member's type, so it does not need to discover another source
generator's output. Nullable value/reference members automatically wrap the base
policy, preserving SQL NULL separately from a JSON `null` value.

`Codecs.Convert` preserves the underlying native codec ID, configuration and
bounded encoder. It is suitable for strongly typed identifiers and other direct
application conversions. It requires a nonnullable storage codec; apply
`Codecs.Nullable` or `Codecs.NullableReference` to the result when needed.
Conversion delegates remain ordinary managed application code. Exceptions occur
during the existing snapshot/materialization stages, with their normal cleanup.

`Codecs.Decimal(18, 2)` selects exact PostgreSQL numeric precision and scale;
ordinary SQLite rejects that provider capability. `FixedScaleMoney(2)` uses an
explicit signed-Int64 representation with no rounding. `DecimalText` preserves
decimal values as text without promising numeric ordering or aggregation. Choose
a representation supported by the intended provider; no fallback silently
changes the representation. The order sample builds each provider's explicit
money policy separately.

A property can override its compilation default:

```csharp
[property: DbUseCodec(typeof(OtherCodecs), nameof(OtherCodecs.Tax))]
```

The override member follows the same nonnullable `ValueCodec<T>` rule. Reused
profiles apply to table writers, raw SQL projections and Fluent bindings.
`ProjectionBuilder.Add(expression, alias, expectedCodec)` validates the exact
scalar configuration and SQL nullability before creating a typed DTO field.

## Diagnostics, compatibility and costs

| Diagnostic | Cause and action |
| --- | --- |
| NWQ0101 | No automatic/static codec; declare a supported scalar or exact reusable policy. |
| NWQ0102 | Nullable storage conflicts with a nonnullable constructor or key. |
| NWQ0103 | Constructor selection or parameter/member mapping is ambiguous, inaccessible or incomplete. |
| NWQ0104 | Conflicting aliases, key convention, descriptor names, codec defaults or policy member types. |
| NWQ0105 | An automatic codec is absent from the explicit compile-time native codec profile. |
| NWQ0106 | Unsupported declaration shape, including open generic, nested or inherited models. |
| NWQ0107 | Mapping would require dynamic code or runtime type discovery. |

The historic NWQ0001–NWQ0016 diagnostic family is not repurposed. User-supplied
codec initializers are not interpreted to guess native capabilities; runtime
profile admission remains required. Generated source is deterministic. Unrelated
file edits reuse unchanged semantic output; relevant model or policy-symbol
changes invalidate it. Static mapping ambiguities have source diagnostics before
native code is loaded.

Static descriptors allocate managed metadata once per generated class. Each
query-bound projection allocates its finite metadata and retains native
expression references; DTO objects and owned strings/arrays have normal C#
allocation costs. Direct constructors and codec delegates require no reflection,
dynamic compilation or runtime model scan. Ordinary model and DTO changes reuse
the provider's existing native asset.

Set `<KordaExcludeGenerators>true</KordaExcludeGenerators>` to use equivalent
handwritten `TableBuilder` and `Projection` declarations. The generator remains
a compiler-host tool; actual product consumers are verified as Linux x64 Native
AOT executables. Compiler diagnostics, runtime mapping assertions and the exact
API/XML reference checks live in [the generator tests](../../tests/Query.Generator/).
The [DB-API-01 Plan record](https://github.com/IzuSharp/Plan/tree/main/ledger/evidence/batches/DB-API-01)
owns source-bound execution and package/native-reuse evidence.
