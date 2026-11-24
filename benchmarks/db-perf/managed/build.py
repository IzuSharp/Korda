#!/usr/bin/env python3
"""Copy the ordinary public-package consumer outside Korda, publish AOT, and bind every input/output hash."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]


def sha(path):
    with Path(path).open('rb') as file:
        return hashlib.file_digest(file, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--provider', choices=('sqlite', 'postgresql'), required=True)
    parser.add_argument('--feed', type=Path, required=True)
    parser.add_argument('--version', required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--manifest', type=Path, default=HERE.parent/'workload/queries-v3.json')
    parser.add_argument('--kind', choices=('workload', 'prepared-probe'), default='workload')
    args = parser.parse_args()
    app = args.output.resolve()
    if app == ROOT or ROOT in app.parents:
        raise ValueError('The consumer output must be outside the Korda source tree')
    if app.exists():
        raise ValueError('Use a fresh consumer output directory; previous evidence is immutable')
    provider = 'Sqlite' if args.provider == 'sqlite' else 'PostgreSql'
    identity = 'Korda.Database.' + provider
    feed = args.feed.resolve()
    package = feed / f'{identity}.{args.version}.nupkg'
    if not package.is_file():
        raise FileNotFoundError(package)
    manifest = args.manifest.resolve()
    if not manifest.is_file():
        raise FileNotFoundError(manifest)
    app.mkdir(parents=True)
    source_folder = 'Korda' if args.kind == 'workload' else 'PreparedProbe'
    assembly = 'DbPerf.Korda' if args.kind == 'workload' else 'DbPerf.PreparedProbe'
    for file in (HERE / source_folder).iterdir():
        if file.suffix in ('.cs', '.csproj'):
            shutil.copy2(file, app / file.name)
    if args.kind == 'workload':
        for file in (HERE / 'Shared').glob('*.cs'):
            shutil.copy2(file, app / file.name)
    shutil.copy2(ROOT / 'global.json', app / 'global.json')
    shutil.copy2(manifest, app / 'workload-manifest.json')
    project = app / (assembly + '.csproj')
    if ET.parse(project).getroot().findall('.//ProjectReference'):
        raise ValueError('Only PackageReference is allowed')
    config = ET.Element('configuration')
    sources = ET.SubElement(config, 'packageSources')
    ET.SubElement(sources, 'clear')
    ET.SubElement(sources, 'add', key='candidate', value=str(feed))
    ET.SubElement(sources, 'add', key='nuget', value='https://api.nuget.org/v3/index.json')
    mapping = ET.SubElement(config, 'packageSourceMapping')
    candidate = ET.SubElement(mapping, 'packageSource', key='candidate')
    ET.SubElement(candidate, 'package', pattern=identity)
    nuget = ET.SubElement(mapping, 'packageSource', key='nuget')
    ET.SubElement(nuget, 'package', pattern='*')
    ET.ElementTree(config).write(app / 'NuGet.Config', encoding='utf-8', xml_declaration=True)
    command = ['dotnet', 'publish', str(project), '-c', 'Release', '-r', 'linux-x64',
               '-p:Provider=' + provider, '-p:KordaVersion=' + args.version,
               '-p:PublishAot=true', '-p:IlcTreatWarningsAsErrors=true',
               '-p:UseSharedCompilation=false', '-m:1', '-nr:false', '-o', str(app / 'out')]
    # Application packages are isolated; SDK/AOT tooling may use the environment's existing cache as a fallback.
    env = os.environ | {'NUGET_PACKAGES': str(app / 'packages')}
    prior_cache = os.environ.get('NUGET_PACKAGES')
    if prior_cache and Path(prior_cache).is_dir():
        fallbacks = ET.SubElement(config, 'fallbackPackageFolders')
        ET.SubElement(fallbacks, 'clear')
        fallback = app / 'sdk-cache'; fallback.mkdir()
        for folder in Path(prior_cache).iterdir():
            if folder.is_dir() and folder.name.lower().startswith(('microsoft.netcore.app.', 'microsoft.aspnetcore.app.', 'microsoft.net.illink.tasks', 'microsoft.dotnet.', 'runtime.linux-x64.microsoft.dotnet.')):
                (fallback / folder.name).symlink_to(folder.resolve(), target_is_directory=True)
        ET.SubElement(fallbacks, 'add', key='sdk', value=str(fallback))
        ET.ElementTree(config).write(app / 'NuGet.Config', encoding='utf-8', xml_declaration=True)
    with (app / 'publish.log').open('w') as log:
        result = subprocess.run(command, cwd=app, env=env, stdout=log, stderr=subprocess.STDOUT, check=False)
    receipt = {'schema_version': 1, 'provider': args.provider, 'kind': args.kind, 'package': identity, 'version': args.version,
               'package_sha256': sha(package), 'command': command, 'exit_code': result.returncode,
               'publish_log_sha256': sha(app / 'publish.log'),
               'sources': {p.name: sha(p) for p in sorted(app.glob('*.cs'))}, 'project_sha256': sha(project),
               'config_sha256': sha(app / 'NuGet.Config'), 'project_references': 0}
    receipt['workloadManifestSha256'] = sha(manifest)
    if result.returncode == 0:
        assets = json.loads((app / 'obj/project.assets.json').read_text())
        korda = {k for k in assets['libraries'] if k.lower().startswith('korda.')}
        if korda != {identity + '/' + args.version}:
            raise ValueError('Unexpected Korda package graph: ' + repr(korda))
        executable = app / 'out' / assembly
        if executable.read_bytes()[:4] != b'\x7fELF' or executable.with_suffix('.dll').exists():
            raise ValueError('Expected a standalone Native AOT ELF')
        receipt['executable_sha256'] = sha(executable)
        receipt['assets_sha256'] = sha(app / 'obj/project.assets.json')
        shutil.copy2(app / 'obj/project.assets.json', app / 'project.assets.json')
        generated = app / 'obj/Release/net11.0/linux-x64/generated'
        if generated.is_dir():
            shutil.copytree(generated, app / 'generated')
        receipt['native_images'] = {str(p.relative_to(app / 'out')): sha(p) for p in sorted((app / 'out').rglob('libnw_database_*.so'))}
    (app / 'build-receipt.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print(json.dumps(receipt, indent=2))
    raise SystemExit(result.returncode)


if __name__ == '__main__':
    main()
