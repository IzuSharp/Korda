# B23 application comparators

These are isolated benchmark executables. They add no NativeWeave public API,
backend selection, product provider, or solution/Cargo workspace member. The
B23 controller owns scheduling and measurement; these programs implement real
business requests for independently checked workload cells.

## Managed NativeAOT route

`managed/ManagedComparator.csproj` uses Kestrel, Npgsql 10.0.3,
Microsoft.Data.Sqlite.Core 11.0.0, and SQLitePCLRaw.provider.sqlite3 3.0.5.
It references no NativeWeave package or assembly. Only the accepted reference
application's BCL protocol/JSON/cursor and filesystem source is linked. Its
four business tables and independent consumer table are the same embedded SQL
resources. Database commands are fixed, typed, parameterized ADO.NET commands;
Npgsql and Microsoft.Data.Sqlite execute them directly.

The explicit custom SQLite provider is important: the comparator uses SQLite
3.51.3 from the same locked `libsqlite3-sys` 0.37.0 amalgamation as the product,
not an unrecorded operating-system or NuGet bundle version. Producer setup is:

```sh
python src/Benchmarks.Domain/comparators/build_sqlite.py \
  --amalgamation /absolute/path/to/libsqlite3-sys-0.37.0/sqlite3 \
  --output /absolute/path/to/sqlite-assets
dotnet restore src/Benchmarks.Domain/comparators/managed/ManagedComparator.csproj \
  -r linux-x64 -p:PublishAot=true --locked-mode
dotnet publish src/Benchmarks.Domain/comparators/managed/ManagedComparator.csproj \
  -c Release -r linux-x64 -p:PublishAot=true --no-restore \
  -p:UseSharedCompilation=false \
  -p:ComparatorSqliteLibrary=/absolute/path/to/sqlite-assets/libsqlite3.so \
  -o /absolute/path/to/managed-aot
```

The C# project only copies the fixed native library; normal managed builds do
not invoke Rust or the SQLite C compiler. `build_sqlite.py` checks the source
hashes, uses generic x86-64, and writes the exact compiler command/artifact hash.
NuGet dependency closures for ordinary and linux-x64 NativeAOT builds are
checked in. Toolchain versions follow the repository's fixed toolchains.

The executable accepts the B21 configuration shape and prints `READY <url>`:

```sh
NativeWeave.Benchmarks.ManagedComparator --config /absolute/config.json
NativeWeave.Benchmarks.ManagedComparator --config /absolute/config.json --migrate-only
NativeWeave.Benchmarks.ManagedComparator --config /absolute/config.json --outbox-once
NativeWeave.Benchmarks.ManagedComparator --golden-vectors
```

`B23_RUNTIME dynamic=False` is an actual `RuntimeFeature.IsDynamicCodeSupported`
observation from the executable, including golden-vector invocation. The
application also verifies and prints the SQLite version and loaded file hash.
Successful shutdown prints `CLOSED` after stopped HTTP handlers, owned database
retirement tasks, and source/consumer shutdown. These receipts describe this
comparator's owners, not NativeWeave cleanup receipts.

## Direct Rust route

`direct-rust` is a standalone Rust executable with its own locked dependency
closure. It directly uses the product's pinned SQLx 0.9.0, SeaQuery 1.0.2,
Axum 0.8.9, Hyper 1.12.0, Tokio 1.54.1, rustls 0.23.46 and bundled SQLite
3.51.3. Every dependency already shared with the product uses a version from
the product lock. The additional Tokio signal support is part of this
application's ordinary Linux lifecycle, with its dependencies locked locally.
There is no NativeWeave ABI call, C# process, or managed HTTP/database helper.
The keyset route constructs its parameterized select through SeaQuery and
projects concrete row values. Transaction statements are fixed parameterized
SQL, including the separate PostgreSQL conflict winner lookup.

```sh
RUSTFLAGS='-C target-cpu=x86-64' cargo build --locked --release \
  --manifest-path src/Benchmarks.Domain/comparators/direct-rust/Cargo.toml
nativeweave-b23-direct-rust --config /absolute/config.json
nativeweave-b23-direct-rust --config /absolute/config.json --outbox-once
nativeweave-b23-direct-rust --golden-vectors
```

Release uses thin LTO, one code-generation unit, and unwinding, matching the
repository's ordinary native release settings. Its single Tokio runtime has
two worker threads. This differs from NativeWeave's separate server/source
runtimes and the managed thread pool, and is recorded as an application
architecture difference; a whole-service ratio is not an isolated FFI cost.
SQLx connections use the accepted SQLite statement/command/row-buffer settings
and PostgreSQL prepared-statement cache capacity. The independent consumer uses
the same bundled SQLite engine, its own database, and its own finite pool.

Rust v1 response structs use a serializer formatter matching the accepted
safe-ASCII JSON bytes, including uppercase UTF-16 escapes. Golden response
vectors execute this actual serializer. Application request framing, HMAC
cursors, scoped operation claims, file hash verification, fsync-before-rename
protocol, and persistent consumer effects are real operations rather than
precomputed responses. The Rust filesystem implementation retains uncertain
commit/upload protection and joins application retirement tasks before
releasing its root lock. Failure/soak acceptance is still outside the declared
ordinary comparator cells.

## Shared ordinary workload contract

The implemented routes are `POST /v1/items`, typed DTO keyset `GET /v1/items`,
principal-scoped `GET /v1/operations/{id}`, and attachment `PUT`/`GET`. Create
returns 201, attachment PUT returns 200, and confirmed recovery returns 200.
Idempotency scope, v1 request hashes, immutable response bytes, authentication,
authorization, Unicode handling, optimistic attachment versions, independent
consumer deduplication, and blob names follow the accepted B21 application.
Uploads finish durable staging before opening their binding transaction.
PostgreSQL conflicts use `INSERT ... ON CONFLICT DO NOTHING RETURNING`, followed
by a separate winner SELECT. SQLite uses `BEGIN IMMEDIATE`, WAL,
`synchronous=FULL`, foreign keys, and a five-second busy timeout.

The ordinary profile uses 16 database leases / 64 admission waiters, a separate
2-lease / 4-waiter consumer, 256 request admissions / 128 request waiters,
512 connections, 32 HTTP2 streams per connection, 1 MiB JSON, 100-row pages,
64 MiB attachments, 32 KiB application copy chunks, four active uploads, and
256 MiB filesystem admission. Workers are disabled during the common timed
core profile and explicitly drained afterward. Bearer identities, TLS
certificate validation, cursor signing key, dataset, request payloads, and
actual negotiated HTTP protocol must be supplied and independently checked by
the controller. PostgreSQL is an independently launched real server, with
durability enabled; plaintext database transport requires the explicit local
development configuration flag.

Both comparison hosts explicitly use 64 KiB HTTP2 stream and connection receive
windows, 32 KiB frames, 32 KiB aggregate headers, and 200 HTTP1 headers. These
values were read from `native/server/host/src/engine.rs` for B21's selected
32 KiB native chunk configuration; an application copy buffer alone does not
define transport frame or flow-control limits. The final managed artifact
supersedes the initial development checkpoint which used Kestrel's defaults.

## Readiness and applicability

`functional_probe.py` invokes the existing independent B21 golden/HTTP/SQL
oracle over the actual comparator process, then independently verifies all
outbox payload hashes and persistent consumer effects, duplicate delivery, and
restart. It does not call the B21 maintenance/fault oracle or turn readiness
into performance, B21 acceptance, or B22 soak evidence. It retains logs, exact
launched artifact hashes, controller hashes, and actual protocol observations.
Use `--expect-aot` for the managed published executable. An optional
`--postgres-bin` starts and stops a private PostgreSQL instance in that same
controller invocation; `--certificate`, `--private-key`, and `--http2` select
validated TLS/protocol cells. The HTTP2 client dependencies are the controller's
existing pinned environment.

The managed NativeAOT normal-route readiness was executed on Linux x64 for
SQLite and PostgreSQL 18.0 with HTTP1 plaintext, HTTP1 TLS, and HTTP2 TLS.
Each cell passed 127 independent checks, including an exact 64 MiB upload and
download. These are functional observations, not benchmark timing results.
The direct Rust executable passed the corresponding 126 checks in each of the
same six cells; the extra managed check is its actual NativeAOT runtime witness.
`comparators.json` binds the actual immutable executables, native SQLite asset,
compiled source inputs, locks, toolchains, and readiness records. Its build
receipts distinguish captured compiler-reported development duration from
unavailable timing; no unrecorded time is inferred or treated as a benchmark.

Limitations must remain visible in B23 profiles and reports:

- Kestrel/ADO.NET, direct Rust, and NativeWeave have different runtime and
  prepared-statement ownership. ADO.NET fixed SQL is an explicitly named raw
  route, not a substitute for NativeWeave typed-query measurements. Pool and
  application limits do not imply identical internal buffering.
- Both comparator migration ledgers use `__b23_migrations` with the exact B21
  application-schema checksum. NativeWeave's full migration mechanism remains
  a separate startup implementation. Cold-start reports must disclose this.
  Comparator request queue admission uses a five-second bound; exact overload,
  idle-connection policy and cancellation equivalence are not established by
  the ordinary functional rows.
- NativeWeave's process/source/request byte-credit ledger and reserved native
  cleanup capacities have no directly equivalent Kestrel/ADO.NET mechanism in
  this comparator. Overload, byte-budget saturation, cancellation/commit-loss,
  and diagnostic ownership cells are **not implemented / not run** here until
  their semantics are explicitly matched and independently checked.
- DI and diagnostics variants are **not implemented / not run**. The current
  comparator directly invokes a private application service with framework
  logging providers disabled. It cannot be labeled a DI or enabled-diagnostics
  measurement.
- `--maintenance-once`, seven-day retention sweeps, and crash/long-running
  recovery acceptance are **not implemented / not run** in the comparator.
  The ordinary measured dataset must be fresh, finish within the retention
  window, and fit the fixed storage budget. Unknown protection is retained;
  absence of a row is never treated as proof of rollback.
- Linux directory durability is the only implemented filesystem profile.
  Other platforms are not developed or accepted by this package.

Primary package/configuration references:
[Microsoft.Data.Sqlite.Core 11.0.0](https://www.nuget.org/packages/Microsoft.Data.Sqlite.Core/11.0.0),
[SQLitePCLRaw provider 3.0.5](https://www.nuget.org/packages/SQLitePCLRaw.provider.sqlite3/3.0.5),
[Npgsql 10.0.3](https://www.nuget.org/packages/Npgsql/10.0.3), and
[custom SQLite builds](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions).
