#!/usr/bin/env python3
import argparse
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
from pathlib import Path

ROOT = Path('/workspace/scratch/b94e2a0e0c3f')
REPO = ROOT / 'Korda-DB-Perf-01-linux'
BASE = Path(__file__).resolve().parent
parser = argparse.ArgumentParser()
parser.add_argument('--phase', required=True)
parser.add_argument('--build', choices=['before', 'after'], required=True)
args = parser.parse_args()
out = BASE / args.phase
out.mkdir(exist_ok=False)


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


build = BASE / (args.build + '-build')
receipt = json.loads((build / 'build-receipt.json').read_text())
executable = build / 'aot/Korda.Data.NativeHost.Tests'
assert sha(executable) == receipt['executableSha256']
source = json.loads((build / 'source-inputs.json').read_text())
assert source == {p: sha(REPO / p) for p in source}
spec = importlib.util.spec_from_file_location('dbperf_controller', REPO / 'benchmarks/db-perf/run.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
identity = {'phase': args.phase, 'startedUtc': datetime.now(timezone.utc).isoformat(),
            'buildReceiptSha256': sha(build / 'build-receipt.json'),
            'executableSha256': sha(executable), 'probeSha256': receipt['probeSha256'],
            'runnerSha256': sha(__file__), 'controllerSha256': sha(REPO / 'benchmarks/db-perf/run.py'),
            'independentProcessesPerProvider': 3, 'warmupPerWorker': 16,
            'operationsPerWindowPerWorker': 16, 'windows': 3, 'concurrency': [1, 4], 'records': []}
(out / 'identity.json').write_text(json.dumps(identity, indent=2) + '\n')
for provider in ('sqlite', 'postgresql'):
    provider_out = out / provider
    provider_out.mkdir()
    plan = {'provider': provider, 'connection': str(BASE / 'fixture.sqlite')}
    if provider == 'postgresql':
        plan['postgres'] = {
            'bin': '/workspace/scratch/82a414196324/Korda-DB-Rename-01-linux/artifacts/postgres-tools/install/bin',
            'data': str(ROOT / 'db-perf-pgdata'),
        }
    with module.database(plan, provider_out) as (connection, server_pid):
        for process in range(3):
            result = provider_out / f'process-{process}'
            item = {'name': f'process-{process}', 'phase': 'short-public-allocation-attribution',
                    'timeoutSeconds': 120, 'argv': [str(executable), '--transaction-default-probe', provider,
                        '{connection}', str(result), '16', '3']}
            observed = module.execute(item, connection, server_pid, provider_out, .05)
            identity['records'].append({'provider': provider, 'process': process,
                'file': str(result / 'transaction-allocation.json'),
                'sha256': sha(result / 'transaction-allocation.json'), 'exitCode': observed['exitCode']})
assert source == {p: sha(REPO / p) for p in source}
identity['completedUtc'] = datetime.now(timezone.utc).isoformat()
identity['status'] = 'passed'
identity['sourcesUnchangedThroughSampling'] = True
(out / 'completion.json').write_text(json.dumps(identity, indent=2) + '\n')
print('completed', args.phase, flush=True)
