#!/usr/bin/env python3
"""Bind affected AOT gates to the exact candidate and reused dependencies."""
import hashlib
import json
import re
from pathlib import Path

BASE = Path(__file__).resolve().parent
ROOT = BASE.parents[1]
OLD = ROOT / 'measurements/native-batch-borrow-v1'
sha = lambda p: hashlib.sha256(Path(p).read_bytes()).hexdigest()
read = lambda p: json.loads(Path(p).read_text())
before = read(BASE / 'before-reuse.json')
for file, digest in before['preservedFiles'].items():
    assert sha(file) == digest, file
old_source = read(OLD / 'host-build-v2/source-inputs.json')
source = read(BASE / 'host-build-v1/source-inputs.json')
changes = {name: digest for name, digest in source.items() if old_source[name] != digest}
assert sorted(changes) == ['src/Data.Streaming/TypedStream.cs', 'tests/Data.Streaming/AllocationProbeRaces.cs']
index = {
    'status': 'passed',
    'scope': 'Iteration15 affected Native AOT correctness and raw short allocation gates. Source-project attribution only; no final PackageReference or formal performance acceptance.',
    'beforeReuseReceiptSha256': sha(BASE / 'before-reuse.json'),
    'productionChanges': {name: digest for name, digest in changes.items() if name.startswith('src/')},
    'testChanges': {name: digest for name, digest in changes.items() if name.startswith('tests/')},
    'frozenDataDllSha256': next(iter(before['dataDependencyAssembliesBefore'].values())),
    'dependencyPolicy': 'BuildProjectReferences=false; existing validated Data DLL bytes and original producer metadata preserved. Actual input/output reference DLL hashes are recorded in every build receipt.',
    'reusedDataCorrectnessIndex': str(OLD / 'validation-index.json'),
    'reusedDataCorrectnessIndexSha256': sha(OLD / 'validation-index.json'),
    'suites': {},
}
for kind in ('modules', 'streaming', 'host'):
    folder = BASE / (kind + '-build-v1')
    receipt = read(folder / 'build-receipt.json')
    assert receipt['exitCode'] == 0 and receipt['sourcesUnchanged'] and receipt['dataDependencyDllsUnchanged']
    assert all(command['exitCode'] == 0 for command in receipt['commands'])
    assert read(folder / 'source-inputs.json') == source
    assert sha(folder / 'source-inputs.json') == receipt['sourceManifestSha256']
    assert sha(folder / 'source-inputs.tar.gz') == receipt['sourceArchiveSha256']
    for phase in ('beforeReferenceDlls', 'afterReferenceDlls'):
        assert all(digest == index['frozenDataDllSha256'] for name, digest in receipt[phase].items()
                   if name.endswith('/Korda.Data.dll'))
    entry = {
        'buildReceipt': str(folder / 'build-receipt.json'),
        'buildReceiptSha256': sha(folder / 'build-receipt.json'),
        'sourceManifestSha256': receipt['sourceManifestSha256'],
        'sourceArchiveSha256': receipt['sourceArchiveSha256'],
        'actualBuildGitHead': receipt['gitHead'],
        'actualBuildSourceDirty': receipt['sourceDirty'],
        'buildCommandCount': len(receipt['commands']),
        'buildExitCode': receipt['exitCode'],
    }
    if kind != 'modules':
        assembly = 'Korda.Data.Streaming.Tests' if kind == 'streaming' else 'Korda.Data.NativeHost.Tests'
        assert sha(folder / 'aot' / assembly) == receipt['executableSha256']
        assert sha(folder / 'project.assets.json') == receipt['restoreGraphSha256']
        entry.update(executableSha256=receipt['executableSha256'], restoreGraphSha256=receipt['restoreGraphSha256'])
        command_path = BASE / (kind + '-correctness-v1') / 'two-provider-suite.command.json'
        command = read(command_path)
        assert command['exitCode'] == 0 and not command['timedOut'] and not command['observerErrors']
        entry.update(actualTwoProviderCommand=str(command_path), actualTwoProviderCommandSha256=sha(command_path))
    index['suites'][kind] = entry

gate_path = BASE / 'streaming-gates-v1/commands.json'
gates = read(gate_path)
assert len(gates['records']) == 4 and all(g['exitCode'] == 0 for g in gates['records'])
for gate in gates['records']:
    assert sha(BASE / 'streaming-gates-v1' / (gate['name'] + '.log')) == gate['logSha256']
zero = read(BASE / 'streaming-gates-v1/allocation-zero/allocation-probe-sync-inbatch.json')
assert not zero['dynamicCodeSupported'] and zero['managedAllocatedBytesRaw'] == 0
assert zero['completedRows'] == 65520 and len(zero['windows']) == 16
assert all(w['allocatedBytes'] == 0 and w['after']['totalAllocatedBytes'] == w['before']['totalAllocatedBytes'] for w in zero['windows'])
owned = read(BASE / 'streaming-gates-v1/owned-batch/owned-batch-probe.json')
assert owned['passed'] and owned['native_aot'] and owned['correctness_assertions'] == 405
owned_windows = [w for w in owned['windows'] if w['lane'] == 'exclusive-owned-handoff']
assert len(owned_windows) == 3 and all(w['allocated_bytes_total'] == 5120 and w['operations'] == 64 for w in owned_windows)
races = (BASE / 'streaming-gates-v1/races-and-native-borrow.log').read_text()
assert '118 assertions' in races and '19 assertions' in races
streaming = (BASE / 'streaming-correctness-v1/two-provider-suite.stdout.log').read_text()
assert 'assertions=615 cases=60 jit=False managed-live-bytes=0 native-wrappers=0' in streaming
host = (BASE / 'host-correctness-v1/two-provider-suite.stdout.log').read_text()
assert 'snapshot parser oracle inputs=5942' in host
assert host.count('BORROWED_HOST_CASE_PASS ') == 12
assert host.count('scenarios=6 assertions=107;') == 2
assert 'all source/session/operation/cursor/buffer/handle counters=0' in host
match = re.search(r'actual Linux two-provider assertions=(\d+); separately observed managed retirements=(\d+)', host)
assert match
index['streaming'] = {
    'freshGateCommandSha256': sha(gate_path), 'actualAssertions': 615, 'actualCases': 60,
    'raceAndConsumptionAssertions': 118, 'nativeBorrowAssertions': 19, 'ownedProbeAssertions': 405,
    'syncZeroWindows': 16, 'syncZeroRows': 65520, 'syncRawBytes': 0,
    'ownedLeaseBytesPerBatch': 80, 'ownedLeaseRawWindows': [w['allocated_bytes_total'] for w in owned_windows],
    'newConsumptionCoverage': 'Both managed-owned and native-borrowed: delayed GetResult, unconsumed completed ValueTask isolated from another pending move, and retained Task after one AsTask conversion remains repeatedly awaitable.',
}
index['host'] = {
    'actualAssertions': int(match.group(1)), 'actualManagedRetirements': int(match.group(2)),
    'parserOracleInputs': 5942, 'realMapperLifecycleScenarios': 12, 'newAssertionsPerProvider': 107,
    'correctnessDatabaseEncoding': 'UTF8, template0, C locale; SHOW verified. Allocation A/B retains original numeric fixture.',
}
index['limits'] = [
    'Warm FetchProbe does not measure the pool first-use or capacity-miss paths.',
    'Ordinary runtime and harness work remain in raw whole-process totals; no empty-control subtraction.',
    'No assertion that entire queries, refills, all worker concurrency, or the full framework have zero allocation.',
]
with (BASE / 'validation-index.json').open('x') as output:
    output.write(json.dumps(index, indent=2) + '\n')
print('validation-index-sha256', sha(BASE / 'validation-index.json'))
