# C# bindings and compiler metadata

A10 consumes the completed A09 validator and native parser inventory. A maintainer
command emits internal C# `LibraryImport` declarations **before compilation**. The
.NET LibraryImport generator therefore sees them in its input compilation. The
separate `netstandard2.0` compiler package emits only static metadata. No generator
waits for another generator's output during the same pass.

## Maintainer path

```sh
dotnet run --project src/Generators.CSharp -- \
  native/interop/exports/binding.json parsed-inventory.json \
  native/interop/exports/src/lib.rs linked-symbols.txt \
  nativeweave_exports_conformance generated NativeWeave.Generated.Conformance
```

All native source bytes, source parser identity, exact export inventory and the A09
semantic sidecar must agree. Validation completes before the command writes
`Bindings.g.cs` and `Bindings.nwmetadata`. Unchanged files retain their bytes and
modification times. Output contains no timestamps, local paths or random identity.
Default namespaces use the full module identity's SHA-256; similarly named modules
do not collapse to a short name. C# keywords are escaped and generated local names
cannot shadow a sidecar parameter.

Malformed/unsupported wire types, open generic exports, stale source, missing or
extra symbols, unknown schemas and illegal callback signatures produce the existing
`NWABI001`, `NWABI002`, `NWGEN001`, `NWOWN001` or `NWASYNC001` diagnostics before
runtime. Current automatic owner calls support free-threaded exports with bounded,
nonblocking calling-thread release. Other threading profiles require an explicit
completed dispatch adapter and are rejected; documentation does not create one.
No global diagnostic suppression, dynamic reflection binder or implicit marshaller
is used. The native function's declared pointer validity and aliasing preconditions
remain caller obligations at this internal boundary.

## Resource and callback generation

Each owner is a sealed private-API reference type with its own private SafeHandle
destructor. It delegates logical admission, AddRef/Release and retirement to the
actual A03 `NativeResourceOwner`. A generated call acquires all required leases
before FFI and releases them in reverse order in `finally`. Output ownership
transfers only on success. Failed managed adoption, malformed native failure output
or later output adoption cannot strand an unpublished native owner. No public
copyable struct owns an allocation. A nullable owner input remains nullable, passes
zero for null, and acquires/releases only a present owner. Automatic typed owner
calls accept call-scoped input borrows; longer owner borrows require a retained-owner
adapter and fail `NWASYNC001` before output. This profile never silently shortens an
owner/retirement borrow to a call lease. Generated operation/setup/dispatch type
names are reserved and conflicting native identities fail `NWGEN001`.

Reverse entries are static, nongeneric `UnmanagedCallersOnly` methods with exact
Cdecl/blittable signatures. They call the supplied internal `BindingCallbacks`
dispatch contract. Ordinary adapter exceptions are contained; fault and wake hooks
also have containment guards. A terminal native status is never rewritten because
the managed bridge failed. Raw reverse entries do not run application handlers.

For each normalized D06 operation, generation emits an internal
`IOperationSetup_<name>` and static `Operation_<name>` calls. The completed runtime
provides `BindingOperations.Prepare_<name>`, retaining setup, control and any input
leases before start. Generated `Start` reports actual start exit in `finally`;
generated `MapResult<T>` requests unpublished-output rollback on a mapper exception
and retirement on either outcome. The coordinator implements the native quiet,
callback/control/start and cancellation-registration obligations. Generated code
does not duplicate that state machine or invent an arbitrary domain `Task<T>` API.

The executable test uses a **fixed synchronous coordinator fixture**, the actual
A03 lifetime kernel and A11 native exports. Its callback arrives before start
returns, and native code accesses the owner after notification. Tests show that a
retained setup lease survives managed completion and logical Dispose; mapping
failure releases unpublished output; final release follows explicit fixture drain.
This verifies generated dispatch/guards. Full A07 scheduling, registration races,
asynchronous workers and Host assembly remain their explicitly assigned work.

## Ordinary compiler consumption

The tools package uses a single `buildTransitive` Analyzer item and a canonical DLL
under `tools/roslyn`. It does not also place that DLL in automatic analyzer assets.
It has no runtime engine, Cargo or network dependency. A facade can retain this exact
tools-package dependency in its nuspec. `NativeWeaveExcludeGenerators=true` is the
advanced handwritten-static path; it does not change restore-time dependencies.

The compiler transforms `.nwmetadata` AdditionalFiles into value-comparable
semantic inputs. Unrelated source edits preserve bytes and cached transforms. A
changed schema affects its own descriptor. Duplicate module identities or unknown
metadata rows fail `NWGEN001` without publishing partial output. Cancellation is
observed in transforms; a cached pass with no work need not execute a transform.
The [format](../../src/Generators.CSharp/metadata-format.md) contains bounded
build-time facts, not a domain protocol or runtime query language.

The package targets `netstandard2.0` against pinned Roslyn 5.12.0. Verification runs
it in the .NET 11.0.100 compiler host. This does not certify older compiler/IDE
versions. The maintainer CLI and shipped runtime code target the pinned `net11.0`
baseline separately. Application additions do not invoke the maintainer CLI or
rebuild the precompiled Rust asset.

## Reference, examples and costs

The [compiled reference](../../src/Generators.CSharp/api-reference.md) distinguishes
the intentionally empty application surface from the compiler plugin entry point.
Generated imports/records include stable XML member IDs, parameter borrow/ownership
and bound descriptions, native status and thread/FFI cost remarks. Existing public
EnginePackage/NativeBuffer/Server declarations retain their original owners.

The [minimal and advanced sample](../../samples/api/generators-csharp/) compiles the
generated code and executes actual native functions in JIT and Native AOT modes.
The advanced fixed-fixture path exercises early notification, a native domain
failure, a managed mapper exception, duplicate Dispose and late admission rejection.
It is a maintainer conformance example, not an installable domain API.

Generation, schema validation, XML and Roslyn work occur during the build. Runtime
sync calls use one A03 gate/AddRef/Release and one FFI call with no per-call lease
object. A newly adopted resource allocates its wrapper, SafeHandle, A03 owner and
gate; retirement observations may allocate A03's completion slot. Callbacks use a
static function pointer and the already retained cookie; the fixed coordinator's
setup/context allocations are fixture costs, not a production async-performance
claim. No runtime scan or generated metadata array is required: metadata is const.

## Verification boundary

```sh
python3 tests/Generators.CSharp/verify.py --output artifacts/A10/verification
```

The receipt retains generator snapshots/digests, exact managed/native hashes,
Roslyn cache/edit/cancellation and unsupported-shape tests, compiled XML/API checks,
actual native/generated/handwritten JIT/AOT results and independent local tools
package consumption. A16/B24 still own final pristine facade packages. Linux
component success does not claim Windows execution or full A07/Host acceptance.
