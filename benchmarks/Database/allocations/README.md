# Public-package Native AOT allocation preflight

This short probe measures unoptimized Korda through a normal `PackageReference` application copied outside the product source tree. Each provider is restored and published independently, with exactly one Korda top-level package. It is a baseline/preflight, not the dedicated four-hour comparison campaign.

The generated `DbTable` model has one `long` primary key. Setup creates and inserts exactly 256 rows. Dynamic Fluent and parameterless prepared queries read the same ordered scalar projection. Every query validates all values and checksum `32896`, reads the terminating `false`, and disposes its enumerator. Dynamic query disposal is also inside the complete-query measurement. No DTO, string, result list or result array is requested in an interval.

## Boundaries

- Complete dynamic query: construction through real cursor cleanup, 256 rows.
- Complete prepared execution: an already prepared parameterless plan through the same cleanup, 256 rows. Preparation is outside this interval and is not claimed allocation-free.
- Within first batch: await its first row before measuring the remaining 127 rows. Every measured move must report `IsCompletedSuccessfully` and return synchronously. A suspension makes the run fail rather than claiming a zero-byte synchronous path.
- Supplemental fetch: one move crossing the 128-row boundary, including the first mapped row of the second batch. It is not a batch-only transport measurement. An incomplete observation and an actual managed-thread change are recorded separately; an incomplete observation alone is not proof that the await suspended.
- Empty synchronous enumerator: the same non-async advance loop and counter boundary, with no production query. It runs while the source remains open.
- Empty asynchronous control: repeated `Task.Yield`; this is a non-equivalent runtime/harness control and is never subtracted from native-operation measurements.
- Source-open idle: a 100 ms no-business-work interval, including background work.

Each measurement retains raw start/end values from `GC.GetTotalAllocatedBytes(true)`, every generation's collection count, operation count, row count and checksum. Ratios are written only after the raw totals. The snapshots cover all managed threads. A zero GC count, rounding, a sampling miss, or one zero-byte row window is not whole-framework acceptance.

The outer async measurement method suspends once before its counter boundary so that its own state is already materialized. There is no per-query async helper in the harness. Remaining runtime/continuation infrastructure is not assumed free: exact framework attribution stays `unproved`, and no estimated subtraction is made. The existing source audit identifies production allocation sites independently. File IO, JSON, hashes, owner diagnostics and console reporting occur outside measured intervals.

## Reproduction

Use the actual .NET 11.0.100 SDK and Clang binaries. The runner uses ordinary serial MSBuild/ILCompiler options and does not use a launcher or wrapper. Supply a fresh external work directory and exact original candidate `release.json`/`feed` directories. SDK support `.nupkg` files are staged from an existing cache; they do not contribute another Korda package. There is no source/project reference.

```bash
python3 benchmarks/Database/allocations/run.py \
  --artifacts /absolute/baseline-artifacts \
  --work /absolute/outside-korda/allocation-preflight \
  --evidence /absolute/Plan/ledger/evidence/batches/DB-Perf-01/preflight/korda-allocation \
  --dotnet /absolute/dotnet/dotnet \
  --clang-bin /absolute/llvm/bin \
  --postgres-bin /absolute/postgresql/bin \
  --support-cache /absolute/nuget-packages \
  --warmup 64 --iterations 128 --processes 3
```

The runner starts its own PostgreSQL cluster on an isolated loopback TCP port with `-k ''`, leaves durability enabled, and stops only that cluster. SQLite uses a separate file for each run. At least three independent consumer processes execute per provider, interleaving the providers between repetitions. These processes do not run concurrently with each other. Physical host exclusivity is not established: elapsed ticks are diagnostic and cannot rank throughput or latency, particularly while other build processes run.

The evidence includes raw JSON and stdout/stderr, SDK/compiler/server versions, package resolution, release receipts, exact package/executable hashes and the provider library actually observed in `/proc/self/maps`. All six live native-owner counters must return to zero after source disposal. SQL dispatches and protocol submissions are reported separately; physical network round trips remain unmeasured.

Parameter binding, transaction allocation, cancellation, high-concurrency allocation, native heap measurements, four-way comparisons and the long mixed-load segments remain outside this probe. Passing it does not mark those requirements complete.
