#!/usr/bin/env python3
"""Build only affected modules, preserving the exact validated Data assembly."""
from datetime import datetime, timezone
import argparse
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
parser.add_argument('--kind', choices=['modules', 'streaming', 'host'], required=True)
parser.add_argument('--attempt', default='v1')
args = parser.parse_args()
out = BASE / (args.kind + '-build-' + args.attempt)
out.mkdir(exist_ok=False)
sha = lambda p: hashlib.sha256(Path(p).read_bytes()).hexdigest()
before = json.loads((BASE / 'before-reuse.json').read_text())
old = ROOT / 'measurements/native-batch-borrow-v1/host-build-v2'
previous = json.loads((old / 'build-receipt.json').read_text())
inputs = json.loads((old / 'source-inputs.json').read_text())
source = {name: sha(REPO / name) for name in inputs}
changes = sorted(name for name in inputs if inputs[name] != source[name])
assert changes == ['src/Data.Streaming/TypedStream.cs', 'tests/Data.Streaming/AllocationProbeRaces.cs'], changes
for name, digest in before['dataDependencyAssembliesBefore'].items(): assert sha(name) == digest
(out / 'source-inputs.json').write_text(json.dumps(source, indent=2) + '\n')
with tarfile.open(out / 'source-inputs.tar.gz', 'w:gz') as archive:
    for name in source: archive.add(REPO / name, arcname=name)
(out / 'source.patch').write_bytes(subprocess.check_output(['git', 'diff', '--', 'src', 'tests'], cwd=REPO))


def references():
    return {str(path.relative_to(REPO)): sha(path)
            for path in sorted((REPO / 'src').rglob('*.dll'))
            if 'bin' in path.parts and 'Release' in path.parts and 'ref' not in path.parts and 'refint' not in path.parts}


receipt = {'scope': 'Iteration15 source-project Native AOT; only private MoveAsync builder production change. Data assembly preserved byte for byte; no metadata override.',
    'kind': args.kind, 'attempt': args.attempt,
    'gitHead': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=REPO, text=True).strip(),
    'sourceDirty': bool(subprocess.check_output(['git', 'status', '--porcelain'], cwd=REPO, text=True).strip()),
    'sourceManifestSha256': sha(out / 'source-inputs.json'), 'sourceArchiveSha256': sha(out / 'source-inputs.tar.gz'),
    'sourceInputCount': len(source), 'changedSourceInputs': changes,
    'probeSha256': source['tests/Data.NativeHost/FetchProbe.cs'], 'nativeImages': previous['nativeImages'],
    'beforeReferenceDlls': references(), 'startedUtc': datetime.now(timezone.utc).isoformat(), 'commands': []}
common = ['-c', 'Release', '-p:BuildProjectReferences=false', '-p:UseSharedCompilation=false', '-m:1', '-nr:false']
native = ['-p:KordaSqliteAssetPath=' + previous['nativeImages']['sqlite']['path'],
          '-p:KordaPostgresAssetPath=' + previous['nativeImages']['postgresql']['path']]
if args.kind == 'modules':
    projects = ['src/Data.Streaming/Korda.Data.Streaming.csproj', 'src/Data.NativeHost/Korda.Database.csproj',
                'src/Data.Sqlite/Korda.Database.Sqlite.csproj', 'src/Data.Postgres/Korda.Database.PostgreSql.csproj',
                'src/Data.NativeHost/DependencyInjection/Korda.Database.DependencyInjection.csproj']
    commands = [['dotnet', 'build', project, *common, *native] for project in projects]
else:
    folder, assembly = ('Data.Streaming', 'Korda.Data.Streaming.Tests') if args.kind == 'streaming' else ('Data.NativeHost', 'Korda.Data.NativeHost.Tests')
    commands = [['dotnet', 'publish', f'tests/{folder}/{assembly}.csproj', *common,
                 '-r', 'linux-x64', '-p:PublishAot=true', '-p:IlcTreatWarningsAsErrors=true', *native, '-o', str(out / 'aot')]]


def save():
    (out / 'build-receipt.json').write_text(json.dumps(receipt, indent=2) + '\n')


save()
for index, command in enumerate(commands):
    log = out / f'build-{index}.log'
    started = datetime.now(timezone.utc).isoformat()
    with log.open('x') as output:
        result = subprocess.run(command, cwd=REPO, stdout=output, stderr=subprocess.STDOUT)
    receipt['commands'].append({'argv': command, 'startedUtc': started, 'finishedUtc': datetime.now(timezone.utc).isoformat(),
                                'exitCode': result.returncode, 'logSha256': sha(log)})
    save()
    print(args.kind, index, 'exit', result.returncode, flush=True)
    if result.returncode:
        print(log.read_text()[-10000:], flush=True)
        raise SystemExit(result.returncode)
    for name, digest in before['dataDependencyAssembliesBefore'].items(): assert sha(name) == digest, name
receipt.update(exitCode=0, finishedUtc=datetime.now(timezone.utc).isoformat(),
    sourcesUnchanged=source == {name: sha(REPO / name) for name in source},
    afterReferenceDlls=references(), dataDependencyDllsUnchanged=True)
assert receipt['sourcesUnchanged']
if args.kind != 'modules':
    assets = REPO / 'tests' / folder / 'obj/project.assets.json'
    shutil.copy2(assets, out / 'project.assets.json')
    receipt['restoreGraphSha256'] = sha(out / 'project.assets.json')
    receipt['executableSha256'] = sha(out / 'aot' / assembly)
save()
if args.kind != 'modules':
    for name in ('bin', 'obj'): shutil.rmtree(REPO / 'tests' / folder / name)
print('receipt', out / 'build-receipt.json', flush=True)
