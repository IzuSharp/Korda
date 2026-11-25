"""Bind exact CMake inputs, selected C/C++ dependencies, notices and ELF output."""
from __future__ import annotations
import json
from pathlib import Path
import re
from model import DATABASE_CAPABILITIES, ENGINES, RID, ROOT, json_bytes, module, sha

SYSTEM_DEPENDENCIES = ['libc.so.6', 'libgcc_s.so.1', 'libm.so.6', 'ld-linux-x86-64.so.2',
                       'libcrypto.so.3', 'libssl.so.3', 'libz.so.1']


def verified_record(record):
    path = Path(record['path'])
    if not path.is_file() or sha(path) != record['sha256']:
        raise ValueError('Recorded build input changed: ' + str(path))
    return path


def collect(build_manifest_path, provider=None):
    build = json.loads(Path(build_manifest_path).read_text())
    if (build.get('schemaVersion'), build.get('task'), build.get('rid'), build.get('target'),
        build.get('sourceDirty'), build.get('preset')) != (2, 'DB-CPP-01', RID, 'x86_64-unknown-linux-gnu', False, 'linux-x64-' + (provider or 'release')):
        raise ValueError('Exact clean C++ Linux Release producer evidence is required')
    if not re.fullmatch(r'[0-9a-f]{40}', build.get('sourceCommit', '')):
        raise ValueError('Native source commit is missing')
    lock_path = ROOT / 'native/dependencies.lock.json'
    if build.get('dependenciesLockSha256') != sha(lock_path):
        raise ValueError('Native dependency/toolchain lock differs from the build')
    lock = json.loads(lock_path.read_text())
    if not build.get('inputSha256'):
        raise ValueError('Source/profile input hashes are missing')
    for name, expected in build['inputSha256'].items():
        path = (ROOT / name).resolve()
        if not path.is_relative_to(ROOT) or sha(path) != expected:
            raise ValueError('Bound native source/profile input changed: ' + name)
        if path.suffix == '.rs' or path.name in ('Cargo.toml', 'Cargo.lock'):
            raise ValueError('Rust product input entered the C++ build')
    for item in build['records'].values():
        verified_record(item)
    for item in build['linkedInputs'].values():
        verified_record(item)
    for item in build['commands']:
        verified_record(item)
        if item['exitCode'] != 0:
            raise ValueError('Candidate contains a failed native build stage')
    compilation = json.loads(verified_record(build['records']['compile_commands.json']).read_text())
    selected_commands = [item for item in compilation if '/native/cpp/src/' in item['file']]
    if not selected_commands:
        raise ValueError('No project C++ compiler commands were retained')
    for item in selected_commands:
        command = item['command']
        if ('clang++' not in command or '-std=c++20' not in command or '-stdlib=libc++' not in command
                or re.search(r'-m(?:arch|cpu)=(?:native|x86-64-v[234])', command)):
            raise ValueError('Compiler/profile differs from pinned portable C++20 configuration')
        if 'NW_HOST_TEST_HOOKS' in command and '/CMakeFiles/nw_database_' in item.get('output', command):
            raise ValueError('Fault-injection hooks entered a product target')
    for tool in lock['toolchain'].values():
        for name, expected in tool['executables'].items():
            if build['tools'].get(name, {}).get('sha256') != expected:
                raise ValueError('Toolchain executable is not pinned: ' + name)
    for item in lock['dependencies']:
        if provider and provider not in item['providers']:
            continue
        if item['name'] == 'libpq':
            if build['postgres']['version'] != item['version'] or 'clang' not in build['postgres']['compiler']:
                raise ValueError('libpq source/compiler version mismatch')
            continue
        actual = build['dependencySources'][item['name']]
        if actual['archiveSha256'] != item['archiveSha256'] or sha(actual['archive']) != item['archiveSha256']:
            raise ValueError('Dependency archive provenance mismatch')
        for name, expected in item['sourceFiles'].items():
            if sha(Path(actual['source']) / name) != expected:
                raise ValueError('Dependency source changed after compilation')
    signatures = json.loads((ROOT / 'native/database/binding/signatures-v1.json').read_text())['signatures']
    extension = json.loads((ROOT / 'native/database/binding/query-command-v1.json').read_text())
    if (extension['version'] != 1 or extension['capability_bit'] != 26 or
            DATABASE_CAPABILITIES[26] != extension['capability']):
        raise ValueError('Query command extension capability differs from the package descriptor')
    signatures += extension['signatures']
    exports = [re.search(r'\b(nw_db_\w+_v1)\(', item).group(1) for item in signatures]
    exports += ['nw_db_diagnostic_count_v1', 'nw_database_get_abi_v1']
    assets = {item['engineId']: item for item in build['assets']}
    if set(assets) != ({'database.' + provider} if provider else set(ENGINES)) or len(assets) != len(build['assets']):
        raise ValueError('Exactly the two selected database native artifacts are required')
    profile = module('domain_linux_profile', 'src/Packaging.Linux/linux_profile.py')
    result = {}
    for engine, asset in assets.items():
        binary = verified_record(asset)
        if sha(asset['compilerArtifactPath']) != asset['sha256'] or sha(asset['installedArtifactPath']) != asset['sha256']:
            raise ValueError('Installed and linked artifact provenance disagree')
        provider = engine.removeprefix('database.')
        allowed = SYSTEM_DEPENDENCIES if provider == 'postgresql' else [x for x in SYSTEM_DEPENDENCIES if x not in ('libssl.so.3', 'libz.so.1')]
        audit = profile.inspect(binary, allowed_dependencies=allowed,
                                expected_exports=exports + ['nw_database_' + provider + '_get_abi_v1'])
        records = [item for item in lock['dependencies'] if provider in item['providers']]
        notice_names = {item['notice'] for item in records}
        notice_names.update(item['notice'] for item in lock['referenceOnly'] if 'notice' in item)
        notice_names.update(lock['runtime']['notices'])
        notice_names.add('native/cpp/licenses/openssl-LICENSE.txt')
        if provider == 'postgresql':
            notice_names.add('native/cpp/licenses/zlib-LICENSE.txt')
        entries = {}; notices = []
        for name in sorted(notice_names):
            target = 'korda/licenses/' + Path(name).name
            entries[target] = (ROOT / name).read_bytes()
            notices.append({'sourcePath': name, 'packagePath': target, 'sha256': sha(ROOT / name)})
        inventory = {'schemaVersion': 2, 'engineId': engine, 'nativeSha256': asset['sha256'],
                     'sourceCommit': build['sourceCommit'], 'sourceTree': build['sourceTree'], 'sourceDirty': False,
                     'dependenciesLockSha256': build['dependenciesLockSha256'], 'preset': build['preset'],
                     'language': 'C++20', 'tools': build['tools'], 'environment': build['environment'],
                     'inputSha256': build['inputSha256'], 'dependencies': records, 'cppRuntime': lock['runtime'],
                     'referenceOnly': lock['referenceOnly'],
                     'linkedInputs': {name: value for name, value in build['linkedInputs'].items()
                                      if provider == 'postgresql' or name not in
                                      ('NW_LIBPQ_STATIC', 'NW_PGCOMMON_SHARED_STATIC', 'NW_PGPORT_SHARED_STATIC',
                                       'OPENSSL_SSL_LIBRARY', 'ZLIB_LIBRARY_RELEASE')},
                     'buildRecords': build['records'], 'compilerArtifact': asset['compilerArtifactPath'],
                     'notices': notices, 'elf': audit,
                     'scope': 'Selected C/C++ inputs. System OpenSSL/zlib/glibc are platform-provided. No Rust input, runtime or compiler is used. Local candidate; no public signer assertion.'}
        entries['korda/dependencies.json'] = json_bytes(inventory)
        entries['NOTICE'] = ('Korda C++ database native implementation.\n'
            'Project sources: MIT; C++ query algorithms adapted from SeaQuery 1.0.2 under MIT.\n'
            'nlohmann/json 3.12.0: MIT. SQLite 3.51.3: public domain (SQLite provider).\n'
            'PostgreSQL/libpq 18.0: PostgreSQL license (PostgreSQL provider).\n'
            'LLVM libc++/libc++abi 23.1.5 are statically linked under Apache-2.0 WITH LLVM-exception.\n'
            'OpenSSL 3 (Apache-2.0) and platform runtime libraries are dynamically provided by the OS; the PostgreSQL leaf also uses zlib 1.x (zlib license).\n'
            'Original notices and file-bound input/license records are in korda/licenses and dependencies.json.\n').encode()
        result[engine] = {**asset, 'path': binary, 'inventory': inventory, 'entries': entries}
    return build, result
