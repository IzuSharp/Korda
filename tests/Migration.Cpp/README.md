# DB-CPP-01 package and historical database comparison

`Consumer/` is one versioned business application. It has ordinary references to
`NativeWeave.Database.Sqlite` and `NativeWeave.Database.PostgreSql`, static entity
and DTO materializers, and no project reference or product build import. The same
C# files run against the fixed Rust packages and the candidate C++ packages in
separate processes. The test adds no public database feature.

`run.py` performs two phases in an output directory outside the product tree.
Every phase refuses to overwrite an existing attempt. A failed attempt remains
available for diagnosis; use a new output directory when fixing the fixture.

## Inputs and commands

Use the pinned .NET SDK from the repository's `global.json` and one PostgreSQL 18
server installation for both phases. The driver creates and stops its own local,
trust-authenticated disposable PostgreSQL cluster. No existing database is used.
SQLite comes from each package's provider asset; Python's independent SQLite
reader only observes the persisted file.

The fixed Rust packages must be built from product commit
`465cd0f90e6af05ec50eb581156240377cf0825b`. The retained shared packages are version
`0.1.0-db-scope.1.1`; the database and provider packages are
`0.1.0-db-scope.1.2`. The driver resolves this exact mixed-version closure from
the package metadata, verifies every resolved archive hash, and records each
package's repository commit. It does not build or invoke Rust.

`reference_prepare.py` retrieves the immutable artifact named in
`reference.lock.json`: workflow run `44924918463`, artifact `14715983822`, ZIP
SHA-256 `7a7982ccda96d3a5d7f3f4b00713a8668ae5548ec189419e3c22af5493248a11`.
CI supplies `GITHUB_TOKEN` with `actions: read`. The helper sends that token only
to the GitHub API, drops it before following the storage redirect, and never
writes it into command arguments or evidence. A cached ZIP is still hash-checked
on every invocation; extracted package bytes, source commits, package closure
and both native assets are also checked. A local `--archive` can provide the
same immutable ZIP without any network request.

```sh
python3 tests/Migration.Cpp/reference_prepare.py \
  --cache /absolute/path/reference-cache \
  --output /absolute/path/rust-reference
# Optional offline input: --archive /absolute/path/rust-465cd0f-candidates.zip
```

```sh
# Run from a committed NativeWeave checkout with the pinned .NET/Clang tools on PATH.
python3 tests/Migration.Cpp/run.py \
  --phase baseline \
  --feed /absolute/path/baseline/artifacts/rust-packages/candidate/feed \
  --version 0.1.0-db-scope.1.2 \
  --postgres-bin /absolute/path/postgresql-18.0/bin \
  --sdk-cache /absolute/path/verified-nuget-cache \
  --output /absolute/path/outside-tree/migration-comparison

python3 tests/Migration.Cpp/run.py \
  --phase candidate \
  --feed /absolute/path/cpp-candidate/feed \
  --version <candidate-provider-version> \
  --postgres-bin /absolute/path/postgresql-18.0/bin \
  --sdk-cache /absolute/path/verified-nuget-cache \
  --output /absolute/path/outside-tree/migration-comparison
```

`--sdk-cache` supplies existing SDK/runtime/ILCompiler NuGet archives, which are
copied or linked into an isolated local feed and hash-recorded. Each phase uses
its own NuGet package cache and CLR home, restores twice including locked mode,
and cannot resolve a NativeWeave package outside the selected provider feed.
The Native AOT consumer is published as a real ELF executable and must execute
with dynamic code disabled. `cargo`, `rustc`, `rustup`, `cmake`, and `ninja` are
trap executables during consumer builds and runs; invoking any fails the matrix.
Clang remains available for ordinary .NET Native AOT linking.

## Matrix and assertions

Both phases require all four SQLite/PostgreSQL × JIT/Native AOT rows. Missing
providers, cases, packages, snapshots, DTO fields, or result receipts fail. The
driver also rejects changed consumer sources, driver bytes, required cases,
.NET SDK pin, or PostgreSQL server version between phases.

| Stage | Rust baseline | C++ candidate | Assertion |
| --- | --- | --- | --- |
| Fresh seed | Migrate and seed new database | Migrate and seed equivalent new database | Every semantic result equals the Rust result; independent SQL matches explicit expected data |
| Historical read | Read old seed and validate/reapply migrations | Open an exact copy of the old SQLite file or restore the old PostgreSQL dump | Same entity/DTO/query results; identical old SHA-384 ledger; reapply changes nothing |
| Continue | Continue writing an isolated old seed copy | Continue writing the inherited old database | Same CAS/bulk/transaction results and durable final rows |
| Changed DTO | Original three-field DTO | Rebuild only ordinary C# with the four-field DTO variant | New field materializes correctly; all original results and loaded native hashes remain unchanged |

`required-cases.json` fixes 25 common result groups, one seed group, and five
continuation groups. These cover:

- Entity and DTO columns, NULL versus empty text, Unicode and quoting, a key
  above JavaScript's exact integer range, exact configured decimal, UTC
  microseconds, and binary bytes including zero and values above 127.
- Parameterized named SQL with deliberately reordered parameters and a repeated
  parameter, interpolation, a prepared plan with snapshotted invocation values,
  offset and keyset paging, explicit relation loads, nullable left join,
  grouping, window snapshot, CTE snapshot, and set operations.
- Optimistic update and stale conflict, confirmed commit, rollback to a tracked
  savepoint, whole transaction rollback, all-batches and per-batch bulk writes,
  and delete with exact affected-row counts.
- Early exit from a batch-limited stream followed by session reuse, cancellation
  before admission, zero timeout, PostgreSQL row locks and the corresponding
  SQLite capability rejection, catalog reads, migration history and idempotence.

The schema deliberately follows existing codec profiles: SQLite stores
`UtcInstant` as a Unix-microsecond `INTEGER` and exact `DecimalText` as `TEXT`;
PostgreSQL uses `TIMESTAMPTZ` and `NUMERIC(18,4)`. The fixture does not infer one
provider's physical storage from the other.

Independent Python SQLite queries and `psql` verify every persisted business
field, relation rows, all three migration checksums and success flags, and the
absence of rolled-back IDs 90/99. SQLite additionally runs `integrity_check`.
These checks use explicit expected values rather than accepting agreement
between the old and new engines as sufficient proof.

`abi_probe.py` additionally exercises 24 public C ABI rejection cases against the
actual product SQLite library in a separate process. Its expected status,
database domain, domain code, and cleared output values were captured by calling
the fixed old Rust asset. Both comparison phases execute the same probe, and the
ordinary Release CTest `native.abi_status` checks future C++ changes without
requiring Rust. It distinguishes binding errors (code 11) from malformed borrowed
views (code 0), Host ownership errors (code 8), and Host capacity rejection
(code 6), including ambiguous invalid-input precedence. It also checks malformed
plan argument batches and empty/oversized projection sequences. The test releases
all owners and requires all six native counters to return to zero. The Python
loader test is excluded from the sanitizer preset because Python is not linked
with that runtime; instrumented native query tests cover the same parsing code.

## Receipts and limits

`baseline/result.json` freezes source, package, SDK archive and native hashes,
build logs, actual runtime receipts, independent SQL evidence, and old SQLite
files/PostgreSQL dumps. `candidate/result.json` contains the equivalent candidate
evidence and all comparisons. `comparison.json` is created only after every
required row passes. Each process verifies the provider module actually mapped
in `/proc/self/maps`, its packaged SHA-256, unchanged native bytes, and all six
native owner counters returning to zero after deterministic disposal.

DTO v2 adds a real selected database column and a CLR record property. It is
stored as `DtoMapping.v2.cs.txt` and replaces only `DtoMapping.cs` in an isolated
consumer copy. The driver checks the four exact new field values and unchanged
provider bytes; it never rebuilds the C++ library for this change.

This finite business comparison supplements the original full regression suites
and the ported CTest gates. It does not replace the fault-injection, concurrent
disposal, provider disconnect, commit-outcome-unknown, memory-safety, or large
bounded-stream tests, and it makes no performance claim.
