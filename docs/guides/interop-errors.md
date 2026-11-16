# Native failures and panic containment

A05 implements the fixed D08 error boundary and its exact managed diagnostics.
The canonical wire status and 24-byte error record are imported from A01, not
redeclared. The error and negotiation sources compose into one `NativeWeave.Core`
assembly. This component starts no executor, database pool or server.

## Application use

Catch ordinary argument, disposal, cancellation and timeout failures using the
corresponding .NET exception category. Database and server adapters own richer
domain exceptions and remote-effect state. A timeout or canceled managed task
never proves rollback or native retirement.

`NativeEngineException` preserves engine, operation, unsigned status/domain,
signed native code and correlation for a complete native record. Unknown wire
failures remain failures. Its three standard constructors have null native
metadata. `EngineLoadException` records engine/`load`, failure classification and
requested RID while all native-record fields stay null.

The [compiled example](../../samples/api/interop-errors/Program.cs) shows minimal
structured inspection and advanced preflight classification. It constructs
diagnostics to demonstrate these types; the separate component verifier executes
the actual Rust boundary from both JIT and Native AOT consumers. There is no
automatic retry, fallback provider, serialization of `Exception`, or universal
managed `Result` layer.

See the [generated API reference](../../src/Interop.Errors/api-reference.md) for
the exact 26 public symbols, defaults, nullable fields and XML parameter contract.
It is generated from the product assembly and compared with the pinned Design
inventory. Public values are immutable; reading them retains no native resource.
Exception creation allocates on the cold failure path. Successful native calls
do not construct an exception or diagnostic string.

## Native adapter integration

Embed one `EngineBoundary` in each independently owned native engine. Run ordinary
entry bodies through `business`, and guard asynchronous work at its actual
execution/poll boundary too. The helper requires `UnwindSafe`; it does not assert
that arbitrary partially mutated engine state is safe. An ordinary Result error
preserves admission. A caught panic or contract violation permanently closes new
business admission. Already admitted calls retain their independent lifetime.

`cleanup` permits explicitly trusted stop/retire/release controls after poison.
The adapter must establish that the fields it touches are still safe. Returning
from a cleanup closure is not a generic proof of callback quiescence, transaction
recovery, worker join or owner retirement. Those remain the owning module's
obligations. Native modules remain resident for process lifetime.

`create_owner` initializes a valid output slot to null before fallible work and
transfers a boxed owner only on success. Ordinary partial temporaries unwind
inside Rust. Matching adapter release must run once, after admitted users finish.
Arbitrary pointer validity cannot be checked by this API.

## Fixed and variable diagnostics

| Call outcome / declared capacity | Observable behavior |
| --- | --- |
| Null error output | Original status is returned; memory is untouched |
| OK or PENDING | No diagnostic bytes are written |
| Failure with capacity 0–23 | Only the readable four-byte header may be inspected; no partial record is written |
| Failure with capacity at least 24 | Exactly 24 bytes are written; output size is 24, flags are zero, caller tail is unchanged |

The caller initializes its entire valid range and supplies a truthful capacity.
Even capacities 0–3 require a valid readable four-byte header. The optional
diagnostic capacity never replaces the original business status with
`BUFFER_TOO_SMALL`. Unknown nonzero status values remain representable.

`BoundedDetail` copies explicitly redacted public text into at most 4,096 owned
UTF-8 bytes, truncating only at code-point boundaries and reporting truncation.
It has no implicit NUL terminator, retains no input borrow and performs no heap
allocation. The fixed status path does not depend on this detail being available.
The boundary never formats unknown panic payloads, connection strings, SQL or
arbitrary user data. Domain adapters must select bounded safe text before calling
the detail helper; it is not a general-purpose secret detector.

## Managed callbacks and committed outcomes

Only valid static nongeneric unmanaged entry points with exact blittable
signatures may be called. The internal preallocated `CallbackFailureLatch`
contains ordinary bridge failures, records a fault and always signals the reserved
cleanup path. It preserves the original native terminal status. A notification
failure does not undo an existing success, cancellation or remote commit, and it
does not remove the obligation to take/abandon, drain and release owned results.
A06/A07 supply the actual callback/operation lifetime coordinator.

The synchronous component fixture exercises a throwing managed bridge across a
real unmanaged callback and releases its GCHandle in `finally`. No exception
object crosses the C ABI. Managed public error construction occurs outside the
unmanaged callback.

## Fatal profiles and quarantine

The default workspace profile is `panic=unwind`. Caught unknown payloads are
deliberately forgotten because their destructor may panic. The exact object count
is recorded; bytes reachable from such payloads are unknown. Multiple admitted
calls may panic independently. Engine admission bounds and the host restart
policy must account for this exceptional retention. It is not leak-free recovery
from unlimited panics.

`panic=abort`, a panicking hook, double panic during destruction, illegal unwind
across `extern C`, invalid unmanaged signatures, and process faults can terminate
the process. An invalid non-blittable UCO signature can fail before its managed
method body executes; an inner `catch` cannot repair it. Tests run these cases in
separate child processes and retain actual exit codes and output.

## Verify on Linux x64

```sh
python3 tests/Interop.Errors/verify.py
```

The runner executes native boundary/property tests, a real C#→Rust fixture under
JIT and Native AOT, normal and poisoned admission, explicit cleanup, callback
failures, all status/capacity combinations, UTF-8 truncation, and fatal subprocess
profiles. It records compiler/SDK identities, source and artifact hashes, command
outputs and failed attempts under `artifacts/interop-errors/`.

For an existing offline SDK package feed, `--nuget-config <file> --offline` changes
only that local restore invocation. It does not refresh vulnerability data or
weaken repository/CI audit settings. The runner uses ordinary supported local
MSBuild compilation without a compiler server when IPC is unavailable.

This scope is Linux x64 component verification. It does not claim Windows
acceptance, A06/A07 full lifetime conformance, a database/server host, a pristine
machine, or new public package publication. Final Core distribution and complete
engine assembly retain their downstream owners.
