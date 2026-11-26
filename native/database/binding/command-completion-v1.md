# One-shot command completion extension v1

`command-completion-v1.json` defines capability bit 28
(`db.command.completion.v1`). Consumers resolve all four exports before selecting
this private path. The frozen database v1 declarations and fingerprint, existing
query command extension and cursor pull extension are unchanged.

## Typed request and results

The 48-byte Linux x64 request is an in-process typed record, not a bytecode or
command tape. It contains `struct_size=48`, request kind, target owner, optional
input owner, the existing payload byte view, action, and zero reserved. Only
the fields listed below may be nonzero; unused input, payload and action fields
are zero. Input bytes and owners are captured synchronously using the existing
source, parameter, model, plan and Session admission helpers.

| Kind | Target | Input | Payload | Action | Result kind |
| --- | --- | --- | --- | --- | --- |
| 1 Execute | Session | SQL or native plan | Existing parameter bytes | 0 | 100 count |
| 2 Mutation | Session | Model | Existing mutation payload | Original nonreturning mutation kind | 100 count |
| 3 Begin transaction | Session | 0 | Existing transaction options | 0 | 3 transaction owner |
| 4 Finish transaction | Transaction | 0 | Empty | 0 commit, 1 rollback | 101 snapshot buffer |
| 5 Begin savepoint | Transaction | 0 | Empty | 0 | 4 savepoint owner |
| 6 Savepoint action | Savepoint | 0 | Empty | 0 rollback, 1 release | 101 snapshot buffer |
| 7 Close child | Session, transaction or savepoint | 0 | Empty | 0 | 101 snapshot buffer |

Execute accepts only nonquery or scalar-count plans. Row-producing execution and
returning mutations use cursor pull and reject before command acceptance here.
Source open, Source close, administration and generic cursor cleanup remain on
their existing paths. This extension introduces no retry of SQL or mutations.

The 32-byte successful take record contains `struct_size=32`, result kind, count,
owner and buffer. Exactly the matching result field is set. `expected_kind` uses
the existing values 3, 4, 100 and 101. A mismatched kind does not consume a result.
Take before readiness returns `NW_PENDING`; repeated take returns
`NW_INVALID_STATE`. Every failed request clears its output owner; every failed
take clears the full result record. Returned owners and buffers use the existing
owner and buffer release functions.

## Acceptance and readiness

The callback record is the existing 24-byte `nw_db_cursor_callbacks_v1` record.
Its notification always uses generation **0**. The record address, exact size,
zero reserved and nonnull ready callback are checked before acceptance.
`NW_OK` from request means accepted and owes exactly one ready notification.
Every other status means rejected, a zero output owner and no notification.
An accepted queue failure becomes a ready error. The request owner is published
to the output slot before the worker can notify; readiness may precede request
return. The caller must retain its callback root through that setup overlap.

The native request stack transfers or destroys all temporary execution captures
before queueing. The worker uses an independent completion state rather than a
generic `DbOperation`. It finishes business work, freezes the value or original
error, detaches cancellation hooks, and releases its action, Session gate, plan,
business accessors and Source capacity reservation before notification. Result
owners may retain the parent resources required by their public ownership
contract. Those result-owned resources are not worker accessors.

The worker clears the saved callback record and invokes its detached local copy
once. After notification it accesses only independent completion tickets and
test-only local observers; it does not revisit the callback cookie or command
state. The callback must return normally without throwing across the ABI.
Managed business continuations remain asynchronous.

Readiness proves that this command's business worker has released its accessors;
it does not replace generic `NativeQuiet`. Transaction finish retains its
existing bounded primary result, including Unknown while independently owned
transaction cleanup continues. The new worker does not wait for that later
cleanup merely to publish the already determined business result.

## Errors, cancellation and cold retirement proof

A failed take returns the original status, domain, code and correlation without
consuming the rich error. `take_error` consumes it once into the existing
provider, cleanup and bulk error record. Cancellation requests do not replace an
already frozen provider error. Unknown native exceptions poison the Source;
allocation failure keeps its out-of-memory classification. Cancellation and
owner release never waive an accepted notification obligation.

The command is a private Owner variant. Existing snapshot and generic close can
prove an indeterminate request's retirement. Its public snapshot reports local
resources released only after the independent command notification fence is
complete. Generic close cancels if needed and joins this fence, releases any
unconsumed result, and returns a cleanup snapshot; it does not rethrow the
business result as a cleanup failure. A Session snapshot alone does not prove a
command callback has returned. When an indeterminate invocation has no usable
command handle, Source join is the parent proof.

Each accepted command also registers an independent Source command-worker
ticket. Source join waits those tickets after closing admission, and cannot
return while a notification tail is live. A control reservation refused because
Source admission has stopped rejects before adding a ticket; callers retain the
existing generic close fallback. Diagnostic index 9 counts command lifetimes,
including an independent notification tail after its public owner was released.

## Scalar count

The new scalar-count terminal reads through the provider's existing typed
conversion loop into a single eight-byte destination. It creates no row-batch
wire buffer and does not raise the stream batch limit. A legal maximum batch
row count of one remains valid. Complete column matching, nonnullable Int64
conversion, zero/two-row cardinality rejection and errors observed while reading
the terminal result retain their existing precedence. A second row receives
normal conversion checks before cardinality is rejected. The frozen generic
execute path is left unchanged for compatibility tests.
