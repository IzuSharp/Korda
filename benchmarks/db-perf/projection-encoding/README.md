# Projection encoding allocation probe

This Linux x64 Native AOT microprobe isolates the internal projection wire encoder.
It creates projection metadata, expression IDs and the codec-binding delegate before
warmup, then encodes and fully compares/consumes every byte of ten projection shapes.
Whole-process cumulative allocated bytes, all three generation counters and process
CPU endpoints remain in the original JSON. The output byte arrays are framework
allocations. This fixture creates no business DTO, list or string result inside its
measurement windows; it is not a public PackageReference or database acceptance.

The project deliberately uses the existing internal test friend assembly name. It
does not add a public API. Build it against the exact before and after worktrees,
retain both immutable executables, and alternate at least three fresh processes per
lane. Use .NET 11, Linux x64, Release and the same native toolchain for both builds:

```sh
dotnet publish benchmarks/db-perf/projection-encoding/ProjectionEncodingProbe.csproj \
  -c Release -r linux-x64 -p:PublishAot=true -p:RestoreLockedMode=true \
  -p:UseSharedCompilation=false -nr:false -m:1 -o /absolute/output/aot
/absolute/output/aot/Korda.Query.Binding.Tests \
  /absolute/output/result.json \
  /absolute/worktree/src/Query.Binding/OperandEncoding.cs \
  FULL_SOURCE_BASE_COMMIT
```

Record the source status as well as the source-base commit: the original experiment
was measured before its production edit was committed, and its actual encoder
source SHA and ELF SHA are the identities that bind those observations. Do not
replace a dirty-source identity with the later commit or relabel an old binary.

The baseline comparison is retained in Plan batch `DB-Perf-01`. Its six interleaved
processes show 12,184 to 1,152 B/encode for eight fields with expressions and
48,088 to 4,296 B/encode for 32 fields with expressions. Those figures include the
owned final byte arrays. A 1,096 B window discrepancy in two smaller shapes remains
unattributed in the original raw counts. Timing on the shared development host is
diagnostic only.

The normal Query.Binding component gate separately checks fixed baseline goldens,
binding and exception order, invalid metadata, exact 4 MiB boundaries, and concurrent
input/output ownership. Its `--projection-allocation` consumer runs a short measured
budget calibrated from three actual AOT processes. SQL semantics, public package
consumption, business-result allocation, sustained CPU and latency remain separate
database benchmark gates.
