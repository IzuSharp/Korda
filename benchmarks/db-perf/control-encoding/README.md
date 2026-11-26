# Internal ControlRecord encoding experiment

This Linux x64 Native AOT probe calls the actual internal `ControlRecord.Encode`
through a `ProjectReference` and the existing `Korda.Data.Tests` friend assembly.
It isolates ordered-field encoding after the fields and their payload arrays have
been constructed. It is a source-only allocation experiment, **not an ordinary
outside-source `PackageReference` consumer, a database query, or DB-Perf-01 final
acceptance**. Ordinary business calls also pay their input-construction costs.

The output wire `byte[]` is a **framework allocation**. This fixture creates no
business DTO, list or result string, so business-result allocation is zero only
within this fixture. Never label the complete encoder or library as zero bytes.
Record collection counts for the shared managed heap; do not divide GC counts
into business and framework categories.

## Recorded comparison and short regression budget

The experiment baseline is `7cf6c7db4957fdb27ea408b56d52ce1b28235a5b`, after earlier
DB-Perf-01 work. It does not replace the batch's original fixed-main baseline.
The baseline `DbWire.cs` SHA-256 is
`d9b3101900038e81707dea2d0d82ec793fd83b0e52d3b5de785ad8a896c101f4`; the reviewed
ordered-field candidate is
`a0178f6ccc63f0431e5f7af03caec691eefa90661e1cd591c920a3e6ed02eaa6`.

The retained `control-encoding-v1/comparison.json` has SHA-256
`cd07281aa0651eb8d1cd3ea0f4ad88849d76730203bb073d300e52c2dcfe8516`. Three independent
processes per lane ran in the order before-1, after-1, after-2, before-2, before-3,
after-3. Each case warmed up for 2,048 calls, then measured 8,192 complete encodes
and byte-sum output consumptions using `GC.GetTotalAllocatedBytes(true)`.
Input creation, golden preparation, invalid-input observations and JSON reporting
are outside each allocation window. Original cumulative counters and Gen0/1/2
counts remain in each result; no estimated business/control allocation is
subtracted.

| Prebuilt input | Encoded bytes | Before framework B/op | Candidate framework B/op |
| --- | ---: | ---: | ---: |
| Empty | 16 | 312 | 40 |
| Ordered 1 field | 33 | 736 | 64 |
| Ordered 4 fields | 90 | 1,016 | 120 |
| Ordered 5 fields | 111 | 1,064 | 136 |
| Ordered 8 fields | 180 | 1,520 | 208 |
| Ordered 32 fields | 1,056 | 3,568 | 1,080 |
| Reversed 5 fields | 111 | 1,064 | 1,032 |
| Mixed 5 fields | 111 | 1,064 | 1,032 |

Each allocation count was identical in all three processes for its lane. The
ordered five-field regression therefore uses the observed **136 B/op maximum**,
or **1,114,112 raw bytes per 8,192 operations**, without inventing an unmeasured
noise allowance. It requires a positive count because the owned output array is
included. The regression retains all three short windows before enforcing the
bound. An unexpected raw-count failure must be investigated against the exact
runtime and fixture; it is not permission to subtract unexplained allocations.

Full golden outputs and seven invalid-input exception observations were equal
across the six original processes. The measured repetition loop checks a byte-sum
checksum, which does not prove a complete byte comparison on every call. Local
timing includes result consumption and short-process noise; it establishes no
database throughput, p99, CPU, native-memory or steady-state retention result.

## Existing abstraction.test integration

`tests/Data.Abstractions/ControlRecordTests.cs` is compiled into the existing AOT
test executable. The ordinary managed correctness run covers fixed golden bytes
for ordered/reversed/mixed inputs and an empty record, input array and payload
preservation, independent returned storage, the existing null/zero/duplicate
errors and their precedence, the exact 4 MiB encoded boundary and one byte over.
Two bounded tests reuse payload arrays to reach checked aggregate-length and
header-plus-payload overflow without allocating gigabytes.

The existing `tests/Data.Abstractions/verify.py` additionally launches the same AOT
executable in a fresh process for `--control-record-allocation-regression`. It
performs 2,048 warmups and three 8,192-operation windows with the fixed five-field
fixture. Its `control-record-allocation.json` retains raw process-wide allocation
and all GC before/after counts, operation counts, checksums, runtime identity and
the reference budget's evidence hash. Input construction and reporting stay
outside the windows. The test has no SQL, native-host dependency or long load.

No new CI module is needed: `tests/Data.Abstractions/` already selects
`abstraction.test`, and the product source is in the existing database dependency
route. Normal PR CI keeps the existing module's API, package/AOT, native golden
and short regression checks. The four-hour mixed-load campaign remains separate.

Run the normal component gate with the repository's pinned toolchain and an
already-built native fixture:

```bash
python3 tests/Data.Abstractions/verify.py \
  --output artifacts/component-tests/abstraction \
  --native-build artifacts/cmake/linux-x64-release
```

After that AOT executable has been published, either new entry can be rerun
without rebuilding:

```bash
artifacts/component-tests/abstraction/aot-tests/Korda.Data.Tests \
  artifacts/db-perf/control-encoding/correctness --require-aot \
  --control-record-correctness
artifacts/component-tests/abstraction/aot-tests/Korda.Data.Tests \
  artifacts/db-perf/control-encoding/regression --require-aot \
  --control-record-allocation-regression
```

## Reproducing the isolated source experiment

Reuse matching original evidence when its source, executable, fixture, toolchain
and environment identities still apply. The commands below are for an intentional
new capture. Run them sequentially on the same Linux x64 machine using the pinned
.NET/Clang tools; do not build both lanes into the same intermediate directories.
Start at the candidate worktree root. Use a new task-named baseline worktree and
copy only this unchanged probe and lock file into it, leaving baseline product
source at the fixed commit.

```bash
set -euo pipefail
control_baseline=7cf6c7db4957fdb27ea408b56d52ce1b28235a5b
control_after_tree="$PWD"
control_before_tree="$(dirname "$PWD")/Korda-control-before-DB-Perf-01-linux"
control_output="$PWD/artifacts/db-perf/control-encoding"
control_project=benchmarks/db-perf/control-encoding/ControlEncodingProbe.csproj
mkdir -p "$control_output"
git worktree add --detach "$control_before_tree" "$control_baseline"
mkdir -p "$control_before_tree/benchmarks/db-perf/control-encoding"
cp benchmarks/db-perf/control-encoding/Program.cs \
   benchmarks/db-perf/control-encoding/ControlEncodingProbe.csproj \
   benchmarks/db-perf/control-encoding/packages.lock.json \
   "$control_before_tree/benchmarks/db-perf/control-encoding/"

for control_lane in before after; do
  if [ "$control_lane" = before ]; then
    control_tree="$control_before_tree"
  else
    control_tree="$control_after_tree"
  fi
  git -C "$control_tree" rev-parse HEAD > "$control_output/$control_lane-commit.txt"
  git -C "$control_tree" status --short > "$control_output/$control_lane-status.txt"
  (
    cd "$control_tree"
    dotnet restore "$control_project" -r linux-x64 -p:PublishAot=true \
      --locked-mode --disable-build-servers
    dotnet publish "$control_project" -c Release -r linux-x64 \
      -p:PublishAot=true -p:IlcTreatWarningsAsErrors=true \
      -p:UseSharedCompilation=false -m:1 -nr:false --no-restore \
      -o "$control_output/$control_lane"
  ) > "$control_output/$control_lane-publish.log" 2>&1
  sha256sum "$control_tree/src/Data.Abstractions/DbWire.cs" \
    "$control_tree/benchmarks/db-perf/control-encoding/Program.cs" \
    "$control_tree/$control_project" \
    "$control_tree/benchmarks/db-perf/control-encoding/packages.lock.json" \
    "$control_output/$control_lane/Korda.Data.Tests" \
    > "$control_output/$control_lane-identities.txt"
done

for control_run in before:1 after:1 after:2 before:2 before:3 after:3; do
  control_lane="${control_run%:*}"
  control_repeat="${control_run#*:}"
  if [ "$control_lane" = before ]; then
    control_tree="$control_before_tree"
  else
    control_tree="$control_after_tree"
  fi
  "$control_output/$control_lane/Korda.Data.Tests" \
    "$control_output/$control_lane-$control_repeat.json" \
    "$control_tree/src/Data.Abstractions/DbWire.cs" "$control_baseline"
done
```

Retain the actual commands, start/finish times, SDK/compiler versions, publish
logs, source/probe/project/lock hashes, executable hashes and all six raw JSON
files with any new comparison. The probe hashes the supplied source file at run
time; this is not embedded proof of which source compiled the executable. Keep
the corresponding publish inputs and build evidence instead of treating that
runtime file hash as a complete build attestation. Changes to the fixture or
compiler require a fresh scoped budget assessment before changing the regression.
