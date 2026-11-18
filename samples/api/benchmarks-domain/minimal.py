#!/usr/bin/env python3
"""Run the real B23 create/replay/page/stream functional comparison."""
from pathlib import Path
import subprocess
import sys

controller = Path(__file__).resolve().parents[3] / 'src/Benchmarks.Domain/run.py'
raise SystemExit(subprocess.call([sys.executable, str(controller), '--phase', 'functional',
    '--select', 'cold-create-small,warm-cycle-low,warm-keyset-page,stream-one-mib', *sys.argv[1:]]))
