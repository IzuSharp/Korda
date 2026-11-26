#!/usr/bin/env python3
"""Sequential affected Native AOT builds with exact, immutable source receipts."""
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
SUITES = {
    'data': ('Data.Abstractions', 'Korda.Data.Tests'),
    'streaming': ('Data.Streaming', 'Korda.Data.Streaming.Tests'),
    'host': ('Data.NativeHost', 'Korda.Data.NativeHost.Tests'),
    'query': ('Query.Fluent', 'Korda.Query.Fluent.Tests'),
    'materialization': ('Data.Materialization', 'Korda.Data.Materialization.Tests'),
}
parser = argparse.ArgumentParser()
parser.add_argument('--suite', choices=SUITES, required=True)
parser.add_argument('--attempt', default='v1')
args = parser.parse_args()
out = BASE / (args.suite + '-build-' + args.attempt)
out.mkdir(exist_ok=False)


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def write(path, value):
    Path(path).write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n')


inputs = set()
excluded = {'bin', 'obj', 'artifacts', '__pycache__'}
for scope in ('src', 'tests/Data.Abstractions', 'tests/Data.Streaming', 'tests/Data.NativeHost',
              'tests/Query.Fluent', 'tests/Data.Materialization', 'samples/api/data-materialization',
              'samples/api/query-fluent'):
    path = REPO / scope
    if path.exists():
        inputs.update(str(f.relative_to(REPO)) for f in path.rglob('*')
                      if f.is_file() and not excluded.intersection(f.relative_to(REPO).parts))
for name in ('Directory.Build.props', 'Directory.Build.targets', 'global.json'):
    if (REPO / name).exists(): inputs.add(name)
source = {name: sha(REPO / name) for name in sorted(inputs)}
write(out / 'source-inputs.json', source)
with tarfile.open(out / 'source-inputs.tar.gz', 'w:gz') as archive:
    for name in source: archive.add(REPO / name, arcname=name)
(out / 'source.patch').write_bytes(subprocess.check_output(['git', 'diff', '--', 'src', 'tests'], cwd=REPO))
previous = json.loads((ROOT / 'measurements/transaction-default-v3/after-build/build-receipt.json').read_text())
folder, assembly = SUITES[args.suite]
command = ['dotnet', 'publish', f'tests/{folder}/{assembly}.csproj', '-c', 'Release', '-r', 'linux-x64',
           '-p:PublishAot=true', '-p:IlcTreatWarningsAsErrors=true', '-p:UseSharedCompilation=false', '-m:1', '-nr:false']
if args.suite in ('host', 'query', 'materialization'):
    command += ['-p:KordaSqliteAssetPath=' + previous['nativeImages']['sqlite']['path'],
                '-p:KordaPostgresAssetPath=' + previous['nativeImages']['postgresql']['path']]
command += ['-o', str(out / 'aot')]
receipt = {
    'scope': 'Iteration14 affected Native AOT correctness and unchanged public allocation probe; source-project validation, not final PackageReference acceptance',
    'suite': args.suite, 'attempt': args.attempt, 'command': command,
    'gitHead': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=REPO, text=True).strip(),
    'sourceDirty': bool(subprocess.check_output(['git', 'status', '--porcelain'], cwd=REPO, text=True).strip()),
    'sourceManifestSha256': sha(out / 'source-inputs.json'),
    'sourceArchiveSha256': sha(out / 'source-inputs.tar.gz'),
    'sourceInputCount': len(source), 'nativeImages': previous['nativeImages'],
    'probeSha256': source['tests/Data.NativeHost/FetchProbe.cs'],
    'startedUtc': datetime.now(timezone.utc).isoformat(),
}
write(out / 'build-receipt.json', receipt)
with (out / 'publish.log').open('x') as log:
    result = subprocess.run(command, cwd=REPO, stdout=log, stderr=subprocess.STDOUT)
receipt.update(exitCode=result.returncode, finishedUtc=datetime.now(timezone.utc).isoformat(),
               logSha256=sha(out / 'publish.log'), sourcesUnchanged=source == {p: sha(REPO / p) for p in source})
assets = REPO / 'tests' / folder / 'obj/project.assets.json'
if assets.exists():
    shutil.copy2(assets, out / 'project.assets.json')
    receipt['restoreGraphSha256'] = sha(out / 'project.assets.json')
executable = out / 'aot' / assembly
if executable.exists(): receipt['executableSha256'] = sha(executable)
write(out / 'build-receipt.json', receipt)
print(json.dumps(receipt, ensure_ascii=False, indent=2), flush=True)
if result.returncode:
    print((out / 'publish.log').read_text()[-12000:], flush=True)
    raise SystemExit(result.returncode)
assert receipt['sourcesUnchanged']
for name in ('bin', 'obj'):
    shutil.rmtree(REPO / 'tests' / folder / name)
