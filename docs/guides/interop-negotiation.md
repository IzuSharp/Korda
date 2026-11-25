# Engine metadata and module preflight

> **DB-CPP-01 implementation update.** The public C# examples, signatures and ownership contracts in this guide remain the compatibility inputs. The current maintained native implementation is C++20; its module mapping, direct libpq/sqlite3 execution, Clang/CMake commands and package production are documented in [C++ native implementation](cpp-native.md). Descriptions below of Rust, SQLx, SeaQuery or Tokio internals are retained as the fixed `465cd0f` behavioral reference, not current build dependencies. The named verification entry points now build the ported C++ fixtures and retain their original managed assertions.

Task A02 implements the metadata and loader part of `Korda.Core` for the
Task-02 Linux x64 delivery. The [compiled API reference](../../src/Interop.Negotiation/api-reference.md)
contains the exact 84 A02 public symbols and their XML documentation. The three
exception types belong to A05 in the same Core assembly. Core has no domain
engine, executor, network dependency, assembly scanning or global service locator.

The implementation follows Design commit
`c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9`, especially `docs/api/interop.md`,
`docs/api/nuget-and-consumption.md`, D02 and the versioned C# API standard. The
[input manifest](../../src/Interop.Negotiation/design-inputs.json) records exact
hashes. The existing .NET 11 source study is inherited from that Design's
`interop-sources.json`: `NativeLibrary.cs` at runtime commit
`deae2e32bf0e018e09d64597b7c59b62b73bb3ce`, lines 1–240. This task does not claim
to rerun upstream tests or revalidate the entire framework release.

## Application use

Domain factories supply their statically compiled descriptor automatically.
An application that wants explicit preflight can use the same descriptor:

```csharp
using Korda.Interop;

EngineInfo info = descriptor.Inspect();
if (info.Supports(new EngineCapability("db.query.v1")))
{
    Console.WriteLine($"{info.EngineId}: {info.AbiVersion}, {info.RuntimeIdentifier}");
}
```

`descriptor` is a supplied `EngineDescriptor`, not a new universal initialization
API. No database pool, server listener or runtime is started. The returned
snapshot remains readable after any later domain instance is disposed. Metadata
itself has no disposal operation because the selected module stays resident until
the process exits.

For an explicit group of engines, `new EngineCatalog([first, second])` snapshots
the supplied descriptors and sorts by ordinal engine identity. `TryGet` returns
false for a valid absent identity; `Get` throws `KeyNotFoundException`. A null or
malformed identity is an argument error. Creating or reading a catalog does not
load a native module. Installing a NuGet package does not automatically register
it in a custom catalog.

Advanced preflight uses the same loader:

```csharp
EngineInfo info = descriptor.Inspect(new EngineLoadOptions
{
    RequiredCapabilities = [new EngineCapability("db.query.v1")]
});
info.Require(new EngineCapability("db.query.v1"));
```

Inspect snapshots the requirement list before file access. A missing additional
requirement produces `EngineLoadException(MissingCapability)` without poisoning
an otherwise valid loaded module. `EngineInfo.Require` is a later local check and
throws `NotSupportedException`. Valid unknown names return false from Supports;
the invalid default capability is rejected. No missing feature selects another
provider or changes SQL semantics.

## Maintainer descriptor and SDK output record

The separately documented `Korda.Interop.Extensibility` seam constructs an
immutable `EnginePackageManifest`, then calls
`EnginePackage.CreateDescriptor(manifest, typeof(KnownFacadeType).Assembly)`.
This is a statically rooted assembly reference. Neither call loads native code.
The manifest's capability map is local to that engine and ABI major; an unknown
advertised bit does not invent a named feature.

The package-relative source path is not assumed to survive publish. Default
loading reads the bounded `Korda.<engine_id>.assets.json` record under
`AppContext.BaseDirectory`, compares its package/engine/version/RID/source/hash
against the immutable descriptor and resolves its canonical `outputPath` beneath
that directory. This supports both SDK development assets and flattened publish
output without walking directories or guessing the NuGet cache. A16 owns the
production build target that emits the record; A02 tests supply records from
their observed SDK output items. Missing or contradictory records report
`NWPKG002` in an actionable load exception.

The record is at most 16 KiB, schema 1, with exact required fields from the pinned
consumption specification. Duplicate required properties, wrong types, absent
fields, unknown schema, traversal, malformed UTF-8/JSON and descriptor mismatches
reject before OS load. Unknown optional fields are ignored within the byte and
depth bounds. An advanced absolute `NativeAssetPath` override bypasses only this
location record; it still must match the packaged hash, format, RID and CPU
profile. The override is not an environment search path or hash bypass.

The loader first checks the target/profile, hashes the selected file, validates
the native image format/architecture, loads the absolute path and invokes the
engine-prefixed bootstrap with A01's exact 32-byte wire record. It accepts matching
major, sufficient minor and complete compatible prefix; rejects reserved fields,
incompatible width/major/minor, short records and every non-OK bootstrap status;
and maps only the actual known capability bits. A larger future provider record
is accepted without reading its unknown tail.

This component selects the fixed engine-prefixed import route. No engine in this
task opts into a function table, so there is no speculative generic table or
universal function registry. A future optional table profile must define and
validate its exact required fields and pointers before being selected.

## Concurrency, failure and process lifetime

The loader publishes one load or failure result for concurrent first use of a
given engine ID. It does not hold the admission lock during file hashing, OS
loading or native bootstrap. Compatible descriptors share the completed module.
Generated static `LibraryImport` calls use a resolver installed once on their
facade assembly. Absolute `NativeLibrary.Load` does not recursively invoke that
resolver. Imports from different engines use unique basenames and symbols.

There is one selected content identity per engine ID. A different hash, ABI
requirement, package identity/version or contradictory manifest fails
`ConflictingModule`; deploy the consistent closure in a new process. No
`NativeLibrary.Free` path exists. A failure after successful OS load is latched,
so repeatedly trying alternate binaries cannot accumulate failed loaded modules
for that engine. Pre-load installation failures create no module owner and can
be retried after repair. Explicit overrides are checked even when metadata is
already cached.

Preflight never publishes a domain handle. Error messages contain bounded
engine/package/version/RID context and remedies; they omit absolute override
paths and raw OS loader diagnostics. A05 loader exceptions identify operation
`load` and keep all native-record fields null for these pre-native/compatibility
diagnostics. Hashes and canonical paths prove consistency with the trusted
package description; they do not authenticate malicious native code or sandbox
its dependencies. Symlinks and OS dependency resolution retain the deployment
trust model described by the pinned consumption specification.

## Bounds and costs

| Operation | Bound and cost |
| --- | --- |
| Capability/engine identifier | 1–128 ASCII bytes, fixed ordinal grammar; no trimming or normalization |
| Catalog construction | At most 256 descriptors, one enumeration, immediate rejection/disposal on the first excess entry; O(n log n) sort |
| Manifest construction | 1–64 assets, 0–64 capability definitions, unique RID/path/name/bit, cumulative string budget 65,536 UTF-8 bytes |
| Option snapshot | At most 64 unique capabilities and a 4,096-byte absolute override path |
| First Inspect | O(asset bytes) hashing, bounded metadata allocations, OS mapping, one bootstrap FFI; no native executor or domain owner |
| Compatible repeated Inspect | Cached metadata and local identity/option checks; explicit path overrides intentionally revalidate bytes |
| Supports/Require success | Ordinal hash lookup, no FFI and no managed allocation after initialization; the 10,000-call Supports check is measured in JIT and AOT |

These bounds do not claim a limit on the OS loader, whole-process RSS or arbitrary
metadata objects created by application code. CPU profiles selected here are
`x86-64` and `x86-64-v2`; the .NET 11 x64 process already requires the latter.
The two designed RID entries remain Linux GNU x64 and Windows x64. Only Linux
x64 is executed and accepted by this task; Windows implementation branches are
not represented as verified platform support.

## Verification and sample state

Run the scoped verifier with the pinned SDK/Clang toolchain:

```sh
python3 tests/Interop.Negotiation/verify.py \
  --plan-root /path/to/Plan \
  --design-root /path/to/Korda-Design \
  --output artifacts/A02/verification
```

For a deliberately offline Microsoft SDK package cache, supply
`--nuget-config /path/to/NuGet.Offline.Config --offline`; the evidence then states
that vulnerability-data refresh was unavailable. The repository audit policy is
not changed. The runner uses locked restore, actual JIT and Linux Native AOT
executions, exact compiled API/XML/reference checks, native symbol/format/hash
records and isolated local NuGet consumption. Its test package envelopes are
local verification fixtures; they do not publish Core or change automatic preview
scope. A16/B24 own complete pristine-machine/release composition.

The [minimal and advanced sample](../../samples/api/interop-negotiation/Program.cs)
executes the real Core loader and two distinct stateless Rust fixture modules.
The modules prove bootstrap/import isolation without initializing any executor.
They are intentionally not a Database/Server or A17/A18 real-engine acceptance.
Each evidence run records `product-executed` JIT/AOT state, exact sources,
commands, stdout/stderr, toolchains and binary hashes; API signature compilation
alone is never counted as an executed native path.
