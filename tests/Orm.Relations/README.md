# B14 Linux component acceptance

These consumers execute the actual source-composed Database facade and two
independent provider Host assets. The test executable requires a real PostgreSQL18
URI through `NW_TEST_POSTGRES`; it never silently skips that provider. The existing
`tests/Data.NativeHost/run.py` helper creates and stops an isolated TCP-only cluster
for the selected consumer without rerunning the B04 test suite.

Use the repository-pinned .NET11/Rust toolchains and lock files. From the repository
root, maintainer native builds are:

```sh
cargo build -p nativeweave-database-host --no-default-features --features sqlite --locked --offline --target-dir artifacts/B14/native-sqlite
cargo build -p nativeweave-database-host --no-default-features --features postgres --locked --offline --target-dir artifacts/B14/native-postgres
```

Copy the resulting libraries to the task's immutable asset directory under the
provider names `libnw_database_sqlite.so` and `libnw_database_postgresql.so`. Supply
their absolute paths through the existing `NativeWeaveSqliteAssetPath` and
`NativeWeavePostgresAssetPath` MSBuild properties for each managed build/publish.
Those are maintainer asset inputs. Ordinary entity/DTO source compilation does
not run Rust.

The actual consumer commands are:

```sh
dotnet restore tests/Orm.Relations/NativeWeave.Orm.Relations.Tests.csproj --locked-mode
dotnet build tests/Orm.Relations/NativeWeave.Orm.Relations.Tests.csproj -c Release --no-restore -p:UseSharedCompilation=false -nr:false -m:1 -p:NativeWeaveSqliteAssetPath=/absolute/libnw_database_sqlite.so -p:NativeWeavePostgresAssetPath=/absolute/libnw_database_postgresql.so
python3 tests/Data.NativeHost/run.py --postgres-bin /absolute/postgres18/bin --output artifacts/B14/jit -- dotnet tests/Orm.Relations/bin/Release/net11.0/NativeWeave.Orm.Relations.Tests.dll

dotnet restore tests/Orm.Relations/NativeWeave.Orm.Relations.Tests.csproj -r linux-x64 -p:PublishAot=true --locked-mode
dotnet publish tests/Orm.Relations/NativeWeave.Orm.Relations.Tests.csproj -c Release -r linux-x64 -p:PublishAot=true --no-restore -p:UseSharedCompilation=false -nr:false -m:1 -p:NativeWeaveSqliteAssetPath=/absolute/libnw_database_sqlite.so -p:NativeWeavePostgresAssetPath=/absolute/libnw_database_postgresql.so -o artifacts/B14/aot
python3 tests/Data.NativeHost/run.py --postgres-bin /absolute/postgres18/bin --output artifacts/B14/aot-run -- artifacts/B14/aot/NativeWeave.Orm.Relations.Tests --require-aot
```

The AOT consumer verifies that runtime dynamic-code support and compilation are
both unavailable. Both modes check static generated/manual metadata identity,
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

The leaf Rust tests independently validate both SQL dialects, exact mutation
inventories and copied parameter ordering:

```sh
cargo test -p nativeweave-data-relations --locked --offline
cargo clippy -p nativeweave-data-relations -p nativeweave-database-host --all-targets --locked --offline -- -D warnings
```

After a Release product build, run `api.py` with `--design` pointing to the exact
read-only pin and `--tool` pointing to the built shared
`NativeWeave.Build.ApiReference.dll`. It compares actual compiled signatures,
generics/nullability/defaults and XML, rejects every declaration absent from the
pin, requires all B14 symbols, and writes owned generated references. Run
`compile_negative.py` to check six invalid typed consumers against those same
product assemblies using the actual pinned SDK compiler. No Design stub is linked.

The runnable sample is `samples/api/orm-relations`; pass `postgresql` for the
explicit local PostgreSQL test configuration, or `sqlite` and an optional database
path. Its advanced path observes caller-owned savepoint bulk rollback, nullable
foreign-key unlink/relink, independent upsert updates and partial committed batches.

Freeze source commits, lock files, native profile/asset hashes, managed/AOT inputs
and logs before issuing an acceptance receipt. B14 introduces no independent
NuGet package: the normal Database/provider distribution and final pristine
consumer/release gates remain owned by their existing tasks. This suite validates
Linux x64 only.
