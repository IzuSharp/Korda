# Perf-01 shared-path profiling

`RetirementProbe` is an isolated package consumer using the existing test-friend
assembly identity. It does not alter product binaries or add public API. A serial
real SQLite/PostgreSQL scalar path records the existing start, terminal-copy,
take and retirement boundaries, CPU and managed allocation. It verifies each
scalar and observed native-quiescence/root-release invariant.

The observer has a measurable cost and cannot be included in the uninstrumented
before/after timing cohort. Callback-to-take and take-to-retirement intervals
localize waiting; they do not directly count native `Pending` responses, physical
database lock waits or connection-pool waits. Those values must stay unavailable
unless independently observed. Serial operations are a deliberate observer
constraint, not the application's high-concurrency acceptance workload.

Copy this project outside the checkout, give it an isolated NuGet configuration
with the exact rebuilt B24 feed, pin `NativeWeavePackageVersion`, and publish
Release Native AOT for `linux-x64` with the same CPU baseline as the main drivers.
Keep source, package, AOT and loaded-native identities with every result. Run it
only in a profiling window, separately from formal timing.

## Public typed-page allocation probe

`PageProbe` uses the ordinary public package API and a distinct assembly identity;
it has no internal test-friend access or boundary callbacks. It creates its own
fixed-name table, inserts 100 deterministic `BIGINT/TEXT/BIGINT` rows through typed
parameters in a confirmed transaction, and closes the seeding session. A new
logical session checks the committed count, runs 20 warmup pages,
then repeats the same ordered, parameterized 100-row `QueryAsync` projection.
Every returned DTO field and row position is checked. Commands and expected rows
are prepared before measurement. The source uses one physical connection,
128-row batches, and 64 KiB batch/buffer limits.
The new session may reuse that pooled physical connection. Its committed-data
check is not the separate-tool durable oracle used by the formal database driver.

The measured window contains the complete buffered page call, owned DTO/list
materialization, stream cleanup and allocation-free successful row comparisons.
Process-wide managed allocation, CPU, GC collection deltas and per-page stopwatch
ticks are recorded. These counters cannot attribute every byte to TypedStream,
separate SQL execution from shared binding work, or establish an uninstrumented
capacity result. Initialization, warmup, hashes, final table removal and session/
source drain remain outside the window. No HTTP or JSON serialization is inside
the window. Serialization of the final profiling receipt is separate.

```sh
python3 tests/Perf01.Shared/prepare.py --probe page \
  --feed "$BEFORE_FEED" --version "$BEFORE_VERSION" --output "$NEW_PAGE_BUILD"
# Execute only after root grants an exclusive profiling window.
taskset -c 2,3 "$NEW_PAGE_BUILD/aot/NativeWeave.Perf01.PageProbe" \
  sqlite "$NEW_ABSOLUTE_SQLITE_FILE" 1000 "$NEW_PAGE_RESULT_JSON"
taskset -c 2,3 "$NEW_PAGE_BUILD/aot/NativeWeave.Perf01.PageProbe" \
  postgresql "$ISOLATED_LOOPBACK_POSTGRES_URI" 1000 "$NEW_PAGE_RESULT_JSON"
```

Use the same source, page count, limits and CPU placement for before/after feeds.
The executable requires actual Native AOT. SQLite requires a new absolute file
path and explicitly retains WAL, FULL synchronization and the five-second BUSY
timeout. PostgreSQL accepts only a loopback URI with the existing local-test TLS
setting; no remote or production configuration is implied. An existing
`nw_perf01_page_probe` table rejects setup without being overwritten or dropped.
Only a table successfully created by this invocation is removed during cleanup.

The result includes the running executable and adjacent prepare-manifest hashes,
actual loaded native module hashes, and observed transaction/session/source
release flags. Failures retain a failed/partial receipt where shutdown permits
and exit unsuccessfully. Per-cursor native counters are unavailable through this
public consumer; successful QueryAsync includes the product's stream retirement
join, and the public owner cleanup snapshots are checked separately. Keep the
build receipt and synthetic database/output artifacts with the profile.

`prepare.py` still selects `RetirementProbe` when `--probe` is omitted. Both
choices retain isolated feeds/caches, exact package versions, Release Linux x64
Native AOT, the existing instruction-set argument and consumer Rust traps.
The new page probe is source preparation only until an actual build/execution
receipt is attached; it grants no performance or 30-minute acceptance credit.
