#!/usr/bin/env python3
"""B13 Linux: real typed native queries, exact API and boundary costs and isolated NuGet Native AOT."""
import argparse
import datetime
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import shutil
import shlex
import socket
import subprocess
import sys
import tempfile
import time
import zipfile

ROOT = Path(__file__).resolve().parents[2]
p = argparse.ArgumentParser()
p.add_argument('--design', type=Path, required=True)
p.add_argument('--postgres-bin', type=Path, required=True)
p.add_argument('--sqlite-module', type=Path, required=True)
p.add_argument('--postgres-module', type=Path, required=True)
p.add_argument('--host-source', type=Path, required=True)
p.add_argument('--output', type=Path, required=True)
a = p.parse_args()
if sys.platform != 'linux' or platform.machine() != 'x86_64':
    raise SystemExit('This task acceptance is Linux x64 only.')
out = a.output.resolve()
out.mkdir(parents=True, exist_ok=False)
env = os.environ.copy()
env.update(DOTNET_CLI_USE_MSBUILD_SERVER='0', MSBUILDUSESERVER='0')
commands = []
server = None
server_log = None
common = ['-p:UseSharedCompilation=false', '-nr:false', '-m:1']
modules = [a.sqlite_module.resolve(), a.postgres_module.resolve()]
assets = [f'-p:NativeWeaveSqliteAssetPath={modules[0]}', f'-p:NativeWeavePostgresAssetPath={modules[1]}']

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def write(name, value):
    (out / name).write_text(json.dumps(value, indent=2) + '\n')

def run(name, argv, cwd=ROOT, environment=env):
    log = out / f'{len(commands) + 1:02d}-{name}.log'
    print('RUN ' + name, flush=True)
    start = time.monotonic()
    with log.open('w') as stream:
        stream.write('$ ' + json.dumps([str(v) for v in argv]) + '\n')
        stream.flush()
        result = subprocess.run([str(v) for v in argv], cwd=cwd, env=environment, stdout=stream, stderr=subprocess.STDOUT, timeout=600)
    commands.append({'name': name, 'argv': [str(v) for v in argv], 'cwd': str(cwd), 'exit_code': result.returncode,
                     'seconds': round(time.monotonic() - start, 3), 'log': log.name})
    write('commands.json', commands)
    if result.returncode:
        raise RuntimeError(f'{name} failed; retained log {log}')
    print('PASS ' + name, flush=True)

def inputs(root, scopes):
    files = subprocess.check_output(['git', 'ls-files', '--', *scopes], cwd=root, text=True).splitlines()
    return {'commit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip(),
            'tree': subprocess.check_output(['git', 'rev-parse', 'HEAD^{tree}'], cwd=root, text=True).strip(),
            'dirty': subprocess.check_output(['git', 'status', '--porcelain', '--', *scopes], cwd=root, text=True).strip(),
            'sha256': {f: sha(root / f) for f in files}}

scopes = ['src/Query.Fluent', 'tests/Query.Fluent', 'samples/api/query-fluent', 'src/Data.Materialization',
          'docs/guides/query-fluent.md', 'src/Data.Abstractions', 'src/Data.Streaming', 'src/Data.NativeHost',
          'src/Data.Sqlite', 'src/Data.Postgres', 'src/Data.Transactions', 'src/Query.Binding', 'src/Query.Generator',
          'src/Interop.Abi', 'src/NativeWeave.Core', 'Directory.Build.props', 'Directory.Build.targets', 'global.json', 'src/Packaging.Release', 'src/Packaging.Linux/packages.py', 'LICENSE', 'tests/Data.Materialization/package_feed.py']
host_scopes = ['native/cpp', 'native/database/binding', 'native/interop/abi', 'native/dependencies.lock.json', 'CMakeLists.txt', 'CMakePresets.json']
source = inputs(ROOT, scopes)
host_source = inputs(a.host_source.resolve(), host_scopes)
write('source-inputs.json', source)
write('host-inputs.json', host_source)
try:
    if source['dirty'] or host_source['dirty']:
        raise RuntimeError('Formal evidence requires committed scoped implementation and Host inputs.')
    design = subprocess.check_output(['git', '-C', str(a.design), 'rev-parse', 'HEAD'], text=True).strip()
    if design != 'c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9':
        raise RuntimeError('Pinned Design identity required.')
    native_hashes = {str(module): sha(module) for module in modules}
    write('environment.json', {'utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'platform': platform.platform(),
          'machine': platform.machine(), 'design_commit': design, 'native_sha256': native_hashes,
          'tools': {t: shutil.which(t) for t in ['dotnet', 'cmake', 'ninja', 'clang', 'clang++']}})
    run('diff-check', ['git', 'diff', '--check'])
    run('format', ['dotnet', 'format', 'whitespace', '.', '--folder', '--include', 'src/Query.Fluent', 'tests/Query.Fluent', 'samples/api/query-fluent', 'src/Data.Materialization', '--verify-no-changes', '--verbosity', 'quiet'])
    pg = a.postgres_bin.resolve()
    data = out / 'postgres-data'
    run('postgres-identity', [pg / 'postgres', '--version'])
    run('postgres-init', [pg / 'initdb', '-D', data, '-U', 'nativeweave', '--auth=trust', '--no-locale', '--encoding=UTF8'])
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0)); port = listener.getsockname()[1]
    server_log = (out / 'postgres.log').open('w')
    server = subprocess.Popen([pg / 'postgres', '-D', data, '-h', '127.0.0.1', '-p', str(port), '-k', ''], stdout=server_log, stderr=subprocess.STDOUT, env=env)
    for _ in range(300):
        if subprocess.run([pg / 'pg_isready', '-h', '127.0.0.1', '-p', str(port), '-U', 'nativeweave'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0:
            break
        if server.poll() is not None:
            raise RuntimeError('PostgreSQL exited before readiness.')
        time.sleep(.05)
    else:
        raise RuntimeError('PostgreSQL did not become ready before deadline.')
    env['NW_TEST_POSTGRES'] = f'postgresql://nativeweave@127.0.0.1:{port}/postgres'
    project = 'tests/Query.Fluent/NativeWeave.Query.Fluent.Tests.csproj'
    run('restore-managed', ['dotnet', 'restore', project, '--locked-mode', *common, *assets])
    run('build-managed', ['dotnet', 'build', project, '-c', 'Release', '--no-restore', *common, *assets])
    dll = ROOT / 'tests/Query.Fluent/bin/Release/net11.0/NativeWeave.Query.Fluent.Tests.dll'
    run('api-build', ['dotnet', 'build', 'tests/Query.Fluent/ApiCheck/ApiCheck.csproj', '-c', 'Release', *common])
    run('api-reference-negative-consumers', ['dotnet', ROOT / 'tests/Query.Fluent/ApiCheck/bin/Release/net11.0/NativeWeave.Query.Fluent.ApiCheck.dll', ROOT, a.design.resolve(), env['DOTNET_ROOT']])
    run('restore-aot', ['dotnet', 'restore', project, '-r', 'linux-x64', '-p:PublishAot=true', '--locked-mode', *common, *assets])
    run('publish-aot', ['dotnet', 'publish', project, '-c', 'Release', '-r', 'linux-x64', '-p:PublishAot=true', '--no-restore', '-o', out / 'aot', *common, *assets])
    executable = out / 'aot/NativeWeave.Query.Fluent.Tests'
    run('actual-aot', [executable, out / 'aot-results', '--require-aot'])

    feed = out / 'feed'; feed.mkdir()
    helper = importlib.util.spec_from_file_location('package_feed', ROOT / 'tests/Data.Materialization/package_feed.py')
    packer = importlib.util.module_from_spec(helper); helper.loader.exec_module(packer)
    packages = packer.build_feed(ROOT, feed, modules[0], modules[1], run)
    for name in ['Sqlite', 'PostgreSql']:
        package = feed / f'NativeWeave.Database.{name}.0.1.0-preview.1.nupkg'
        asset = 'libnw_database_sqlite.so' if name == 'Sqlite' else 'libnw_database_postgresql.so'
        with zipfile.ZipFile(package) as archive:
            if hashlib.sha256(archive.read('runtimes/linux-x64/native/' + asset)).hexdigest() != sha(modules[0 if name == 'Sqlite' else 1]):
                raise RuntimeError('Package native identity differs from executed component.')
    # Resolve SDK-supplied compiler packages locally into a fresh consumer cache.
    # Package identities/content hashes remain recorded; this is not a pristine-machine claim.
    cache = Path(env['NUGET_PACKAGES'])
    sdk_packages = ['microsoft.net.illink.tasks', 'microsoft.dotnet.ilcompiler', 'runtime.linux-x64.microsoft.dotnet.ilcompiler', 'microsoft.netcore.app.runtime.linux-x64', 'microsoft.aspnetcore.app.runtime.linux-x64', 'microsoft.netcore.app.runtime.nativeaot.linux-x64']
    for name in sdk_packages:
        package = cache / name / '11.0.0' / f'{name}.11.0.0.nupkg'
        shutil.copy2(package, feed / package.name)
    consumer = Path(tempfile.mkdtemp(prefix='B13-NuGet-', dir=ROOT.parent))
    for filename in ['QueryFluent.Sample.csproj', 'Program.cs']:
        shutil.copy2(ROOT / 'samples/api/query-fluent' / filename, consumer / filename)
    shutil.copy2(ROOT / 'global.json', consumer / 'global.json')
    config = consumer / 'NuGet.Config'
    config.write_text('<configuration><packageSources><clear/><add key="local" value="' + str(feed) + '"/></packageSources></configuration>')
    consumer_env = env.copy(); consumer_env['NUGET_PACKAGES'] = str(out / 'consumer-cache')
    consumer_env['CARGO_HOME'] = str(out / 'unused-cargo-home')
    traps = consumer / 'rust-traps'; traps.mkdir()
    rust_marker = consumer / 'unexpected-rust.txt'
    for tool in ['cargo', 'rustc', 'rustup']:
        trap = traps / tool
        trap.write_text("#!/bin/sh\nprintf 'unexpected Rust invocation\\n' >> " + shlex.quote(str(rust_marker)) + "\nexit 197\n")
        trap.chmod(0o755)
    consumer_env['PATH'] = str(traps) + os.pathsep + consumer_env['PATH']
    cp = consumer / 'QueryFluent.Sample.csproj'
    run('consumer-restore', ['dotnet', 'restore', cp, '--configfile', config, *common], consumer, consumer_env)
    run('consumer-build', ['dotnet', 'build', cp, '-c', 'Release', '--no-restore', *common], consumer, consumer_env)
    consumer_dll = consumer / 'bin/Release/net11.0/QueryFluent.Sample.dll'
    run('consumer-restore-aot', ['dotnet', 'restore', cp, '-r', 'linux-x64', '-p:PublishAot=true', '--configfile', config, *common], consumer, consumer_env)
    run('consumer-publish-aot', ['dotnet', 'publish', cp, '-c', 'Release', '-r', 'linux-x64', '-p:PublishAot=true', '--no-restore', '-o', out / 'consumer-aot', *common], consumer, consumer_env)
    consumer_aot = out / 'consumer-aot/QueryFluent.Sample'
    run('consumer-aot', [consumer_aot, '--require-aot'], consumer, consumer_env)
    if rust_marker.exists():
        raise RuntimeError('Ordinary NuGet consumption unexpectedly invoked Rust.')
    write('consumer-identities.json', {'directory': str(consumer), 'project_references': 0,
          'nativeweave_packages': packages, 'consumer_sha256': {f.name: sha(f) for f in consumer.iterdir() if f.is_file()},
          'managed_sha256': sha(consumer_dll), 'aot_sha256': sha(consumer_aot), 'pristine_machine': False, 'no_consumer_rust': True, 'sdk_package_sha256': {name: sha(feed / f'{name}.11.0.0.nupkg') for name in sdk_packages}})
    run('dotnet-identity', ['dotnet', '--info'])
    if source != inputs(ROOT, scopes) or host_source != inputs(a.host_source.resolve(), host_scopes) or native_hashes != {str(m): sha(m) for m in modules}:
        raise RuntimeError('Acceptance input or native artifact changed during execution.')
    write('artifact-identities.json', {'managed_sha256': sha(dll), 'aot_sha256': sha(executable), 'native_sha256': native_hashes,
          'source_inputs_unchanged': True, 'exact_compiled_public_api_xml_and_negative_consumers': True})
    write('status.json', {'result': 'passed', 'platform': 'linux-x64', 'candidate': source['commit'], 'host_candidate': host_source['commit'],
          'commands': len(commands),
          'aot': json.loads((out / 'aot-results/result.json').read_text()), 'isolated_nuget_aot': True,
          'public_facade_owner': 'B13; public namespace source-composed in NativeWeave.Database', 'other_platforms': 'not executed'})
    print('PASS B13 immutable Linux actual Fluent query and isolated NuGet Native AOT', flush=True)
except Exception as failure:
    write('status.json', {'result': 'failed', 'platform': 'linux-x64', 'candidate': source['commit'], 'error': str(failure), 'completed_commands': len(commands)})
    raise
finally:
    if server is not None:
        subprocess.run([str(a.postgres_bin.resolve() / 'pg_ctl'), '-D', str(out / 'postgres-data'), 'stop', '-m', 'fast'], stdout=server_log, stderr=subprocess.STDOUT)
        server.wait(timeout=15)
    if server_log is not None:
        server_log.close()
