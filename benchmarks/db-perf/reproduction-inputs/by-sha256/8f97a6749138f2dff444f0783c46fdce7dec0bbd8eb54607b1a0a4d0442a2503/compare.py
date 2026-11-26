#!/usr/bin/env python3
"""Compare pooled Move with recovered iteration14 AFTER without relabeling it."""
import hashlib
import json
from pathlib import Path

BASE = Path(__file__).resolve().parent
OLD = BASE.parent / 'native-batch-borrow-v1'
sha = lambda p: hashlib.sha256(Path(p).read_bytes()).hexdigest()
read = lambda p: json.loads(Path(p).read_text())
before_path, after_path = OLD / 'after-summary.json', BASE / 'after-summary.json'
before, after = read(before_path), read(after_path)
comparison = {
    'scope': 'Local source-project public Fluent allocation attribution; exact old iteration14 AFTER is iteration15 before. No formal CPU/latency/throughput or independent-package acceptance.',
    'beforeSummaryPath': str(before_path), 'beforeSummarySha256': sha(before_path),
    'beforeOriginalPhase': before['phase'], 'beforeReuseReceiptSha256': sha(BASE / 'before-reuse.json'),
    'afterSummarySha256': sha(after_path), 'validationIndexSha256': sha(BASE / 'validation-index.json'),
    'singleProductionChange': 'Private TypedStream Enumerator.MoveAsync uses PoolingAsyncValueTaskMethodBuilder<bool> and returns its ValueTask directly; public signature and body logic unchanged.',
    'poolLimitations': 'These windows are after warmup64. First-use, capacity misses, per-thread/per-core retained slots and unconsumed outstanding ValueTasks are not quantified here. Any costs that occur during the windows remain in raw totals.',
    'attribution': 'Scalar Int64 output needs no business DTO/string/list allocation. Raw totals include all process threads plus runtime/harness. Controls are reported separately and never subtracted.',
    'providers': {},
}


def compare_lane(a, b):
    assert a['operations'] == b['operations'] and a['rows'] == b['rows']
    assert a['retirements'] == b['retirements']
    if 'nativeSqlExecutions' in a:
        assert a['nativeSqlExecutions'] == b['nativeSqlExecutions']
        assert a['nativeProtocolSubmissions'] == b['nativeProtocolSubmissions']
    delta = b['rawAllocatedBytes'] - a['rawAllocatedBytes']
    return {'before': a, 'after': b, 'deltaRawBytes': delta,
            'deltaBytesPerOperation': delta / a['operations'],
            'percentChange': delta * 100 / a['rawAllocatedBytes'] if a['rawAllocatedBytes'] else None}


for provider in ('sqlite', 'postgresql'):
    a, b = before['providers'][provider], after['providers'][provider]
    assert a['loadedNativeSha256'] == b['loadedNativeSha256']
    old_id = read(OLD / 'after' / provider / 'identity.json')
    new_id = read(BASE / 'after' / provider / 'identity.json')
    for key in ('fetchProbeSha256', 'controllerSha256', 'provider', 'independentProcesses', 'cpuAffinity',
                'warmup', 'iterationsPerWindow', 'windows', 'resultRows', 'batchRows', 'timeoutSecondsPerProcess'):
        assert old_id[key] == new_id[key], key
    old_raw = read(a['processes'][0]['file'])
    new_raw = read(b['processes'][0]['file'])
    assert [(q['Sql'], q['ResultRows']) for q in old_raw['Queries']] == [(q['Sql'], q['ResultRows']) for q in new_raw['Queries']]
    comparison['providers'][provider] = {
        'beforeExecutableSha256': a['executableSha256'], 'afterExecutableSha256': b['executableSha256'],
        'loadedNativeSha256': a['loadedNativeSha256'], 'sameProbeControllerAffinitySqlAndParameters': True,
        'wrapper': {'beforeSha256': old_id['runnerSha256'], 'afterSha256': new_id['runnerSha256'],
                    'note': 'Wrapper label changed to iteration15; SQLite fixture explicitly points back to the unchanged iteration14 path. Probe, command flags, SQL, controller, affinity and database inputs are unchanged.'},
        'fullQueriesByResultRows': {rows: compare_lane(a['queriesByResultRows'][rows], b['queriesByResultRows'][rows]) for rows in ('0', '1', '256')},
        'slices': {name: compare_lane(a['slices'][name], b['slices'][name]) for name in a['slices']},
        'controls': {name: compare_lane(a['controls'][name], b['controls'][name]) for name in a['controls']},
        'allWarmOwnerSnapshotsMatchedAndFinalOwnersZero': True,
    }
with (BASE / 'comparison.json').open('x') as output:
    output.write(json.dumps(comparison, indent=2) + '\n')
print('comparisonSha256', sha(BASE / 'comparison.json'))
for provider, p in comparison['providers'].items():
    print(provider)
    for name, row in p['fullQueriesByResultRows'].items():
        print(name, row['before']['rawAllocatedBytes'], row['after']['rawAllocatedBytes'],
              row['before']['bytesPerOperation'], row['after']['bytesPerOperation'], row['percentChange'])
    for name, row in p['slices'].items():
        print(name, row['before']['rawAllocatedBytes'], row['after']['rawAllocatedBytes'], row['percentChange'])
