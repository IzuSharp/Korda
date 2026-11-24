#!/usr/bin/env python3
"""Validate immutable raw allocation receipts, sum counters before division."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path

BASE = Path(__file__).resolve().parent
parser = argparse.ArgumentParser()
parser.add_argument('--phase', required=True)
args = parser.parse_args()


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def total(samples):
    result = {
        'windows': len(samples),
        'operations': sum(s['Operations'] for s in samples),
        'rows': sum(s['Rows'] for s in samples),
        'rawAllocatedBytes': sum(s['AllocatedBytes'] for s in samples),
        'gen0': sum(s['Gen0'] for s in samples),
        'gen1': sum(s['Gen1'] for s in samples),
        'gen2': sum(s['Gen2'] for s in samples),
        'retirements': sum(s['Retirements'] for s in samples),
        'incompleteMovesObserved': sum(s['IncompleteMoves'] for s in samples),
        'incompleteDisposalsObserved': sum(s['IncompleteDisposals'] for s in samples),
        'moveThreadChanges': sum(s['MoveThreadChanges'] for s in samples),
        'rawBytesPerWindowDistribution': dict(sorted(Counter(s['AllocatedBytes'] for s in samples).items())),
    }
    result['bytesPerOperation'] = result['rawAllocatedBytes'] / result['operations']
    result['bytesPerRow'] = result['rawAllocatedBytes'] / result['rows'] if result['rows'] else None
    if all(s['NativeBefore'] is not None for s in samples):
        result['nativeSqlExecutions'] = sum(s['NativeAfter'][6] - s['NativeBefore'][6] for s in samples)
        result['nativeProtocolSubmissions'] = sum(s['NativeAfter'][7] - s['NativeBefore'][7] for s in samples)
    return result


summary = {'phase': args.phase,
           'scope': 'All-thread local Native AOT source-project allocation attribution; no control subtraction; not formal throughput/latency or final PackageReference acceptance.',
           'asyncObservationLimit': 'IncompleteMoves observes non-completion at one check; it is not an exact suspension count. MoveThreadChanges separately proves continuation on a different thread.',
           'providers': {}}
for provider in ('sqlite', 'postgresql'):
    folder = BASE / args.phase / provider
    completion = folder / 'completion.json'
    identity = json.loads(completion.read_text())
    assert identity['status'] == 'passed'
    assert len(identity['records']) == 3
    receipts = []
    for record in identity['records']:
        path = Path(record['file'])
        assert sha(path) == record['sha256']
        raw = json.loads(path.read_text())
        assert raw['ExecutableSha256'] == identity['executableSha256']
        assert raw['NativeAot'] and raw['OwnersReturnedToZero']
        assert raw['NativeDrained'][:6] == [0] * 6
        assert (raw['Warmup'], raw['Windows'], raw['IterationsPerWindow'], raw['BatchRows'], raw['DataRows']) == (64, 9, 128, 32, 256)
        assert [q['ResultRows'] for q in raw['Queries']] == [0, 1, 256]
        for q in raw['Queries']:
            assert len(q['Measurements']) == 9
            for s in q['Measurements']:
                assert s['Operations'] == 128
                assert s['Rows'] == 128 * q['ResultRows']
                assert s['Checksum'] == 128 * q['ResultRows'] * (q['ResultRows'] + 1) // 2
                assert s['NativeBefore'][:6] == s['NativeAfter'][:6]
                assert s['AllocatedAfter'] - s['AllocatedBefore'] == s['AllocatedBytes']
                assert s['RetirementsAfter'] - s['RetirementsBefore'] == s['Retirements']
                assert s['Retirements'] == 128 * (q['ExpectedFetchesIncludingEos'] + 2)
        assert len(raw['SupplementalAndWithinBatch']) == 256
        for s in raw['SupplementalAndWithinBatch'] + raw['Controls']:
            assert s['AllocatedAfter'] - s['AllocatedBefore'] == s['AllocatedBytes']
            assert s['RetirementsAfter'] - s['RetirementsBefore'] == s['Retirements']
            if s['Name'] == 'supplemental-fetch-and-first-row':
                assert (s['Operations'], s['Rows'], s['Checksum'], s['Retirements']) == (1, 1, 33, 1)
            elif s['Name'] == 'within-first-batch':
                assert (s['Operations'], s['Rows'], s['Checksum'], s['Retirements'], s['IncompleteMoves']) == (31, 31, 527, 0, 0)
        receipts.append(raw)
    rows = {}
    for index, count in enumerate((0, 1, 256)):
        samples = [s for raw in receipts for s in raw['Queries'][index]['Measurements']]
        rows[str(count)] = total(samples)
        rows[str(count)]['expectedFetchesPerQueryIncludingEos'] = receipts[0]['Queries'][index]['ExpectedFetchesIncludingEos']
        rows[str(count)]['fetchCountScope'] = 'Inferred from batchRows and the EOS contract; total operation retirements and SQL/protocol counters are separately observed.'
    slices = {name: total([s for raw in receipts for s in raw['SupplementalAndWithinBatch'] if s['Name'] == name])
              for name in ('supplemental-fetch-and-first-row', 'within-first-batch')}
    controls = {name: total([s for raw in receipts for s in raw['Controls'] if s['Name'] == name])
                for name in ('empty-sync', 'task-yield-control-not-native-equivalent')}
    summary['providers'][provider] = {'completionSha256': sha(completion),
        'originalBuildReceiptSha256': identity['originalBuildReceiptSha256'],
        'executableSha256': identity['executableSha256'],
        'loadedNativeSha256': receipts[0]['LoadedNativeSha256'],
        'processes': identity['records'], 'queriesByResultRows': rows,
        'slices': slices, 'controls': controls, 'allImmediateWarmOwnersMatched': True,
        'allFinalOwnersZero': True}
output = BASE / (args.phase + '-summary.json')
with output.open('x') as file:
    file.write(json.dumps(summary, indent=2) + '\n')
print(json.dumps(summary, indent=2))
