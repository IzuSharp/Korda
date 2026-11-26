#!/usr/bin/env python3
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess

ROOT = Path('/workspace/scratch/b94e2a0e0c3f')
REPO = ROOT / 'Korda-DB-Perf-01-linux'
OUT = Path(__file__).resolve().parent / 'correctness'
OUT.mkdir(exist_ok=False)
spec = importlib.util.spec_from_file_location('dbperf_controller', REPO / 'benchmarks/db-perf/run.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
executable = OUT.parent / 'after-build/aot/Korda.Data.NativeHost.Tests'
assert hashlib.sha256(executable.read_bytes()).hexdigest() == '1068ecbf6ec2e75a033262838c8e996077eee43d1c8c174c8dd3b9d65e1839b5'
pgbin = '/workspace/scratch/82a414196324/Korda-DB-Rename-01-linux/artifacts/postgres-tools/install/bin'
plan = {'provider': 'postgresql', 'postgres': {'bin': pgbin, 'data': str(ROOT / 'db-perf-pgdata')}}
setup = []
with module.database(plan, OUT) as (connection, server_pid):
    administrative = connection.rsplit('/', 1)[0] + '/postgres'
    fixture = connection.rsplit('/', 1)[0] + '/db_perf_fetch_delegates_v1'
    for sql, name in [('CREATE DATABASE db_perf_fetch_delegates_v1', 'create-fixture')]:
        command = [pgbin + '/psql', administrative, '-v', 'ON_ERROR_STOP=1', '-c', sql]
        with (OUT / (name + '.log')).open('x') as log:
            result = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT)
        setup.append({'name': name, 'argv': command, 'exitCode': result.returncode})
        (OUT / 'fixture-commands.json').write_text(json.dumps(setup, indent=2) + '\n')
        result.check_returncode()
    module.execute({'name': 'two-provider-suite', 'phase': 'correctness',
                    'timeoutSeconds': 120,
                    'argv': ['env', 'NW_TEST_POSTGRES=' + fixture, str(executable)]},
                   connection, server_pid, OUT, .05)
    command = [pgbin + '/psql', administrative, '-v', 'ON_ERROR_STOP=1',
               '-c', 'DROP DATABASE db_perf_fetch_delegates_v1']
    with (OUT / 'drop-fixture.log').open('x') as log:
        result = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT)
    setup.append({'name': 'drop-fixture', 'argv': command, 'exitCode': result.returncode})
    (OUT / 'fixture-commands.json').write_text(json.dumps(setup, indent=2) + '\n')
    result.check_returncode()
print((OUT / 'two-provider-suite.stdout.log').read_text(), flush=True)
