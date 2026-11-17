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
- The installed runtime's VMR source is
  `297acf930fb0f5c07fd4e51d78e521b4ad2e5d1f`. The exact CoreCLR/NativeAOT
  field-cache and `RuntimeHelpers.SizeOf(RuntimeTypeHandle)` sources, hashes, and
  the SDK implementation-reference selector are recorded in
  [runtime-layout-sources.json](runtime-layout-sources.json). The relevant CoreCLR
  reflection source is byte-identical at the Design runtime reference and this VMR.
- Fixed private identity resolution and the fieldless framework interfaces use the
  [23 exact runtime identity sources](../../src/Protocols.Mcp/Generator/runtime-identity-sources.json)
  checked by the compiler witness, including both runtime lookup/cache paths and
  `IEquatable<T>` used by ordinary C# records.

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
   the registered static output's managed overlap **before** calling the public rmcp
   service. Resources/prompts use their fixed text profile; tools additionally include
   the complete typed output prewalk's boxing path.
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

For the selected tool, let `L` be the greatest proven complete CLR storage among its
reachable input types. The separate managed grant is
`3W + max(4096, 4L)*N + 8S + 65536 + R`, where `R` is its proven reply overlap.
`3W` covers owned bridge bytes and static decoder scratch; `8S` covers UTF-16 strings
and overlapping UTF-8 encoding capacity. The four storage copies cover live collection
growth, final arrays and value copies. A JSON value supplies at least one counted node
for every serializer-created object/collection element, including empty sparse objects;
keys and envelope nodes add conservative slack. References are counted as inline slots
inside their owner, with any separately created reachable object charged by its own node.

`L` is not inferred from visible JSON properties. The public .NET 11
`RuntimeHelpers.SizeOf(type.TypeHandle)` returns exact managed inline width for every
value type, including private/ignored fields, explicit padding and nullable payloads.
It returns only pointer width for reference types, so classes need complete preserved
field metadata. The existing MCP package carries a compiler analyzer that roots public
and nonpublic fields for every statically reachable class/base and emits a fixed internal
proof helper. Before requesting field arrays, registration resolves that one helper in
only the explicit context, DTO and entry assemblies and verifies its finite counts.
There is no assembly scan, DTO instantiation, constructor execution or reflection codec.
Accessible definitions use direct `typeof` identity checks. Private definitions use
only generated fixed name literals against a statically known assembly; the helper
never reads an incoming `Type.FullName` or assembly name. Its name-free
`IdentityLookupBytes(Type)` comparison path supplies the complete fixed-candidate
lookup cost before either count method can invoke the separate no-inline name resolver.
Registration admits twice that cost, once for each count delegate, and retains the
cumulative charge for every attempted helper assembly throughout export. Duplicate
context/DTO/entry assemblies are not queried again. The complete scratch expression is
checked before these calls and before each field/cache array request. Missing proof or
identity-budget failure uses bounded tool/context wording, without formatting the
unadmitted type name; root-shape diagnostics still name their offending type.

For each class, the bound is its proven base storage (or a 128-byte header margin),
plus twice every declared field's exact inline width, plus twice the greater of declared
`StructLayout.Size` and any `FieldOffset + field width`. Each field's alignment gap is
bounded by its own width; the fixed header also covers final object alignment. Summing
the base rather than reflecting inherited fields as declared preserves private base
storage. Explicit offsets cover sparse layouts even when `Size` is zero. Ordinary
non-nullable struct roots remain enabled.

The generated field proof uses complete implementation metadata for external classes.
The package's existing build-transitive integration sets the SDK-supported
`CompileUsingReferenceAssemblies=false`: ProjectReference implementation-only field
changes therefore rebuild the proof even when the public reference assembly is unchanged.
Reference-only third-party metadata cannot certify absent private fields and fails
closed unless the actual context/DTO assembly supplies its own preserved proof.
A compilation-end invariant checks the final DTO/base field surface, including fields
added by another generator, against emitted conservative counts. The runtime never
accepts a field array larger than that proof. The compiler/JIT/NativeAOT witnesses live
under [Generator](../../src/Protocols.Mcp/Generator/README.md), and product Host witnesses are in
[LayoutFixtures.cs](LayoutFixtures.cs).

The earlier fixed `4096N` estimate was disproved by a 600-byte request containing
128 empty objects. An item with 80 ordinary nested value fields allocated 5,248,664
bytes; an item with one 65,536-byte padded value allocated 8,394,392 bytes. Both exceeded
the old 2,776,072-byte complete grant. These are framework-created object bodies, not
application constructor allocations. The repaired fixtures cover these cases, private
and ignored fields, inherited private storage, explicit offsets, and explicit class
size. Each wide request is rejected before static deserialization/business entry;
the native fixture independently observes zero public SDK admissions. A small ordinary
struct tool remains callable beside the wide registered tools.

The two derived terms cross the existing schema slot in exactly one root extension
annotation, `x-nativeweave-managed-peak-v1`, containing unsigned `nodeBytes` and
`replyBytes`. Both are range-checked at registration; nested instances or extra members
are rejected. The annotation changes no assertion, required list, nullable member,
exporter annotation or nested node. It is not a standard JSON Schema keyword and does
not use `$comment`, whose standard forbids semantic action. Design server section 8
requires B19's implementation-derived allocation formula; section 10.1's outer
descriptor remains unchanged. [JSON Schema 2020-12 Core sections 4.3.1 and 6.5](https://json-schema.org/draft/2020-12/json-schema-core)
permit unknown extension annotations. Native `get_tool` returns the exact registered schema,
including this annotation; a real rmcp `tools/list` regression checks that equality.
Its encoded bytes count toward the ordinary schema and startup scratch limits.
The native preflight selects the named tool before SDK entry and binds the admitted
handler identity again before creating any managed argument owner, so a header/body
conflict cannot select a wider codec under another tool's budget.

Output serialization uses bounded writers (at most 256 KiB each), a bounded static
object/string/node prewalk and explicit static metadata. For a tool,
`R = 2 MiB + deepest live boxing path + 2*maximum inline value width + 128`.
The metadata-derived path sums exact value widths plus box headers along nested getter,
array and nullable visits; sibling values are visited sequentially. The two extra value
copies cover the framework's deferred result closure and copy overlap. Thus a sparse
JSON output cannot hide a large framework-created value box. A supported chain of
fourteen 128 KiB structs supplies a real pre-SDK reply-budget regression.
The remaining 2 MiB covers DTO JSON, outer result JSON, final byte copy and
writer/encoding overlap. Native reply
validation parses only under the already-owned 3 MiB reservation, checks its own
structural/native peak, exact output schema and structured/text agreement. An invalid shape or an output whose derived native peak exceeds that reservation
is replaced with a small redacted error while the failed-create token is still
caller-owned. The actual1000-object output fixture reaches this native peak boundary
after bounded managed encoding and verifies the same-token fallback. Successful create transfers the token exactly once. The SDK response
moves the same reservation to the actual Body; short reply operation completion is
not a release boundary.

## Startup exporter scratch

The metadata walk first acquires the existing full 4 MiB startup scratch grant, before
field proof lookup or any new layout reflection arrays. It rejects types>64,
properties>256, depth>16, names>16 KiB, recursive/
open forms, unsupported converters and exporter callbacks before BCL export. It also
bounds enum fields to256 each, attribute records to1024, attribute arrays to256 and
aggregate default/name/attribute strings to64 KiB. Only already supplied static
context metadata is resolved. Unknown attribute constructors are rejected using
`CustomAttributeData`; they are not executed by the exporter.

Complete layout inspection separately caps 64 class/base types, 1,024 declared
instance-field slots and 4,096 reflection-cache slots. The compiler helper supplies
both declared storage count and the complete cache surface before `GetFields` runs.
The latter includes static/literal fields, bases and interfaces: both pinned runtimes
can populate these before applying `Instance | DeclaredOnly`. Counts are summed for
each reflected class/base query, so repeated inherited cache entries are covered.
An empty base/interface contributes an owner slot for the runtime's type arrays.
Field names are lazy in these name-null runtime paths, so the count does not assume
eager name copies. Enum field inspection uses the same prior cache admission.
The helper is looked up by an exact generated type and method name; it does not
materialize assembly attributes. Localized trimming suppressions document and test
the generated `DynamicDependency` proof rather than claiming reflection-free code.

For `T` distinct metadata types, `C` class/base layouts, `F` declared field slots,
`Q` reflection-cache slots, `P` unique mapped properties, `E` exporter visits
(including finite enum-name nodes), `A` unique plus revisited attribute records,
`K` unique plus revisited attribute argument elements and `B` unique plus revisited
property/default/name/path/attribute bytes, the pre-export reservation is:

`66560 + I + 4096(T+C) + 1024(P+F) + 256Q + 8192E + 256A + 64K + 16B`,
where `I` is the cumulative pre-admitted identity lookup cost.

For one fixed private resolver invocation, the generated identity bound is a 16 KiB
cold-helper margin plus, for every candidate it can visit,
`16 KiB + 64*literalCharacterCount + 8192*(1+nestedSeparatorCount)`.
The literal retention, parser copies and pinned NativeAOT nested-type lookup caches
are included. `IdentityLookupBytes` returns zero for a direct covered definition;
the private bound is checked twice before the two count calls. Bounds saturate to an
unadmittable integer value instead of overflowing. This is an allocation bound for
the executed resolver, not a quota on unrelated public descriptors.

After proving that this complete derived bound fits 4 MiB, registration atomically
reduces its existing grant to that amount. The grant includes retained prewalk state;
there is no uncharged overlap before export. Every failure releases the current grant
in `finally`.

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

An independent check of the first CLR-layout correction found a separate identity
corner: 60 private types at CLR nesting depth 70 still formed an admitted 62-type,
120-property JSON graph. Their retained `FullName` UTF-16 payload alone was
4,209,940 bytes, and registration allocated 9,361,464 bytes. Checking JSON depth did
not bound CLR identity depth. This is why the final helper pre-admits fixed-literal
name resolution and never materializes the incoming type's full name for lookup or
failure reporting. Public direct identity paths do not acquire an unrelated private
name charge; there is no arbitrary table-size cap or blanket private-DTO prohibition.

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
