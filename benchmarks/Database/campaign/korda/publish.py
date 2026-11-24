#!/usr/bin/env python3
"""Publish an ordinary PackageReference consumer outside the product checkout."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import xml.etree.ElementTree as ET
import zipfile


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--provider', choices=('sqlite', 'postgresql'), required=True)
    parser.add_argument('--feed', type=Path, required=True)
    parser.add_argument('--version', required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--dotnet', type=Path, required=True)
    parser.add_argument('--clang', type=Path, required=True)
    parser.add_argument('--support-cache', type=Path)
    args = parser.parse_args()
    here = Path(__file__).resolve().parent
    product = here.parents[3]
    root = args.output.resolve()
    if root == product or product in root.parents or root.exists():
        raise ValueError('Use a fresh output directory outside the product checkout')
    provider = 'Sqlite' if args.provider == 'sqlite' else 'PostgreSql'
    identity = 'Korda.Database.' + provider
    feed = args.feed.resolve()
    package = feed / f'{identity}.{args.version}.nupkg'
    with zipfile.ZipFile(package) as archive:
        spec = ET.fromstring(archive.read(next(n for n in archive.namelist() if n.endswith('.nuspec'))))
        repository = next(e for e in spec.iter() if e.tag.rsplit('}',1)[-1] == 'repository')
        source_commit = repository.attrib['commit']
        native_members = {n: hashlib.sha256(archive.read(n)).hexdigest() for n in archive.namelist() if n.endswith('.so')}
    root.mkdir(parents=True)
    app = root / 'app'; app.mkdir()
    common = root / 'common'; common.mkdir()
    for directory, destination in ((here, app), (here.parent / 'common', common)):
        for file in sorted(directory.iterdir()):
            if file.suffix in ('.cs', '.csproj'):
                shutil.copy2(file, destination / file.name)
    shutil.copy2(product / 'global.json', root / 'global.json')
    (root / 'Directory.Build.props').write_text('<Project />\n')
    project = app / 'DbPerf.Korda.csproj'
    if ET.parse(project).getroot().findall('.//ProjectReference'):
        raise ValueError('A source/project reference invalidates this consumer')
    config = ET.Element('configuration')
    sources = ET.SubElement(config, 'packageSources'); ET.SubElement(sources, 'clear')
    ET.SubElement(sources, 'add', key='candidate', value=str(feed))
    ET.SubElement(sources, 'add', key='nuget', value='https://api.nuget.org/v3/index.json')
    mapping = ET.SubElement(config, 'packageSourceMapping')
    selected = ET.SubElement(mapping, 'packageSource', key='candidate'); ET.SubElement(selected, 'package', pattern=identity)
    support = ET.SubElement(mapping, 'packageSource', key='nuget'); ET.SubElement(support, 'package', pattern='*')
    if args.support_cache:
        fallback = root / 'support-cache'; fallback.mkdir()
        for directory in args.support_cache.resolve().iterdir():
            if directory.is_dir() and not directory.name.lower().startswith('korda.'):
                (fallback / directory.name).symlink_to(directory, target_is_directory=True)
        folders = ET.SubElement(config, 'fallbackPackageFolders'); ET.SubElement(folders, 'clear')
        ET.SubElement(folders, 'add', key='support', value=str(fallback))
    ET.ElementTree(config).write(root / 'NuGet.Config', encoding='utf-8', xml_declaration=True)
    env = dict(os.environ)
    env.update(DOTNET_ROOT=str(args.dotnet.resolve().parent), NUGET_PACKAGES=str(root / 'packages'),
               DOTNET_CLI_HOME=str(root / 'cli-home'), DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1')
    env['PATH'] = str(args.clang.resolve().parent) + os.pathsep + str(args.dotnet.resolve().parent) + os.pathsep + env['PATH']
    command = [str(args.dotnet), 'publish', str(project), '-c', 'Release', '-r', 'linux-x64',
               '--self-contained', 'true', '-p:Provider=' + provider, '-p:KordaVersion=' + args.version,
               '-p:PublishAot=true', '-p:IlcTreatWarningsAsErrors=true', '-p:IlcSingleThreaded=true',
               '-p:NuGetAudit=false', '-p:UseSharedCompilation=false', '-m:1', '-nr:false',
               '-p:CppCompilerAndLinker=' + str(args.clang.resolve()), '-o', str(root / 'out')]
    receipt = {'schema_version': 1, 'provider': args.provider, 'package_id': identity, 'version': args.version,
               'package_sha256': sha(package), 'package_source_commit': source_commit,
               'command': command, 'source_files': {str(p.relative_to(root)): sha(p) for folder in (app, common) for p in sorted(folder.iterdir())},
               'publisher_sha256': sha(__file__), 'project_references': 0, 'status': 'building'}
    receipt_path = root / 'build-receipt.json'
    receipt_path.write_text(json.dumps(receipt, indent=2) + '\n')
    with (root / 'publish.log').open('w') as log:
        result = subprocess.run(command, cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT, check=False)
    receipt.update(exit_code=result.returncode, publish_log_sha256=sha(root / 'publish.log'), status='build_failed')
    if result.returncode == 0:
        assets = app / 'obj/project.assets.json'
        graph = json.loads(assets.read_text())
        kordas = sorted(k for k in graph['libraries'] if k.lower().startswith('korda.'))
        if kordas != [identity + '/' + args.version]:
            raise ValueError('Unexpected Korda package graph: ' + repr(kordas))
        executable = root / 'out/DbPerf.Korda'
        with executable.open('rb') as stream:
            if stream.read(4) != b'\x7fELF' or executable.with_suffix('.dll').exists():
                raise ValueError('Expected standalone Native AOT ELF')
        native = {str(p.relative_to(root / 'out')): sha(p) for p in sorted((root / 'out').rglob('libnw_database_*.so'))}
        if sorted(native.values()) != sorted(native_members.values()):
            raise ValueError('Published native bytes differ from package')
        receipt.update(status='built_not_executed', executable=str(executable), executable_sha256=sha(executable),
                       assets_sha256=sha(assets), resolved_korda_packages=kordas, native_images=native)
        shutil.copy2(assets, root / 'project.assets.json')
    receipt_path.write_text(json.dumps(receipt, indent=2) + '\n')
    print(json.dumps(receipt, indent=2))
    raise SystemExit(result.returncode)


if __name__ == '__main__':
    main()
