#!/usr/bin/env python3
"""Build and run B22 public API composition from immutable ordinary packages.

The executable sends actual HTTP/TLS and gRPC/MCP traffic to the Rust host.
This controller separately reconciles durable SQL state after process exit.
These short rows do not satisfy the separate fault or long-duration profiles.
"""
import argparse
from contextlib import contextmanager
from datetime import datetime, timezone
import csv
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
import traceback
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]
ASSEMBLY = 'NativeWeave.Verification.EndToEnd.ApiProbe'
COMMON = ['-m:1', '-nr:false', '-p:UseSharedCompilation=false']
DIRECT = {'NativeWeave.Database.Sqlite', 'NativeWeave.Database.PostgreSql',
          'NativeWeave.Server.Grpc', 'NativeWeave.Server.Mcp'}
REQUIRED = [provider + '-' + mode for provider in ('sqlite', 'postgresql') for mode in ('jit', 'aot')]

# Reuse the accepted B21 package identity/inventory helpers, not its passed rows.
HELPER_PATH = ROOT / 'tests/Reference.TaskService/package_probe.py'
spec = importlib.util.spec_from_file_location('b21_package_identity', HELPER_PATH)
identity = importlib.util.module_from_spec(spec)
spec.loader.exec_module(identity)
sha, save, inventory = identity.sha, identity.save, identity.inventory


def utc():
    return datetime.now(timezone.utc).isoformat()


class Controller:
    def __init__(self, args):
        self.args = args
        self.output = args.output.resolve()
        if self.output.exists():
            raise ValueError('Use a new output directory; never overwrite an executed attempt.')
        self.source = args.source.resolve() / 'src/Verification.EndToEnd/ApiProbe'
        if self.output.is_relative_to(args.source.resolve()):
            raise ValueError('Independent package consumer output must be outside the source worktree.')
        self.output.mkdir(parents=True)
        self.feed = args.feed.resolve()
        packages, native = identity.package_inputs(self.feed, '0.1.0-preview.1')
        if not DIRECT.issubset(packages):
            raise ValueError('The exact packed gRPC/MCP components are required.')
        self.native = native
        self.state = {'schema_version': 1, 'task': 'B22', 'platform': 'linux-x64',
            'claim': 'Public API composition only; not full B22 fault/soak or B24 pristine acceptance',
            'started_utc': utc(), 'required_rows': REQUIRED, 'rows': {}, 'commands': [], 'builds': {},
            'source': identity.git_identity(self.source), 'inputs': {
                'source_sha256': inventory(self.source), 'controller_sha256': sha(__file__),
                'accepted_identity_helper_sha256': sha(HELPER_PATH), 'packages': packages,
                'native_sha256': native, 'global_json_sha256': sha(args.source / 'global.json')},
            'host': {'os': platform.platform(), 'machine': platform.machine(), 'python': platform.python_version()},
            'unlocked_development_restore': args.allow_unlocked_restore, 'failures': []}
        self.environment = os.environ.copy()
        self.environment['NUGET_PACKAGES'] = str(self.output / 'consumer-cache')
        self.environment['DOTNET_CLI_HOME'] = str(self.output / 'dotnet-home')
        self.environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
        self.environment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
        self.environment['DOTNET_NOLOGO'] = '1'
        self.marker = self.output / 'unexpected-rust.txt'
        traps = self.output / 'rust-traps'; traps.mkdir()
        for name in ('cargo', 'rustc', 'rustup'):
            path = traps / name
            path.write_text('#!/bin/sh\nprintf "%s\\n" ' + shlex.quote(name)
                + ' >> ' + shlex.quote(str(self.marker)) + '\nexit 197\n')
            path.chmod(0o755)
        entries = [entry for entry in self.environment.get('PATH', '').split(os.pathsep)
            if entry and not any((Path(entry) / name).exists() for name in ('cargo', 'rustc', 'rustup'))]
        self.environment['PATH'] = str(traps) + os.pathsep + os.pathsep.join(entries)
        self.checkpoint()

    def checkpoint(self):
        save(self.output / 'progress.json', self.state)

    def command(self, label, argv, *, cwd=None, timeout=900, allow_failure=False):
        argv = [str(value) for value in argv]
        log = self.output / 'logs' / (label + '.log'); log.parent.mkdir(exist_ok=True)
        record = {'label': label, 'argv': argv, 'started_utc': utc(), 'log': str(log.relative_to(self.output))}
        self.state['commands'].append(record); self.checkpoint()
        try:
            with log.open('w') as stream:
                process = subprocess.run(argv, cwd=cwd, env=self.environment,
                    stdout=stream, stderr=subprocess.STDOUT, timeout=timeout)
            record['exit_code'] = process.returncode
        except subprocess.TimeoutExpired:
            record['exit_code'] = 'timeout'
        record['ended_utc'] = utc(); record['log_sha256'] = sha(log); self.checkpoint()
        if record['exit_code'] != 0 and not allow_failure:
            raise RuntimeError(label + ' failed; inspect ' + str(log))
        return record

    def build(self, mode):
        consumer = self.output / 'consumers' / mode
        if inventory(self.source) != self.state['inputs']['source_sha256']:
            raise AssertionError('Probe source changed after the attempt was frozen.')
        shutil.copytree(self.source, consumer, ignore=shutil.ignore_patterns('bin', 'obj', '__pycache__'))
        if inventory(consumer) != self.state['inputs']['source_sha256']:
            raise AssertionError('Probe source changed during the consumer snapshot.')
        shutil.copy2(self.args.source / 'global.json', consumer / 'global.json')
        (consumer / 'Directory.Build.props').write_text('<Project />\n')
        (consumer / 'Directory.Build.targets').write_text('<Project />\n')
        project = consumer / 'ApiProbe.csproj'
        tree = ET.parse(project)
        if any(node.tag in ('Import', 'ProjectReference') for node in tree.iter()):
            raise AssertionError('Source project imports are forbidden for this package probe.')
        if {node.attrib['Include'] for node in tree.iter('PackageReference')} != DIRECT:
            raise AssertionError('Unexpected direct package surface.')
        config = ET.Element('configuration'); sources = ET.SubElement(config, 'packageSources')
        ET.SubElement(sources, 'clear')
        ET.SubElement(sources, 'add', key='candidate', value=str(self.feed))
        ET.SubElement(sources, 'add', key='nuget.org', value='https://api.nuget.org/v3/index.json')
        mapping = ET.SubElement(config, 'packageSourceMapping'); ET.SubElement(mapping, 'clear')
        ET.SubElement(ET.SubElement(mapping, 'packageSource', key='candidate'), 'package', pattern='NativeWeave.*')
        ET.SubElement(ET.SubElement(mapping, 'packageSource', key='nuget.org'), 'package', pattern='*')
        ET.ElementTree(config).write(consumer / 'NuGet.Config')
        self.state.setdefault('consumer_sources', {})[mode] = inventory(consumer)
        rid = ['-r', 'linux-x64', '-p:PublishAot=true'] if mode == 'aot' else []
        locking = [] if self.args.allow_unlocked_restore else ['--locked-mode']
        self.command(mode + '-restore', ['dotnet', 'restore', project, '--configfile', consumer / 'NuGet.Config',
            '--disable-parallel', *locking, *rid, *COMMON], cwd=consumer)
        metadata = self.output / 'metadata' / mode; metadata.mkdir(parents=True)
        assets = consumer / 'obj/project.assets.json'
        resolved = json.loads(assets.read_text())
        if any(item.get('type') == 'project' for item in resolved['libraries'].values()):
            raise AssertionError('A project dependency entered the graph.')
        cache = self.output / 'consumer-cache'
        if {Path(folder).resolve() for folder in resolved['packageFolders']} != {cache}:
            raise AssertionError('A global NuGet cache entered the consumer graph.')
        archives = {}
        for name, item in resolved['libraries'].items():
            if item.get('type') != 'package':
                continue
            files = list((cache / item['path']).glob('*.nupkg'))
            if len(files) != 1:
                raise AssertionError('Missing resolved package archive: ' + name)
            archives[name] = sha(files[0])
            package = name.rsplit('/', 1)[0]
            if package.startswith('NativeWeave.'):
                expected = self.state['inputs']['packages'].get(package)
                if not expected or name != package + '/' + expected['version'] or archives[name] != expected['sha256']:
                    raise AssertionError('Resolved package differs from fixed producer feed: ' + name)
        shutil.copy2(assets, metadata / 'project.assets.json')
        lock = 'packages.linux-x64.aot.lock.json' if mode == 'aot' else 'packages.lock.json'
        shutil.copy2(consumer / lock, metadata / lock)
        save(metadata / 'resolved-archive-hashes.json', archives)
        destination = self.output / 'artifacts' / mode
        verb = 'publish' if mode == 'aot' else 'build'
        self.command(mode + '-' + verb, ['dotnet', verb, project, '-c', 'Release', '--no-restore',
            *rid, *COMMON, '-o', destination], cwd=consumer)
        for name, expected in self.native.items():
            files = list(destination.rglob(name))
            if not files or any(sha(path) != expected for path in files):
                raise AssertionError('Consumer native bytes changed: ' + name)
        self.state['builds'][mode] = {'artifact_directory': str(destination.relative_to(self.output)),
            'artifacts_sha256': inventory(destination), 'metadata_sha256': inventory(metadata)}
        self.checkpoint()
        return destination

    def certificate(self):
        certificate, key = self.output / 'localhost.pem', self.output / 'localhost.private.pem'
        self.command('certificate', ['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-sha256', '-nodes',
            '-days', '30', '-subj', '/CN=localhost', '-addext', 'subjectAltName=DNS:localhost,IP:127.0.0.1',
            '-addext', 'extendedKeyUsage=serverAuth', '-keyout', key, '-out', certificate])
        key.chmod(0o600)
        self.state['tls'] = {'certificate_sha256': sha(certificate),
            'validation': 'Exact local self-signed certificate and no hostname mismatch; actual HTTP1/HTTP2 TLS',
            'private_key_not_for_publication': key.name}
        self.checkpoint(); return certificate, key

    @contextmanager
    def postgres(self):
        binary = self.args.postgres_bin.resolve()
        version = self.command('postgres-version', [binary / 'postgres', '--version'])
        observed = (self.output / version['log']).read_text().strip()
        if not observed.endswith(' 18.0'):
            raise AssertionError('Expected pinned PostgreSQL 18.0.')
        directory = self.output / 'postgres'; directory.mkdir()
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0)); port = sock.getsockname()[1]
        data = directory / 'data'
        self.command('postgres-init', [binary / 'initdb', '-D', data, '-U', 'nativeweave',
            '--auth=trust', '--no-locale', '--encoding=UTF8'])
        self.command('postgres-start', [binary / 'pg_ctl', '-D', data, '-l', directory / 'server.log',
            '-o', f"-h 127.0.0.1 -p {port} -k ''", '-w', 'start'])
        self.state['postgres'] = {'version': observed, 'binary_sha256': sha(binary / 'postgres'),
            'client_sha256': sha(binary / 'psql'), 'durability': 'Unmodified initdb defaults; no fsync/synchronous_commit disable'}
        self.checkpoint()
        try:
            yield f'postgresql://nativeweave@127.0.0.1:{port}'
        finally:
            self.command('postgres-stop', [binary / 'pg_ctl', '-D', data, '-m', 'fast', '-w', 'stop'])

    def independent_sql(self, provider, database, query):
        if provider == 'sqlite':
            with sqlite3.connect(database) as connection:
                return connection.execute(query).fetchall()
        process = subprocess.run([str(self.args.postgres_bin / 'psql'), database, '-X', '--csv', '-t',
            '-v', 'ON_ERROR_STOP=1', '-c', query], env=self.environment, capture_output=True, text=True, timeout=30)
        if process.returncode:
            raise RuntimeError('Independent psql failed: ' + process.stderr[-1000:])
        return [tuple(row) for row in csv.reader(io.StringIO(process.stdout)) if row]

    def run_row(self, provider, mode, artifacts, certificate, key, pgbase=None):
        row = provider + '-' + mode; directory = self.output / 'rows' / row; directory.mkdir(parents=True)
        if provider == 'postgresql':
            name = 'b22_' + mode
            self.command(row + '-create-database', [self.args.postgres_bin / 'psql', pgbase + '/postgres',
                '-X', '-v', 'ON_ERROR_STOP=1', '-c', 'CREATE DATABASE ' + name])
            database = pgbase + '/' + name
        else:
            database = str(directory / 'database.sqlite')
        config = {'provider': provider, 'database': database, 'outputDirectory': str(directory),
            'tlsCertificatePem': str(certificate), 'tlsPrivateKeyPem': str(key), 'allowLocalDatabaseTlsDisable': provider == 'postgresql'}
        config_path = directory / 'config.private.json'; save(config_path, config); config_path.chmod(0o600)
        executable = artifacts / (ASSEMBLY + ('.dll' if mode == 'jit' else ''))
        argv = ['dotnet', executable] if mode == 'jit' else [executable]
        before = inventory(artifacts)
        if before != self.state['builds'][mode]['artifacts_sha256']:
            raise AssertionError('Artifact mutated before execution.')
        entry = self.state['rows'][row] = {'status': 'running', 'started_utc': utc(), 'executable_sha256': sha(executable)}
        self.checkpoint()
        try:
            run = self.command(row + '-execute', [*argv, '--config', config_path], timeout=150, allow_failure=True)
            entry['command'] = run
            receipt_path = directory / 'receipt.json'
            if not receipt_path.is_file():
                raise AssertionError('Actual probe produced no completed receipt.')
            receipt = json.loads(receipt_path.read_text())
            entry['receipt_sha256'] = sha(receipt_path)
            if run['exit_code'] != 0 or receipt['failures'] or any(case['status'] != 'passed' for case in receipt['cases']):
                raise AssertionError('Actual public API case failed; see immutable receipt and process log.')
            if receipt['dynamicCodeSupported'] != (mode == 'jit'):
                raise AssertionError('Actual runtime mode does not match requested row.')
            frozen_cases = json.loads((self.output / 'consumers' / mode / 'required-cases.json').read_text())
            required = frozen_cases['common'] + (frozen_cases['postgresql'] if provider == 'postgresql' else [])
            observed = [case['id'] for case in receipt['cases']]
            if len(observed) != len(set(observed)) or set(observed) != set(required):
                raise AssertionError('Missing, duplicated or unexpected public API oracle IDs.')
            if inventory(artifacts) != before:
                raise AssertionError('Artifact mutated during execution.')
            independent = {}
            queries = {'rows': ('SELECT COUNT(*) FROM b22_api_tx', 5),
                'canceled_commit_absent': ('SELECT COUNT(*) FROM b22_api_tx WHERE id=100', 0),
                'nullable_children': ('SELECT COUNT(*) FROM b22_api_children WHERE tenant IS NULL', 2),
                'complete_fk_children': ('SELECT COUNT(*) FROM b22_api_children WHERE tenant=7 AND parent_id=1', 1)}
            for name, (query, expected) in queries.items():
                value = self.independent_sql(provider, database, query)
                if len(value) != 1 or len(value[0]) != 1 or int(value[0][0]) != expected:
                    raise AssertionError('Independent durable SQL disagrees: ' + name)
                independent[name] = {'query': query, 'expected': expected, 'observed': int(value[0][0])}
            if provider == 'sqlite':
                integrity = self.independent_sql(provider, database, 'PRAGMA integrity_check')
                foreign_keys = self.independent_sql(provider, database, 'PRAGMA foreign_key_check')
                if integrity != [('ok',)] or foreign_keys:
                    raise AssertionError('SQLite integrity/foreign key oracle failed.')
                independent['integrity'] = integrity; independent['foreign_keys'] = foreign_keys
            save(directory / 'independent-sql.json', independent)
            entry.update(status='passed', assertions=len(receipt['cases']), independent_sql_sha256=sha(directory / 'independent-sql.json'))
        except Exception as error:
            entry.update(status='failed', failure=type(error).__name__ + ': ' + str(error))
            self.state['failures'].append({'row': row, 'failure': entry['failure']})
        finally:
            entry['ended_utc'] = utc(); self.checkpoint()
            print(row + ': ' + entry['status'], flush=True)

    def run(self):
        try:
            artifacts = {mode: self.build(mode) for mode in self.args.modes}
            certificate, key = self.certificate()
            if 'sqlite' in self.args.providers:
                for mode in self.args.modes:
                    self.run_row('sqlite', mode, artifacts[mode], certificate, key)
            if 'postgresql' in self.args.providers:
                with self.postgres() as pgbase:
                    for mode in self.args.modes:
                        self.run_row('postgresql', mode, artifacts[mode], certificate, key, pgbase)
        except Exception as error:
            self.state['failures'].append({'controller': type(error).__name__ + ': ' + str(error)})
            (self.output / 'controller-failure.log').write_text(traceback.format_exc())
        self.state['consumer_rust_invocations'] = self.marker.read_text().splitlines() if self.marker.exists() else []
        if self.state['consumer_rust_invocations']:
            self.state['failures'].append({'consumer_rust_invocations': self.state['consumer_rust_invocations']})
        self.state['ended_utc'] = utc()
        self.state['all_four_rows_passed'] = all(self.state['rows'].get(row, {}).get('status') == 'passed' for row in REQUIRED)
        self.state['public_api_matrix_complete'] = self.state['all_four_rows_passed'] and not self.state['failures'] and not self.args.allow_unlocked_restore
        self.state['b22_soak_acceptance'] = False
        self.checkpoint()
        return 1 if self.state['failures'] else 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=ROOT)
    parser.add_argument('--feed', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--postgres-bin', type=Path, default=Path(os.environ.get('NW_POSTGRES_BIN', '/usr/lib/postgresql/18/bin')))
    parser.add_argument('--providers', nargs='+', choices=['sqlite', 'postgresql'], default=['sqlite', 'postgresql'])
    parser.add_argument('--modes', nargs='+', choices=['jit', 'aot'], default=['jit', 'aot'])
    parser.add_argument('--allow-unlocked-restore', action='store_true', help='Development lock generation only; cannot complete matrix acceptance')
    args = parser.parse_args()
    return Controller(args).run()


if __name__ == '__main__':
    raise SystemExit(main())
