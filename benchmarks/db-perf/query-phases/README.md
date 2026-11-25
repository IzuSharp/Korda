# Same SQL, same C++ Host: local query phases

This local Linux Native AOT diagnostic compares ordinary Fluent, warmed
`.Prepare()` and complete parameterized SQL through the same C++ Host. It is not
a CI job. The three routes return the same complete 100-element generated DTO
list, with identical ordered IDs, strings and nulls. A single persistent session
and physical connection are used. Tenant values alternate between 1 and 2.

The SQL is obtained from the native Fluent renderer. The parameterized query must
render exactly the same text; the raw Host template replaces its single provider
marker with the equivalent typed logical parameter. Both observed Fluent SQL and
raw template are recorded. PostgreSQL renders `$1` in both routes. SQLite Fluent
renders `?` and the raw-template compiler renders `?1`: these address the same
sole parameter at ordinal one, but are **not byte-identical SQL**. This known
marker normalization is the only SQL difference. Predicates, selected columns,
aliases, null ordering, parameter types/values and result mapping are identical.
A Fluent/prepared text mismatch aborts.

Each operation records construction, submit-to-first-row, remaining read through
EOF, and release/retirement timestamps. Whole-process window counters preserve
allocation totals before division, all three GC counts, CPU, RSS and existing
native owner/SQL/protocol counts. All routes materialize the complete list. Result
verification is outside the four phase durations and inside the full window; it
is diagnostic overhead, not a library performance claim. Preparation and first
executions have separate records; these are not independent process-cold runs.

Use the existing external PackageReference consumer builder with the pinned
local SDK/compiler on PATH. It copies the diagnostic outside the repository,
isolates application packages, checks the standalone AOT ELF and records the
source, exact package, executable and native image identities, retaining generated code:

```sh
python3 benchmarks/db-perf/managed/build.py --kind query-phases --provider sqlite \
  --feed /absolute/path/to/exact-feed --version 0.1.0-ci.1 \
  --output /absolute/path/to/new-external-consumer
/absolute/path/to/new-external-consumer/out/DbPerf.QueryPhases \
  /absolute/path/to/new-database.sqlite result.json 256 3 initialize
```

Use `--provider postgresql` and the dedicated local PostgreSQL connection string
for that build. Only the first process on an empty fixture uses `initialize`. The
diagnostic creates its own `query_phases` table, never drops or clears an existing
table, and validates all result fields on every operation. Repeat/interleave
independent baseline and candidate processes on the same host. Store actual logs,
package/executable/native hashes and raw receipts in the existing Plan batch.

The session queries `version()` or `sqlite_version()` before any measured phase
and records the returned database version. Summarize fresh receipts with
`python3 benchmarks/db-perf/query-phases/summarize.py receipt.json --output summary.json`.
The summary preserves raw counter totals and computes nearest-rank p50/p99 without
trimming samples. It does not grant measurement or environment acceptance.

This phase diagnostic does not replace the local mixed-load correctness runs or
the final candidate/Dapper.AOT 1,800-second segments for each provider.
