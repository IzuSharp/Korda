"""D05 discard-pending/join-active subscription drain, independent of native quiet."""

from dataclasses import dataclass, replace
from checker import Model, Transition


@dataclass(frozen=True)
class State:
    closing: bool = False
    produced: int = 0
    callback: int = -1
    copied: tuple = (False, False)
    freed: tuple = (0, 0)
    pending: tuple = ()
    active: int = -1
    retired: bool = False
    reclaimed: bool = False
    self_wait_rejected: bool = False
    wait_canceled: bool = False
    close_with_active: bool = False
    discarded_pending: bool = False
    overflow_rejected: bool = False
    started_after_close: bool = False


def _free(state, items, **changes):
    freed = list(state.freed)
    for item in items:
        freed[item] += 1
    return replace(state, freed=tuple(freed), **changes)


def successors(state, mutation):
    s = state
    if not s.closing:
        yield Transition("subscription.close_and_discard_pending", "A06",
                         _free(s, s.pending, closing=True, pending=(),
                               discarded_pending=s.discarded_pending or bool(s.pending),
                               close_with_active=s.close_with_active or s.active != -1))
    if s.closing and not s.reclaimed and not s.wait_canceled:
        yield Transition("caller.cancel_wait_without_releasing_payloads", "A06", replace(s, wait_canceled=True))
    if not s.closing and s.callback == -1 and s.produced < 2:
        yield Transition("native.admit_event_callback_lease", "A06",
                         replace(s, callback=s.produced, produced=s.produced + 1))
    if s.callback != -1:
        index = s.callback
        if not s.copied[index]:
            copied = list(s.copied)
            copied[index] = True
            if s.closing or len(s.pending) == 1:
                yield Transition("callback.copy_then_discard_closed_or_full_event", "A06",
                                 _free(s, (index,), copied=tuple(copied),
                                       overflow_rejected=s.overflow_rejected or (not s.closing and len(s.pending) == 1)))
            else:
                yield Transition("callback.copy_into_bounded_dispatch_queue", "A06",
                                 replace(s, copied=tuple(copied), pending=s.pending + (index,)))
        else:
            yield Transition("callback.return_native_lease", "A06", replace(s, callback=-1))
    if s.pending and s.active == -1 and not s.closing:
        yield Transition("dispatcher.atomic_open_gate_and_start_handler", "A06",
                         replace(s, active=s.pending[0], pending=s.pending[1:]))
    if s.active != -1:
        yield Transition("handler.complete_and_release_owned_copy", "A06", _free(s, (s.active,), active=-1))
        if not s.self_wait_rejected:
            yield Transition("handler.dispose_async_self_wait_rejected", "A06", replace(s, self_wait_rejected=True))
    if s.closing and s.callback == -1 and not s.retired:
        yield Transition("native.retire_after_all_callback_sources_close", "A06", replace(s, retired=True))
    if s.retired and not s.pending and not s.reclaimed and (s.active == -1 or mutation == "omit_handler_drain"):
        yield Transition("cleanup.complete_dispose_async", "A06", replace(s, reclaimed=True))


def violations(state):
    s = state
    if s.reclaimed and s.active != -1:
        yield "dispose_success_with_active_handler"
    if s.reclaimed and (s.pending or not s.retired or s.callback != -1):
        yield "dispose_success_before_dispatch_and_native_drain"
    if s.started_after_close:
        yield "handler_started_after_dispatch_close"
    if len(s.pending) > 1:
        yield "dispatch_queue_capacity_exceeded"
    for index, (copied, freed) in enumerate(zip(s.copied, s.freed)):
        if freed > 1:
            yield "event_owner_freed_twice"
        owned = int(index in s.pending) + int(index == s.active)
        if int(copied) - freed != owned:
            yield "event_copy_ownership_not_conserved"


def milestones(state):
    s = state
    if s.close_with_active and s.reclaimed:
        yield "close_joins_handler_already_running"
    if s.discarded_pending:
        yield "close_discards_pending_owned_copy"
    if s.retired and s.active != -1:
        yield "native_quiet_while_managed_handler_still_running"
    if s.self_wait_rejected:
        yield "handler_self_wait_is_rejected"
    if s.overflow_rejected:
        yield "full_queue_rejects_and_releases_event"
    if s.wait_canceled and s.reclaimed:
        yield "canceled_wait_does_not_prevent_background_drain"
    if s.reclaimed and s.copied[0] and not s.freed[0] == 0:
        yield "event_received_then_clean_release"


def model(mutation=""):
    return Model(
        "dispatcher/" + (mutation or "candidate"), State(),
        lambda state: successors(state, mutation), violations,
        lambda state: state.reclaimed, milestones,
        ("close_joins_handler_already_running", "close_discards_pending_owned_copy",
         "native_quiet_while_managed_handler_still_running", "handler_self_wait_is_rejected",
         "full_queue_rejects_and_releases_event", "canceled_wait_does_not_prevent_background_drain",
         "event_received_then_clean_release"),
        {"subscriptions": 1, "events": 2, "pending_capacity": 1, "active_handlers": 1,
         "native_callbacks_in_flight": 1, "owned_copies": 2},
        ("Atomic SC admission/closure; copying completes within each finite native callback.",
         "Native producer closure prevents new callback admission but does not revoke an admitted callback.",
         "Explicit Dispose discards pending events and joins active handlers; no end-of-stream drain variant.",
         "An asynchronous handler's abstract completion represents completion of its whole awaited operation.",
         "Handlers and callbacks eventually return, and cleanup gets execution; permanently hung handlers retain ownership.",
         "Cancellation of the caller's close wait is independent of the internal cleanup promise."),
        "dispose_success_with_active_handler" if mutation else "")
