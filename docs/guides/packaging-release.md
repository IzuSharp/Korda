# Independent packages and immutable release closure

A16 supplies an exact package closure check, SDK native-output records and a
maintainer release coordinator. Linux x64 is the exercised profile. Windows
packaging, other RIDs, and additional public package publication remain deferred.
The locked inputs are recorded in
[`design-inputs.json`](../../src/Packaging.Release/design-inputs.json).

## Application experience

An application references a reviewed managed facade package at an exact version.
The facade depends on exact `[V]` versions of Core, its independent native leaf,
and its compiler tools. Ordinary `dotnet restore`, `dotnet build` and `dotnet run`
use NuGet's selected precompiled files. Native AOT uses an explicit `linux-x64`
RID and the SDK's AOT linker. Application installation, compilation and execution
do not invoke Cargo, download a Rust toolchain or compile native engine sources.

[`samples/api/packaging-release`](../../samples/api/packaging-release/README.md)
contains a runnable local package example using the actual Core descriptor and
ABI bootstrap. The local `NativeWeave.Conformance.Packaging` package exercises
the packaging boundary; it does not implement Database or Server domain APIs.
The package's Entity/DTO/Handler examples are ordinary strongly typed C#.

The transitive metadata generator and analyzer are compiler-host dependencies.
Their emitted metadata compiles into the application; their compiler assemblies
and `NativeWeave.Packaging.Tasks.dll` do not enter the runtime/AOT output.
Core contains no mandatory shared native runtime. Its managed package bundles
the internal ABI assembly without introducing a public application ABI package.

## Exact closure and SDK native selection

Each facade embeds three build-time pieces:

| Package path | Purpose |
| --- | --- |
| `nativeweave/closure.json` | Exact owned IDs, versions, dependency edges and native profile/hash |
| `buildTransitive/<FacadeId>.targets` | Restored-graph validation and SDK output record generation |
| `tools/net11.0/NativeWeave.Packaging.Tasks.dll` and XML | SDK 11 compiler-host implementation and documented contract |

The shared implementation is
[`NativeWeave.Packaging.targets`](../../src/Packaging.Release/buildTransitive/NativeWeave.Packaging.targets)
and [`PackageClosure`](../../src/Packaging.Release/BuildTasks/PackageClosure.cs).
Facades reuse these files, rather than implementing another native search path.
The compiled ten-symbol compiler-host contract is generated in
[`api-reference.md`](../../src/Packaging.Release/api-reference.md). Application
API applicability is recorded separately in
[`api-applicability.json`](../../src/Packaging.Release/api-applicability.json).

After restore and before compilation or publication, the task compares the
actual `project.assets.json` owned-package set, normalized exact versions and
resolved exact `[V]` dependency edges. It then checks the SDK-selected native
package identity, RID, canonical package path and bytes against the accepted
SHA-256. Multiple facade closures must agree on every shared package definition.
Cycles, duplicate engines/basenames, malformed JSON, unsupported profiles and
missing leaves fail with hard `NWPKG` errors. Suppressing `NU1608` or `NU1605`, or
adding a direct package override, does not suppress this validation.

The build and publish phases use the SDK's resolved native and publish items.
The output record identifies the facade, engine, version, RID, original package
path, actual output-relative path and native hash. RID-free development output
can retain `runtimes/linux-x64/native/...`; publication may flatten that path.
The record follows that actual SDK transformation. Core consumes the bounded
record from the application directory, checks its immutable descriptor, hashes
the selected file and performs its independent bootstrap negotiation. There is
no CWD-based probing, cache guessing or native load inside MSBuild.

The build check binds ABI/capability metadata to reviewed native bytes. Runtime
bootstrap negotiation is still required: a deliberately incompatible native ABI
is rejected in a fresh process even when its package hash is internally coherent.
Missing capabilities produce an ordinary Core diagnostic before creating a
domain owner. They never trigger a native rebuild.

## Maintainer staging and provenance

Use the pinned SDK 11.0.100, Rust 1.100.0 and existing repository build settings.
Run staging from a clean, committed tree on the declared Linux baseline:

```sh
python3 src/Packaging.Release/prepare.py --output artifacts/task04/A16/prepared
python3 tests/Packaging.Release/consume.py \
  --prepared artifacts/task04/A16/prepared \
  --output artifacts/task04/A16/consumer --negative-cases
```

Staging builds actual Core, ABI, compiler tools and the small native ABI adapter.
It records the source commit/dirty state, input inventory, lock and toolchain
identities, native ELF/dependency profile and package entry hashes. Each package
has its MIT license and exact source repository commit; the native leaf includes
NOTICE and a bounded source/native manifest. The archive SHA-256 lives in the
external closure record, avoiding circular package self-hashing.

The two default local versions provide a complete previous/candidate pair.
They are unsigned, local conformance artifacts. `prepare.py` never pushes a
package or changes the existing automated HelloWorld publication workflow.
Rust crate distribution is optional and is not added by this implementation.

`consume.py` creates a fresh directory and package cache outside the repository.
It performs actual RID-free build/run, prepares AOT restore, clears every feed,
adds and executes typed Entity/DTO/Handler code, then builds and publishes with
`--no-restore`. It verifies that the native hash remains unchanged, the
transitive compiler tools resolve once, and the actual AOT executable runs.
The development and published native records must both match the restored bytes.

`--offline-sdk-cache` supports development when the external SDK package feed is
unavailable. It copies only SDK/runtime packages into an independent feed and
disables online NuGet auditing for that run. It is never reported as a pristine
machine. A Rust invocation trap demonstrates that the consumer did not invoke
Rust; it does not demonstrate that Rust was absent from the machine.

## Immutable publication and recovery

[`release.py`](../../src/Packaging.Release/release.py) orders exact dependency
leaves before facades. Its local immutable-feed adapter uses exclusive package
creation and journals each uploaded or existing ID/version before readback.
Every package passes readback verification, exact nuspec/payload/closure checks
and real complete destination consumption before the local catalog is atomically
promoted. This is a local staging transaction boundary; NuGet uploads have no
cross-package transaction.

A partial upload leaves an unadvertised journal and immutable already-written
leaves. A retry validates an existing version exactly as it validates a newly
uploaded one. Matching bytes can resume; conflicting payloads require a new
version. A failed or incomplete consumer cannot create a release catalog.

The `verify_registry` adapter reuses A22's actual `dotnet nuget verify` signature
verification and accepted service/owner/certificate policy. It compares all
unsigned package entries, including nuspec, dependency ranges and native
payload, while recording upload and registry archive hashes separately.
Repository signing may change the archive envelope; an arbitrary differing
entry is never ignored. A local package hash proves integrity only, and the
local adapter explicitly records `signer_authenticated=false`.

The coordinator checks expected staging builder identity and artifact subject
digest. This local builder label is a policy allowlist, not a cryptographic
remote-builder attestation. The existing reviewed GitHub publication identity
and signature policy remain responsible for public publication authentication.

Run the coordinator's actual destination-restore and resume cases with:

```sh
python3 tests/Packaging.Release/publication_cases.py \
  --prepared artifacts/task04/A16/prepared \
  --output artifacts/task04/A16/publication \
  --signed-package /path/to/previously-accepted-signed-package.nupkg
```

The signed-package input exercises this new coordinator against retained real
repository-signed bytes; it does not repeat old consumer acceptance. The local
NuGet V3 fixture demonstrates that a known version still restores when its
registration is unlisted. It does not unlist any public package. Rollback selects
the complete previously tested package set for a new deployment and process.
Replacing or hot-unloading a native module inside a running process is rejected.

## Linux acceptance boundary

Final A16 Linux acceptance combines A14's completed Core callback/async package
AOT closure and A22's accepted compatibility/signature rules with new scoped
packaging results. A fresh pinned SDK container, without Rust toolchain mounts,
must execute `tests/Packaging.Release/consume-linux.sh`. Transfer only the
produced publish directory to the matching runtime-only Linux environment and
run `tests/Packaging.Release/runtime.py --require-tool-absence`. The runtime
receipt records the actual OS, executable/native hashes, output and absence of
the SDK, Rust and native compilers.

Local preparation receipts preserve their actual OS, dirty state and
`pristine_machine=false`; they do not accept Ubuntu 24.04 or the minimum CPU by
inference. The existing single Linux workflow supplies the baseline, pristine
container and runtime-only rows. Windows and other platforms remain explicitly
unexecuted. Full acceptance requires those receipts and independent technical
review; a build-only result or HelloWorld smoke test cannot replace them.
