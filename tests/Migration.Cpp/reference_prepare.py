#!/usr/bin/env python3
"""Retrieve and verify the fixed Rust package artifact without a Rust toolchain.

GITHUB_TOKEN is read only from the environment. It is sent only to api.github.com,
never placed in a command line, log, receipt, or redirected storage request.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import tempfile
import urllib.error
import urllib.parse
import urllib.request
import zipfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
spec = importlib.util.spec_from_file_location('package_identity', ROOT / 'tests/Reference.TaskService/package_probe.py')
identity = importlib.util.module_from_spec(spec)
spec.loader.exec_module(identity)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, file, code, message, headers, new_url):
        return None


def fetch(lock, destination):
    token = os.environ.get('GITHUB_TOKEN')
    if not token:
        raise RuntimeError('Set GITHUB_TOKEN with actions:read, or provide --archive with the fixed ZIP.')
    endpoint = 'https://api.github.com/repos/' + lock['repository'] + '/actions/artifacts/' + str(lock['artifact_id'])
    headers = {'Authorization': 'Bearer ' + token, 'Accept': 'application/vnd.github+json',
        'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': 'Korda-DB-CPP-01-reference'}
    opener = urllib.request.build_opener(NoRedirect)
    with opener.open(urllib.request.Request(endpoint, headers=headers), timeout=60) as response:
        metadata = json.loads(response.read(1024 * 1024))
    workflow = metadata.get('workflow_run', {})
    if metadata.get('id') != lock['artifact_id'] or workflow.get('id') != lock['workflow_run'] or workflow.get('head_sha') != lock['source_commit'] or metadata.get('expired'):
        raise RuntimeError('The fixed GitHub artifact identity is unavailable or no longer matches its workflow/commit.')
    try:
        response = opener.open(urllib.request.Request(endpoint + '/zip', headers=headers), timeout=60)
    except urllib.error.HTTPError as error:
        if error.code not in (301, 302, 303, 307, 308):
            raise RuntimeError('GitHub artifact download failed with HTTP status ' + str(error.code)) from None
        location = error.headers.get('Location', '')
        if urllib.parse.urlsplit(location).scheme != 'https':
            raise RuntimeError('Artifact storage redirect must use HTTPS.') from None
        # A new unauthenticated request intentionally drops the GitHub token.
        response = urllib.request.urlopen(urllib.request.Request(location, headers={'User-Agent': headers['User-Agent']}), timeout=60)
    with response, destination.open('wb') as output:
        remaining = lock['archive_bytes']
        while True:
            chunk = response.read(min(1024 * 1024, remaining + 1))
            if not chunk:
                break
            if len(chunk) > remaining:
                raise RuntimeError('Artifact download exceeded its fixed byte size.')
            output.write(chunk); remaining -= len(chunk)
        if remaining:
            raise RuntimeError('Artifact download was shorter than its fixed byte size.')


def verify_archive(path, lock):
    if path.stat().st_size != lock['archive_bytes'] or identity.sha(path) != lock['archive_sha256']:
        raise ValueError('The Rust reference archive size or SHA-256 differs from reference.lock.json.')


def verify_feed(path, lock, archive_hashes):
    packages, native = identity.package_inputs(path, lock['provider_version'], prefix='NativeWeave')
    if native != lock['native_sha256']:
        raise ValueError('Reference native assets differ from the fixed accepted hashes.')
    if any(package['repository'].get('commit') != lock['source_commit'] for package in packages.values()):
        raise ValueError('A Rust package repository commit differs from the fixed baseline.')
    if {package['filename']: package['sha256'] for package in packages.values()} != archive_hashes:
        raise ValueError('The extracted package bytes differ from the fixed artifact ZIP.')
    return packages, native


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--cache', type=Path, required=True)
    parser.add_argument('--archive', type=Path, help='Use an already downloaded ZIP; its exact hash is still required.')
    args = parser.parse_args()
    lock = json.loads((HERE / 'reference.lock.json').read_text())
    args.cache.mkdir(parents=True, exist_ok=True)
    archive = args.cache.resolve() / (lock['archive_sha256'] + '.zip')
    if not archive.exists():
        temporary = archive.with_suffix('.download')
        try:
            if args.archive:
                shutil.copyfile(args.archive, temporary)
            else:
                fetch(lock, temporary)
            verify_archive(temporary, lock)
            temporary.replace(archive)
        finally:
            temporary.unlink(missing_ok=True)
    verify_archive(archive, lock)
    with zipfile.ZipFile(archive) as zipped:
        archive_hashes = {PurePosixPath(name).name: hashlib.sha256(zipped.read(name)).hexdigest()
            for name in zipped.namelist() if name.startswith('candidate/feed/') and name.endswith('.nupkg')}
    if archive_hashes != lock['package_sha256']:
        raise ValueError('The ZIP package inventory differs from the exact reference lock.')
    output = args.output.resolve()
    if output.exists():
        packages, native = verify_feed(output / 'candidate/feed', lock, archive_hashes)
        prior = json.loads((output / 'reference-provenance.json').read_text())
        if prior.get('lock') != lock or prior.get('packages') != packages:
            raise ValueError('Existing reference output differs from the fixed archive.')
    else:
        output.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix='nw-rust-reference-', dir=output.parent) as temporary:
            extracted = Path(temporary) / 'artifact'; extracted.mkdir()
            with zipfile.ZipFile(archive) as zipped:
                for member in zipped.infolist():
                    path = PurePosixPath(member.filename)
                    if path.is_absolute() or '..' in path.parts or (member.external_attr >> 16) & 0o170000 == 0o120000:
                        raise ValueError('Unsafe path in the fixed artifact ZIP.')
                zipped.extractall(extracted)
            packages, native = verify_feed(extracted / 'candidate/feed', lock, archive_hashes)
            identity.save(extracted / 'reference-provenance.json', {'lock': lock, 'packages': packages, 'native_sha256': native,
                'workflow_url': 'https://github.com/' + lock['repository'] + '/actions/runs/' + str(lock['workflow_run'])})
            extracted.rename(output)
    print('PASS fixed Rust reference feed:', output / 'candidate/feed')
    print('Archive SHA-256:', lock['archive_sha256'])


if __name__ == '__main__':
    main()
