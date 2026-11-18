# B22 staged resource profile

`soak.py` has four explicit modes. `probe` exercises the controller for three
seconds of load and five seconds of idle; it cannot create a calibration profile
or a long-stage pass. `calibrate` measures five actual20-second load /20-second
idle cycles. `freeze` binds that passed receipt and an explicit review note to an
immutable profile. `run` accepts only that frozen identity and one exact stage:

| Stage | Required actual duration | Workload |
| --- | --- | --- |
| `steady-30m` | 1,800 seconds of uninterrupted offered load, then drain/idle | Continuous fixed arrivals |
| `regression-24h` | 86,400 seconds | Repeated240-second load /60-second drain/idle cycles |
| `release-72h` | 259,200 seconds | Repeated240-second load /60-second drain/idle cycles |

All times are monotonic. Cleanup and trace analysis never supply missing load
time. A signal interrupts the attempt, drains the finite client set, attempts
normal application closure and preserves progress. A subsequent attempt can
name `--predecessor /absolute/old/progress.json`; the relationship and previously
observed time remain visible, without silently adding an old process lifetime
to a new uninterrupted stage. Existing evidence directories are never reused.

## Exact inputs and resource bands

Supply the observer consumer created by `prepare_observer.py`, its build
manifest, the pinned TraceReader executable, TLS files, provider and optional
PostgreSQL18 binaries. The profile binds every artifact hash, native module,
observer entrypoint delta, controller, trace reader, kernel, CPU affinity and
`DOTNET_PROCESSOR_COUNT`. Each runtime/provider has its own measured profile.
The target application remains the actual packed B21 implementation; the
observer is an explicitly separate diagnostic artifact.

The offered rate is an explicit input chosen using measured B23 product capacity;
the proposed B23 profile alone is not evidence of a sustainable rate. Controller
concurrency is finite, the executor cannot accumulate a pending queue, and a
one-second generator lag or exhausted outstanding slot invalidates the run.
Every request start retains its scheduled arrival time. This controller does not
make throughput or comparative latency claims.

The calibration proposes each resource band as the actual observed maximum plus
the actual measured idle-cycle spread. It introduces no universal percentage
improvement threshold. The accepted Server hard profile remains256 active or
retiring requests,128 metadata waiters, and64MiB per managed/native payload side.
Idle Server permits and payload credits must be exactly zero. Native sources and
registered metadata remain alive during service idle; their actual measured
baseline is distinguished from post-Main all-owner zero. Review the candidate
before freezing it. A later breach is a failed attempt, not a reason to edit its
frozen limits in place.

The observer samples public Meter/native counters at1.1-second intervals and
records explicit diagnostic collections during idle. Short transient peaks can
fall between samples; these observations do not replace the separate actual
resource-limit and retained-owner fixtures. Pre-finalizer values remain visible.
At normal completion, both the exact provider inventory and all six counters
must reach zero after application entrypoint join and finalization. Server
permits/bytes must also be zero.

EventPipe starts with the observed process. Eight normal and four pinned known
handles must be seen allocated and freed within their explicit phase window.
Actual GCHeapStats totals anchor tracked handle kinds at the idle windows.
Unknown initial destroy events remain recorded. Any trace loss, missing window,
failed control or unavailable counter prevents acceptance. The64MiB EventPipe
buffer is not permission to discard evidence: the complete output file is
retained and checked. Trace files have an8GiB cap; operation journals have a1GiB
cap; the host must retain2GiB free disk. Crossing a budget fails explicitly.

RSS, peak RSS, CPU ticks, thread and file-descriptor counts come from Linux
procfs. Runtime GCHandle totals are process observations and retain their kind
breakdown; they do not count every managed object graph root or attribute every
handle to NativeWeave. Native allocator live/retained bytes are unavailable and
are not replaced with RSS or fabricated zeroes.

## Workload and independent effects

The deterministic mix contains40% new create + same-key replay + authorized
recovery,35% keyset listing,20% streamed64KiB attachment + download, and5% changed
request-hash conflict. HTTP2/TLS requests deliberately use new client connections
to include connection churn. Every300 seconds a separate HTTP1/TLS upload sends
an incomplete body; its actual matching staging manifest/file is observed before
the client closes. It must produce no committed operation. This fixed schedule
stays below the declared8192 staging-manifest bound even for72 hours. Database
transport/commit/crash fault coverage remains bound to the separately executed
focused fault matrix.

The ordinary application outbox worker runs throughout load and idle. Each idle
window waits for durable acknowledgment, then an independent SQLite or `psql`
connection checks items/operations/outbox cardinality, atomic references and the
separate consumer's one-effect receipts. Application state intentionally grows
with confirmed writes; its size and count are distinguished from retained request
objects. The controller keeps only four attachment slots and finite outstanding
requests rather than an in-memory history of every created item.

## Commands and recovery

The following paths are explicit inputs, not repository-generated product files:

```sh
python tests/Verification.EndToEnd/soak.py calibrate \
  --artifact-directory /absolute/observer/artifacts/aot \
  --trace-reader /absolute/TraceReader.dll \
  --provider sqlite --mode aot --rate 4 --concurrency 4 \
  --certificate /absolute/localhost.pem --private-key /absolute/localhost.private.pem \
  --output /absolute/new/calibration

python tests/Verification.EndToEnd/soak.py freeze \
  --profile /absolute/new/calibration/profile-candidate.json \
  --review-note 'Record the actual independent threshold/profile review here.' \
  --output /absolute/frozen/sqlite-aot.json

python tests/Verification.EndToEnd/soak.py run \
  --profile /absolute/frozen/sqlite-aot.json --stage steady-30m \
  --artifact-directory /absolute/observer/artifacts/aot \
  --trace-reader /absolute/TraceReader.dll \
  --provider sqlite --mode aot \
  --certificate /absolute/localhost.pem --private-key /absolute/localhost.private.pem \
  --output /absolute/new/steady-30m
```

The example rate is syntax, not a measured capacity recommendation. PostgreSQL
adds `--provider postgresql --postgres-bin /absolute/postgres-18.0/bin` and uses a
separate frozen profile. All database/service children run in the same controller
invocation. `progress.json` is atomically replaced and fsynced after observations;
raw operation/resource/observer/trace files and the final `receipt.json` preserve
the exact attempt. Archive that directory and its protected local configuration
before replacing a transient environment. No named long stage is accepted merely
because the runner, probe, calibration or another stage succeeded.
