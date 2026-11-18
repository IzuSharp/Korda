# End-to-end fault and resource verification

B22 verifies the assembled reference application through actual native database
and HTTP/TLS execution. The external Python controller observes HTTP and inspects
durable SQLite/PostgreSQL data independently. It does not call a product debug
endpoint, replace a provider, manufacture outcomes, or turn accepted component
tests into a new assembled-product pass.

The required scope is pinned by Plan `tasks/B22.md`,
`fragments/end-to-end.json`, and Design commit
`c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9`, particularly D20
`docs/verification/reference-application.md` and D12
`docs/verification/models-and-matrix.md`. `tests/Verification.EndToEnd/matrix.json`
is the implementation coverage index. Execution receipts, not the index, establish
which cells actually passed. Linux x64 is the only platform exercised here.

## Fault evidence

PostgreSQL transport barriers preserve the actual frontend/backend protocol.
Holding COMMIT before forwarding it upstream is **after NativeWeave dispatch**;
the application may correctly preserve Unknown even when the independent oracle
later observes no durable effect. Pre-admission cancellation is a separate public
transaction fixture. Discarded acknowledgment requires observing the real server
COMMIT command completion and idle ReadyForQuery before dropping those replies.

Provider cleanup uses the accepted PostgreSQL protocol drain and physical-close
rules. A failed cleanup is not a rollback receipt. Source/session identity,
transaction outcome, connection disposition, and actual durable effects are
recorded separately. External PostgreSQL SQL inspection never traverses the
fault proxy.

Outbox failure is injected after a separate durable consumer receipt exists and
before source acknowledgment. A restart must increase delivery attempts while
retaining one effect for the event identity. Attachment failures cover an open
incomplete upload, durable rename before reference commitment, and interrupted
orphan collection. A process crash is not evidence of power-loss durability.

SQLite busy uses an actual independent writer transaction. The filesystem fault
profile targets native writes to the disposable database/WAL with injected
`ENOSPC`. If child syscall tracing is unavailable, a B22-only `LD_PRELOAD` shim
forwards libc calls except the single externally armed, exact-file write. Its
audit must prove injection; the actual native exception must preserve SQLite
provider code13, including Unknown when the error occurs during COMMIT. This is
labelled **injected libc I/O ENOSPC**. It does not fill a shared filesystem, claim
physical disk exhaustion, or substitute `RLIMIT_FSIZE`.
`PRAGMA max_page_count` on an unrelated connection does not constrain the
application's pooled connections and is not used as a whole-application fault.

## Resource observations

The standard `MeterListener` reads the public `NativeWeave.Server` source for
active, queued and retiring requests, managed/native retained payload bytes, and
bounded diagnostics backlog. The source caches native snapshots for one second;
the observer respects that interval. The gauges measure framework ownership,
not process memory or all native objects.

Database counts come from `nw_db_diagnostic_count_v1` in the exact selected
provider module: source, session, operation, cursor, buffer, and opaque registry
entries. The observer uses public `NativeLibrary` APIs and keeps its own library
reference through final observation. It never loads a copied module to obtain an
empty counter set. Long-lived sources and metadata legitimately keep nonzero
counts; zero is required only where the fixture actually joins those owners.
The instrumented consumer uses an entrypoint-only wrapper around the unchanged
application body and joins its thread before collecting final observations.
Pre-finalizer and post-finalizer counts remain separate. This prevents a completed
Main Task or its entry frame from being misclassified as a persistent framework
root at ProcessExit, and discloses GC-owned SafeHandle retirement explicitly.

Normal GCHandle roots are not publicly counted by the NativeWeave packages.
An explicitly identified instrumented Native AOT consumer may enable EventPipe;
its real handle events, trace-loss accounting and known allocate/free controls
must pass before they can be used. Weak references to public objects provide a
separate retention witness and do not measure total runtime roots. Missing
counters remain unavailable. Private friend-assembly names and internal test
hooks are not consumer contracts.

## Duration and recovery

The required stages are 30 minutes of steady state, 24 hours of regression and
72 hours of release soak. Each stage requires a frozen workload, actual artifact
and storage identities, PRNG seed, fault schedule and finite resource thresholds
derived from the measured baseline. These are separate from B23 timed throughput
measurements and must not pollute them.

Every stage records monotonic elapsed time, load/drain/idle windows, raw resource
samples, independent effects, and interruption state. An incomplete stage remains
unaccepted. Restarts and changed inputs create explicit segments/attempts; they
never silently become one uninterrupted process soak. Process exit, body EOF,
and a flat RSS graph do not prove safe ownership retirement.

## Current delivery state

The external controller and public observer consumer are runnable. The controller
preserves failed attempts and distinguishes selected functional passes from full
B22 acceptance. See `tests/Verification.EndToEnd/README.md` for exact inputs and
invocations. `tests/Verification.EndToEnd/soak-profile.md` defines the actual
staged runner, finite trace/file budgets, calibration/freeze steps and interrupted
attempt recovery. Long stages remain subject to actual execution and independent
review. No 30-minute, 24-hour or 72-hour acceptance is asserted by this document.
