import hashlib
import json
import pathlib
import tarfile
from collections import Counter

ROOT = pathlib.Path('/workspace/scratch/b94e2a0e0c3f')
BASE = ROOT / 'measurements/native-batch-borrow-v1'


def sha(path):
    return hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest()


def read(path):
    return json.loads(pathlib.Path(path).read_text())


def checked(path, expected):
    assert sha(path) == expected, str(path)
    return read(path)


def archive(directory):
    receipt = read(directory / 'build-receipt.json')
    assert receipt['exitCode'] == 0 and receipt['sourcesUnchanged']
    manifest = checked(directory / 'source-inputs.json', receipt['sourceManifestSha256'])
    assert sha(directory / 'source-inputs.tar.gz') == receipt['sourceArchiveSha256']
    with tarfile.open(directory / 'source-inputs.tar.gz') as tar:
        actual = {}
        for member in tar.getmembers():
            if member.isfile():
                assert member.name not in actual
                actual[member.name] = hashlib.sha256(tar.extractfile(member).read()).hexdigest()
        assert actual == manifest
    executables = [p for p in (directory / 'aot').iterdir() if p.is_file() and sha(p) == receipt['executableSha256']]
    assert len(executables) == 1
    for native in receipt.get('nativeImages', {}).values():
        assert sha(native['path']) == native['sha256']
    return manifest, receipt


def totals(samples):
    for sample in samples:
        assert sample['AllocatedAfter'] - sample['AllocatedBefore'] == sample['AllocatedBytes'] >= 0
        assert sample['RetirementsAfter'] - sample['RetirementsBefore'] == sample['Retirements'] >= 0
        assert sample['FinishedTicks'] >= sample['StartedTicks']
        assert all(sample[k] >= 0 for k in ('Gen0', 'Gen1', 'Gen2'))
    return {
        'windows': len(samples),
        'operations': sum(s['Operations'] for s in samples),
        'rows': sum(s['Rows'] for s in samples),
        'rawAllocatedBytes': sum(s['AllocatedBytes'] for s in samples),
        'gen0': sum(s['Gen0'] for s in samples),
        'gen1': sum(s['Gen1'] for s in samples),
        'gen2': sum(s['Gen2'] for s in samples),
        'retirements': sum(s['Retirements'] for s in samples),
        'incompleteMovesObserved': sum(s['IncompleteMoves'] for s in samples),
        'moveThreadChanges': sum(s['MoveThreadChanges'] for s in samples),
        'rawBytesPerWindowDistribution': {str(k): v for k, v in sorted(Counter(s['AllocatedBytes'] for s in samples).items())},
    }


comparison = read(BASE / 'comparison.json')
index = checked(BASE / 'validation-index.json', comparison['validationIndexSha256'])
before_manifest, before_build = archive(ROOT / 'measurements/transaction-default-v3/after-build')
after_manifest, after_build = archive(BASE / 'host-build-v2')
changed_production = {k: after_manifest[k] for k in before_manifest if k.startswith('src/') and before_manifest[k] != after_manifest.get(k)}
assert changed_production == index['productionChangedFromBefore']
assert not [k for k in after_manifest.keys() - before_manifest.keys() if k.startswith('src/')]
assert before_manifest['tests/Data.NativeHost/FetchProbe.cs'] == after_manifest['tests/Data.NativeHost/FetchProbe.cs'] == after_build['probeSha256']

result = {'scope': 'Independent read-only recomputation; no build or workload rerun; no final-package or comparative timing credit',
          'productionDiff': changed_production, 'beforeSourceCount': len(before_manifest), 'afterSourceCount': len(after_manifest),
          'beforeExecutable': before_build['executableSha256'], 'afterExecutable': after_build['executableSha256'],
          'sourceArchivesFullyVerified': True, 'providers': {}, 'rawProcesses': 0, 'fullQueryWindows': 0, 'sliceWindows': 0}
for phase in ('before', 'after'):
    summary = checked(BASE / (phase + '-summary.json'), comparison[phase + 'SummarySha256'])
    for provider, declared in summary['providers'].items():
        identity = read(BASE / phase / provider / 'identity.json')
        assert sha(identity['originalBuildReceiptPath']) == declared['originalBuildReceiptSha256']
        assert sha(identity['executablePath']) == declared['executableSha256']
        groups, slices, controls, sql = {}, {}, {}, None
        for process in declared['processes']:
            report = checked(process['file'], process['sha256'])
            path = pathlib.Path(process['file'])
            command = read(path.parent.parent / (path.parent.name + '.command.json'))
            assert command['exitCode'] == process['exitCode'] == 0 and command['success'] and not command['timedOut']
            for name, digest in command['files'].items():
                assert sha(path.parent.parent / name) == digest
            assert report['NativeAot'] and report['OwnersReturnedToZero'] and report['NativeDrained'][:6] == [0] * 6
            assert report['ExecutableSha256'] == declared['executableSha256']
            assert sha(report['LoadedNativePath']) == report['LoadedNativeSha256'] == declared['loadedNativeSha256']
            assert (report['Warmup'], report['IterationsPerWindow'], report['Windows'], report['DataRows'], report['BatchRows']) == (64, 128, 9, 256, 32)
            current_sql = [q['Sql'] for q in report['Queries']]
            assert sql is None or sql == current_sql
            sql = current_sql
            for query in report['Queries']:
                key = str(query['ResultRows'])
                groups.setdefault(key, []).extend(query['Measurements'])
                for sample in query['Measurements']:
                    assert sample['Rows'] == sample['Operations'] * int(key)
                    assert sample['Checksum'] == sample['Operations'] * int(key) * (int(key) + 1) // 2
                    assert sample['NativeBefore'][:6] == sample['NativeAfter'][:6]
                    assert sample['NativeAfter'][6] - sample['NativeBefore'][6] == sample['Operations']
                    assert sample['NativeAfter'][7] - sample['NativeBefore'][7] == sample['Operations'] * (5 if provider == 'postgresql' else 0)
                    assert sample['Retirements'] == sample['Operations'] * (query['ExpectedFetchesIncludingEos'] + 2)
            for sample in report['SupplementalAndWithinBatch']:
                slices.setdefault(sample['Name'], []).append(sample)
                if sample['Name'] == 'supplemental-fetch-and-first-row':
                    assert sample['Retirements'] == sample['Operations'] == sample['Rows'] == 1 and sample['Checksum'] == 33
                else:
                    assert sample['Rows'] == sample['Operations'] == 31 and sample['AllocatedBytes'] == sample['Retirements'] == 0
            for sample in report['Controls']:
                controls.setdefault(sample['Name'], []).append(sample)
            result['rawProcesses'] += 1
        for actual, expected in ((groups, declared['queriesByResultRows']), (slices, declared['slices']), (controls, declared['controls'])):
            assert actual.keys() == expected.keys()
            for name, samples in actual.items():
                for key, value in totals(samples).items():
                    assert expected[name][key] == value, (phase, provider, name, key)
        result['providers'].setdefault(provider, {})[phase] = {'sql': sql, 'fullQueries': {k: totals(v) for k, v in groups.items()}, 'slices': {k: totals(v) for k, v in slices.items()}}
        result['fullQueryWindows'] += sum(map(len, groups.values()))
        result['sliceWindows'] += sum(map(len, slices.values()))
for provider, phases in result['providers'].items():
    assert phases['before']['sql'] == phases['after']['sql']
    for rows in ('0', '1', '256'):
        left, right = phases['before']['fullQueries'][rows], phases['after']['fullQueries'][rows]
        expected = comparison['providers'][provider]['fullQueriesByResultRows'][rows]
        assert right['rawAllocatedBytes'] - left['rawAllocatedBytes'] == expected['deltaRawBytes']

for suite in index['suites'].values():
    receipt = checked(ROOT / suite['buildReceipt'], suite['buildReceiptSha256'])
    assert receipt['exitCode'] == 0 and receipt['sourcesUnchanged']
    manifest = checked((ROOT / suite['buildReceipt']).parent / 'source-inputs.json', suite['sourceManifestSha256'])
    assert all(manifest[k] == v for k, v in changed_production.items())
    if 'actualTwoProviderCommand' in suite:
        command_path = ROOT / suite['actualTwoProviderCommand']
        command = checked(command_path, suite['actualTwoProviderCommandSha256'])
        assert command['exitCode'] == 0 and command['success'] and not command['timedOut']
        for name, digest in command['files'].items():
            assert sha(command_path.parent / name) == digest

owned = read(BASE / 'streaming-gates-v1/owned-batch/owned-batch-probe.json')
owned_windows = [w for w in owned['windows'] if w['lane'] == 'exclusive-owned-handoff']
assert len(owned_windows) == 3 and all(w['allocated_bytes_total'] == 80 * w['operations'] for w in owned_windows)
previous_owned = read(ROOT / 'measurements/merged-focused-v1/streaming/owned-batch/owned-batch-probe.json')
assert all(w['allocated_bytes_total'] == 72 * w['operations'] for w in previous_owned['windows'] if w['lane'] == 'exclusive-owned-handoff')
failure = checked(BASE / index['preservedFailure']['report'], index['preservedFailure']['sha256'])
failed_command = read(BASE / 'host-correctness-v1/two-provider-suite.command.json')
assert failed_command['exitCode'] != 0 and not failed_command['success']
pg_log = (BASE / 'host-correctness-v1/postgres.log').read_text()
assert "server's encoding SQL_ASCII" in pg_log and 'SELECT CAST($1 AS TEXT)' in pg_log
log = (BASE / 'host-correctness-v2/two-provider-suite.stdout.log').read_text()
assert log.count('BORROWED_HOST_CASE_PASS ') == 12
assert 'scenarios=6 assertions=107' in log and '5942' in log
result.update(passed=True, comparisonSha256=sha(BASE / 'comparison.json'), validationIndexSha256=sha(BASE / 'validation-index.json'),
              preservedFixtureFailure=True, sourceProjectOnly=True, ownedLeaseBytesBefore=72, ownedLeaseBytesAfter=80)
output = pathlib.Path(__file__).with_name('receipt.json')
output.write_text(json.dumps(result, indent=2) + '\n')
print(json.dumps({'passed': True, 'receipt': str(output), 'sha256': sha(output), 'processes': result['rawProcesses'],
                  'fullQueryWindows': result['fullQueryWindows'], 'sliceWindows': result['sliceWindows'], 'productionChanges': len(changed_production)}))
