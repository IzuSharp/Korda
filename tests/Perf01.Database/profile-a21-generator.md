# Perf-01 A21 generator profiling

Status: source-prepared diagnostic launcher. Root and the independent codec-domain
reviewer reported no source blocker. No syntax check, finite fixture, application
or profiling execution is claimed yet. Use only in root's separate exclusive
execution window; the independent reviewer ran no tests or workloads.

`profile_a21_generator.py` runs the existing Perf-01 DB driver and its ordinary
PostgreSQL supervisor, package consumer, exception observer, HTTP target,
workload and correctness/retirement checks. Only the supervised A21 child
scheduler command is wrapped. The child loads the unchanged A21 file once and
adds in-memory timing wrappers; no formal driver file is modified.

The source-defined hooks record:

- `gc.callbacks` start/stop monotonic timestamps, generation, collected and
  uncollectable counts, the active instrumented method and `gc.get_count()`.
- Synchronous raw-event `write` and `flush`, the A21 module's `json.loads` and
  `json.dumps`, `RunState.event`, and `observe_process` wall durations.
- All-call counts, total durations and maximum intervals. Calls of at least
  1 ms and GC events are retained in bounded numeric buffers, capped at 20,000
  records each, with explicit dropped-record counts. No extra diagnostic file
  write occurs in a load hook.
- After `run_cell` returns, retained-result container cardinalities and one
  `gc.get_objects(generation)` count for each generation. The returned result
  stays referenced through this post-run snapshot. The enumeration creates
  temporary lists and is deliberately outside the application load; it is not
  a during-load graph-size series. `gc.get_count()` likewise is not a live-object
  count.

The launcher never disables GC, invokes a manual collection, or changes GC
thresholds. It records their before/after values and requires the worker receipt
to finish with the policy unchanged. Other pre-existing callbacks are left in
place. Method wall durations include nested collection and possible OS
descheduling; this is not a CPU or syscall stack profiler.
`active_method` is an interval label, not proof of the initiating stack or thread.
The final `gc_count_after` / `gc_stats_after` snapshots include the post-run graph
introspection performed after this profiler's callback is removed; use the
recorded `gc_events` for actual callback observation intervals.

The wrappers add calls, timestamps, counters and bounded allocation. These
changes can affect collection timing, scheduling and latency. Consequently the
launcher requires `--variant diagnostic`, marks `frozen-inputs.json` and
`profile-driver.json` with `formal_timing_eligible=false`, and cannot provide a
formal before/after or long-duration result. The original generator validity
verdict remains unchanged, including any observed dispatch-lag failure.

## Concrete invocation

The following 120-second example preserves the observed 171/s mixed-load point
and passes ten thousand operations before stopping. Use the actual before
application and observer outputs shown only if their retained build manifests
still match. All timing on this machine must be stopped first.

```sh
PERF01_ROOT=/workspace/scratch/01c249676f47
source "$PERF01_ROOT/perf01-toolchain/env.sh"
PERF01_PRODUCT="$PERF01_ROOT/.worktree/Perf-01-linux/NativeWeave"
PERF01_PYTHON=/opt/codex/runtimes/codex-primary-runtime/dependencies/python/bin/python3

taskset -c 2,3 "$PERF01_PYTHON" \
  "$PERF01_PRODUCT/tests/Perf01.Database/profile_a21_generator.py" launch -- \
  --package-probe "$PERF01_ROOT/perf01-build/before/reference-002" \
  --observer "$PERF01_ROOT/perf01-build/before/db-observer/manifest.json" \
  --provider postgresql --postgres-bin "$PERF01_ROOT/perf01-toolchain/postgres18/bin" \
  --protocol http1 --scenario mixed --variant diagnostic --stage short \
  --rate 171 --seconds 120 --concurrency 16 --generator-cpus 0,1 \
  --seed 20261119 --warmup 16 --recovery-requests 16 --idle-seconds 5 \
  --max-p99-ms 500 --max-rss-mib 256 --continue-classified-overload \
  --isolation-note 'Exclusive profiling-only A21 generator diagnostic; service CPUs 2,3; generator CPUs 0,1' \
  --output "$PERF01_ROOT/perf01-evidence/profiling/before/db-generator-001"
```

Python must be exactly 3.12.14; its resolved executable and SHA-256 are recorded.
PostgreSQL starts on service CPUs2,3 before the original controller moves to
generator CPUs0,1. The wrapped scheduler remains the supervised root PID, so the
existing target/controller parent checks and process-tree cleanup still apply.
The application's taskset launch and provider durability settings are unchanged.

The cell retains all original numerical evidence and adds
`a21-generator-profile.json` and `profile-driver.json`. A sibling
`db-generator-001-launcher.json` records ownership before output setup and the
actual wrapped child command. An absent/running/failed worker receipt is an
incomplete profile, even if a partial load happened to return responses. The
original watchdog and termination behavior are reused. Partial records remain
available on interruption; forced termination cannot manufacture a final
profiling receipt or resource retirement proof.

Align GC/slow-call intervals with the original A21 `dispatch_ns` and
`target_completion_observed_ns` timestamps. `previous_state_event` identifies the
last completed event-hook call, not the current event being decoded or written.
An interval overlap supports the instrumented run's attribution; absence of a
record below the 1 ms slow-call threshold does not prove zero cost, and a change
in profiling behavior is not an optimization result. Keep the existing invalid
190/s and171/s calibration cells unchanged.
