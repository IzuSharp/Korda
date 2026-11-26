"""Read-only verification of existing iteration13 evidence. Runs no workload."""
import hashlib
import json
from pathlib import Path
import tarfile

ROOT = Path('/workspace/scratch/b94e2a0e0c3f')
BASE = ROOT / 'measurements/transaction-default-v3'
OUT = ROOT / 'measurements/transaction-default-independent-v3'


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def read(path):
    return json.loads(Path(path).read_text())


comparison = read(BASE / 'comparison.json')
assert sha(BASE / 'comparison.json') == '014d46f7d75385dfe2e56d2d23af2e0ae7fb36b4002643e36f44292659f12133'
source_manifests = {}
receipts = []
totals = {}
steady_windows = cold_windows = controls = 0
for version, summary in comparison['versions'].items():
    build = BASE / (version + '-build')
    receipt = read(build / 'build-receipt.json')
    assert sha(build / 'build-receipt.json') == summary['buildReceiptSha256']
    assert receipt['exitCode'] == 0 and receipt['sourcesUnchanged']
    assert sha(build / 'aot/Korda.Data.NativeHost.Tests') == summary['executableSha256'] == receipt['executableSha256']
    for filename, field in [('source-inputs.json', 'sourceManifestSha256'), ('source-inputs.tar.gz', 'sourceArchiveSha256'),
                            ('publish.log', 'logSha256'), ('project.assets.json', 'restoreGraphSha256')]:
        assert sha(build / filename) == receipt[field]
    manifest = read(build / 'source-inputs.json')
    source_manifests[version] = manifest
    assert len(manifest) == 311
    with tarfile.open(build / 'source-inputs.tar.gz', 'r:gz') as archive:
        names = [member.name for member in archive if member.isfile()]
        assert len(names) == len(set(names)) and set(names) == set(manifest)
        for name in names:
            assert hashlib.sha256(archive.extractfile(name).read()).hexdigest() == manifest[name], name
    assert manifest['tests/Data.NativeHost/TransactionDefaultProbe.cs'] == summary['probeSha256'] == '024378809c2298ec4dae86ca2895d592b21a4b57e49274e6a4e4f9d4742b12f7'
    completion = read(BASE / version / 'completion.json')
    assert completion['status'] == 'passed' and completion['sourcesUnchangedThroughSampling']
    assert completion['buildReceiptSha256'] == summary['buildReceiptSha256']
    assert completion['executableSha256'] == summary['executableSha256']
    assert len(completion['records']) == 6
    for provider, lanes in summary['providers'].items():
        accumulated = {}
        for raw in lanes['rawRecords']:
            path = Path(raw['path'])
            assert sha(path) == raw['sha256']
            data = read(path)
            assert data['NativeAot'] and data['ExecutableSha256'] == summary['executableSha256']
            assert data['Provider'].lower() == provider
            assert sha(data['LoadedNativePath']) == data['LoadedNativeSha256'] == raw['loadedNativeSha256'] == receipt['nativeImages'][provider]['sha256']
            assert data['DefaultFrameDataBytes'] == 104
            assert data['DefaultFrameSha256'] == raw['defaultFrameSha256'] == sha(path.parent / 'explicit-default-control.bin')
            assert data['OperationsPerWorker'] == 16 and data['Windows'] == 3 and data['WarmupOperationsPerWorker'] == 16
            command_path = path.parent.parent / (path.parent.name + '.command.json')
            command = read(command_path)
            assert command['exitCode'] == 0 and command['success'] and not command['timedOut'] and not command['observerErrors']
            assert not command['longRunAcceptance']
            for name, digest in command['files'].items():
                assert sha(command_path.parent / name) == digest
            assert command['argv'][0] == str(build / 'aot/Korda.Data.NativeHost.Tests')
            assert command['argv'][1] == '--transaction-default-probe'
            seen = set()
            for sample in data['Cold'] + data['Measurements']:
                before, api, settled, ingress = [sample[key] for key in ('Before', 'AfterApi', 'AfterSettle', 'BaselineStart')]
                assert ingress['Ticks'] <= before['Ticks'] <= api['Ticks'] <= settled['Ticks']
                assert ingress['AllocatedBytes'] <= before['AllocatedBytes'] <= api['AllocatedBytes'] <= settled['AllocatedBytes']
                assert sample['AllocatedBefore'] == before['AllocatedBytes']
                assert sample['AllocatedAfter'] == api['AllocatedBytes']
                assert sample['AllocatedAfterSettle'] == settled['AllocatedBytes']
                assert sample['AllocatedBytes'] == api['AllocatedBytes'] - before['AllocatedBytes']
                assert sample['PublicAndSettleRawBytes'] == settled['AllocatedBytes'] - before['AllocatedBytes']
                assert sample['PhysicalSettleAdditionalBytes'] == settled['AllocatedBytes'] - api['AllocatedBytes'] == 0
                assert sample['BaselinePreparationRawBytes'] == before['AllocatedBytes'] - ingress['AllocatedBytes']
                assert sample['Retirements'] == api['Retirements'] - before['Retirements'] >= 2 * sample['Operations']
                for generation in range(3):
                    key = 'Gen' + str(generation)
                    assert sample[key] == api[key] - before[key] >= 0
                    assert sample['Settle' + key] == settled[key] - api[key] == 0
                for key in ('NativeBeforeImmediate', 'NativeBefore', 'NativeAfter', 'NativeSettledAfter'):
                    assert sample[key][:6] == [1, 4, 0, 0, 0, 5]
                assert sample['BaselinePolls'] == sample['SettlePolls'] == 2
                assert sample['NativeAfter'][6:] == sample['NativeSettledAfter'][6:]
                assert sample['Operations'] == sample['Concurrency'] * sample['OperationsPerWorker']
                if sample['Window'] < 0:
                    cold_windows += 1
                    continue
                steady_windows += 1
                lane = sample['Name'] + '/c' + str(sample['Concurrency'])
                assert (lane, sample['Window']) not in seen
                seen.add((lane, sample['Window']))
                total = accumulated.setdefault(lane, {'rawBytes': 0, 'combinedBytes': 0, 'operations': 0, 'retirements': 0, 'threadChanges': 0, 'sqlDispatches': 0, 'protocolSubmissions': 0, 'gc': [0, 0, 0]})
                for field, key in [('rawBytes','AllocatedBytes'), ('combinedBytes','PublicAndSettleRawBytes'), ('operations','Operations'), ('retirements','Retirements'), ('threadChanges','ThreadChanges')]:
                    total[field] += sample[key]
                total['sqlDispatches'] += sample['NativeAfter'][6] - sample['NativeBefore'][6]
                total['protocolSubmissions'] += sample['NativeAfter'][7] - sample['NativeBefore'][7]
                for generation in range(3):
                    total['gc'][generation] += sample['Gen' + str(generation)]
            assert len(seen) == 12 and len(data['Cold']) == 2
            for control in data['ArraySizeControls']:
                controls += 1
                assert control['Operations'] == 10000
                assert control['AllocatedAfter'] - control['AllocatedBefore'] == control['AllocatedBytes']
                assert control['AllocatedBytes'] == (0 if control['Name'] == 'empty-reference-control' else 1280000)
            final = data['FinalClose']
            assert final['NativeImmediate'][:6] == final['NativeSettled'][:6] == data['NativeDrained'][:6] == [0] * 6
            assert data['OwnersReturnedToZero']
            assert final['Before']['Ticks'] <= final['AfterApi']['Ticks'] <= final['AfterSettle']['Ticks']
            assert final['PublicRawBytes'] == final['AfterApi']['AllocatedBytes'] - final['Before']['AllocatedBytes']
            assert final['PublicAndSettleRawBytes'] == final['AfterSettle']['AllocatedBytes'] - final['Before']['AllocatedBytes']
            assert final['PhysicalSettleAdditionalBytes'] == final['AfterSettle']['AllocatedBytes'] - final['AfterApi']['AllocatedBytes'] == 0
            receipts.append({'path': str(path), 'sha256': raw['sha256'], 'commandSha256': sha(command_path)})
        for lane, total in accumulated.items():
            target = lanes['totals'][lane]
            for field, value in total.items():
                assert target[field] == value, (version, provider, lane, field)
            assert target['bytesPerOperation'] == total['rawBytes'] / total['operations']
        totals[version + '/' + provider] = accumulated

assert set(source_manifests['before']) == set(source_manifests['after'])
changed = [name for name in source_manifests['before'] if source_manifests['before'][name] != source_manifests['after'][name]]
assert changed == ['src/Data.NativeHost/DataSession.cs']
assert source_manifests['after'][changed[0]] == sha(BASE / 'candidate-source.cs') == '32bd0d87610533c8f1df8bffc5add5b9527497671eb5397c8497dcd68f7c9a5c'
assert source_manifests['before'][changed[0]] == '24bdb76479cca7dc3f8a6e55cd027946fe1bbeec9328d76523bd10bf214d0e74'
for provider, lanes in comparison['changes'].items():
    for lane, change in lanes.items():
        before, after = [totals[v + '/' + provider][lane] for v in ('before', 'after')]
        assert before['operations'] == after['operations']
        delta = after['rawBytes'] - before['rawBytes']
        assert change['rawDeltaBytes'] == delta
        assert change['deltaBytesPerOperation'] == delta / before['operations']
        assert abs(change['percentChange'] - 100 * delta / before['rawBytes']) < 1e-10

correctness_path = BASE / 'correctness/two-provider-suite.command.json'
correctness = read(correctness_path)
assert sha(correctness_path) == comparison['correctness']['commandReceiptSha256']
assert correctness['success'] and correctness['exitCode'] == 0 and not correctness['observerErrors'] and not correctness['timedOut']
for name, digest in correctness['files'].items():
    assert sha(correctness_path.parent / name) == digest
log = (correctness_path.parent / 'two-provider-suite.stdout.log').read_text()
assert '492' in log and '174' in log and '5942' in log
assert steady_windows == 144 and cold_windows == 24 and controls == 72 and len(receipts) == 12
result = {'scope': 'Independent read-only verification of existing files; no new workload, build, CPU/RSS comparison or long acceptance',
          'passed': True, 'comparisonSha256': sha(BASE / 'comparison.json'), 'verifierSha256': sha(__file__),
          'sourceFilesPerBuild': 311, 'changedSources': changed, 'steadyWindows': steady_windows, 'coldWindows': cold_windows,
          'arrayControls': controls, 'rawProcesses': receipts, 'recomputedTotals': totals, 'retainedV1Failure': 'unclassified'}
(OUT / 'receipt.json').write_text(json.dumps(result, indent=2) + '\n')
print(json.dumps({k:result[k] for k in ('passed','sourceFilesPerBuild','changedSources','steadyWindows','coldWindows','arrayControls')}))
