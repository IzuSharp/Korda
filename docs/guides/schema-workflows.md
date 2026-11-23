# Reviewable schema workflows

The standalone `Korda.Database.PostgreSql` and `Korda.Database.Sqlite` packages include the static C# mapping generator and the C++20 database engine. The same generated table metadata used for queries and writes can produce a migration draft. Existing catalog metadata can produce an ordinary C# entity that the application reviews and compiles.

Neither operation synchronizes a database on startup. Drafting compares immutable metadata without opening a connection. Scaffolding emits source without native loading or database changes. Applying SQL and compiling the emitted source are separate, explicit steps.

## From models to reviewed SQL

Build the desired snapshot from generated or manually declared tables. Keep the previous applied snapshot in source control alongside the complete migration history:

```csharp
using Korda.Database;

var current = DatabaseModel.Empty
    .Add(Customers.Table)
    .Add(Orders.Table)
    .Add(OrderLines.Table);
var previous = DatabaseModel.Parse(previousSnapshotJson);
var draft = await source.Administration.DraftMigrationAsync(previous, current, token);

foreach (var change in draft.Changes)
{
    Console.WriteLine($"{change.Id} {change.Kind} destructive={change.IsDestructive}");
    Console.WriteLine(change.Sql);
}
```

Use `DatabaseModel.Empty` as the previous snapshot for a new database. `ToJson()` persists a versioned snapshot; `Parse()` reads it without reflection or database access. Adding a table returns another snapshot and does not mutate the previous one. Native compilation checks provider names, codec policies and finite input/result limits.

The supported difference is deliberately small:

| Difference | Result |
| --- | --- |
| New table | Explicit columns and primary key, using the provider's scalar storage types |
| Sole generated integer primary key on a new table | PostgreSQL identity or SQLite `INTEGER PRIMARY KEY AUTOINCREMENT` |
| New nullable, nongenerated, nonkey column | `ALTER TABLE ... ADD COLUMN` |
| Removed nonkey column | Destructive `DROP COLUMN`, requiring that change's exact review ID |
| Removed table | Destructive `DROP TABLE`, requiring that change's exact review ID |
| Existing type, nullability, key, generated flag, collation or length-policy change | Rejected with the table/column needing an explicit handwritten migration |
| Required/generated/key column addition, key-column removal, unknown generated expression, backfill or conversion | Rejected; no data repair or provider rebuild is guessed |

The snapshot describes the declared model. It does not infer indexes, foreign keys, server defaults, CHECK expressions or database state. A custom schema must already exist. If the database drifted from the recorded snapshot, inspect it before deployment.

Every change contains deterministic native SQL and a stable review ID for that operation. `MigrationDraft.ToMigration` rejects an empty draft, unknown/repeated approvals, and any destructive change whose ID is missing. Approval is an individual developer decision:

```csharp
// Populate only with exact destructive IDs chosen during review.
// An empty list is sufficient when every change is nondestructive.
string[] approvedDestructiveIds = [];
var next = draft.ToMigration(3, "Reviewed order schema change", approvedDestructiveIds);
var bundle = new MigrationBundle(source.Provider, [.. previouslyAppliedMigrations, next]);
await source.Administration.ValidateMigrationsAsync(bundle, token);
var report = await source.Administration.MigrateAsync(bundle, cancellationToken: token);

// Persist current.ToJson() as applied only after confirmed deployment.
```

All changes in the draft are included. To omit one, revise the desired model and generate another draft. A rename can appear as creation plus destructive removal; it does not copy data. Use reviewed handwritten SQL when data must be preserved or transformed. Keep previously applied migrations in the bundle, preserve their exact SQL bytes, and assign a new increasing version. The existing [migration and recovery guide](data-migrations.md) explains history, locks, cancellation and partial nontransactional PostgreSQL effects.

The [order/customer/detail consumer](../../samples/api/orm-experience/) generates a new schema, applies its reviewed creation migration and inspects an unapproved drop rejection. Its provider-specific Native AOT runner also compiles source generated from the live catalog.

## From an existing table to an entity

`GetTablesAsync` reads a bounded native catalog snapshot. `SchemaScaffolder.Generate` produces attributed source with exact store names and nullable members:

```csharp
var table = (await source.Administration.GetTablesAsync(cancellationToken: token))
    .Single(table => table.Name == "customers");
var result = SchemaScaffolder.Generate(source.Provider, table,
    new SchemaScaffoldOptions("Application.Models", "CustomerFromDatabase")
    {
        GeneratedColumns = [] // Explicitly reviewed: no generated columns here.
    });
File.WriteAllText("CustomerFromDatabase.cs", result.Source);
foreach (var note in result.Notes) Console.WriteLine(note);
```

Include the emitted file in the application project and compile it with the provider package's generator. It is an ordinary class; its generated descriptor is `CustomerFromDatabaseMapping`. Review and keep the source in version control before using it.

Catalog v1 cannot reconstruct a complete application model. `GeneratedColumns` is mandatory, including an explicit empty list when none are generated. A composite key requires the complete `PrimaryKeyOrder`. Defaults, indexes, relationships and generated expressions are retained as limitations in `Notes`; they are not silently recreated. Nullable catalog primary keys reject because static entity keys must be nonnullable.

Provider types with a clear CLR interpretation are emitted directly. SQLite `INTEGER`, `TEXT`, `BLOB` and `REAL` map to `long`, `string`, `byte[]` and `double`; those storage classes alone cannot identify custom IDs, money, JSON or dates. PostgreSQL numeric/JSON, typeless SQLite columns and other ambiguous types require a reviewed hint. Reuse the same configured codec member as application mapping:

```csharp
var options = new SchemaScaffoldOptions("Application.Models", "OrderFromDatabase")
{
    PrimaryKeyOrder = ["TenantId", "Id"],
    GeneratedColumns = [],
    ColumnHints =
    [
        new("Id", "Application.OrderId", "Application.OrderCodecs", "Id"),
        new("Total", "decimal", "Application.OrderCodecs", "Money"),
        new("Settings", "Application.OrderSettings", "Application.OrderCodecs", "Settings")
    ]
};
```

A hint can supply `PropertyName` when a catalog name should map to a different C# member. The scaffold rejects invalid namespace/type/member names and collisions, and escapes store identifiers as C# string literals. User-supplied codec and type symbols are checked by the consuming compiler. Successful text generation does not establish that those symbols exist; compile and execute the generated entity against the selected provider.

## Limits and verification

Model/table counts, encoded input, output changes and UTF-8 fields have finite source limits. Drafting unsupported differences returns the relevant table/column after the native operation and buffer retire. Cancellation retains the operation until retirement. No provider SQL executes during model drafting or pure scaffolding.

The migration component suite covers immutable/Unicode snapshots, stable review IDs, destructive selection, rejected alterations, bounded failures, scaffold ambiguity and zero drafting SQL dispatches. Existing administration tests cover real SQLite/PostgreSQL history, locks, rollback, checksum mismatch and cancellation. The external package consumer verifies actual creation SQL, live-catalog entity compilation and business results with Linux x64 Native AOT.
