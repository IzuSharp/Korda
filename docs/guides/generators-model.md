# Binding contract model

A09 provides a small **maintainer build component**. It validates an explicit Rust
C export inventory together with a semantic sidecar and produces immutable,
canonical ABI data. It does not load an engine, render SQL, execute queries, generate
marshalling stubs, or run a compiler from a source generator. A10/A11 own emission;
A12 owns application diagnostics integration. No new public C# API is introduced.

## Inputs and the existing public API

The exact design is `C-BINDING-001@0.4.0`, at Design commit
`c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9`. `design-inputs.json` binds its required API
inputs. The A01 wire layouts remain canonical. The fixture's `NwAbiInfoV1` field
inventory is compared with `native/interop/abi/abi-v1.json` and the executed A01 writer.
This is validation of the new model's layout, not repeated A01 acceptance.

`EnginePackage`, `EnginePackageManifest`, `NativeAssetDescriptor` and
`EngineCapabilityDefinition` remain the exact compiled A02 declarations in
`NativeWeave.Core`. `src/Generators.Model/api-bindings.json` maps all 30 symbols in
those four policies, including implicit members, to metadata-only behavior. Creating
metadata has no native operation; A02's explicit catalog preflight owns loading and
bootstrap. A09 does not duplicate the types or manufacture domain entrypoints.
The compiled model/XML reference gate rejects accidental public types.

## Strict schema version 1

`BindingReader.Read` accepts at most 1 MiB of UTF-8 JSON, depth 64. It rejects unknown
or duplicate properties and missing required fields. Counts are finite: 4,096
records/exports/owners/operations, 256 fields per record and 64 parameters per export.
Every failure includes a stable design diagnostic ID, input file, JSON path and
line/byte location. Parsing returns a complete immutable model or throws; cancellation
never returns partial output. Malformed parser inventories are rejected separately.

| Element | Validated contract |
| --- | --- |
| Module | Stable qualified ID, independent schema version, nonzero ABI major, minor, exact RID/triple and 64-bit pointer width |
| Provenance | Actual adapter SHA-256, maintainer tool version, exact upstream commits and feature sets; no timestamps or absolute build paths in canonical data |
| Record | Fixed-width fields, natural size/alignment/offsets, explicit reserved fields, no overlapping fields or semantic padding |
| Export | Exact C name/convention, u32 status or nonfallible void cleanup, required capability, target availability, bounded parameters |
| Parameter | Direction, nullability, borrow end, mutable access, output transfer on success, owner kind, finite byte limit and copy-before-accept |
| Owner | Creating module, one release, optional clone and parent-state relation, release executor and close progress obligation |
| Operation | Separate create/start/cancel/take/retire/release functions, the same retained owner, D06-v1 state profile and copied or retirement-borrowed inputs |
| Callback | Blittable cookie position, notification owner, no user-code reentrancy and explicit D05 close/drain authority |

The supported physical ABI uses fixed scalars and named pointer targets, with up to
two indirections for an output owner slot. Rust tuples, generics, references, strings,
`bool` and `char` do not acquire guessed marshalling behavior. Nullable output storage
and a null owner on failure are separate concepts: output transfer is success-only;
the generated implementation must initialize stored owner results to null on failure.
The schema describes this rule; A05/A11 enforce generated export implementation.

The initial target layouts describe Linux GNU x64 and Windows x64 without OS-specific
runtime code. Only Linux x64 is executed in Task-02; Windows is not certified here.
Unsupported targets require an explicit layout profile and do not fall back silently.

## Maintainer extraction and completeness

`src/Generators.Model/native-inventory` uses pinned `syn 2.0.119`, `quote 1.0.47` and
`proc-macro2 1.0.107` to parse the explicitly supplied adapter file. It reads that one
file and writes one inventory. It never recursively follows modules, starts Cargo,
uses the network or queries a database. Exported functions are retained even when
their parameter type is unsupported. Conditional exported declarations must be
normalized for the target first. Linked-symbol equality catches exports produced by
unlisted files/macros; private engine implementation types are not part of the model.

The build orchestrator, outside any compiler generator, runs three checks:

1. Parsed Rust exports and raw-generator output must have exactly the same unique
   names. A successful extractor process exit cannot hide an unsupported export.
2. Parsed signatures and the sidecar agree, including parameter order and C calling
   convention. Types and lifetime semantics must satisfy the narrow profile.
3. Linked native symbols equal the same complete inventory for the selected target.

The regression actually runs pinned `csbindgen 1.9.8` against the R05-shaped tuple
counterexample. csbindgen reports success while omitting `unsupported_tuple`; the
new completeness gate rejects that output with `NWABI002` at the source function.
The probe is evidence about that exact extractor version, not a claim that all
future generator versions behave identically.

## Determinism and handwritten parity

Canonical JSON uses ordinal property and identity ordering, stable UTF-8 encoding
and no formatting whitespace. Ordered parameters and record fields retain their
semantic order. Independent processes produce identical bytes and SHA-256. Reordered
input object keys and unordered inventories normalize identically. A semantic field
change changes the digest. Fully qualified managed IDs preserve namespace/nesting
and closed-generic distinctions; no short-name guessing occurs.

`VerifyParity` compares complete canonical bytes for handwritten/static descriptors.
Diagnostic locations are kept outside that semantic model. Consumers receive copies
of canonical bytes and immutable record/export arrays; callers cannot mutate a
validated descriptor. The model intentionally does not cache Roslyn symbols, syntax,
compilations, native pointers or runtime resources.

## Build, example and actual verification

Run `python3 tests/Generators.Model/verify.py` from the checkout root using the fixed
SDK/Rust toolchain. The optional `--offline-config` selects a local verification feed
without changing repository audit policy. Dependency restores are locked. The verifier
records each command's stdout/stderr, exact source and dependency bytes, tool versions,
native/extractor/JIT/AOT executable hashes and source stability.

The sample in `samples/api/generators-model` validates a sidecar, proves descriptor
parity and constructs the existing typed A02 metadata. The Linux component test uses
static framework `LibraryImport` stubs to call the actual A01-backed native fixture,
with checked extent edge values, in normal JIT and explicit-RID NativeAOT processes.
The AOT process asserts dynamic code is unavailable. Layout, type, inventory, owner,
async pin, callback, cancellation, malformed input and identity negatives are separate
from successful native calls. Full solution formatting remains an ordinary CI gate;
a local folder whitespace check is not relabeled as that full gate.

This library is internal and not independently distributed yet, so A09 does not claim
an installable application generator or pristine NuGet consumer. A16 and the owning
generator delivery tasks retain those composition gates. Ordinary Entity/DTO changes
continue to use precompiled native assets; this maintainer extraction is not an
application build hook.

## Cost and source comparison

Parsing, validation, sorting and hashing allocate managed build-time data bounded by
the input and item limits. Inventory comparison materializes bounded name arrays.
The application runtime has no model parser, reflection scan or extra FFI call. This
work establishes correctness, not an unmeasured performance/RSS advantage.

The implementation uses the installed stable .NET 11 `System.Text.Json` DOM/writer,
`System.Collections.Immutable`, framework `LibraryImportGenerator` and SHA-256 rather
than a custom JSON grammar or marshaller. The native extractor's exact Cargo.lock
pins mature Rust syntax tooling. The verifier records actual tool/source hashes;
Design R05/source-inspection supplies the pinned csbindgen parser provenance. No
broad trimming/AOT warning suppression is used.
