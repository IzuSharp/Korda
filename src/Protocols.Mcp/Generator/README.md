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
types separated by dots), separately from reflection marker names using `+`.
The focused actual NativeAOT witness verifies that this overload retains closed
generic field substitutions. The annotations are attached to a reachable,
empty module initializer; no DTO constructor or registration callback runs there.

Each preserved class definition also receives an assembly metadata marker:
`NativeWeave.Mcp.Layout.v1` with value `assembly-simple-name|metadata-type-name`.
Nested types use `+`, and generic definitions retain their metadata arity. The
runtime must require this marker before using field reflection; a missing
analyzer or an erased registration path with no discovered static root cannot
silently fall back to incomplete NativeAOT metadata. This marker is an internal
coverage contract, not an authorization boundary against application code.

Discovery is finite. It tracks closed types and bounds a conservative candidate
superset at depth 32 and 1024 types per root, above the runtime's admitted 16/64
JSON graph. Recursive or unsupported JSON shapes remain governed by the
existing runtime profile. Unrelated JSON contexts do not gain a new compilation
error; any required class whose metadata cannot be proven remains unmarked and
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
