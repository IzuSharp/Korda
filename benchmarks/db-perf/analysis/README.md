# Independent database performance evidence verifier

This offline tool recomputes the active 35-case campaign's evidence from fixed Git objects and original archives. It checks actual bytes and counters before calculating ratios. It does not start workloads, write databases, change original evidence, fetch Git objects or access remote services.

Measurement results, execution receipts and the current acceptance state belong in the [DB-Perf-01 Plan handoff](https://github.com/IzuSharp/Plan/blob/task/DB-Perf-01-linux/ledger/evidence/batches/DB-Perf-01/handoff-linux.md). This directory contains the verifier and its regression checks.

## Inputs and output

Supply the original runtime ZIP, a cumulative observation ZIP, their SHA-256 values, the expected workflow run and attempt, and local Git repositories containing the frozen harness and package-producer commits. The package repository can be the same checkout as the harness repository. A separate read-only checkout is supported when it supplies a required Git object missing from the first checkout.

The output retains raw totals, evidence identities, failed SLO observations and remaining acceptance gaps. A successful partial verification does not mean the entire performance task passed. `--require-eight-long` additionally rejects an incomplete long-run matrix while retaining the valid observations in its output.

Archives are read without extraction. Runtime payload bytes, lengths, modes and symlink targets are checked against their declared inventory. Before/after file hashes and stable stat identity detect changes during verification. The chosen output JSON is the verifier's only explicit output; normal Python imports can also create bytecode caches.

## Verification boundaries

| Area | Checked evidence and limits |
| --- | --- |
| Source and binary identity | Fixed public Git objects, package-producer trees, four candidate nupkgs, nested ZIP hashes, unsigned payload inventories, nuspec metadata, external consumer inputs, Linux x64 application ELF bytes and loaded native hashes |
| Workload | Source-defined client/server argv, task CPU partitions, batching, queue budget, warmup, primary/heavy dataset cardinalities and actual initializer receipt |
| Correctness | Sticky integrity-oracle state, full per-case coverage, consumed rows and request conservation; a low ordinary error rate cannot hide a correctness mismatch |
| Latency and throughput | Histogram population and quantile upper bounds recomputed from raw bins; planned, attempted, sent, completed, failed, rejected and retried counts remain distinct |
| Managed allocation | Cumulative process allocation endpoints are subtracted and checked before division by operations or rows; mixed business/framework/harness totals are not relabeled as framework-only allocation |
| GC | Source-bound generation deltas are retained; absent cumulative endpoints are not invented, and no-GC execution does not prove zero allocation |
| Client CPU | Actual cumulative user/system seconds are primary; source-ordered CPU, allocation, steady-window and drain boundaries remain distinct |
| PostgreSQL CPU | Task-owned cgroup ancestry and cumulative counters include exited children; only complete sampling intervals inside the phase are used, without extrapolating unsampled edges |
| Memory | RSS/PSS completeness, sampled peak and process-lifetime high-water are separate; sampled peaks are not exact whole-address-space maxima |
| Recovery | Source, connection and worker identities remain bound across drained phases; a restarted scheduler is not described as carrying an old queue continuously across the transition |
| Native allocation | Every snapshot and interval must conserve requested/usable bytes, with monotonic event counters and explicit coverage failures; the bounded profiler table and tracked allocator peaks do not prove the complete native-heap peak |
| Admission | Recorded idle/noise counters, all capacity points, bounded refinement decisions and the common offered-rate derivation are independently checked; best tested throughput is not proof of a global maximum |
| Long matrix | Eight genuine 1800-second cells require one admitted VM, common workload/concurrency, a rate shared by the four routes within each provider, no overlap, frozen chronological order and complete selected-case coverage |

PostgreSQL startup settings must match the source-defined set exactly. Additional planner tuning is rejected; legitimate query-plan logging settings and the loaded `auto_explain` library are bound separately. Korda recovery owner/activity counts must be observable integers. Dapper's explicitly unavailable internal counters remain `null` rather than becoming invented zeroes.

The tool keeps native warm-idle and post-drain closed-idle boundaries separate. Some producer formats do not record a closed-idle duration; that value remains unknown. Failed operations can stop after a partial SQL sequence, so their exact SQL attribution is not inferred from successful-operation counts. SQL executions, protocol submissions and physical network round trips are not treated as interchangeable.

Complete query-plan semantic review, compiler/static-native and third-party package closure, full allocation-path attribution, optimization stopping review, merge/preview and downloaded public NuGet Native AOT consumption remain separate task gates. The output always records its partial task scope.

## Reproduction

Set these task-specific variables to local input paths, original archive hashes, the expected workflow run/attempt, and a chosen output JSON path. Git objects must already exist locally; this tool does not fetch them.

```bash
python3 benchmarks/db-perf/analysis/verify_current.py \
  --repository "$DB_PERF_KORDA_CHECKOUT" \
  --package-repository "$DB_PERF_PACKAGE_CHECKOUT" \
  --runtime-zip "$DB_PERF_RUNTIME_ZIP" \
  --runtime-sha256 "$DB_PERF_RUNTIME_SHA256" \
  --observations-zip "$DB_PERF_CHECKPOINT_ZIP" \
  --observations-sha256 "$DB_PERF_CHECKPOINT_SHA256" \
  --expected-run-id "$DB_PERF_RUN_ID" \
  --expected-run-attempt "$DB_PERF_RUN_ATTEMPT" \
  --output "$DB_PERF_OUTPUT_JSON"
```

For runtime-only validation, omit the observation and run arguments. Add `--require-eight-long` when checking the complete long matrix. Use `test_current.py` with the same archive inputs, omitting `--expected-run-attempt`, to execute archive-backed negative controls. Its `--review-delta-only` option selects the three focused regressions for unexpected PostgreSQL settings, a constant offset in native allocator snapshots and missing Korda recovery-owner visibility. Synthetic mutations never count as performance measurements.

## Ordinary CI

`benchmarks/db-perf/check.py` invokes only `analysis/test_units.py` for this verifier, with `check=True` and a 30-second timeout. The nine finite offline checks cover strict JSON/counter types, a golden histogram, sticky warmup failure, single-member gzip framing, native allocation conservation and coverage, and bounded rate-search decisions. They neither download original archives nor start a database, benchmark campaign or long run.

## Modules

| File | Responsibility |
| --- | --- |
| `strict_io.py` | Duplicate-key/nonfinite JSON rejection, exact integer counters and hash-bound lossless data |
| `archive_inputs.py` | Read-only ZIP/runtime traversal, archive safety, actual payload inventories and x64 ELF checks |
| `identity_binding.py` | Source/package/consumer/provider/route binding, actual commands and workload settings |
| `raw_metrics.py` | Request conservation, per-case rows, histograms, oracle/error state, allocation, CPU and owner counters |
| `resource_metrics.py` | Raw PID memory, complete sample intervals, cgroup ancestry/lifetime CPU and retirement |
| `diagnostic_metrics.py` | Retained-owner recovery phases and separate bounded allocator profiles |
| `campaign_admission.py` | Idle/noise, capacity/refinement selections, offered-rate derivation and companion dependencies |
| `verify_current.py` | CLI, archive/seed binding, observation traversal and separate eight-cell long matrix |
| `test_current.py` | Actual archive positives and explicitly synthetic negative controls |
| `test_units.py` | Finite synthetic checks for ordinary PR CI |
