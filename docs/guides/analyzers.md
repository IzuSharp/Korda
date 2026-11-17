# ABI, lifetime and AOT diagnostics

`NativeWeave.Analyzers` is a compiler tool. It reports known unsafe or unsupported
source shapes before native execution and consumes the same normalized binding
metadata as the maintainer generators. It does not create a runtime service,
rewrite an application API, generate marshaling code or invoke Cargo. The current
component is implemented for the pinned .NET 11 CLI on Linux x64; its local package
verification does not announce a public package release or certify another IDE.

The normative inputs are D03/D01/D04 at Design
`c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9`. Exact API input hashes and related server
type policies are in `src/Analyzers/design-inputs.json`. B10 owns full entity/codec
generation, B15 owns complete handler generation, and A22 owns release compatibility
decisions. This task checks statically visible unsupported inputs at those seams
without implementing their later engines or copying design stubs into the product.

## Installation and build-only behavior

Maintainers can run the source example with the project-reference route in
`samples/api/analyzers/AnalyzersSample.csproj`. Component verification packs
`NativeWeave.Analyzers` into a local feed, copies the sample outside the repository
and builds it through an actual `PackageReference`. The package stores one compiler
DLL and XML under `tools/netstandard2.0/`; one `buildTransitive` target canonicalizes
the package-relative path, removes only that identical Analyzer item and includes
it once. It does not change PackageReference, TargetFramework or restore inputs.
It has no `developmentDependency` flag that would remove a future facade's owned
dependency, and it carries no runtime/native payload or dependency on Core.

Compiler compatibility is separate from the consumer's `net11.0` target. The
plug-in targets .NET Standard 2.0 and pins `Microsoft.CodeAnalysis.CSharp` 5.12.0,
whose actual package includes a .NET Standard 2.0 asset. The current tests execute
both Roslyn's semantic driver and the .NET SDK 11.0.100 CLI loader. Visual Studio,
other IDE hosts, other compiler versions and Windows remain unexecuted. Roslyn
dependencies are supplied by that compiler host, not copied into the application's
runtime graph.

Future provider/Server facades retain an exact owned analyzer dependency and expose
its transitive build assets. An application need not register the analyzer again.
Those complete facade closures and pristine no-Rust machines remain A16/B24 gates.
The component consumer uses an isolated cache and Rust invocation traps; it does
not claim that Rust has never been installed on the test machine.

## Analysis boundary and explicit inputs

By default, raw ABI, owner, AOT and generator checks apply to source in
`NativeWeave.*`. Attributed entity and endpoint shapes are recognized by their
fully qualified accepted attribute names wherever application code lives. A
maintainer checking other namespaces can set `NativeWeaveAnalyzeAll=true`.
Unrelated third-party P/Invoke code is not silently assigned the NativeWeave ABI.

The following MSBuild properties are exposed to the compiler by the package:

| Property | Meaning |
| --- | --- |
| `NativeWeaveAnalyzeAll` | Apply the narrow ABI/AOT rules to every source namespace; default false. |
| `NativeWeaveRequireBindingMetadata` | Require A10 `.nwmetadata` for every checked import namespace; default false for independently owned/manual component seams. |
| `NativeWeaveCheckPublicDocumentation` | Check public/protected source XML in the selected scope; default false until the owning module opts into its documentation gate. |
| `NativeWeaveRequireApiBaseline` | Require one reviewed `.nwapi` source-declaration baseline; default false. |

Add the relevant files with ordinary `AdditionalFiles` items. `.nwmetadata` is
A10's versioned UTF-8/tab format: module, canonical schema digest, exact library,
generated namespace/source SHA-256, exports, records, fields, owner releases,
ordered parameter lifetimes and callback close/drain authorities. A09 validates
the full Rust/semantic-sidecar contract before A10 emits this compact view. The
analyzer rejects unknown or partial metadata, conflicting exports, signature
mismatches and changed generated C# bytes. It does not duplicate A09's full schema
normalizer or treat a matching digest as authenticity/signature verification.

The `.nwapi` first line is `nativeweave-public-api-v1`; each following line holds
one compiler XML ID, a tab and its reviewed Roslyn signature. The signature includes
qualified types, modifiers, generic constraints, nullable reference annotations,
parameter names/defaults and property accessors. A baseline addition or change is
a reviewed input, never an automatic acceptance action. This source diagnostic
complements A13's compiled assembly/XML inventory and A22's binary/behavior tests;
it does not replace their synthesized/inherited-member or behavior coverage.

Input files are capped at 2 MiB and metadata rows at 8,192 characters. The analyzer
reads only compiler-provided text and semantic models. Per-compilation dictionaries
are bounded by those inputs and source symbols; concurrent source-hash caches are
discarded with the compilation. Cancellation is propagated during input and symbol
walks, including before publication. No Compilation, ISymbol, SyntaxNode or Location
is stored in a global cache.

## Stable diagnostics

All diagnostics below are errors with source locations when the offending source
or AdditionalFile row exists. Missing entire inputs/exports/baseline members use
the compilation location and name the missing identity. The first six IDs retain
their D03 meanings. `NWAOT001` and `NWAPI001` extend the catalog without reusing an
older ID for another meaning.

### NWABI001

An import/callback uses a managed string, object, Boolean, character, unsupported
record or generic/UCO shape. Replace it with the explicit fixed-width wire type,
named sequential C record or pointer accepted by the engine contract. UCO entries
must be static and closed. The raw engine profile uses explicit pointer storage;
its analyzer does not infer custom marshaling or adopt arbitrary ref/out shapes.

### NWABI002

The exact Cdecl convention, required metadata schema, library, signature or full
export inventory disagrees. Declare the exact convention and regenerate from the
same A09-validated source/sidecar/linked inventory. An absent required schema fails
before execution. Native linked-layout verification itself remains A09/A11 work.

### NWOWN001

A raw import is public, or a syntactically visible borrowed address from `fixed`,
`SafeHandle.DangerousGetHandle` or `Unsafe.AsPointer` escapes through a return/member
store. Keep the import internal, consume the address under an admitted lease and
return an owned result. Dereferencing a pinned byte and returning the byte is
allowed. Returning a locally stack-allocated Span is rejected while a heap-backed view may escape. Logical admission, exclusive mutation and physical AddRef retirement
still require the runtime protocol and deterministic A03/A04 tests.

### NWASYNC001

A local delegate becomes a function pointer without a statically persistent root,
a call-only fixed pointer is passed to a metadata-declared retained input, a
callback lacks distinct declared close/drain authorities, or a checked engine call
uses `SuppressGCTransition`. Use a static UCO entry/static readonly delegate, or a
reviewed GCHandle retained through actual native retirement. Copy async input
before acceptance or retain its owner/pin until retirement. A matching callback
signature alone never proves native quiescence.

### NWMODEL001

An explicitly attributed entity/endpoint is open generic, has duplicate declared
column identity or contains a known unsupported ref-like/pointer/dynamic field;
an attributed route is not a closed static handler. Use a supported concrete value
codec/handler or explicitly ignore the member. This diagnostic performs the shared
known-shape checks; complete codec support and routing/serialization generation
remain with B10/B15 and their domain diagnostic catalogs.

### NWGEN001

Generated source/metadata are stale or ambiguous, or a source generator visibly
uses process/file/network/database/environment/random/time operations. Supply
explicit AdditionalTexts/configuration, use stable identities and move native
compilation into the maintainer build. Ordinary application generators must not
emit LibraryImport expecting another generator to process it in the same pass.

### NWAOT001

Owned source uses known dynamic type/generic construction, unbounded assembly
discovery or expression compilation. Use closed static metadata and generated
serializers/handlers. The analyzer does not suppress, rewrite or relabel framework
IL2026/IL3050 warnings. A separate actual publish-negative fixture proves those
framework diagnostics remain visible even when this plug-in reports no error.

### NWAPI001

A declaration differs from the reviewed nullable/default/signature baseline, or
enabled XML checking finds missing/mismatched summaries, parameters, type
parameters, returns/values, type-policy remarks or unresolved inheritance. Update
the implementation/docs together and review compatibility before changing a
baseline. Exception, cancellation and concurrency prose still receives semantic
review; an XML tag parser cannot prove that prose describes the runtime correctly.

## Limits and narrow suppressions

This is a set of definite source-pattern checks, not a proof system for unsafe code.
It does not infer arbitrary interprocedural pointer aliases, mutable callback roots,
native concurrency schedules, foreign allocator identity, retained closure graphs
or custom helper I/O. It never emits unsafe replacement code when uncertain.
No code fix is offered for changing an ABI type, lifetime or encoding because such
a change requires the native contract and can alter behavior.

For a verified helper/retained owner that this local analysis cannot establish,
record the exact diagnostic, source span, lifetime/retirement evidence, reviewer
and reason beside a narrow `#pragma warning disable <ID>`/restore region. Do not
disable the catalog project-wide, suppress framework AOT warnings, or remove the
analyzer to make a negative fixture pass. A static readonly delegate is a persistent
managed root, not evidence that native code has stopped calling it.

## Examples, cost and verification

The minimal example queries an existing precompiled Rust bootstrap version. The
advanced example passes bounded UTF-8 storage to the same real native asset under
call-scoped fixed blocks, checks status/extent and copies the result into a managed
string. These are compiler-consumer fixtures; their success does not re-accept the
bootstrap or establish new domain runtime behavior. Analyzer payloads never appear
in the sample's runtime/publish output, and adding a C# source file preserves the
native asset hash.

Run `python3 tests/Analyzers/verify.py --design-root <locked-design-checkout>` from
the source checkout with the fixed toolchain. The verifier records real command
exits, stdout/stderr, source/lock/compiler/native/package/JIT/AOT hashes and failed
attempts. `--prepare` only creates first-time lock/reference inputs and cannot claim
formal acceptance; a normal run requires a clean source checkpoint. Semantic
fixtures verify exact spans/IDs/severity, safe alternatives, unrelated source,
metadata drift, one-field edits and cancellation. Package tests prove one compiler
payload inclusion, compile-negative activation, actual native JIT/AOT execution and
unsuppressed framework warnings. Independent review is recorded separately in Plan.

All analysis allocation, hashing and semantic traversal is build-time cost; normal
application execution adds no analyzer allocation, FFI call, runtime scan, pin or
worker. Bounds on inputs do not establish a measured speed/RSS improvement. The
package's download/build-time dependency costs are separate from runtime cost.

The compiled compiler extension reference is
[`src/Analyzers/api-reference.md`](../../src/Analyzers/api-reference.md). Its only
public type is the Roslyn plug-in entry point, with constructor and required
overrides; all contract parsing/diagnostic plumbing stays internal.
