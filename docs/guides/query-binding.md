# Direct query binding contracts

B09 supplies the internal C# calls and native validation metadata between the
strongly typed application facade and the Rust query engine. It uses the exact
Design commit recorded in `native/database/binding/signatures-v1.json`. B01 remains
the sole owner of Table, Column, Relation, Projection, codec identities and db-wire.
B11 owns SeaQuery statements and rendering; B04 owns source/session scheduling and
the Core operation lifecycle. This component is independently testable before
those modules are assembled, as required by the B09 dependency graph.

## Consumption and scope

`NativeWeave.Query.Binding` exports zero public CLR types. B10/B11/B13 and the
Database adapter use its internal direct-call descriptors. Application code keeps
using the canonical `NativeWeave.Data` types; this task does not introduce another
Table schema, public query facade, managed SQL tree or LINQ translator. The
[compiled reference](../../src/Query.Binding/api-reference.md) lists all 42 exact
Cdecl domain declarations and identifies their applicability. The
[applicability record](../../src/Query.Binding/api-applicability.json) records the
internal-only public API check.

The Rust `nativeweave-query-binding` library contains validation and retained
metadata. A successful validator call authorizes one matching native builder
operation; it does not execute SQL. B11 must apply that operation to its own
SeaQuery candidate under the same query admission. A failed backend mutation must
be rolled back completely, or the arena must be poisoned before another call.
There is no optional batched command language in this implementation.

The call table receives exports from the already negotiated creating module. The
A02 loader keeps that module resident; the table is never a global basename
resolver shared between PostgreSQL and SQLite. A missing required function fails
before invocation. This project is not independently distributed, adds no preview
package, and does not run Cargo from an ordinary managed build. Maintainers build
the actual native modules once. Final precompiled package consumption remains the
existing A16/B24 responsibility.

## Identity, ownership and admission

| Item | Representation and checks |
| --- | --- |
| Native owner | Creating-engine opaque pointer; source, model, query, plan, relation, operation and buffer kinds are distinct. No shared owner layout is published. |
| Source generation | Managed reference identity and native engine/source/generation/profile identity. Different source generations reject even when schema bytes match. |
| Arena token | Unsigned 64-bit generation plus a one-based slot; zero is invalid except the explicit root alias in `expr_column`. Slots invalidated by JOIN are never reused. |
| Expression | A same-arena token with native codec/configuration, nullability, depth, aggregate dependency and exact windowable-root metadata. C# generics do not authorize a native read. |
| Imported model/plan/CTE | A same-source immutable retained dependency. Closing the original wrapper does not invalidate it. Nodes, bytes and nested depth remain charged. |
| Sealed plan | Retains its root model, imported metadata, projection, parameters and copied values. Ordinary terminals share one atomic submission flag with their query. Immutable terminal8 permits prepared executions. |

Managed `DatabaseOwner` uses the creating module's void owner release. A synchronous
`OwnerLease` adds a SafeHandle reference for the entire call. A separate fail-fast
query gate prevents concurrent or reentrant mutation. Every owner also publishes
an atomic logical Closing state before SafeHandle disposal, checked both before
and after acquiring an access lease; a retained physical handle cannot reopen
source/session/model admission. SafeHandle refcounts alone
would not provide that guarantee. Disposal closes managed admission immediately
and physical release waits for outstanding native-access leases. B04 supplies the
real close/retirement path before final owner release; no provider I/O or blocking
executor join is added to SafeHandle.

Raw ABI callers must provide active correctly allocated owner pointers. Checking
an arbitrary freed address is outside the ABI preconditions. Default, foreign and
disposed managed references fail before native extraction. Native validation still
repeats source, generation, slot kind, codec, capability and size checks. Native
capabilities are parsed from the exact engine-local section9.3 bit assignments;
reserved bits reject. Joins, set operations, ordinary/recursive CTEs and prepared
execution each check their own bit. Internal seal8/import composition remains
usable under query capability even when public prepared execution is absent.

Mutable queries become Sealed and then Submitted for ordinary execution. The
first validated argument snapshot wins an atomic submission gate; another call
fails, including concurrent callers. Invalid argument snapshots do not consume
the gate. Closing or poisoning prevents further mutation. A shape plan created
with terminal8 is immutable and can be used for repeated prepared execution.
Clone copies mutable metadata into a new generation, preserving an optional
selection; it does not seal or consume the source.

## Direct operations and failure atomicity

The normative binary table is codes1..14; unary codes1..4 include metadata-only
nullable lifting; list codes1..3 use B01 kind3 homogeneous db-wire values. All
clause records use the existing kind6 control envelope, with exact sorted fields,
wire types, widths, ranges and required/forbidden fields. Unknown versions,
opcodes, capabilities, fields, alias kinds or reserved values reject before
publication. Output owners and tokens start at zero and transfer only on success.

The native validator covers direct WHERE/HAVING, order, group, bounded limit/offset,
projection, JOIN, DISTINCT, set imports, CTE imports, windows, provider lock
capability, aggregates, subqueries, CASE, text operations, root assignments and
finite timeout snapshots. SELECT decodes exact kind13/kind22 records back into
B01's canonical projection and verifies every expression's codec/nullability.
A single empty-alias field uses B01's exact scalar sentinel; its admitted scalar
consumer still enforces single-column/cardinality rules. Empty aliases in general
row projections remain invalid.
SUM and AVG use the pinned widening rules; configured decimal results preserve
exact configuration. Derived aggregate predicates retain their aggregate
dependency. Only an aggregate/window-capable root accepts OVER; applying OVER or
constructing an arithmetic/CASE expression cannot preserve that root permission.
NullableLift preserves the underlying root kind because it adds no SQL operator.

Each accepted operation consumes finite node/byte budget, including clauses with
no returned token. Retained plans/CTEs charge their transitive nodes and nested
depth before import. Byte views are borrowed for the synchronous call; values,
execution parameters and relation keys that outlive it are copied before return.
A failed operation leaves the existing shape and accounting unchanged. Imports
that succeeded before a later clause failure stay bounded until query disposal,
as specified by the direct clone/seal/import sequence.

B11 still validates properties that require its actual SeaQuery tree or admitted
session: grouping-key coverage, ordering uniqueness and seek semantics, recursive
CTE anchor/member/self-reference shape, provider SQL semantics/collation, and
active-transaction requirements for row locks. B09 does not claim to validate a
SQL tree it does not own. Its root mutation gate does require WHERE and rejects
join/group/paging/CTE/set/window shapes before seal6/7; B11 repeats any additional
provider-tested mutation restrictions instead of dropping clauses.

## JOIN and composition

JOIN uses reserve18, synchronous expression construction, then commit7. The
provisional alias exposes declared ON nullability. During that reservation, only
expression construction/read and commit/abort are admitted. Commit first validates
the ON predicate, promotes the same alias token, then invalidates every public
expression depending on the provisional alias. Abort19 invalidates the same
expressions and reservation without publishing a JOIN. Abort remains possible
when construction reaches the node budget.

A captured non-null right expression cannot escape a left JOIN callback. Fresh
committed left-alias columns have native nullable metadata. JOIN MATCH uses the
native private-marker contract; it does not infer a match from nullable user data.
The marker's SQL source is a B11 responsibility.

Self-composition uses source read admission, clone, end source admission, seal the
temporary clone with terminal8, destination import2, then SET or SUBQUERY. No
source gate is held while destination mutation is acquired. Temporary plan/query
owners are disposed in reverse order, while the imported native metadata keeps
its own references. This sequence also works when source and destination are the
same mutable query.

Parameter IDs are source-generation identities. Reusing an ID with a different
name, codec configuration or nullability rejects. Composing distinct IDs with the
same name also rejects atomically. The sealed kind1 schema uses the exact canonical
logical JSON/hash, while db-wire descriptors retain source-bound codec IDs.
Execution rejects missing, extra, reordered or incompatible values before copying
and claiming the ordinary submission gate.

## Independent relations

Relation registration accepts B01's kind23 fields1..8 with both table IDs and
model hashes, ordered parent/child IDs, child NULL flags and independent relation
hash. It leaves both frozen Table identities unchanged. Native registration
requires all metadata, scalar-codec, query, streaming and explicit-relationship
capabilities. It validates complete ordered primary-key/FK pairs, configuration,
collation and source-bound codec identity before publishing a cache entry.

The source-local relation registry is bounded by both entries and canonical
bytes. Cache hits compare complete identity, not only the hash. Source close
removes cache ownership; admitted `RelationLoad` snapshots retain both models,
relation, projection and copied parent key independently.

Load-create accepts one B01 kind4 parent-key row and a source-bound projection
that exactly matches the child's Table.Row. It validates source/profile, key
hash/table/order/non-null codecs and finite limits, then copies the key before
returning arguments for B04's Created operation. There is no parent existence
lookup, implicit ordering or inferred tenant/security filter in this helper.
B11/B14 construct the actual child equality query using these ordered arguments.

The exact bound lifecycle is create, start, terminal notification,
`take_owner(expected_kind=2)`, bounded cursor fetch, close, operation retirement
and release. A wrong take kind does not consume the result; retirement does not
destroy a transferred cursor. Request-cancel is a nonblocking request and cannot
rewrite an already completed result. No second production cancellation or async
runtime protocol is defined by B09.

## Costs and error behavior

A direct operation performs one FFI call and bounded metadata validation. Control
encoding allocates one bounded byte array; projection encoding snapshots the
field sequence once. Value encoding uses B01 scalar serialization and db-wire.
Native parsing may create temporary validated copies as well as the persistent
snapshot, so this implementation makes no zero-copy claim. Clone and import
retain immutable dependencies and charge their conservative budgets; query-local
expression handles need no per-expression public release.

Local default/foreign/disposed references raise argument, disposal or admission
errors before native code. Native failures preserve Core status/domain/code and
correlation in an internal cold-path failure record for the domain adapter to
map. Diagnostics contain stable bounded messages, not SQL or parameter values.
Neither failure nor cancellation proves cleanup or safe retry. Poisoned arenas
accept cleanup only. This component makes no throughput, allocation or RSS benefit
claim without comparative measurements.

## Linux verification and samples

Maintainer verification uses the fixed repository toolchains:

```sh
python3 tests/Query.Binding/verify.py --design /path/to/pinned/NativeWeave-Design \
  --nuget-config /path/to/NuGet.Offline.Config --output artifacts/B09/linux
```

The verifier checks the pinned 42-signature inventory, generated C/Rust/C#
declarations, compiled function-pointer parameter/return types and exact Cdecl
signature headers, zero public CLR exports, XML/reference completeness, C header
compatibility, native negative/property tests and both JIT and Linux x64 Native
AOT execution. It regenerates managed golden inputs into the evidence directory
and compares them with the committed fixtures without modifying source files.

The native test fixture wraps the **production** B09 validators. Its source/session
and synchronous callback transport are explicitly test-only; its cursor yields a
canonical EOS batch and it does not render SQL or contact a database. This permits
actual product binding/validation execution before the B04/B11 host exists. It is
not a Design stub pass, provider execution result, full-host claim or pristine
NuGet consumption claim. Native owner/operation/buffer retirement counters are
observed independently of managed scope disposal.

The [minimal and composition sample](../../samples/api/query-binding/Program.cs)
uses an already built fixture asset to show typed Table registration, immediate
predicate/projection calls and self-composition. The same ordinary managed source
builds and executes in JIT and AOT. Its test-only owner creation helper is clearly
named and is not an application DataSource factory. Windows acceptance remains
outside this Linux run.
