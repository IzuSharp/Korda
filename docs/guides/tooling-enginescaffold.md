# Engine scaffolding and explicit API adoption

Status: implemented maintainer tooling; real A17/A18 generated-package conformance
is recorded separately. The CLI adds no runtime public API. Existing `EnginePackage`,
`EnginePackageManifest`, `EngineDescriptor` and creator-owned `NativeBufferFactory`
are reused from Core; their exact declarations and XML remain owned there.

## Inputs and generation

Supply a versioned descriptor containing exact engine/package/ABI identities,
capability bits, explicitly selected common components, execution profile, one
Linux x64 GNU asset, A09 sidecar/parsed inventory/adapter/linked symbol inputs,
upstream commits/crate checksums/features/patches/licenses, source coverage, reviewed
facade/sample sources and the accepted compiled API baseline. All file inputs are
SHA-256 bound and relative to the maintainer source root. Generation rejects unknown
schema/components, incompatible profiles, absent creator destructors, mismatched
inventories and unsupported targets before publishing a partial tree.

```sh
python3 src/Tooling.EngineScaffold/scaffold.py engine-descriptor.json \
  artifacts/generated-engine --root .
python3 artifacts/generated-engine/eng/verify.py \
  --feed /absolute/reviewed-feed --sdk-root /absolute/pinned/NativeWeave
```

The command executes the existing A09/A10/A11 validated binding pipeline. It emits
an immutable static manifest, typed facade sources, precompiled native asset,
exact package dependencies, a normal package consumer, explicit `AGENTS.md`, the
versioned standard and hash, API/XML checks, guide and Linux conformance entrypoint.
Reviewed build-transitive metadata/closure targets may be included with the facade.
No template starts a runtime. A synchronous Engine opts out of callbacks/async;
an asynchronous Engine explicitly requires callback and owner retirement. The
adapter's domain operations and destructor are real reviewed source inputs,
not fabricated throwing stubs or copies of a managed domain algorithm.

## Ownership, errors and cost

Generation is an offline maintainer action apart from the normal pinned .NET
restore/build tools. It copies only explicitly bound files and publishes the new
directory atomically after both generation stages succeed. An existing output is
never overwritten. `scaffold-manifest.json` identifies exact source and template
bytes and initially records `accepted=false`. The generated tree needs independent
technical review and actual package execution before acceptance.

The application path remains ordinary NuGet restore/build/run/AOT using prebuilt
assets. No application target invokes this CLI or Cargo. The optional engine-local
runtime remains the Engine's responsibility. ABI records, raw handles, roots and
import classes are internal. A generated descriptor loads no library until explicit
preflight or the domain factory uses it. Buffers keep creator release and operation
retirement requirements from the original adapter; the scaffold adds none.

The scaffold verifier checks compiled signatures/XML against the supplied reviewed
baseline and executes an outside-tree package consumer in JIT and Native AOT.
Adding a managed DTO then rebuilding must preserve the native hash. A Rust invocation
trap verifies the build mechanism; this is explicitly not pristine-machine evidence.
A16 owns the clean no-Rust and final release-closure gate. Windows/static/mobile/WASM
and unavailable GPU/device certification are not asserted by this Linux scaffold.

The initial workflow template installs the same conformance command in a new
repository. Its reviewed package feed and pinned SDK source are explicit maintainer
inputs, not application dependencies or automatic release authorization.
