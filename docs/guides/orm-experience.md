# Order workflow with generated mappings

Start with [installation and DI](../getting-started.md), then inspect the complete
[external package consumer](../../samples/api/orm-experience/). The same sources
run independently against `Korda.Database.Sqlite` and
`Korda.Database.PostgreSql` as Linux x64 Native AOT applications.

## What this batch adds

The existing [query example](../../samples/api/query-fluent/) already provides
`WhereIf`, typed joins, aggregate expressions and `ProjectionBuilder`. The
[relation example](../../samples/api/orm-relations/) already provides keyed
lookup, explicit partial updates, checked concurrency and insert-returning.
Those operations retain their existing contracts.

| Business need | Existing operation or focused addition |
| --- | --- |
| Ordinary entities with exact names and nullable members | Existing table generator; optional `Id` convention added |
| Immutable DTO shared by Fluent and SQL | New `[WeaveProjection]` emits `Bind` and `Row` |
| Constructor names differ from property names | New `[WeaveConstructor]` and `[WeaveParameter]` |
| Repeated ID/money/JSON policy | New assembly `[WeaveCodec]`, property `[WeaveUseCodec]`, `Codecs.Convert` |
| Optional filter, join, aggregate | Existing `WhereIf`, `LeftJoin`, `GroupBy`, `Having` and typed expressions |
| Continue after a complete order tuple | New `QueryCursor<T>` and `SeekAfter` |
| Fetch all details for a page | New bounded list overload of `LoadRelatedAsync` |
| Find/patch/checked write/return inserted values | Existing `FindAsync`, `Changes<T>`, `UpdateCheckedAsync`, `InsertReturningAsync` |
| Await one business transaction | New `InTransactionAsync` and retained `TransactionFailure` snapshot |
| Review model changes or reverse an existing table | New `DatabaseModel`, `MigrationDraft` and `SchemaScaffolder` |

## Entities, aliases and codec reuse

`Models.cs` defines customers as an ordinary class and orders/details as records.
Orders use a composite tenant/order key. An assembly-level codec converts the
strong `OrderId` to Int64; another declares money policy once; a JSON codec uses
a source-generated `JsonTypeInfo<OrderSettings>`. Nullable members wrap the same
underlying policy. PostgreSQL money uses `numeric(18,2)`; SQLite uses explicit
fixed-scale integer storage. There is no implicit decimal rounding policy.

`OrderCard` declares `order_id`, `customer_name` and `score` as explicit result
aliases. The generated typed binder takes selected expressions, including the
left-joined nullable customer name and a computed score. It checks each codec's
full descriptor, including decimal/JSON configuration, against the DTO policy.
The raw SQL path uses `session.QueryAsync(command, OrderCards.Row, token)` with
the same aliases and parameter codecs. No reflection materializer is introduced.

Constructor or static codec ambiguity is reported by the compiler. SQL result
shape remains a runtime contract: missing, duplicate, wrong-type or nullability
incompatible results still fail before unsafe materialization.

## Stable pages and bounded associations

The example orders by `CreatedAt`, `TenantId`, then `Id`. Its continuation uses
the exact same complete tuple. `SeekAfter` requires nonnullable root columns,
the full declared primary key, matching directions and ordering, and no offset.
It expands to the existing typed/native comparison operations. It does not
invent a second SQL builder or compare database keys in a managed dictionary.

The next page is exclusive of the supplied last tuple. The fixture deliberately
contains equal timestamps and an order with no customer. Traversing all pages
checks both tie boundaries and the final empty page. Cursor paging does not
promise a stable snapshot while other sessions change sort keys; use suitable
transaction isolation when a business operation needs that guarantee.

`LoadRelatedAsync(pageKeys, relation, token)` returns one detail list for each
input key, in input order. Empty associations and repeated keys retain their
positions. An empty input performs no SQL; a nonempty input performs one bounded
SQL statement with a parent ordinal in each branch. Database comparison and
collation determine membership. The maximum input is `MaxBatchRows`, while the
total materialized result is bounded by `MaxBufferedRows`; there is no hidden
multi-command chunking or N+1 loading. Choose page size and limits deliberately.

## Write and transaction outcomes

The fixture finds an order by its typed composite key, clears its nullable note,
increments an explicitly supplied version, and verifies that other fields did
not change. A stale expected version fails. Insert-returning runs in a callback
whose result is observed only after commit confirmation and local retirement.

A failing callback preserves the original exception and attaches a
`TransactionFailure` observation. Cancellation before admission never invokes
the callback. After a commit attempt, uncertainty remains `Unknown` even when
local resources have been released. The API never retries the callback.
The [relationship AOT suite](../../tests/Orm.Relations/FluentExperience.cs) also
exercises in-flight cancellation and actual PostgreSQL backend loss during
commit/rollback; these failure injections are separate from the business sample.

## Review migrations and compile reverse mappings

The sample drafts the three-table schema through the native administration
operation, inspects its changes, explicitly creates a `MigrationBundle`, and
applies it to an isolated test database. A destructive draft is inspected but
never applied to the business fixture. Every destructive ID must be explicitly
approved before a migration can be materialized; unknown or repeated approvals
are errors. Unsupported transformations reject with their table/column context.

The existing customer table is read through `GetTablesAsync`. The scaffolder is
given an explicit empty generated-column list because catalog v1 does not expose
that information. Composite keys require a reviewed key order; ambiguous types
require a CLR/codec hint. After a scaffold bootstrap, the runner includes the generated customer and
composite-key order entities in both original and edited AOT compilations and
executes typed queries against them. It does
not treat generated text alone as successful reverse engineering.

## Evidence and diagnostic scope

`eng/ci/consume.py` first exercises the retained standalone package contract and
then runs `eng/ci/orm_experience.py` outside the source tree. Each business project
has exactly one Korda `PackageReference` and zero project references. The runner
checks the actual restored graph and runs a scaffold bootstrap. It then publishes
and executes a baseline containing both generated entities, edits only the DTO
source to add its version field, and publishes/executes again. All loaded
provider hashes must equal the exact `.nupkg` payload. Only `Models.cs` changes between the baseline and edited source snapshots.
The native library is never rebuilt by the consumer.

The process reports two SQL dispatches for one page plus its batched details.
`nw_db_diagnostic_count_v1` selector 6 counts SQLite statement starts or
successful PostgreSQL SQL submissions. Selector 7 separately counts successful
libpq preparation, description, query and synchronization submissions. A single
SQL statement can require several such protocol operations; neither counter
claims to measure physical network round trips. Selectors 0–5 keep their
historical meanings. Counters are module-wide and monotonic, so the test takes
deltas with no concurrent work on that loaded provider.

Receipts under `artifacts/public/consumer-evidence/{candidate,public}/` contain
business checks, executable/DTO/scaffold/native hashes, exact package versions
and the observed counters. Provider CI jobs run independently. Source changes,
API/XML checks, native tests and the broader retained regression suites remain
separate required gates under the registered module plan.
