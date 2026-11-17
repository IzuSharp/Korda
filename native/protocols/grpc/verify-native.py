#!/usr/bin/env python3
"""Immutable Linux B18 native component receipt, separate from product acceptance."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import time

ROOT = Path(__file__).resolve().parents[3]
parser = argparse.ArgumentParser()
parser.add_argument('--output', type=Path, default=ROOT / 'artifacts/task06/B18/native-final')
parser.add_argument('--rust-allocation-source', type=Path)
args = parser.parse_args()
output = args.output.resolve()
output.mkdir(parents=True, exist_ok=True)

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def git(*parts):
    return subprocess.check_output(['git', *parts], cwd=ROOT, text=True).strip()

if git('status', '--porcelain'):
    raise SystemExit('The verification checkout must be clean and all source committed.')

scope = (
    'native/protocols/grpc/', 'native/protocols/mcp/', 'native/server/',
    'native/interop/abi/', 'native/interop/async/', 'native/interop/errors/',
)

def sources():
    names = git('ls-files').splitlines()
    names = [n for n in names if n in ('Cargo.toml', 'Cargo.lock', 'rust-toolchain.toml') or n.startswith(scope)]
    return {n: sha(ROOT / n) for n in names}

initial = sources()
result = dict(schema_version=1, task='B18', platform='linux-x64',
              scope='Native component only; assembled independent HTTP2/TLS/JIT/AOT/package acceptance is separate.',
              commit=git('rev-parse', 'HEAD'), source_sha256=initial, commands=[], upstream=[])
manifest = json.loads((ROOT / 'native/protocols/grpc/codec-sources.json').read_text())
registry = Path(os.environ['CARGO_HOME']) / 'registry/src'
for row in manifest['sources']:
    matches = list(registry.glob('*/' + row['crate'] + '/' + row['path']))
    if not matches or any(sha(path) != row['sha256'] for path in matches):
        raise SystemExit('Pinned upstream source mismatch: ' + row['crate'] + '/' + row['path'])
    result['upstream'].append(row)
allocation = manifest['rust_allocation_source']
if args.rust_allocation_source:
    if sha(args.rust_allocation_source) != allocation['sha256']:
        raise SystemExit('Pinned Rust allocation source mismatch.')
    result['rust_allocation_source'] = dict(allocation, verified=True)
else:
    result['rust_allocation_source'] = dict(allocation, verified=False,
        provenance='Reused B17 formatter-sources.json exact Rust1.100.0 source; pass --rust-allocation-source to recheck cached bytes.')

def run(label, command):
    log = output / (label + '.log')
    started = time.monotonic()
    with log.open('w') as stream:
        status = subprocess.run(command, cwd=ROOT, stdout=stream, stderr=subprocess.STDOUT, check=False).returncode
    result['commands'].append(dict(label=label, command=command, exit_code=status,
                                   seconds=round(time.monotonic() - started, 3), log=log.name, sha256=sha(log)))
    print(label + ': ' + ('PASS' if status == 0 else 'FAIL'), flush=True)
    if status:
        raise RuntimeError('Required stage failed: ' + label + '; inspect ' + str(log))

try:
    run('rust-toolchain', ['rustc', '-Vv'])
    toolchain_text = (output / 'rust-toolchain.log').read_text()
    if 'release: 1.100.0' not in toolchain_text or 'host: x86_64-unknown-linux-gnu' not in toolchain_text:
        raise RuntimeError('This receipt requires the fixed Linux x64 Rust1.100.0 toolchain.')
    run('cargo-features', ['cargo', 'tree', '--locked', '--offline', '-p', 'nativeweave-protocols-grpc', '-e', 'features', '--format', '{p} {f}'])
    run('leaf-format', ['cargo', 'fmt', '--package', 'nativeweave-protocols-grpc', '--check'])
    run('host-child-format', ['rustfmt', '--edition', '2024', '--config', 'skip_children=true', '--check',
                            'native/protocols/grpc/host.rs', 'native/protocols/grpc/exports.rs', 'native/protocols/grpc/host_tests.rs'])
    run('strict-clippy', ['cargo', 'clippy', '--locked', '--offline', '-p', 'nativeweave-protocols-grpc', '-p', 'nativeweave-server', '--all-targets', '--', '-D', 'warnings'])
    for profile in ('debug', 'release'):
        release = ['--release'] if profile == 'release' else []
        run('leaf-' + profile, ['cargo', 'test', '--locked', '--offline', *release, '-p', 'nativeweave-protocols-grpc'])
        run('core-host-' + profile, ['cargo', 'test', '--locked', '--offline', *release, '-p', 'nativeweave-server', 'engine::grpc::tests'])
    run('native-release', ['cargo', 'build', '--locked', '--offline', '--release', '-p', 'nativeweave-server', '--lib'])
    native = ROOT / 'target/release/libnativeweave_server.so'
    result['native'] = dict(path=str(native.relative_to(ROOT)), sha256=sha(native), bytes=native.stat().st_size)
    run('native-exports', ['nm', '-D', '--defined-only', str(native)])
    expected = sorted(set(re.findall(r'NW_CDECL (nw_server_\w+)\(', (ROOT / 'native/server/host/include/nativeweave_server_api_v1.h').read_text())))
    actual = sorted(line.split()[-1] for line in (output / 'native-exports.log').read_text().splitlines()
                    if line.split() and line.split()[-1].startswith('nw_server_'))
    if len(expected) != 48 or actual != expected:
        raise RuntimeError('The composed native library must preserve exactly the original 48 Server ABI exports.')
    result['server_exports'] = actual
    if sources() != initial or git('status', '--porcelain'):
        raise RuntimeError('Verification changed committed source, generated inputs or lockfiles.')
    result['status'] = 'passed'
except BaseException as error:
    result['status'] = 'failed'
    result['error'] = str(error)
    raise
finally:
    result['finished_utc'] = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
    (output / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
