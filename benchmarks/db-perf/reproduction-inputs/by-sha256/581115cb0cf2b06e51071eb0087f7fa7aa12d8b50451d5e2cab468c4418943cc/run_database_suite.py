#!/usr/bin/env python3
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess

ROOT = Path('/workspace/scratch/b94e2a0e0c3f')
REPO = ROOT / 'Korda-DB-Perf-01-linux'
BASE = Path(__file__).resolve().parent
parser = argparse.ArgumentParser()
parser.add_argument('--suite', choices=['streaming', 'host', 'query', 'materialization'], required=True)
parser.add_argument('--attempt', default='v1')
args = parser.parse_args()
out = BASE / (args.suite + '-correctness-' + args.attempt)
out.mkdir(exist_ok=False)
sha = lambda p: hashlib.sha256(Path(p).read_bytes()).hexdigest()
build = BASE / (args.suite + '-build-' + args.attempt)
receipt = json.loads((build / 'build-receipt.json').read_text())
assembly = {'streaming': 'Korda.Data.Streaming.Tests', 'host': 'Korda.Data.NativeHost.Tests',
            'query': 'Korda.Query.Fluent.Tests', 'materialization': 'Korda.Data.Materialization.Tests'}[args.suite]
executable = build / 'aot' / assembly
assert sha(executable) == receipt['executableSha256']
spec = importlib.util.spec_from_file_location('dbperf_controller', REPO / 'benchmarks/db-perf/run.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
pgbin = '/workspace/scratch/82a414196324/Korda-DB-Rename-01-linux/artifacts/postgres-tools/install/bin'
plan = {'provider': 'postgresql', 'postgres': {'bin': pgbin, 'data': str(ROOT / 'db-perf-pgdata')}}
identity = {'scope': 'Actual two-provider source-project Native AOT correctness, not performance measurement.',
            'suite': args.suite, 'buildReceiptSha256': sha(build / 'build-receipt.json'),
            'executableSha256': sha(executable), 'controllerSha256': sha(REPO / 'benchmarks/db-perf/run.py'),
            'runnerSha256': sha(__file__)}
if args.suite == 'streaming':
    fixture_receipt = ROOT / 'measurements/native-batch-borrow-v1/native-fixture-reuse/receipt.json'
    fixture = json.loads(fixture_receipt.read_text())
    assert sha(fixture['nativeFixturePath']) == fixture['nativeFixtureSha256']
    identity['nativeFixtureReuseReceiptSha256'] = sha(fixture_receipt)
    identity['nativeFixtureSha256'] = fixture['nativeFixtureSha256']
(out / 'identity.json').write_text(json.dumps(identity, indent=2) + '\n')
setup = []
with module.database(plan, out) as (connection, server_pid):
    administrative = connection.rsplit('/', 1)[0] + '/postgres'
    database = 'db_perf_poolmove_' + args.suite + '_' + args.attempt
    assert database.replace('_', '').isalnum()
    fixture_url = connection.rsplit('/', 1)[0] + '/' + database
    command = [pgbin + '/psql', administrative, '-v', 'ON_ERROR_STOP=1', '-c',
               'CREATE DATABASE ' + database + " TEMPLATE template0 ENCODING 'UTF8' LC_COLLATE 'C' LC_CTYPE 'C'"]
    with (out / 'create-fixture.log').open('x') as log:
        result = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT)
    setup.append({'name': 'create-fixture', 'argv': command, 'exitCode': result.returncode})
    (out / 'fixture-commands.json').write_text(json.dumps(setup, indent=2) + '\n')
    result.check_returncode()
    command = [pgbin + '/psql', fixture_url, '-v', 'ON_ERROR_STOP=1', '-At', '-c', 'SHOW server_encoding']
    result = subprocess.run(command, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    (out / 'database-encoding.txt').write_text(result.stdout)
    setup.append({'name': 'check-encoding', 'argv': command, 'exitCode': result.returncode, 'observed': result.stdout.strip()})
    (out / 'fixture-commands.json').write_text(json.dumps(setup, indent=2) + '\n')
    result.check_returncode()
    assert result.stdout.strip() == 'UTF8'
    if args.suite == 'streaming':
        argv = ['env', 'LD_LIBRARY_PATH=' + str(Path(fixture['nativeFixturePath']).parent),
                'KORDA_TEST_POSTGRES_URL=' + fixture_url, str(executable), str(out / 'results'),
                '--require-aot', '--correctness-only']
    else:
        argv = ['env', 'NW_TEST_POSTGRES=' + fixture_url, str(executable)]
        if args.suite != 'host': argv += [str(out / 'results'), '--require-aot']
    module.execute({'name': 'two-provider-suite', 'phase': 'correctness', 'timeoutSeconds': 120, 'argv': argv},
                   connection, server_pid, out, .05)
    command = [pgbin + '/psql', administrative, '-v', 'ON_ERROR_STOP=1', '-c', 'DROP DATABASE ' + database]
    with (out / 'drop-fixture.log').open('x') as log:
        result = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT)
    setup.append({'name': 'drop-fixture', 'argv': command, 'exitCode': result.returncode})
    (out / 'fixture-commands.json').write_text(json.dumps(setup, indent=2) + '\n')
    result.check_returncode()
print((out / 'two-provider-suite.stdout.log').read_text(), flush=True)
