# Public Native AOT resource inventory

This companion to the 35-case campaign exercises the ordinary public Korda
provider packages in a new application outside the source tree. It supplies
separate cold, suspended, concurrent, exhausted-pool, cleanup and idle-retention
observations. It does not contribute effective long-test seconds or establish
a framework-wide zero-allocation result.

## Inputs and isolation

Build each provider/version in a fresh directory with the shared package-only
builder. The candidate artifact directory must contain both independently
produced provider feeds and their original `release.json` receipts. Supply the
actual packaging producer commit, which can differ from a PR branch head.

```sh
python3 benchmarks/db-perf/managed/build.py \
  --kind resource-probe --provider sqlite \
  --feed "$candidate_artifacts/sqlite/feed" --version "$candidate_version" \
  --output "$external_consumer"

python3 benchmarks/db-perf/resources/run.py \
  --artifacts "$candidate_artifacts" --expected-source-commit "$packaging_commit" \
  --provider sqlite --app "$external_consumer" --output "$fresh_evidence" \
  --processes 3 --concurrency 16 --processor-count 4
```

For PostgreSQL, select `postgresql` for both commands, use its independent feed,
and supply `--postgres-bin "$postgres_bin"` to the runner. The runner creates a
new database for **each** process, starts and joins its own PostgreSQL server,
uses explicit UTF8 and durable settings, and retains its startup/shutdown
receipts. SQLite receives a fresh file. The executable creates a small fixture;
invoke it through this driver rather than against an existing database.

The driver checks the package archive/manifest/native dependency provenance,
actual source and restore graph, standalone AOT ELF, loaded native library,
and all recorded hashes. Before and after applications must contain identical
consumer sources. Only the package inputs change. The 35-case manifest hash is
kept as a reference identity; these diagnostic queries are defined in the
consumer source and do not count as execution of that manifest.

## Boundaries and qualification

The consumer records precise cumulative **all-process** managed bytes and all
three GC counters before and after each named interval. The output includes
raw integer deltas, operation/result/error counts, rows and checksums. No
empty-control or estimated runtime cost is subtracted. The exact framework
split remains unproved; raw process bytes include runtime and continuations.

- Cold source `Build`, opening the bounded session set, and creating a public
  Fluent prepared query have separate intervals. Builder configuration, probe
  arrays, fixture DDL, and fixed raw command/projection construction are outside
  these intervals; this is not a complete cold process-lifecycle total.
- A fixed recursive SQL aggregate returns one Int64 while a simple public
  Fluent query returns eight Int64 values. Both drain the complete result and
  dispose each enumerator. A successful operation includes successful disposal.
- All first `MoveNextAsync` calls in a burst are issued on one thread, with no
  returned ValueTask consumed until launch is complete. Actual incomplete
  status is retained both immediately at return and before consumption.
- `DOTNET_PROCESSOR_COUNT=4` makes the runtime cache-capacity input explicit;
  it is not a CPU-affinity or sustainable-throughput claim. Each required raw
  burst must observe more than `ProcessorCount + 1` incomplete first returns.
  This proves cache misses only for an implementation separately shown to use
  the exact .NET pooling builder (one calling-thread cache slot plus the shared
  slots). The before implementation is not assumed to contain that pool.
- All configured connections remain held while three more opens are attempted
  with `MaxWaiters=1`. A recorded 20 ms admission opportunity precedes cancel.
  Qualification requires actual pending cancellation with the original token
  and a separate `ResourceLimit` rejection; requested concurrency alone earns
  neither observation. Exceptions are formatted after the allocation interval.
- Initial native owners must reach the known source/session counts with no
  operation/cursor/buffer and two equal owner observations. After every burst
  and exhaustion, they must return to that baseline; after source close the
  six tracked owner categories must become zero. Immediate observations and
  every finite settlement interval retain their own bytes/counters. Settlement
  costs are not hidden or deducted from query costs.

Correctness and coverage qualification are distinct. The consumer preserves
raw results on failure; the driver rejects missing or false required coverage,
failed cleanup, incorrect results, incomplete operation ledgers, altered
provenance, and changed boundaries. Historical results are never backfilled
with a successful qualification field.

## CPU, memory and retention scope

The consumer samples managed live bytes after explicitly labelled forced GC,
thread-pool thread count, process RSS, OS-reported peak RSS, and PSS where
available. Forced-GC counters are retained separately and are outside operation
intervals. A one-second idle interval records process user/system CPU; forced
GC takes place before/after that CPU boundary. The observer also preserves raw
process resource time series and wait4 lifetime CPU/maxRSS.

These local measurements are diagnostic. PostgreSQL's live process tree is not
a complete per-query server CPU accounting mechanism; the dedicated sustained
campaign uses its separate cgroup observer for exited backends. SQLite runs
inside the client process. If OS-reported peak samples are inconsistent or
non-monotonic, retain that limitation and the independent raw observations
instead of promoting them to an exact peak claim.

After-GC heap and RSS/PSS include probe report collections, possible live async
locals, runtime caches and native retained memory. Their differences cannot be
attributed exclusively to a framework pool. Native owner counters are not
native malloc/free instrumentation; native-heap peaks remain the responsibility
of the separate allocator diagnostic. Cold initialization, exceptions,
cancellation and exhaustion must remain distinct from normal warm operation.
