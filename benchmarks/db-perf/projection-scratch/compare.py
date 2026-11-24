#!/usr/bin/env python3
"""Compare original whole-process encoder observations without estimating framework totals."""
import argparse
import hashlib
import json
from pathlib import Path


def read(path):
    raw = path.read_bytes()
    def pairs(values):
        result = {}
        for key, value in values:
            if key in result:
                raise ValueError('Duplicate JSON key: ' + key)
            result[key] = value
        return result
    def invalid(value):
        raise ValueError('Non-finite JSON value: ' + value)
    return json.loads(raw, object_pairs_hook=pairs, parse_constant=invalid), hashlib.sha256(raw).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--before', nargs=3, type=Path, required=True)
    parser.add_argument('--after', nargs=3, type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    lanes = {}
    identities = {}
    for lane in ('before', 'after'):
        lanes[lane] = []
        identities[lane] = []
        for path in getattr(args, lane):
            data, digest = read(path)
            if not data['nativeAot'] or data['operationsPerCase'] != 4096 or data['warmupPerCase'] != 1024:
                raise ValueError('Different measurement contract')
            expected = 'owned-array-baseline' if lane == 'before' else 'bounded-stack-scratch-4096'
            if data['callerEncodingMode'] != expected or data['businessResultAllocatedBytes'] != 0:
                raise ValueError('Different fixture/encoding mode')
            lanes[lane].append(data)
            identities[lane].append({'path': str(path), 'sha256': digest,
                'sourceSha256': data['sourceSha256'], 'executableSha256': data['executableSha256'],
                'processId': data['processId'], 'processStartedUtc': data['processStartedUtc']})
        if len({(d['sourceSha256'], d['executableSha256']) for d in lanes[lane]}) != 1:
            raise ValueError('Lane binaries or product sources differ')
    expected_cases = lanes['before'][0]['cases']
    results = []
    keys = ('name', 'encodedBytes', 'encodedHex', 'canonicalMetadataHex', 'shapeHash',
            'bindingOrder', 'fixtureSha256Before', 'fixtureSha256After', 'checksum')
    for index, expected in enumerate(expected_cases):
        samples = {lane: [] for lane in lanes}
        for lane, documents in lanes.items():
            for data in documents:
                if len(data['cases']) != len(expected_cases) or data['exceptions'] != lanes['before'][0]['exceptions']:
                    raise ValueError('Case set or owned-entry-point error observations changed')
                case = data['cases'][index]
                if any(case[key] != expected[key] for key in keys) or not case['inputUnchanged']:
                    raise ValueError('Full wire, binding order or input fingerprint differs')
                delta = case['allocatedAfter'] - case['allocatedBefore']
                if delta < 0 or delta != case['allocatedBytes'] or delta / 4096 != case['allocatedBytesPerOperation']:
                    raise ValueError('Inconsistent cumulative allocation counters')
                samples[lane].append({'allocatedBefore': case['allocatedBefore'], 'allocatedAfter': case['allocatedAfter'],
                    'allocatedBytes': delta, 'bytesPerOperation': delta / 4096,
                    'gcBefore': [case[f'gc{i}Before'] for i in range(3)],
                    'gcAfter': [case[f'gc{i}After'] for i in range(3)]})
        results.append({'name': expected['name'], 'encodedBytes': expected['encodedBytes'], 'samples': samples,
            'wholeProcessZeroInAllAfterWindows': all(row['allocatedBytes'] == 0 for row in samples['after'])})
    args.output.write_text(json.dumps({'status': 'verified', 'identities': identities, 'cases': results,
        'scope': 'Prebuilt synchronous internal encoding and full consumption only. No estimated subtraction, public query or stable CPU/latency acceptance.',
        'fullFrameworkZeroClaim': False, 'businessResultBytesInThisFixture': 0}, indent=2) + '\n')


if __name__ == '__main__':
    main()
