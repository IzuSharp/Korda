# Immediate projection scratch allocation probe

This Linux x64 Native AOT microprobe compares an independently owned projection
wire array with a caller-owned 4096-byte stack buffer consumed synchronously.
It is an internal `ProjectReference` fixture. Real public-package Fluent queries,
business result materialization, database work and long-run performance acceptance
are separate measurements.

Build the original candidate before editing product code, retaining its source and
executable hashes. The identical probe source selects only the encode callsite:

```sh
dotnet publish benchmarks/db-perf/projection-scratch/ProjectionScratchProbe.csproj -c Release -r linux-x64 -p:PublishAot=true -p:RestoreLockedMode=true -p:ProjectionScratch=false -o /tmp/projection-before
/tmp/projection-before/Korda.Query.Binding.Tests /tmp/before.json /path/to/frozen-before/OperandEncoding.cs BEFORE_COMMIT

dotnet publish benchmarks/db-perf/projection-scratch/ProjectionScratchProbe.csproj -c Release -r linux-x64 -p:PublishAot=true -p:RestoreLockedMode=true -p:ProjectionScratch=true -o /tmp/projection-after
/tmp/projection-after/Korda.Query.Binding.Tests /tmp/after.json /path/to/frozen-after/OperandEncoding.cs BEFORE_COMMIT
```

Run three independent processes per candidate, interleaving their order. Every
case warms 1024 operations and measures 4096 calls, comparing and consuming all
wire bytes before the scratch lifetime ends. Retain full cumulative process
allocation/GC and user/system CPU endpoints, input fingerprints, encoded bytes,
executable/source hashes and failed attempts. Do not apply compiler-only processor
limits to measured processes.

```sh
python3 benchmarks/db-perf/projection-scratch/compare.py --before /tmp/before-0.json /tmp/before-1.json /tmp/before-2.json --after /tmp/after-0.json /tmp/after-1.json /tmp/after-2.json --output /tmp/projection-comparison.json
```

The fixture creates no business DTO, list or string result during the measured
window. A zero cumulative increment supports only that exact prebuilt encoder
path; it does not establish zero allocation for `Select`, a complete query or the
framework. Wide frames retain their owned-array fallback. Exception observations
in this probe exercise the original owned entry point; the normal Query.Binding
suite separately checks the borrowed entry point's poisoned buffers, exact
4096/4097 boundaries, invalid metadata/binding order, independent concurrent stack
buffers and synchronous native retention after scratch overwrite.

The short Query.Binding regression preserves raw whole-process and current-thread
endpoints in three 4096-operation windows. Its zero-byte budget was calibrated
from three independent Native AOT processes (nine actual windows), with no
subtraction. Business allocation and process GC ownership remain separate concepts.
Shared-host micro CPU/timing values are descriptive and do not establish a stable
throughput or tail-latency improvement.
