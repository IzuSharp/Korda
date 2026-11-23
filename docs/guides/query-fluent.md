# Strongly typed native queries

> **DB-CPP-01 implementation update.** The public C# examples, signatures and ownership contracts in this guide remain the compatibility inputs. The current maintained native implementation is C++20; its module mapping, direct libpq/sqlite3 execution, Clang/CMake commands and package production are documented in [C++ native implementation](cpp-native.md). Descriptions below of Rust, SQLx, SeaQuery or Tokio internals are retained as the fixed `465cd0f` behavioral reference, not current build dependencies. The named verification entry points now build the ported C++ fixtures and retain their original managed assertions.

`Korda.Query` is the application query surface bundled into each standalone
`Korda.Database.Sqlite` or `Korda.Database.PostgreSql` package. A C# call
creates or changes a C++ native query owner immediately. C# retains typed columns, codecs and owner tokens;
it does not collect a SQL AST, compile expression trees or execute a managed
LINQ database engine. Ordinary entity and DTO changes use the same precompiled
provider module.

## Install, describe a model, execute

Reference `Korda.Database.Sqlite` or `Korda.Database.PostgreSql` independently.
Each package bundles its managed components, generator and provider asset. Import `Korda.Data`,
`Korda.Database`, the selected provider namespace and
`Korda.Query`. Use either explicit `TableBuilder<TEntity>` metadata or
B10's optional generated descriptor; both produce the same typed table/column
and static row contracts.

```csharp
await using var source = SqliteDataSource.Create("application.db");
await using var session = await source.OpenSessionAsync();
using var query = session.From(People.Table);
var rows = await query
    .WhereGreaterThanOrEqual(People.Id, 1L)
    .OrderBy(People.Id)
    .Take(100)
    .SelectAll()
    .ToListAsync(cancellationToken);
```

`People.Table` and `People.Id` in the complete
[`samples/api/query-fluent`](../../samples/api/query-fluent/README.md) consumer
are ordinary handwritten static descriptors. That consumer creates isolated
sample tables and executes both providers using normal PackageReferences. It
also contains grouping, explicit DTOs, window snapshots, CTEs, sets, parameters,
locking and pagination. Current Linux checks publish and directly execute Native
AOT consumers without a native compiler or a source-project reference in the
consumer project.

## Values and callbacks have a precise snapshot point

`WhereEqual(column, value)`, scalar comparisons, `Value`, arithmetic values and
text-pattern values copy their inputs before returning. `Where(row => ...)`
invokes the ordinary delegate synchronously once. It does not inspect the
lambda's syntax or retain it for execution. `WhereIf(false, callback)` does not
invoke that callback and binds none of its captured values.

```csharp
long minimum = 1;
query.Where(row => row.GreaterThanOrEqual(People.Id, minimum));
minimum = 99; // The already-created native predicate still compares with 1.
```

Use `IsNull`, `IsNotNull` and the nullable codecs for SQL NULL. Equality,
ordering and Boolean conditions use SQL three-valued logic. `ContainsLiteral`,
`StartsWithLiteral` and `EndsWithLiteral` escape SQL pattern metacharacters;
`Like` deliberately accepts a pattern. `Collate` accepts only the provider's
registered collation allowlist. Values and diagnostic SQL never become a route
for injecting identifiers or SQL fragments.

## Ownership, selection and concurrency

A `Query<TEntity>` is one disposable, session-bound mutable arena. Composition
is synchronous and fails immediately when another thread owns its admission.
The first terminal or `Prepare` seals the owner before asynchronous work begins.
Further composition or direct execution rejects; call `Clone` while still
mutable if a second independent query is required. Cloning is explicit native
work and old scalar/alias proxies remain associated with the original query.

A `QuerySelection<T>` is a borrowed view of the current selection. Selecting
another projection invalidates an old view. `ToSql` renders a bounded native
clone with placeholders; it does not execute or seal the original query.
`SelectAll` selects root columns only and never creates an implicit joined
object graph. An explicit `ProjectionBuilder<T>` freezes unique ordered aliases,
exact codecs/nullability, typed result tokens and a direct DTO constructor.

`AsAsyncEnumerable` seals immediately and dispatches lazily on its first
`MoveNextAsync`. It is one-shot. Keep the query alive until enumeration ends and
dispose the enumerator on early exit. Query disposal prevents further admission,
requests active cursor stop, and releases its arena/plan references. Native
children retain their own accepted-operation references. The publication path
also releases a newly sealed plan when disposal wins the race before that plan
is stored in the facade.

`ToListAsync` and `ToArrayAsync` enforce source buffered row/encoded-byte budgets;
zero buffered demand rejects without silently switching to streaming. First
reads at most one row, Single at most two, while preserving a smaller existing
`Take`. Their default variants return `default(T)` on absence, so use `Single`
when an absent value must differ from a legitimate zero/default value. Mapping,
cardinality and cancellation failures release current batches and await actual
native retirement before completing the task.

## Explicit joins and SQL nullability

```csharp
var note = query.LeftJoin(Notes.Table, "note",
    on => on.Left(People.Id).AsNullable().Equal(on.Right(Notes.PersonId)));
var id = query.NullableValue(note, Notes.Id);
```

`AsNullable` changes the native result metadata without changing its SQL value
or scalar codec. `AsNullableReference` is the corresponding one-way reference
lift. There is no reverse lift or sentinel replacement. A parent key of zero
cannot match a NULL child foreign key. LEFT columns require nullable read APIs;
`HasMatch` uses an explicit native match marker independently of nullable child
data.

The JOIN callback is scoped to that call. Both `Left` and `Right` reject after
it returns or throws; provisional alias expressions also expire. Failed
callbacks abort the reserved join and preserve query usability. Inner and LEFT
aliases are distinct types and carry exact table and query association.

## Ordering and advanced operations

`OrderBy` replaces ordering, while `ThenBy` requires an existing order and
appends. Both providers receive explicit direction and NULL placement. Offset
paging requires an order with all declared primary-key columns providing the
unique tie-breaker. A later replacement that removes uniqueness rejects before
execution. The original `SeekAfter(column, value)` overload requires the sole
non-null primary key ordered ascending and no offset. It appends its comparison
without removing existing security/tenant predicates.

For a composite key or a nonkey sort followed by a unique key, pass an immutable
`QueryCursor<TEntity>`. For example, the relation sample's tenant and parent ID
form a complete key:

```csharp
var cursor = QueryCursor<Parent>.Create(Catalog.ParentTenant, tenant)
    .Then(Catalog.ParentId, lastParentId);
using var page = session.From(Catalog.Parents);
var rows = await page.OrderBy(Catalog.ParentTenant)
    .ThenBy(Catalog.ParentId)
    .SeekAfter(cursor)
    .Take(50)
    .SelectAll()
    .ToListAsync(cancellationToken);
```

The cursor must name every ordering column in the same sequence, and that
sequence must contain the complete root primary key. Each value is typed and
copied at `Create` or `Then`. Mixed ascending and descending directions follow
the query's `OrderBy`/`ThenBy`; the strict native comparison excludes the cursor
row and handles ties using the later keys. Nullable columns, foreign metadata,
repeated columns, incomplete keys and conflicting offsets reject. After applying
this overload, changing the order or adding an offset also rejects, including
on a clone. Existing filters and `Take` remain in force.

An empty result is the end of the page sequence. The cursor does not create a
snapshot across requests: updates to sort values can move rows between pages.
Keep one explicit transaction when a consistent database snapshot is required,
and choose a supported isolation policy. A complete root key also cannot make
one-to-many join output unique; page root entities before batch-loading children.

`GroupBy` and `Having` operate natively. Nonaggregated selected expressions must
be grouping keys; the native adapter validates their actual SeaQuery expression
identity even across clones. COUNT is Int64. SUM/AVG/MIN/MAX preserve empty-input
NULL and the specified widening: integral SUM becomes checked Int64, integral
AVG becomes Double, and decimal profiles retain their precision/scale contract.
Unsupported SQLite DecimalText aggregates fail explicitly.

`WindowDefinition` holds finite native inline-window state. Rank, RowNumber and
aggregate Over snapshot it at the call; later mutations do not change an already
created expression. Set operations clone a same-source right operand with exact
ordered projection shape. `CommonTable<T>.Create` freezes a selected body
against an explicit logical row/table name. Recursive mode requires the
validated anchor/member UNION with one declared self-reference. It does not
accept arbitrary cyclic or nested recursive expressions.

PostgreSQL row locks require an explicit active transaction and the advertised
`db.row-lock.v1` capability. SQLite rejects before dispatch. Filtered native
UPDATE and DELETE are explicit set-based operations; unfiltered or unsupported
joined/grouped/paged mutation shapes reject. Typed setters are valid only in
the synchronous callback, and Changes snapshots are copied before submission.
No entity tracking, relation fix-up or implicit optimistic version policy is
introduced here; B14 owns explicit relation/concurrency operations.

## Reusable immutable plans

```csharp
using var query = session.From(People.Table);
var minimum = query.Parameter("minimum", Codecs.Int64);
query.Where(query.Column(People.Id).GreaterThanOrEqual(query.ParameterValue(minimum)));
using var prepared = query.SelectAll().Prepare();
var parameters = new QueryParameters().Set(minimum, 1L);
var first = prepared.ToListAsync(session, parameters);
parameters.Set(minimum, 2L); // Does not alter first's value snapshot.
var firstRows = await first;
var secondRows = await prepared.ToListAsync(session, parameters);
```

A parameter ID belongs to a datasource generation, not an expression arena.
Every execution snapshots the complete declared set before its first await.
Missing, extra, duplicate-name conflicts, wrong-source or mismatched codecs
reject. Reusing a CLR type does not make a foreign source valid. Prepared plans
are immutable and concurrently executable with separate sessions from their
source; each execution has its own parameter values and cursor. Disposing a
prepared plan prevents new dispatch and does not abandon accepted operations.

## Errors and cancellation

Bad arguments, wrong owner associations and invalid state reject before native
execution. Unavailable named capabilities throw `DatabaseCapabilityException`;
unsupported native operator/codec/shape combinations retain
`DatabaseErrorKind.UnsupportedCapability`. Provider diagnostics retain bounded
correlation identity. A user DTO constructor's original exception remains
observable after cleanup. Cancellation uses the supplied token and requests
native stop; it does not claim a connection is reusable before its retirement
receipt. `WithTimeout` snapshots a finite total execution/cursor deadline; zero
expires before dispatch, with the normal cleanup path.

## Boundary costs and verification

These calls allocate, encode and cross the ABI. The component records actual
managed allocation bytes and elapsed Stopwatch ticks for 64 warmed synchronous
composition/render/dispose iterations per provider. It also records the exact
wire size for one Int64 literal. Those observations describe the tested process,
not a comparative performance result or a whole-process memory bound. B23 owns
comparative benchmarks.

The measured source path is `From(...).WhereEqual(...).Take(1).SelectAll().ToSql()`
with four selected root columns. Inspection yields the following direct C ABI
call count after model/codec registration is warm; the count is structural,
not a sampled runtime counter:

| Part | Direct C ABI calls |
| --- | ---: |
| Create the query owner | 1 |
| Column, copied literal, comparison, WHERE | 4 |
| LIMIT | 1 |
| Four root scalar tokens and SELECT descriptor | 5 |
| Render, view and release returned SQL buffer | 3 |
| Release query owner | 1 |
| Total per recorded iteration | 15 |

The Int64 codec writes an eight-byte managed value; the bounded wire encoder
copies it into its recorded envelope. B09 retains the complete admitted wire
snapshot, while B11 retains its owned scalar cell and SeaQuery token. Control
records and native expression clones have additional bounded allocation costs.
Rendering copies the SQL result to an owned CLR string. No batching or
zero-overhead claim is made.

The Linux evidence includes exact pinned public API/nullability/generic and XML
reference checks; typed negative consumers; actual PostgreSQL18/SQLite SQL
paths; native operation/cursor/buffer counters after each group and all six
owner counters after shutdown; deterministic plan-disposal and JOIN-scope
regressions; actual Native AOT execution; isolated normal NuGet consumption;
and exact source, lock, native, package and executable hashes. The optional
B10 generator's consumer is verified separately against the same facade.
Windows and final pristine release consumption remain their own platform/task
rows.
