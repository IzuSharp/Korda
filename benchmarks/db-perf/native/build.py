#!/usr/bin/env python3
"""Build the finite direct-C++ comparison with Korda's exact compiler and driver sources."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
SQLITE_DEFINES = [
    'SQLITE_CORE', 'SQLITE_DEFAULT_FOREIGN_KEYS=1', 'SQLITE_ENABLE_API_ARMOR',
    'SQLITE_ENABLE_COLUMN_METADATA', 'SQLITE_ENABLE_DBSTAT_VTAB', 'SQLITE_ENABLE_FTS3',
    'SQLITE_ENABLE_FTS3_PARENTHESIS', 'SQLITE_ENABLE_FTS5', 'SQLITE_ENABLE_JSON1',
    'SQLITE_ENABLE_LOAD_EXTENSION=1', 'SQLITE_ENABLE_MEMORY_MANAGEMENT', 'SQLITE_ENABLE_RTREE',
    'SQLITE_ENABLE_STAT4', 'SQLITE_SOUNDEX', 'SQLITE_THREADSAFE=1', 'SQLITE_USE_URI=1',
    'SQLITE_ENABLE_UNLOCK_NOTIFY', 'HAVE_USLEEP=1', 'HAVE_ISNAN',
    '_POSIX_THREAD_SAFE_FUNCTIONS', 'HAVE_LOCALTIME_R',
]


def sha(path):
    with Path(path).open('rb') as file:
        return hashlib.file_digest(file, 'sha256').hexdigest()


def verify(path, expected):
    actual = sha(path)
    if actual != expected:
        raise ValueError(f'Input hash differs: {path}: {actual} != {expected}')
    return actual


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sqlite-source', type=Path, required=True)
    parser.add_argument('--json-source', type=Path, required=True)
    parser.add_argument('--postgres-prefix', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--reuse-sqlite-build', type=Path,
                        help='Reuse a previously verified identical SQLite library; its receipt and bytes are rechecked')
    args = parser.parse_args()
    output = args.output.resolve()
    if output.exists():
        raise ValueError('Use a fresh output directory; existing evidence is immutable')
    lock_path = ROOT / 'native/dependencies.lock.json'
    lock = json.loads(lock_path.read_text())
    deps = {value['name']: value for value in lock['dependencies']}
    cc = Path(shutil.which(os.environ.get('CC', 'clang'))).resolve()
    cxx = Path(shutil.which(os.environ.get('CXX', 'clang++'))).resolve()
    compiler_hash = verify(cc, lock['toolchain']['llvm']['executables']['clang'])
    verify(cxx, lock['toolchain']['llvm']['executables']['clang++'])
    version = subprocess.check_output([str(cc), '--version'], text=True)
    if 'clang version ' + lock['toolchain']['llvm']['version'] not in version:
        raise ValueError('The selected compiler version differs from the lock')
    sources = {}
    for name, directory in [('sqlite', args.sqlite_source), ('nlohmann-json', args.json_source)]:
        for relative, expected in deps[name]['sourceFiles'].items():
            path = directory.resolve() / relative
            sources[str(path)] = verify(path, expected)
    pg = args.postgres_prefix.resolve()
    provenance_path = pg / 'korda-postgresql-build.json'
    provenance = json.loads(provenance_path.read_text())
    if (provenance['version'] != deps['libpq']['version'] or
            provenance['archiveSha256'] != deps['libpq']['archiveSha256'] or
            provenance['compilerSha256'] != compiler_hash or
            provenance['patchSha256'] != deps['libpq']['patches'][0]['sha256']):
        raise ValueError('libpq version, archive, compiler, or explicit-configuration patch differs')
    archives = [pg / 'lib' / name for name in ('libpq.a', 'libpgcommon_shlib.a', 'libpgport_shlib.a')]
    for archive in archives:
        sources[str(archive)] = verify(archive, provenance['files']['install/lib/' + archive.name])
    llvm = cc.parent.parent
    runtime = llvm / 'lib/x86_64-unknown-linux-gnu'
    runtimes = [runtime / 'libc++.a', runtime / 'libc++abi.a']
    for archive in runtimes:
        sources[str(archive)] = sha(archive)
    output.mkdir(parents=True)
    sqlite_library = output / 'libsqlite3.so'
    sqlite_command = [str(cc), '-std=c17', '-O3', '-DNDEBUG', '-fPIC', '-shared', '-fno-omit-frame-pointer',
                      '-Wl,-soname,libsqlite3.so', *['-D' + value for value in SQLITE_DEFINES],
                      str(args.sqlite_source.resolve() / 'sqlite3.c'), '-pthread', '-ldl', '-lm', '-o', str(sqlite_library)]
    executable = output / 'db-perf-native'
    oracle_test = output / 'native-oracle-failure-test'
    profiler = output / 'libdb-perf-alloc.so'
    profile_test = output / 'allocation-profile-test'
    command = [str(cxx), '-std=c++20', '-O3', '-DNDEBUG', '-fno-omit-frame-pointer', '-Wall', '-Wextra',
               '-Werror=return-type', '-stdlib=libc++', '-nostdlib++', str(HERE / 'main.cpp'),
               '-I' + str(args.sqlite_source.resolve()), '-I' + str(args.json_source.resolve() / 'include'),
               '-I' + str(pg / 'include'), '-L' + str(output), '-Wl,-rpath,$ORIGIN', '-lsqlite3',
               *map(str, archives), *map(str, runtimes), '-lssl', '-lcrypto', '-lz', '-lm', '-ldl', '-pthread',
               '-o', str(executable)]
    reused_sqlite = None
    if args.reuse_sqlite_build:
        prior = args.reuse_sqlite_build.resolve()
        prior_receipt = prior / 'build-receipt.json'
        prior_build = json.loads(prior_receipt.read_text())
        if (prior_build['compilerSha256'] != compiler_hash or
                prior_build['sqliteVersion'] != deps['sqlite']['version'] or
                prior_build['sqliteDefines'] != SQLITE_DEFINES or
                prior_build['dependencyLockSha256'] != sha(lock_path)):
            raise ValueError('Reusable SQLite build inputs differ')
        for relative, expected in deps['sqlite']['sourceFiles'].items():
            matching = [value for key, value in prior_build['inputs'].items() if Path(key).name == Path(relative).name]
            if matching != [expected]:
                raise ValueError('Reusable SQLite source identity differs')
        reused_hash = verify(prior / 'libsqlite3.so', prior_build['outputs']['libsqlite3.so'])
        shutil.copyfile(prior / 'libsqlite3.so', sqlite_library)
        reused_sqlite = {'sourceBuild': str(prior), 'receiptSha256': sha(prior_receipt),
                         'librarySha256': reused_hash, 'reason': 'identical locked compiler, defines and SQLite source inputs'}
    oracle_command = [str(HERE / 'oracle_failure_test.cpp') if value == str(HERE / 'main.cpp')
                      else str(oracle_test) if value == str(executable) else value for value in command]
    commands = [command] if reused_sqlite else [sqlite_command, command]
    commands += [oracle_command, [str(oracle_test)]]
    commands += [
        [str(cc), '-std=c17', '-O2', '-g', '-Wall', '-Wextra', '-shared', '-fPIC',
         str(HERE / 'allocation_profile.c'), '-pthread', '-o', str(profiler)],
        [str(cc), '-std=c17', '-O2', '-g', '-Wall', '-Wextra', '-fno-builtin',
         str(HERE / 'allocation_profile_test.c'), '-pthread', '-ldl', '-o', str(profile_test)],
        ['env', 'LD_PRELOAD=' + str(profiler), 'DB_PERF_ALLOC_CAPACITY=1048576', str(profile_test)],
        ['env', 'LD_PRELOAD=' + str(profiler), 'DB_PERF_ALLOC_CAPACITY=1024', str(profile_test), '--exhaust-table'],
    ]
    results = []
    for index, current in enumerate(commands):
        with (output / f'build-{index}.log').open('w') as log:
            result = subprocess.run(current, stdout=log, stderr=subprocess.STDOUT, check=False)
        results.append(result.returncode)
        if result.returncode:
            break
    receipt = {
        'schemaVersion': 1, 'route': 'direct-native-cpp', 'compilerSha256': compiler_hash,
        'compilerVersion': version, 'language': 'C++20', 'configuration': 'Release -O3 -DNDEBUG',
        'dependencyLockSha256': sha(lock_path), 'libpqProvenanceSha256': sha(provenance_path),
        'libpqVersion': deps['libpq']['version'], 'libpqLinkage': 'static, exact Korda patched client',
        'sqliteVersion': deps['sqlite']['version'], 'sqliteLinkage': 'shared, exact locked amalgamation and product defines',
        'sqliteDefines': SQLITE_DEFINES, 'inputs': sources,
        'sqliteReusedFromVerifiedBuild': reused_sqlite,
        'sources': {path.name: sha(path) for path in [HERE / 'main.cpp', HERE / 'oracle_failure_test.cpp', Path(__file__), HERE / 'allocation_profile.c',
                    HERE / 'allocation_profile.h', HERE / 'allocation_profile_test.c']},
        'commands': commands, 'exitCodes': results,
        'logs': {path.name: sha(path) for path in output.glob('build-*.log')},
        'outputs': {path.name: sha(path) for path in [sqlite_library, executable, oracle_test, profiler, profile_test] if path.exists()},
    }
    (output / 'build-receipt.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print(json.dumps({'output': str(output), 'exitCodes': results, 'outputs': receipt['outputs']}))
    raise SystemExit(next((code for code in results if code), 0))


if __name__ == '__main__':
    main()
