"""Fingerprint inputs to the Linux PostgreSQL client or test-tool installation."""
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import sys

root = Path(__file__).resolve().parents[1]
lock = json.loads((root / "native/dependencies.lock.json").read_text())
pg = next(dep for dep in lock["dependencies"] if dep["name"] == "libpq")
CACHE_REVISION = 1
key = hashlib.sha256(json.dumps([CACHE_REVISION, pg], sort_keys=True).encode())
for path in (Path(__file__), root / "eng/postgres-tools.sh",
             root / "native/cpp/patches/libpq-explicit-configuration.patch",
             Path(os.environ["CC"]).resolve(), Path("/etc/os-release")):
    with path.open("rb") as stream:
        key.update(hashlib.file_digest(stream, "sha256").digest())
for value in (sys.argv[1], platform.machine(),
              subprocess.check_output(["pkg-config", "--modversion", "openssl", "zlib"], text=True)):
    key.update(value.encode() + b"\0")
print(key.hexdigest())
