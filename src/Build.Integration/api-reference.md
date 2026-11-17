# Build integration tool reference

A13 exposes no application runtime types. The existing Core descriptor APIs remain
owned by A02 and buffer APIs by A04. These tools execute only during explicit
maintainer work; they are not shipped as application runtime dependencies.

| Entry | Inputs and outcome |
| --- | --- |
| `build_native.py build --root ROOT --profile PROFILE` | A reviewed profile with exact source/binding hashes, tool pins, target/features, Cargo and native dependency inventories; returns the path and SHA-256 of a completed manifest |
| `build_native.py verify --root ROOT --profile PROFILE --manifest MANIFEST` | Rechecks current source/binding identities, native bytes, architecture, exact exports and ELF dependency names; never infers a previous successful output |
| MSBuild `NativeWeaveBuildNative` | Requires `NativeWeaveMaintainerBuild=true` and an existing `NativeWeaveBuildProfile`; no dependency connects it to ordinary Restore/Build/Publish |
| `ApiReference ROOT DOTNET_ROOT CONFIG [--write]` | Reads a compiled assembly/XML and compares its whole public/protected symbol set with the explicitly reviewed baseline files, then checks or writes its generated reference |
| `ApiReference ... --snapshot` | Produces a proposed compiled API inventory in the configured artifact output; it does not update or accept a reviewed source baseline |
| `compose_docs.py --config CONFIG --output ARTIFACT_DIRECTORY` | Performs the compiled API/XML gate, pinned DocFX 2.81.0 metadata generation and warnings-as-errors HTML build; no website publication |

Profiles use schema version 1. Unknown/missing fields, path traversal, unexpected
targets, unbound local Rust source/workspace configuration, undeclared Cargo/native
dependencies and export drift fail. The current executed target is Linux x64 GNU.
Source hashes establish byte consistency, not the safety of an arbitrary pointer,
publisher identity or a complete release closure; A22/A16 own those boundaries.

Output lives below the current worktree's `artifacts/build-integration`. Cache keys
include exact tools, target, profile, features, default-feature selection, lock,
runner and reviewed profile bytes. A per-profile lock rejects competing builds in
the same worktree; separate worktrees have distinct output/cache directories. No
global compilation lock is introduced. Failed/cancelled attempts retain logs and
remove incomplete staging and the previous advertised output. Completed artifacts
are immutable directories selected only from Cargo `compiler-artifact` messages.

Manifests contain exact native/source/tool/lock/binding hashes, exported symbols,
dependency names, features, actual source commit and a truthful dirty flag. They
contain no timestamp or machine-specific source path. Native hashes are computed
before the manifest; its hash is kept externally. Identical manifests are a tested
result for the recorded fixtures, not a universal promise across different linkers
or operating systems. Build logs retain actual commands and paths separately.

XML validation requires exact parameter names, generic parameter coverage, non-void
returns, summary and member/inherited type remarks. Declarations preserve
nullability, defaults, generic constraints and consumption attributes. Compiler
symbols supply signatures; source syntax supplies only record/synthesized-member
classification. Semantic compatibility and relevant exception-policy review remain
human/independent-review obligations under A22, not claims made by a text diff.
