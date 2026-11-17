# Maintainer builds and compiled documentation

**Status:** Linux x64 component tooling. Ordinary applications consume precompiled
native assets with normal `dotnet build`, `dotnet run` and explicit-RID Native AOT
publication. A maintainer build is an explicit operation; neither an application
generator nor an ordinary MSBuild target starts Cargo.

## Minimal maintainer path

Use the repository's pinned .NET SDK 11.0.100 and Rust/Cargo 1.100.0 with a C linker,
`nm`, `readelf` and Python 3.9+. The executable conformance profile is deliberately
separate from the Engine ABI and from published packages:

```sh
python3 src/Build.Integration/build_native.py build \
  --profile samples/api/build-integration/linux-x64.json
```

The command reports one completed manifest. Pass that exact path to `verify` before
staging or consuming its native file. Do not select a library by searching a Cargo
output directory: Rust/Cargo may change intermediate layouts, and an old successful
file is not evidence that the current build succeeded.

The sample uses only precompiled `nativeweave_build_conformance` bytes. Its minimal
path reads a version and value; its advanced boundary value verifies the documented
modulo behavior. Normal managed build and Native AOT compilation execute no Rust
tool. Native search/asset loading for production engines remains A02/A14/A16's
existing responsibility; the sample's explicit fixture directory is not an
application configuration pattern.

## Reviewed inputs and safe staging

The profile records each local Rust source, workspace manifest, Cargo lock and
binding input by SHA-256, together with exact features, export inventory and allowed
native dependencies. Changing an input requires review and a new profile digest;
the runner never regenerates a baseline to make an unexpected change pass. It
rejects external path dependencies and undeclared compiler environment overrides.

Only Cargo's current JSON `compiler-artifact` record can select the native output.
The runner verifies architecture, final exported symbols and native dependency
names before copying it into an incomplete staging directory. It checks source
bytes again before atomically selecting the completed result. Failed compilation,
wrong exports/dependencies, cancellation or stale source removes the old advertised
success and leaves diagnostic logs. Previously completed immutable directories are
retained for explicit verification; they are never silently treated as a new build.

Separate worktrees may build concurrently. Two builds targeting one worktree/profile
fail with a clear ownership error rather than overwriting each other. The build
runner cancels Cargo's process group and waits for cleanup; it does not leave an
unobserved build script writing a package after cancellation.

An explicit MSBuild entry is available for maintainer workflows:

```sh
dotnet msbuild samples/api/build-integration/Build.Integration.Sample.csproj \
  -t:NativeWeaveBuildNative -p:NativeWeaveMaintainerBuild=true \
  -p:NativeWeaveBuildProfile=samples/api/build-integration/linux-x64.json
```

The target is not connected to Restore, Build or Publish and is not included in a
consumer package. The ordinary no-Rust path is verified separately with executable
Rust invocation traps; that is not described as a pristine-machine release test.

## One compiled API and documentation authority

Module authors maintain XML, their reviewed public API baseline, guide and runnable
samples together. The shared `ApiReference` tool reads the actual compiled assembly
and XML, compares the complete declared symbol set and emits exact signatures.
`--snapshot` produces a proposed review artifact only. A normal check cannot accept
an unexpected symbol, removed overload, nullable/default change or missing parameter
documentation merely by rewriting a baseline.

`compose_docs.py` then restores the pinned DocFX 2.81.0 tool, executes metadata
generation against those .NET 11 assembly/XML bytes and builds reference/guide HTML
with warnings as errors. Tool/compiler-host dependencies are separate from the
application target. The local tool manifest permits running the documentation tool
on an installed newer host runtime; that does not change its pinned package version
or the application's framework.

```sh
python3 src/Build.Integration/compose_docs.py \
  --config tests/Build.Integration/api-fixture.json \
  --output artifacts/docs/build-conformance
```

Generated HTML is labelled with its component support/evidence scope. No website
is published by this command. A16/B24 retain final package/doc-version composition
and pristine consumption; no future API is presented as an installable release.

## Verification and limits

The executable suite tests actual cold and cached Cargo builds, simultaneous
independent worktrees, byte-identical fixture manifests, stale binding input,
intentional compiler failure, unexpected exports, unapproved dependencies, native
artifact substitution, same-worktree contention and cancellation during a real
Cargo child build. The sample then runs in JIT and Native AOT with Rust invocation
traps. Compiled API/XML checks and actual DocFX metadata/HTML are separate recorded
checks. Component evidence is retained in Plan under `ledger/evidence/A13`.

Reproducibility is established only for the tested input/tool/target combination.
Changed tool or linker identity produces a different manifest/cache key. A manifest
hash does not authenticate a publisher, replace signature/identity checking or
complete A22's compatibility decision. Existing automatic preview publication keeps
its existing package scope; these tools do not publish a new component package.

## Verification scope

The current release/acceptance profile is Linux x64 GNU `cdylib`. The runner also
selects `staticlib` compiler-artifact records, but no final static link, retain rule
or static consumer is certified here. D10 requires those target-specific checks
when an actual static product profile is introduced. Other platforms are unexecuted.

Manifest verification is an explicit local maintainer operation. It checks all
profile identities, binding/lock/runner digests, current source commit, actual tool
identities and the exact native path/bytes/export/dependency inventory. It performs
no publisher authentication; signed package identity belongs to the release gate.
The existing `python3 eng/build.py native` Linux path uses this runner, and the
HelloWorld package embeds the verified provenance in its existing manifest.
