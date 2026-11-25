from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess

WORK = Path(__file__).resolve().parent
PLAN_PATH = WORK / 'plan.json'
PLAN = json.loads(PLAN_PATH.read_text())
REPO = Path(PLAN['repository'])
TOOLING = WORK.parents[1] / 'tooling'
BUILDER = REPO / 'benchmarks/db-perf/business-results/build.py'

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
    for cell in PLAN['cells']:
        package = cell['package']
        if package:
            assert sha(package['path']) == package['sha256'], package['path']

def add_support_cache(app):
    cache = WORK / 'support-cache'
    for package in sorted((app / 'packages').iterdir()):
        if not package.is_dir() or package.name.lower().startswith('korda.'):
            continue
        target = cache / package.name
        target.mkdir(exist_ok=True)
        for version in sorted(package.iterdir()):
            if not version.is_dir(): continue
            link = target / version.name
            if link.exists():
                for archive in version.glob('*.nupkg'):
                    assert sha(link / archive.name) == sha(archive), str(link)
            else:
                link.symlink_to(version.resolve(), target_is_directory=True)

env = os.environ.copy()
env.update({
    'DOTNET_ROOT': str(TOOLING / 'dotnet'),
    'DOTNET_CLI_HOME': str(WORK / 'dotnet-cli-home'),
    'TMPDIR': str(WORK / 'tmp'),
    'NUGET_HTTP_CACHE_PATH': str(WORK / 'nuget-http-cache'),
    'NUGET_SCRATCH': str(WORK / 'tmp/nuget'),
    'DOTNET_PROCESSOR_COUNT': '2',
    'DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE': 'true',
    'DOTNET_CLI_TELEMETRY_OPTOUT': '1',
    'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1',
    'DOTNET_NOLOGO': '1',
    'DOTNET_CLI_USE_MSBUILD_SERVER': '0',
    'MSBUILDDISABLENODEREUSE': '1',
    'UseSharedCompilation': 'false',
    'PYTHONDONTWRITEBYTECODE': '1',
})
commands = []
for cell in PLAN['cells']:
    command = ['python3', '-B', str(BUILDER), '--route', cell['route'],
               '--provider', cell['provider'], '--support-cache', str(WORK / 'support-cache'),
               '--dotnet', str(TOOLING / 'dotnet/dotnet'), '--output', cell['app']]
    if cell['package']:
        command += ['--feed', str(Path(cell['package']['path']).parent),
                    '--version', cell['package']['version']]
    commands.append({'cell': cell['id'], 'argv': command, 'app': cell['app']})
save(WORK / 'build-commands.json', {
    'created_utc': utc(), 'plan_sha256': sha(PLAN_PATH), 'controller_sha256': sha(__file__),
    'compile_only_processor_count': 2, 'maximum_concurrent_builds': 1,
    'work_directory': str(WORK), 'commands': commands,
    'environment_overrides': {key: env[key] for key in
        ['DOTNET_ROOT', 'DOTNET_CLI_HOME', 'TMPDIR', 'NUGET_HTTP_CACHE_PATH',
         'DOTNET_PROCESSOR_COUNT', 'DOTNET_CLI_USE_MSBUILD_SERVER',
         'MSBUILDDISABLENODEREUSE', 'UseSharedCompilation']},
})
state = {'started_utc': utc(), 'status': 'started', 'builds': [],
         'plan_sha256': sha(PLAN_PATH), 'consumer_execution_performed': False}
save(WORK / 'build-controller-start.json', state)
try:
    for entry in commands:
        verify_inputs()
        app = Path(entry['app'])
        assert not app.exists(), app
        record = {'cell': entry['cell'], 'started_utc': utc(), 'argv': entry['argv']}
        log_path = WORK / 'logs' / (entry['cell'] + '-builder.log')
        process = None
        try:
            with log_path.open('xb') as log:
                process = subprocess.Popen(entry['argv'], cwd=WORK, env=env,
                                           stdout=log, stderr=subprocess.STDOUT,
                                           start_new_session=True)
                record['controller_child_pid'] = process.pid
                record['exit_code'] = process.wait(timeout=1200)
            if record['exit_code']:
                raise RuntimeError(f"Build failed: {entry['cell']} ({record['exit_code']})")
            receipt_path = app / 'build-receipt.json'
            receipt = json.loads(receipt_path.read_text())
            assert receipt['exit_code'] == 0 and not receipt.get('validation_error')
            assert sha(app / 'out/BusinessResults') == receipt['executable_sha256']
            record.update(status='validated', build_receipt_sha256=sha(receipt_path),
                          executable_sha256=receipt['executable_sha256'])
            add_support_cache(app)
            print(json.dumps({'cell': entry['cell'], 'status': 'built',
                              'executable_sha256': receipt['executable_sha256']}), flush=True)
        except BaseException as error:
            record['status'] = 'failed'
            record['error'] = {'type': type(error).__name__, 'message': str(error)}
            if process is not None and process.poll() is None:
                os.killpg(process.pid, signal.SIGTERM)
                try: process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait(timeout=10)
            raise
        finally:
            record['finished_utc'] = utc()
            if log_path.exists(): record['builder_log_sha256'] = sha(log_path)
            save(WORK / 'logs' / (entry['cell'] + '-build-execution.json'), record)
            state['builds'].append(record)
    state['status'] = 'all_six_builds_validated'
except BaseException as error:
    state['status'] = 'failed'
    state['error'] = {'type': type(error).__name__, 'message': str(error)}
    raise
finally:
    state['finished_utc'] = utc()
    save(WORK / 'build-controller-complete.json', state)
print(json.dumps({'status': state['status'], 'builds': len(state['builds'])}))
