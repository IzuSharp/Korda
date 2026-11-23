# ORM relations and DB-API-01 Linux component acceptance

These consumers execute the actual source-composed Database facade and two
independent provider Host assets. The test executable requires a real PostgreSQL18
URI through `NW_TEST_POSTGRES`; it never silently skips that provider. The existing
`tests/Data.NativeHost/run.py` helper creates and stops an isolated TCP-only cluster
for the selected consumer without rerunning the B04 test suite.

Use the repository-pinned .NET 11 and C++20 toolchains and lock files. Follow
[`AGENTS.md`](../../AGENTS.md) and the selected checks from
`python3 eng/ci/impact.py origin/main`. From a clean committed repository,
the maintainer native build is:

```sh
python3 eng/task07_native.py --output artifacts/B14/native
```

This requires the pinned tools and PostgreSQL 18 build described in the
[C++ native guide](../../docs/guides/cpp-native.md). It installs each provider
library at the default source-build asset path and records a copy, its hash and
build inputs in the fresh output directory. Alternate immutable asset paths can
be supplied through `KordaSqliteAssetPath` and `KordaPostgresAssetPath` when
publishing. Ordinary NuGet entity/DTO builds use the packaged native assets.

The actual consumer commands are:

```sh
dotnet restore tests/Orm.Relations/Korda.Orm.Relations.Tests.csproj -r linux-x64 -p:PublishAot=true --locked-mode
dotnet publish tests/Orm.Relations/Korda.Orm.Relations.Tests.csproj -c Release -r linux-x64 -p:PublishAot=true --no-restore -p:IlcTreatWarningsAsErrors=true -p:UseSharedCompilation=false -nr:false -m:1 -o artifacts/B14/aot
python3 tests/Data.NativeHost/run.py --postgres-bin /absolute/postgres18/bin --output artifacts/B14/aot-run -- artifacts/B14/aot/Korda.Orm.Relations.Tests --require-aot
```

The AOT consumer verifies that runtime dynamic-code support and compilation are
both unavailable. It checks static generated/manual metadata identity,
unchanged native asset bytes, real results/errors, managed A07 retirement and all
six native owner counters. The scripts deliberately terminate only their own
PostgreSQL sessions to observe actual unknown-commit and rollback-failure paths.
No write is automatically retried.

The focused snapshot cases use preallocated oversized input values and record
current-thread rejection allocations separately from those application inputs.
They check strings, byte arrays, JSON requiring escaping, aggregate JSON, remaining
row budgets and aggregate wire records. A static converter failure after producing
oversized pending JSON must retain its original exception. Near-limit codec
encoding preserves exact bytes. Real JSON within the provider's physical input
grant round trips on both providers, while logical entity preflight failures
admit no native asynchronous operation. The SQLite test separately observes its
existing nonwaiting SQL-plus-input-copy grant rejecting before statement dispatch;
the logical parameter ceiling does not guarantee this physical grant. This is a
bounded-rejection regression, not a throughput or complete-process memory benchmark.

The C++ component tests independently validate both SQL dialects, exact mutation
inventories and copied parameter ordering:

```sh
ctest --test-dir artifacts/cmake/linux-x64-release --output-on-failure --no-tests=error
```

After a Release product build, run `api.py` with `--design` pointing to the exact
read-only pin and `--tool` pointing to the built shared
`Korda.Build.ApiReference.dll`. It compares actual compiled signatures,
generics/nullability/defaults and XML, rejects every declaration absent from the
historical pin or the explicit DB-API-01 additive contract, requires all B14
symbols, and writes owned generated references. Run
`compile_negative.py` to check six invalid typed consumers against those same
product assemblies using the actual pinned SDK compiler. No Design stub is linked.

`FluentExperience.cs` adds real-provider checks for exact DTO codec policy,
rejection before SQL and a usable projection after rejection; complete composite
cursor ordering, ties, descending and mixed directions, cloned cursors, rejected
offsets and incomplete keys. Batch relation checks observe one actual SQL
execution for nonempty input and none for empty input, duplicate positions,
empty groups, composite nullable foreign keys, result budgets and cleanup after
materializer failure. SQLite's real `NOCASE` comparison also matches differently
cased parent inputs; no CLR string regrouping decides membership. The SQL
execution counter is separate from PostgreSQL protocol submissions.

Transaction callback checks cover committed results, original exception
identity including custom read-only/throwing `Data`, confirmed rollback,
pre-admission and active-query cancellation, commit-admission cancellation and
nested-call rejection. PostgreSQL tests
terminate their own backend to observe unknown commit and unconfirmed rollback
separately. No automatic replay is part of the callback contract.

The runnable sample is `samples/api/orm-relations`; pass `postgresql` for the
explicit local PostgreSQL test configuration, or `sqlite` and an optional database
path. Its advanced path observes caller-owned savepoint bulk rollback, nullable
foreign-key unlink/relink, independent upsert updates and partial committed batches.

Freeze source commits, lock files, native profile/asset hashes, managed/AOT inputs
and logs before issuing an acceptance receipt. B14 introduces no independent
NuGet package: the normal Database/provider distribution and final pristine
consumer/release gates remain owned by their existing tasks. This suite validates
Linux x64 only.
