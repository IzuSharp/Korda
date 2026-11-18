#!/usr/bin/env python3
"""Run the declared full B23 matrix; defaults to functional until phase is explicit."""
from pathlib import Path
import subprocess
import sys

controller = Path(__file__).resolve().parents[3] / 'src/Benchmarks.Domain/run.py'
raise SystemExit(subprocess.call([sys.executable, str(controller), '--cases', 'all', *sys.argv[1:]]))
