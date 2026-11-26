# Go consumer for the frozen DB-Perf-01 workload

This Linux x64 comparator uses Go `database/sql`, pgx, and go-sqlite3. It is a
local benchmark consumer, not a Korda provider or a new CI performance lane.
It accepts only the SHA-bound `../workload/queries-v3.json` with all 35 cases,
8 tenants, 1,024 orders per tenant, and 32 requested rows. Every operation checks
its result count and FNV-1a digest. JSON is deserialized, numeric money is read
as exact cents, related children are buffered and grouped, and early-stop closes
the full query after consuming three DTOs. Transactions check original, changed,
restored and persisted state plus affected-row optimistic conflicts.

## Build

Use the Go version and modules pinned in `go.mod` / `go.sum`. Point cgo at the
same SQLite header and shared image used by the reference campaign. The
`libsqlite3` build tag is mandatory; the default bundled amalgamation is not
the measured database image.

```bash
export CGO_ENABLED=1
export CGO_CFLAGS="-O2 -g -I${SQLITE_HEADER_DIR}"
export CGO_LDFLAGS="-O2 -g -L${SQLITE_LIBRARY_DIR} -Wl,-rpath,${SQLITE_LIBRARY_DIR}"
go mod download
go mod verify
go test -tags sqlite,libsqlite3 ./...
go build -trimpath -tags sqlite,libsqlite3 -o /absolute/task/path/db-perf-go-sqlite .
go build -trimpath -tags postgresql -o /absolute/task/path/db-perf-go-postgresql .
```

Set task-local `GOCACHE`, `GOMODCACHE`, `GOPATH` and `GOTMPDIR` if the host will be
cleaned afterwards. The runtime verifies that the selected `--native-library`
is the sole loaded SQLite shared image and records its SHA-256. The historical
image is SQLite 3.51.3 with SHA-256
`2f693e43707c8174c862283185610abca5c671bbf17b6b1af7866bfbcd290282`.

The provider build tags produce independent executables. Each contains only
its selected database driver; PostgreSQL does not link the SQLite shared image.
The `--provider` option must match the executable's compiled provider.

## Reproduce the additional Go cells

`make_plan.py` takes the immutable previous SQLite or PostgreSQL final plan,
the frozen `../run.py`, manifest, new executable, a fresh result directory and
`--kind preflight` or `--kind final`. Supply `--fixture-executable` and
`--postgres-bin` when relocating the original artifacts. The fixture command
retains `--initialize --operations 0`; it creates the exact data without adding
another measured Korda cell. The original Korda/Dapper executables are not
selected for timed comparison.

The generated `runnerCommand` runs the existing resource and PostgreSQL
ownership runner on CPU 8. The client uses CPUs 0–3 and GOMAXPROCS=4, with
GOGC=100 and no memory limit. PostgreSQL runs on CPUs 4–7. Final cells are serial:
SQLite at 3,000 offered requests/second with four persistent workers, then
PostgreSQL at 3,600/second with eight workers. Both run for 1,800 offered seconds,
queue capacity 64, verification of all 35 cases plus 64 mixed warmup operations
per worker, and 15 seconds idle after closing all database resources. Preflight
uses the same workload for five seconds and receives no long-run credit.

## Mapping and driver policy

Go uses typed DTOs and typed `Rows.Scan` targets. A DTO can remain on the Go stack
when escape analysis permits; no artificial allocation is introduced to imitate
C# object placement. Related groups retain owned child DTOs through grouping,
sorting and hashing. Ordinary results stream one row at a time. `database/sql`
has no row-batch control, so the reference batch size 32 is recorded as reference
metadata rather than a driver guarantee.

SQLite uses retained prepared read statements, WAL, synchronous FULL, foreign
keys, a 5,000 ms busy timeout and BEGIN IMMEDIATE transactions. pgx explicitly
uses `QueryExecModeExec`, without retained server prepared statements or a
statement cache, to match the reference Npgsql `MaxAutoPrepare=0` policy. This
mode uses text-encoded parameters; it is an actual driver difference and is
not described as identical wire encoding. Transaction command construction and
all reader/transaction retirement remain inside operation latency.

## Measurement boundaries

The producer and bounded FIFO preserve global arrival indices, including those
that are rejected. No retries occur. Every admitted operation drains before the
steady interval ends. Successful service and scheduled-response latencies use
the original base-2 histogram with 128 subdivisions per exponent and report bin
upper bounds. Rejections have no invented latency. Raw histograms, per-second
and per-case counts, error classes, rows, digests and business SQL/control call
counts are saved. SQL calls are not protocol-message or network-round-trip
counts.

Process CPU includes all Go threads, the producer, cgo and GC; PostgreSQL server
CPU is separate. `runtime.MemStats.TotalAlloc` and `NumGC` are sampled for the
whole process before and after admitted work. These are Go heap allocations and
non-generational collections, not .NET generation counts. DTOs, strings, JSON,
driver and harness allocations are included. Business-only/framework-only bytes
and native allocation counts are unavailable, not zero. `runtime.NumCgoCall`
records Go-to-C calls separately from unobservable driver protocol replies.

Worker/connection/statement/pool close and the unforced 15-second idle period
follow the steady interval. The unchanged Python observer records lifetime
wait4 maxRSS and CPU separately from steady client counters. Source, module,
binary, SQL/manifest and database identities must be archived with every run.

The Go consumer is intentionally isolated from CI execution. Changes remain
under the already registered `db-perf-benchmarks` test-path ownership module;
no workflow, performance job, trigger or package fingerprint is added.
