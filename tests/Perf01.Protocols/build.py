#!/usr/bin/env python3
"""Build the unchanged Perf-01 application/client against one supplied B24 closure."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def save(path, value):
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + '\n')


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--feed', type=Path, required=True)
    p.add_argument('--version', required=True)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--sdk', type=Path, required=True, help='Exact dotnet executable, SDK 11.0.100')
    p.add_argument('--packages', type=Path, help='Optional shared package download cache')
    p.add_argument('--package-manifest', type=Path, help='B24 manifest; defaults to FEED/../../manifest.json')
    p.add_argument('--native-manifest', type=Path, help='Native producer manifest; defaults to B24 parent/native-stable/manifest.json')
    p.add_argument('--compile-only', action='store_true', help='Compile diagnostics only; output is not eligible for measurement')
    args = p.parse_args()
    package_manifest = (args.package_manifest or args.feed.resolve().parent.parent / 'manifest.json').resolve()
    native_manifest = (args.native_manifest or package_manifest.parent.parent / 'native-stable/manifest.json').resolve()
    producer = json.loads(package_manifest.read_text()); native_producer = json.loads(native_manifest.read_text())
    if producer['sourceCommit'] != native_producer['sourceCommit'] or producer['sourceDirty'] or native_producer['sourceDirty']:
        raise ValueError('Product and native producer must identify one clean fixed source commit')
    closures = [value for value in producer.values() if isinstance(value, dict) and 'feed' in value and (package_manifest.parent / value['feed']).resolve() == args.feed.resolve()]
    if len(closures) != 1: raise ValueError('Supplied feed is not exactly one declared B24 closure')
    closure = closures[0]
    expected_packages = {(row['id'].lower(), row['version']): row for row in closure['packages']}
    for row in closure['packages']:
        if row['sourceCommit'] != producer['sourceCommit'] or sha(package_manifest.parent / row['path']) != row['sha256']:
            raise ValueError('Producer package provenance mismatch: ' + row['id'])
    out = args.output.resolve(); out.mkdir(parents=True, exist_ok=False)
    source = out / 'source'; source.mkdir()
    copied = []
    for name in ['Server', 'Client']:
        shutil.copytree(HERE / name, source / name, ignore=shutil.ignore_patterns('bin', 'obj', 'packages*.json'))
        copied += [path for path in (source / name).rglob('*') if path.is_file()]
    for name in ['Shared.cs', 'perf01.proto']:
        shutil.copy2(HERE / name, source / name); copied.append(source / name)
    shutil.copy2(ROOT / 'global.json', source / 'global.json')
    (source / 'Directory.Build.props').write_text('<Project><PropertyGroup><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile><Deterministic>true</Deterministic><ContinuousIntegrationBuild>true</ContinuousIntegrationBuild></PropertyGroup></Project>\n')
    configuration = ET.Element('configuration')
    sources = ET.SubElement(configuration, 'packageSources'); ET.SubElement(sources, 'clear')
    ET.SubElement(sources, 'add', key='candidate', value=str(args.feed.resolve()))
    ET.SubElement(sources, 'add', key='nuget.org', value='https://api.nuget.org/v3/index.json')
    mapping = ET.SubElement(configuration, 'packageSourceMapping')
    candidate = ET.SubElement(mapping, 'packageSource', key='candidate'); ET.SubElement(candidate, 'package', pattern='NativeWeave.*')
    external = ET.SubElement(mapping, 'packageSource', key='nuget.org'); ET.SubElement(external, 'package', pattern='*')
    ET.ElementTree(configuration).write(source / 'NuGet.Config', encoding='unicode', xml_declaration=False)
    environment = dict(os.environ, DOTNET_ROOT=str(args.sdk.resolve().parent), DOTNET_CLI_TELEMETRY_OPTOUT='1',
                       DOTNET_CLI_USE_MSBUILD_SERVER='0', MSBUILDDISABLENODEREUSE='1')
    commands = []
    def command(label, argv):
        with (out / (label + '.log')).open('w') as log:
            process = subprocess.run([str(x) for x in argv], cwd=source, env=environment, stdout=log, stderr=subprocess.STDOUT)
        commands.append({'label': label, 'argv': [str(x) for x in argv], 'exit_code': process.returncode, 'log_sha256': sha(out / (label + '.log'))})
        save(out / 'commands.json', commands)
        if process.returncode: raise RuntimeError('Build failed: ' + label)
    sdk_version = subprocess.check_output([str(args.sdk), '--version'], cwd=source, env=environment, text=True).strip()
    if sdk_version != '11.0.100': raise ValueError('Pinned SDK 11.0.100 is required, observed ' + sdk_version)
    cache = (args.packages or out / 'packages').resolve()
    for name in ['Server', 'Client']:
        project = source / name / (name + '.csproj')
        properties = ['-p:NativeWeavePackageVersion=' + args.version, '-p:UseSharedCompilation=false', '-p:RestorePackagesPath=' + str(cache)]
        target = [] if args.compile_only else ['-r', 'linux-x64', '-p:PublishAot=true', '-p:IlcInstructionSet=x86-64-v2']
        command(name.lower() + '-restore', [args.sdk, 'restore', project, '--configfile', source / 'NuGet.Config', *target, *properties])
        command(name.lower() + '-locked-restore', [args.sdk, 'restore', project, '--locked-mode', '--configfile', source / 'NuGet.Config', *target, *properties])
        command(name.lower() + '-publish', [args.sdk, 'publish', project, '-c', 'Release', '--no-restore', '-o', out / name.lower(), *target, *properties])
    files = {str(path): sha(path) for name in ['server', 'client'] for path in sorted((out / name).rglob('*')) if path.is_file()}
    locks = {str(path): sha(path) for path in source.rglob('packages.lock.json')}
    package_files = {}
    for path in args.feed.resolve().glob('*.nupkg'):
        package_files[str(path)] = sha(path)
    resolved_packages = {}
    for path in source.rglob('project.assets.json'):
        for identity in json.loads(path.read_text())['libraries']:
            name, version = identity.rsplit('/', 1)
            if not name.startswith('NativeWeave.'): continue
            row = expected_packages.get((name.lower(), version))
            if row is None: raise ValueError('Consumer resolved a package outside the selected closure: ' + identity)
            archive = cache / name.lower() / version / (name.lower() + '.' + version + '.nupkg')
            if sha(archive) != row['sha256']: raise ValueError('NuGet cache bytes differ from producing closure: ' + identity)
            resolved_packages[str(archive)] = row['sha256']
    provenance = {str(package_manifest): sha(package_manifest), str(native_manifest): sha(native_manifest)}
    for key in ['sourceInventory']:
        if producer.get(key): provenance[str(package_manifest.parent / producer[key])] = sha(package_manifest.parent / producer[key])
    if closure.get('buildClosure'): provenance[str(package_manifest.parent / closure['buildClosure'])] = sha(package_manifest.parent / closure['buildClosure'])
    manifest = {'schema_version': 1, 'task': 'Perf-01', 'platform': 'linux-x64', 'nativeweave_package_version': args.version,
                'product_source_commit': producer['sourceCommit'], 'product_source_dirty': False,
                'driver_source_commit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
                'driver_dirty': bool(subprocess.check_output(['git', 'status', '--porcelain'], cwd=ROOT, text=True).strip()),
                'driver_source_sha256': {str(path.relative_to(source)): sha(path) for path in copied},
                'source_files_sha256': {str(path): sha(path) for path in copied + [source / 'global.json', source / 'Directory.Build.props', source / 'NuGet.Config']},
                'package_archives_sha256': package_files, 'resolved_package_archives_sha256': resolved_packages,
                'producer_manifests_sha256': provenance, 'artifacts_sha256': files, 'lock_hashes': locks,
                'toolchain': {'sdk': sdk_version, 'dotnet_sha256': sha(args.sdk), 'configuration': 'Release', 'aot': not args.compile_only, 'isa': 'x86-64-v2'},
                'eligible_for_measurement': not args.compile_only,
                'server': str(out / 'server/Perf01.ProtocolServer'), 'client': str(out / 'client/Perf01.ProtocolClient'),
                'server_native_sha256': {str(path): sha(path) for path in (out / 'server').rglob('*nativeweave*.so')},
                'commands_sha256': sha(out / 'commands.json')}
    save(out / 'build.json', manifest)
    print(out / 'build.json')


if __name__ == '__main__':
    main()
