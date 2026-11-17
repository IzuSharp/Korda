#!/usr/bin/env python3
"""B18 ordinary NuGet/JIT/AOT consumer using the accepted A14 package writer.

The maintainer supplies a frozen Rust Server asset. Consumer processes have only
PackageReference inputs, their own NuGet cache, and no working Rust executable.
This is scoped component proof, not pristine-machine/public-release acceptance.
"""
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
import zipfile


ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--native', type=Path, required=True)
parser.add_argument('--expected-native-sha256', required=True)
parser.add_argument('--native-source-commit', required=True)
args = parser.parse_args()
output = args.output.resolve()
output.mkdir(parents=True, exist_ok=False)
logs = output / 'logs'
logs.mkdir()
commands = []
environment = os.environ.copy()
flags = ['-m:1', '-nr:false', '-p:UseSharedCompilation=false']


def sha(path):
    with Path(path).open('rb') as file:
        return hashlib.file_digest(file, 'sha256').hexdigest()


def run(label, command, cwd=ROOT, env=environment):
    command = list(map(str, command))
    result = subprocess.run(command, cwd=cwd, env=env, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, timeout=900)
    log = logs / (label + '.log')
    log.write_bytes(result.stdout)
    commands.append(dict(label=label, command=command, exit_code=result.returncode,
                         log_sha256=sha(log)))
    (output / 'commands.json').write_text(json.dumps(commands, indent=2) + '\n')
    print(label, result.returncode, flush=True)
    if result.returncode:
        raise RuntimeError(result.stdout.decode(errors='replace')[-8000:])
    return result.stdout.decode()


source = run('source', ['git', 'rev-parse', 'HEAD']).strip()
assert not run('source-status', ['git', 'status', '--porcelain']), 'Freeze committed source first.'
native_source = run('native-source', ['git', 'rev-parse', args.native_source_commit + '^{commit}']).strip()
native_paths = subprocess.check_output(['git', 'ls-tree', '-r', '--name-only', native_source, 'native', '.cargo', 'Cargo.toml', 'Cargo.lock', 'rust-toolchain.toml'], cwd=ROOT, text=True).splitlines()
native_inputs = {path: hashlib.sha256(subprocess.check_output(['git', 'show', native_source + ':' + path], cwd=ROOT)).hexdigest() for path in native_paths}
supplied = args.native.resolve()
native_hash = sha(supplied)
assert native_hash == args.expected_native_sha256.lower(), 'The supplied native asset is not the reviewed asset.'
(output / 'native').mkdir()
native = output / 'native/libnativeweave_server.so'
shutil.copy2(supplied, native)
assert sha(native) == native_hash
asset = '-p:NativeWeaveServerAssetPath=' + str(native)
project = ROOT / 'src/Protocols.Grpc/NativeWeave.Server.Grpc.csproj'
run('product-restore', ['dotnet', 'restore', project, '--locked-mode', '--ignore-failed-sources', '--disable-parallel', *flags])
run('product-build', ['dotnet', 'build', project, '-c', 'Release', '--no-restore', asset, *flags])
task = ROOT / 'src/Packaging.Release/BuildTasks/NativeWeave.Packaging.Tasks.csproj'
run('sdk-task-restore', ['dotnet', 'restore', task, '--locked-mode', '--ignore-failed-sources', '--disable-parallel', *flags])
run('sdk-task-build', ['dotnet', 'build', task, '-c', 'Release', '--no-restore', *flags])

spec = importlib.util.spec_from_file_location('accepted_a14_packages', ROOT / 'src/Packaging.Linux/packages.py')
packer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packer)
version = '0.1.0-preview.1'
external = {item.attrib['Include']: item.attrib['Version']
            for item in ET.parse(ROOT / 'src/Server.Application/NativeWeave.Server.csproj').iter('PackageReference')}
registry = {
    'NativeWeave.Interop.Abi': ('src/Interop.Abi', {}),
    'NativeWeave.Core': ('src/NativeWeave.Core', {'NativeWeave.Interop.Abi': version}),
    'NativeWeave.Server.Generator': ('src/Server.Application/Generator', {}),
    'NativeWeave.Server': ('src/Server.Application', {
        'NativeWeave.Core': version, 'NativeWeave.Server.Generator': version, **external}),
    'NativeWeave.Server.Grpc': ('src/Protocols.Grpc', {'NativeWeave.Server': version}),
}
closure = []
for identity, (_, dependencies) in registry.items():
    record = dict(id=identity, version=version, dependencies={name: value for name, value in dependencies.items() if name.startswith('NativeWeave.')})
    if identity == 'NativeWeave.Server':
        record['nativeAssets'] = [dict(
            engineId='server', facadePackageId=identity, facadePackageVersion=version,
            rid='linux-x64', sourcePath='runtimes/linux-x64/native/libnativeweave_server.so',
            sha256=native_hash, abiMajor=1, abiMinor=0,
            capabilities={name: bit for bit, name in enumerate(['http1', 'http2', 'tls', 'trailers', 'grpc', 'mcp', 'sse'])},
            cpuBaseline='x86-64')]
    closure.append(record)
feed = output / 'feed'
packages = {}
for identity, (folder, dependencies) in registry.items():
    directory = ROOT / folder
    entries = {'LICENSE': ROOT / 'LICENSE'}
    if identity.endswith('.Generator'):
        entries['tools/roslyn/' + identity + '.dll'] = directory / 'bin/Release/netstandard2.0' / (identity + '.dll')
        entries['buildTransitive/' + identity + '.targets'] = directory / 'buildTransitive' / (identity + '.targets')
    else:
        for suffix in ['.dll', '.xml']:
            entries['lib/net11.0/' + identity + suffix] = directory / 'bin/Release/net11.0' / (identity + suffix)
    if identity == 'NativeWeave.Server':
        entries['runtimes/linux-x64/native/libnativeweave_server.so'] = native
    if identity in ('NativeWeave.Server', 'NativeWeave.Server.Grpc'):
        entries['buildTransitive/' + identity + '.targets'] = ROOT / 'src/Packaging.Release/buildTransitive/NativeWeave.Packaging.targets'
        selected = closure if identity.endswith('.Grpc') else [entry for entry in closure if not entry['id'].endswith('.Grpc')]
        entries['nativeweave/closure.json'] = json.dumps(dict(schemaVersion=1, packages=selected), sort_keys=True).encode()
        for suffix in ['.dll', '.xml']:
            entries['tools/net11.0/NativeWeave.Packaging.Tasks' + suffix] = task.parent / 'bin/Release/net11.0' / ('NativeWeave.Packaging.Tasks' + suffix)
    file = packer.pack(feed, identity, version, entries, dependencies, ('https://github.com/IzuSharp/NativeWeave', source))
    with zipfile.ZipFile(file) as archive:
        document = ET.fromstring(archive.read(identity + '.nuspec'))
        actual = {item.attrib['id']: item.attrib['version'] for item in document.iter() if item.tag.endswith('}dependency')}
        assert actual == {name: '[' + value + ']' for name, value in dependencies.items()}
    packages[identity] = dict(path=str(file), sha256=sha(file), exact_dependencies=actual)
(output / 'packages.json').write_text(json.dumps(packages, indent=2) + '\n')

consumer = output / 'consumer'
consumer.mkdir()
sample = ROOT / 'samples/api/protocols-grpc'
for file in sample.iterdir():
    if file.suffix in ('.cs', '.proto'):
        shutil.copy2(file, consumer / file.name)
shutil.copy2(ROOT / 'global.json', consumer / 'global.json')
# Explicit boundaries stop the checkout's Directory.Build imports. The project
# below has no ProjectReference, special resolver, root descriptor or suppression.
(consumer / 'Directory.Build.props').write_text('<Project />\n')
(consumer / 'Directory.Build.targets').write_text('<Project />\n')
(consumer / 'Consumer.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><LangVersion>15.0</LangVersion>
    <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><IsAotCompatible>true</IsAotCompatible>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
    <NuGetAudit>true</NuGetAudit><NuGetAuditMode>all</NuGetAuditMode>
    <NuGetLockFilePath Condition="'$(PublishAot)' == 'true'">$(MSBuildProjectDirectory)/packages.aot.lock.json</NuGetLockFilePath>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="NativeWeave.Server.Grpc" Version="[0.1.0-preview.1]" />
    <PackageReference Include="Google.Protobuf" Version="[3.36.2]" />
    <PackageReference Include="Grpc.Tools" Version="[2.85.0]" PrivateAssets="All" />
    <PackageReference Include="Grpc.Net.Client" Version="[2.85.0]" />
    <Protobuf Include="echo.proto;counter.proto" GrpcServices="Client" />
  </ItemGroup>
</Project>
''')
config = ET.Element('configuration')
sources = ET.SubElement(config, 'packageSources')
ET.SubElement(sources, 'clear')
ET.SubElement(sources, 'add', key='candidate', value=str(feed))
ET.SubElement(sources, 'add', key='nuget.org', value='https://api.nuget.org/v3/index.json')
mapping = ET.SubElement(config, 'packageSourceMapping')
ET.SubElement(mapping, 'clear')
ET.SubElement(ET.SubElement(mapping, 'packageSource', key='candidate'), 'package', pattern='NativeWeave.*')
ET.SubElement(ET.SubElement(mapping, 'packageSource', key='nuget.org'), 'package', pattern='*')
ET.ElementTree(config).write(consumer / 'NuGet.Config')
env = environment.copy()
env['NUGET_PACKAGES'] = str(consumer / 'packages')
env['PATH'] = os.pathsep.join(entry for entry in env['PATH'].split(os.pathsep)
                            if not any((Path(entry) / name).exists() for name in ('cargo', 'rustc', 'rustup')))
assert all(shutil.which(name, path=env['PATH']) is None for name in ('cargo', 'rustc', 'rustup'))
traps = consumer / 'rust-traps'
traps.mkdir()
marker = consumer / 'unexpected-rust.txt'
for name in ('cargo', 'rustc', 'rustup'):
    file = traps / name
    file.write_text("#!/bin/sh\nprintf 'unexpected Rust invocation\\n' >> " + shlex.quote(str(marker)) + '\nexit 197\n')
    file.chmod(0o755)
env['PATH'] = str(traps) + os.pathsep + env['PATH']


def snapshot(name, folder):
    destination = output / 'snapshots' / name
    destination.mkdir(parents=True)
    for file in consumer.iterdir():
        if file.is_file():
            shutil.copy2(file, destination / file.name)
    files = {str(file.relative_to(folder)): sha(file) for file in sorted(folder.rglob('*')) if file.is_file()}
    (destination / 'binary-inputs.json').write_text(json.dumps(files, indent=2) + '\n')
    return files


def check_outputs(folder):
    record = json.loads((folder / 'NativeWeave.server.assets.json').read_text())
    assert record['sha256'] == native_hash == sha(folder / record['outputPath'])
    assert not marker.exists(), 'A consumer attempted to invoke a Rust tool.'
    assert sha(native) == native_hash
    return record


def exercise(label, command, aot):
    extra = ['--require-aot'] if aot else []
    run(label + '-minimal', [*command, '--minimal', *extra], consumer, env)
    run(label + '-advanced-clear', [*command, *extra], consumer, env)
    run(label + '-advanced-tls', [*command, '--tls', *extra], consumer, env)


project = consumer / 'Consumer.csproj'
run('consumer-restore', ['dotnet', 'restore', project, '--ignore-failed-sources', '--disable-parallel', *flags], consumer, env)
run('consumer-locked-restore', ['dotnet', 'restore', project, '--locked-mode', '--ignore-failed-sources', '--disable-parallel', *flags], consumer, env)
run('consumer-build', ['dotnet', 'build', project, '-c', 'Release', '--no-restore', *flags], consumer, env)
dll = consumer / 'bin/Release/net11.0/Consumer.dll'
jit = snapshot('jit', dll.parent)
exercise('consumer-jit', ['dotnet', dll], False)
jit_record = check_outputs(dll.parent)
assert all(sha(dll.parent / path) == digest for path, digest in jit.items())
run('consumer-aot-restore', ['dotnet', 'restore', project, '-r', 'linux-x64', '-p:PublishAot=true', '--ignore-failed-sources', '--disable-parallel', *flags], consumer, env)
run('consumer-aot-locked-restore', ['dotnet', 'restore', project, '-r', 'linux-x64', '-p:PublishAot=true', '--locked-mode', '--ignore-failed-sources', '--disable-parallel', *flags], consumer, env)
run('consumer-aot-publish', ['dotnet', 'publish', project, '-c', 'Release', '-r', 'linux-x64', '-p:PublishAot=true', '--no-restore', '-o', consumer / 'publish', *flags], consumer, env)
exe = consumer / 'publish/Consumer'
aot = snapshot('aot', exe.parent)
exercise('consumer-aot', [exe], True)
aot_record = check_outputs(exe.parent)
assert all(sha(exe.parent / path) == digest for path, digest in aot.items())

# Regenerate a real DTO field and update the typed handler/client. No package or
# native input changes; the copied ordinary project does all protobuf generation.
proto = consumer / 'echo.proto'
text = proto.read_text()
assert text.count('int32 count = 3;') == text.count('int32 index = 3;') == 1
proto.write_text(text.replace('int32 count = 3;', 'int32 count = 3;\n  string tag = 4;')
                .replace('int32 index = 3;', 'int32 index = 3;\n  string tag = 4;'))
services = consumer / 'Services.cs'
text = services.read_text()
assert text.count('Index = index };') == 1
services.write_text(text.replace('Index = index };', 'Index = index, Tag = request.Tag };'))
client = consumer / 'ClientDemo.cs'
text = client.read_text()
before = 'Payload = ByteString.CopyFrom([0, 1, 255]) };'
assert text.count(before) == 1
text = text.replace(before, 'Payload = ByteString.CopyFrom([0, 1, 255]), Tag = "ordinary managed DTO edit" };')
before = 'result.Payload.Equals(request.Payload), "typed generated protobuf unary round trip"'
assert text.count(before) == 1
client.write_text(text.replace(before, 'result.Payload.Equals(request.Payload) && result.Tag == request.Tag, "edited generated DTO and typed handler round trip"'))
run('edited-jit-restore', ['dotnet', 'restore', project, '--locked-mode', '--ignore-failed-sources', '--disable-parallel', *flags], consumer, env)
run('edited-jit-build', ['dotnet', 'build', project, '-c', 'Release', '--no-restore', *flags], consumer, env)
edited_jit = snapshot('edited-jit', dll.parent)
run('edited-jit-execute', ['dotnet', dll, '--tls'], consumer, env)
edited_jit_record = check_outputs(dll.parent)
assert all(sha(dll.parent / path) == digest for path, digest in edited_jit.items())
run('edited-aot-restore', ['dotnet', 'restore', project, '-r', 'linux-x64', '-p:PublishAot=true', '--locked-mode', '--ignore-failed-sources', '--disable-parallel', *flags], consumer, env)
run('edited-aot-publish', ['dotnet', 'publish', project, '-c', 'Release', '-r', 'linux-x64', '-p:PublishAot=true', '--no-restore', '-o', consumer / 'publish-edited', *flags], consumer, env)
edited_exe = consumer / 'publish-edited/Consumer'
edited_aot = snapshot('edited-aot', edited_exe.parent)
run('edited-aot-execute', [edited_exe, '--tls', '--require-aot'], consumer, env)
edited_aot_record = check_outputs(edited_exe.parent)
assert all(sha(edited_exe.parent / path) == digest for path, digest in edited_aot.items())
assert jit['Consumer.dll'] != edited_jit['Consumer.dll'] and aot['Consumer'] != edited_aot['Consumer']
assert all(sha(Path(package['path'])) == package['sha256'] for package in packages.values())
summary = dict(
    task='B18', platform='linux-x64', source_commit=source,
    native_source_commit=native_source, native_input_sha256=native_inputs,
    cargo_lock_sha256=sha(ROOT / 'Cargo.lock'), native_sha256=native_hash,
    packages=packages, selected_output_records=dict(jit=jit_record, aot=aot_record,
        edited_jit=edited_jit_record, edited_aot=edited_aot_record),
    package_reference_only=True, no_parent_repository_imports=True,
    isolated_nuget_cache=True, no_accessible_rust_toolchain=True, rust_trap_fired=False,
    static_protobuf_services=2, all_four_cardinalities_jit_aot_http2_tls=True,
    whole_call_deadline_after_headers=True, binary_metadata_and_status_trailers=True,
    explicit_aot_no_dynamic_code=True, no_root_descriptors_or_trim_suppressions=True,
    ordinary_proto_dto_handler_edit_jit_aot_same_native=True,
    executable_sha256=dict(jit=jit['Consumer.dll'], aot=aot['Consumer'],
        edited_jit=edited_jit['Consumer.dll'], edited_aot=edited_aot['Consumer']),
    command_receipt_sha256=sha(output / 'commands.json'),
    scope='Actual component NuGet consumption over the reviewed Server native asset; not pristine-machine or public-release acceptance.')
(output / 'summary.json').write_text(json.dumps(summary, indent=2) + '\n')
print('PASS B18 independent NuGet two-service JIT/AOT, generated DTO edit, unchanged native asset and no Rust tools.', flush=True)
