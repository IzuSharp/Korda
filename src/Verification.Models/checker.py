"""Bounded, deterministic, sequentially consistent finite-state exploration.

This maintainer tool does not execute a Korda runtime. Model transitions
are atomic specifications, not CLR/Rust instructions or weak-memory events.
"""

from collections import defaultdict, deque
from dataclasses import asdict, dataclass, is_dataclass
import time
from typing import Callable, Iterable, NamedTuple


TOOL_VERSION = "1.0.0"


class Transition(NamedTuple):
    """One atomic action and the task owning its eventual implementation."""

    action: str
    owner: str
    target: object


@dataclass(frozen=True)
class Model:
    """An explicit finite problem; goals do not replace safety checks."""

    name: str
    initial: object
    successors: Callable[[object], Iterable[Transition]]
    violations: Callable[[object], Iterable[str]]
    goal: Callable[[object], bool]
    milestones: Callable[[object], Iterable[str]]
    required_milestones: tuple
    bounds: dict
    assumptions: tuple
    expected_violation: str = ""


def snapshot(state):
    """Return stable JSON-compatible state for replay and independent review."""
    if is_dataclass(state):
        return snapshot(asdict(state))
    if isinstance(state, dict):
        return {name: snapshot(value) for name, value in state.items()}
    if isinstance(state, (tuple, list)):
        return [snapshot(value) for value in state]
    return state


def _trace(target, previous, incoming):
    steps = []
    cursor = target
    while previous[cursor] is not None:
        action, owner = incoming[cursor]
        steps.append({"action": action, "owner": owner, "state": snapshot(cursor)})
        cursor = previous[cursor]
    steps.reverse()
    return {"initial": snapshot(cursor), "steps": steps}


def replay(model, trace):
    """Replay recorded named transitions against the current model source.

    A state or transition mismatch rejects the trace rather than treating an old
    counterexample as fresh execution evidence.
    """
    state = model.initial
    if snapshot(state) != trace["initial"]:
        raise ValueError("Trace initial state differs from the current model")
    for step in trace["steps"]:
        choices = [edge for edge in model.successors(state)
                   if edge.action == step["action"] and edge.owner == step["owner"]
                   and snapshot(edge.target) == step["state"]]
        if len(choices) != 1:
            raise ValueError("Trace transition is absent or ambiguous: " + step["action"])
        state = choices[0].target
    return state


def explore(model, max_states=200000, timeout_seconds=30.0):
    """Explore all candidate states; stop a negative control at its first fault.

    Safety violations retain a shortest breadth-first trace. Bounded liveness
    uses a universal successor fixed point: every non-stuttering path must reach
    a goal, under the model's finite-progress assumptions. A non-goal deadlock is
    a failure; a residual cycle or exhausted resource budget is inconclusive.
    No timeout, partial search or absent success witness can pass.
    """
    if max_states < 1 or timeout_seconds <= 0:
        raise ValueError("Positive state and time limits are required")
    started = time.monotonic()
    initial = model.initial
    queue = deque([initial])
    previous = {initial: None}
    incoming = {}
    graph = {}
    goals = set()
    witnesses = {}
    failures = {}
    edges = 0
    interrupted = None

    while queue:
        if time.monotonic() - started >= timeout_seconds:
            interrupted = "time_limit"
            break
        state = queue.popleft()
        found = sorted(set(model.violations(state)))
        if found:
            for name in found:
                failures.setdefault(name, _trace(state, previous, incoming))
            # Preserve the first actual violation; this is not exhaustive success.
            break
        if model.goal(state):
            goals.add(state)
        for name in model.milestones(state):
            witnesses.setdefault(name, state)
        transitions = sorted(model.successors(state), key=lambda edge: edge.action)
        if len({edge.action for edge in transitions}) != len(transitions):
            raise ValueError("Actions must be unique at each state: " + model.name)
        graph[state] = []
        for transition in transitions:
            target = transition.target
            edges += 1
            if target not in previous:
                if len(previous) >= max_states:
                    interrupted = "state_limit"
                    break
                previous[target] = state
                incoming[target] = (transition.action, transition.owner)
                queue.append(target)
            graph[state].append(target)
        if interrupted:
            break

    missing = sorted(set(model.required_milestones) - set(witnesses))
    progress = {"status": "not_checked", "assumptions": list(model.assumptions)}
    status = "violated" if failures else "inconclusive" if interrupted else "passed"
    exhaustive = not failures and not interrupted and not queue
    if exhaustive:
        reverse = defaultdict(list)
        remaining = {}
        distance = {state: 0 for state in goals}
        for state, targets in graph.items():
            remaining[state] = len(targets)
            for target in targets:
                reverse[target].append(state)
        ready = deque(goals)
        longest = defaultdict(int)
        while ready:
            if time.monotonic() - started >= timeout_seconds:
                interrupted = "time_limit_during_progress_check"
                break
            target = ready.popleft()
            for source in reverse[target]:
                if source in distance:
                    continue
                remaining[source] -= 1
                longest[source] = max(longest[source], distance[target])
                if remaining[source] == 0:
                    distance[source] = longest[source] + 1
                    ready.append(source)
        residual = set(graph) - set(distance)
        deadlocks = [state for state in residual if not graph[state]]
        if interrupted:
            status = "inconclusive"
            progress["status"] = "inconclusive"
        elif deadlocks:
            # Pick deterministically in BFS insertion order.
            deadlock = next(state for state in previous if state in deadlocks)
            progress.update(status="failed", reason="non_goal_deadlock",
                            counterexample=_trace(deadlock, previous, incoming))
            status = "violated"
        elif residual:
            state = next(state for state in previous if state in residual)
            progress.update(status="inconclusive", reason="non_goal_cycle",
                            residual_states=len(residual),
                            prefix=_trace(state, previous, incoming))
            status = "inconclusive"
        else:
            progress.update(status="passed", states_reaching_goal=len(distance),
                            maximum_progress_steps=max(distance.values(), default=0),
                            initial_maximum_progress_steps=distance.get(initial))
        if missing:
            status = "violated"

    return {
        "model": model.name,
        "status": status,
        "exhaustive_safety_search": exhaustive,
        "bounds": model.bounds,
        "search": "deterministic breadth-first; first violation retained",
        "seed": None,
        "seed_reason": "Exhaustive deterministic exploration; no PRNG is used.",
        "states_discovered": len(previous),
        "states_expanded": len(graph),
        "transitions_examined": edges,
        "goal_states": len(goals),
        "limits": {"max_states": max_states, "timeout_seconds": timeout_seconds},
        "limit_reached": interrupted,
        "counterexamples": failures,
        "progress": progress,
        "required_milestones": list(model.required_milestones),
        "missing_milestones": missing,
        "milestone_witnesses": {name: _trace(state, previous, incoming)
                                for name, state in sorted(witnesses.items())},
        "elapsed_seconds": round(time.monotonic() - started, 6),
    }
