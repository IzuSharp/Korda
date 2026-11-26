# PostgreSQL Find SQL layers

This bounded Native AOT diagnostic separates two differences between the
independent ORM's original Find SQL and Korda's generated Find SQL. It leaves the
formal workload, production code, and original independent ORM results intact.

| Lane | SQL change | Ordered parameters |
| --- | --- | --- |
| `original` | Exact original Dapper `FindSql` | `tenant: Int64 = 1`, `id: Int64 = 1024` |
| `qualified` | Korda's qualified columns, aliases and predicates, without `LIMIT` | `p1: Int64 = 1`, `p2: Int64 = 1024` |
| `limited` | Complete Korda Find SQL, including its parameterized limit | `p1: Int64 = 1`, `p2: Int64 = 1024`, `p3: Int64 = 2` |

All lanes use one unchanged AOT executable and the same public
`QuerySingleAsync<Order>` operation on one open Npgsql connection. Dapper is
2.1.100, Dapper.AOT is 1.1.0, and Npgsql is 11.0.0. Command caching is disabled;
`MaxAutoPrepare` is zero. Every operation creates the lane's typed argument record
and returns the same complete nine-field `Order`, including decimal conversion,
typed JSON, nullable values and strings. The additional limit argument and binding
remain part of the `limited` measurement.

`contracts.py` derives the SQL and expected result from the exact committed
queries-v3 workload; it does not carry a separate handwritten query contract.
`build.py` copies exact source slices for `Order`, `OrderSettings` and their type
handlers from the original Dapper benchmark. It retains the complete source,
byte ranges and hashes of every extracted segment. It adds an explicit nullable
context for the generated file and omits unrelated result types and JSON roots.
The original dataset formulas and digest implementation are copied unchanged.

## Reproduce

Use the repository's locked .NET and native toolchain on Linux x64. Build outside
the source tree. The optional support cache contains previously restored
non-Korda packages and is validated through the build's actual package assets.

```sh
python3 -B benchmarks/db-perf/sql-layers/build.py \
  --workload /absolute/path/to/exact/queries-v3/workload.json \
  --support-cache /absolute/path/to/support-cache \
  --dotnet /absolute/path/to/dotnet \
  --output /absolute/path/to/fresh/external-app

python3 -B benchmarks/db-perf/sql-layers/run.py \
  --app /absolute/path/to/fresh/external-app \
  --runtime-zip /absolute/path/to/original-campaign/runtime.zip \
  --output /absolute/path/to/fresh/find-evidence
```

The builder forces `DOTNET_PROCESSOR_COUNT=2` and serial compilation, while the
runner removes that variable for actual consumers. It rejects JIT execution,
project references, a changed fixture, a changed package or executable, and
existing output directories. Failed builds and runs retain their original
receipts and logs. Use a new directory for a corrected attempt; never replace a
successful process merely to improve its measured result.

The runner verifies the original runtime artifact, every runtime-bundle member,
the initializer ELF and its native images, and the original initializer's source
and build receipt before reuse. The archived Korda initializer creates the full
unchanged fixture: 8 tenants, 1024 orders per tenant, 256 customers, original
indexes and statistics. This is not the separate narrow `List<BusinessRow>`
fixture. Each independent process receives a freshly initialized database;
initialization and database restart are outside measured query windows.

The frozen schedule is:

1. Timing round 0: `original`, `qualified`, `limited`.
2. Timing round 1: `limited`, `qualified`, `original`.
3. Timing round 2: `qualified`, `original`, `limited`.
4. Separate instrumented processes: `original`, `qualified`, `limited`.

Each timing process warms 1024 operations and measures 8192 operations. Each
instrumented process warms 8 and measures 8. Both phases perform one separately
counted full result readback after the measured interval. All processes are
serial. Plan logging is absent from the nine timing processes. The three plan
processes retain actual PostgreSQL statements, ordered parameter values,
auto_explain plans and backend identities; their timings never enter the timing
comparison.

## Evidence and limits

Every operation validates all nine fields and consumes the complete result into
the original typed digest. Raw output keeps planned, issued, succeeded, failed,
retry, row and warmup counts, individual latency ticks, incomplete async returns,
cross-thread continuations, precise cumulative process allocation and generation
GC counters, and user/system CPU endpoints. Failed latency array tails remain
raw evidence; only the successful prefix can enter quantiles.

Allocation endpoints cover the entire managed process, including continuations
on other threads. The query window includes result allocation, argument records,
driver/ORM work, and complete result consumption. BCL CPU endpoint observation
may add small common measurement allocations. Setup, disposal and JSON receipt
serialization are outside that interval. There is no exact framework/business
allocation split and no zero-allocation claim. Application connection disposal,
pool clearing and task-owned PostgreSQL shutdown are recorded separately;
internal provider ownership is not instrumented.

`analyze.py` checks original source/package/ELF bytes, counters, full result
digests and ordered parameters. It requires exactly three independent timing
processes and one separate instrumented process per lane. Instrumented evidence
must contain exactly 17 actual statements and plans matching that lane's server
SQL and parameter values on its recorded backend. Server parameter OIDs and
generic/custom-plan counts remain unknown unless actually collected; this
diagnostic does not infer them from plan shapes.

The diagnostic helps explain SQL and parameter differences within the same
independent ORM. It does not measure the original transaction workload, replace
the formal 35-case results, certify formal throughput/latency acceptance, or
contribute any long-test seconds. Record the actual environment beside results.

## Recover successful original native plans

The original native per-operation and reuse preparation diagnostics already
contain actual range, deep-offset and early-stop plans. Recover them offline
instead of running successful work again:

```sh
python3 -B benchmarks/db-perf/sql-layers/recover_native_plans.py \
  --profiles /absolute/path/to/original-profiles.zip \
  --runtime /absolute/path/to/original-campaign/runtime.zip \
  --output /absolute/path/to/fresh/native-plan-recovery
```

The recovery script verifies both archived artifacts, exact native ELF/build
identity, workload and fixture source, original result oracles, raw server logs
and the original plan collector. Both archived layers use the same
`protocolConfirmation=drain-only`; only physical preparation changes. Its report
keeps the original harness/candidate identities and explicitly separates these
historical diagnostics from any current candidate's formal campaign. Observed
shape occurrence ordinals are evidence; generic/custom-plan counters and server
parameter OIDs were not collected and remain null.
