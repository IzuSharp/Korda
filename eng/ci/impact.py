"""Compatibility entry for the documented local impact command."""
import subprocess
import sys
from facts import IDS, PROVIDERS, inputs  # Stable imports used by existing packaging callers.
from pathlib import Path

if __name__ == "__main__":
    root = Path(__file__).resolve().parents[2]
    raise SystemExit(subprocess.call(["dotnet", "run", "--project", str(root / "eng/ci"), "--",
                                     "plan", *sys.argv[1:]], cwd=root))
