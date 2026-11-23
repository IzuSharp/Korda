#!/usr/bin/env python3
"""Task-local feed composition using the already accepted A14 package/closure task."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[2]
REGISTRY = {
    'Korda.Interop.Abi': ('src/Interop.Abi', []),
    'Korda.Core': ('src/Korda.Core', ['Korda.Interop.Abi']),
    'Korda.Data': ('src/Data.Abstractions', []),
    'Korda.Database.Transactions': ('src/Data.Transactions', ['Korda.Data']),
    'Korda.Data.Streaming': ('src/Data.Streaming', ['Korda.Data']),
    'Korda.Query.Binding': ('src/Query.Binding', ['Korda.Data', 'Korda.Interop.Abi']),
    'Korda.Query.Generator': ('src/Query.Generator', []),
    'Korda.Database': ('src/Data.NativeHost', ['Korda.Core', 'Korda.Data', 'Korda.Database.Transactions', 'Korda.Data.Streaming', 'Korda.Query.Binding']),
    'Korda.Database.Sqlite': ('src/Data.Sqlite', ['Korda.Core', 'Korda.Data', 'Korda.Database', 'Korda.Query.Generator']),
    'Korda.Database.PostgreSql': ('src/Data.Postgres', ['Korda.Core', 'Korda.Data', 'Korda.Database', 'Korda.Query.Generator']),
}
CAPABILITIES = ['db.metadata.v1', 'db.query.v1', 'db.transactions.v1', 'db.savepoints.v1', 'db.streaming.v1', 'db.migrations.v1', 'db.schema.read.v1', 'db.returning.v1', 'db.upsert.v1', 'db.cte.v1', 'db.window.v1', 'db.row-lock.v1', 'db.null-safe-equality.v1', 'db.json.v1', 'db.decimal.numeric.v1', 'db.decimal.text.v1', 'db.fixed-money.v1', 'db.relationships.explicit.v1', 'db.prepared-shape.v1', 'db.bulk-batches.v1', 'db.dynamic-fields.v1', 'db.recursive-cte.v1', 'db.set-operations.v1', 'db.scalar-codecs.v1', 'db.sql.expert.v1']

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def build_feed(root, feed, sqlite, postgres, run):
    """Libraries must already be built normally; the application still uses ordinary NuGet."""
    spec = importlib.util.spec_from_file_location('existing_a14_packages', root / 'src/Packaging.Linux/packages.py')
    packing = importlib.util.module_from_spec(spec); spec.loader.exec_module(packing)
    task = root / 'src/Packaging.Release/BuildTasks/Korda.Packaging.Tasks.csproj'
    common = ['-p:UseSharedCompilation=false', '-nr:false', '-m:1']
    run('closure-task-restore', ['dotnet', 'restore', task, '--locked-mode', *common])
    run('closure-task-build', ['dotnet', 'build', task, '-c', 'Release', '--no-restore', *common])
    version = '0.1.0-preview.1'
    commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip()
    providers = {
        'Korda.Database.Sqlite': ('database.sqlite', sqlite, 'libnw_database_sqlite.so'),
        'Korda.Database.PostgreSql': ('database.postgresql', postgres, 'libnw_database_postgresql.so'),
    }
    def record(identity):
        item = {'id': identity, 'version': version, 'dependencies': {name: version for name in REGISTRY[identity][1]}}
        if identity in providers:
            engine, native, basename = providers[identity]
            item['nativeAssets'] = [{'engineId': engine, 'facadePackageId': identity, 'facadePackageVersion': version,
                'rid': 'linux-x64', 'sourcePath': 'runtimes/linux-x64/native/' + basename, 'sha256': sha(native),
                'abiMajor': 1, 'abiMinor': 0, 'capabilities': {name: bit for bit, name in enumerate(CAPABILITIES)}, 'cpuBaseline': 'x86-64'}]
        return item
    def closure(identity):
        selected = set()
        def visit(name):
            if name in selected:
                return
            selected.add(name)
            for child in REGISTRY[name][1]:
                visit(child)
        visit(identity)
        return {'schemaVersion': 1, 'packages': [record(name) for name in sorted(selected)]}
    packages = {}
    feed.mkdir(parents=True, exist_ok=True)
    for identity, (folder, dependencies) in REGISTRY.items():
        entries = {'LICENSE': root / 'LICENSE'}
        directory = root / folder
        if identity == 'Korda.Query.Generator':
            entries['tools/roslyn/' + identity + '.dll'] = directory / 'bin/Release/netstandard2.0' / (identity + '.dll')
            entries['buildTransitive/' + identity + '.targets'] = directory / 'buildTransitive' / (identity + '.targets')
        else:
            for suffix in ['.dll', '.xml']:
                entries['lib/net11.0/' + identity + suffix] = directory / 'bin/Release/net11.0' / (identity + suffix)
        if identity in providers or identity == 'Korda.Database':
            entries['buildTransitive/' + identity + '.targets'] = root / 'src/Packaging.Release/buildTransitive/Korda.Packaging.targets'
            entries['korda/closure.json'] = json.dumps(closure(identity), sort_keys=True).encode()
            for suffix in ['.dll', '.xml']:
                entries['tools/net11.0/Korda.Packaging.Tasks' + suffix] = task.parent / 'bin/Release/net11.0' / ('Korda.Packaging.Tasks' + suffix)
        if identity in providers:
            _, native, basename = providers[identity]
            entries['runtimes/linux-x64/native/' + basename] = native
        artifact = packing.pack(feed, identity, version, entries, {name: version for name in dependencies}, ('https://github.com/IzuSharp/Korda', commit))
        packages[artifact.name] = sha(artifact)
    (feed / 'package-identities.json').write_text(json.dumps({'source_commit': commit, 'sha256': packages}, indent=2) + '\n')
    return packages

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--source-root', type=Path, default=ROOT)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--sqlite-module', type=Path, required=True)
    parser.add_argument('--postgres-module', type=Path, required=True)
    args = parser.parse_args()
    def run(name, command):
        print(name, flush=True)
        subprocess.run(list(map(str, command)), cwd=args.source_root, check=True, timeout=300)
    build_feed(args.source_root.resolve(), args.output.resolve(), args.sqlite_module.resolve(), args.postgres_module.resolve(), run)
