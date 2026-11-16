#!/usr/bin/env python3
"""Run the finite candidate mailbox model without a native runtime or SDK."""

import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "src/Verification.Models"))

from checker import explore
from mailbox import model
from suite import oracle_met


problem = model()
report = explore(problem)
print(json.dumps({"model": problem.name, "status": report["status"],
                  "states": report["states_discovered"], "progress": report["progress"],
                  "scope": "Bounded sequentially consistent model, not actual runtime proof."}, indent=2))
raise SystemExit(0 if oracle_met(problem, report) else 1)
