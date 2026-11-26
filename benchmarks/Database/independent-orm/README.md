# Independent micro-ORM Native AOT capability probe

This prerequisite runs ordinary, source-tree-external C# applications against real
PostgreSQL and SQLite using Dapper.AOT. It does not measure performance, allocation,
or the complete DB-Perf-01 workload. Passing it admits this micro-ORM as a candidate
for the independent C# ORM comparison column.

## Inputs and scope

| Component | Fixed version |
| --- | --- |
| .NET SDK / target | 11.0.100 / net11.0, Linux x64 Native AOT |
| Dapper / Dapper.AOT | 2.1.79 / 1.1.0 |
| PostgreSQL provider / server | Npgsql 11.0.0 / PostgreSQL 18.0 |
| SQLite provider | Microsoft.Data.Sqlite.Core 11.0.0 |
| SQLitePCLRaw provider / resolved core | provider.sqlite3 2.1.11 / core 3.0.5 |
| SQLite source / C compiler | 3.51.3 / Clang 23.1.5 |

Dapper 2.1.79 matches the upstream Dapper.AOT 1.1.0 tested version. Package lockfiles
are committed and restore uses locked mode. A real Native AOT runtime guard rejects
JIT execution. Each project's emitted Dapper interceptor source must contain the
seven tested call sites; the runner retains it and its SHA-256.

The probes create a table, insert with parameters and commit, materialize a typed
row with `QuerySingleAsync<T>`, enumerate `QueryUnbufferedAsync<T>`, update and roll
back, read the original persisted state with `QuerySingle<T>`, and clean up. They
check Unicode, NULL and decimal `12.25` as well as the integer identifier.

## Run outside the source tree

Provide actual tool and source paths. The output directory must be new and outside
the Korda repository; this preserves earlier successful and failed records.

```sh
python3 benchmarks/Database/independent-orm/run.py \
  --output /absolute/path/DB-Perf-01-orm-probe-001 \
  --dotnet /absolute/path/dotnet \
  --clang /absolute/path/clang \
  --sqlite-source /absolute/path/sqlite3.c \
  --postgres-bin /absolute/path/postgresql-18/bin
```

`NW_POSTGRES_BIN` can supply `--postgres-bin`. Optional `--sdk-cache` exposes only
SDK/runtime NuGet directories as fallback folders; application packages remain in
the probe's isolated cache. Optional `--feed` adds a package source whose origin
must be retained by the caller. The normal source is nuget.org.

The runner checks SDK, Clang and PostgreSQL versions and SQLite source SHA-256. It
builds a separate `libsqlite3.so`, publishes both applications with ordinary
single-process SDK options, validates generated mapping, runs SQLite, then starts a
disposable PostgreSQL cluster on an available TCP loopback port with Unix sockets
disabled. The cluster is stopped after execution. Each command's arguments, working
directory, timestamps, exit code, raw log and SHA-256 are retained. No package
publication or remote action occurs.

`evidence/result.json` binds executable, package, source and native-library bytes.
Preserve `logs/`, `evidence/`, `source/` input files and package locks with the
measurement record. Do not commit the cache, executable, native library or database.

## Boundaries before performance measurement

The probe's SQLite library uses `-O3 -DNDEBUG`, `SQLITE_THREADSAFE=1` and
`SQLITE_ENABLE_COLUMN_METADATA`. The source matches Korda's pinned SQLite input, but
the complete compilation option set is not yet aligned with Korda's static engine.
Align compile options, durability, transactions, connection policy, dataset and
SQL semantics before accepting comparative performance data.

The `async_*` fields in execution output denote the called API shapes. They are not
proof of an actual asynchronous suspension; Microsoft.Data.Sqlite executes async
ADO.NET calls synchronously. Cancellation, early termination, JSON mapping, pooling,
high concurrency, saturation, allocation attribution and long runs are outside this
minimum probe.

Dapper.AOT is a micro-ORM with generated object mapping. It has no LINQ or Fluent
SQL translator. Charge explicit SQL construction to its dynamic-query cells and
separate those from reused-query cells. Do not silently fall back to vanilla Dapper
for unsupported `CommandDefinition`, multi-mapping or `QueryMultiple` overloads.

## Primary sources

- [Dapper.AOT 1.1.0](https://github.com/DapperLib/DapperAOT/releases/tag/1.1.0)
- [Classified Dapper API surface](https://github.com/DapperLib/DapperAOT/blob/1.1.0/test/Dapper.AOT.Test/ApiSurface.expected.txt)
- [Dapper.AOT setup](https://aot.dapperlib.dev/gettingstarted.html)
- [Npgsql AOT compatibility](https://www.npgsql.org/doc/compatibility.html)
- [Custom SQLite libraries](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions)
- [Microsoft.Data.Sqlite async behavior](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async)
