#!/usr/bin/env python3
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tarfile

ROOT = Path('/workspace/scratch/b94e2a0e0c3f')
REPO = ROOT / 'Korda-DB-Perf-01-linux'
BASE = Path(__file__).resolve().parent
parser = argparse.ArgumentParser()
parser.add_argument('--phase', choices=['before', 'after'], required=True)
args = parser.parse_args()
out = BASE / (args.phase + '-build')
out.mkdir(exist_ok=False)


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def write(path, value):
    Path(path).write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n')


previous = ROOT / 'measurements/fetch-delegates-v1/after-build/source-inputs.json'
inputs = json.loads(previous.read_text())
paths = sorted(set(inputs) | {'tests/Data.NativeHost/TransactionDefaultProbe.cs'})
source = {path: sha(REPO / path) for path in paths}
if args.phase == 'before':
    changed = [p for p in inputs if source[p] != inputs[p]]
    assert changed == ['tests/Data.NativeHost/Program.cs'], changed
else:
    before = json.loads((BASE / 'before-build/source-inputs.json').read_text())
    changed = [p for p in source if before[p] != source[p]]
    assert changed == ['src/Data.NativeHost/DataSession.cs'], changed
write(out / 'source-inputs.json', source)
with tarfile.open(out / 'source-inputs.tar.gz', 'w:gz') as archive:
    for path in source:
        archive.add(REPO / path, arcname=path)
(out / 'source.patch').write_bytes(subprocess.check_output(['git', 'diff', '--',
    'src/Data.NativeHost/DataSession.cs', 'tests/Data.NativeHost/Program.cs'], cwd=REPO))
previous_receipt = json.loads((ROOT / 'measurements/fetch-delegates-v1/after-build/build-receipt.json').read_text())
command = previous_receipt['command'].copy(); command[-1] = str(out / 'aot')
receipt = {
    'scope': 'Iteration13 same public default/explicit Begin-Rollback-Dispose probe with concurrent independent sessions; source-project Native AOT, not package acceptance',
    'phase': args.phase, 'command': command,
    'gitHead': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=REPO, text=True).strip(),
    'sourceDirty': True,
    'changedSourcesFromPreviousBoundary': changed,
    'sourceManifestSha256': sha(out / 'source-inputs.json'),
    'sourceArchiveSha256': sha(out / 'source-inputs.tar.gz'),
    'probeSha256': source['tests/Data.NativeHost/TransactionDefaultProbe.cs'],
    'nativeImages': previous_receipt['nativeImages'],
    'startedUtc': datetime.now(timezone.utc).isoformat(),
}
write(out / 'build-receipt.json', receipt)
with (out / 'publish.log').open('x') as log:
    result = subprocess.run(command, cwd=REPO, stdout=log, stderr=subprocess.STDOUT)
receipt.update(exitCode=result.returncode, finishedUtc=datetime.now(timezone.utc).isoformat(),
               logSha256=sha(out / 'publish.log'),
               sourcesUnchanged=source == {p: sha(REPO / p) for p in source})
assets = REPO / 'tests/Data.NativeHost/obj/project.assets.json'
if assets.exists():
    shutil.copy2(assets, out / 'project.assets.json')
    receipt['restoreGraphSha256'] = sha(out / 'project.assets.json')
executable = out / 'aot/Korda.Data.NativeHost.Tests'
if executable.exists():
    receipt['executableSha256'] = sha(executable)
write(out / 'build-receipt.json', receipt)
print(json.dumps(receipt, ensure_ascii=False, indent=2), flush=True)
if result.returncode:
    raise SystemExit(result.returncode)
assert receipt['sourcesUnchanged']
for name in ('bin', 'obj'):
    shutil.rmtree(REPO / 'tests/Data.NativeHost' / name)
