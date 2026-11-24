# DB-Perf-01 preflight probes

These three probes establish concrete prerequisites for the database performance
campaign. They are not the complete business workload or long-run acceptance.

| Probe | Actual scope |
| --- | --- |
| [allocations](allocations/README.md) | Original standalone Korda packages, external Native AOT consumers, complete scalar Fluent queries, batch-local synchronous rows, supplemental fetch and raw process-wide allocation controls |
| [independent-orm](independent-orm/README.md) | Dapper.AOT real-query and transaction capability through independent PostgreSQL/SQLite providers |
| [native](native/README.md) | Direct pinned Clang/libpq/sqlite3 query, parameter, result-consumption and transaction capability |

The DB-Perf-01 plan, immutable baseline, measured receipts and recovery entry belong
in [Plan](https://github.com/IzuSharp/Plan/tree/task/DB-Perf-01-linux/ledger/evidence/batches/DB-Perf-01).
Every receipt binds its actual source, package, executable and library bytes.
Failed attempts are retained. A probe result must not be relabeled as a business
case, a throughput comparison, a full framework allocation result or a thirty-minute run.

Run each probe explicitly with the tool/source/package inputs documented in its
own README. All build and database output directories are disposable task-specific
locations; consumers are built outside the product checkout. Existing source and
test state in another task worktree is not changed.

The `database-perf-probes` CI module checks entry-point imports/arguments and input
file syntax only. It deliberately reports `product_execution_claimed: false`.
The actual AOT/database executions are separately retained in Plan, and production
changes still require their affected correctness, API, package and allocation gates.
This module neither starts a long campaign nor establishes performance acceptance.
