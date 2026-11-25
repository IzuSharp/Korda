from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tarfile
import zipfile

ROOT = Path('/workspace/scratch/37c4f8cc3a91')
WORK = ROOT / 'recovery-20261125/public-list-current'
REPO = ROOT / 'worktrees/Korda-DB-Perf-01-linux'
REF = 'db0f674f3849fa86f69d86eea2c830dc1c84da83'
PRODUCER = '3df4f4f3ac455cb8bae8901c80a4fefb33021970'

def sha(path):
    with Path(path).open('rb') as f:
        return hashlib.file_digest(f, 'sha256').hexdigest()

def digest(data):
    return hashlib.sha256(data).hexdigest()

def write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open('xb') as f:
        f.write(data)

def write_json(path, data):
    write(path, (json.dumps(data, ensure_ascii=False, indent=2) + '\n').encode())

sources = {}
for name in ['global.json', 'benchmarks/db-perf/run.py',
             *['benchmarks/db-perf/business-results/' + n for n in
               ['Program.cs', 'BusinessResults.csproj', 'Directory.Build.props',
                'build.py', 'run.py', 'analyze.py', 'README.md']]]:
    original = subprocess.check_output(['git', 'show', REF + ':' + name], cwd=REPO)
    assert (REPO / name).read_bytes() == original, name
    write(WORK / 'frozen-source' / name, original)
    sources[name] = digest(original)

producer_path = ROOT / 'recovery-20261125/campaign-activation-review/producer-git.json'
producer = json.loads(producer_path.read_text())
tree = subprocess.check_output(['git', 'rev-parse', REF + '^{tree}'], cwd=REPO, text=True).strip()
assert producer['sha'] == PRODUCER and producer['tree']['sha'] == tree
write(WORK / 'inputs/producer-git.json', producer_path.read_bytes())

packages = []
for label, original_label in [('before', 'before'), ('after', 'after')]:
    for provider, identity in [('sqlite', 'Korda.Database.Sqlite'),
                               ('postgresql', 'Korda.Database.PostgreSql')]:
        origin = ROOT / 'recovery-20261125/campaign-activation-review/packages' / original_label / provider
        release_path = origin / 'release.json'
        release = json.loads(release_path.read_text())
        item = next(p for p in release['packages'] if p['id'] == identity)
        original = origin / 'feed' / (identity + '.' + item['version'] + '.nupkg')
        copied = WORK / 'inputs/packages' / label / provider / original.name
        expected_commit = '771bc77cbd68d3f932b99ed2497c0697bbba5f02' if label == 'before' else PRODUCER
        assert item['source_commit'] == expected_commit
        with zipfile.ZipFile(original) as z:
            for path, expected in item['unsigned_entries'].items():
                assert digest(z.read(path)) == expected, (original, path)
            binaries = {name: digest(z.read(name)) for name in z.namelist()
                        if name.endswith(('.dll', '.so'))}
        write(copied, original.read_bytes())
        write(copied.parent / 'release.json', release_path.read_bytes())
        packages.append({'label': label, 'provider': provider, 'id': identity,
                         'version': item['version'], 'path': str(copied),
                         'original_path': str(original), 'sha256': sha(copied),
                         'source_commit': item['source_commit'],
                         'input_fingerprint': release['inputs'][provider],
                         'release_json_sha256': sha(release_path),
                         'native': item['native'], 'dll_and_native_payloads': binaries})

runtime_path = ROOT / 'recovery-20261125/original-campaign/runtime.zip'
runtime_digest = sha(runtime_path)
assert runtime_digest == 'aecbd4e949e333399704ba141f7d52e9225323e658f6180262355831b5aec7e8'
selected = {'bundle/bundle.json', 'bundle/native/libsqlite3.so',
            'bundle/native/build-receipt.json', 'bundle/workload.json',
            'bundle/apps/sqlite/korda_before/Dataset.cs'}
members = {}
with zipfile.ZipFile(runtime_path) as z:
    preparation_bytes = z.read('preparation.json')
    preparation = json.loads(preparation_bytes)
    with z.open('runtime-bundle.tar.gz') as f:
        archive_digest = hashlib.file_digest(f, 'sha256').hexdigest()
    assert archive_digest == preparation['sha256']
    with z.open('runtime-bundle.tar.gz') as f:
        with tarfile.open(fileobj=f, mode='r|gz') as t:
            for m in t:
                if m.isfile() and m.name in selected:
                    members[m.name] = t.extractfile(m).read()
assert set(members) == selected
assert digest(members['bundle/bundle.json']) == preparation['bundleManifestSha256']
bundle = json.loads(members['bundle/bundle.json'])
for name, data in members.items():
    if name != 'bundle/bundle.json':
        expected = bundle['files'][name.removeprefix('bundle/')]
        assert len(data) == expected['bytes'] and digest(data) == expected['sha256'], name
native_build = json.loads(members['bundle/native/build-receipt.json'])
assert native_build['outputs']['libsqlite3.so'] == digest(members['bundle/native/libsqlite3.so'])
for name, data in members.items():
    write(WORK / 'inputs/runtime-reuse' / name.removeprefix('bundle/'), data)
write(WORK / 'inputs/runtime-reuse/preparation.json', preparation_bytes)

rows = [{'TenantId': 1, 'Id': i, 'Note': None if i % 5 == 0 else 'note-Ω-' + str(i % 16)}
        for i in range(1, 101)]
sql = 'CREATE TABLE business_result_orders ("TenantId" BIGINT NOT NULL, "Id" BIGINT NOT NULL, "Note" TEXT NULL, PRIMARY KEY("TenantId","Id"))'
fixture = {
    'schema': 'DB-Perf-01.public-list-fixture.v1',
    'source': {'path': 'benchmarks/db-perf/business-results/Program.cs',
               'sha256': sources['benchmarks/db-perf/business-results/Program.cs']},
    'initialization': 'The unchanged Native AOT consumer creates exactly one fresh table and inserts these 100 rows before warmup. Existing tables cause failure; no preexisting database is dropped.',
    'schema_sql': sql, 'table': 'business_result_orders', 'rows': rows,
    'result_type': 'List<BusinessRow>', 'dto_fields': ['Int64 Id', 'nullable String Note'],
    'operations': 'Dynamic Fluent generated projection ToListAsync or dynamic Dapper QueryAsync().AsList(), tenant=Int64(1), limit=Int32(100), ordered by Id; all 100 values consumed and checked.',
    'result_objects': {'dtos': 100, 'nonnull_independent_strings': 80, 'null_strings': 20},
    'graph_control': 'Same retained List capacity, one new DTO per row, one fresh nonempty string per nonnull row; runs after database disposal. This is not a trace of allocations inside the actual query.',
}
write_json(WORK / 'inputs/fixture.json', fixture)

cells = []
for provider in ['sqlite', 'postgresql']:
    for label, route in [('before', 'korda'), ('after', 'korda'), ('independent', 'dapper')]:
        package = next((p for p in packages if p['label'] == label and p['provider'] == provider), None)
        cells.append({'id': provider + '-' + label, 'label': label, 'route': route, 'provider': provider,
                      'app': str(WORK / 'apps' / (provider + '-' + label)),
                      'source_label': package['source_commit'] if package else 'Dapper/2.1.100;Dapper.AOT/1.1.0;' + ('Npgsql/11.0.0' if provider == 'postgresql' else 'Microsoft.Data.Sqlite.Core/11.0.0;SQLitePCLRaw.provider.sqlite3/3.0.5'),
                      'package': package})
order = [
    ['sqlite-before', 'sqlite-after', 'sqlite-independent', 'postgresql-before', 'postgresql-after', 'postgresql-independent'],
    ['postgresql-independent', 'postgresql-after', 'postgresql-before', 'sqlite-independent', 'sqlite-after', 'sqlite-before'],
    ['sqlite-after', 'sqlite-independent', 'sqlite-before', 'postgresql-after', 'postgresql-independent', 'postgresql-before'],
]
for directory in ['support-cache', 'tmp', 'dotnet-cli-home', 'nuget-http-cache', 'logs']:
    (WORK / directory).mkdir()
plan = {
    'schema': 'DB-Perf-01.public-list-current-plan.v1',
    'created_utc': datetime.now(timezone.utc).isoformat(),
    'scope': 'New Debian 13 Linux x64 allocation diagnostic. No formal throughput/latency acceptance or long-campaign seconds. Exact narrow BusinessRow DTO; not the full order DTO.',
    'reason_for_new_builds': 'The previous local standalone ELF files and original business-result process records were lost when the scratch environment reset.',
    'repository': str(REPO), 'observer_ref': REF, 'consumer_ref': REF,
    'frozen_production_tree': tree, 'package_producer_commit': PRODUCER,
    'package_producer_git_record_sha256': sha(producer_path), 'sources': sources,
    'toolchain_manifest': str(ROOT / 'tooling/receipts/toolchain-manifest.json'),
    'toolchain_manifest_sha256': sha(ROOT / 'tooling/receipts/toolchain-manifest.json'),
    'fixture_path': str(WORK / 'inputs/fixture.json'), 'fixture_sha256': sha(WORK / 'inputs/fixture.json'),
    'runtime_reuse': {'original_zip': str(runtime_path), 'zip_sha256': runtime_digest,
                     'inner_archive_sha256': archive_digest,
                     'bundle_manifest_sha256': preparation['bundleManifestSha256'],
                     'sqlite_image': str(WORK / 'inputs/runtime-reuse/native/libsqlite3.so'),
                     'sqlite_image_sha256': digest(members['bundle/native/libsqlite3.so']),
                     'dataset_initializer_reused': False,
                     'reason': 'The archived initializer is for the 35-case multi-table dataset; its table/shape/row-count contract does not match this dedicated 100-row business_result_orders fixture.',
                     'archived_workload_sha256': digest(members['bundle/workload.json']),
                     'archived_initializer_source_sha256': digest(members['bundle/apps/sqlite/korda_before/Dataset.cs'])},
    'build': {'serial': True, 'maximum_ilc_processes': 1, 'DOTNET_PROCESSOR_COUNT': '2',
              'all_six_builds_must_succeed_before_consumption': True},
    'execution': {'serial': True, 'DOTNET_PROCESSOR_COUNT': None, 'processes_per_cell': 3,
                  'warmup_queries_per_process': 64, 'windows_per_process': 3,
                  'queries_per_window': 128, 'graph_controls_per_window': 1024,
                  'measured_queries_total': 6912, 'measured_query_rows_total': 691200,
                  'round_order': order},
    'allocation_accounting': 'Precise cumulative entire-process managed allocation and Gen0/1/2 endpoints for each window. Business clone is isolated construction evidence; its subtraction leaves an unresolved framework/runtime/harness/model remainder, never exact framework bytes or framework 0 B. No GC event category ownership is invented.',
    'cells': cells,
}
write_json(WORK / 'plan.json', plan)
print(json.dumps({'plan': str(WORK / 'plan.json'), 'sha256': sha(WORK / 'plan.json'),
                  'fixture_sha256': plan['fixture_sha256'], 'sqlite_native_sha256': plan['runtime_reuse']['sqlite_image_sha256'],
                  'cells': len(cells), 'processes': 18}))
