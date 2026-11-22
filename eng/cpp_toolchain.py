#!/usr/bin/env python3
"""Checksum-pinned Linux maintainer tools and source inputs; never a NuGet target."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import tarfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
LOCK = ROOT / 'native/dependencies.lock.json'


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def download(url, checksum, path):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    cache = os.environ.get('NW_DOWNLOAD_CACHE')
    cached = Path(cache) / path.name if cache else None
    if not path.exists() and cached and cached.is_file():
        shutil.copyfile(cached, path)
    if not path.exists():
        temporary = path.with_name(path.name + '.partial')
        subprocess.run(['curl', '--fail', '--silent', '--show-error', '--location', '--retry', '3',
                        url, '--output', str(temporary)], check=True)
        if sha(temporary) != checksum:
            raise RuntimeError('Downloaded archive checksum mismatch: ' + path.name)
        temporary.replace(path)
    if sha(path) != checksum:
        raise RuntimeError('Cached archive checksum mismatch: ' + path.name)
    return path


def sources(output, providers=('sqlite', 'postgresql')):
    """Bind archives and every consumed header/amalgamation, including cache reuse."""
    result = {}
    for item in json.loads(LOCK.read_text())['dependencies']:
        if item['name'] == 'libpq' or not set(providers).intersection(item['providers']):
            continue  # Independently source-built by postgres-tools.sh.
        filename = 'nlohmann-json-3.12.0.tar.xz' if item['name'] == 'nlohmann-json' else 'sqlite-amalgamation-3510300.zip'
        archive = download(item['url'], item['archiveSha256'], output / 'downloads' / filename)
        directory = output / item['name']
        source = directory / ('json' if item['name'] == 'nlohmann-json' else 'sqlite-amalgamation-3510300')
        if not source.exists():
            directory.mkdir(parents=True, exist_ok=True)
            if zipfile.is_zipfile(archive):
                with zipfile.ZipFile(archive) as stream:
                    if any(not (directory / e.filename).resolve().is_relative_to(directory.resolve()) for e in stream.infolist()):
                        raise RuntimeError('Archive path escapes destination')
                    stream.extractall(directory)
            else:
                with tarfile.open(archive) as stream:
                    stream.extractall(directory, filter='data')
        for name, expected in item['sourceFiles'].items():
            if sha(source / name) != expected:
                raise RuntimeError('Dependency source checksum mismatch: ' + item['name'] + '/' + name)
        result[item['name']] = {'source': str(source.resolve()), 'archive': str(archive.resolve()),
                                'archiveSha256': sha(archive), 'sourceFiles': item['sourceFiles']}
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=ROOT / 'artifacts/toolchain')
    output = parser.parse_args().output.resolve()
    paths = []
    for item in json.loads(LOCK.read_text())['toolchain'].values():
        archive = download(item['url'], item['sha256'], output / 'downloads' / item['filename'])
        destination = output / item['directory']
        if not destination.exists():
            destination.mkdir(parents=True)
            if zipfile.is_zipfile(archive):
                with zipfile.ZipFile(archive) as stream:
                    stream.extractall(destination)
                (destination / 'ninja').chmod(0o755)
            else:
                command = ['tar', '-xf', str(archive), '-C', str(destination),
                           '--strip-components=' + str(item['stripComponents'])]
                if archive.name.endswith('.zst'):
                    command += ['--use-compress-program=zstd -d --long=30']
                subprocess.run(command, check=True)
        binary = destination / 'bin' if (destination / 'bin').is_dir() else destination
        for name, expected in item['executables'].items():
            if sha((binary / name).resolve()) != expected:
                raise RuntimeError('Pinned tool executable mismatch: ' + name)
        paths.append(str(binary))
    env = output / 'environment.sh'
    env.write_text('export PATH=' + shlex.quote(':'.join(paths)) + ':"$PATH"\n'
                   + 'export CC=' + shlex.quote(paths[0] + '/clang') + '\n'
                   + 'export CXX=' + shlex.quote(paths[0] + '/clang++') + '\n')
    if os.environ.get('GITHUB_PATH'):
        with Path(os.environ['GITHUB_PATH']).open('a') as stream:
            stream.write('\n'.join(paths) + '\n')
        with Path(os.environ['GITHUB_ENV']).open('a') as stream:
            stream.write('CC=' + paths[0] + '/clang\nCXX=' + paths[0] + '/clang++\n')
    print(json.dumps({'environment': str(env), 'lockSha256': sha(LOCK), 'verifiedToolDirectories': paths}))


if __name__ == '__main__':
    main()
