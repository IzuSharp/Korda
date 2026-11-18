# Domain packages and Linux release acceptance

NativeWeave applications restore independent managed provider and protocol
facades with their exact native dependencies. Maintainers build Rust; ordinary
Entity, DTO and Handler changes build with the .NET SDK and retain the selected
native bytes. The current B24 candidate is a **local-only Linux x64 closure**.
Its package receipt does not authorize public domain publication. The existing
automatic HelloWorld preview remains the public publishing scope.

## Package selection

| Application graph | Direct facade references | Selected native engines |
| --- | --- | --- |
| SQLite | `NativeWeave.Database.Sqlite` | SQLite |
| PostgreSQL | `NativeWeave.Database.PostgreSql` | PostgreSQL |
| HTTP Server | `NativeWeave.Server` | Server |
| gRPC only | `NativeWeave.Server.Grpc` | Server |
| MCP only | `NativeWeave.Server.Mcp` | Server |
| Combined | Both provider facades, gRPC and MCP | SQLite, PostgreSQL and Server |

The producer creates seventeen owned package identities, including separate
`NativeWeave.Database.Sqlite.Native`, `NativeWeave.Database.PostgreSql.Native`
and `NativeWeave.Server.Native` leaves. The native leaves are transitive exact
dependencies. Application authors select facades rather than hand-loading a
shared library. An independent provider or protocol graph contains only its
owned dependency subclosure. The Server native module contains HTTP, TLS, Tonic
and rmcp code even when the corresponding managed protocol facade is absent.

Use the exact versions from one accepted candidate manifest and source-map
`NativeWeave.*` to its local feed. The previous/candidate pair retains eight
common archives byte-for-byte and advances six managed facades and three native
leaves. The resulting version map can contain more than one version string;
forcing every transitive package to the facade version is incorrect. The
shipped closure task checks actual restored versions and exact dependency edges
before compilation and publishing. Suppressing NuGet warnings does not disable
this hard check.

## Ordinary application workflow

The [sample source](../../samples/api/packaging-domain/) uses the existing
typed table metadata, insert/materialization APIs and native Server callbacks.
It includes no managed database driver or server alternative. Independent
conditional compilation keeps unselected provider/protocol imports out of
each graph. A maintainer controller copies the source outside the checkout,
creates an ordinary SDK project with exact `PackageReference` entries, and
performs normal `dotnet restore`, `dotnet build` and actual `dotnet` execution.

After the normal and explicit `linux-x64` AOT restores, the controller empties
NuGet sources and uses `--no-restore`. In the combined graph it adds a new C#
Entity, DTO and typed database mapping, plus an HTTP Handler that actually
returns the added DTO. Those changes execute against SQLite and real PostgreSQL
and through real HTTP. It publishes Native AOT and executes the resulting
native executable. The original selected native bytes, ordinary build outputs
and AOT outputs must have identical hashes. No consumer command invokes Rust.
Native AOT publication still needs the SDK's normal platform linker prerequisites.

The PostgreSQL test URI is explicitly supplied as `NW_TEST_POSTGRES` by the
existing Linux job and passed to consumers only in their environment. It refers
to the disposable local test service and explicitly selects its local-development
TLS setting. Each process creates and removes its own uniquely named tables;
the controller does not rerun migrations or historical component suites.
Server tests use ephemeral localhost TLS certificates, pinned client checks,
and actual negotiated HTTP/1.1 and HTTP/2. gRPC checks the real terminal status
and bounded protobuf value; MCP checks the typed JSON-RPC result. In the combined
graph these handlers query the real selected native database.

## Local publication and new-process consumption

The producer writes `prepared/manifest.json`, two exact feeds, build closures,
release records and source/native/license provenance. The existing release
controller invokes this consumer contract:

```sh
python3 src/Packaging.Domain/consume.py \
  --manifest /absolute/path/prepared/manifest.json \
  --closure candidate \
  --feed /absolute/path/destination \
  --output /absolute/path/new-candidate-consumer \
  --mode full
```

Acceptance execution belongs inside the existing pinned Linux SDK container,
with `B24_PRISTINE_IMAGE` identifying that exact image. Before installing command
traps, the controller requires Rust executables and toolchain directories to be
absent. Each graph has its own package cache and source mapping. Both the actual
cache archives and resolved owned graph must match the accepted destination.
A PATH trap on a host containing Rust is not a pristine-container pass.

The full candidate runs the six graphs once each. The combined artifact is
parameterized for the two providers; it is not rebuilt for every provider or
HTTP variant. The publisher separately uses `--closure previous --mode smoke`
for actual initial destination and fresh-process rollback consumption. A smoke
receipt executes the complete combined package graph with both providers but
does not claim the candidate's full AOT/negative matrix.

`tests/Packaging.Domain/release_cases.py` owns actual interrupted publication,
resume/readback, immutable-version conflicts, signature-aware rejection and
whole-closure rollback. A complete catalog is advertised only after the actual
destination callback succeeds. Rollback uses a new process and the complete
previous closure; it does not hot-replace a resident native module.

## Focused assembled negative cases

The new candidate exercises an owned direct override both with visible NuGet
warnings and with `NoWarn` suppression. Both must fail the actual shipped
MSBuild closure task. Further cases remove an exact native leaf from a fresh
restore feed, omit an SDK-selected native item, select an unsupported RID, and
tamper with native bytes before compilation and actual process loading.

A fresh AOT process also invokes the **actual SQLite domain bootstrap** through
Core's existing public maintainer descriptor API with required ABI major 99.
It must reject `IncompatibleAbi` before creating any database/server owner.
The native bytes are the real candidate bytes; this case does not replace them
with the old conformance fixture or add a handwritten FFI implementation.
Runtime records and bytes modified for the negative cases are restored and
their original identities revalidated before the consumer finishes.

## Transfer to the existing runtime-only image

The full consumer writes this relocatable bundle:

```text
runtime-transfer/manifest.json
runtime-transfer/<graph>/B24Consumer
runtime-transfer/<graph>/<native files and exact output records>
```

The manifest binds executable/runtime/native hashes, selected package versions,
provider parameters and required observed checks for all six graphs. Transfer
is preceded by the existing A16 `linux_profile.inspect` check of each actual
AOT executable: the same system-dependency allowlist, no RPATH/RUNPATH,
little-endian ELF x64, no stronger recorded ISA, and glibc at most 2.39. The
audit and its executable hash accompany the safe receipt and transfer manifest.
Transfer
this directory and the standalone standard-library verifier into the existing
pinned Noble runtime-only image. No SDK, Rust, compiler, checkout or package cache
is required by this verifier:

```sh
python3 runtime_verify.py \
  --bundle /bundle \
  --output /results/new-runtime-consumption
```

The runner requires the exact `B24_RUNTIME_IMAGE` identity and actual absence of
`dotnet`, `cargo`, `rustc`, `rustup`, `clang` and `cc`. Pass the same disposable
PostgreSQL endpoint via `NW_TEST_POSTGRES`. It executes the already published
artifacts, verifies Native AOT and the required real domain operations, and
checks artifact hashes before and after execution. A successful SDK-container
receipt does not by itself establish runtime-only acceptance.

## Evidence and acceptance boundaries

Every consumer output contains an initially unsuccessful `receipt.json`, then
actual process/graph/lock/output hashes and observed terminal checks. `passed`
becomes true only after all selected work, native sameness and destination
readback complete. Only `evidence/` is the safe upload subset; working folders
contain caches, packages, databases and private test material. Raw command logs
remain local and are represented in receipts by their hashes.
Unexpected restore/build/publish failures additionally expose a bounded set of
compiler/package diagnostic codes and messages, with local paths and source URIs
redacted. Runtime output and private configuration are not copied into that
diagnostic channel.

This task introduces no public application API. Existing Core, provider, query,
Server and protocol API/XML/reference baselines remain the owned component
inputs. The new compiled consumers verify actual package composition and
application experience against those APIs. Historical B21–B23 correctness and
performance evidence retains its exact original source/binary identities;
source/build applicability to the new candidate is independently reviewed.
New package consumption does not numerically transfer an old benchmark result.

The 24-hour regression and 72-hour release soak remain deferred by user
instruction, and other platforms remain outside this run. Positive authentication
of a new domain publisher and public domain publication remain unexecuted.
Source implementation or static checks alone never count as passed B24 execution;
the final Plan acceptance must bind the actual SDK/runtime, release and review
receipts of the candidate being accepted.
