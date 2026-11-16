"""D05/D06 setup, native quiet and independent managed retirement dimensions.

Native completion values and result transfer are refined in results.py. This
model retains distinct setup, registration, start, callback and call leases.
"""

from dataclasses import dataclass, replace
from checker import Model, Transition


@dataclass(frozen=True)
class State:
    setup: str = "unadmitted"
    registration: str = "unassigned"
    start: str = "not_called"
    budget: bool = False
    owner: bool = False
    cookie: bool = False
    accepted: bool = False
    work: bool = False
    callback: int = 0  # absent / queued / entered / notice published / exited
    cancel: int = 0  # absent / delegate entered / native call / returning / exited
    cancel_requested: bool = False
    control: int = 0  # optional call: unused / leased / accessed / returned
    task: bool = False
    closing: bool = False
    retired: bool = False
    reclaimed: bool = False
    quarantined: bool = False
    close_wait_canceled: bool = False
    free_count: int = 0
    invalid_access: bool = False
    synchronous_notice: bool = False
    synchronous_cancel: bool = False


def successors(state, omitted):
    s = state
    if s.quarantined:
        # The model deliberately has no fabricated successful cleanup edge.
        return
    if not s.closing:
        yield Transition("owner.close_admission_and_request_stop", "A07", replace(s, closing=True))
    if s.closing and not s.reclaimed and not s.close_wait_canceled:
        yield Transition("caller.cancel_close_wait_only", "A07", replace(s, close_wait_canceled=True))

    if s.setup == "unadmitted":
        yield Transition("setup.pre_admission_cancel_or_closed", "A07",
                         replace(s, setup="settled", registration="none", start="skipped", task=True))
        if not s.closing:
            yield Transition("setup.reserve_operation_and_cleanup_slot", "A07",
                             replace(s, setup="reserved", budget=True))
    elif s.setup == "reserved":
        yield Transition("create.success_one_owner", "A07", replace(s, setup="created", owner=True))
        yield Transition("create.failure_no_owner_and_return_budget", "A07",
                         replace(s, setup="settled", registration="none", start="skipped",
                                 task=True, budget=False))
    elif s.setup == "created":
        yield Transition("setup.initialize_context_and_cookie", "A07",
                         replace(s, setup="initialized", cookie=True))
        yield Transition("setup.allocation_failure_publish_never_started", "A07",
                         replace(s, setup="settled", registration="none", start="skipped", task=True))
    elif s.setup == "initialized":
        yield Transition("register.enter", "A07", replace(s, setup="registering", registration="installing"))
        yield Transition("register.explicit_no_registration", "A07",
                         replace(s, setup="registered", registration="none"))
    elif s.setup == "registering":
        yield Transition("register.return_and_publish_registered", "A07",
                         replace(s, setup="registered", registration="registered"))
        if s.cancel == 0:
            yield Transition("register.verified_failure_before_install", "A07",
                             replace(s, setup="settled", registration="none", start="skipped", task=True))
        yield Transition("register.indeterminate_retains_owners", "A07",
                         replace(s, setup="settled", registration="indeterminate", start="indeterminate",
                                 closing=True, quarantined=True))
    elif s.setup == "registered":
        if s.cancel_requested or s.closing:
            yield Transition("start.skip_with_explicit_sentinel", "A07",
                             replace(s, setup="settled", start="skipped", task=True))
        if not s.closing:
            yield Transition("start.enter_frame", "A07", replace(s, setup="starting"))
    elif s.setup == "starting":
        if not s.accepted:
            yield Transition("start.reject_without_native_obligations", "A07",
                             replace(s, setup="settled", start="rejected", task=True))
            yield Transition("start.commit_responsibility", "A07",
                             replace(s, accepted=True, work=True, start="accepted"))
        else:
            yield Transition("start.return_and_settle_setup", "A07",
                             replace(s, setup="settled", start="accepted_returned",
                                     invalid_access=s.invalid_access or not s.owner))

    if s.registration in ("installing", "registered") and s.cancel == 0:
        yield Transition("cancel.delegate_enters", "A07",
                         replace(s, cancel=1, cancel_requested=True,
                                 synchronous_cancel=s.synchronous_cancel or s.setup == "registering"))
    if s.cancel == 1:
        if not s.closing:
            yield Transition("cancel.acquire_control_lease", "A07", replace(s, cancel=2))
        else:
            yield Transition("cancel.closed_gate_skips_native_call", "A07", replace(s, cancel=3))
    if s.cancel == 2:
        yield Transition("cancel.native_request_returns_and_releases_lease", "A07",
                         replace(s, cancel=3, invalid_access=s.invalid_access or not s.owner))
    if s.cancel == 3:
        yield Transition("cancel.delegate_exits", "A07", replace(s, cancel=4))

    if s.cookie and not s.closing and s.control == 0:
        yield Transition("control.acquire_ordinary_lease", "A07", replace(s, control=1))
    if s.control == 1:
        yield Transition("control.access_native_owner", "A07",
                         replace(s, control=2, invalid_access=s.invalid_access or not s.owner))
    if s.control == 2:
        yield Transition("control.return_and_release_lease", "A07", replace(s, control=3))

    if s.accepted and s.work:
        yield Transition("native.finish_work_commit_terminal_and_queue_notice", "A07",
                         replace(s, work=False, callback=1))
    if s.callback == 1:
        yield Transition("callback.enter_under_native_lease", "A06",
                         replace(s, callback=2, invalid_access=s.invalid_access or not s.cookie))
    if s.callback == 2:
        yield Transition("callback.release_publish_terminal_latch", "A07",
                         replace(s, callback=3, synchronous_notice=s.synchronous_notice or s.setup == "starting"))
    if s.callback == 3:
        yield Transition("callback.exit_and_return_native_lease", "A06", replace(s, callback=4))
    if s.callback >= 3 and not s.task:
        yield Transition("coordinator.publish_task_outside_callback", "A07", replace(s, task=True))

    setup_barrier = s.setup == "settled" or ("setup" in omitted and s.setup in ("registered", "starting"))
    if s.closing and setup_barrier:
        if s.registration == "none":
            yield Transition("cleanup.no_registration_is_already_drained", "A07",
                             replace(s, registration="drained"))
        if s.registration == "registered":
            yield Transition("cleanup.external_registration_dispose_begins", "A07",
                             replace(s, registration="draining"))
        if s.registration == "draining" and s.cancel not in (1, 2, 3):
            yield Transition("cleanup.external_registration_drain_finishes", "A07",
                             replace(s, registration="drained"))

    if s.accepted and s.callback == 4 and not s.work and not s.retired:
        yield Transition("native.try_retire_closes_future_sources", "A06", replace(s, retired=True))

    allowed = s.closing and s.task and not s.reclaimed
    if "setup" not in omitted:
        allowed = allowed and s.setup == "settled"
    if "native" not in omitted:
        allowed = allowed and (s.retired or (not s.accepted and s.start in ("skipped", "rejected")))
    if "registration" not in omitted:
        allowed = allowed and s.registration == "drained"
    if "control" not in omitted:
        allowed = allowed and s.control not in (1, 2) and s.cancel != 2
    if allowed:
        yield Transition("cleanup.reclaim_owners_and_complete_cleanup_promise", "A07",
                         replace(s, reclaimed=True, owner=False, cookie=False, budget=False,
                                 free_count=s.free_count + int(s.owner)))


def violations(state):
    s = state
    if s.invalid_access:
        yield "access_after_free"
    if s.free_count > 1:
        yield "multiple_frees"
    if s.reclaimed:
        if s.setup != "settled":
            yield "reclaimed_before_start_return"
        if s.control in (1, 2) or s.cancel == 2:
            yield "reclaimed_with_control_call_in_flight"
        if s.registration != "drained":
            yield "cleanup_success_without_managed_registration_drain"
        if s.accepted and not s.retired:
            yield "reclaimed_without_native_quiet"
        if s.budget or s.owner or s.cookie:
            yield "reclaimed_with_retained_internal_owner"
    if s.quarantined and (s.reclaimed or not s.budget or not s.owner or not s.cookie):
        yield "indeterminate_setup_lost_ownership"
    if s.retired and (s.callback != 4 or s.work):
        yield "retired_with_callback_source_or_work"


def milestones(state):
    s = state
    if s.synchronous_notice:
        yield "terminal_notice_before_start_returns"
    if s.synchronous_cancel:
        yield "cancellation_inside_registration"
    if s.reclaimed and s.start == "skipped":
        yield "never_started_cleanup_finishes"
    if s.reclaimed and s.start == "rejected":
        yield "rejected_start_cleanup_finishes"
    if s.reclaimed and s.accepted:
        yield "accepted_operation_reclaimed"
    if s.reclaimed and s.close_wait_canceled and s.accepted:
        yield "cleanup_continues_after_close_wait_cancellation"
    if s.quarantined:
        yield "indeterminate_registration_isolated_with_owners"
    if s.task and s.setup == "starting":
        yield "task_visible_while_start_frame_is_live"
    if s.retired and s.control in (1, 2):
        yield "native_quiet_does_not_imply_managed_drain"
    if s.registration == "draining" and s.cancel in (1, 2, 3):
        yield "external_drain_waits_for_cancel_delegate"


def model(omitted=""):
    expected = {"setup": "reclaimed_before_start_return",
                "native": "reclaimed_without_native_quiet",
                "registration": "cleanup_success_without_managed_registration_drain",
                "control": "reclaimed_with_control_call_in_flight"}.get(omitted, "")
    # Omitting setup skips that barrier both before registration drain and
    # before reclamation; every independent registration/control guard remains.
    omitted_guards = {omitted} if omitted else set()
    required = (
        "terminal_notice_before_start_returns", "cancellation_inside_registration",
        "never_started_cleanup_finishes", "rejected_start_cleanup_finishes",
        "accepted_operation_reclaimed", "cleanup_continues_after_close_wait_cancellation",
        "indeterminate_registration_isolated_with_owners",
        "task_visible_while_start_frame_is_live", "native_quiet_does_not_imply_managed_drain",
        "external_drain_waits_for_cancel_delegate")
    return Model(
        "retirement/" + ("omit_" + omitted if omitted else "candidate"), State(),
        lambda state: successors(state, omitted_guards), violations,
        lambda state: state.reclaimed or state.quarantined, milestones, required,
        {"operations": 1, "native_terminal_callbacks": 1, "cancellation_invocations": 1,
         "ordinary_control_calls": 1, "reserved_cleanup_slots": 1},
        ("Atomic sequentially consistent protocol actions; no weak-memory/GC/FFI execution.",
         "Every accepted finite native work item and admitted callback/control/start frame eventually exits.",
         "External cancellation disposal and the reserved coordinator eventually run; no permanent executor loss.",
         "Registration/start ambiguity reaches retained quarantine, never fabricated cleanup success.",
         "Idle stuttering is excluded; fairness permits each finite enabled actor to make progress."),
        expected)
