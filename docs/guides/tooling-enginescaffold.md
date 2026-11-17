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
  artifacts/generated-engine --root . --sdk-revision <exact-published-SDK-commit>
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

The callback component covers both A10-generated reverse entries and actual
shared-Core v1 callback tables. The latter must appear as declared 24-byte records
passed readonly and copied before acceptance under a native owner borrow. A09's
`callbacks` list describes signatures for generated reverse entries; it is empty
when an Engine reuses the existing A06/A07 UCO implementations. An unused record
alone does not establish callback use. The bound facade and real conformance still
establish stop, drain and retirement; recognizing a table does not prove them.

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
The generated A10 owner helpers currently use Core's internal lifetime seam.
These profiles therefore retain their already registered maintainer assembly
identities and Core friend grants. An arbitrary new assembly identity requires
that explicit integration prerequisite; this tool does not establish unrestricted
third-party assembly access from a Core package alone or add a new public bridge.
Adding a managed DTO then rebuilding must preserve the native hash. A Rust invocation
trap verifies the build mechanism; this is explicitly not pristine-machine evidence.
A16 owns the clean no-Rust and final release-closure gate. Windows/static/mobile/WASM
and unavailable GPU/device certification are not asserted by this Linux scaffold.

The initial workflow template installs the same conformance command in a new
repository. Its reviewed package feed and pinned SDK source are explicit maintainer
inputs, not application dependencies or automatic release authorization.

The SDK revision is checked against every conformance tool input used from that
checkout. The generated workflow pins this exact published commit. Its manual
inputs select the prior workflow run and reviewed feed artifact in the generated
repository; it downloads that artifact and runs the same installed verifier.
It does not assume an undeclared feed directory is already present on a fresh
runner. Generation records exact binding-tool sources, output logs, template
hashes and generated files. The consumer calls the generated descriptor's real
preflight, checks threading and requires every declared capability before testing
the copied typed facade. Build and publish native locations come from A16's actual
SDK-selected asset records; their layouts need not be identical.

## Reusing accepted real-engine inputs

For the synchronous profile, `tests/Tooling.EngineScaffold/prepare_sync.py` accepts
`--engine-root`, the accepted A17 `--evidence` directory and a fresh `--output`
inside the maintainer checkout. It verifies the original source/native/package
hashes, stages read-only copies and uses the current A16 packaging task. It does
not repeat the accepted A17 component suite. The staged `descriptor.json` and
`feed/` are then inputs to:

```sh
python3 tests/Tooling.EngineScaffold/verify.py \
  --descriptor artifacts/sync-inputs/descriptor.json \
  --feed artifacts/sync-inputs/feed --output artifacts/sync-scaffold \
  --sdk-revision <exact-published-SDK-commit>
```

Run this entrypoint separately for the A18 descriptor and its reviewed package
feed. Each result retains deliberate unsupported schema/component and contradictory
native-layout rejection, generation logs, actual facade API/XML checks, independent
JIT/AOT package execution, separate normal/AOT locks and unchanged shared Core
source hashes. A passing synchronous result alone does not accept A24's required
heterogeneous pair. The template deliberately leaves `accepted=false`; acceptance
belongs to the independently reviewed execution receipts.

`tests/Tooling.EngineScaffold/prepare_async.py` accepts the same three preparation
arguments for the accepted A18 formal result. It stages the actual Hyper/Tokio
facade, full consumer and native library, verifies source and package identities,
and copies the exact A16 target/task/closure from the already consumed facade
archive. It preserves upstream VCS revisions, locked crate checksums, enabled
features, notices and the explicit A06/A07 callback authorities. This Python-only
staging does not rebuild Rust or repeat A18's accepted native/direct-Rust suite.
The generated package then executes the full consumer separately in JIT and AOT,
with the existing facade assembly friend prerequisite stated above.
