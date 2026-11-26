# Buffered business-result allocation diagnostic

This is a separate attribution diagnostic for DB-Perf-01. It does not change the
frozen 35-case campaign or contribute long-run seconds. It uses one identical
consumer source/project for Korda and Dapper.AOT on both providers, with ordinary
PackageReference and a real standalone Linux Native AOT executable.

Each operation returns an actual `List<BusinessRow>` containing 100 class DTOs,
80 nonempty non-interned strings and 20 NULL strings. The row values are identical
on every route. Korda uses public Fluent query construction, generated projection
and `ToListAsync`; Dapper uses `QueryAsync<BusinessRow>().AsList()`. The probe
rejects an extra diagnostic list copy. Both routes dynamically construct their
query and bind tenant/limit values on every operation. SQL spelling and parameter
names remain visible in each receipt; this is not a physical prepared-plan test.

## What is counted

The end-to-end interval includes query construction, binding, complete buffered
materialization, full value/checksum consumption and query/cursor cleanup.
Session/source setup and final return are outside this particular boundary.
The primary counter is precise cumulative all-process allocated bytes, so actual
async continuations are included. The common harness's async wrappers are also
included. Original operation/row counts and all three GC generation counters are
retained before any division. An incomplete Task return and a cross-thread await
are reported as separate observations, without declaring every race a suspension.

The isolated business control runs after database owners and provider pools have
drained. Its template is the actual returned object graph. It allocates one list
with that exact retained capacity, 100 new DTOs and a fresh string for each
nonnull output. It neither interns strings nor reuses DTO references. Full graph
shape, content and independence checks occur outside the measured control;
complete per-iteration consumption prevents unused results. Raw process and
current-thread counters are retained for these synchronous windows. Discarded
list-growth arrays are not part of the retained business graph and are not
included in this control.

This is **an attribution control, not an exact in-query allocation trace**.
End-to-end bytes minus its per-graph result is only an unresolved estimate of
framework, runtime, harness and possible model mismatch. The analyzer leaves
`exact_framework_bytes` null and never establishes framework 0 B by subtraction.
Collection events belong to the shared process heap and are not divided into
business and framework GC counts. Required business outputs need not allocate
zero bytes. The existing scalar-stream probe supplies the separate numeric lane;
this diagnostic does not substitute a scalar checksum for a buffered DTO result.

## Build and execute

Use an independent output directory per route/provider/package. Before and after
must use these same source/project/global inputs; only candidate packages change.

```sh
python3 benchmarks/db-perf/business-results/build.py \
  --route korda --provider sqlite \
  --feed "$candidate_artifacts/sqlite/feed" --version "$candidate_version" \
  --support-cache "$package_cache" --dotnet "$dotnet" \
  --output "$external_consumer"

"$external_consumer/out/BusinessResults" \
  "$fresh_sqlite_file" "$new_result_json" "$actual_primary_nupkg" \
  "$package_source_commit" - 128
```

For Dapper choose `--route dapper`, omit feed/version, and take its actual Dapper
archive path from the retained build receipt. SQLite Dapper requires the exact
shared `libsqlite3.so` selected by the native comparator as the fifth executable
argument. The probe verifies the actual mapped image path and hash. Npgsql is a
managed client; it has no claimed libpq image. Select `--provider postgresql`
and pass the fresh database's connection string or local `postgresql://` URI.
Create a distinct PostgreSQL database for every process with explicit UTF8 and
the campaign's durable settings. The executable creates only its own
`business_result_orders` fixture and fails if the table exists; it never drops
an existing table. Caller-side runner/observer commands and service lifecycle
receipts must be retained separately.

Run at least three independent processes for each of before, after and the
independent ORM on both databases. Interleave route processes where practical.
Each process warms 64 complete queries, retains three end-to-end windows of the
requested operation count, and three synchronous business controls of 1,024
graphs. Normal queries consume all 100 rows and validate every field. A baseline
sample must have the same graph shape/content as its control; list capacity is
reported per route rather than silently normalized. All six Korda native-owner
counts must drain to zero. Independent-provider visibility is limited to disposed
application connection and cleared pools, and is labelled accordingly.

The serial runner creates a fresh SQLite file or PostgreSQL cluster for every
process and retains the original command, resource time series, database settings
and server shutdown receipt. The shared observer is read from an exact Git commit
and hashed before use, so another worker's uncommitted observer edit cannot
silently change the executing controller.

```sh
python3 benchmarks/db-perf/business-results/run.py \
  --app "$external_consumer" --source-label "$package_source_commit" \
  --output "$fresh_evidence_directory" --database-root "$fresh_database_directory" \
  --processes 3 --operations 128 --observer-ref "$reviewed_observer_commit"
```

Supply `--postgres-bin` for PostgreSQL or `--sqlite-native` for the independent
SQLite route. `--processes 1 --operations 1` is a separate capability smoke only;
the formal analyzer requires at least three distinct process identities per cell.
It identifies an execution by PID, UTC start and executable hash, so copying one
result to several paths cannot manufacture independent repetitions, while actual
PID reuse at a different start time remains possible.

```sh
python3 benchmarks/db-perf/business-results/analyze.py \
  --input before=/absolute/before/process-0/result.json \
  --input before=/absolute/before/process-1/result.json \
  --input before=/absolute/before/process-2/result.json \
  --input after=/absolute/after/process-0/result.json \
  --input after=/absolute/after/process-1/result.json \
  --input after=/absolute/after/process-2/result.json \
  --verify-local --output /absolute/new-summary.json
```

Add every provider and independent ORM process with further labelled inputs. The
analyzer rejects repeated process files, different consumer sources, changed
package/executable/native inputs within a cell, invalid raw arithmetic, wrong
rows/digests, unresolved failures or invented exact framework attribution. Raw
results and failures are immutable: use new output paths for each attempt.
Production native allocator/CPU/latency acceptance remains in its own campaign.
