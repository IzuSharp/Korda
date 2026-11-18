# B22 external verification controller

These are actual-product tests of the accepted reference executable. Supply an
immutable B21 JIT or Linux x64 Native AOT artifact directory. The controller does
not rebuild product Rust, alter SQL, call internal framework test hooks or use
managed database/network implementations as the product Host.

## Focused fault execution

Use the existing pinned .NET11.0.100 environment and the B21 Python dependency
environment (`httpx[http2]==0.28.1`; resolved transitive versions are recorded with
the run). Both the reference service and PostgreSQL must run as descendants of
the same invocation on executors that isolate loopback between commands.

```sh
python tests/Verification.EndToEnd/run.py \
  --artifact-directory /absolute/existing/b21/artifacts/baseline-aot/attempt-001 \
  --mode aot --provider postgresql \
  --postgres-bin /absolute/postgres-18.0/bin \
  --certificate /absolute/test-ca/localhost.pem \
  --private-key /absolute/test-ca/localhost.private.pem \
  --output /absolute/new/b22-postgresql-aot
```

`--cases` selects explicit cells from `matrix.json`, for example
`--cases commit-acknowledgement-discarded provider-drain-failure`. SQLite uses
`--provider sqlite` and requires no PostgreSQL installation. HTTP/2 cells require
the explicit TLS certificate; validation is never disabled. Every output
directory is new. An interrupted/failed directory is evidence, not a location to
overwrite with a later pass.

`receipt.json` identifies exact executed application/native/controller bytes,
selected and unselected cells, assertions and failures. `timeline.jsonl`, actual
process logs, bounded protocol traces and independent storage preserve the fault
timeline. Generated `config.private.json` files, test credentials and application
data remain private and must not be added to Git or public PR logs.

`syscall_fault.py` records whether child ptrace is actually permitted. Its failure
is preserved. The default unprivileged alternative builds the narrowly targeted
`FilesystemFaults/fs_fault.c` shim for disposable children only. The real SQLite
library still executes SQL and reports the resulting provider error. A positive
control must prove the selected write failed while an unrelated file write
succeeded. `after-sync` is only a staging barrier: the outbox case separately
holds the source SQLite writer, releases consumer progress, and independently
observes the committed receipt before killing the publisher. A WAL-header fsync
alone is never called durable publication.

The PostgreSQL proxy distinguishes COMMIT dispatch from the selected upstream
network write. Its first post-COMMIT Sync is the accepted transaction driver's
confirmation drain; the subsequent Sync is session pool-return cleanup. The
pool-failure case targets that latter operation and retains the confirmed effect.
Source mapping is `native/database/postgres/src/transaction.rs` `commit` and
`native/database/postgres/src/session.rs` `Inner::request_close` / `Inner::drain`.

## Explicit public observation profile

```sh
python tests/Verification.EndToEnd/prepare_observer.py \
  --reference-source /absolute/accepted/b21/consumers/baseline \
  --feed /absolute/existing/feed \
  --output /absolute/new/b22-observer-build
```

This copies the exact application sources and adds the internal B22 observer.
It sets `EventSourceSupport=true` in this test consumer only, retains ordinary
PackageReferences and records all source/package/lock/native/artifact identities.
The copied application's Main accessibility changes from private to internal;
its body is verified unchanged. A separately selected entrypoint runs it on a
joined thread. Post-Main observations therefore occur after the application
entry frame and completed Task have ceased to be intentional lifetime roots.
No implementation package is changed. The observer uses public MeterListener,
NativeLibrary, runtime GC APIs and bounded FirstChanceException provider-code
observations; it does not record exception messages or request values.

Run the resulting `artifacts/jit` or `artifacts/aot` directory with `--observed`.
Every provider must have a nonzero actual source counter while running. Graceful
closure records Server permits/bytes and all six native counters. Pre-finalizer
and post-finalizer observations remain separate. A lingering owner fails the
observation; neither a CLOSED line nor process exit can turn it into zero.

The TraceReader is an internal, JIT-only external analysis tool with a pinned
TraceEvent package and lockfile. Its runtime is separate from the actual JIT/AOT
application under observation. Set these variables on a diagnostic target to
capture actual events from startup:

```text
DOTNET_EnableEventPipe=1
DOTNET_EventPipeConfig=Microsoft-Windows-DotNETRuntime:0x3:5,NativeWeave.B22:0xffffffffffffffff:5
DOTNET_EventPipeOutputPath=/absolute/new/runtime.nettrace
DOTNET_EventPipeCircularMB=0x40
DOTNET_EventPipeOutputStreaming=1
DOTNET_EventPipeBufferingMode=0
NW_B22_HANDLE_CONTROL=1
NW_B22_HANDLE_CONTROL_OUTPUT=/absolute/new/known-handles.jsonl
```

The known-control window allocates and frees eight normal and four pinned
GCHandles. TraceReader requires the actual set/destroy events in that explicit
window, GCHeapStats totals, and zero trace loss. Slot reuse after the window is
recorded separately. Missing counters stay unavailable; a flat RSS graph is not
a root-count substitute. The runtime trace contains handle/object addresses and
is a diagnostic artifact, not default product telemetry.

## Scope and acceptance

The root-owned `ApiProbe` contributes the required assembled public-API cases.
Its exact accepted receipts can be reused; this controller does not rerun whole
historical component suites. B22 adds no public framework API and ships no new
product NuGet package. Internal helpers are documented here and in the guide;
their source and exact public-package consumers supply the applicable API/XML
and usability evidence.

A focused functional pass is not full B22 acceptance. The required30-minute,
24-hour and72-hour stages must each meet their frozen workload/resource profile
for the actual elapsed duration. No long stress runs may overlap B23 timed
performance measurements in the same resource environment.

### Implementation checkpoint

The external PostgreSQL, SQLite, outbox, attachment, orphan-GC, half-close and
HTTP/2-reset fixtures have runnable implementations. The ordinary immutable B21
JIT artifacts have exercised these fixtures; individual failed fixture attempts
remain in their evidence directories. Final execution receipts bind the
JIT/AOT fault matrix and the separate `transport_faults.py` cases. The staged
runner is documented in `soak-profile.md`; its required long stages remain
pending actual elapsed time.
The initial ProcessExit-only observation retained an application entry lifetime.
The narrow joined-entrypoint profile distinguishes pre-finalizer native owners
from post-Main full-GC retirement. Focused actual JIT/AOT SQLite faults and a
PostgreSQL pool-return fault reach zero for the six native counters after that
join and collection. This supports those specific lifetime rows, not the pending
long-duration plateau gate. Actual JIT and Native AOT EventPipe controls observe
all twelve known handle set/destroy pairs, GCHeapStats and zero lost events.

`soak.py probe` separately verifies its local observer-control and durable-oracle
path in eight seconds. That result is labelled `probe-passed`; it cannot create a
frozen calibration profile or satisfy any long-stage duration.

## Supplemental Linux descriptor ownership evidence

`descriptor_profile.py` supervises an unchanged PostgreSQL `soak.py run` stage
when a separately reviewed frozen profile contains an `fdThresholdSupplement`.
The supplement binds this monitor's exact SHA-256. It records live process
identity, descriptor counts, TCP endpoint roles, sampling races, inventories at
new maxima, and a fresh classified idle observation before process closure.
The supplemented result requires both the original stage receipt and the
monitor's `supplemented-stage-passed` receipt. A successful ordinary soak receipt
alone does not establish the supplemental owner checks.

The descriptor band is prospective and specific to the measured workload. It
does not change native connection limits or imply that client concurrency is a
universal bound on live server sockets. Earlier failed profiles and attempts
remain failed. PostgreSQL physical connections may persist during idle up to
the configured pool maximum; accepted HTTP sockets and open partial uploads
must have retired while the application is still alive.
