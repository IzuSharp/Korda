#!/usr/bin/env python3
"""Build the matching direct Hyper/Tonic fixture with the repository's locked crates."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tomllib

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def sha(path):
    with Path(path).open('rb') as stream: return hashlib.file_digest(stream, 'sha256').hexdigest()


def save(path, value): path.write_text(json.dumps(value, indent=2, sort_keys=True) + '\n')


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--client-build', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--target-dir', type=Path, help='Reusable Cargo compiler cache, outside immutable copied binary evidence')
    args = p.parse_args()
    client = json.loads(args.client_build.read_text())
    if not client['eligible_for_measurement'] or client['toolchain'].get('isa') != 'x86-64-v2': raise ValueError('Pinned AOT client required')
    out = args.output.resolve(); out.mkdir(parents=True, exist_ok=False)
    source = out / 'source'; shutil.copytree(HERE / 'DirectRust', source, ignore=shutil.ignore_patterns('target', 'Cargo.lock'))
    shutil.copy2(ROOT / 'Cargo.lock', source / 'Cargo.lock')
    env = dict(os.environ)
    env.pop('RUSTFLAGS', None); env.pop('CARGO_ENCODED_RUSTFLAGS', None)
    commands = []
    def command(name, argv):
        with (out / (name + '.log')).open('w') as log:
            process = subprocess.run(argv, cwd=source, env=env, stdout=log, stderr=subprocess.STDOUT)
        commands.append({'argv': argv, 'exit_code': process.returncode, 'log_sha256': sha(out / (name + '.log'))})
        save(out / 'commands.json', commands)
        if process.returncode: raise RuntimeError(name + ' failed')
    rust = subprocess.check_output(['rustc', '--version', '--verbose'], env=env, text=True)
    if not rust.startswith('rustc 1.100.0 '): raise ValueError('Exact Rust 1.100.0 required')
    command('resolve-offline', ['cargo', 'generate-lockfile', '--offline'])
    product = tomllib.loads((ROOT / 'Cargo.lock').read_text())
    fixture = tomllib.loads((source / 'Cargo.lock').read_text())
    pinned = {(item['name'], item['version'], item.get('checksum')) for item in product['package'] if 'source' in item}
    actual = {(item['name'], item['version'], item.get('checksum')) for item in fixture['package'] if 'source' in item}
    if not actual <= pinned: raise ValueError('Direct fixture introduced a dependency version/checksum absent from the product lock: ' + repr(actual - pinned))
    target = (args.target_dir or out / 'target').resolve()
    command('release', ['cargo', 'build', '--release', '--locked', '--offline', '--target-dir', str(target)])
    executable = out / 'perf01-direct-protocols'; shutil.copy2(target / 'release/perf01-direct-protocols', executable)
    files = {str(executable): sha(executable)}
    files.update({path: digest for path, digest in client['artifacts_sha256'].items() if Path(path).is_relative_to(Path(client['client']).parent)})
    save(out / 'build.json', {'schema_version': 1, 'task': 'Perf-01', 'route': 'direct-rust', 'platform': 'linux-x64',
        'eligible_for_measurement': True, 'server': str(executable), 'client': client['client'], 'server_native_sha256': {},
        'artifacts_sha256': files, 'client_build_sha256': sha(args.client_build),
        'driver_source_commit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
        'driver_source_sha256': {str(path.relative_to(source)): sha(path) for path in source.rglob('*') if path.is_file()},
        'source_files_sha256': {str(path): sha(path) for path in source.rglob('*') if path.is_file()},
        'lock_hashes': {str(ROOT / 'Cargo.lock'): sha(ROOT / 'Cargo.lock'), str(source / 'Cargo.lock'): sha(source / 'Cargo.lock')},
        'toolchain': {'rustc': rust, 'configuration': 'Release', 'isa': 'x86-64-default', 'RUSTFLAGS': None, 'CARGO_ENCODED_RUSTFLAGS': None,
                      'lto': 'thin', 'codegen_units': 1, 'panic': 'unwind', 'runtime': 'Tokio current_thread, same as NativeWeave server'},
        'scope': 'Direct Hyper/rustls/Tonic, matching fixture semantics and conservative native payload quota; typed fixture protobuf codec, no general protobuf groups. No NativeWeave runtime dependency.',
        'third_party': 'Only versions and checksums already in root Cargo.lock; crate source/license metadata remains in Cargo registry and root third-party inventory.',
        'commands_sha256': sha(out / 'commands.json')})
    print(out / 'build.json')


if __name__ == '__main__': main()
