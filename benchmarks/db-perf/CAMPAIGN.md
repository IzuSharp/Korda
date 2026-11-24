# DB-Perf-01 Linux campaign controller

This is the batch's sole active campaign after integrating the parallel
`benchmarks/Database/campaign` implementation. That implementation, its exact old
workflow and failed observations remain available for historical reproduction;
its 36-descriptor results cannot be relabeled as this 35-case contract. The
historical workflow lives outside `.github/workflows`, so a PR title edit cannot
launch a competing long run. Both implementations now await transaction
retirement before issuing post-commit fixture DDL.

This directory contains an executable campaign path, not a completed performance
claim. The checked-in request explicitly enables this task campaign. The batch ledger and accepted evidence
live in `IzuSharp/Plan`, `ledger/evidence/batches/DB-Perf-01/handoff-linux.md`.
Only observations actually recorded on the admitted VM can receive long-segment
credit. Eight observed segments do not establish whole-framework zero allocation,
complete native-memory instrumentation, package publication, or batch completion.

## Inputs and preparation

`campaign-request.json` freezes the original main commit, final package-producing
commit, successful normal CI runs, provider artifact IDs, ZIP/package/native
SHA-256 values, consumer commit, workload bytes and case sequence. The original
main is `771bc77cbd68d3f932b99ed2497c0697bbba5f02`. PostgreSQL and SQLite remain
independent packages. An artifact is accepted only after its original unsigned
package entries, producer manifest, source commit and Git tree agree.

The harness commit is frozen before the opt-in request is enabled. A later request
commit may change only the request within the benchmark source directory; all
compiled benchmark inputs must remain tracked and identical to the frozen
commit. The preparation job downloads the four original candidate artifacts,
publishes identical ordinary external C# applications against before/after
`PackageReference` inputs, publishes the independent ORM applications, and builds
the direct C++ comparator with the locked compiler and drivers. Source files,
restore graphs, generated Dapper mapping sources when available, build logs,
package manifests and runtime hashes accompany the relocatable bundle.

The original and optimized applications use the same consumer source and project
bytes. Their package versions are command-line properties, rather than edits to
the consumer project. A package's native source commit may be older than its C#
source commit when the normal build legitimately reuses an unchanged native input;
the exact native payload hash remains required.

Commands below validate or prepare inputs. A disabled request cannot start a
preparation or a measurement:

```bash
python3 benchmarks/db-perf/campaign.py validate-request benchmarks/db-perf/campaign-request.json --allow-disabled
python3 benchmarks/db-perf/test_controller.py
python3 benchmarks/db-perf/test_plan_diagnostics.py
python3 benchmarks/db-perf/campaign.py prepare \
  --request benchmarks/db-perf/campaign-request.json \
  --repo "$GITHUB_WORKSPACE" --output "$RUNNER_TEMP/db-perf-prepared"
```

Preparation requires read-only GitHub Actions artifact access through `gh` and the
repository's pinned toolchain/PostgreSQL installation. It runs in a separate job,
outside all timing boundaries. No credential is written to the bundle.

## Dedicated measurement environment

`.github/workflows/db-perf-campaign.yml` has a manual dispatch trigger and a
task-branch push trigger restricted to `campaign-request.json`; ordinary
pull-request CI remains short. The
measurement job has a 360-minute timeout. All eight sequential long steps run in
that single job and therefore on the same hosted VM. Every step uploads a distinct
checkpoint artifact, so a later failure cannot delete an earlier checkpoint.

The preflight step allows 85 minutes and the controller's monotonic deadline is
340 minutes; the job retains another 20 minutes for final artifact handling.
`timeBudgetEstimate` in the request is checked against the actual frozen layer
settings. Each ordinary cell uses `measurement seconds + idle windows × 5 seconds`:
native currently has two idle windows, managed
routes one; same-process recovery has one final idle window for every route.
The maximum configured schedule assumes one attempt per step, both distinct
scheduled concurrencies and all three bounded rate refinements execute. Reviewed
retries consume the remaining finite budget and may prevent completion:

| Layer, across both databases | Configured seconds |
| --- | ---: |
| Idle admission | 10 |
| Interleaved noise processes | 390 |
| Native attribution layers | 120 |
| Closed-loop concurrency ramp | 850 |
| Scheduled-rate coarse grid | 1,040 |
| Up to three halving/midpoint refinements per route/concurrency | 540 |
| Frozen-rate validation | 170 |
| Dynamic/prepared short projections | 340 |
| Fully consumed 8,192-row heavy streams | 340 |
| Same-process recovery | 136 |
| Separate allocation profiles | 148 |
| Separate query-plan consumer command caps | 240 |
| **Preflight configured total** | **4,324 (72.07 minutes)** |
| Eight unchanged 1,800-second long segments + idle | **14,450** |
| **Configured overall total** | **18,774 (312.9 minutes)** |

The query-plan command caps include those consumers' warmup. Other initialization,
warmup, process/database startup, drain, hashing and artifact transfer are
additional observed time, not long-load credit. The preflight step has 776
seconds of allowance beyond configured intervals; the controller has 1,626
seconds overall. The actual budget checks retain failed/interrupted work
instead of shortening a long segment or claiming its missing duration.

Every new campaign PostgreSQL cluster explicitly uses UTF-8. The recorded server
and client encodings and locale are common to all four routes. Earlier allocation
probes that used SQL_ASCII retain their original environment and identities.

Before timing cells, each database runs four separate, single-worker query-plan
diagnostics. Each consumer warms 350 operations, then completes all 35 cases once
with prepared-query reuse. PostgreSQL uses a separately built, source-locked
`auto_explain` module with JSON plans and full ordered parameter values, plus
server statement logs. Coverage sums the occurrence requirements of identical
SQL/parameter legs across cases; one lookup cannot satisfy several required
executions. The actual server plans are retained, including independent ORM SQL
differences and extra provider health/transaction commands. Parameter type OIDs
and generic/custom plan counters are not emitted by these logs and are explicitly
unproven.

SQLite records the same successful consumer case/row counters, then replays each
manifest SQL leg as read-only `EXPLAIN QUERY PLAN` with the exact bundled SQLite
library and typed parameters. This is a separate replay, not observation of the
provider's dispatch or hidden statement cache. Database/WAL hashes and zero
changes verify that the replay preserved data. Neither diagnostic receives timing,
allocation-comparison, or long-segment credit. Timing PostgreSQL servers do not
load `auto_explain` or enable its logging collector.

The controller admits Linux x64 on a GitHub-hosted VM only after recording its
boot ID, image version, CPU model, visible CPU set, quotas, throttling counters,
memory limits, idle CPU and steal. Client, PostgreSQL server and observer CPU
affinities are disjoint. SQLite runs inside the client process. Both preparation
and measurement must report the same image version. Runtime ELF dependencies are
resolved and hashed on the measurement VM; consumers additionally hash their
actual dynamically loaded provider or SQLite library. Physical-host exclusivity
is not asserted merely because the guest job is dedicated.

A failed idle admission is retained and cannot be bypassed with `--resume`.
Independent before processes must also pass the frozen noise limit. Guest quota
changes, throttle events, excessive total/interval steal, uncertain CPU counters
or failed error/tail criteria prevent a long observation from qualifying.

## Workload and comparison boundaries

Each process starts from a hash-bound snapshot created through the before public
consumer. PostgreSQL starts with fixed durability/isolation/settings and reserves
administrative connections beyond the maximum client concurrency. SQLite copies
the same seed files. Startup, initialization, warmup, calibration, drain and idle
observations are separate from the requested 1,800 seconds of offered load.
Every accepted request is drained after the offered interval, and that drain time
remains visible in throughput and scheduled latency.

After a successful, fully validated process and joined database owner, the
controller records the private database clone's file hashes and sizes and retires
that reproducible clone. This bounds disk/WAL retention across the many independent
processes. The immutable seed and every source, binary, result, resource sample and
owner receipt remain. Failed attempts retain their database copy for investigation.

The case sequence is identical across routes. Per-case started/succeeded/failed
and row totals must reconcile with the process totals. Declared cases with no
accepted execution may remain in an overload diagnostic, where every rejection
is retained. A qualifying long segment must actually execute all frozen cases.
Mixed-write IDs must be backed by `writesDatabase: true` in the workload manifest;
a read-only case cannot receive a write label in the request to pass this gate.

Every new consumer receipt explicitly records process-lifetime oracle failures,
the first bounded failure detail, and the cumulative counts at the measured
interval's start and end. Row/type/shape/digest, persisted-state and owner-retirement
violations invalidate the entire command even if a later cleanup exception replaces
the original thrown exception. The counters remain sticky across recovery phases.
Missing fields in older receipts mean unproven, not zero; those earlier receipts
retain their original historical scope. Ordinary provider failures, cancellations
and rejected arrivals retain their separate operation/error ledgers and SLO budget.
The workload's deliberately caught stale-write conflict is a successful checked
business outcome. The two managed routes use the same reader, retain both primary
and retirement exceptions on a double failure, and dispose each acquired reader
exactly once.

Managed allocation totals cover the complete consumer process, including async
and concurrent worker activity. Lists, DTOs, strings and typed JSON results are
business-result allocations even when Korda's generated mapper creates them.
Framework overhead is the additional query/binding/transport/mapping-control/
ownership cost; harness work remains a separately described contribution to the
raw total. No business-result-zero requirement is imposed. A numeric row-advance
window with zero raw bytes does not establish a zero-byte query, result DTO, or
whole framework. Allocation attribution uses the separately retained measured
boundaries; the controller does not subtract estimated result or harness bytes.

`shortQueries` runs five basic projections (`numeric`, `text`, `money`, `json`,
`wide`) for 15 seconds at one connection in both dynamic and prepared modes.
All four routes run both modes on each database: sixteen independent short
processes. They share the primary manifest, immutable seed, package inputs and
result oracles. Every selected case must execute, and its row count must equal
its successful operation count times its exact expected rows. Subset and mode
are explicit in command/result checkpoints; the primary mixed workload is not
mutated. These closed-loop service-latency comparisons, warmup, drain and idle
time remain separate from the eight long offered-load segments.

The primary prepared C++ column reuses physical statements and performs the
driver's ordinary complete result drain. Dynamic C++ constructs and prepares each
operation. It does not inherit Korda's extra protocol-confirmation traffic merely
to make the gap smaller. Explicit short native layers separately measure
per-operation preparation and product-style confirmation, so their contribution
can be isolated. SQL executions, libpq submissions and confirmation calls remain
separate counters; none is relabeled as a physical network round trip.

The calibration has distinct purposes:

1. Independent processes interleave the four routes to estimate before-process
   noise on the admitted VM.
2. A single-connection and concurrency ramp measures closed-loop service latency.
   It is explicitly a capacity estimate and not a scheduled-latency throughput
   limit.
3. A scheduled-rate grid runs at the frozen long concurrency and the best
   concurrency observed in the closed-loop ramp. It reports the highest observed
   success throughput satisfying the same error/rejection and scheduled-p99
   thresholds. If every grid rate fails, the next refinement tests half the
   lowest observed rate. Once a tested successful rate has a higher tested
   failing rate, midpoint windows narrow that observed bracket while its relative
   gap exceeds 5%. Halving and midpoint observations share the same limit of
   three five-second windows per route/concurrency; no extra timing budget is
   added. If none passes, calibration still fails. Selection inputs, prior failures, each new result and
   the final bracket are retained. There is no retry to select a preferred sample
   and no unlimited expansion when an upper failure is absent. Five-second
   refinements are explicitly short observations; the best tested rate and its
   bracket are not a proof of a global maximum or a long-duration capacity limit.
4. The common sustainable rate is frozen using the before, native and independent
   ORM results at the actual long concurrency. The optimized route cannot lower
   its comparison load after a regression. Every route validates that rate before
   the distinct retained-owner overload/recovery diagnostic.

Future campaigns omit the redundant fresh-process overload/recovery pair. A new
process cannot establish recovery of the same saturated pool, and this removal
leaves the historical controller snapshots and prior raw records intact. The
capacity grid still observes saturation; `same-process-recovery` runs for all
four routes and both providers. It retains the same source, connections/sessions,
prepared plans and worker objects through reference load, overload, complete
queue drain and return to the frozen sustainable rate. Worker threads and bounded
schedulers may restart between phases; that fact is explicit in the receipts.
All accepted and rejected operations remain counted. Each phase must return to
the warm owner baseline, final tracked owners must drain, overload must actually
reject arrivals, and no phase may hide a business failure behind a top-level
`Passed` flag. Reference and recovery must satisfy the same scheduled-p99 limit.
Failed or unclassified recovery evidence prevents qualification and remains saved.

`heavyWorkload` is a separately hashed and seeded 8,192-row fully consumed wide
stream. Its SQL, projection, materialization and construction contract must match
the compiled primary `wide` query; only the explicitly frozen fixture size and
row parameter change. The controller runs C1 and C4 for every route/provider,
records the heavy manifest/cases/seed identity in each observation, and checks
exact rows per successful operation. It does not mutate the primary mixed case
sequence. These 15-second service-latency comparisons have no long-segment credit.
The independent ORM receives `--native-library` only for SQLite.

The native allocation diagnostic runs separately for all four routes at C1/C4 with a bounded glibc
interposition library, identified by its actual loaded SHA-256. Its table is fixed
at 1,048,576 entries / 24 MiB; it never expands. Raw allocator snapshots must agree
on capacity and version, have no unknown/dropped/duplicate/initialization failures,
and reconcile requested/usable allocated, released and live bytes before division.
Successful reallocation counts retirement plus a replacement, including in-place
resize. The profiler, its table overhead and its global lock affect resources,
so these runs are explicitly excluded from normal timing comparisons. Their peak
covers tracked public allocator boundaries, not private mmap, hidden allocator
work or transient internal realloc overlap. Ordinary timed binaries run without
`LD_PRELOAD`; managed consumers additionally use an explicit, optional snapshot
adapter and require `DB_PERF_ALLOC_PROFILE_LIBRARY` to name that exact preloaded
image. Missing full-address-space native heap metrics remain explicit.

The shared managed scheduler and native scheduler use `ceil(rate * seconds)`
planned arrivals, absolute deadlines, a bounded FIFO, non-waiting admission and
explicit rejection. Counters retain planned, attempted, sent, started, successful,
failed, rejected and retried requests. Four histograms retain service latency,
successful scheduled latency, all completed scheduled latency including failures,
and start delay. Per-second and per-case ledgers are independently reconciled.
No queue or harness allocation is blindly subtracted from measured totals.

## Measurement, interruption and reuse

`run.py` is also usable for a short frozen command plan. It does not independently
qualify a host or grant long credit:

```bash
python3 benchmarks/db-perf/run.py plan.json --output /absolute/fresh/evidence --sample-seconds 0.5
```

It retains stdout, command receipts, raw resource samples, process identity and
OS `wait4` lifetime CPU/peak RSS for successful and failed commands. Every command
has a finite deadline. Exceptions and timeouts terminate, kill if necessary, and
join the owned client. PostgreSQL shutdown also has a bounded join; an unretired
owner aborts the campaign. Result files cannot overwrite another command's
output or observer evidence.

CPU/RSS/PSS samples have acquisition start/end times and PID start identities.
Steady summaries use only complete sample intervals inside the application's
recorded monotonic boundary; unsampled edges are not extrapolated. Process-tree
samples remain explicitly non-atomic. Missing RSS/PSS is unavailable, not zero.
OS lifetime CPU/peak RSS, sampled steady peaks, application-reported CPU, native
allocator observations and idle retention are distinct measurements.

Formal PostgreSQL cells require task-owned cgroup-v2 cumulative CPU accounting.
For each server owner the observer creates one fresh child of its current cgroup.
It does not enable controllers, edit parent settings, change quotas or alter
permissions. A fixed launcher writes only its own PID to that new group's
`cgroup.procs`, waits for the write to succeed, then executes PostgreSQL. Every
backend therefore inherits the group from its first fork. The launcher helper
finishes before workload boundaries; its earlier setup CPU is not benchmark
server work. A missing counter, failed entry, foreign member, reused PID, changed
group identity or unjoined owner rejects the cell and preserves its receipts.

The group's `cpu.stat` user/system/usage counters retain CPU from exited
PostgreSQL backends. Raw cumulative start/end counters and the group device/inode
are retained at every sample. Steady CPU uses complete sample acquisitions within
the application's monotonic workload/drain boundary. The original process-tree
own/reaped counters remain separately labeled non-atomic cross-checks, and final
group totals are retained beside the OS `wait4` lifetime totals. Missing PSS/RSS
remains explicit. A group must be empty after server join before this task may
remove it; a failed or occupied group is retained for investigation.

Creating a group also creates a scheduling-group identity. Its actual
`cpu.weight`, `cpu.max`, effective cpuset and memory limit, together with unchanged
ancestor constraints, are recorded and must match across all four PostgreSQL
columns. No claim is made that group creation is invisible to scheduling.
Task-group throttle events must be zero in ordinary timing cells. Kernel
[CPU interface documentation](https://docs.kernel.org/admin-guide/cgroup-v2.html#cpu-interface-files)
specifies that cumulative usage/user/system counters are readable even when the
CPU controller is not enabled, so the observer does not enable it. Local negative
and launcher tests are not evidence of actual VM cgroup admission; the dedicated
preflight must create, observe and retire these owners successfully.

Every ordinary timing cell must also pass the same guest-quota, zero-throttling,
overall/interval steal and client/server CPU-counter quality checks before it
can influence calibration, capacity refinement, fixed-load comparisons or long
qualification. The saved `resourceQualityPassed` and reason list are separate
from `sloPassed`: an environmental failure cannot become a capacity failure
bracket. Raw and normalized evidence is retained before the controller stops.
Resume recomputes these quality checks; another attempt requires the explicit,
bounded investigation-and-retry flow below, including when the workload itself
met its SLO. Instrumented diagnostics retain their separate scope.

After unpacking the immutable runtime bundle, the workflow uses:

```bash
python3 "$DB_PERF_BUNDLE/harness/campaign.py" measure \
  --bundle "$DB_PERF_BUNDLE" --output "$RUNNER_TEMP/db-perf-campaign" --short-only
python3 "$DB_PERF_BUNDLE/harness/campaign.py" measure \
  --bundle "$DB_PERF_BUNDLE" --output "$RUNNER_TEMP/db-perf-campaign" --resume --next-segment
```

Repeat the second command for the eight segments on the same still-running VM.
Resume requires identical request, bundle, source hashes, image and boot/job
identity. Every reused normalized result rechecks its raw result, stdout, command,
resource samples, plan and controller hashes. A partial attempt does not silently
rerun. A reviewed retry on the same live VM requires an explicit step and reason,
is bounded by the frozen attempt limit, and retains the previous attempt:

```bash
python3 "$DB_PERF_BUNDLE/harness/campaign.py" measure \
  --bundle "$DB_PERF_BUNDLE" --output "$RUNNER_TEMP/db-perf-campaign" --resume \
  --retry-step postgresql/long-korda_after --retry-reason "Recorded investigation of the retained failure"
```

A qualifying long result cannot be replaced with a preferred sample. Successful
calibration samples cannot be retried to improve the noise estimate. Lost owner
retirement receipts require investigation instead of a retry. A new VM needs a
new campaign; observations remain available for analysis but are not rebound to
the new environment. The controller reserves the end of the six-hour job for
drain and artifact upload, and refuses a new measurement that exceeds its finite
campaign budget.

## Acceptance remains in Plan

The campaign's final state is `long_campaign_observed_batch_acceptance_separate`.
`wholeBatchAccepted` remains false. Public allocation coverage, narrow regression
proofs, cancellation and failure paths, native whole-heap metrics, observed
same-pool recovery, SQL/plan review, normal CI/review/merge, automatic preview and actual
public-package AOT verification must be reconciled in Plan before batch completion
is claimed. The controller does not turn missing measurements into zeros or use
document generation as evidence of execution.
