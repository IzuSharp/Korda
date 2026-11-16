"""D06 sole terminal authority and the independent, once-only result owner."""

from dataclasses import dataclass, replace
from checker import Model, Transition


@dataclass(frozen=True)
class State:
    native: str = "running"
    outcome: str = "pending"
    commits: int = 0
    canceled: bool = False
    duplicate_terminal: bool = False
    notice: str = "absent"
    binding_failure: bool = False
    duplicate_notice: bool = False
    poisoned: bool = False
    owner: str = "none"
    wrapper: bool = False
    take_count: int = 0
    duplicate_take: bool = False
    processed: bool = False
    task: str = "pending"
    result_frees: int = 0
    invalidated_result: bool = False
    took_after_retire: bool = False
    survived_operation_release: bool = False
    conversion_failed: bool = False
    untaken_released: bool = False


def successors(state, mutation):
    s = state
    if not s.canceled:
        task = "canceled" if mutation == "cancel_overwrites_terminal" and s.task == "succeeded" else s.task
        yield Transition("caller.request_cancellation_only", "A07", replace(s, canceled=True, task=task))
    if s.outcome == "pending":
        for outcome in ("success", "canceled", "faulted", "timeout", "unknown_effect"):
            yield Transition("native.commit_" + outcome, "A07",
                             replace(s, native="terminal", outcome=outcome, commits=1,
                                     owner="native" if outcome == "success" else "none"))
    if s.outcome != "pending" and not s.duplicate_terminal:
        yield Transition("native.reject_second_terminal_cas", "A07",
                         replace(s, duplicate_terminal=True, poisoned=True))
    if s.outcome != "pending" and s.notice == "absent":
        yield Transition("callback.claim_terminal_latch", "A07", replace(s, notice="claimed"))
    if s.notice == "claimed":
        yield Transition("callback.copy_and_release_publish_latch", "A07", replace(s, notice="received"))
        yield Transition("callback.copy_failure_latched_in_finally", "A07",
                         replace(s, notice="received", binding_failure=True))
    if s.notice == "received" and not s.duplicate_notice:
        yield Transition("callback.duplicate_notice_does_not_overwrite_latch", "A07",
                         replace(s, duplicate_notice=True, poisoned=True))
    if s.native == "terminal" and s.notice == "received":
        # Retirement.py separately establishes all native/managed call barriers.
        yield Transition("native.retire_after_callback_and_work_exit", "A07", replace(s, native="retired"))
    if s.notice == "received" and not s.processed:
        if s.binding_failure:
            yield Transition("coordinator.publish_binding_fault_preserving_native_outcome", "A07",
                             replace(s, processed=True, task="faulted"))
        elif s.outcome != "success":
            task = "canceled" if s.outcome == "canceled" else "timeout" if s.outcome == "timeout" else "faulted"
            yield Transition("coordinator.publish_native_failure_outcome", "A07", replace(s, processed=True, task=task))
        elif not s.wrapper:
            yield Transition("coordinator.prepare_result_wrapper_before_take", "A07", replace(s, wrapper=True))
            yield Transition("coordinator.wrapper_allocation_failure_keeps_native_owner", "A07",
                             replace(s, processed=True, task="faulted"))
        elif s.owner == "native":
            yield Transition("take.failure_keeps_owner_and_empty_output", "A04",
                             replace(s, processed=True, task="faulted"))
            yield Transition("take.atomic_ready_to_taken_transfer", "A04",
                             replace(s, owner="managed", take_count=s.take_count + 1,
                                     took_after_retire=s.native == "retired"))
        elif s.owner == "managed":
            yield Transition("materializer.publish_usable_result", "A07", replace(s, processed=True, task="succeeded"))
            yield Transition("materializer.failure_disposes_taken_owner", "A04",
                             replace(s, processed=True, task="faulted", owner="freed",
                                     result_frees=s.result_frees + 1, conversion_failed=True))
    if s.take_count and not s.duplicate_take and s.native != "released":
        count = s.take_count + int(mutation == "second_take_transfers_owner")
        yield Transition("take.repeat_returns_invalid_state_and_empty_output", "A04",
                         replace(s, duplicate_take=True, take_count=count))
    if s.native == "retired" and s.processed:
        free_result = s.owner == "native" or (s.owner == "managed" and mutation == "release_transferred_result")
        yield Transition("operation.release_only_untaken_result", "A07",
                         replace(s, native="released", owner="freed" if free_result else s.owner,
                                 result_frees=s.result_frees + int(free_result),
                                 invalidated_result=s.owner == "managed" and free_result,
                                 survived_operation_release=s.owner == "managed" and not free_result,
                                 untaken_released=s.owner == "native"))
    if s.task == "succeeded" and s.owner == "managed":
        yield Transition("application.dispose_independent_result", "A04",
                         replace(s, owner="freed", result_frees=s.result_frees + 1))


def violations(state):
    s = state
    if s.commits > 1:
        yield "multiple_native_terminal_commits"
    if s.take_count > 1:
        yield "multiple_result_ownership_transfers"
    if s.result_frees > 1:
        yield "multiple_result_frees"
    if s.invalidated_result:
        yield "operation_release_invalidates_transferred_result"
    if s.task == "canceled" and s.outcome != "canceled":
        yield "cancellation_request_overwrites_native_terminal"
    if s.task == "succeeded" and (s.outcome != "success" or s.binding_failure or s.take_count != 1):
        yield "success_without_exact_result_transfer"
    if s.take_count and s.owner == "native":
        yield "native_keeps_transferred_owner"
    if s.native == "released" and s.owner == "native":
        yield "untaken_result_not_released"


def milestones(state):
    s = state
    if s.task == "succeeded" and s.canceled:
        yield "cancel_request_does_not_overwrite_success"
    if s.took_after_retire:
        yield "take_after_native_retirement"
    if s.survived_operation_release:
        yield "owned_result_survives_operation_release"
    if s.conversion_failed:
        yield "conversion_failure_disposes_transferred_owner"
    if s.untaken_released:
        yield "untaken_result_released_by_operation"
    if s.duplicate_take:
        yield "second_take_rejected"
    if s.duplicate_terminal and s.duplicate_notice:
        yield "duplicate_terminal_and_notice_preserve_first_latch"
    if s.binding_failure and s.task == "faulted" and s.outcome == "unknown_effect":
        yield "binding_failure_preserves_unknown_native_effect"
    if s.task == "timeout":
        yield "timeout_is_distinct_from_cancellation"
    if s.task == "canceled":
        yield "native_cancellation_published"


def model(mutation=""):
    expected = {"cancel_overwrites_terminal": "cancellation_request_overwrites_native_terminal",
                "second_take_transfers_owner": "multiple_result_ownership_transfers",
                "release_transferred_result": "operation_release_invalidates_transferred_result"}.get(mutation, "")
    return Model(
        "results/" + (mutation or "candidate"), State(),
        lambda state: successors(state, mutation), violations,
        lambda state: state.native == "released" and state.owner in ("none", "freed"), milestones,
        ("cancel_request_does_not_overwrite_success", "take_after_native_retirement",
         "owned_result_survives_operation_release", "conversion_failure_disposes_transferred_owner",
         "untaken_result_released_by_operation", "second_take_rejected",
         "duplicate_terminal_and_notice_preserve_first_latch",
         "binding_failure_preserves_unknown_native_effect", "timeout_is_distinct_from_cancellation",
         "native_cancellation_published"),
        {"operations": 1, "independent_result_owners": 1, "terminal_attempts": 2,
         "notice_attempts": 2, "take_attempts": 2, "terminal_outcomes": 5},
        ("Terminal and latch claims and ownership moves are atomic SC actions.",
         "Retirement has already satisfied the native barriers modeled separately in retirement.py.",
         "The coordinator eventually processes every received latch, including ordinary binding failures.",
         "For this bounded fixture the application eventually disposes a successfully transferred result.",
         "No promise of deterministic result disposal is made when an application discards an owned Task result.",
         "Idle stuttering is excluded; fatal allocation failure and arbitrary corrupt cookies are outside scope."),
        expected)
