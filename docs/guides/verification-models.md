# Executable lifetime and asynchronous models

A19 supplies executable finite models and replayable counterexamples for the
accepted ownership and async protocol. They are **maintainer verification tools**.
They do not implement the future C#/Rust runtime or establish that its atomics,
GC handles, native callbacks or Native AOT consumers are correct.

The inputs are Plan task A19 revision 0.4.0 and Design commit
`c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9`. The exact 24 input hashes are in
[design-inputs.json](../../src/Verification.Models/design-inputs.json). This
includes D04/D05/D06/D12, the referenced D09 mailbox rules, the Interop public
owner semantics and the original research model sources. Original research
results are not copied as new product evidence.

## Run the complete Linux acceptance scope

Python 3.9 or newer is sufficient; there are no external Python dependencies.
Run from the implementation repository root, using the actual path to the
Plan-locked Design checkout:

```sh
python3 tests/Verification.Models/run.py \
  --design-root ../NativeWeave-Design \
  --output-dir artifacts/A19/linux-1
```

This runs the checker regressions, both runnable examples, and all candidate and
negative model cases. It verifies Design HEAD and every pinned input byte hash.
`run.json` records commands, platform, UTC times, exits and raw stdout/stderr
hashes. `models/summary.json` records model bounds, Python/checker identity, the
implementation commit and an exact ordered source manifest. Individual JSON
reports retain complete shortest counterexample traces and non-vacuity witnesses.
Uncommitted source is marked explicitly and hashed; final acceptance should use
the reviewed committed source. A nonempty evidence directory is rejected so an
earlier failure or successful run cannot be silently overwritten.

For a quick individual run or just the regression suite:

```sh
python3 src/Verification.Models/verify.py \
  --model mailbox/candidate \
  --output-dir artifacts/A19/mailbox-example
python3 -m unittest discover -s tests/Verification.Models -p 'test_*.py' -v
```

The individual command without `--design-root` explicitly records that locked
inputs were not revalidated locally. It is useful for development; it does not
replace the full acceptance command. A selected subset is recorded as
`complete_suite: false`.

Exit `0` means the selected candidate oracles and specifically named negative
controls passed. Exit `1` means an invariant, success witness, input identity or
other required oracle failed. Exit `2` means exploration was **inconclusive**
because of a state/time limit or an unresolved progress cycle. A partial search
never becomes a passed candidate.

## Model boundaries and observable states

Every action is a sequentially consistent atomic step. The checker enumerates
the complete reachable state graph within the declared bounds, with no PRNG and
therefore no random seed. The bounds are intentionally small and explicit.

| Family | Finite bound | Required observations | Future code owners |
| --- | --- | --- | --- |
| Logical admission | One owner, two callers, at most three references, two Dispose requests | Open check and AddRef are atomic; calls admitted before close complete; calls arriving after close are rejected; acquired leases roll back on exceptions; final free occurs once | A03; NativeBuffer composition A04 |
| Setup and retirement | One operation, one native terminal callback, one cancellation invocation and one ordinary control call | Create failure transfers no owner; allocation/registration/start failure sentinels; synchronous registration and terminal callback; native quiet and managed drain remain separate; canceled close waiting retains cleanup; indeterminate setup retains quarantine | A06/A07 |
| Terminal and result ownership | One operation/result, five terminal outcomes, two terminal/notice/take attempts | Cancel requests never replace the native result; copy failure publishes a failure latch; native effect remains visible; Ready→Taken occurs once; take after retirement is legal; conversion failure releases the taken owner; operation release preserves an independently owned result | A04/A07 |
| Subscription dispatcher | One subscription, two events, one pending slot, one active handler and one native callback | Dispose discards pending copies, joins active handlers and rejects handler self-wait; full queue has explicit rejection; native quiet alone does not imply dispatcher drain | A06 |
| Coalesced mailbox | One reserved slot, one worker, three notifications | A notification during drain cannot be lost by unscheduling; idempotent status updates coalesce without allocating new slots | A07/A08 |

The admission model covers the Open/Closing/Released policy shared by synchronous
owners and `NativeBuffer` read/copy leases. The retirement and result models
cover the visible Task, cancellation, CloseAsync and successful DisposeAsync
policies of typed domain operations. The dispatcher covers the independent
subscription handler drain. Immutable catalog/descriptor metadata has no native
owner lifecycle to model. Parent-child engine composition, actual buffer contents
and domain-specific owner graphs remain with their owning implementation tasks.

The families are separate abstractions. In the result model, native retirement
imports the quiet predicate checked by the retirement model. The dispatcher
likewise separates managed handler ownership from native callback counts. These
facts and their later concrete refinement must be checked when A06/A07/A08 are
assembled; independent model success is not a proof of every cross-model
composition.

## Bounded progress and fairness

Safety and progress are checked separately. A safety check can reject a bad
state without proving that a good state will ever be reached. Every candidate
also requires explicit usable-result/cleanup witnesses so an over-restricted
model cannot pass by preventing all work.

For progress, the checker starts from all declared goal states and repeatedly
includes a state only when **all** of its outgoing successors already have a
finite route bound to a goal. The report includes the maximum number of
non-stuttering abstract actions. This universal check detects a deadlock even
when another branch reaches cleanup. A cycle with an available exit remains
inconclusive; the checker does not infer fairness merely from the exit's
existence.

The bounded fixtures assume continued execution of enabled actors: accepted
finite native work, admitted callbacks, control/start frames and managed handlers
eventually return, and the reserved coordinator and external cancellation drain
eventually run. Idle stuttering is excluded. The result fixture also explicitly
assumes that the application eventually disposes its returned owner. There is no
claim that discarding a Task result guarantees deterministic disposal. The
mailbox producer emits exactly its declared finite notification count; no later
notification or unrelated periodic scanner is allowed to rescue a lost wake.

An indeterminate registration path has a separate **retained quarantine** goal.
It records a diagnosed inability to prove cleanup and preserves owner/cookie/
budget; it is never a successful DisposeAsync state. A permanently stalled
native executor or handler is outside the finite-progress premise and cannot
justify free, successful close, or an unbounded liveness claim. State and time
exhaustion are recorded as inconclusive even if no invariant has failed yet.

## Unsafe controls and evidence replay

There are eleven deliberately incorrect controls:

- Split Open-check/AddRef admits a new business call after close while an old
  lease keeps the physical handle alive.
- Missing exception compensation leaks an acquired reference.
- Omitting setup, native quiet, control-call or external registration barriers
  violates the corresponding reclamation or cleanup contract.
- Treating a cancellation request as terminal overwrites native success.
- Returning a result owner on a second take duplicates ownership.
- Freeing a transferred result during operation release invalidates the caller's
  independent owner.
- Completing subscription disposal before its handler ends violates managed
  drain.
- Reading dirty and clearing scheduled in separate steps loses a wake.

Each unsafe case succeeds as a test only if it produces its **specific expected
violation**. The underlying result remains `status: violated`, with its exact
trace preserved. Missing registration drain is labeled a DisposeAsync cleanup
contract failure; it is not falsely called a native use-after-free when the
remaining lease barriers still protect native memory.

The legacy mailbox trace contains `consume_current_state`,
`read_dirty_before_unschedule`, a producer notification, and
`unschedule_from_stale_snapshot`. The final state has dirty work, an idle worker
and no scheduled slot. The corrected action atomically rechecks dirty and either
continues draining or unschedules. The
[advanced example](../../samples/api/verification-models/advanced.py) records and
replays this failure after JSON serialization. Tampering with an action, owner
or state rejects replay.

## Exact transition and future implementation binding

[transition-map.json](../../src/Verification.Models/transition-map.json) maps all
88 candidate actions to their current model source/symbol, normative Design
section, concrete synchronization obligation and existing implementation owner.
Each trace also records the owning A/B task. A19 introduces no placeholder
runtime implementation in A03/A06/A07/A08's file scopes.

When the owners implement those algorithms, A20 can supply `--refinement` and
`--implementation-root` to the model runner. The refinement manifest contains a
full implementation `commit` and one `transitions` record for every action in
`refinement_boundaries`. Each record requires:

| Field | Required value |
| --- | --- |
| `action` | Exact action identity from the transition map |
| `path`, `sha256`, `symbol` | Tracked implementation source, exact SHA-256 bytes and actual symbol |
| `synchronization` | Actual lock/CAS and acquire/release relation implementing the abstract step |
| `test_path`, `test_sha256`, `test_symbol` | Exact committed implementation-refinement test source and symbol |

The implementation checkout must be at that full commit and both working and
committed file bytes must match. Missing/duplicate actions, stale source and
escaped paths fail validation. This validation proves **source identity only**;
the corresponding A20 execution and independent review remain separate evidence.
Current A19 reports explicitly use `refinement.status: not_yet_bound` because
those runtime tasks are outside Task-01. No unimplemented algorithm is cited as
already verified.

## API, documentation, platform and cost applicability

A19 exposes no C# public/protected symbols, managed assembly, native ABI, Engine
or package. Therefore XML assembly documentation, generated application API
Reference, binary compatibility and NuGet/Native AOT consumer checks are not
applicable to this task's `model_check` matrix. They are not silently deferred
requirements for a new API. Python docstrings, this guide, the transition catalog
and runnable [minimal/advanced examples](../../samples/api/verification-models/)
document the actual delivered tool. Existing public C# signatures and ABI records
are unchanged by A19.

The checker uses O(V + E) memory and time for its bounded graph, plus serialized
witness traces. It allocates Python state objects and adjacency lists; these are
maintainer-tool costs, not application hot-path measurements. No FFI, pin,
native-runtime startup or domain allocation is performed. The state/time limits
control verification work and do not claim a product RSS bound.

This round records **Linux x64** execution only. Windows execution remains a
separate platform obligation; no extra CI matrix is introduced. The models do
not establish Rust/CLR weak-memory safety, actual GCHandle/SafeHandle behavior,
all operation counts, actual network/database effects, Native AOT correctness,
real load/resource performance or pristine package consumption. Those retain the
existing task owners and acceptance scopes.
