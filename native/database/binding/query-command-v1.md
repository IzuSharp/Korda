# Complete query command extension v1

`query-command-v1.json` records the additive signatures and capability;
`korda_query_command_v1.h` adds the synchronous compile, render and CTE entry
points negotiated by capability bit 26 (`db.query.command.v1`). A consumer must
resolve all three exports before selecting this path. The existing database v1
header, signature JSON and ABI fingerprint remain unchanged. A missing extension
is `NotSupported`; product callers do not fall back to per-node submission.

The command is a bounded, little-endian operation tape. C# owns composition,
immediate argument checks and copied literal bytes. C++ resolves source-bound
owners, validates and replays the complete tape through `NativeQuery`, then
compiles SQL and its parameter layout before returning an immutable plan. These
entry points neither lease a connection nor execute SQL. Plan execution and
batched result reads retain their existing ABI and lifetime rules.

## Envelope and blocks

The 32-byte header contains eight `u32` values in order:

| Offset | Field | Required value |
| --- | --- | --- |
| 0 | Magic | `0x4351574e` (`NWQC`) |
| 4 | Version | `1` |
| 8 | Total length | Exact command byte length |
| 12 | Block count | At least one |
| 16 | Root block index | Block count minus one |
| 20 | Record count | Sum of all block record counts |
| 24 | Origin binding count | Zero when no prior scalar origins are reused |
| 28 | Reserved | Zero |

The header is followed by that many 12-byte origin bindings, each containing
`u32 commandOrigin, u32 ownerSidecarIndex, u32 ownerOrigin`. A binding refers only
to an origin retained by a prior command plan or CTE in the same source. Its
command origin must be used by a record in this command; duplicate target origins
are rejected. Bindings may reference origins that appear later in the tape.

Blocks follow the binding table. Each starts with six `u32` values (24 bytes): its exact byte length, root
model sidecar index, table ID, record count, result slot count and reserved zero.
Records follow without padding. A block may import only earlier blocks. Every
non-root block must be imported by a later block, so all blocks reach the root.
Earlier blocks are sealed as immutable selected plans; the last block is compiled
with the entry point's terminal or used as the CTE body.

The source's query limits still apply independently to every native arena and to
its imported/expanded graph. The envelope additionally limits blocks to
`limits.nodes + 1`, records to `2 * limits.nodes`, origin bindings to the record
count, sidecar entries to
`2 * limits.nodes + 1`, and bytes to
`limits.bytes + 128 * limits.nodes + 64`. These allowances cover tape headers and
local references, and do not increase native expression or value budgets.

## Records and local references

A record has a 16-byte header: `u32 length`, `u16 kind`, `u16 opcode`,
`u32 resultSlot`, `u32 originId`, followed by its payload. Length includes the
header. Result slots start at one and are consecutive within each block. An
operation without a result uses zero. Every referenced result must have been
defined earlier in the same block. Only a column's root alias may be zero.

| Kind | Operation | Opcode | Payload |
| --- | --- | --- | --- |
| 1 | Column | Zero | `u32 aliasSlot, u32 columnId` |
| 2 | Literal | Zero | Existing one-cell wire frame |
| 3 | Parameter | Zero | `u32 id, u32 boundCodec, u32 nullable, u32 nameLength`, then UTF-8 name |
| 4 | Binary expression | Existing binary opcode | `u32 leftSlot, u32 rightSlot` |
| 5 | Unary expression | Existing unary opcode | `u32 inputSlot` |
| 6 | List expression | Existing list opcode | `u32 inputSlot`, then existing cells wire frame |
| 7 | Import | 1 model, 2 plan, 3 CTE, 4 earlier block | `u32 index` |
| 8 | Clause | Existing clause opcode 1–22 | Existing kind-6 ControlRecord, with local slots in token fields |

Import opcodes 1–3 index the sidecar. Opcode 4 indexes a block. A sidecar entry
contains an owner pointer, kind and reserved zero; the pointer never appears in
the byte stream. The Host resolves every entry through its live-handle registry,
checks its owner kind and source generation, and retains it for the synchronous
call. Stream integers cannot be interpreted as native pointers or generation
tokens.

For clauses, type-2 fields are local slot references except paging (opcode 5),
whose values are numeric. Timeout's type-3 value is numeric. Projection (opcode 6)
also remaps the type-8 token vector and optional field-5 tokens in each nested
kind-22 projection field. The normal validators still check the projection hash,
types, nullability and correspondence with those expressions.

Clause 7 commits an existing provisional JOIN alias: its result slot is zero and
the existing alias slot remains valid. Clauses 13–16, 18, 20 and 21 return new
slots. Clause 11 returns a slot only for action zero. All other clauses have no
result. Window operations are replayed in their original order, preserving the
snapshot taken by `OVER` or an aggregate with a window.

## Snapshot scalar identity and failure behavior

`originId` is required for literal, parameter and list records, and for text
clause 16 operations 0–3. It is zero for other records. First appearances use
consecutive command-wide IDs starting at one. Managed clones and snapshots retain
the same origin object, while independent construction creates a new origin.

Repeating an origin must retain its record kind, opcode and scalar content.
List identity excludes its input expression slot. Text identity includes its text
operation, escape and original cell frame, and excludes its input expression.
Native replay shares the associated immutable scalar slots across those copies;
it does not deduplicate unrelated equal values. This preserves existing
clone/self-import scalar identity and expanded-value accounting. Successful
command plans retain their immutable origin map; a CTE retains it through its
body plan. A later binding reuses those scalar slots and verifies the new record's
kind, opcode, identity, codec and value. The map holds scalar data, not owner
pointers, so it cannot create an owner cycle. No thread-local or source-global
literal registry is introduced.

Managed composition retains a bounded lease for a shared origin's first compiled
owner until its last tape/snapshot reference is released. Later CTEs and plans
made from that same origin send its binding, including after the original CTE's
public wrapper is disposed. First compilation of shared origins must coordinate
publication of that capture. Independent equal literals have independent origins
and never receive a binding merely because their bytes match. This also covers a
constructed but unused escaped text predicate: its expanded literal is retained
for budget accounting even when it contributes no SQL parameter.

Malformed lengths, reserved fields, references, dependency kinds, origins and
control records fail before a plan is published. Every semantic operation still
uses the existing native validation, including source identity, parameter
collision, JOIN alias invalidation, window snapshots, recursive CTE restrictions
and node/depth/value limits. Parameter identities are first accumulated in a
private registry, then atomically merged into the source registry only after
successful final compilation. Failed commands do not reserve source parameter
IDs. The ABI clears the output slot before work and returns a structured error;
no database I/O or write retry occurs in these entry points.
