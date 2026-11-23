#!/usr/bin/env python3
"""Execute one immutable business source against fixed Rust packages and C++ packages.

The baseline phase creates and retains old-engine snapshots. The candidate phase
loads these bytes directly and compares every semantic field, independently of
the product's native tests. All consumers are ordinary outside-tree NuGet builds.
"""
import argparse
import base64
from contextlib import contextmanager
import csv
from datetime import datetime, timedelta, timezone
from decimal import Decimal
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import platform
import shlex
import shutil
import socket
import sqlite3
import subprocess
import tempfile
import traceback
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
ASSEMBLY = 'Korda.Migration.Cpp.Consumer'
RUST_COMMIT = '465cd0f90e6af05ec50eb581156240377cf0825b'
ROWS = [provider + '-' + mode for provider in ('sqlite', 'postgresql') for mode in ('aot',)]
COMMON = ['-m:1', '-nr:false', '-p:UseSharedCompilation=false']
spec = importlib.util.spec_from_file_location('package_identity', ROOT / 'tests/Reference.TaskService/package_probe.py')
identity = importlib.util.module_from_spec(spec)
spec.loader.exec_module(identity)
sha, save, inventory = identity.sha, identity.save, identity.inventory


def utc():
    return datetime.now(timezone.utc).isoformat()


class Run:
    def __init__(self, args):
        self.args = args
        self.output = args.output.resolve()
        self.directory = self.output / args.phase
        if self.output.is_relative_to(ROOT):
            raise ValueError('Package consumers and databases must be outside the product source tree.')
        if self.directory.exists():
            raise ValueError('Use a new phase directory; executed attempts must not be overwritten.')
        self.source_hashes = inventory(HERE / 'Consumer')
        self.cases = json.loads((HERE / 'required-cases.json').read_text())
        reference = json.loads((HERE / 'reference.lock.json').read_text())
        self.abi_inputs = {str(path.relative_to(ROOT)): sha(path) for path in (
            HERE / 'abi_probe.py', HERE / 'abi-status.expected.json',
            ROOT / 'tests/Query.Binding/golden/parent-model.json', ROOT / 'tests/Query.Binding/golden/value.bin')}
        self.baseline = None
        if args.phase == 'candidate':
            self.baseline = json.loads((self.output / 'baseline/result.json').read_text())
            if not self.baseline.get('passed') or self.baseline['source_sha256'] != self.source_hashes:
                raise ValueError('The successful baseline must use this exact consumer source.')
            if self.baseline['postgres_version'] != self.version(args.postgres_bin / 'postgres'):
                raise ValueError('Both implementations must use the same PostgreSQL server version.')
            if self.baseline['global_json_sha256'] != sha(ROOT / 'global.json'):
                raise ValueError('Both implementations must use the same pinned .NET SDK.')
            if self.baseline['controller_sha256'] != sha(__file__) or self.baseline['required_cases_sha256'] != sha(HERE / 'required-cases.json'):
                raise ValueError('Both implementations must use the same comparison driver and required cases.')
            if self.baseline['reference_lock_sha256'] != sha(HERE / 'reference.lock.json'):
                raise ValueError('The fixed Rust reference inventory changed between phases.')
            if self.baseline['abi_inputs_sha256'] != self.abi_inputs:
                raise ValueError('The frozen public-ABI probes changed between phases.')
        self.directory.mkdir(parents=True)
        self.package_prefix = 'NativeWeave' if args.phase == 'baseline' else 'Korda'
        packages, native = identity.package_inputs(args.feed, args.version, self.package_prefix)
        if args.phase == 'baseline':
            if native != reference['native_sha256'] or {package['filename']: package['sha256'] for package in packages.values()} != reference['package_sha256']:
                raise ValueError('Rust package/native hashes differ from reference.lock.json.')
            for package in packages.values():
                if package['repository'].get('commit') != RUST_COMMIT:
                    raise ValueError('Rust package provenance differs from the fixed product commit.')
        self.state = {'schema_version': 1, 'task': 'DB-CPP-01', 'phase': args.phase,
            'platform': 'linux-x64', 'started_utc': utc(), 'passed': False,
            'claim': 'Finite package-based behavior and history compatibility; no performance claim',
            'required_rows': ROWS, 'source_sha256': self.source_hashes,
            'source_identity': identity.git_identity(HERE), 'controller_sha256': sha(__file__),
            'required_cases_sha256': sha(HERE / 'required-cases.json'), 'identity_helper_sha256': sha(ROOT / 'tests/Reference.TaskService/package_probe.py'),
            'reference_lock_sha256': sha(HERE / 'reference.lock.json'),
            'abi_inputs_sha256': self.abi_inputs,
            'dto_variant_sha256': sha(HERE / 'DtoMapping.v2.cs.txt'),
            'global_json_sha256': sha(ROOT / 'global.json'), 'packages': packages,
            'native_sha256': native, 'postgres_version': self.version(args.postgres_bin / 'postgres'),
            'sqlite_observer_version': sqlite3.sqlite_version, 'host': platform.platform(),
            'commands': [], 'builds': {}, 'rows': {}, 'failures': []}
        self.environment = os.environ.copy()
        self.environment.update(NUGET_PACKAGES=str(self.directory / 'consumer-cache'),
            DOTNET_CLI_HOME=str(self.directory / 'dotnet-home'), DOTNET_CLI_TELEMETRY_OPTOUT='1',
            DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1', DOTNET_NOLOGO='1')
        traps = self.directory / 'tool-traps'; traps.mkdir()
        self.trap_marker = self.directory / 'unexpected-native-build.txt'
        for name in ('cargo', 'rustc', 'rustup', 'cmake', 'ninja'):
            path = traps / name
            path.write_text('#!/bin/sh\nprintf "%s\\n" ' + shlex.quote(name) + ' >> ' + shlex.quote(str(self.trap_marker)) + '\nexit 197\n')
            path.chmod(0o755)
        self.environment['PATH'] = str(traps) + os.pathsep + self.environment.get('PATH', '')
        self.sdk_feed = self.directory / 'sdk-feed'; self.sdk_feed.mkdir()
        for archive in sorted(args.sdk_cache.rglob('*.nupkg')):
            if archive.name.lower().startswith(('nativeweave.', 'korda.')):
                continue
            destination = self.sdk_feed / archive.name
            if destination.exists():
                if sha(destination) != sha(archive):
                    raise ValueError('Conflicting SDK package archives: ' + archive.name)
                continue
            try:
                os.link(archive, destination)
            except OSError:
                shutil.copy2(archive, destination)
        self.state['sdk_feed_sha256'] = inventory(self.sdk_feed)
        self.checkpoint()

    @staticmethod
    def version(binary):
        return subprocess.check_output([str(binary), '--version'], text=True).strip()

    def checkpoint(self):
        save(self.directory / 'progress.json', self.state)

    def command(self, label, argv, *, cwd=None, timeout=900):
        argv = [str(value) for value in argv]
        log = self.directory / 'logs' / (label + '.log'); log.parent.mkdir(exist_ok=True)
        record = {'label': label, 'argv': argv, 'started_utc': utc(), 'log': str(log.relative_to(self.directory))}
        self.state['commands'].append(record); self.checkpoint()
        with log.open('w') as stream:
            process = subprocess.run(argv, cwd=cwd, env=self.environment, stdout=stream, stderr=subprocess.STDOUT, timeout=timeout)
        record.update(exit_code=process.returncode, ended_utc=utc(), log_sha256=sha(log)); self.checkpoint()
        if process.returncode:
            raise RuntimeError(label + ' failed: ' + str(log))
        return log

    def build(self, mode, variant=False):
        name = mode + ('-dto-v2' if variant else '')
        directory = self.directory / 'consumers' / name
        if inventory(HERE / 'Consumer') != self.source_hashes:
            raise AssertionError('Versioned consumer source changed during the comparison.')
        shutil.copytree(HERE / 'Consumer', directory)
        if inventory(directory) != self.source_hashes:
            raise AssertionError('Consumer copy differs from the versioned source.')
        if variant:
            shutil.copy2(HERE / 'DtoMapping.v2.cs.txt', directory / 'DtoMapping.cs')
        if self.args.phase == 'baseline':
            # Compile the same business logic against the immutable Rust package namespace.
            for path in [*directory.glob('*.cs'), directory / 'Consumer.csproj']:
                path.write_text(path.read_text().replace('Korda.', 'NativeWeave.').replace('NativeWeave.Migration.Cpp.Consumer', ASSEMBLY))
        source_hashes = inventory(directory)
        if variant and source_hashes == self.source_hashes:
            raise AssertionError('The DTO change did not alter ordinary C# source.')
        shutil.copy2(ROOT / 'global.json', directory / 'global.json')
        (directory / 'Directory.Build.props').write_text('<Project />\n')
        (directory / 'Directory.Build.targets').write_text('<Project />\n')
        project = directory / 'Consumer.csproj'
        tree = ET.parse(project)
        if any(node.tag in ('Import', 'ProjectReference') for node in tree.iter()):
            raise AssertionError('The consumer may only use ordinary packages.')
        if {node.attrib['Include'] for node in tree.iter('PackageReference')} != {p.replace('Korda.', self.package_prefix + '.', 1) for p in identity.DIRECT_PACKAGES}:
            raise AssertionError('Unexpected direct package closure.')
        config = ET.Element('configuration'); sources = ET.SubElement(config, 'packageSources')
        ET.SubElement(sources, 'clear')
        ET.SubElement(sources, 'add', key='database', value=str(self.args.feed.resolve()))
        ET.SubElement(sources, 'add', key='sdk', value=str(self.sdk_feed))
        mapping = ET.SubElement(config, 'packageSourceMapping'); ET.SubElement(mapping, 'clear')
        ET.SubElement(ET.SubElement(mapping, 'packageSource', key='database'), 'package', pattern=self.package_prefix + '.*')
        ET.SubElement(ET.SubElement(mapping, 'packageSource', key='sdk'), 'package', pattern='*')
        ET.ElementTree(config).write(directory / 'NuGet.Config')
        version = ['-p:KordaPackageVersion=' + self.args.version]
        rid = ['-r', 'linux-x64', '-p:PublishAot=true'] if mode == 'aot' else []
        for label, locked in (('restore', '--use-lock-file'), ('locked-restore', '--locked-mode')):
            self.command(name + '-' + label, ['dotnet', 'restore', project, '--configfile', directory / 'NuGet.Config',
                '--disable-parallel', locked, *version, *rid, *COMMON], cwd=directory)
        assets = json.loads((directory / 'obj/project.assets.json').read_text())
        if any(item.get('type') == 'project' for item in assets['libraries'].values()):
            raise AssertionError('A source-project dependency entered the consumer.')
        cache = self.directory / 'consumer-cache'
        if {Path(folder).resolve() for folder in assets['packageFolders']} != {cache}:
            raise AssertionError('A shared cache entered the consumer restore.')
        archives = {}
        for name_version, item in assets['libraries'].items():
            if item.get('type') != 'package':
                continue
            archive, = (cache / item['path']).glob('*.nupkg')
            archives[name_version] = sha(archive)
            package = name_version.rsplit('/', 1)[0]
            if package.startswith(self.package_prefix + '.'):
                expected = self.state['packages'].get(package)
                if not expected or name_version != package + '/' + expected['version'] or sha(archive) != expected['sha256']:
                    raise AssertionError('Resolved package differs from the frozen feed: ' + name_version)
        metadata = self.directory / 'metadata' / name; metadata.mkdir(parents=True)
        shutil.copy2(directory / 'obj/project.assets.json', metadata / 'project.assets.json')
        lock = 'packages.linux-x64.aot.lock.json' if mode == 'aot' else 'packages.lock.json'
        shutil.copy2(directory / lock, metadata / lock)
        save(metadata / 'resolved-archives.json', archives)
        output = self.directory / 'artifacts' / name
        verb = 'publish' if mode == 'aot' else 'build'
        self.command(name + '-' + verb, ['dotnet', verb, project, '-c', 'Release', '--no-restore', *version, *rid, *COMMON, '-o', output], cwd=directory)
        for filename, digest in self.state['native_sha256'].items():
            paths = list(output.rglob(filename))
            if len(paths) != 1 or sha(paths[0]) != digest:
                raise AssertionError('Consumer native asset differs from package: ' + filename)
        if mode == 'aot':
            if (output / ASSEMBLY).read_bytes()[:4] != b'\x7fELF' or (output / (ASSEMBLY + '.dll')).exists():
                raise AssertionError('The published AOT consumer is not a real native executable.')
        self.state['builds'][name] = {'source_sha256': source_hashes, 'artifacts_sha256': inventory(output), 'archives_sha256': archives,
            'output': str(output.relative_to(self.directory)), 'native_sha256': self.state['native_sha256']}
        self.checkpoint()
        return output

    @contextmanager
    def postgres(self):
        with tempfile.TemporaryDirectory(prefix='nw-cpp-migration-pg-') as temporary:
            directory = Path(temporary)
            with socket.socket() as owner:
                owner.bind(('127.0.0.1', 0)); port = owner.getsockname()[1]
            self.command('postgres-init', [self.args.postgres_bin / 'initdb', '-D', directory, '-U', 'postgres', '--no-locale', '-A', 'trust'])
            self.command('postgres-start', [self.args.postgres_bin / 'pg_ctl', '-D', directory, '-l', self.directory / 'logs/postgres-server.log', '-o', f"-p {port} -h 127.0.0.1 -k ''", '-w', 'start'])
            try:
                yield f'postgresql://postgres@127.0.0.1:{port}'
            finally:
                self.command('postgres-stop', [self.args.postgres_bin / 'pg_ctl', '-D', directory, '-m', 'fast', '-w', 'stop'])

    def create_postgres(self, base, name):
        self.command('create-' + name, [self.args.postgres_bin / 'psql', base + '/postgres', '-X', '-v', 'ON_ERROR_STOP=1', '-c', 'CREATE DATABASE ' + name])
        return base + '/' + name

    def execute(self, provider, mode, stage, database, directory, artifacts, label, variant=False):
        directory.mkdir(parents=True, exist_ok=True)
        receipt = directory / 'receipt.json'
        name = mode + ('-dto-v2' if variant else '')
        before = inventory(artifacts)
        if before != self.state['builds'][name]['artifacts_sha256']:
            raise AssertionError('Built artifact changed before execution.')
        executable = artifacts / (ASSEMBLY)
        command = [executable]
        self.command(label, [*command, provider, database, stage, mode, receipt], timeout=180)
        value = json.loads(receipt.read_text())
        expected_cases = self.cases['common'] + self.cases[stage]
        if len(expected_cases) != len(set(expected_cases)) or set(value['Semantic']) != set(expected_cases):
            raise AssertionError('Missing, duplicate or unexpected business case groups.')
        filename = 'libnw_database_sqlite.so' if provider == 'sqlite' else 'libnw_database_postgresql.so'
        if value['NativeSha256'] != self.state['native_sha256'][filename] or value['Aot'] != (mode == 'aot') or value['RetiredCounts'] != [0] * 6:
            raise AssertionError('Runtime native provenance, mode or retirement mismatch.')
        if inventory(artifacts) != before:
            raise AssertionError('Runtime changed a built artifact.')
        if variant:
            expected_extra = ['|'.join(base64.b64encode(part.encode()).decode() for part in (str(key), '1')) for key in (1, 2, 3, 9007199254740993)]
            if value['DtoRevision'] != 'v2-four-fields' or value['DtoExtra'] != expected_extra:
                raise AssertionError('Changed DTO did not materialize all four projected fields.')
        elif value['DtoRevision'] != 'v1-three-fields' or value['DtoExtra']:
            raise AssertionError('Unexpected base DTO source.')
        return value

    def snapshot(self, provider, database, destination, label):
        if provider == 'sqlite':
            if Path(str(database) + '-wal').exists() and Path(str(database) + '-wal').stat().st_size:
                raise AssertionError('Closed SQLite source left a nonempty WAL outside the preserved file.')
            shutil.copy2(database, destination)
        else:
            self.command(label + '-dump', [self.args.postgres_bin / 'pg_dump', '--format=custom', '--no-owner', '--no-acl', '--file', destination, database])
        return {'file': str(destination.relative_to(self.output)), 'sha256': sha(destination), 'bytes': destination.stat().st_size}

    def restore(self, provider, snapshot, directory, base, name):
        directory.mkdir(parents=True, exist_ok=True)
        original = self.output / snapshot['file']
        if sha(original) != snapshot['sha256']:
            raise AssertionError('The original Rust snapshot changed.')
        if provider == 'sqlite':
            database = directory / 'database.sqlite'; shutil.copy2(original, database)
            if sha(database) != snapshot['sha256']:
                raise AssertionError('SQLite historical handoff must preserve exact old bytes before opening.')
            return str(database)
        database = self.create_postgres(base, name)
        self.command(name + '-restore', [self.args.postgres_bin / 'pg_restore', '--no-owner', '--no-acl', '--exit-on-error', '--dbname', database, original])
        return database

    def sql(self, provider, database, query):
        if provider == 'sqlite':
            with sqlite3.connect('file:' + str(database) + '?mode=ro', uri=True) as connection:
                return [list(row) for row in connection.execute(query).fetchall()]
        process = subprocess.run([str(self.args.postgres_bin / 'psql'), database, '-X', '--csv', '-t', '-v', 'ON_ERROR_STOP=1', '-c', query], env=self.environment, capture_output=True, text=True, timeout=30, check=True)
        return [row for row in csv.reader(io.StringIO(process.stdout)) if row]

    def audit(self, provider, database, continued, path):
        columns = "id,title,COALESCE(note,'<NULL>'),CAST(score AS TEXT),occurred,lower(hex(payload)),version" if provider == 'sqlite' else "id,title,COALESCE(note,'<NULL>'),score::text,to_char(occurred AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"'),encode(payload,'hex'),version"
        query = 'SELECT ' + columns + ' FROM migration_work ORDER BY id'
        raw = self.sql(provider, database, query)
        def instant(value):
            return (datetime(1970, 1, 1, tzinfo=timezone.utc) + timedelta(microseconds=int(value))).isoformat() if provider == 'sqlite' else datetime.fromisoformat(value.replace('Z', '+00:00')).astimezone(timezone.utc).isoformat()
        rows = [[int(row[0]), row[1], row[2], format(Decimal(row[3]), '.4f'), instant(row[4]), row[5], int(row[6])] for row in raw]
        values = {1: ("continued by same C#" if continued else "O'Reilly 数据庫", '<NULL>', 2 if continued else 1), 2: ('second', '', 1), 9007199254740993: ('large integer', 'last', 1)}
        values.update({10: ('committed', '<NULL>', 1), 11: ('bulk one', '<NULL>', 1), 12: ('bulk two', 'explicit note', 1)} if continued else {3: ('third', 'combining e\u0301\nline', 1)})
        expected = [[key, title, note, '123456789.0123', '2024-02-29T23:59:58.123456+00:00', '00017f80ff', version] for key, (title, note, version) in sorted(values.items())]
        if rows != expected:
            raise AssertionError('Independent durable rows differ from the explicit business oracle: ' + repr(rows))
        child_query = "SELECT id,COALESCE(CAST(work_id AS TEXT),'<NULL>'),COALESCE(body,'<NULL>') FROM migration_comment ORDER BY id"
        children = [[int(row[0]), str(row[1]), row[2]] for row in self.sql(provider, database, child_query)]
        if children != [[10, '1', '<NULL>'], [11, '1', 'comment'], [12, '<NULL>', 'unlinked']]:
            raise AssertionError('Independent relation rows differ.')
        checksum = 'lower(hex(checksum))' if provider == 'sqlite' else "encode(checksum,'hex')"
        history_query = 'SELECT version,description,' + checksum + ',success FROM _sqlx_migrations ORDER BY version'
        history = self.sql(provider, database, history_query)
        types = ('NUMERIC(18,4)', 'TIMESTAMPTZ', 'BYTEA') if provider == 'postgresql' else ('TEXT', 'INTEGER', 'BLOB')
        migrations = [
            ('Create business work', f'CREATE TABLE migration_work(id BIGINT PRIMARY KEY, title TEXT NOT NULL, note TEXT NULL, score {types[0]} NOT NULL, occurred {types[1]} NOT NULL, payload {types[2]} NOT NULL, version BIGINT NOT NULL)'),
            ('Create explicit relation', 'CREATE TABLE migration_comment(id BIGINT PRIMARY KEY, work_id BIGINT NULL REFERENCES migration_work(id), body TEXT NULL)'),
            ('Index explicit relation', 'CREATE INDEX migration_comment_work ON migration_comment(work_id)')]
        if len(history) != 3:
            raise AssertionError('Expected exactly three persisted migrations.')
        for index, (row, (description, sql)) in enumerate(zip(history, migrations), 1):
            if int(row[0]) != index or row[1] != description or row[2] != hashlib.sha384(sql.encode()).hexdigest() or str(row[3]).lower() not in ('1', 't', 'true'):
                raise AssertionError('Independent original SHA-384 history differs: ' + repr(row))
        integrity = self.sql(provider, database, 'PRAGMA integrity_check') if provider == 'sqlite' else [['not applicable']]
        if provider == 'sqlite' and integrity != [['ok']]:
            raise AssertionError('SQLite integrity check failed.')
        result = {'passed': True, 'continued': continued, 'queries': [query, child_query, history_query], 'rows': rows, 'children': children, 'history': history, 'integrity': integrity}
        save(path, result)
        return result

    @staticmethod
    def compare(expected, actual, label):
        if expected['Semantic'] != actual['Semantic']:
            keys = sorted(key for key in set(expected['Semantic']) | set(actual['Semantic']) if expected['Semantic'].get(key) != actual['Semantic'].get(key))
            raise AssertionError('Rust/C++ semantic difference in ' + label + ': ' + ', '.join(keys))

    def execute_all(self):
        self.command('dotnet-version', ['dotnet', '--version'])
        artifacts, variants = {}, {}
        with self.postgres() as base:
            for mode in ('aot',):
                for provider in ('sqlite', 'postgresql'):
                    if mode not in artifacts:
                        artifacts[mode] = self.build(mode)
                        if self.args.phase == 'candidate':
                            variants[mode] = self.build(mode, True)
                    if 'abi' not in self.state:
                        library, = artifacts[mode].rglob('libnw_database_sqlite.so')
                        receipt = self.directory / 'abi-status.json'
                        self.command('abi-status', [os.sys.executable, HERE / 'abi_probe.py', '--native-library', library, '--output', receipt], timeout=60)
                        self.state['abi'] = json.loads(receipt.read_text())
                        if not self.state['abi']['passed'] or self.state['abi']['native_sha256'] != self.state['native_sha256']['libnw_database_sqlite.so']:
                            raise AssertionError('Public-ABI results are not from the verified package asset.')
                    name = provider + '-' + mode
                    directory = self.directory / 'rows' / name; directory.mkdir(parents=True)
                    entry = self.state['rows'][name] = {'passed': False, 'snapshots': {}}
                    self.checkpoint()
                    seed_directory = directory / 'fresh'; seed_directory.mkdir()
                    database = str(seed_directory / 'database.sqlite') if provider == 'sqlite' else self.create_postgres(base, 'seed_' + mode)
                    seed = self.execute(provider, mode, 'seed', database, seed_directory, artifacts[mode], name + '-seed')
                    self.audit(provider, database, False, seed_directory / 'independent-sql.json')
                    if self.args.phase == 'baseline':
                        snapshot = self.snapshot(provider, database, directory / ('rust-seed.sqlite' if provider == 'sqlite' else 'rust-seed.dump'), name)
                        entry['snapshots']['seed'] = snapshot
                        source_database = database
                    else:
                        old = self.output / 'baseline/rows' / name
                        self.compare(json.loads((old / 'fresh/receipt.json').read_text()), seed, name + '/fresh')
                        snapshot = self.baseline['rows'][name]['snapshots']['seed']
                        entry['snapshots']['old_seed'] = snapshot
                        source_database = self.restore(provider, snapshot, directory / 'historical', base, 'historical_' + mode)
                    # observe calls migration validation/idempotence before reading the original file.
                    observed = self.execute(provider, mode, 'observe', source_database, directory / 'observe', artifacts[mode], name + '-observe')
                    self.audit(provider, source_database, False, directory / 'observe/independent-sql.json')
                    if self.args.phase == 'baseline':
                        continuation = self.restore(provider, snapshot, directory / 'continue', base, 'continued_' + mode)
                    else:
                        continuation = source_database
                        self.compare(json.loads((old / 'observe/receipt.json').read_text()), observed, name + '/historical-read')
                    continued = self.execute(provider, mode, 'continue', continuation, directory / 'continue', artifacts[mode], name + '-continue')
                    self.audit(provider, continuation, True, directory / 'continue/independent-sql.json')
                    if self.args.phase == 'candidate':
                        self.compare(json.loads((old / 'continue/receipt.json').read_text()), continued, name + '/historical-continue')
                        changed_database = self.restore(provider, snapshot, directory / 'dto-v2', base, 'dto_' + mode)
                        changed = self.execute(provider, mode, 'observe', changed_database, directory / 'dto-v2', variants[mode], name + '-dto-v2', True)
                        self.compare(observed, changed, name + '/changed-dto')
                        self.audit(provider, changed_database, False, directory / 'dto-v2/independent-sql.json')
                        entry['comparisons'] = ['fresh-seed', 'old-database-read', 'old-database-continue', 'changed-dto-same-native']
                        entry['dto_change'] = {'before': observed['DtoRevision'], 'after': changed['DtoRevision'], 'new_fields': changed['DtoExtra'], 'native_sha256_unchanged': changed['NativeSha256'] == observed['NativeSha256']}
                    entry.update(passed=True, semantic_groups={'seed': len(seed['Semantic']), 'observe': len(observed['Semantic']), 'continue': len(continued['Semantic'])}, evidence_sha256=inventory(directory))
                    self.checkpoint()
        if self.trap_marker.exists():
            raise AssertionError('A package consumer invoked a native build tool.')
        if inventory(HERE / 'Consumer') != self.source_hashes:
            raise AssertionError('Consumer source changed during matrix execution.')
        self.state.update(passed=True, ended_utc=utc(), rust_or_cpp_build_tools_invoked=False)
        save(self.directory / 'result.json', self.state)
        self.checkpoint()
        if self.args.phase == 'candidate':
            save(self.output / 'comparison.json', {'task': 'DB-CPP-01', 'passed': True,
                'baseline_result_sha256': sha(self.output / 'baseline/result.json'), 'candidate_result_sha256': sha(self.directory / 'result.json'),
                'rows': self.state['rows'], 'same_consumer_source_sha256': self.source_hashes,
                'rust_commit': RUST_COMMIT, 'cpp_packages': self.state['packages'],
                'abi_compatibility': {'baseline': self.baseline['abi'], 'candidate': self.state['abi'], 'inputs_sha256': self.abi_inputs},
                'limits': 'Finite behavior/history matrix; race, disconnect, forced commit-unknown and memory-safety gates are reported by the product regression suites.'})
        print('PASS', self.args.phase, 'SQLite/PostgreSQL x Native AOT; source, packages, native hashes and independent SQL recorded at', self.directory)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--phase', choices=('baseline', 'candidate'), required=True)
    parser.add_argument('--feed', type=Path, required=True)
    parser.add_argument('--version', required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--postgres-bin', type=Path, required=True)
    parser.add_argument('--sdk-cache', type=Path, default=Path(os.environ.get('NUGET_PACKAGES', Path.home() / '.nuget/packages')))
    args = parser.parse_args()
    run = Run(args)
    try:
        run.execute_all()
    except Exception:
        run.state['failures'].append(traceback.format_exc())
        run.state['ended_utc'] = utc(); run.checkpoint()
        save(run.directory / 'result.json', run.state)
        raise


if __name__ == '__main__':
    main()
