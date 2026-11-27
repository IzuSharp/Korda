# Admitted cursor pull execution extension v1

`cursor-pull-execution-v1.json` defines capability bit 30
(`db.cursor.pull.execution.v1`). Consumers resolve all three exports and the
existing cursor pull extension before selecting this private path. The frozen
database v1 declarations, records and fingerprint, existing cursor pull request
semantics and public query contracts remain unchanged.

## Admission and execution

Prepare accepts the same callback record, generation and batch limits as cursor
pull request. It performs the original validation and captures the existing
Cancel, Source pending reservation and independent channel/Source retirement
tickets before acceptance. It performs no provider I/O and posts no native
worker. `NW_OK` means one accepted generation and exactly one owed ready
notification. A rejected prepare owes no notification. Concurrent close can
settle accepted work and invoke its callback before prepare returns, so callers
retain their callback root through that setup overlap.

The accepted scheduling obligation retains the original native Worker. The
caller retains its channel owner while scheduling and either runs or abandons
that generation. A temporary managed queue refusal may retry scheduling under
the existing bounded coordinator contract. Source, Session and owner close can
independently settle work that never reaches a managed executor; they do not
depend on eventual queue acceptance to release its reservation and tickets.

Run and abandon require the exact prepared generation. Under the channel lock,
exactly one of these entries claims the scheduling obligation. Duplicate claims,
old generations and generations admitted with the existing request entry are
rejected. Run executes the original CursorPull algorithm synchronously on the
calling worker: admission and metadata checks, provider creation and fetch,
cancellation detach, error enrichment and completion have one implementation.
The original request entry continues to dispatch that algorithm through its
native queue.

Abandon marks the admitted cancellation token under the same lock and enters the
original admission/completion path. It performs no new provider execution.
Existing cursor or gate cleanup still follows the original ownership and error
rules. Source/Session/owner close performs this same settlement when it extracts
unclaimed work. Cancellation does not overwrite an already frozen error.

## Ready, result and retirement

`NW_OK` from run or abandon describes successful execution of the control entry;
it does not mean the query succeeded. The existing ready notification and
one-time take/take-error entries deliver the batch or original failure.

After close automatically extracts unclaimed work, the first late run or abandon
for that exact generation returns `NW_OK` without repeating execution or
notification. A second claim is rejected. The close thread may still be
publishing the original callback when that late no-op returns. Consumers must
observe readiness and callback drain before taking or publishing the result;
the return of a late claim alone proves neither readiness nor retirement.

The exported execution entries move their temporary channel accessor into the
execution helper and release it before running the Worker. The Worker is the
last owner-bearing local. Its independent retirement tickets cover all native
execution accessors, callback execution and their release, including the Source
reservation. Source join cannot pass those tickets while the synchronous
execution or automatic settlement tail still holds its accessors. A ready batch
may be available earlier; final close still requires the original retirement
proof. Automatic settlement invokes no join on its own ticket.

Managed business continuations remain asynchronous. The managed executor may
take a ready result on the same work item only after synchronous native run has
returned and the original setup, controls, cancellation and callback drain
conditions hold. No borrowed OwnerLease or native pointer crosses that
synchronous execution scope.
