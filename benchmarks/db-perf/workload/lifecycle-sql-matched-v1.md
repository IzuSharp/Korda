# Matched lifecycle business SQL

`lifecycle-sql-matched-v1.json` adds an explicit contract for **new** external
Dapper.AOT workload consumers. `queries-v3.json`, its observed independent ORM
SQL, existing consumer directories, and historical receipts remain unchanged.
The supplement pins the exact v3 bytes. It covers `tx-commit`, `tx-rollback`,
`partial-update`, and `stale-conflict`; existing read contracts and `early-stop`
are unchanged.

## SQL, parameters, and consumption

The five lifecycle SQL constants now retain Korda's complete identifiers,
qualification, aliases, whitespace, predicates, and `LIMIT`. Find binds
`tenant`, `id`, and `take = 2` as `Int64`, then reads all nine `Order` fields.
Updates retain the same ordered business values and `Int64`/`String` types,
including nullable notes and the expected version predicate. Transaction
sequencing, affected-row checks, full DTO equality and digests, disposal before
the final read, and `[CacheCommand(false)]` remain in place.

| Provider | Permitted SQL difference | Evidence and limit |
| --- | --- | --- |
| PostgreSQL | Dapper's `@name` spelling before driver rewriting | The pinned [Npgsql parser](https://github.com/npgsql/npgsql/blob/4d174200ed48e3e3ab16419dbf84dd655da59de6/src/Npgsql/SqlQueryParser.cs) assigns positional parameters in SQL occurrence order. Replacing those names with `$1`, `$2`, … must produce the exact Korda business SQL. The [type map](https://github.com/npgsql/npgsql/blob/4d174200ed48e3e3ab16419dbf84dd655da59de6/src/Npgsql/NpgsqlTypes/NpgsqlDbType.cs) maps `Int64` to `int8` and `String` to text. |
| SQLite | Korda's ordinal `?` becomes a unique `@name` | [Microsoft.Data.Sqlite parameter binding](https://github.com/dotnet/efcore/blob/9e685c7a52d1a344d133a3d370e458db97417b3f/src/Microsoft.Data.Sqlite.Core/SqliteParameter.cs) uses named parameter lookup. The checker permits only this substitution; raw SQLite SQL bytes are explicitly different. |

Provider transaction APIs remain their normal implementations. Their control
SQL and send timing can differ: this contract does not add Korda's internal
PostgreSQL health probe or Sync, or SQLite `query_only` control, to Dapper. The
comparison must expose those framework costs. Source and generated-code proof
are distinct from a runtime wire trace.

## Static validation and new consumers

From the repository root, static validation does not build or execute a consumer:

```sh
python3 benchmarks/db-perf/workload/check_dapper_contract.py \
  --lifecycle-sql-mode matched --receipt /absolute/new/static-sql-contract.json
```

To check a retained historical consumer, pass its copied source and manifest
with `--lifecycle-sql-mode observed`. That mode still requires the original v3
`independentOrmSql`; it does not silently accept the new SQL.

When a new build is authorized, use a fresh output outside the source tree:

```sh
python3 benchmarks/db-perf/managed/build_orm.py \
  --provider postgresql --kind benchmark --lifecycle-sql-mode matched \
  --output /absolute/new/dapper-postgresql-workload
```

Use `--provider sqlite` for the independent SQLite consumer. New benchmark
builds default to `matched`; probes and query-phase consumers retain their
existing behavior. A current matched source does not satisfy `observed` mode.

The builder validates the actual copied sources and saves `sql-contract.json`,
the unchanged `workload-manifest.json`, and `lifecycle-sql-policy.json` before
publishing. The static receipt hashes the source, validator, manifest, and
policy and explicitly reports `generatedFactoriesVerified = false` and
`runtimeSqlTraceVerified = false`.

After a successful Native AOT publish, the builder inspects the **actual**
emitted Dapper.AOT source. It resolves source interception locations to each
lifecycle call, requires all nine timed call sites and three verification Find
sites, checks the generated parameter names, `DbType`, input directions and
value members, and verifies Find's complete generated `Order` constructor and
column matching. `generated-sql-contract.json` records the emitted source hash
and resolved factories. A missing or mismatched generated contract fails the
build and preserves `postValidationError` in `build-receipt.json`.

Neither receipt grants runtime, timing, allocation, or long-run credit. New
consumer verification must still prove business outcomes. Historical runs keep
their original SQL mismatch disclosure and cannot be relabeled with this mode.
