# Native SeaQuery implementation

The database query owner calls SeaQuery 1.0.2 directly. B09 validates the canonical
operation, token identity, codec, capability, and lifetime; B11 retains the actual
SeaQuery expression/statement; B04 supplies the runtime, session and operation
owners; the existing B02/B03 provider calls B07's sole checked SQLx binder.

The public `NativeWeave.Query` namespace belongs to `NativeWeave.Database`.
Applications do not load a query-only assembly, emit SQL syntax, call an FFI
function, or rebuild native code when adding an ordinary entity. This internal
component does not advertise a separately published package.

## Native call and ownership path

`B13 typed call → B09 QueryBinding → existing C ABI → B04 owner admission →
B11 NativeQuery → SeaQuery → B04 provider operation → B07 SQLx Arguments`.

The query owns its mutable SeaQuery state. Every expression refers to B09's
generation-bearing token. Queries retain models, aliases and imported immutable
dependencies. A failed clause first discards its private candidate: no half-join,
partial projection or changed SQL is published. Unknown capabilities and malformed
records fail before execution. A caught engine panic poisons the owner; panic
catching is not a feature-capability probe or a recoverable-OOM guarantee.

Sealing creates an immutable plan. Ordinary plans admit one execution. Prepared
plans use separate copied parameters on each execution and can be used concurrently
through distinct sessions of the same source. Mutation after seal and cross-query
tokens reject. Close drops query-owned roots while admitted plans/cursors retain
their own dependencies until retirement.

## Values and dialects

Identifiers originate in validated model/projection metadata and are quoted by
SeaQuery. Native expressions use private placeholder identities. SeaQuery renders
their exact SQL order; the adapter then emits one B01 canonical parameter payload
in that order. The provider feeds that payload to B07. The marker never reaches
SQLx, is never a raw pointer, and cannot collide with validated LIMIT/OFFSET
values. Literal text, binary data, Unicode and SQL-looking values stay parameters.
Prepared parameter IDs remain source-bound; clone/CTE/set/subquery composition
retains their identities and rejects incompatible names, types or configurations.

SQLite row locks reject before render. SQLite/PostgreSQL capabilities determine
CTE, recursive CTE, set operation, window, null-safe equality and prepared-shape
support. Unsupported required opcodes are structured failures; there is no managed
fallback or implicit ignored clause. Native nullable lifting changes metadata only:
there is no COALESCE, sentinel replacement or cast that could make a NULL FK match
parent key zero.

LEFT JOIN reserves an alias, constructs ON expressions, then commits or aborts.
Either outcome invalidates every captured provisional right-side expression.
Committed left-side nullable access creates fresh tokens. `HasMatch` uses a private
constant marker in a derived table, so a matched row containing nullable business
columns remains distinguishable from an absent joined row.

Window snapshots use SeaQuery's public `SelectExpr`/window renderer. Its generated
template and bound placeholder values are retained as a native expression, enabling
ordinary native composition without an independent SQL/window emitter. Later
window mutations cannot alter an existing OVER expression. Collations are limited
to the exact provider allowlist and are quoted as identifiers.

## Costs and bounds

This version performs one existing FFI operation per direct query call. It copies
borrowed value/control input during synchronous validation, retains native SeaQuery
nodes and copies the immutable shape at seal/clone. Clause mutations use a bounded
private native candidate before publication. Expanded-expression charging covers
cloned children, in addition to B09 token/depth/encoded-byte limits. This deliberately
trades bounded copies for clear failure atomicity; it makes no zero-allocation or
throughput claim. Query preparation does not create a new runtime or pool.

## Verification status

`tests/Query.Native/native` compares SQL and placeholder order against direct
SeaQuery, exercises malformed/capability/owner/state/snapshot/resource cases and
runs actual SQLite/PostgreSQL commands using B07. The PostgreSQL row requires
an ephemeral test server URL; an omitted endpoint is not
a PostgreSQL acceptance result. B04/B13 integration owns actual host/public-facade
JIT/AOT composition. See the current Plan B11 evidence for commands, hashes,
independent review and the precise completed platform scope.
