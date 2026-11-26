# Local cursor ABI diagnostic

`build.py --consumer <external-consumer> --source <matching-Korda-source> --output <fresh-folder>`
builds a separate Native AOT consumer and a Linux/glibc `LD_PRELOAD` probe. The
ordinary build receipt selects `kind=query-phases` or `kind=workload`; both must
be successful external Korda PackageReference consumers. The output directory
must not exist. The builder compiles the diagnostic but never runs a workload.

Use the returned `runEnvironment` only when starting this diagnostic executable.
The preload must be present **before** the Host resolves its ABI exports. The
probe returns forwarding wrapper addresses from actual `dlsym` resolutions, so
the C# function-pointer table captures those addresses. It does not redirect
function pointers which a running consumer has already resolved. Missing
interception is rejected when the first counter snapshot observes no source
creation. Callback records, arguments, result ownership and return values pass
through unchanged.

## Phase consumer

Use the ordinary positional 1/100/1000-row arguments against the same dedicated
database. Its existing per-window counter snapshots and phase receipt remain in
place, with an additive `OkReturns` array alongside `Calls` and `PendingReturns`.

## Workload consumer

Use the ordinary workload options, for example:

```sh
LD_PRELOAD=<fresh-folder>/out/libcursor_probe.so \
  <fresh-folder>/out/DbPerf.Korda \
  --connection '<dedicated-local-connection>' --case tx-rollback \
  --mode prepared --session persistent --concurrency 1 --warmup 64 \
  --seconds 15 --rows 32 --batch-rows 32 --tenants 8 --orders-per-tenant 1024 \
  --output <new-receipt.json>
```

Run `numeric`, `tx-rollback` and `tx-commit` in separate processes. `--cases`
overrides `--case`; omit it when selecting one case. Initialize the fixture in
a separate ordinary consumer process. Initialization, lifecycle/recovery checks,
empty-driver runs and allocation profiling are rejected by the workload overlay.

The overlay attaches `Ffi` to the existing `Before`, `After` and `Drained` native
snapshots. Subtract `Before` from `After` to exclude validation, worker creation
and warmup. `Drained` includes final source/session disposal and is a separate
observation. Each workload receipt has `TimingComparisonEligible=false`,
`Instrumented=true`, `TimingCredit=false` and `AllocationCredit=false` regardless
of workload options. Ordinary consumer files and the package/provider bytes are
checked against their build receipt and retained unchanged.

The probe intercepts actual `dlsym` resolutions of the bound database C ABI and
forwards every call with the original exact signature, callback record and
owner. It counts managed-to-native calls and exact `NW_PENDING` / `NW_OK` returns
per symbol. Array indices follow `symbols` in `diagnostic-build.json`; status
arrays are zero for void-returning functions. Inspect at least:

- `nw_db_operation_start_v1`, `nw_db_operation_try_retire_v1` and
  `nw_db_operation_release_v1` for generic operation use and retirement.
- `nw_db_tx_begin_create_v1`, `nw_db_mutation_create_v1`,
  `nw_db_tx_finish_create_v1` and `nw_db_close_create_v1` for their factories.
- `nw_db_cursor_pull_close_request_v1` and `nw_db_cursor_pull_close_take_v1`
  for direct cursor close, separately from generic transaction/source close.

`TryRetire` returning `NW_OK` proves its generic quiet contract; a ready callback
alone does not. A non-Pending return is not necessarily OK. A Pending count is a
count of attempts, not a count of distinct operations or elapsed timer waits.
No per-operation timing spans are added. With persistent sessions, ordinary
numeric row queries should not start generic operations in the steady window;
transaction cases still have begin, non-returning mutations and finish, plus a
generic close when their transaction cleanup snapshot is not already released.

The diagnostic count exports are outside the wrapped ABI inventory and do not
add to these call totals. Native-to-managed notifications are not intercepted;
the public Host contract tests count their accepted/ready/taken generations.

For PostgreSQL, the probe additionally counts actual provider-originated `poll`
calls, timeout/error returns and their elapsed time. These are socket waits,
not physical round trips or counts of protocol messages: buffered messages may
need no `poll`, while one required response can need several. Pair them with the
ordinary receipt's SQL execution/send counters and the source-verified
Parse/Describe/Sync and Execute/ReadyForQuery boundaries.

**No instrumented timing or allocation receives acceptance credit.** Generated
shim C, copied ordinary build receipt, original/overlay managed source hashes,
ABI inventories, compiler, package, actual provider, executable and logs are
retained. The build receipt also explicitly marks the output instrumented and
ineligible for timing/allocation credit. A separate ordinary consumer supplies
performance results. This tool has no CI registration and must only run on the
local Linux measurement machine.
