"""Versioned bounded model catalog used by the CLI, tests and examples."""

import admission
import dispatcher
import mailbox
import results
import retirement


def models():
    """Return candidates and deliberately unsafe controls with explicit oracles."""
    return [
        admission.model(),
        admission.model("split_gate"),
        admission.model("missing_rollback"),
        retirement.model(),
        retirement.model("setup"),
        retirement.model("native"),
        retirement.model("registration"),
        retirement.model("control"),
        results.model(),
        results.model("cancel_overwrites_terminal"),
        results.model("second_take_transfers_owner"),
        results.model("release_transferred_result"),
        dispatcher.model(),
        dispatcher.model("omit_handler_drain"),
        mailbox.model(),
        mailbox.model(True),
    ]


def oracle_met(model, result):
    """A negative fixture passes only by finding its specific named violation."""
    if model.expected_violation:
        return result["status"] == "violated" and model.expected_violation in result["counterexamples"]
    return (result["status"] == "passed" and result["exhaustive_safety_search"]
            and result["progress"]["status"] == "passed" and not result["missing_milestones"])
