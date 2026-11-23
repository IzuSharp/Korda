# Native C++ query implementation

The retained public `Korda.Query` namespace belongs to `Korda.Database`. The C# Fluent and internal QueryBinding calls enter the unchanged 42-function database C ABI. `native/cpp/src/query.cpp` owns runtime expressions, aliases, clauses, plans, rendering and the prepared shape cache. `host_abi.cpp` admits the call; the Host schedules execution through the selected direct libpq or sqlite3 provider.

This is the C++ replacement of the accepted implementation at `465cd0f90e6af05ec50eb581156240377cf0825b`. The fixed Korda validator and SeaQuery 1.0.2 sources define compatibility, including the existing SQL-output goldens. SeaQuery is no longer a linked or built product dependency. See [the native implementation guide](cpp-native.md) for the complete source map, dependencies, ownership and reproduction commands.

## Query and expression ownership

The query owns a mutable runtime arena. Generation-bearing tokens identify expressions, aliases and imported immutable dependencies. Metadata arrives as canonical bytes from C#; ordinary business tables need not exist when the C++ library is built. Each mutation validates a private candidate before publication, so failure cannot expose a half-join, partial projection or changed SQL. Unknown capabilities and malformed records reject before execution. Unexpected native exceptions are contained at the ABI and poison the appropriate engine owner; this is not a recoverable-OOM guarantee.

Sealing creates an immutable plan. Ordinary plans admit one execution; prepared plans accept separate copied parameters for each execution and may run through distinct sessions of the same source. Mutation after seal and foreign query tokens reject. Disposing a query releases its own roots while admitted plans and cursors retain their dependencies until retirement.

LEFT JOIN reserves an alias, builds ON expressions and then commits or aborts. Both outcomes invalidate provisional right-side tokens. Committed nullable access creates fresh tokens. A private constant marker in the derived table distinguishes an absent joined row from a matched row whose business fields are NULL. Nullable lifting changes metadata; it never substitutes a sentinel or changes SQL equality semantics.

## Parameter and dialect semantics

Validated table/column metadata supplies identifiers. Values remain ordered parameters, including Unicode, text that resembles SQL and binary content. The renderer emits the exact legacy SQL/parameter order and a canonical db-wire argument payload; the provider performs the checked scalar conversion. Runtime placeholder identities are neither raw pointers nor SQL fragments. Prepared parameter IDs retain source identity and reject incompatible names, types or configurations when plans are cloned or composed as CTEs, sets and subqueries.

The original capabilities continue to distinguish providers. SQLite rejects row locks before rendering. PostgreSQL plans retain the transaction requirement through nested plans, and the Host rejects execution outside a live transaction before sending SQL. CTEs, recursive CTEs, set operations, windows and null-safe equality keep their accepted rules. Unsupported required opcodes report the same structured failure.

Window expressions snapshot their partition/order/frame state; later builder mutation cannot alter an already retained OVER expression. Collations use the exact provider allowlist and identifier quoting. Recursive CTEs require a physical anchor and UNION or UNION ALL with the declared self-reference. The existing rejection rules for aggregate, grouped, nested-set and window recursive members remain. PostgreSQL aggregate widening surrounds the full OVER expression, preserving the Int64/Double result schema.

## Copies, limits and prepared shapes

Each direct binding call uses one existing ABI operation. Borrowed input is copied before synchronous return. Native nodes and immutable seal/clone snapshots are bounded by the original token, depth, expanded-expression and encoded-byte limits. Candidate copies retain failure atomicity. No new runtime or pool is created for query preparation.

The source owns an entry-and-byte-bounded LRU of immutable SQL/parameter schemas. Its key includes SHA-256 of the exact binding signature inventory, source generation, provider, retained metadata, projection, operators, ordered codecs and named parameter identity. Ordinary values are excluded. Each execution owns fresh arguments and a provider-ready SQL copy; the cache owns no connection, callback or managed delegate. Eviction releases the cache root while active plans remain valid. A shape larger than the cache allowance may still execute within the query limits without entering the cache.

## Retained verification

`tests/Query.Native/golden/oracle` contains the 52 immutable SQL, ordered-parameter and actual-row oracles from the accepted Plan commit, with provenance and per-file hashes. The ported native checks retain query/mutation assertions. The unchanged C# composition driver calls the actual combined Host and checks SQL, typed results, refusals, callbacks, retirement and released buffers against both databases, then runs as Linux Native AOT. A second model/DTO build must leave the native module hash unchanged.

The [maintainer example](../../samples/api/query-native/Examples.cs) remains compiled into both executed consumers. This component adds no public CLR symbol or separate query package. The public Fluent facade, package consumers and complete old/new business comparison remain separate gates; a native unit pass does not stand for those results.

After building the shared Release preset, use the immutable historical Design declaration input described in README:

```sh
python3 tests/Query.Native/verify.py \
  --design artifacts/frozen-design \
  --postgres-bin "$NW_POSTGRES_BIN" \
  --native-module artifacts/cmake/linux-x64-release/libkorda_database_host.so \
  --host-source "$PWD" \
  --output artifacts/query-native-linux
```

Verification requires committed inputs and a fresh evidence directory. The migration report in Plan owns actual tested-source identities, CI and acceptance; neither building a driver nor publishing an unexecuted AOT binary establishes a passing provider cell.
