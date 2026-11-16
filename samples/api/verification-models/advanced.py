#!/usr/bin/env python3
"""Reproduce and replay a lost wake; distinguish an incomplete search from pass."""

import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "src/Verification.Models"))

from checker import explore, replay
from mailbox import model
from suite import oracle_met


unsafe = model(stale_unschedule=True)
negative = explore(unsafe)
if not oracle_met(unsafe, negative):
    raise SystemExit("Expected lost-wake counterexample was not found")
trace = json.loads(json.dumps(negative["counterexamples"][unsafe.expected_violation]))
final = replay(unsafe, trace)
if unsafe.expected_violation not in unsafe.violations(final):
    raise SystemExit("Counterexample replay did not establish the expected violation")
bounded = explore(model(), max_states=1)
if bounded["status"] != "inconclusive":
    raise SystemExit("State exhaustion was incorrectly treated as a pass")
print(json.dumps({"negative_control": unsafe.name, "expected_violation": unsafe.expected_violation,
                  "counterexample": trace, "resource_limited_candidate_status": bounded["status"],
                  "scope": "The unsafe control is deliberately wrong; it is not the product implementation."}, indent=2))
