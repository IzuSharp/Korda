#!/usr/bin/env python3
"""Retained B11 Linux oracle: fixed Rust expectations, C++ Host, C# JIT and AOT."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import socket
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
p = argparse.ArgumentParser()
p.add_argument('--design', type=Path, required=True)
p.add_argument('--postgres-bin', type=Path, required=True)
p.add_argument('--native-module', type=Path, required=True)
p.add_argument('--host-source', type=Path, required=True)
p.add_argument('--native-build', type=Path, default=ROOT / 'artifacts/cmake/linux-x64-release')
p.add_argument('--output', type=Path, required=True)
p.add_argument('--nuget-config', type=Path)
a = p.parse_args()
if sys.platform != 'linux' or platform.machine() != 'x86_64':
    raise SystemExit('This acceptance run supports the authorized Linux x64 row only.')
out = a.output.resolve()
out.mkdir(parents=True, exist_ok=False)
env = os.environ.copy()
env.update(DOTNET_CLI_USE_MSBUILD_SERVER='0', MSBUILDUSESERVER='0', NATIVEWEAVE_QUERY_ORACLE_OUTPUT=str(out / 'oracle'))
(out / 'oracle').mkdir()
commands = []
server = None
server_log = None

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def write(name, value):
    (out / name).write_text(json.dumps(value, indent=2) + '\n')

def run(name, argv):
    log = out / f'{len(commands) + 1:02d}-{name}.log'
    start = time.monotonic()
    print('RUN ' + name, flush=True)
    with log.open('w') as stream:
        stream.write('$ ' + json.dumps([str(v) for v in argv]) + '\n')
        stream.flush()
        result = subprocess.run([str(v) for v in argv], cwd=ROOT, env=env,
                                stdout=stream, stderr=subprocess.STDOUT, timeout=600)
    commands.append({'name': name, 'argv': [str(v) for v in argv], 'exit_code': result.returncode,
                     'seconds': round(time.monotonic() - start, 3), 'log': log.name})
    write('commands.json', commands)
    if result.returncode:
        raise RuntimeError(f'{name} failed; raw log retained in {log}')
    print('PASS ' + name, flush=True)

def inputs(root, scopes):
    files = subprocess.check_output(['git', 'ls-files', '--', *scopes], cwd=root, text=True).splitlines()
    return {'commit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip(),
            'tree': subprocess.check_output(['git', 'rev-parse', 'HEAD^{tree}'], cwd=root, text=True).strip(),
            'dirty': subprocess.check_output(['git', 'status', '--porcelain', '--', *scopes], cwd=root, text=True).strip(),
            'sha256': {name: sha(root / name) for name in files}}

scopes = ['native/cpp', 'src/Query.Native', 'tests/Query.Native', 'samples/api/query-native',
          'docs/guides/query-native.md', 'native/database/binding', 'native/database/interfaces',
          'src/Query.Binding', 'src/Data.Abstractions', 'src/Interop.Abi',
          'native/interop/abi', 'CMakeLists.txt', 'CMakePresets.json', 'cmake', 'global.json',
          '.clang-format', 'Directory.Build.props', 'Directory.Build.targets']
host_scopes = ['native/cpp', 'native/database/binding', 'native/database/interfaces',
               'native/interop/abi', 'CMakeLists.txt', 'CMakePresets.json', 'cmake']
source = inputs(ROOT, scopes)
host_source = inputs(a.host_source.resolve(), host_scopes)
write('source-inputs.json', source)
write('host-inputs.json', host_source)
try:
    if source['dirty'] or host_source['dirty']:
        raise RuntimeError('Formal evidence requires committed implementation and Host inputs; dirty identities are retained.')
    design = subprocess.check_output(['git', '-C', str(a.design), 'rev-parse', 'HEAD'], text=True).strip()
    module = a.native_module.resolve()
    native_hash = sha(module)
    # These are byte-for-byte copies of the independently recorded, merged Plan
    # baseline. A candidate renderer/provider must never regenerate its oracle.
    golden = ROOT / 'tests/Query.Native/golden/oracle'
    provenance = json.loads((golden / 'provenance.json').read_text())
    pinned = {
        'sqlite.json': '0ef5dcd5117315d6c7f70189fb53da5def4ecf00dc8ec5032bf269f70fd975e9',
        'postgres.json': '2b3b491a39d9ac6860587e6768997686a7f83447a5c80f3353a560f6f3101934',
    }
    if provenance['commit'] != '1fe52a8bd792ccdbb4bbea17ba59b0ad0c24349d':
        raise RuntimeError('The merged Rust oracle provenance changed.')
    if {entry['name']: entry['sha256'] for entry in provenance['files']} != pinned:
        raise RuntimeError('The merged Rust oracle inventory changed.')
    for name, expected in pinned.items():
        fixture = golden / name
        if sha(fixture) != expected or len(json.loads(fixture.read_text())) != 26:
            raise RuntimeError('The fixed 52-case Rust oracle changed: ' + name)
        shutil.copyfile(fixture, out / 'oracle' / name)
    write('oracle-provenance.json', provenance)
    write('environment.json', {'utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
          'platform': platform.platform(), 'machine': platform.machine(), 'design_commit': design,
          'tools': {t: shutil.which(t) for t in ['dotnet', 'clang', 'clang++', 'cmake', 'ctest', 'ld']},
          'native_module': str(module), 'native_sha256': native_hash})
    run('diff-check', ['git', 'diff', '--check'])
    run('canonical-cdecl', ['python3', 'tests/Query.Binding/generate.py', '--check', '--design', a.design])
    run('native-format', ['clang-format', '--dry-run', '--Werror',
         'native/cpp/include/nw/query.hpp', 'native/cpp/include/nw/mutations.hpp',
         'native/cpp/src/query.cpp', 'native/cpp/src/mutations.cpp', 'native/cpp/tests/query_tests.cpp'])
    run('native-state-and-sql-contracts', ['ctest', '--test-dir', a.native_build.resolve(),
         '--output-on-failure', '--no-tests=error', '-R', '^native\\.query$'])
    run('managed-format', ['dotnet', 'format', 'whitespace', '.', '--folder', '--include', 'tests/Query.Native', 'samples/api/query-native', '--verify-no-changes', '--verbosity', 'quiet'])
    pg = a.postgres_bin.resolve()
    data = out / 'postgres-data'
    run('postgres-identity', [pg / 'postgres', '--version'])
    run('postgres-init', [pg / 'initdb', '-D', data, '-U', 'nativeweave', '--auth=trust', '--no-locale', '--encoding=UTF8'])
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        port = listener.getsockname()[1]
    server_log = (out / 'postgres.log').open('w')
    server = subprocess.Popen([pg / 'postgres', '-D', data, '-h', '127.0.0.1', '-p', str(port), '-k', ''], stdout=server_log, stderr=subprocess.STDOUT, env=env)
    for _ in range(300):
        if subprocess.run([pg / 'pg_isready', '-h', '127.0.0.1', '-p', str(port)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0:
            break
        if server.poll() is not None:
            raise RuntimeError('PostgreSQL exited before readiness.')
        time.sleep(.05)
    else:
        raise RuntimeError('PostgreSQL startup exceeded its finite deadline.')
    # Host accepts typed TLS policy only; URI query options are intentionally rejected.
    env['NATIVEWEAVE_TEST_POSTGRES_URL'] = f'postgresql://nativeweave@127.0.0.1:{port}/postgres'
    common = ['-p:UseSharedCompilation=false', '-nr:false', '-m:1']
    config = ['--configfile', a.nuget_config] if a.nuget_config else []
    project = 'tests/Query.Native/NativeWeave.Query.Native.Tests.csproj'
    dll = ROOT / 'tests/Query.Native/bin/Release/net11.0/NativeWeave.Query.Native.Tests.dll'
    run('restore-jit', ['dotnet', 'restore', project, '--locked-mode', *config, *common])
    run('build-jit', ['dotnet', 'build', project, '-c', 'Release', '--no-restore', *common])
    run('actual-jit-host', ['dotnet', dll, module, out / 'oracle', out / 'jit'])
    run('restore-aot', ['dotnet', 'restore', project, '-r', 'linux-x64', '-p:PublishAot=true', '--locked-mode', *config, *common])
    run('publish-aot', ['dotnet', 'publish', project, '-c', 'Release', '-r', 'linux-x64', '-p:PublishAot=true',
         '-p:IlcTreatWarningsAsErrors=true', '--no-restore', '-o', out / 'aot', *common])
    executable = out / 'aot/NativeWeave.Query.Native.Tests'
    if executable.read_bytes()[:4] != b'\x7fELF' or executable.with_suffix('.dll').exists():
        raise RuntimeError('Expected an actual standalone Native AOT executable.')
    run('actual-aot-host', [executable, module, out / 'oracle', out / 'aot-results', '--require-aot'])
    applicability = json.loads((ROOT / 'src/Query.Native/api-applicability.json').read_text())
    if applicability['public_clr_surface'] != [] or list((ROOT / 'src/Query.Native').glob('*.cs')):
        raise RuntimeError('B11 internal-only CLR API applicability drifted.')
    write('api-applicability.json', {'public_clr_symbols': 0, 'exact_cdecl_signatures': 42,
          'compiled_examples': 'samples/api/query-native/Examples.cs in both executed JIT/AOT consumers',
          'reused_internal_binding': applicability['managed_adapter']})
    run('dotnet-identity', ['dotnet', '--info'])
    run('clang-identity', ['clang++', '--version'])
    run('clang-target', ['clang++', '-dumpmachine'])
    run('native-dependencies', ['readelf', '-d', module])
    after = inputs(ROOT, scopes)
    after_host = inputs(a.host_source.resolve(), host_scopes)
    if source != after or host_source != after_host or native_hash != sha(module):
        raise RuntimeError('Acceptance inputs changed during execution.')
    write('artifact-identities.json', {'native_sha256': native_hash, 'managed_sha256': sha(dll),
          'aot_sha256': sha(executable), 'source_inputs_unchanged': True,
          'oracle_sha256': {f.name: sha(f) for f in (out / 'oracle').iterdir()},
          'separate_distribution_package': False, 'pristine_machine': False})
    write('status.json', {'result': 'passed', 'task': 'DB-CPP-01', 'platform': 'linux-x64', 'candidate': source['commit'],
          'host_candidate': host_source['commit'], 'commands': len(commands),
          'fixed_rust_oracle_cases': 52, 'oracle_plan_commit': provenance['commit'],
          'jit': json.loads((out / 'jit/result.json').read_text()),
          'aot': json.loads((out / 'aot-results/result.json').read_text()),
          'scope': 'Retained B11 C# ABI cases and fixed Rust SQL/row oracles, executed through the C++ Host on both providers in JIT and Native AOT'})
    print('PASS retained B11 Linux C++ real-provider JIT and AOT acceptance', flush=True)
except Exception as failure:
    write('status.json', {'result': 'failed', 'platform': 'linux-x64', 'candidate': source['commit'], 'error': str(failure), 'completed_commands': len(commands)})
    raise
finally:
    if server is not None:
        subprocess.run([str(a.postgres_bin.resolve() / 'pg_ctl'), '-D', str(out / 'postgres-data'), 'stop', '-m', 'fast'], stdout=server_log, stderr=subprocess.STDOUT)
        server.wait(timeout=15)
    if server_log is not None:
        server_log.close()
