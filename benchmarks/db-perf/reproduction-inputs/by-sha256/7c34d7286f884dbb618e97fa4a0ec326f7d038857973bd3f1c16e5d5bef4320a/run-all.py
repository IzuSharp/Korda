from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import subprocess

WORK = Path(__file__).resolve().parent
PLAN_PATH = WORK / 'plan.json'
PLAN = json.loads(PLAN_PATH.read_text())
REPO = Path(PLAN['repository'])
TOOLING = WORK.parents[1] / 'tooling'
RUNNER = REPO / 'benchmarks/db-perf/business-results/run.py'
ANALYZER = REPO / 'benchmarks/db-perf/business-results/analyze.py'

def utc(): return datetime.now(timezone.utc).isoformat()

def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open('x') as stream:
        json.dump(value, stream, indent=2)
        stream.write('\n')

def verify_inputs():
    for name, expected in PLAN['sources'].items():
        assert sha(REPO / name) == expected, name
    assert sha(PLAN['fixture_path']) == PLAN['fixture_sha256']
    assert sha(PLAN['runtime_reuse']['sqlite_image']) == PLAN['runtime_reuse']['sqlite_image_sha256']

verify_inputs()
build_state_path = WORK / 'build-controller-complete.json'
build_state = json.loads(build_state_path.read_text())
assert build_state['status'] == 'all_six_builds_validated'
assert len(build_state['builds']) == 6
assert all(item['status'] == 'validated' for item in build_state['builds'])
for cell in PLAN['cells']:
    app = Path(cell['app'])
    receipt = json.loads((app / 'build-receipt.json').read_text())
    assert receipt['exit_code'] == 0 and not receipt.get('validation_error')
    assert sha(app / 'out/BusinessResults') == receipt['executable_sha256']

env = os.environ.copy()
env.pop('DOTNET_PROCESSOR_COUNT', None)
env.update({
    'DOTNET_CLI_HOME': str(WORK / 'dotnet-cli-home'),
    'TMPDIR': str(WORK / 'tmp'),
    'NUGET_HTTP_CACHE_PATH': str(WORK / 'nuget-http-cache'),
    'NUGET_SCRATCH': str(WORK / 'tmp/nuget'),
    'PYTHONDONTWRITEBYTECODE': '1',
})
commands = []
lookup = {cell['id']: cell for cell in PLAN['cells']}
for round_number, ids in enumerate(PLAN['execution']['round_order']):
    for name in ids:
        cell = lookup[name]
        output = WORK / 'evidence' / name / f'round-{round_number}'
        database = WORK / 'databases' / name / f'round-{round_number}'
        command = ['python3', '-B', str(RUNNER), '--app', cell['app'],
                   '--source-label', cell['source_label'], '--output', str(output),
                   '--database-root', str(database), '--processes', '1',
                   '--operations', '128', '--observer-ref', PLAN['observer_ref']]
        if cell['provider'] == 'postgresql':
            command += ['--postgres-bin', str(TOOLING / 'postgres/install/bin')]
        elif cell['route'] == 'dapper':
            command += ['--sqlite-native', PLAN['runtime_reuse']['sqlite_image']]
        commands.append({'cell': name, 'label': cell['label'], 'round': round_number,
                         'argv': command, 'output': str(output), 'database_root': str(database)})
save(WORK / 'run-commands.json', {
    'created_utc': utc(), 'controller_sha256': sha(__file__), 'plan_sha256': sha(PLAN_PATH),
    'all_six_builds_completed_utc': build_state['finished_utc'],
    'build_state_sha256': sha(build_state_path), 'commands': commands,
    'DOTNET_PROCESSOR_COUNT': env.get('DOTNET_PROCESSOR_COUNT'),
    'affinity': sorted(os.sched_getaffinity(0)),
    'scope': 'Serial standalone Native AOT allocation runs, no build or other application workload overlaps.',
})
state = {'started_utc': utc(), 'status': 'started', 'runs': [],
         'plan_sha256': sha(PLAN_PATH), 'fixture_sha256': PLAN['fixture_sha256'],
         'DOTNET_PROCESSOR_COUNT': env.get('DOTNET_PROCESSOR_COUNT'),
         'long_segments_credited': 0, 'exact_framework_bytes': None}
save(WORK / 'run-controller-start.json', state)
try:
    for index, entry in enumerate(commands):
        verify_inputs()
        output = Path(entry['output'])
        assert not output.exists() and not Path(entry['database_root']).exists()
        log_path = WORK / 'logs' / f"run-{index:02d}-{entry['cell']}.log"
        record = {'cell': entry['cell'], 'round': entry['round'], 'index': index,
                  'started_utc': utc(), 'argv': entry['argv'],
                  'fixture_sha256': PLAN['fixture_sha256']}
        try:
            with log_path.open('xb') as log:
                result = subprocess.run(entry['argv'], cwd=WORK, env=env,
                                        stdout=log, stderr=subprocess.STDOUT)
            record['exit_code'] = result.returncode
            if result.returncode:
                raise RuntimeError(f"Actual allocation run failed: {entry['cell']}, round {entry['round']}")
            receipt = json.loads((output / 'receipt.json').read_text())
            assert receipt['status'] == 'validated' and len(receipt['processes']) == 1
            raw = output / 'process-0/result.json'
            report = json.loads(raw.read_text())
            assert report['Passed'] and report['NativeAot'] and report['DatabaseDisposed']
            assert report['ConsumerSources']['Program.cs'] == PLAN['sources']['benchmarks/db-perf/business-results/Program.cs']
            record.update(status='validated', result_path=str(raw), result_sha256=sha(raw),
                          run_receipt_sha256=sha(output / 'receipt.json'),
                          process_id=report['ProcessId'], process_started_utc=report['StartedUtc'],
                          executable_sha256=report['ExecutableSha256'],
                          build_receipt_sha256=report['BuildReceiptSha256'],
                          primary_package_sha256=report['PrimaryPackageSha256'],
                          native_images=report['NativeImages'], graph=report['Graph'])
            print(json.dumps({'index': index, 'cell': entry['cell'], 'round': entry['round'],
                              'status': 'validated', 'process_id': report['ProcessId']}), flush=True)
        except BaseException as error:
            record['status'] = 'failed'
            record['error'] = {'type': type(error).__name__, 'message': str(error)}
            raise
        finally:
            record['finished_utc'] = utc()
            if log_path.exists(): record['controller_log_sha256'] = sha(log_path)
            save(WORK / 'logs' / f"run-{index:02d}-{entry['cell']}-execution.json", record)
            state['runs'].append(record)
    command = ['python3', '-B', str(ANALYZER), '--verify-local',
               '--output', str(WORK / 'allocation-summary.json')]
    for entry, record in zip(commands, state['runs'], strict=True):
        command += ['--input', entry['label'] + '=' + record['result_path']]
    save(WORK / 'analysis-command.json', {'argv': command, 'started_utc': utc(),
         'analyzer_sha256': sha(ANALYZER), 'plan_sha256': sha(PLAN_PATH)})
    with (WORK / 'logs/analysis.log').open('xb') as log:
        analysis = subprocess.run(command, cwd=WORK, env=env, stdout=log, stderr=subprocess.STDOUT)
    assert analysis.returncode == 0, 'Original analyzer rejected the collected evidence'
    state['analysis_exit_code'] = analysis.returncode
    state['allocation_summary_sha256'] = sha(WORK / 'allocation-summary.json')
    state['status'] = 'all_eighteen_processes_validated'
except BaseException as error:
    state['status'] = 'failed'
    state['error'] = {'type': type(error).__name__, 'message': str(error)}
    raise
finally:
    state['finished_utc'] = utc()
    save(WORK / 'run-controller-complete.json', state)
print(json.dumps({'status': state['status'], 'runs': len(state['runs'])}))
