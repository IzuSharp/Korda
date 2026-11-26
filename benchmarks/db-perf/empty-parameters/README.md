# Dynamic empty-parameter allocation seam

This is a Native AOT internal diagnostic. It invokes the actual
`QueryContext.Values` implementation through the existing Fluent test friend
assembly, with real SQLite and PostgreSQL sources, sessions, and query contexts.
Source/session/model setup, input construction, output JSON, and public SQL
correctness checks are outside allocation windows. It is not an external
PackageReference acceptance test or an end-to-end business workload.

Build with the repository-pinned SDK and Clang. `EmptyParameterInputs` contains
provider DLLs and native assets from the exact independently built provider
packages being diagnosed:

- `lib/net11.0/Korda.Database.Sqlite.dll`
- `lib/net11.0/Korda.Database.PostgreSql.dll`
- both `libnw_database_*.so` files at the root and their original
  `runtimes/linux-x64/native/` paths
- the corresponding `Korda.database.*.assets.json` SDK output records, with
  package version, native source path, actual output path, and SHA-256 matching
  the provider DLL and files

The diagnostic loads the SDK's root native output path. It requires the native
source/session counters to read one each while live, and checks `/proc/self/maps`
before accepting later zero-owner observations. Engine manifest/hash validation
is unchanged. Internal product assemblies are built through ProjectReference;
provider DLLs supply only the unchanged provider fixture.

```sh
dotnet restore benchmarks/db-perf/empty-parameters/EmptyParameters.csproj \
  -r linux-x64 -p:PublishAot=true -p:EmptyParameterInputs=/absolute/inputs \
  --disable-parallel -m:1 -nr:false
dotnet publish benchmarks/db-perf/empty-parameters/EmptyParameters.csproj \
  -c Release -r linux-x64 --no-restore -p:EmptyParameterInputs=/absolute/inputs \
  -p:UseSharedCompilation=false -m:1 -nr:false '-t:Rebuild,Publish' \
  -o /absolute/output
/absolute/output/Korda.Query.Fluent.Tests result.json QueryContext.cs \
  SOURCE_COMMIT postgresql://USER@127.0.0.1:PORT/DATABASE /absolute/fresh.sqlite
```

Use separately retained baseline and candidate outputs, complete source
snapshots/hashes, and a serial `B/C/B/C/B/C` order. Build before measurement;
never run builders alongside the ELF. Force rebuilding when changing source
snapshots so preserved file timestamps cannot cause stale incremental reuse.
Three independent processes per variant each execute both profiles. Each case
has 1,024 warmup calls and 16,384 measured calls, except the single first dynamic
empty-frame access. Every call compares and consumes the full encoded bytes.
The 96-byte empty golden is constructed independently from the fixed wire
layout and canonical metadata. Cold means the first empty-frame access after
source/session/model setup, not process initialization.

Raw output includes whole-process cumulative managed allocation endpoints,
all GC-generation endpoints, operation counts, full frame hex, CPU endpoints,
monotonic boundaries, process and executable identity, source hash, native
hashes and loaded mappings. Runtime/observer noise remains in the total. GC
counts belong to the whole process heap and are not split into framework and
business categories. No DTO, list, or string business result is created inside
this isolated encoder window. The later public database checks do create and
consume results, but their allocations are not part of the encoder window.

Controls cover explicit empty parameter sets, a nonempty parameter, unchanged
prepared empty encoding, missing/extra/wrong-identity rejection, independent
query contexts on concurrent threads, actual driver acceptance, separately
captured lazy streams, and complete owner drain. Do not infer whole-framework
zero allocation or end-to-end CPU/latency/throughput improvement from the
isolated empty-frame result. Final candidate packages require separate public
API, Native AOT, and mixed-workload validation.
