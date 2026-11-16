"""D09 coalescing mailbox and the historical stale-unschedule counterexample."""

from dataclasses import dataclass, replace
from checker import Model, Transition


@dataclass(frozen=True)
class State:
    generation: int = 1
    observed: int = 0
    dirty: bool = True
    scheduled: bool = True
    worker: str = "idle"
    snapshot: bool = False
    woke_during_drain: bool = False
    coalesced: bool = False


def successors(state, maximum_notifications, stale_unschedule):
    s = state
    if s.generation < maximum_notifications:
        yield Transition("producer.publish_dirty_and_schedule_reserved_slot", "A08",
                         replace(s, generation=s.generation + 1, dirty=True, scheduled=True,
                                 woke_during_drain=s.woke_during_drain or s.worker in ("check", "stale"),
                                 coalesced=s.coalesced or s.scheduled))
    if s.worker == "idle" and s.scheduled:
        yield Transition("coordinator.dispatch_reserved_slot", "A08", replace(s, worker="drain"))
    if s.worker == "drain":
        yield Transition("coordinator.consume_current_state", "A08",
                         replace(s, observed=s.generation, dirty=False, worker="check"))
    if s.worker == "check":
        if stale_unschedule:
            yield Transition("coordinator.read_dirty_before_unschedule", "A08",
                             replace(s, worker="stale", snapshot=s.dirty))
        else:
            yield Transition("coordinator.atomic_recheck_and_unschedule", "A08",
                             replace(s, scheduled=s.dirty, worker="drain" if s.dirty else "idle"))
    if s.worker == "stale":
        yield Transition("coordinator.unschedule_from_stale_snapshot", "A08",
                         replace(s, scheduled=s.snapshot, worker="drain" if s.snapshot else "idle", snapshot=False))


def violations(state):
    if state.dirty and not state.scheduled and state.worker == "idle":
        yield "lost_wake_dirty_without_runnable_slot"
    if state.observed > state.generation:
        yield "observed_unpublished_state"


def milestones(state):
    if state.woke_during_drain:
        yield "wake_between_drain_and_unschedule"
    if state.coalesced:
        yield "notifications_coalesce_in_one_reserved_slot"
    if state.observed == 3 and not state.scheduled:
        yield "all_three_notifications_observed"


def model(stale_unschedule=False):
    maximum = 3
    return Model(
        "mailbox/" + ("stale_unschedule" if stale_unschedule else "candidate"), State(),
        lambda state: successors(state, maximum, stale_unschedule), violations,
        lambda state: state.generation == maximum and state.observed == maximum
        and not state.dirty and not state.scheduled and state.worker == "idle", milestones,
        ("wake_between_drain_and_unschedule", "notifications_coalesce_in_one_reserved_slot",
         "all_three_notifications_observed"),
        {"operations": 1, "reserved_slots": 1, "coordinators": 1, "notifications": maximum},
        ("Each atomic action is sequentially consistent; one lock protects dirty and scheduled in the candidate.",
         "A notification means resample idempotent status, not discard an ordered data-bearing event.",
         "The one reserved slot is queued or executing at most once; no notification allocates another slot.",
         "The producer emits exactly the finite notification bound and the coordinator eventually runs.",
         "No new notification or unrelated periodic scanner is assumed to rescue an already lost wake."),
        "lost_wake_dirty_without_runnable_slot" if stale_unschedule else "")
