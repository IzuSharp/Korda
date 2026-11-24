#!/usr/bin/env python3
"""Freeze a fully-consumed wide stream from all eight actual managed AOT probes."""
import argparse
import copy
import hashlib
import json
from pathlib import Path


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--reads', type=Path, required=True)
    parser.add_argument('--reports', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    source = json.loads(args.reads.read_text())
    reports = {}
    for route in ('korda', 'dapper'):
        for provider in ('sqlite', 'postgresql'):
            for mode in ('prepared', 'dynamic'):
                name = f'{route}-{provider}-{mode}'
                path = args.reports / (name + '.json')
                report = json.loads(path.read_text())
                assert report['NativeAot'] and report['Completed'] > 0 and report['Failed'] == 0
                assert report['ActualCaseIds'] == ['wide']
                expected = report['ExpectedResults']['wide']
                assert expected['Rows'] == report['RequestedRows'] >= 1024
                assert report['Rows'] == report['Completed'] * expected['Rows']
                assert report.get('OwnersReturnedToZero', report.get('ApplicationConnectionsReturnedToZero'))
                reports[name] = (path, report)
    first = next(iter(reports.values()))[1]
    for _, report in reports.values():
        assert report['ExpectedResults']['wide'] == first['ExpectedResults']['wide']
        for key in ('RequestedRows', 'BatchRows', 'Tenants', 'OrdersPerTenant'):
            assert report[key] == first[key], key
    query = copy.deepcopy(next(item for item in source['queries'] if item['name'] == 'wide'))
    expected = first['ExpectedResults']['wide']
    query.update(expectedRows=expected['Rows'], expectedDigest=str(expected['Digest']),
                 expectedDigestHex=f"{expected['Digest']:016x}",
                 caseRole='fully-consumed-wide-stream', materialization='stream',
                 validation={'kind': 'Actual full consumption and independent seed-formula digest in both modes, providers and managed routes',
                             'performanceAcceptance': False})
    query['parameters'] = [{'name': 'tenant', 'kind': 'int64', 'value': 1},
                           {'name': 'take', 'kind': 'int64', 'value': first['RequestedRows']}]
    for provider in ('sqlite', 'postgresql'):
        actual = reports['korda-'+provider+'-prepared'][1]['ActualDiagnosticSql']['wide']
        assert actual == query['sql'][provider]
        independent = reports['dapper-'+provider+'-prepared'][1]['ActualDiagnosticSql']['wide']
        independent = independent.replace('@p1', '$1' if provider == 'postgresql' else '?').replace('@p2', '$2' if provider == 'postgresql' else '?')
        assert independent == actual, provider
    result = {'schemaVersion': 3,
              'dataset': {'tenants': first['Tenants'], 'ordersPerTenant': first['OrdersPerTenant'],
                          'customers': 256, 'requestedRows': first['RequestedRows'], 'batchRows': first['BatchRows']},
              'digest': source['digest'], 'sourceManifest': {'path': str(args.reads), 'sha256': sha(args.reads)},
              'invocationOverrides': {'case': 'wide', 'cases': ['wide'], 'rows': first['RequestedRows'],
                                      'batchRows': first['BatchRows'], 'tenants': first['Tenants'], 'ordersPerTenant': first['OrdersPerTenant']},
              'scope': 'Separate heavy read lane; does not modify the frozen normal35 mix. Same compiled managed applications, with explicit runtime row/dataset options.',
              'validationInputs': {name: {'path': str(path), 'sha256': sha(path),
                                         'executableSha256': report['ExecutableSha256'], 'packageVersion': report['PackageVersion'],
                                         'loadedNativeSha256': report['LoadedNativeSha256'], 'rawAllocatedBefore': report['AllocatedBefore'],
                                         'rawAllocatedAfter': report['AllocatedAfter'], 'completed': report['Completed'], 'rows': report['Rows']}
                                   for name, (path, report) in reports.items()},
              'queries': [query]}
    args.output.write_text(json.dumps(result, indent=2)+'\n')
    print('Exported full wide stream:', expected['Rows'], 'rows, digest', expected['Digest'])


if __name__ == '__main__':
    main()
