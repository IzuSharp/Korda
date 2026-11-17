# Static MCP layout preservation

This compiler component is carried by the existing MCP package; it is not a
separate package or runtime dependency. It preserves the metadata used by the
MCP DTO storage bound. The normal static `JsonTypeInfo` codec still owns JSON
conversion, and the existing Rust asset still owns MCP execution.

The generator discovers local `JsonSerializable` roots and the concrete input
and output types of semantic `McpRegistry.AddTool` calls. The latter covers an
application consuming a precompiled DTO/`JsonSerializerContext` assembly which
does not reference MCP or its analyzer. It traverses possible static JSON
members, arrays, `List<T>` and nullable values; every discovered reference DTO
and non-object base receives all-field `DynamicDependency` annotations. Private
and ignored fields remain visible for inline storage accounting even when they
are absent from JSON. An ignored or private reference field does not cause
discovery of an unrelated heap graph: its storage is one reference slot.

Accessible types use `typeof` on the exact closed instantiation. Inaccessible
private nested types and bases use the BCL type-name/assembly-name overload.
Its type name follows the trimmer's documentation-signature format (nested
types separated by dots), separately from reflection identity names using `+`.
The focused actual NativeAOT witness verifies that this overload retains closed
generic field substitutions. The annotations are attached to a reachable,
empty module initializer; no DTO constructor or registration callback runs there.

The generated internal helper has one fixed name:
`NativeWeave.Server.Mcp.Generated.LayoutPreservation`. Its
`DeclaredInstanceFieldCount(Type)` and `ReflectionFieldCount(Type)` methods
return conservative upper bounds, or `-1` when coverage is absent. The runtime
binds these exact methods as typed delegates in explicitly known assemblies;
it does not enumerate assembly attributes or load a registration table. These
methods are themselves retained by the reachable module initializer. Accessible
types compare the generic definition directly with `typeof`. Inaccessible
definitions compare against `Assembly.GetType` using fixed emitted metadata
names on a statically accessible assembly anchor. They never expand the incoming
type's `FullName`, `ToString` or assembly identity. Unknown types only traverse
the same finite known candidates.

`IdentityLookupBytes(Type)` is queried before either field-count method. It
returns zero for a direct `typeof` match, `-1` when no private lookup exists, or
a conservative byte allowance for one private lookup. A separate `NoInlining`
method contains the private literals, so direct lookup and the cost query cannot
cause that method's literals to materialize during JIT compilation. For each
private candidate, `L` is its fixed UTF16 literal length and `D` the number of
nested `+` separators. The allowance is:

`16 KiB helper cold margin + sum(16 KiB + 64L + 8 KiB * (D + 1))`.

The runtime checks twice this allowance before the two count calls, retains it
in the complete startup scratch calculation, and reports missing/oversized
proof without formatting an incoming type identity. The formula covers actual
retained literals, parser/name/namespace copies, and the fixed NativeAOT lookup
cache/temporary layouts. It overcounts shared parents and warm candidates; it
is neither an exact allocation measurement nor an arbitrary catalog-count cap.
The actual-runtime URLs, hashes and component inputs are recorded in
`runtime-identity-sources.json`. A public `AddTool` regression with long legal
private identities verifies rejection before identity expansion under JIT and
NativeAOT, while ordinary private/generic/external fixtures remain admitted.

The compiler resolves only the discovered definitions through Roslyn's public
`MetadataImportOptions.All` view. Its default imported view can omit private
external fields and is not a count proof. The existing MCP package sets the
SDK's `CompileUsingReferenceAssemblies=false` for its consumers, so an ordinary
`ProjectReference` supplies complete implementation metadata and a private DTO
field edit invalidates the consumer's compilation. The focused maintainer
witness uses the same SDK setting. A third-party package that supplies only
reference compile assets still cannot prove omitted private fields; definitions
or base/interface surfaces marked `ReferenceAssemblyAttribute` receive no
proof unless they are the fixed, source-checked framework roots described below.
Such a library can carry the existing MCP analyzer's proof in its own static
context assembly. No binary files are scanned or substituted by the generator.
The declared-instance bound includes
private/backing fields, conservative event slots and primary constructor capture
slots. The reflection bound also includes static/literal fields, each base and
interface surface, and a slot for each metadata owner, including empty
interfaces. It is charged separately for each runtime `GetFields` query because
the runtime can populate inherited fields again under another reflected type.
The actual returned instance-field array must fit the declared upper bound.

The fixed .NET 11 reference pack always supplies framework definitions.
`Object`, `ValueType`, `IEquatable<T>` (used by records) and the four `Enum` interfaces are fieldless; the pinned
runtime's `Enum` has `EnumSeparatorChar` and CoreCLR's `s_underlyingTypes`
(NativeAOT uses only the former). These exact framework roots are handled from
[the actual runtime source](https://github.com/dotnet/dotnet/blob/297acf930fb0f5c07fd4e51d78e521b4ad2e5d1f/src/runtime/src/libraries/System.Private.CoreLib/src/System/Enum.cs)
and its
[CoreCLR partial](https://github.com/dotnet/dotnet/blob/297acf930fb0f5c07fd4e51d78e521b4ad2e5d1f/src/runtime/src/coreclr/System.Private.CoreLib/src/System/Enum.CoreCLR.cs).
Other reference-only framework bases are not guessed from their public fields.

`LayoutProofAnalyzer` checks the final generated compilation against every
emitted proof. Its semantic action inspects only the one generated helper tree
and the named definitions, after all source generators have completed. A second
generator that adds fields to a partial DTO cannot silently invalidate the
earlier proof: an understated bound produces the non-suppressible `NWMCP001`
compiler error. Ordinary partial DTOs and generated methods remain supported.

A missing analyzer or an erased registration path with no discovered static
root fails registration before requesting field arrays. A shared static-context
assembly can include the analyzer from the existing MCP package, or an entry
project can supply concrete `AddTool` roots. An unrelated third registration
assembly is not discovered by scanning loaded assemblies. This is an internal
coverage contract, not an authorization boundary against application code.

Discovery is finite. It tracks closed types and bounds a conservative candidate
superset at depth 32 and 1024 types per root, above the runtime's admitted 16/64
JSON graph. Recursive or unsupported JSON shapes remain governed by the
existing runtime profile. Unrelated JSON contexts do not gain a new compilation
error; any required class whose metadata cannot be proven remains uncovered and
fails registration. No referenced assembly is scanned for candidate DTOs.

Value types need no field-reflection promise for their own inline width. The
fixed .NET 11 public `RuntimeHelpers.SizeOf(RuntimeTypeHandle)` reports their
managed inline size, including ignored fields and explicit layout padding.
For a reference type it reports only a reference slot, so the runtime separately
accounts the preserved fields, base storage, offsets and explicit layout size.
No private runtime object-layout structure or allocation probe is used.

Focused tests live in `tests/Protocols.Mcp/Generator`. The compiler driver checks
discovery and emitted coverage; the executable witness uses actual static JSON
contexts and the real `McpRegistry` API in a separate precompiled-context case.
The witness is run both as JIT and as a Linux x64 NativeAOT executable. It only
tests field preservation and static DTO decoding; the B19 product acceptance
suite owns request admission, lease accounting and handler rejection evidence.
