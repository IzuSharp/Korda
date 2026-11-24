#!/usr/bin/env python3
"""Replay the retained external consumer sources, changing only candidate feed/version."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path('/workspace/scratch/b94e2a0e0c3f')
SOURCE = ROOT / 'consumer-before-sqlite-v7'
TARGET = ROOT / 'consumer-fourth-sqlite-v1'
FEED = ROOT / 'fourth-artifacts/sqlite/feed'
VERSION = '0.1.0-ci.1'


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    original = json.loads((SOURCE / 'build-receipt.json').read_text())
    TARGET.mkdir(exist_ok=False)
    for name, expected in original['sources'].items():
        assert sha(SOURCE / name) == expected
        shutil.copy2(SOURCE / name, TARGET / name)
    for name in ('DbPerf.Korda.csproj', 'global.json', 'workload-manifest.json'):
        shutil.copy2(SOURCE / name, TARGET / name)
    assert sha(TARGET / 'DbPerf.Korda.csproj') == original['project_sha256']
    config = ET.parse(SOURCE / 'NuGet.Config')
    for source in config.findall('./packageSources/add'):
        if source.attrib['key'] == 'candidate':
            source.set('value', str(FEED))
    config.write(TARGET / 'NuGet.Config', encoding='utf-8', xml_declaration=True)
    command = list(original['command'])
    command[2] = str(TARGET / 'DbPerf.Korda.csproj')
    command[command.index('-o') + 1] = str(TARGET / 'out')
    command = [('-p:KordaVersion=' + VERSION) if value.startswith('-p:KordaVersion=') else value for value in command]
    with (TARGET / 'publish.log').open('w') as output:
        result = subprocess.run(command, cwd=TARGET,
                                env=os.environ | {'NUGET_PACKAGES': str(TARGET / 'packages')},
                                stdout=output, stderr=subprocess.STDOUT, check=False)
    package = next(FEED.glob('*.nupkg'))
    receipt = dict(original, version=VERSION, package_sha256=sha(package), command=command,
                   exit_code=result.returncode, publish_log_sha256=sha(TARGET / 'publish.log'),
                   config_sha256=sha(TARGET / 'NuGet.Config'),
                   original_consumer_build_receipt_sha256=sha(SOURCE / 'build-receipt.json'),
                   source_comparison='All C# and project bytes exactly equal retained before consumer; only package input/feed/version changed.')
    if result.returncode == 0:
        assets = TARGET / 'obj/project.assets.json'
        graph = json.loads(assets.read_text())
        assert {key for key in graph['libraries'] if key.lower().startswith('korda.')} == {'Korda.Database.Sqlite/' + VERSION}
        executable = TARGET / 'out/DbPerf.Korda'
        assert executable.read_bytes()[:4] == b'\x7fELF'
        assert not executable.with_suffix('.dll').exists()
        receipt['executable_sha256'] = sha(executable)
        receipt['assets_sha256'] = sha(assets)
        receipt['native_images'] = {str(path.relative_to(TARGET / 'out')): sha(path) for path in (TARGET / 'out').glob('libnw_database_*.so')}
        shutil.copy2(assets, TARGET / 'project.assets.json')
        generated = TARGET / 'obj/Release/net11.0/linux-x64/generated'
        if generated.is_dir():
            shutil.copytree(generated, TARGET / 'generated')
    (TARGET / 'build-receipt.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print(json.dumps({key: receipt.get(key) for key in ('version', 'exit_code', 'package_sha256', 'executable_sha256', 'native_images')}))
    if result.returncode == 0:
        assert sha(TARGET / 'project.assets.json') == receipt['assets_sha256']
        shutil.rmtree(TARGET / 'bin')
        shutil.rmtree(TARGET / 'obj')
    raise SystemExit(result.returncode)


if __name__ == '__main__':
    main()
