#!/usr/bin/env python3
"""Bind the five normal lifecycle contracts to actual public-package AOT results.

The statements are business operations, not a claim that providers use the same
number of internal SQL calls. PostgreSQL Korda statements are verified against
the retained server log. SQLite placeholder conversion follows the same CRUD
renderer; SQLite correctness is separately required. Timing acceptance is never
inferred from these probes.
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import re


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--reads', type=Path, required=True)
    parser.add_argument('--sqlite', type=Path, required=True)
    parser.add_argument('--postgresql', type=Path, required=True)
    parser.add_argument('--dapper-sqlite', type=Path, required=True)
    parser.add_argument('--dapper-postgresql', type=Path, required=True)
    parser.add_argument('--postgres-log', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--combined', type=Path, required=True)
    args = parser.parse_args()
    reads = json.loads(args.reads.read_text())
    paths = {'korda-sqlite': args.sqlite, 'korda-postgresql': args.postgresql,
             'dapper-sqlite': args.dapper_sqlite, 'dapper-postgresql': args.dapper_postgresql}
    reports = {route: json.loads(path.read_text()) for route, path in paths.items()}
    names = ('tx-commit', 'tx-rollback', 'partial-update', 'stale-conflict', 'early-stop')
    baseline = reports['korda-sqlite']
    for route, report in reports.items():
        assert report['NativeAot'] and report['Failed'] == 0, route
        assert all(any(item['Name'] == name and item['Succeeded'] > 0 and item['Failed'] == 0
                       for item in report['Cases']) for name in names), route
        for name in names:
            assert report['ExpectedResults'][name] == baseline['ExpectedResults'][name], (route, name)
        for key in ('Tenants', 'OrdersPerTenant', 'RequestedRows', 'BatchRows'):
            assert report[key] == baseline[key], (route, key)
    order_id = baseline['OrdersPerTenant']
    assert order_id == 1024, 'The exported mutation values require the frozen initial 1024-order fixture.'
    log = args.postgres_log.read_text()
    statements = [line.split('execute <unnamed>: ', 1)[1] for line in log.splitlines()
                  if 'execute <unnamed>: ' in line]
    finds = [sql for sql in statements if sql.startswith('SELECT "orders"."TenantId"')
             and 'WHERE "orders"."TenantId" = $1 AND "orders"."Id" = $2 LIMIT $3' in sql]
    assert finds and len(set(finds)) == 1
    find = finds[0]
    update = 'UPDATE "orders" SET "Note" = $1, "Version" = $2 WHERE "TenantId" = $3 AND "Id" = $4'
    partial = 'UPDATE "orders" SET "Note" = $1 WHERE "TenantId" = $2 AND "Id" = $3'
    checked = update + ' AND "Version" = $5'
    stale = partial + ' AND "Version" = $4'
    for statement in (update, partial, checked, stale, 'BEGIN ISOLATION LEVEL READ COMMITTED READ WRITE',
                      'COMMIT', 'ROLLBACK', 'SELECT 1'):
        assert statement in statements, statement
    wide = next(query for query in reads['queries'] if query['name'] == 'wide')
    original = {'TenantId': 1, 'Id': order_id, 'CreatedAt': 1700000256, 'CustomerId': 1,
                'TotalCents': 38997, 'Note': 'note-0', 'Version': 1,
                'Settings': {'Channel': 'web', 'Gift': False}, 'InternalMemo': 'm' * 32}

    def parameters(values):
        return [{'name': f'p{index+1}', 'kind': 'nullable-string' if value is None else
                 'string' if isinstance(value, str) else 'int64', 'value': value}
                for index, value in enumerate(values)]

    def step(name, sql, values=(), **more):
        return {'name': name, 'sql': {'postgresql': sql, 'sqlite': re.sub(r'\$\d+', '?', sql)},
                'parameters': parameters(values), **more}

    lookup = step('find-original', find, (1, order_id, 2), expectedRows=1, hash='full-order')
    result = []
    for name in names:
        expected = baseline['ExpectedResults'][name]
        item = {'name': name, 'entry': 'fluent' if name == 'early-stop' else 'public-crud-and-transactions',
                'operationKind': {'tx-commit': 'transaction-read-write', 'tx-rollback': 'transaction-write-rollback',
                                  'partial-update': 'partial-update', 'stale-conflict': 'optimistic-write-conflict',
                                  'early-stop': 'early-read-termination'}[name],
                'writesDatabase': name != 'early-stop',
                'expectedRows': expected['Rows'], 'expectedDigest': str(expected['Digest']),
                'expectedDigestHex': f"{expected['Digest']:016x}", 'columns': copy.deepcopy(wide['columns']),
                'validation': {'kind': 'actual Native AOT execution on both databases and both managed routes',
                               'performanceAcceptance': False}}
        if name == 'early-stop':
            item.update(sql={provider: reports['korda-'+provider]['ActualDiagnosticSql'][name]
                             for provider in ('sqlite', 'postgresql')},
                        parameters=parameters((1, order_id)), materialization='stream-early-stop',
                        consumeRows=3, queryExecutionsPerOperation=1,
                        preparedContract='Reuse the full typed query shape; consume first three owned rows and await enumerator disposal',
                        lifecycle={'kind': 'early-stop', 'limit': order_id, 'consumeRows': 3,
                                   'digest': 'First three full orders, in Id order, with the same wide field encodings',
                                   'cleanup': 'Await stream/enumerator disposal; remaining result cleanup is part of the operation'})
        else:
            steps = [copy.deepcopy(lookup)]
            changing = step('change', partial if name == 'partial-update' else checked if name == 'stale-conflict' else update,
                            (None, 1, order_id) if name == 'partial-update' else
                            ('perf-mutated', 2, 1, order_id, 1) if name == 'stale-conflict' else
                            ('perf-mutated', 2, 1, order_id), expectedAffected=1)
            steps.append(changing)
            if name == 'stale-conflict':
                steps.append(step('stale-change', stale, ('stale-write', 1, order_id, 1),
                                  expectedAffected=0, hash='boolean true', expectedOutcome='optimistic conflict'))
            else:
                steps.append(step('find-changed', find, (1, order_id, 2), expectedRows=1, hash='full-order',
                                  changedFields={'Note': None} if name == 'partial-update' else
                                  {'Note': 'perf-mutated', 'Version': 2}))
                if name != 'tx-rollback':
                    steps.append(step('restore-before-commit', partial if name == 'partial-update' else update,
                                      ('note-0', 1, order_id) if name == 'partial-update' else
                                      ('note-0', 1, 1, order_id), expectedAffected=1))
            finish = 'rollback' if name in ('tx-rollback', 'stale-conflict') else 'commit'
            steps.append({'name': finish, 'sql': {'sqlite': finish.upper(), 'postgresql': finish.upper()},
                          'parameters': [], 'completion': 'confirm transaction completion before final lookup'})
            steps.append(step('find-persisted', find, (1, order_id, 2), expectedRows=1,
                              hash='full-order', expectedEquals='original all nine fields'))
            item.update(sql=copy.deepcopy(lookup['sql']), parameters=copy.deepcopy(lookup['parameters']),
                        materialization='transaction', queryExecutionsPerOperation=len(steps)+1,
                        preparedContract='Public CRUD has no prepared-plan overload; construct typed key/changes/commands during each operation in both requested modes',
                        lifecycle={'kind': name, 'target': {'tenant': 1, 'id': order_id}, 'initial': original,
                                   'begin': {'postgresql': 'BEGIN ISOLATION LEVEL READ COMMITTED READ WRITE',
                                             'sqlite': 'BEGIN IMMEDIATE'},
                                   'steps': steps, 'digest': 'Concatenate only steps marked hash; all full orders use wide column order',
                                   'finalState': 'Exact original durable row; other rows and all omitted fields unchanged',
                                   'actualProviderOverhead': {
                                       'korda-postgresql': 'Commit path adds SELECT 1 before COMMIT; rollback does not. This internal query is included in Korda counters, not imposed on direct native business contract.',
                                       'korda-sqlite': 'Transaction begin additionally executes PRAGMA query_only=OFF. Internal controls belong to framework overhead.',
                                       'dapper': 'Uses normal provider transaction API; full QuerySingle<Order> SQL omits LIMIT 2 but composite key guarantees at most one row.'},
                                   'independentOrmSql': {
                                       'FindSql': 'SELECT "TenantId","Id","CreatedAt","CustomerId","Total","Note","Version","Settings","InternalMemo" FROM orders WHERE "TenantId"=@tenant AND "Id"=@id',
                                       'UpdateSql': 'UPDATE orders SET "Note"=@note,"Version"=@version WHERE "TenantId"=@tenant AND "Id"=@id',
                                       'PartialSql': 'UPDATE orders SET "Note"=@note WHERE "TenantId"=@tenant AND "Id"=@id',
                                       'CheckedSql': 'UPDATE orders SET "Note"=@note,"Version"=@version WHERE "TenantId"=@tenant AND "Id"=@id AND "Version"=@expected',
                                       'StaleSql': 'UPDATE orders SET "Note"=@note WHERE "TenantId"=@tenant AND "Id"=@id AND "Version"=@expected'},
                                   'transactionModeValidation': 'v4 single-worker SQLite correctness used default Korda deferred BEGIN; final harness explicitly selects Immediate to match Microsoft.Data.Sqlite Serializable and direct native. Final binaries require their own correctness check.'})
        item['construction'] = {provider: {'kind': 'SQL-token-concatenation; distinct from typed Fluent/CRUD construction',
                                          'fragments': re.findall(r'\s+|[^\s]+', sql)}
                                for provider, sql in item['sql'].items()}
        result.append(item)
    evidence = {route: {'path': str(path), 'sha256': sha(path),
                        'executableSha256': reports[route]['ExecutableSha256'],
                        'packageVersion': reports[route]['PackageVersion']}
                for route, path in paths.items()}
    document = {'schemaVersion': 1, 'dataset': reads['dataset'], 'digest': reads['digest'],
                'validationInputs': evidence, 'postgresStatementLog': {'path': str(args.postgres_log), 'sha256': sha(args.postgres_log)},
                'sqliteStatementProvenance': 'Same CRUD renderer with positional placeholders; SQL text derived from captured PostgreSQL form. Whole-operation SQLite result, affected rows and persistent-state checks ran independently.',
                'queries': result}
    args.output.write_text(json.dumps(document, indent=2)+'\n')
    combined = copy.deepcopy(reads)
    combined['schemaVersion'] = 3
    combined['readContractSource'] = {'path': str(args.reads), 'sha256': sha(args.reads)}
    combined['lifecycleContractSource'] = {'path': str(args.output), 'sha256': sha(args.output)}
    combined['queries'].extend(result)
    args.combined.write_text(json.dumps(combined, indent=2)+'\n')
    print(f'Exported five lifecycle operations and {len(combined["queries"])} total contracts')


if __name__ == '__main__':
    main()
