#!/usr/bin/env python3
"""B14 existing package graph, public sample and frozen native assets in Native AOT."""
import argparse
import atexit
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
p = argparse.ArgumentParser()
p.add_argument('--output', type=Path, required=True)
p.add_argument('--native-dir', type=Path, required=True)
p.add_argument('--postgres-bin', type=Path)
a = p.parse_args()
out = a.output.resolve(); out.mkdir(parents=True, exist_ok=False)
logs = out / 'logs'; logs.mkdir()
records = []
sha = lambda path: hashlib.sha256(Path(path).read_bytes()).hexdigest()
common = ['-m:1', '-nr:false', '-p:UseSharedCompilation=false']
env = os.environ.copy()
if a.postgres_bin:
    pg = a.postgres_bin.resolve(); data = out / 'postgres-data'
    with (out / 'postgres-init.log').open('w') as log:
        subprocess.run([pg / 'initdb', '-D', data, '-U', 'nativeweave', '--auth=trust', '--no-locale', '--encoding=UTF8'], stdout=log, stderr=subprocess.STDOUT, check=True)
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0)); port = listener.getsockname()[1]
    server_log = (out / 'postgres.log').open('w')
    server = subprocess.Popen([pg / 'postgres', '-D', data, '-h', '127.0.0.1', '-p', str(port), '-k', ''], stdout=server_log, stderr=subprocess.STDOUT)
    def stop_server():
        server.terminate()
        try: server.wait(timeout=15)
        except subprocess.TimeoutExpired: server.kill(); server.wait()
        server_log.close()
    atexit.register(stop_server)
    import time
    for _ in range(300):
        if subprocess.run([pg / 'pg_isready', '-h', '127.0.0.1', '-p', str(port), '-U', 'nativeweave'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0: break
        if server.poll() is not None: raise RuntimeError('PostgreSQL exited during startup')
        time.sleep(.05)
    else: raise RuntimeError('PostgreSQL readiness deadline')
    env['NW_TEST_POSTGRES'] = f'postgresql://nativeweave@127.0.0.1:{port}/postgres'
assert env.get('NW_TEST_POSTGRES'), 'Use an explicit isolated PostgreSQL test server.'

def run(label, argv, cwd=ROOT, environment=env):
    print(label, flush=True)
    file = logs / (label + '.log')
    with file.open('w') as stream:
        result = subprocess.run(list(map(str, argv)), cwd=cwd, env=environment, stdout=stream, stderr=subprocess.STDOUT, timeout=900)
    records.append(dict(label=label, command=list(map(str, argv)), exit_code=result.returncode, log_sha256=sha(file)))
    (out / 'commands.json').write_text(json.dumps(records, indent=2) + '\n')
    if result.returncode:
        raise RuntimeError(label + ': ' + file.read_text()[-5000:])

source = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
names = subprocess.check_output(['git', 'ls-files', '--', 'src', 'tests/Orm.Relations', 'samples/api/orm-relations', 'Directory.Build.props', 'Directory.Build.targets', 'global.json'], cwd=ROOT, text=True).splitlines()
inputs = {name: sha(ROOT / name) for name in names}
native = [a.native_dir.resolve() / ('libnw_database_' + provider + '.so') for provider in ['sqlite', 'postgresql']]
native_hashes = {file.name: sha(file) for file in native}
assets = ['-p:NativeWeaveSqliteAssetPath=' + str(native[0]), '-p:NativeWeavePostgresAssetPath=' + str(native[1])]
sample = ROOT / 'samples/api/orm-relations'
run('product-build', ['dotnet', 'build', sample / 'NativeWeave.Orm.Relations.Sample.csproj', '-c', 'Release', '-p:RestoreLockedMode=true', *common, *assets])
spec = importlib.util.spec_from_file_location('accepted_package_feed', ROOT / 'tests/Data.Materialization/package_feed.py')
packer = importlib.util.module_from_spec(spec); spec.loader.exec_module(packer)
feed = out / 'feed'
packages = packer.build_feed(ROOT, feed, native[0], native[1], run)
consumer = out / 'consumer'; consumer.mkdir()
for name in ['Program.cs', 'Advanced.cs']:
    shutil.copy2(sample / name, consumer / name)
program = consumer / 'Program.cs'
program.write_text('using System.Runtime.CompilerServices;\n' + program.read_text().replace('var postgres =', 'if (args.Contains("--require-aot") && RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("Actual AOT required.");\nConsole.WriteLine("DynamicCode=" + RuntimeFeature.IsDynamicCodeSupported);\nvar postgres =', 1))
shutil.copy2(ROOT / 'global.json', consumer / 'global.json')
for name in ['Directory.Build.props', 'Directory.Build.targets']:
    (consumer / name).write_text('<Project />\n')
(consumer / 'Consumer.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><LangVersion>15.0</LangVersion><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><IsAotCompatible>true</IsAotCompatible><TreatWarningsAsErrors>true</TreatWarningsAsErrors><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile><NuGetLockFilePath Condition="'$(PublishAot)' == 'true'">$(MSBuildProjectDirectory)/packages.aot.lock.json</NuGetLockFilePath></PropertyGroup><ItemGroup><PackageReference Include="NativeWeave.Database.Sqlite" Version="[0.1.0-preview.1]"/><PackageReference Include="NativeWeave.Database.PostgreSql" Version="[0.1.0-preview.1]"/></ItemGroup></Project>\n''')
config = ET.Element('configuration'); sources = ET.SubElement(config, 'packageSources'); ET.SubElement(sources, 'clear')
ET.SubElement(sources, 'add', key='candidate', value=str(feed)); ET.SubElement(sources, 'add', key='nuget.org', value='https://api.nuget.org/v3/index.json')
mapping = ET.SubElement(config, 'packageSourceMapping'); ET.SubElement(mapping, 'clear')
ET.SubElement(ET.SubElement(mapping, 'packageSource', key='candidate'), 'package', pattern='NativeWeave.*')
ET.SubElement(ET.SubElement(mapping, 'packageSource', key='nuget.org'), 'package', pattern='*')
ET.ElementTree(config).write(consumer / 'NuGet.Config')
isolated = env.copy(); isolated['NUGET_PACKAGES'] = str(out / 'consumer-cache')
isolated['PATH'] = os.pathsep.join(entry for entry in env['PATH'].split(os.pathsep) if not any((Path(entry) / name).exists() for name in ['cargo', 'rustc', 'rustup']))
assert all(shutil.which(name, path=isolated['PATH']) is None for name in ['cargo', 'rustc', 'rustup'])
run('consumer-restore', ['dotnet', 'restore', 'Consumer.csproj', '--disable-parallel', *common], consumer, isolated)
run('consumer-build', ['dotnet', 'build', 'Consumer.csproj', '-c', 'Release', '--no-restore', *common], consumer, isolated)
dll = consumer / 'bin/Release/net11.0/Consumer.dll'
run('consumer-aot-restore', ['dotnet', 'restore', 'Consumer.csproj', '-r', 'linux-x64', '-p:PublishAot=true', '--disable-parallel', *common], consumer, isolated)
run('consumer-aot-publish', ['dotnet', 'publish', 'Consumer.csproj', '-c', 'Release', '-r', 'linux-x64', '-p:PublishAot=true', '--no-restore', '-o', out / 'aot', *common], consumer, isolated)
exe = out / 'aot/Consumer'
for provider in ['sqlite', 'postgresql']:
    run('consumer-aot-' + provider, [exe, provider, out / ('aot-' + provider + '.db'), '--require-aot'], consumer, isolated)
outputs = {}
for mode, folder in [('aot', exe.parent)]:
    outputs[mode] = {}
    for provider in ['sqlite', 'postgresql']:
        manifest = json.loads((folder / ('NativeWeave.database.' + provider + '.assets.json')).read_text())
        assert manifest['sha256'] == native_hashes[Path(manifest['outputPath']).name] == sha(folder / manifest['outputPath'])
        outputs[mode][provider] = manifest
assert {name: sha(ROOT / name) for name in names} == inputs
assert {file.name: sha(file) for file in native} == native_hashes
(out / 'summary.json').write_text(json.dumps(dict(task='B14', platform='linux-x64', source_commit=source, source_sha256=inputs, native_sha256=native_hashes, packages=packages, no_consumer_rust=True, no_parent_repository_imports=True, project_references=0, aot_providers=['SQLite', 'PostgreSQL18'], aot_sha256=sha(exe), output_manifests=outputs, consumer_files_sha256={str(file.relative_to(consumer)):sha(file) for file in consumer.iterdir() if file.is_file()}), indent=2) + '\n')
print('PASS B14 independent exact NuGet sample: both real providers in Native AOT; no Rust available.', flush=True)
