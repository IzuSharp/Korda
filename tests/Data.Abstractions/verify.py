#!/usr/bin/env python3
"""Verify B01's own Linux contracts and isolated package consumer, without invoking other tasks.

Use --offline-packages with a directory containing the pinned .NET 11.0.0 nupkg
archives (including a hierarchical global-package cache). All NuGet sources are
then local, audit is unavailable, and no remote restore is attempted.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[2]
p = argparse.ArgumentParser()
p.add_argument('--output', type=Path, default=ROOT / 'artifacts/B01/verification')
p.add_argument('--native-build', type=Path, default=ROOT / 'artifacts/cmake/linux-x64-release')
p.add_argument('--dotnet', default=shutil.which('dotnet'))
p.add_argument('--offline-packages', type=Path)
p.add_argument('--write-reference', action='store_true')
a = p.parse_args()
if platform.system() != 'Linux' or platform.machine() not in ('x86_64', 'AMD64'):
    raise SystemExit('This verification invocation is specifically Linux x64.')
if not a.dotnet:
    raise SystemExit('Use the repository-pinned .NET SDK 11.0.100 on PATH or --dotnet.')
DOTNET = str(Path(a.dotnet).resolve())
SDK = Path(DOTNET).parent
OUT = a.output.resolve()
OUT.mkdir(parents=True, exist_ok=True)
if (OUT / 'commands.json').exists():
    raise SystemExit('Use a fresh --output directory; prior evidence is retained.')
env = os.environ.copy()
env.update({'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1', 'DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE': 'true', 'DOTNET_CLI_USE_MSBUILD_SERVER': '0', 'MSBUILDUSESERVER': '0', 'DOTNET_ROOT': str(SDK)})
commands = []
def write_json(name, value):
    (OUT / name).write_text(json.dumps(value, indent=2) + '\n')
def run(name, args, cwd=ROOT, extra_env=None, fail=False):
    effective = env | (extra_env or {})
    r = subprocess.run([str(v) for v in args], cwd=cwd, env=effective, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    (OUT / (name + '.log')).write_text(r.stdout)
    commands.append({'name': name, 'argv': [str(v) for v in args], 'exit_code': r.returncode, 'expected_failure': fail, 'log': name + '.log'})
    write_json('commands.json', commands)
    print(f'{name}: exit {r.returncode}', flush=True)
    if (r.returncode == 0) == fail:
        raise RuntimeError(f'{name}: unexpected result; see {OUT / (name + ".log")}')
    return r.stdout

def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()

def config(path, feeds):
    node = ET.Element('configuration'); sources = ET.SubElement(node, 'packageSources'); ET.SubElement(sources, 'clear')
    mappings = ET.SubElement(node, 'packageSourceMapping'); ET.SubElement(mappings, 'clear')
    for index, feed in enumerate(feeds):
        key = 'local' + str(index); ET.SubElement(sources, 'add', {'key': key, 'value': str(feed)})
        mapping = ET.SubElement(mappings, 'packageSource', {'key': key}); ET.SubElement(mapping, 'package', {'pattern': '*'})
    ET.ElementTree(node).write(path, encoding='unicode')

feed = OUT / 'offline-feed'
if a.offline_packages:
    feed.mkdir(exist_ok=True)
    allowed = {'microsoft.net.illink.tasks', 'microsoft.dotnet.ilcompiler', 'runtime.linux-x64.microsoft.dotnet.ilcompiler', 'microsoft.netcore.app.runtime.linux-x64', 'microsoft.netcore.app.runtime.nativeaot.linux-x64', 'microsoft.aspnetcore.app.runtime.linux-x64'}
    copied = []
    for archive in a.offline_packages.resolve().rglob('*.nupkg'):
        if archive.name.removesuffix('.11.0.0.nupkg').lower() in allowed:
            shutil.copy2(archive, feed / archive.name); copied.append({'name': archive.name, 'sha256': sha(archive)})
    if len(copied) < 6:
        raise SystemExit('The local cache is missing a pinned .NET 11 AOT/runtime pack.')
    write_json('offline-packs.json', copied)
    nuget = OUT / 'NuGet.offline.config'; config(nuget, [feed])
    restore_options = ['--configfile', nuget, '-p:NuGetAudit=false']
else:
    restore_options = []

toolchain = {'platform': platform.platform(), 'architecture': platform.machine(), 'os_release': Path('/etc/os-release').read_text()}
toolchain['sdk'] = run('sdk', [DOTNET, '--version']).strip()
if (OUT / 'sdk.log').read_text().strip() != '11.0.100':
    raise RuntimeError('The SDK must be the fixed 11.0.100 baseline.')
toolchain['sdk_info'] = run('sdk-info', [DOTNET, '--info'])
native_build = a.native_build.resolve()
provenance = native_build / 'compiler-provenance.txt'
if not provenance.is_file():
    raise RuntimeError('Configure and build the shared linux-x64-release CMake preset first.')
toolchain['native_provenance'] = provenance.read_text()
if '\nID=Clang\nVERSION=23.1.5\n' not in toolchain['native_provenance']:
    raise RuntimeError('The native compiler must be the pinned LLVM Clang 23.1.5.')
toolchain['clang'] = run('clang', ['clang++', '--version'])
if not toolchain['clang'].startswith('clang version 23.1.5 '):
    raise RuntimeError('PATH must select the same pinned native toolchain as CMake.')
toolchain['clang_target'] = run('clang-target', ['clang++', '-dumpmachine']).strip()
toolchain['cmake'] = run('cmake', ['cmake', '--version'])
toolchain['ninja'] = run('ninja', ['ninja', '--version'])
run('schema', ['python3', 'native/database/interfaces/generate_schema.py', '--check'])
managed = 'tests/Data.Abstractions/Korda.Data.Tests.csproj'
api = 'tests/Data.Abstractions/ApiCheck/ApiCheck.csproj'
library = 'src/Data.Abstractions/Korda.Data.csproj'
for label, project in [('library', library), ('api', api)]:
    run(label + '-restore', [DOTNET, 'restore', project, '--locked-mode', '--disable-build-servers', *restore_options])
    run(label + '-build', [DOTNET, 'build', project, '-c', 'Release', '--no-restore', '--disable-build-servers'])
api_result = json.loads(run('api-exact-xml-reference', [DOTNET, ROOT / 'tests/Data.Abstractions/ApiCheck/bin/Release/net11.0/ApiCheck.dll', ROOT, SDK, ROOT / 'src/Data.Abstractions/bin/Release/net11.0/Korda.Data.dll', *(['--write'] if a.write_reference else [])]))

# The unchanged managed golden encoder is consumed by the production C++
# contract implementation, also registered in the shared native CTest preset.
native_binary = native_build / 'tests/nw_contracts_tests'
if not native_binary.is_file():
    raise RuntimeError('Build the nw_contracts_tests CMake target before B01 verification.')
run('native-format', ['clang-format', '--dry-run', '--Werror',
    'native/cpp/include/nw/contracts.hpp', 'native/cpp/src/contracts.cpp',
    'native/cpp/tests/contracts_tests.cpp'])
run('native-ctest', ['ctest', '--test-dir', native_build, '--output-on-failure',
                    '--no-tests=error', '-R', r'^native\.contracts$'])
toolchain['native_artifact_comment'] = run('native-artifact-comment', ['readelf', '-p', '.comment', native_binary])
toolchain['native_dynamic_dependencies'] = run('native-dependencies', ['readelf', '-d', native_binary])
toolchain['native_binary_sha256'] = sha(native_binary)
toolchain['native_provenance_sha256'] = sha(provenance)
write_json('toolchain.json', toolchain)
dependency_lock = ROOT / 'native/dependencies.lock.json'
locked = json.loads(dependency_lock.read_text())
write_json('dependency-licenses.json', {
    'lock_sha256': sha(dependency_lock),
    'notice_sha256': sha(ROOT / 'THIRD_PARTY_NOTICES.md'),
    'compiled_contract_dependencies': [item for item in locked['dependencies'] if item['name'] == 'nlohmann-json'],
    'cxx_runtime': locked['runtime'],
    'system_crypto': {'name': 'OpenSSL', 'license': 'Apache-2.0',
                      'version': run('openssl-version', ['openssl', 'version']),
                      'notice_sha256': sha(ROOT / 'native/cpp/licenses/openssl-LICENSE.txt')},
    'scope': 'B01 contract executable; database-provider dependencies are recorded by the product build inventory.'})

run('data-pack', [DOTNET, 'pack', 'src/Data.Abstractions/Korda.Data.csproj', '-c', 'Release', '--no-build', '--no-restore', '--output', OUT / 'packages', '--disable-build-servers'])
packages = list((OUT / 'packages').glob('*.nupkg'))
if len(packages) != 1:
    raise RuntimeError('Expected exactly the independent Korda.Data package.')
with zipfile.ZipFile(packages[0]) as archive:
    names = archive.namelist()
    if any(n.startswith(('runtimes/', 'build/', 'buildTransitive/')) for n in names):
        raise RuntimeError('Data abstractions must not load a runtime or invoke a consumer build tool.')
    nuspec_name = next(n for n in names if n.endswith('.nuspec'))
    nuspec = ET.fromstring(archive.read(nuspec_name))
    versions = [n.text for n in nuspec.iter() if n.tag.rsplit('}', 1)[-1] == 'version']
    package_version = versions[0]
    dependencies = [n.attrib for n in nuspec.iter() if n.tag.rsplit('}', 1)[-1] == 'dependency']
    if dependencies:
        raise RuntimeError('The implemented B01 assembly requires only BCL APIs; unexpected runtime dependency.')
    write_json('package-inspection.json', {'package': packages[0].name, 'sha256': sha(packages[0]), 'entries': names, 'runtime_dependencies': dependencies, 'no_native_assets_or_build_hooks': True})

# Package consumption is outside the source checkout. The trap proves these invocations
# did not launch Rust; it does not claim a pristine machine (A16/B24 own that gate).
with tempfile.TemporaryDirectory(prefix='korda-b01-consumer-') as temporary:
    consumer = Path(temporary)
    shutil.copy2(ROOT / 'global.json', consumer / 'global.json')
    shutil.copy2(ROOT / 'samples/api/data-abstractions/Program.cs', consumer / 'Program.cs')
    (consumer / 'Consumer.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><LangVersion>15.0</LangVersion><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile></PropertyGroup><ItemGroup><PackageReference Include="Korda.Data" Version="{package_version}" /></ItemGroup></Project>''')
    consumer_config = consumer / 'NuGet.Config'
    feeds = [OUT / 'packages', feed] if a.offline_packages else [OUT / 'packages', 'https://api.nuget.org/v3/index.json']
    config(consumer_config, feeds)
    traps = consumer / 'traps'; traps.mkdir(); trap_log = OUT / 'rust-consumer-trap.log'
    for name in ('cargo', 'rustc', 'rustup'):
        trap = traps / name
        trap.write_text('#!/bin/sh\nprintf "Rust invocation rejected\\n" >> "$B01_RUST_TRAP_LOG"\nexit 86\n')
        trap.chmod(0o755)
    consumer_env = {'PATH': str(traps) + os.pathsep + str(SDK) + os.pathsep + env['PATH'], 'B01_RUST_TRAP_LOG': str(trap_log), 'NUGET_PACKAGES': str(consumer / 'packages')}
    offline = ['-p:NuGetAudit=false'] if a.offline_packages else []
    run('package-consumer-aot-restore', [DOTNET, 'restore', '-r', 'linux-x64', '-p:PublishAot=true', '--configfile', consumer_config, *offline, '--disable-build-servers'], consumer, consumer_env)
    selection = json.loads(run('aot-linker-selection', [DOTNET, 'msbuild', 'Consumer.csproj', '-nologo', '-t:ResolveReferences,SetupOSSpecificProps', '-p:RuntimeIdentifier=linux-x64', '-p:PublishAot=true', '-getProperty:CppCompilerAndLinker,CppLinker,LinkerFlavor'], consumer, consumer_env))['Properties']
    driver = Path(shutil.which(selection['CppLinker']) or selection['CppLinker']).resolve()
    selected = 'ld.' + selection['LinkerFlavor'] if selection['LinkerFlavor'] else 'ld'
    linker_name = run('aot-driver-linker-selection', [driver, '-print-prog-name=' + selected]).strip()
    linker = Path(shutil.which(linker_name) or linker_name).resolve()
    toolchain['aot_linker'] = {'selection': selection, 'driver': str(driver), 'driver_sha256': sha(driver), 'driver_version': run('aot-driver-version', [driver, '--version']), 'linker': str(linker), 'linker_sha256': sha(linker), 'linker_version': run('aot-linker-version', [linker, '--version'])}
    write_json('toolchain.json', toolchain)
    run('package-consumer-aot-publish', [DOTNET, 'publish', '-c', 'Release', '-r', 'linux-x64', '-p:PublishAot=true', '--self-contained', 'true', '--no-restore', '-o', OUT / 'aot-consumer', '--disable-build-servers'], consumer, consumer_env)
    run('package-consumer-aot-run', [OUT / 'aot-consumer/Consumer', '--require-aot'], consumer, consumer_env)
    toolchain['aot_artifact_comment'] = run('aot-artifact-comment', ['readelf', '-p', '.comment', OUT / 'aot-consumer/Consumer'])
    write_json('toolchain.json', toolchain)
    shutil.copy2(consumer / 'packages.lock.json', OUT / 'consumer-packages.lock.json')
    if trap_log.exists():
        raise RuntimeError('A package consumer invoked a Rust tool.')
    negatives = json.loads((ROOT / 'tests/Data.Abstractions/negative/cases.json').read_text())
    for filename, codes in negatives.items():
        (consumer / 'Program.cs').write_text((ROOT / 'tests/Data.Abstractions/negative' / filename).read_text())
        # These are compiler-negative API probes, not executable consumer builds.
        text = run('negative-' + filename.removesuffix('.cs.txt'), [DOTNET, 'msbuild', 'Consumer.csproj', '-t:Compile',
            '-p:Configuration=Release', '-p:UseSharedCompilation=false', '-nr:false', '-m:1'], consumer, consumer_env, fail=True)
        if not any(code in text for code in codes):
            raise RuntimeError('Negative compilation failed for an unrelated reason: ' + filename)

run('managed-aot-restore', [DOTNET, 'restore', managed, '-r', 'linux-x64', '-p:PublishAot=true', '--locked-mode', '--disable-build-servers', *restore_options])
run('managed-aot-publish', [DOTNET, 'publish', managed, '-c', 'Release', '-r', 'linux-x64', '-p:PublishAot=true', '--self-contained', 'true', '--no-restore', '--disable-build-servers', '-o', OUT / 'aot-tests'])
run('managed-aot-run', [OUT / 'aot-tests/Korda.Data.Tests', OUT / 'managed-aot', '--require-aot'])
native_results = json.loads(run('native-aot-goldens', [native_binary, OUT / 'managed-aot']))
tracked = subprocess.check_output(['git', 'ls-files', '--cached', '--others', '--exclude-standard',
    'src/Data.Abstractions', 'tests/Data.Abstractions', 'native/database/interfaces',
    'native/cpp/include/nw/contracts.hpp', 'native/cpp/include/nw/generated/schema.hpp',
    'native/cpp/src/contracts.cpp', 'native/cpp/tests/contracts_tests.cpp',
    'native/cpp/tests/golden/contracts', 'samples/api/data-abstractions',
    'docs/guides/data-abstractions.md'], cwd=ROOT, text=True).splitlines()
tracked = [name for name in tracked if (ROOT / name).is_file()]
write_json('source-identity.json', {'commit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(), 'status': subprocess.check_output(['git', 'status', '--short'], cwd=ROOT, text=True), 'design_commit': 'c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9', 'platform': 'linux-x64', 'files': {f: sha(ROOT / f) for f in tracked}, 'root_build_inputs': {f: sha(ROOT / f) for f in ['global.json', 'Directory.Build.props', 'CMakeLists.txt', 'CMakePresets.json', 'native/cpp/tests/CMakeLists.txt', 'native/dependencies.lock.json', 'THIRD_PARTY_NOTICES.md']}})
artifacts = [packages[0], ROOT / 'src/Data.Abstractions/bin/Release/net11.0/Korda.Data.dll', native_binary, OUT / 'aot-consumer/Consumer', OUT / 'aot-tests/Korda.Data.Tests']
write_json('artifact-hashes.json', {str(f.relative_to(OUT)) if f.is_relative_to(OUT) else str(f.relative_to(ROOT)) if f.is_relative_to(ROOT) else str(f): sha(f) for f in artifacts})
write_json('summary.json', {'status': 'passed', 'task': 'B01', 'platform': 'linux-x64', 'compiled_api_symbols': api_result['compiled_symbols'], 'managed_case_families': len(json.loads((OUT / 'managed-aot/managed-results.json').read_text())['cases']), 'negative_compile_cases': 4, 'native_scalar_kinds': native_results['scalar_kinds'], 'native_malformed_rejections': native_results['malformed_rejections'], 'native_cross_language_goldens': native_results['cross_language_goldens'], 'package_native_aot_executed': True, 'managed_contracts_native_aot_executed': True, 'rust_consumer_invocations': 0, 'nuget_audit': 'unavailable_offline' if a.offline_packages else 'normal_restore', 'scope': 'B01 metadata/row/value contracts; not a live database host, generator, provider or final pristine platform acceptance', 'windows': 'deferred_by_user'})
print('B01 Linux x64 verification passed.', flush=True)
