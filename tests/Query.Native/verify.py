#!/usr/bin/env python3
"""B11 Linux x64: immutable inputs, real providers, canonical C# ABI JIT and AOT."""
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

scopes = ['native/database/query', 'src/Query.Native', 'tests/Query.Native', 'samples/api/query-native',
          'docs/guides/query-native.md', 'native/database/binding', 'native/database/interfaces',
          'native/database/codecs', 'src/Query.Binding', 'src/Data.Abstractions', 'src/Interop.Abi',
          'native/interop/abi', 'Cargo.toml', 'Cargo.lock', 'global.json', 'rust-toolchain.toml',
          'Directory.Build.props', 'Directory.Build.targets']
host_scopes = ['native/database', 'native/interop', 'Cargo.toml', 'Cargo.lock', 'rust-toolchain.toml']
source = inputs(ROOT, scopes)
host_source = inputs(a.host_source.resolve(), host_scopes)
write('source-inputs.json', source)
write('host-inputs.json', host_source)
try:
    if source['dirty'] or host_source['dirty']:
        raise RuntimeError('Formal evidence requires committed implementation and Host inputs; dirty identities are retained.')
    design = subprocess.check_output(['git', '-C', str(a.design), 'rev-parse', 'HEAD'], text=True).strip()
    if design != 'c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9':
        raise RuntimeError('Exact pinned Design commit required.')
    module = a.native_module.resolve()
    native_hash = sha(module)
    write('environment.json', {'utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
          'platform': platform.platform(), 'machine': platform.machine(), 'design_commit': design,
          'tools': {t: shutil.which(t) for t in ['dotnet', 'cargo', 'rustc', 'clang', 'ld']},
          'native_module': str(module), 'native_sha256': native_hash})
    run('diff-check', ['git', 'diff', '--check'])
    run('canonical-cdecl', ['python3', 'tests/Query.Binding/generate.py', '--check', '--design', a.design])
    run('native-format', ['cargo', 'fmt', '-p', 'nativeweave-query-native', '--', '--check'])
    manifest = 'tests/Query.Native/native/Cargo.toml'
    run('test-format', ['cargo', 'fmt', '--manifest-path', manifest, '--', '--check'])
    run('native-clippy', ['cargo', 'clippy', '-p', 'nativeweave-query-native', '--all-targets', '--locked', '--offline', '--', '-D', 'warnings'])
    run('test-clippy', ['cargo', 'clippy', '--manifest-path', manifest, '--all-targets', '--locked', '--offline', '--', '-D', 'warnings'])
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
    run('native-real-providers', ['cargo', 'test', '--manifest-path', manifest, '--locked', '--offline', '--', '--nocapture'])
    common = ['-p:UseSharedCompilation=false', '-nr:false', '-m:1']
    config = ['--configfile', a.nuget_config] if a.nuget_config else []
    project = 'tests/Query.Native/NativeWeave.Query.Native.Tests.csproj'
    dll = ROOT / 'tests/Query.Native/bin/Release/net11.0/NativeWeave.Query.Native.Tests.dll'
    run('restore-jit', ['dotnet', 'restore', project, '--locked-mode', *config, *common])
    run('build-jit', ['dotnet', 'build', project, '-c', 'Release', '--no-restore', *common])
    run('actual-jit-host', ['dotnet', dll, module, out / 'oracle', out / 'jit'])
    run('restore-aot', ['dotnet', 'restore', project, '-r', 'linux-x64', '-p:PublishAot=true', '--locked-mode', *config, *common])
    run('publish-aot', ['dotnet', 'publish', project, '-c', 'Release', '-r', 'linux-x64', '-p:PublishAot=true', '--no-restore', '-o', out / 'aot', *common])
    executable = out / 'aot/NativeWeave.Query.Native.Tests'
    run('actual-aot-host', [executable, module, out / 'oracle', out / 'aot-results', '--require-aot'])
    applicability = json.loads((ROOT / 'src/Query.Native/api-applicability.json').read_text())
    if applicability['public_clr_surface'] != [] or list((ROOT / 'src/Query.Native').glob('*.cs')):
        raise RuntimeError('B11 internal-only CLR API applicability drifted.')
    write('api-applicability.json', {'public_clr_symbols': 0, 'exact_cdecl_signatures': 42,
          'compiled_examples': 'samples/api/query-native/Examples.cs in both executed JIT/AOT consumers',
          'reused_internal_binding': applicability['managed_adapter']})
    run('features', ['cargo', 'tree', '--manifest-path', manifest, '--locked', '--offline', '-e', 'features'])
    run('dotnet-identity', ['dotnet', '--info'])
    run('rustc-identity', ['rustc', '-vV'])
    after = inputs(ROOT, scopes)
    after_host = inputs(a.host_source.resolve(), host_scopes)
    if source != after or host_source != after_host or native_hash != sha(module):
        raise RuntimeError('Acceptance inputs changed during execution.')
    write('artifact-identities.json', {'native_sha256': native_hash, 'managed_sha256': sha(dll),
          'aot_sha256': sha(executable), 'source_inputs_unchanged': True,
          'oracle_sha256': {f.name: sha(f) for f in (out / 'oracle').iterdir()},
          'separate_distribution_package': False, 'pristine_machine': False})
    write('status.json', {'result': 'passed', 'platform': 'linux-x64', 'candidate': source['commit'],
          'host_candidate': host_source['commit'], 'commands': len(commands),
          'jit': json.loads((out / 'jit/result.json').read_text()),
          'aot': json.loads((out / 'aot-results/result.json').read_text()),
          'scope': 'B11 native and C# ABI composition with actual B04/B02/B03/B06/B07; B13 Fluent and other OS rows remain separate'})
    print('PASS B11 Linux real-provider JIT and AOT acceptance', flush=True)
except Exception as failure:
    write('status.json', {'result': 'failed', 'platform': 'linux-x64', 'candidate': source['commit'], 'error': str(failure), 'completed_commands': len(commands)})
    raise
finally:
    if server is not None:
        subprocess.run([str(a.postgres_bin.resolve() / 'pg_ctl'), '-D', str(out / 'postgres-data'), 'stop', '-m', 'fast'], stdout=server_log, stderr=subprocess.STDOUT)
        server.wait(timeout=15)
    if server_log is not None:
        server_log.close()
