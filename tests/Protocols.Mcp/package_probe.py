#!/usr/bin/env python3
"""Actual B19 NuGet consumption; reuse the existing A14 exact packer and SDK task."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
p = argparse.ArgumentParser()
p.add_argument('--output', type=Path, required=True)
p.add_argument('--native', type=Path, required=True)
a = p.parse_args()
out = a.output.resolve(); out.mkdir(parents=True, exist_ok=False)
logs = out / 'logs'; logs.mkdir()
env = os.environ.copy(); commands = []
def sha(path):
    with Path(path).open('rb') as f: return hashlib.file_digest(f, 'sha256').hexdigest()
def run(label, argv, cwd=ROOT, environment=None):
    r = subprocess.run(list(map(str, argv)), cwd=cwd, env=environment or env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=360)
    log = logs / (label + '.log'); log.write_bytes(r.stdout)
    commands.append({'label': label, 'argv': list(map(str, argv)), 'exit_code': r.returncode, 'log_sha256': sha(log)})
    (out / 'commands.json').write_text(json.dumps(commands, indent=2) + '\n')
    print(label, r.returncode, flush=True)
    if r.returncode: raise RuntimeError(r.stdout.decode(errors='replace')[-5000:])
    return r.stdout.decode()
common = ['-m:1', '-nr:false', '-p:UseSharedCompilation=false']
source = run('source', ['git', 'rev-parse', 'HEAD']).strip()
native = a.native.resolve(); native_hash = sha(native)
asset = '-p:NativeWeaveServerAssetPath=' + str(native)
project = ROOT / 'src/Protocols.Mcp/NativeWeave.Server.Mcp.csproj'
run('product-restore', ['dotnet', 'restore', project, '--locked-mode', '--ignore-failed-sources', *common])
run('product-build', ['dotnet', 'build', project, '-c', 'Release', '--no-restore', asset, *common])
task = ROOT / 'src/Packaging.Release/BuildTasks/NativeWeave.Packaging.Tasks.csproj'
run('sdk-task-restore', ['dotnet', 'restore', task, '--locked-mode', '--ignore-failed-sources', *common])
run('sdk-task-build', ['dotnet', 'build', task, '-c', 'Release', '--no-restore', *common])
spec = importlib.util.spec_from_file_location('accepted_a14_packer', ROOT / 'src/Packaging.Linux/packages.py')
packer = importlib.util.module_from_spec(spec); spec.loader.exec_module(packer)
version = '0.1.0-preview.1'
registry = {
 'NativeWeave.Interop.Abi': ('src/Interop.Abi', {}),
 'NativeWeave.Core': ('src/NativeWeave.Core', {'NativeWeave.Interop.Abi': version}),
 'NativeWeave.Server.Generator': ('src/Server.Application/Generator', {}),
 'NativeWeave.Server': ('src/Server.Application', {'NativeWeave.Core': version, 'NativeWeave.Server.Generator': version,
    'Microsoft.Extensions.Configuration': '11.0.0', 'Microsoft.Extensions.Configuration.CommandLine': '11.0.0', 'Microsoft.Extensions.DependencyInjection': '11.0.0', 'Microsoft.Extensions.Logging': '11.0.0'}),
 'NativeWeave.Server.Mcp': ('src/Protocols.Mcp', {'NativeWeave.Server': version}),
}
records = []
for identity, (_, dependencies) in registry.items():
    r = {'id': identity, 'version': version, 'dependencies': {name: v for name, v in dependencies.items() if name.startswith('NativeWeave.')}}
    if identity == 'NativeWeave.Server':
        r['nativeAssets'] = [{'engineId': 'server', 'facadePackageId': identity, 'facadePackageVersion': version, 'rid': 'linux-x64',
          'sourcePath': 'runtimes/linux-x64/native/libnativeweave_server.so', 'sha256': native_hash, 'abiMajor': 1, 'abiMinor': 0,
          'capabilities': {name: bit for bit, name in enumerate(['http1','http2','tls','trailers','grpc','mcp','sse'])}, 'cpuBaseline': 'x86-64'}]
    records.append(r)
feed = out / 'feed'; packages = {}
for identity, (folder, dependencies) in registry.items():
    directory = ROOT / folder; entries = {'LICENSE': ROOT / 'LICENSE'}
    if identity.endswith('.Generator'):
        entries['tools/roslyn/' + identity + '.dll'] = directory / 'bin/Release/netstandard2.0' / (identity + '.dll')
        entries['buildTransitive/' + identity + '.targets'] = directory / 'buildTransitive' / (identity + '.targets')
    else:
        for suffix in ['.dll', '.xml']: entries['lib/net11.0/' + identity + suffix] = directory / 'bin/Release/net11.0' / (identity + suffix)
    if identity == 'NativeWeave.Server':
        entries['runtimes/linux-x64/native/libnativeweave_server.so'] = native
    if identity in ['NativeWeave.Server', 'NativeWeave.Server.Mcp']:
        entries['buildTransitive/' + identity + '.targets'] = ROOT / 'src/Packaging.Release/buildTransitive/NativeWeave.Packaging.targets'
        selected = records if identity.endswith('.Mcp') else [r for r in records if r['id'] != 'NativeWeave.Server.Mcp']
        entries['nativeweave/closure.json'] = json.dumps({'schemaVersion': 1, 'packages': selected}, sort_keys=True).encode()
        for suffix in ['.dll', '.xml']: entries['tools/net11.0/NativeWeave.Packaging.Tasks' + suffix] = task.parent / 'bin/Release/net11.0' / ('NativeWeave.Packaging.Tasks' + suffix)
    package = packer.pack(feed, identity, version, entries, dependencies, ('https://github.com/IzuSharp/NativeWeave', source))
    packages[identity] = {'sha256': sha(package), 'path': str(package)}
(out / 'packages.json').write_text(json.dumps(packages, indent=2) + '\n')
consumer = out / 'consumer'; consumer.mkdir()
for name in ['Program.cs', 'Mcp.Sample.csproj']: shutil.copy2(ROOT / 'samples/api/protocols-mcp' / name, consumer / name)
shutil.copy2(ROOT / 'global.json', consumer / 'global.json')
config = ET.Element('configuration'); sources = ET.SubElement(config, 'packageSources'); ET.SubElement(sources, 'clear')
ET.SubElement(sources, 'add', key='candidate', value=str(feed)); ET.SubElement(sources, 'add', key='nuget.org', value='https://api.nuget.org/v3/index.json')
mapping = ET.SubElement(config, 'packageSourceMapping'); ET.SubElement(mapping, 'clear')
ET.SubElement(ET.SubElement(mapping, 'packageSource', key='candidate'), 'package', pattern='NativeWeave.*')
ET.SubElement(ET.SubElement(mapping, 'packageSource', key='nuget.org'), 'package', pattern='*')
ET.ElementTree(config).write(consumer / 'NuGet.Config')
ce = env.copy(); ce['NUGET_PACKAGES'] = str(consumer / 'packages')
traps = consumer / 'rust-traps'; traps.mkdir(); marker = consumer / 'unexpected-rust.txt'
for name in ['cargo', 'rustc', 'rustup']:
    f = traps / name; f.write_text("#!/bin/sh\nprintf 'unexpected Rust invocation\\n' >> " + shlex.quote(str(marker)) + '\nexit 197\n'); f.chmod(0o755)
ce['PATH'] = str(traps) + os.pathsep + ce['PATH']
cp = consumer / 'Mcp.Sample.csproj'
run('consumer-restore', ['dotnet', 'restore', cp, '--ignore-failed-sources', *common], consumer, ce)
run('consumer-build', ['dotnet', 'build', cp, '-c', 'Release', '--no-restore', *common], consumer, ce)
dll = consumer / 'bin/Release/net11.0/Mcp.Sample.dll'
for mode in ['minimal','advanced']:
    run('consumer-jit-' + mode, ['dotnet', dll, '--self-test', *(['--minimal'] if mode == 'minimal' else [])], consumer, ce)
run('consumer-aot-publish', ['dotnet', 'publish', cp, '-c', 'Release', '-r', 'linux-x64', '-p:PublishAot=true', '--ignore-failed-sources', '-o', consumer / 'publish', *common], consumer, ce)
exe = consumer / 'publish/Mcp.Sample'
for mode in ['minimal','advanced']:
    run('consumer-aot-' + mode, [exe, '--self-test', *(['--minimal'] if mode == 'minimal' else [])], consumer, ce)
program = consumer / 'Program.cs'
program.write_text(program.read_text().replace('internal sealed record AddOutput(int Total, string Tool);', 'internal sealed record AddOutput(int Total, string Tool) { public string Version => "edited DTO"; }'))
run('ordinary-dto-edit-build', ['dotnet', 'build', cp, '-c', 'Release', '--no-restore', *common], consumer, ce)
run('ordinary-dto-edit-execute', ['dotnet', dll, '--self-test'], consumer, ce)
assert not marker.exists(), 'Consumer attempted a Rust tool'
outputs = {}
for mode, directory in [('jit', dll.parent), ('aot', exe.parent)]:
    record = json.loads((directory / 'NativeWeave.server.assets.json').read_text())
    assert record['sha256'] == native_hash == sha(directory / record['outputPath'])
    outputs[mode] = record
(out / 'summary.json').write_text(json.dumps({'source_commit': source, 'cargo_lock_sha256': sha(ROOT / 'Cargo.lock'), 'native_sha256': native_hash,
    'packages': packages, 'sdk_output_records': outputs, 'no_consumer_rust': True, 'transitive_generator': True,
    'minimal_advanced_jit_aot_http1_http2': True, 'ordinary_dto_edit_same_native': True, 'aot_executable_sha256': sha(exe)}, indent=2) + '\n')
print('B19 actual independent NuGet JIT/AOT consumers passed without Rust.', flush=True)
