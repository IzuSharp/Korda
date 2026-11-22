#!/usr/bin/env python3
"""Build/install both C++ provider assets from clean, fully bound inputs."""
import argparse
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
from cpp_toolchain import LOCK, ROOT, sha, sources


def capture(argv, env=None):
    return subprocess.check_output(argv, cwd=ROOT, env=env, text=True).strip()


def run(argv, output, label, env):
    log = output / (label + '.log')
    print('+ ' + ' '.join(map(str, argv)), flush=True)
    with log.open('wb') as stream:
        result = subprocess.run(argv, cwd=ROOT, env=env, stdout=stream, stderr=subprocess.STDOUT)
    if result.returncode:
        raise RuntimeError(f'{label} failed ({result.returncode}); inspect {log}')
    return {'command': list(map(str, argv)), 'exitCode': 0, 'path': str(log), 'sha256': sha(log)}


def cache_values(path):
    result = {}
    for line in path.read_text().splitlines():
        if line and not line.startswith(('#', '//')) and '=' in line and ':' in line.split('=', 1)[0]:
            key, value = line.split('=', 1)
            result[key.split(':', 1)[0]] = value
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    output = parser.parse_args().output.resolve()
    if (platform.system(), platform.machine().lower()) != ('Linux', 'x86_64'):
        raise RuntimeError('This acceptance row requires Linux x64')
    if output.exists():
        raise RuntimeError('Choose a fresh output; retained candidate evidence is immutable')
    if capture(['git', 'status', '--porcelain']):
        raise RuntimeError('Commit the maintainer source before building a candidate')
    source = capture(['git', 'rev-parse', 'HEAD'])
    source_tree = capture(['git', 'rev-parse', 'HEAD^{tree}'])
    files = capture(['git', 'ls-files', '--', 'native', 'CMakeLists.txt', 'CMakePresets.json', '.clang-format',
                     '.clang-tidy', 'eng/cpp_toolchain.py', 'eng/task07_native.py', 'eng/postgres-tools.sh']).splitlines()
    inputs = {name: sha(ROOT / name) for name in files}
    env = os.environ.copy()
    for key in ('CFLAGS', 'CXXFLAGS', 'CPPFLAGS', 'LDFLAGS', 'CMAKE_TOOLCHAIN_FILE', 'CMAKE_GENERATOR',
                'CMAKE_BUILD_TYPE', 'CMAKE_C_FLAGS', 'CMAKE_CXX_FLAGS'):
        if env.get(key):
            raise RuntimeError('Unsupported build-profile override: ' + key)
    lock = json.loads(LOCK.read_text())
    tools = {}
    for item in lock['toolchain'].values():
        for name, expected in item['executables'].items():
            selected = shutil.which(name)
            if not selected:
                raise RuntimeError('Missing pinned maintainer tool: ' + name)
            path = Path(selected).resolve(strict=True)
            if sha(path) != expected:
                raise RuntimeError('Tool is not the checksum-pinned LLVM distribution: ' + name)
            tools[name] = {'path': selected, 'realPath': str(path), 'sha256': sha(path),
                           'version': capture([selected, '--version'])}
    target = capture([tools['clang']['path'], '-dumpmachine'])
    if target != 'x86_64-unknown-linux-gnu':
        raise RuntimeError('Unexpected Clang target: ' + target)
    output.mkdir(parents=True)
    logs = output / 'logs'; logs.mkdir()
    dependency_sources = sources(ROOT / 'artifacts/dependencies')
    pg = Path(env.get('NW_POSTGRES_BIN', shutil.which('pg_config') or '')).resolve()
    pg_config = pg / 'pg_config' if pg.is_dir() else pg
    if not pg_config.is_file() or capture([str(pg_config), '--version']) != 'PostgreSQL 18.0':
        raise RuntimeError('Build pinned PostgreSQL 18.0 with eng/postgres-tools.sh')
    pg_prefix = pg_config.parent.parent
    pg_cc = capture([str(pg_config), '--cc'])
    if 'clang' not in Path(pg_cc).name:
        raise RuntimeError('libpq must be built with selected Clang, not the default system compiler')
    pg_record_path = pg_prefix / 'nativeweave-postgresql-build.json'
    pg_record = json.loads(pg_record_path.read_text())
    pg_lock = next(item for item in lock['dependencies'] if item['name'] == 'libpq')
    if (pg_record['archiveSha256'] != pg_lock['archiveSha256'] or
            pg_record['compilerSha256'] != tools['clang']['sha256'] or
            pg_record['patchSha256'] != pg_lock['patches'][0]['sha256']):
        raise RuntimeError('libpq build provenance differs from pinned source/compiler')
    env['CC'], env['CXX'] = tools['clang']['path'], tools['clang++']['path']
    commands = []
    configure = ['cmake', '--preset', 'linux-x64-release', '--fresh',
                 '-DCMAKE_C_COMPILER=' + env['CC'], '-DCMAKE_CXX_COMPILER=' + env['CXX'],
                 '-DPostgreSQL_ROOT=' + str(pg_prefix),
                 '-DFETCHCONTENT_SOURCE_DIR_NW_JSON=' + dependency_sources['nlohmann-json']['source'],
                 '-DFETCHCONTENT_SOURCE_DIR_NW_SQLITE=' + dependency_sources['sqlite']['source']]
    commands.append(run(configure, logs, 'configure', env))
    commands.append(run(['cmake', '--build', '--preset', 'linux-x64-release', '--parallel',
                         env.get('CMAKE_BUILD_PARALLEL_LEVEL', '4')], logs, 'build', env))
    commands.append(run(['cmake', '--install', 'artifacts/cmake/linux-x64-release'], logs, 'install', env))
    build_dir = ROOT / 'artifacts/cmake/linux-x64-release'
    cache = cache_values(build_dir / 'CMakeCache.txt')
    for key, name in (('CMAKE_C_COMPILER', 'clang'), ('CMAKE_CXX_COMPILER', 'clang++')):
        if Path(cache[key]).resolve() != Path(tools[name]['realPath']):
            raise RuntimeError('CMake silently selected another compiler')
    bound_files = {name: {'path': cache[name], 'sha256': sha(cache[name])} for name in
                   ('NW_LIBPQ_STATIC', 'NW_PGCOMMON_SHARED_STATIC', 'NW_PGPORT_SHARED_STATIC',
                    'NW_LIBCXX_STATIC', 'NW_LIBCXXABI_STATIC', 'OPENSSL_CRYPTO_LIBRARY', 'OPENSSL_SSL_LIBRARY', 'ZLIB_LIBRARY_RELEASE')}
    for name in ('NW_LIBPQ_STATIC', 'NW_PGCOMMON_SHARED_STATIC', 'NW_PGPORT_SHARED_STATIC'):
        item = bound_files[name]
        if pg_record['files'].get('install/lib/' + Path(item['path']).name) != item['sha256']:
            raise RuntimeError('libpq archive changed after its source build')
    copies = {}
    for name in ('compiler-provenance.txt', 'compile_commands.json', 'CMakeCache.txt', 'install_manifest.txt'):
        shutil.copyfile(build_dir / name, output / name)
        copies[name] = {'path': str(output / name), 'sha256': sha(output / name)}
    assets = []
    for label in ('sqlite', 'postgresql'):
        installed = ROOT / 'artifacts/native-install/linux-x64-release/lib' / ('libnw_database_' + label + '.so')
        path = output / installed.name; shutil.copyfile(installed, path)
        built = build_dir / installed.name
        if sha(built) != sha(path):
            raise RuntimeError('Installed artifact differs from linked CMake output')
        assets.append({'engineId': 'database.' + label, 'path': str(path), 'sha256': sha(path),
                       'bytes': path.stat().st_size, 'buildLabel': label,
                       'compilerArtifactPath': str(built), 'installedArtifactPath': str(installed)})
    manifest = {'schemaVersion': 2, 'task': 'DB-CPP-01', 'sourceCommit': source, 'sourceTree': source_tree,
                'sourceDirty': False, 'rid': 'linux-x64', 'target': target, 'profile': 'release',
                'preset': 'linux-x64-release', 'language': 'C++20', 'tools': tools,
                'inputSha256': inputs, 'dependenciesLockSha256': sha(LOCK), 'dependencySources': dependency_sources,
                'linkedInputs': bound_files, 'records': copies, 'commands': commands, 'assets': assets,
                'postgres': {'version': '18.0', 'compiler': pg_cc, 'configure': capture([str(pg_config), '--configure']),
                             'build': pg_record, 'provenanceSha256': sha(pg_record_path)},
                'environment': {'CC': env['CC'], 'CXX': env['CXX']},
                'rustOnPath': {name: shutil.which(name) for name in ('rustc', 'cargo', 'rustup')},
                'scope': 'Exact C++ producer inputs/assets; clean-machine tool absence is a separate CI receipt.'}
    if source != capture(['git', 'rev-parse', 'HEAD']) or capture(['git', 'status', '--porcelain']):
        raise RuntimeError('Source changed during native candidate production')
    if any(sha(ROOT / name) != value for name, value in inputs.items()):
        raise RuntimeError('A bound source input changed during native candidate production')
    (output / 'manifest.json').write_text(json.dumps(manifest, indent=2, sort_keys=True) + '\n')
    print(json.dumps({'manifest': str(output / 'manifest.json'), 'sha256': sha(output / 'manifest.json')}))


if __name__ == '__main__':
    main()
