#!/usr/bin/env python3
"""Reuse a frozen Host AOT artifact; never rebuild or relabel its producer."""
import argparse
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess

ROOT = Path('/workspace/scratch/b94e2a0e0c3f')
REPO = ROOT / 'Korda-DB-Perf-01-linux'
BASE = Path(__file__).resolve().parent
parser = argparse.ArgumentParser()
parser.add_argument('--phase', required=True)
parser.add_argument('--build-directory', type=Path, required=True)
parser.add_argument('--provider', choices=['sqlite', 'postgresql'], required=True)
parser.add_argument('--processes', type=int, default=3)
args = parser.parse_args()
if args.processes != 3:
    raise ValueError('This frozen comparison requires exactly three fresh processes.')
out = BASE / args.phase / args.provider
out.mkdir(parents=True, exist_ok=False)


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def write(name, value):
    (out / name).write_text(json.dumps(value, indent=2) + '\n')


build = args.build_directory.resolve()
receipt_path = build / 'build-receipt.json'
receipt = json.loads(receipt_path.read_text())
manifest_path = build / 'source-inputs.json'
manifest = json.loads(manifest_path.read_text())
executable = build / 'aot/Korda.Data.NativeHost.Tests'
probe = REPO / 'tests/Data.NativeHost/FetchProbe.cs'
assert sha(executable) == receipt['executableSha256']
assert sha(manifest_path) == receipt['sourceManifestSha256']
assert sha(build / 'source-inputs.tar.gz') == receipt['sourceArchiveSha256']
assert sha(probe) == manifest['tests/Data.NativeHost/FetchProbe.cs']
assert sha(probe) == '2d7e50e095d630a0caf22a773bbd94c869c60cb50127175a8d050d51f5417d6c'
source_changes = [name for name, digest in manifest.items() if sha(REPO / name) != digest]
assert not source_changes, source_changes
spec = importlib.util.spec_from_file_location('dbperf_controller', REPO / 'benchmarks/db-perf/run.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
plan = {'provider': args.provider, 'connection': str(BASE / 'fixture.sqlite')}
if args.provider == 'postgresql':
    plan['postgres'] = {
        'bin': '/workspace/scratch/82a414196324/Korda-DB-Rename-01-linux/artifacts/postgres-tools/install/bin',
        'data': str(ROOT / 'db-perf-pgdata'),
    }
identity = {
    'scope': 'Iteration14 copy attribution; unchanged public FetchProbe with frozen original AOT provenance. Not final independent-package acceptance or comparative timing.',
    'phase': args.phase, 'startedUtc': datetime.now(timezone.utc).isoformat(),
    'reusedBuildDirectory': str(build), 'originalBuildReceiptPath': str(receipt_path),
    'originalBuildReceiptSha256': sha(receipt_path),
    'originalBuildGitHead': receipt['gitHead'], 'originalBuildSourceDirty': receipt['sourceDirty'],
    'sourceManifestPath': str(manifest_path), 'sourceManifestSha256': sha(manifest_path),
    'sourceArchivePath': str(build / 'source-inputs.tar.gz'),
    'sourceArchiveSha256': sha(build / 'source-inputs.tar.gz'),
    'executablePath': str(executable), 'executableSha256': sha(executable),
    'fetchProbeSha256': sha(probe),
    'measurementGitHead': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=REPO, text=True).strip(),
    'currentSourcesMatchBuildManifest': True, 'matchedSourceCount': len(manifest),
    'runnerSha256': sha(__file__), 'controllerSha256': sha(REPO / 'benchmarks/db-perf/run.py'),
    'provider': args.provider, 'independentProcesses': args.processes,
    'cpuAffinity': sorted(os.sched_getaffinity(0)),
    'warmup': 64, 'iterationsPerWindow': 128, 'windows': 9,
    'resultRows': [0, 1, 256], 'batchRows': 32,
    'timeoutSecondsPerProcess': 120, 'records': [],
    'failurePolicy': 'Retain original command/log and stop; never alter unchanged immediate ownership assertions or retry a failed process for acceptance.',
}
write('identity.json', identity)
try:
    with module.database(plan, out) as (connection, server_pid):
        for process in range(args.processes):
            result = out / f'process-{process}'
            item = {'name': f'process-{process}', 'phase': 'short-public-allocation-attribution',
                    'timeoutSeconds': 120, 'argv': [str(executable), '--fetch-probe', args.provider,
                        '{connection}', str(result), '64', '128', '9']}
            print(f'START {args.phase} {args.provider} process={process}', flush=True)
            observed = module.execute(item, connection, server_pid, out, .05)
            raw_path = result / 'fetch-allocation.json'
            raw = json.loads(raw_path.read_text())
            assert raw['ExecutableSha256'] == identity['executableSha256']
            assert raw['LoadedNativeSha256'] == receipt['nativeImages'][args.provider]['sha256']
            assert raw['OwnersReturnedToZero'] is True
            identity['records'].append({'process': process, 'file': str(raw_path),
                                        'sha256': sha(raw_path), 'exitCode': observed['exitCode']})
            write('progress.json', identity)
            print(f'PASS {args.phase} {args.provider} process={process}', flush=True)
except BaseException as error:
    identity['completedUtc'] = datetime.now(timezone.utc).isoformat()
    identity['status'] = 'failed'
    identity['error'] = f'{type(error).__name__}: {error}'
    write('completion.json', identity)
    raise
identity['completedUtc'] = datetime.now(timezone.utc).isoformat()
identity['status'] = 'passed'
write('completion.json', identity)
print(f'COMPLETED {args.phase} {args.provider}', flush=True)
