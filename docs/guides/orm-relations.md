# Explicit relations, writes and optimistic concurrency

Use the `NativeWeave.Database` facade with `NativeWeave.Database.Sqlite` or
`NativeWeave.Database.PostgreSql`. `NativeWeave.Data` supplies immutable table,
column, key and value contracts; `NativeWeave.Query` supplies typed native query
builders. B14 is part of that facade and its provider assets, with no separate ORM
package to register. The provider's maintainer-built native asset contains
SeaQuery, SQLx and the bounded database Host. Adding a C# entity, DTO, descriptor
or materializer reuses that asset.

See the [database Host guide](data-nativehost.md) for source creation, provider
configuration, package installation and disposal. The runnable
[minimal consumer](../../samples/api/orm-relations/Program.cs) and
[advanced consumer](../../samples/api/orm-relations/Advanced.cs) use manual static
descriptors so the generated alternative is optional. The normal facade also
supplies the static model generator. Both routes use the same immutable metadata,
codecs, native query and DTO materialization contracts.

## A complete key and an explicit changeset

Keep tenant scope in the declared composite key and pass every key component in
declaration order. A missing ordinary `FindAsync` returns null. Ordinary update or
delete returns zero when the key is absent. A declared single key matching more
than one physical row is a schema constraint failure.

```csharp
var key = Catalog.Parents.Key(Catalog.ParentTenant, tenant)
    .And(Catalog.ParentId, 1L);
var parent = await session.FindAsync(Catalog.Parents, key, cancellationToken);

var changes = new Changes<Parent>()
    .Set(Catalog.ParentName, (string?)null)
    .Set(Catalog.ParentVersion, 2L);
await session.UpdateCheckedAsync(Catalog.Parents, key, changes,
    new ConcurrencyCheck<Parent>().Expect(Catalog.ParentVersion, 1L),
    cancellationToken);
```

An omitted or `Unset` column is absent from the native assignment list. `Set` with
a nullable null binds SQL NULL. Other values pass the declared codec. Changes are
snapshotted before dispatch; later changes to the builder cannot change accepted
work. Key and generated columns cannot appear in assignments.

`UpdateCheckedAsync` and `DeleteCheckedAsync` send the complete key and every
declared expected concurrency token in one native predicate. A zero-row match
throws `ConcurrencyConflictException` with `ExpectedRows=1` and `ActualRows=0`.
The caller chooses the new version through `Changes`; there is no implicit
increment or write retry. A duplicate declared key produces a constraint error,
including on a checked operation. These are distinct from encoding failures,
missing ordinary results and an unknown commit outcome.

`InsertAsync` copies all required nongenerated columns. Generated-only records
use native `DEFAULT VALUES`. `InsertReturningAsync` validates the complete ordered
return projection and its codecs before writing, then materializes exactly one
owned row. It does not modify the input object.

`UpsertAsync` accepts either an ordered column conflict target or a PostgreSQL
constraint name. The insert entity and conflict-update `Changes` are independent
snapshots. An omitted update column keeps its stored value; a null update is still
an explicit assignment. The actual database must have the matching unique
constraint. SQLite rejects a named PostgreSQL constraint target before dispatch.

```csharp
await session.UpsertAsync(Catalog.Parents,
    new Parent(tenant, 1, "insert value", 1),
    ConflictTarget<Parent>.Columns(Catalog.ParentTenant).And(Catalog.ParentId),
    new Changes<Parent>().Set(Catalog.ParentName, "conflict update value"),
    cancellationToken);
```

## Relationship loading and foreign-key changes

Create a relationship after both tables have been built. Its ordered column pairs
describe the complete parent key and compatible child foreign-key codecs. This
does not mutate either table's metadata hash.

```csharp
var relation = Relation<Parent, Child>
    .CreateOptional(Catalog.Parents, Catalog.ParentTenant,
                    Catalog.Children, Catalog.ChildTenant)
    .AndOptional(Catalog.ParentId, Catalog.ChildParentId);
var children = await session.LoadRelatedAsync(key, relation, cancellationToken);
```

Each load copies the exact parent key and issues one native child SELECT. The child
table's `Row` projection controls ordered aliases, nullability and materialization.
The result is an owned list bounded by source row and byte limits. Any null child
foreign-key component matches no parent. The loader does not query parent
existence, impose ordering or add a tenant filter. When tenant isolation belongs
to the relation, include it in its key pairs. For additional predicates, ordering,
aggregation or streaming, use an explicit Fluent query.

Link or unlink a child with an ordinary typed `UpdateAsync` or checked update on
its foreign-key columns. Assigning nullable null explicitly unlinks that component.
The advanced consumer demonstrates changing both parts of a composite link.
Database foreign-key constraints remain authoritative.

With an explicit LEFT JOIN, use `HasMatch` or the joined key presence marker to
distinguish an absent child. A nullable business column can be null on a present
row. Count and sum keep their declared SQL null/cardinality semantics; relation
loading does not hide duplicates, deduplicate parent requests or track entities.

The source interns relations by its generation and canonical description hash,
then compares full identity bytes. The identity includes both model hashes/table
IDs, ordered column pairs and child null flags. Native registration repeats these
checks and shares the existing finite metadata registry count/byte budgets.
Concurrent identical registrations reuse one retained native description while
each successful ABI call still owns its returned wrapper. Source closure drops
cache roots before waiting for accepted cursor cleanup. Accepted operations retain
their model/projection roots until the cursor and callbacks retire.

## Bounded bulk writes

`InsertManyAsync` enumerates once and snapshots each entity before asking for the
next one. `BulkOptions.BatchSize` defaults to 256; source row, parameter, query-node
and encoded-byte limits can lower it. A row that cannot fit fails as a resource
limit. Generated-only inserts use one row per native batch.

| Policy | Transaction owner | Successful result | Failure cleanup |
| --- | --- | --- | --- |
| `All`, no caller transaction | The operation | One confirmed commit; `CommittedByOperation=true` | Roll back the owned transaction with an independent cleanup token |
| `All`, caller transaction | The caller; operation owns one tracked savepoint | Savepoint released; `CommittedByOperation=false` | Roll back and release that savepoint; keep a recoverable outer transaction active |
| `PerBatch`, no caller transaction | One owned transaction per batch | Each complete batch confirmed; `CommittedByOperation=true` | Preserve previous committed batches; clean up only the current transaction |
| `PerBatch`, caller transaction | Unsupported ownership combination | Rejected before input enumeration | Caller state is unchanged |

The bulk operation exclusively reserves its session even while awaiting application
input between batches. Other commands and a second bulk fail with `ConcurrentUse`.
Its transaction also rejects outside commit, rollback and savepoint actions during
that interval. An explicit internal lease authorizes only this bulk's control
operations; no ambient context changes unrelated calls. Source/session/transaction
closure can still request terminal cleanup. Success, input failure and cancellation
release the reservation after required cleanup.

Input enumeration and disposal are part of the operation. Whole-operation commit
happens only after input disposal succeeds. Every admitted failure keeps its
original exception and attaches an immutable `BulkProgress` under the defined
exception-data key. `BulkFailure.TryGetProgress` inspects only the supplied
exception; it does not search nested exceptions or manufacture progress for
ordinary writes.

```csharp
try
{
    var result = await session.InsertManyAsync(Catalog.Parents, input,
        new BulkOptions { BatchSize = 64, Atomicity = BulkAtomicity.All },
        cancellationToken);
}
catch (Exception error) when (BulkFailure.TryGetProgress(error, out var progress))
{
    Console.Error.WriteLine($"Completed batches: {progress.CompletedBatches}; " +
        $"acknowledged rows: {progress.RowsAffectedInCompletedBatches}; " +
        $"outcome: {progress.CurrentTransactionOutcome}");
    throw;
}
```

Completed-batch counts are historical acknowledgements. Under `All`, those rows
may subsequently be rolled back. Only confirmed preceding `PerBatch` commits set
`CompletedBatchesCommitted=true`; an incomplete current batch is excluded. An
unknown commit remains `CommitOutcomeUnknownException` and `Unknown`, retaining
recovery identity. Rollback failures preserve the primary exception and attach
cleanup detail. Neither cancellation nor affected-row counts prove a commit or
safe replay.

## Native boundary, lifetime and cost

The C# layer copies typed descriptors and values; SeaQuery builds CRUD, relation
predicates and dialect SQL in Rust. Native mutation kinds 0 through 6 validate
their exact required/forbidden control fields before admission. Upsert kind 6
uses the independent field 8 update payload. SQLx receives parameters through the
existing single B07 codec decoder, preserving typed NULL and provider semantics.

Each entity snapshot owns per-cell bytes. Each finite batch assembles a canonical
db-wire value record and control record. The native Created call validates and
copies borrowed input before returning; native SeaQuery parameter ordering then
produces the provider's owned argument record. Input enumerators cannot keep an
unbounded backlog inside the bulk operation. One batch pays one mutation creation,
start and count-take path plus retirement; transaction/savepoint control adds its
own operations according to the table above. Relation results also pay bounded
fetches and static DTO copies. No zero-copy or performance advantage is claimed
without a workload benchmark.

For native bulk failures, the existing owned error buffer contains its self-sized
canonical kind 7 diagnostic followed by one self-sized canonical kind 15 progress
record. There is no added enclosing record or export. The adapter rejects duplicate,
truncated or unknown trailing records and preserves primary/cleanup information.
Ordinary failures continue to contain only kind 7. The managed bulk layer combines
the current native failure with its acknowledged preceding-batch history.

Dispose sources, sessions, transactions and enumerators explicitly. Public Tasks
join A07 retirement, and each result batch is materialized before releasing its
native buffer. A materializer/decode exception or cancellation closes the cursor
and releases retained models/projections in dependency order. No lazy proxy,
identity map, graph save operation or dynamic SeaORM eager loader is introduced.

## Verification and contract sources

The Linux component suite in `tests/Orm.Relations` executes the actual separate
SQLite and PostgreSQL Host assets in ordinary managed and `linux-x64` Native AOT
consumers. It covers native LEFT JOIN/count/sum, composite nullable keys, generated
and manual metadata parity, tenant CAS races, exact mutation inventories, bounded
cache registration, mapping/cancellation cleanup, savepoint rollback, partial
commits, actual PostgreSQL connection-loss/unknown-commit behavior and paused-input
session exclusivity. Managed retirement and six native resource counters are
observed independently. Executable/native/source identities and actual results
belong to the immutable task receipt; this guide does not claim a new benchmark,
Windows acceptance or the final A16/B24 pristine package gate.

The exact public contract is pinned to Design commit
`c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9`:
[database API](https://github.com/IzuSharp/NativeWeave-Design/blob/c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9/docs/api/database.md),
[query semantics](https://github.com/IzuSharp/NativeWeave-Design/blob/c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9/docs/database/query-semantics.md),
and [public declarations](https://github.com/IzuSharp/NativeWeave-Design/blob/c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9/experiments/public-api/NativeWeave.Api.Design/Database.cs).
Generated [Database reference](../../src/Orm.Relations/reference/database.md)
and [Data reference](../../src/Orm.Relations/reference/data.md) are checked against
that exact inventory and actual compiled XML documentation.
