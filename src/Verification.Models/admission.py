"""D04 logical admission and exception compensation, with two bounded callers."""

from dataclasses import dataclass, replace
from checker import Model, Transition


@dataclass(frozen=True)
class State:
    closing: bool = False
    disposed_twice: bool = False
    references: int = 1
    frees: int = 0
    phases: tuple = ("new", "new")
    leases: tuple = (False, False)
    admitted_after_close: bool = False
    completed_after_close: bool = False
    exception_rolled_back: bool = False
    addref_rejected: bool = False


def _call(state, index, phase, lease, **changes):
    phases, leases = list(state.phases), list(state.leases)
    phases[index], leases[index] = phase, lease
    return replace(state, phases=tuple(phases), leases=tuple(leases), **changes)


def successors(state, mutation):
    if not state.closing:
        count = state.references - 1
        yield Transition("owner.close_and_drop_reference", "A03",
                         replace(state, closing=True, references=count,
                                 frees=state.frees + int(count == 0)))
    elif not state.disposed_twice:
        yield Transition("owner.dispose_again_noop", "A03", replace(state, disposed_twice=True))
    for index, phase in enumerate(state.phases):
        prefix = "call" + str(index) + "."
        if phase == "new":
            if state.closing:
                yield Transition(prefix + "reject_closed", "A03",
                                 _call(state, index, "denied", False))
            elif mutation == "split_gate":
                yield Transition(prefix + "read_open_without_lease", "A03",
                                 _call(state, index, "checked", False))
            else:
                yield Transition(prefix + "atomic_open_and_addref", "A03",
                                 _call(state, index, "leased", True,
                                       references=state.references + 1))
                yield Transition(prefix + "addref_fails_without_reference", "A03",
                                 _call(state, index, "failed", False, addref_rejected=True))
        if phase == "checked":
            if state.references > 0:
                yield Transition(prefix + "addref_after_separate_check", "A03",
                                 _call(state, index, "leased", True,
                                       references=state.references + 1,
                                       admitted_after_close=state.admitted_after_close or state.closing))
            else:
                yield Transition(prefix + "safehandle_rejects_closed", "A03",
                                 _call(state, index, "denied", False))
        if phase == "leased":
            yield Transition(prefix + "ffi_access_under_lease", "A03",
                             _call(state, index, "in_call", True))
            count = state.references - 1
            if mutation == "missing_rollback":
                failed = _call(state, index, "failed", True)
            else:
                failed = _call(state, index, "failed", False, references=count,
                               frees=state.frees + int(count == 0), exception_rolled_back=True)
            yield Transition(prefix + "managed_exception_finally", "A03", failed)
        if phase == "in_call":
            count = state.references - 1
            yield Transition(prefix + "ffi_return_and_finally_release", "A03",
                             _call(state, index, "done", False, references=count,
                                   frees=state.frees + int(count == 0),
                                   completed_after_close=state.completed_after_close or state.closing))


def violations(state):
    if state.admitted_after_close:
        yield "business_admission_after_close"
    if state.references != int(not state.closing) + sum(state.leases):
        yield "unbalanced_owner_or_lease_count"
    if state.frees > 1:
        yield "multiple_frees"
    if state.frees and (state.references or any(state.leases)):
        yield "free_with_live_lease"
    if any(phase == "failed" and lease for phase, lease in zip(state.phases, state.leases)):
        yield "lease_leak_on_exception"


def milestones(state):
    if state.completed_after_close:
        yield "old_call_finishes_after_close"
    if state.exception_rolled_back:
        yield "acquired_reference_compensated_on_exception"
    if state.addref_rejected:
        yield "addref_failure_transfers_no_reference"
    if "denied" in state.phases:
        yield "new_call_rejected_after_close"
    if state.frees == 1 and "done" in state.phases:
        yield "usable_result_then_exactly_one_release"
    if state.disposed_twice:
        yield "repeated_dispose_is_idempotent"


def model(mutation=""):
    expected = {"split_gate": "business_admission_after_close",
                "missing_rollback": "lease_leak_on_exception"}.get(mutation, "")
    return Model(
        "admission/" + (mutation or "candidate"), State(),
        lambda state: successors(state, mutation), violations,
        lambda state: state.frees == 1 and all(p in ("done", "failed", "denied") for p in state.phases),
        milestones,
        ("old_call_finishes_after_close", "acquired_reference_compensated_on_exception",
         "addref_failure_transfers_no_reference", "new_call_rejected_after_close",
         "usable_result_then_exactly_one_release", "repeated_dispose_is_idempotent"),
        {"owners": 1, "callers": 2, "maximum_references": 3, "dispose_requests": 2},
        ("Open check and AddRef form one short atomic gate in the candidate.",
         "Each admitted finite call or injected failure eventually returns; finally runs.",
         "Dispose and each modeled caller eventually receive execution; idle stuttering is excluded.",
         "Sequentially consistent reference-count abstraction; not actual SafeHandle/GC verification."),
        expected)
