# Cursor pull extension v1

`cursor-pull-v1.json` defines capability bit 27 (`db.cursor.pull.v1`). It replaces
the unpublished result-batch extension. The frozen 42 database declarations,
their fingerprint, the bit-26 complete-command extension and all NWDB reserved
bits are unchanged. Both independent provider profiles expose this same extension.

## Admission and requests

The ordinary, relation and mutation creation entries return a private channel
owner. They reuse the original execution-plan compilation, capture, capability,
source and Session admission helpers. Only a reader result is accepted. Creation
retains independent typed values and the admitted Session gate without opening,
stepping or executing a database statement. Relation and returning-mutation
deadlines still include input capture, delayed first request and worker queue
time. Ordinary SQL retains its execution-time timeout origin.

The first request opens the reader using the existing complete-column validation
and then checks Source, Session, cancellation and stop again before the first
fetch. Later requests fetch directly. PostgreSQL still separates preparation and
description from Bind/Execute; SQLite still prepares and binds before step.

Generations start at 1 and increase by exactly one. At most one accepted request
or unconsumed result exists. A live transferred Buffer still blocks the next
request. Bounds, callback record, generation, Source and Session admission are
validated before acceptance. Every accepted request reserves the existing Source
capacity until its independent worker tail is gone. `NW_OK` means accepted and
requires exactly one ready notification, including when queue submission fails.
Any other request status means rejected and creates no callback obligation.

The 24-byte Linux x64 callback record contains `struct_size=24`, zero `reserved`,
`context` at byte 8 and a Cdecl `ready(context, generation)` function at byte 16.
The callback only announces readiness; it returns no result, borrowed pointer or
retirement proof. It must return normally and must not throw across the ABI.
Before notification, all provider accesses and read guards have finished. The
worker stores the result under the channel mutex, detaches and clears this
generation's callback record, then invokes it exactly once. The worker never
accesses that context or changes channel state after invocation.

Readiness can precede the end of independent native worker/callback tails.
Taking a result and requesting the next generation does not wait for old tails.
Final close joins all such tails. This private readiness boundary does not alter
generic async `NativeQuiet` or operation retirement.

## Results and errors

`take` requires the exact outstanding generation. Before readiness it returns
`NW_PENDING`; a stale or consumed generation returns `NW_INVALID_STATE`. Success
transfers one native Buffer and its borrowed view in the 32-byte output record:
owner at byte 0, view at 8, observed EOF at 24, zero reserved at 28. Existing
`nw_db_buffer_release_v1` releases the transferred owner. The view remains valid
only while that owner is retained. No copied managed byte array is implied.

An error result returns its original status/domain/code/correlation from `take`
without consuming the rich error. `take_error` then consumes the matching error
once into the existing cleanup/provider/bulk snapshot Buffer. Failure never
transfers a partial row batch. Unknown native exceptions poison the Source;
allocation errors retain their out-of-memory classification.

An accepted request still owes its notification if queueing or cleanup throws.
The original error remains primary and a cleanup failure is added to its rich
error record. Cleanup side effects finish or are contained before readiness is
published. An unconfirmed cursor cleanup poisons the Source, excludes its
connection from reuse and retains the bounded accessor; its close and snapshot
report failure with `released=false`. A failed cleanup queue cannot escape a
read-guard destructor or strand an accepted callback. Failed parent cleanup
launches keep their existing compensation and retry semantics.

Observed EOF is attached only when the driver has actually returned its terminal
result. No speculative extra row is read to set it. After releasing the final
batch, `check_read` preserves Source poison, Session closure, channel stop and
active/live-batch admission errors before a managed reader returns false. It
performs no SQL, protocol send or generic operation allocation. Transaction
disposal waiting for an existing reader does not itself forbid that reader from
finishing.

## Stop, abandonment and final close

`stop` closes read admission and requests cancellation/cleanup without creating
an operation. It is valid while idle or while a request is running. It does not
revoke an accepted callback obligation or certify retirement.

The returned channel supports the frozen `nw_db_close_create_v1`,
`nw_db_snapshot_v1` and `nw_db_owner_release_v1` entries. There is at most one
generic close operation per normal managed cursor. Close first stops admission,
joins all request tickets, releases unconsumed buffers, and joins the actual
cursor/provider cleanup. A channel snapshot reports released only when both
its cursor receipt and independent worker ledger are complete.

Owner release is nonblocking. Dropping an idle channel releases its admission;
dropping an active channel requests stop while the worker retains cleanup.
Session closure includes its weakly tracked lazy channels. Source closure waits
the independent channel worker ledger before stopping its pools, after those
workers have released every Source and channel capture.

`nw_db_diagnostic_count_v1(8)` reports live channel objects. Existing owner counts
0 through 5 and SQL/protocol counters 6 and 7 keep their meanings. Channels also
appear in the existing handle table count. These diagnostics do not contribute
timing evidence.

The retained native streaming and actual C ABI tests cover generation rejection,
one-shot result/error transfer, lazy execution, the second first-fetch admission,
held-batch ownership, EOF admission, transaction disposal, abandonment and a
deterministic old-worker tail barrier. Measurements and independent package
acceptance belong to the matching Plan candidate receipts.
