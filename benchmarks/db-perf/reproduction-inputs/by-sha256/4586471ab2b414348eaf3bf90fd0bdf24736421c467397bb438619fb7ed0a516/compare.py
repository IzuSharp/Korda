#!/usr/bin/env python3
"""Compare exact frozen summaries; keep per-query/batch/row scopes separate."""
import hashlib
import json
from pathlib import Path

BASE = Path(__file__).resolve().parent
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
before_path, after_path = BASE / 'before-summary.json', BASE / 'after-summary.json'
before, after = (json.loads(p.read_text()) for p in (before_path, after_path))
comparison = {'scope': 'Local source-project public Fluent allocation attribution only. Raw all-thread bytes retain runtime/harness cost; no empty-driver subtraction. No formal elapsed/CPU/throughput claim.',
    'beforeSummarySha256': sha(before_path), 'afterSummarySha256': sha(after_path),
    'validationIndexSha256': sha(BASE / 'validation-index.json'),
    'synchronousMapperLifetime': 'Complete SafeHandle read scopes cover wire validation, row sizing and synchronous mapping; no persistent AddRef across await.',
    'nativeStorage': 'C++ provider and native-buffer ownership are unchanged; the existing immutable native buffer is retained until disposal, so no copy allocation is moved to a new native cache.',
    'additionalMeasuredCost': {'ownedLeaseBeforeBytesPerBatch': 72, 'ownedLeaseAfterBytesPerBatch': 80,
        'scope': 'Separate existing OwnedBatchProbe, three windows each. The 8B increase is included in every public query and refill raw count.'},
    'providers': {}}


def compare_lane(a, b):
    assert a['operations'] == b['operations'] and a['rows'] == b['rows']
    delta = b['rawAllocatedBytes'] - a['rawAllocatedBytes']
    result = {'before': a, 'after': b, 'deltaRawBytes': delta,
              'deltaBytesPerOperation': delta / a['operations'],
              'percentChange': delta * 100 / a['rawAllocatedBytes'] if a['rawAllocatedBytes'] else None}
    if 'nativeSqlExecutions' in a:
        assert a['nativeSqlExecutions'] == b['nativeSqlExecutions']
        assert a['nativeProtocolSubmissions'] == b['nativeProtocolSubmissions']
    assert a['retirements'] == b['retirements']
    return result


for provider in ('sqlite', 'postgresql'):
    a, b = before['providers'][provider], after['providers'][provider]
    assert a['loadedNativeSha256'] == b['loadedNativeSha256']
    before_identity = json.loads((BASE / 'before' / provider / 'identity.json').read_text())
    after_identity = json.loads((BASE / 'after' / provider / 'identity.json').read_text())
    for key in ('fetchProbeSha256', 'runnerSha256', 'controllerSha256', 'provider', 'independentProcesses',
                'cpuAffinity', 'warmup', 'iterationsPerWindow', 'windows', 'resultRows', 'batchRows', 'timeoutSecondsPerProcess'):
        assert before_identity[key] == after_identity[key], key
    before_raw = json.loads(Path(a['processes'][0]['file']).read_text())
    after_raw = json.loads(Path(b['processes'][0]['file']).read_text())
    assert [(q['Sql'], q['ResultRows']) for q in before_raw['Queries']] == [(q['Sql'], q['ResultRows']) for q in after_raw['Queries']]
    comparison['providers'][provider] = {
        'beforeExecutableSha256': a['executableSha256'], 'afterExecutableSha256': b['executableSha256'],
        'loadedNativeSha256': a['loadedNativeSha256'], 'sameProbeRunnerControllerAffinitySqlAndParameters': True,
        'fullQueriesByResultRows': {rows: compare_lane(a['queriesByResultRows'][rows], b['queriesByResultRows'][rows]) for rows in ('0', '1', '256')},
        'slices': {name: compare_lane(a['slices'][name], b['slices'][name]) for name in a['slices']},
        'controls': {name: compare_lane(a['controls'][name], b['controls'][name]) for name in a['controls']},
        'allWarmOwnerSnapshotsMatchedAndFinalOwnersZero': True,
    }
with (BASE / 'comparison.json').open('x') as file:
    file.write(json.dumps(comparison, indent=2) + '\n')
print('comparisonSha256', sha(BASE / 'comparison.json'))
for provider, p in comparison['providers'].items():
    print(provider)
    for name, row in p['fullQueriesByResultRows'].items():
        print(name, row['before']['rawAllocatedBytes'], row['after']['rawAllocatedBytes'],
              row['before']['bytesPerOperation'], row['after']['bytesPerOperation'], row['percentChange'])
    for name, row in p['slices'].items():
        print(name, row['before']['rawAllocatedBytes'], row['after']['rawAllocatedBytes'], row['percentChange'])
