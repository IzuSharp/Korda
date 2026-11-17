# B19 enabled-profile allocation analysis

This is the implementation's source/layout argument, paired with executable witnesses.
It is not a process RSS guarantee or a claim that a wire cap alone bounds JSON memory.
Only the profile actually configured in `native/protocols/mcp/src/runtime.rs` is enabled.

## Fixed inputs and ownership domain

- Design: `c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9`, API server section 8 and section 10.4,
  and protocols-and-observability's JSON parsing admission section.
- rmcp 3.10.0, archive SHA-256
  `b5949e504d5ab9944a168259afc0c39ac0230627b6ec5367ace194c59bcbe163`, upstream commit
  `53e64a93c8e32d8f7699af587b9e28566929940c`.
- Cargo.lock pins serde 1.0.229, serde_json 1.0.154, bytes 1.12.1 and Tokio 1.54.1.
  No serde_json `preserve_order` map feature is enabled: object maps use BTreeMap.
- .NET 11.0.0 runtime source `deae2e32bf0e018e09d64597b7c59b62b73bb3ce`:
  [JsonSchemaExporter](https://github.com/dotnet/runtime/blob/deae2e32bf0e018e09d64597b7c59b62b73bb3ce/src/libraries/System.Text.Json/src/System/Text/Json/Schema/JsonSchemaExporter.cs),
  SHA-256 `33f9777cb73ab3d53bb006416ef46b528b92ca399cf27a05a9c93ebf4c059f5e`;
  [EnumConverter](https://github.com/dotnet/runtime/blob/deae2e32bf0e018e09d64597b7c59b62b73bb3ce/src/libraries/System.Text.Json/src/System/Text/Json/Serialization/Converters/Value/EnumConverter.cs).

The ledger measures requested backing capacities and controlled ownership, not allocator
headers, fragmentation, runtime/JIT caches or arbitrary application code allocations.
A published DTO becomes application-owned under the canonical API boundary. Custom
constructors, getters and application services remain trusted application code; the
framework cannot turn a payload ledger into a limit on arbitrary CLR execution.

## Admission order

The outer Host validates headers, creates one request, and waits for the managed gate.
It has not polled the body or entered rmcp. Deny never parses the body. After Allow:

1. Reserve the fixed collector capacity (declared Content-Length, otherwise wire cap).
2. Copy the collected bytes into an exact owned buffer under a second grant, then drop
   the collector and its grant. This prevents hidden retained maximum capacity.
3. Reserve visitor scratch; scan with serde `DeserializeSeed`/`Visitor`, including map
   keys and duplicate-key rejection, then release scratch.
4. Atomically try the separate native and managed peak grants and the automatic-method
   metadata allowance. A failed partial acquisition rolls back immediately.
5. For business methods, also reserve 3 MiB of native output/queue capacity and include
   2 MiB of managed output overlap **before** calling the public rmcp service.
6. Native SDK, argument buffer, nested managed continuation, reply and Body each retain
   their appropriate ownership. Cancellation does not imply that a nested handler or
   upstream SDK worker has retired.

The private rmcp `expect_json` helper is not called. Public `StreamableHttpService`
performs its normal collection and typed JSON parsing. The wrapper's `Full<Bytes>`
replays the admitted owned input without introducing a second parser implementation.

## Native terms

Let `W` be retained wire backing capacity, `N` structural nodes (including object keys),
`M` object members, `S` total decoded UTF-8 bytes, and `D <= 32` depth.

| Grant | Requested capacity |
| --- | --- |
| Scan scratch | `2W + 128*4096 + 1024D + 4096` |
| Native input/SDK/bridge peak, including retained input | `4W + 512N + 256M + 6S + 65536` |
| Automatic list metadata | `8*registeredMetadataBytes + 65536` |
| Other method fixed SDK overhead | `65536` |
| Business reply/SDK output/network queue | `3*1048576` |

The scanner does not build a Value tree. Escaped strings use serde's reusable scratch;
`Cow<str>` keys borrow unescaped bytes. A per-map HashSet detects duplicate keys,
with the global node cap bounding the total key slots. The `128*4096` term covers
hash-table growth/slots; decoded escaped key storage is covered by `2W`. Depth work is
checked at each node against the same monotonic deadline.

The pinned 64-bit layout witness records Value=32, map=24, String=24 and Vec=24 bytes.
`512N` covers overlapping typed/Value records, Vec geometric capacity and parser work;
`256M` separately covers BTreeMap node/slot growth. `6S` covers decoded text, string
capacity, UTF-8 bridge and compatibility text overlap. `4W` covers retained input,
SDK recollection growth and byte serialization overlap. There are no input-dependent
unbounded retries or new independent collections outside these bounded passes.

`tests/peak.rs` uses a test-only tracking allocator. It checks visitor scratch and
simultaneously live source tree, bridge JSON, second tree and compatibility JSON text
for tiny arrays, wide objects, depth, escaped strings and UTF-8. It also enters the
**actual public rmcp service** for tiny/escaped/UTF-8 tool requests with two explicit
argument-byte bridge copies. Upstream thread/process metadata is initialized in a
separate warmup; per-request requested live capacities and final live allocations are
then checked. Host lifetime/callback quiescence is independently checked in C ABI and
managed actual-Host tests; allocator measurements do not substitute for retirement.

## Managed materialization and output

The separate managed grant is `3W + 4096N + 8S + 65536`, plus 2 MiB for a business
reply. `3W` covers owned bridge bytes and static decoder scratch; `8S` covers UTF-16
strings and overlapping UTF-8 encoding capacity. A sparse JSON object can construct a
wide statically mapped DTO even if most values are absent. The term is therefore
`4096N`, not a small fixed-size-per-present-property assumption: it includes the
admitted 256 mapped fields and collection capacity growth. The finite static metadata
prewalk bounds the reachable mapped graph. The wide sparse-array witness materializes
320 objects with 255 declared reference properties and records the actual allocation.

Output serialization uses bounded writers (at most 256 KiB each), a bounded static
object/string/node prewalk and explicit static metadata. The managed grant covers
DTO JSON, outer result JSON, final byte copy and writer/encoding overlap. Native reply
validation parses only under the already-owned 3 MiB reservation, checks its own
structural/native peak, exact output schema and structured/text agreement. Invalid
output is replaced with a small redacted error while the failed-create token is still
caller-owned. Successful create transfers the token exactly once. The SDK response
moves the same reservation to the actual Body; short reply operation completion is
not a release boundary.

## Startup exporter scratch

The metadata walk rejects types>64, properties>256, depth>16, names>16 KiB, recursive/
open forms, unsupported converters and exporter callbacks before BCL export. It also
bounds enum fields to256 each, attribute records to1024, attribute arrays to256 and
aggregate default/name/attribute strings to64 KiB. Only already supplied static
context metadata is resolved. Unknown attribute constructors are rejected using
`CustomAttributeData`; they are not executed by the exporter.

For `T` distinct metadata types, `P` unique mapped properties, `E` exporter visits
(including finite enum-name nodes), `A` unique plus revisited attribute records,
`K` unique plus revisited attribute argument elements and `B` unique plus revisited
property/default/name/path/attribute bytes, the pre-export reservation is:

`65536 + 4096T + 1024P + 8192E + 256A + 64K + 16B`.

The metadata-only `CountExport` follows the pinned `(JsonTypeInfo, JsonPropertyInfo)`
cache cardinality. Scalar nodes are uncached; repeated nested properties, scalar enum
lists, inherited attribute checks and JSON-pointer path lengths therefore count at
every actual visit. The estimator stops before BCL export as soon as the derived grant
would exceed the existing4 MiB startup cap. This prevents a small unique metadata graph
from expanding into a large exporter graph. It does not implement schema semantics.
Custom serializer-option converters and naming callbacks are outside this bounded
profile; ordinary numeric and source-generated string enums remain supported.

The pinned exporter constructs JsonSchema/JsonNode objects, property/required maps,
path arrays and cached context entries, then creates the final JsonNode representation.
The fixed/type/property terms cover metadata inspection, bounded writer storage and
exporter state. The per-visit term includes both native-AOT reflection/attribute costs
and the simultaneously live schema/node/container representations. Explicit repeated
attribute/argument terms cover construction and array growth. `16B` covers UTF-16,
escaped UTF-8, JSON pointers and overlapping encoded representations. The64 KiB encoded
limit is enforced while writing, not after an unbounded serialized allocation.
Concurrent scratch admission is separately capped at4 MiB and released in `finally`.

The failed `final02` receipt exposed why the earlier unique-property estimate was
insufficient: wide256 used1,523,920 allocated bytes in Native AOT, exceeding its old
1,148,160-byte allowance. The corrected count admits3,244,032 bytes for the same graph.
The new repeated32-by32 fixture fails before export based on its repeated visits even
though its unique metadata stays within the type/property caps. This finding is retained
as evidence; the failed receipt is never rewritten as a passed run.

`SchemaFixtures.cs` measures allocated bytes around the full repeated prewalk/export,
checks the reservation and encoded cap, and separately verifies metadata boundary
rejections. Numeric and renamed string enums use the exact BCL descriptors; the
string-enum exporter emits a finite `enum` without a `type`, which native shape
validation recognizes without changing the descriptor. The enum-only reflection
suppression is narrowly justified: the instantiated BCL `EnumConverter<T>` already
reads these same public static fields/rename attributes in `ResolveEnumFields`.
Actual JIT and Native AOT fixtures check that preservation invariant.

## Failure and retirement evidence

The native actual-Host fixture records SDK admission immediately before the public
service invocation. Every rejected depth/node/escaped/combined-peak/global-budget
case asserts **SDK admissions=0 and business callbacks=0**, one request take and one
gate, followed by zero native/managed bytes and completed Host close. Global pressure
holds separate valid RequestBudget grants and releases each exactly once. Managed
actual-Host tests add two concurrent real requests under4 MiB global limits.

Cancellation tests deliberately hold a business handler after cancellation intent.
Close returns Pending with positive native and managed retained bytes; lifecycle
completion and scope disposal have not run. Releasing that handler permits one
cleanup, separate A07 root/control/input/callback retirement observations and zero
native Host counters. The immutable verifier records exact source, locks, toolchain,
native asset, package and executable hashes alongside these results.
