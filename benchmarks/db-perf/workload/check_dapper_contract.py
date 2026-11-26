#!/usr/bin/env python3
"""Check frozen reads and either observed or matched lifecycle SQL, without execution."""
import argparse
import hashlib
import json
from pathlib import Path
import re


POLICY = Path(__file__).with_name('lifecycle-sql-matched-v1.json')


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def constants(path):
    return {name: json.loads(value) for name, value in re.findall(
        r'private const string (\w+)\s*=\s*("(?:[^"\\]|\\.)*");', path.read_text())}


def named_sql(sql, provider, names):
    """Only the driver's parameter spelling may differ from the frozen SQL."""
    if provider == 'postgresql':
        ordinals = [int(value) for value in re.findall(r'\$(\d+)', sql)]
        if ordinals != list(range(1, len(names) + 1)):
            raise ValueError('Lifecycle positional parameter order differs')
        return re.sub(r'\$(\d+)', lambda match: '@' + names[int(match[1]) - 1], sql)
    if sql.count('?') != len(names):
        raise ValueError('Lifecycle SQLite parameter count differs')
    values = iter(names)
    return re.sub(r'\?', lambda _: '@' + next(values), sql)


def lifecycle_details(manifest_path, source_path):
    policy = json.loads(POLICY.read_text())
    if sha(manifest_path) != policy['baseManifestSha256']:
        raise ValueError('Matched lifecycle requires the unchanged frozen manifest')
    manifest = json.loads(manifest_path.read_text())
    queries = {query['name']: query for query in manifest['queries']}
    folder = source_path.parent
    actual = constants(folder / 'LifecycleCases.cs')
    if set(actual) != set(policy['constants']):
        raise ValueError('Lifecycle SQL constant inventory differs')
    if constants(folder / 'LifecycleVerification.cs').get('FindSql') != actual['FindSql']:
        raise ValueError('Verification Find must use the same full SQL')
    declared = {}
    types = {'int64': 'Int64', 'string': 'String', 'nullable-string': 'String'}
    for name, definition in policy['constants'].items():
        step = next(item for item in queries[definition['case']]['lifecycle']['steps']
                    if item['name'] == definition['step'])
        names = definition['parameterNames']
        if len(names) != len(step['parameters']) or len(set(names)) != len(names):
            raise ValueError('Lifecycle parameter inventory differs')
        for provider in ('postgresql', 'sqlite'):
            if actual[name] != named_sql(step['sql'][provider], provider, names):
                raise ValueError(f'Matched {provider} {name} differs from Korda business SQL')
        declared[name] = {'sql': actual[name], 'orderedParameters': [
            {'name': parameter_name, 'dbType': types[parameter['kind']],
             'manifestKind': parameter['kind']} for parameter_name, parameter in zip(names, step['parameters'])]}
    cases = {}
    for name in ('tx-commit', 'tx-rollback', 'partial-update', 'stale-conflict'):
        query = queries[name]
        if [column['name'] for column in query['columns']] != policy['resultColumns']:
            raise ValueError('Lifecycle full DTO column order differs')
        steps = []
        for step in query['lifecycle']['steps']:
            step_name = step['name']
            if step_name.startswith('find-'):
                sql_name = 'FindSql'
            elif step_name == 'stale-change':
                sql_name = 'StaleSql'
            elif step_name in ('change', 'restore-before-commit'):
                sql_name = ('PartialSql' if name == 'partial-update' else
                            'CheckedSql' if name == 'stale-conflict' else 'UpdateSql')
            elif step_name in ('commit', 'rollback'):
                steps.append({'step': step_name, 'providerTransactionApi': True})
                continue
            else:
                raise ValueError(f'Unknown lifecycle step {name}/{step_name}')
            parameter_names = policy['constants'][sql_name]['parameterNames']
            for provider in ('postgresql', 'sqlite'):
                if actual[sql_name] != named_sql(step['sql'][provider], provider, parameter_names):
                    raise ValueError(f'Matched lifecycle step differs: {name}/{step_name}/{provider}')
            steps.append({'step': step_name, 'constant': sql_name, 'kordaSql': step['sql'],
                          'parameters': [{**parameter, 'driverName': driver_name}
                                         for parameter, driver_name in zip(step['parameters'], parameter_names)]})
        cases[name] = {'steps': steps, 'expectedRows': query['expectedRows'],
                       'expectedDigest': query['expectedDigest']}
    compact = re.sub(r'\s+', '', (folder / 'LifecycleCases.cs').read_text())
    verify = re.sub(r'\s+', '', (folder / 'LifecycleVerification.cs').read_text())
    if 'longid=_options.OrdersPerTenant;varkey=new{tenant=1L,id,take=2L};' not in compact:
        raise ValueError('Lifecycle Find requires Int64 tenant/id/limit=2')
    if 'varkey=new{tenant=1L,id=(long)options.OrdersPerTenant,take=2L};' not in verify:
        raise ValueError('Verification Find must use the same Int64 limit')
    if '[CacheCommand(false)]privateasyncValueTask<OperationResult>TransactionCase(' not in compact:
        raise ValueError('Lifecycle commands must retain their uncached construction policy')
    for expression in (
        'varchanges=new{tenant=1L,id,note=name=="partial-update"?(string?)null:"perf-mutated",version=2L,expected=1L};',
        'new{tenant=1L,id,note="stale-write",expected=1L}',
        'varrestore=new{tenant=1L,id,note=original.Note,version=original.Version};'):
        if expression not in compact:
            raise ValueError('Lifecycle mutation parameter values or types differ')
    return {'policySha256': sha(POLICY), 'constants': declared, 'cases': cases,
            'resultColumns': policy['resultColumns'],
            'placeholderPolicy': policy['providerPlaceholderPolicy'],
            'providerControls': policy['providerControls']}


def validate(manifest_path: Path, source_path: Path, lifecycle_sql_mode='observed'):
    if lifecycle_sql_mode not in ('observed', 'matched'):
        raise ValueError('Unknown lifecycle SQL mode')
    manifest=json.loads(manifest_path.read_text())
    text=source_path.read_text()
    postgres=text.split('#if POSTGRES',1)[1].split('#else',1)[0]
    sqlite=text.split('#else',1)[1].split('#endif',1)[0]
    reads=[query for query in manifest['queries'] if 'lifecycle' not in query]
    for provider,block in [('postgresql',postgres),('sqlite',sqlite)]:
        actual={int(index):json.loads(value) for index,value in re.findall(r'private const string Sql(\d+)\s*=\s*("(?:[^"\\]|\\.)*");',block)}
        if len(actual)!=len(reads): raise ValueError('Generated read SQL inventory differs from manifest')
        for index,query in enumerate(reads):
            expected=query['sql'][provider]
            def name(ordinal,param):
                return '@'+(param['name'] if query['name']=='related' else 'pattern' if param['kind']=='string' else 'p'+str(ordinal))
            if provider=='postgresql':
                for ordinal,param in reversed(list(enumerate(query['parameters'],1))):
                    expected=re.sub(r'\$'+str(ordinal)+r'(?![0-9])',name(ordinal,param),expected)
            else:
                parameters=iter(enumerate(query['parameters'],1))
                expected=re.sub(r'\?',lambda _:name(*next(parameters)),expected)
            if actual[index]!=expected:
                raise ValueError(f'Dapper {provider} {query["name"]} SQL differs from frozen manifest')
    lifecycle=[query for query in manifest['queries'] if 'lifecycle' in query]
    if lifecycle:
        if {query['name'] for query in lifecycle}!={'tx-commit','tx-rollback','partial-update','stale-conflict','early-stop'}:
            raise ValueError('Unknown lifecycle contract inventory')
        if lifecycle_sql_mode == 'matched':
            lifecycle_details(manifest_path, source_path)
        else:
            actual = constants(source_path.parent / 'LifecycleCases.cs')
            for query in lifecycle:
                for name,value in query['lifecycle'].get('independentOrmSql',{}).items():
                    if actual.get(name)!=value:raise ValueError(f'Dapper lifecycle {name} differs from its observed ORM SQL contract')
    return manifest


def contract_receipt(manifest_path, source_path, lifecycle_sql_mode):
    validate(manifest_path, source_path, lifecycle_sql_mode)
    files = [source_path, *(source_path.parent / name for name in
             ('LifecycleCases.cs', 'LifecycleVerification.cs', 'Models.cs', 'Workload.cs'))]
    return {'schemaVersion': 1, 'lifecycleSqlMode': lifecycle_sql_mode,
            'manifestSha256': sha(manifest_path), 'validatorSha256': sha(Path(__file__)),
            'sources': {file.name: sha(file) for file in files},
            'lifecycle': lifecycle_details(manifest_path, source_path) if lifecycle_sql_mode == 'matched' else None,
            'generatedFactoriesVerified': False, 'runtimeSqlTraceVerified': False,
            'scope': 'Static source contract. Generated factories and actual business results require their own evidence.'}


def validate_generated(folder, receipt):
    """Read the actual successful AOT build output; never synthesize a factory."""
    if receipt['lifecycleSqlMode'] != 'matched':
        raise ValueError('Generated lifecycle proof requires matched mode')
    for name, expected in receipt['sources'].items():
        if sha(folder / name) != expected:
            raise ValueError('Consumer source changed after static SQL validation')
    paths = list((folder / 'generated').rglob('DbPerf.Dapper.generated.cs'))
    if len(paths) != 1:
        raise ValueError('Expected one actual Dapper.AOT generated workload')
    text = paths[0].read_text(encoding='utf-8-sig')
    blocks = {match['name']: match['body'] for match in re.finditer(
        r'^        private sealed class (?P<name>(?:CommandFactory|RowFactory)\d+)[^\n]*\n'
        r'(?P<body>.*?)^        }', text, re.MULTILINE | re.DOTALL)}
    methods = re.finditer(
        r'(?P<attributes>(?:^[ \t]*\[global::System.Runtime.CompilerServices.InterceptsLocationAttribute\([^\n]*\)\]\n)+)'
        r'[ \t]*internal static[^\n]*\n(?P<body>.*?)^        }', text, re.MULTILINE | re.DOTALL)
    lines = {name: (folder / name).read_text().splitlines() for name in
             ('LifecycleCases.cs', 'LifecycleVerification.cs')}
    found = []
    for method in methods:
        locations = re.findall(r'InterceptsLocationAttribute\("([^"\n]+)", (\d+), (\d+)\)', method['attributes'])
        for path, line, column in locations:
            source_name = Path(path).name
            if source_name not in lines:
                continue
            call = re.search(r'(?:QuerySingleAsync<Order>|ExecuteAsync)\((\w+),',
                             lines[source_name][int(line) - 1][int(column) - 1:])
            if not call or call[1] not in receipt['lifecycle']['constants']:
                continue
            definition = receipt['lifecycle']['constants'][call[1]]
            factory = re.search(r'(CommandFactory\d+)\.Instance', method['body'])
            if not factory or factory[1] not in blocks:
                raise ValueError('Lifecycle call lacks its generated command factory')
            bindings = re.findall(r'p\.ParameterName = "(\w+)";\s*'
                r'p\.DbType = global::System.Data.DbType\.(\w+);\s*'
                r'p\.Direction = global::System.Data.ParameterDirection.Input;\s*'
                r'(?:p\.Value = AsValue\(typed\.(\w+)\);|SetValueWithDefaultSize\(p, typed\.(\w+)\);)',
                blocks[factory[1]])
            parameters = {name: db_type for name, db_type, value, sized in bindings}
            expected = {parameter['name']: parameter['dbType'] for parameter in definition['orderedParameters']}
            if (parameters != expected or len(bindings) != len(expected)
                    or any(name != (value or sized) for name, db_type, value, sized in bindings)):
                raise ValueError(f'Actual generated parameters differ for {call[1]}: {parameters}')
            entry = {'source': source_name, 'constant': call[1], 'line': int(line), 'column': int(column),
                     'factory': factory[1], 'parameterDbTypes': parameters,
                     'parameterValueMembers': {name: value or sized for name, db_type, value, sized in bindings}}
            if call[1] == 'FindSql':
                row = re.search(r'(RowFactory\d+)\.Instance', method['body'])
                if not row or row[1] not in blocks:
                    raise ValueError('Find lacks its generated row factory')
                row_body = blocks[row[1]]
                names = set(re.findall(r'NormalizedEquals\(name, "([^"]+)"\)', row_body))
                constructor = 'new global::Order(' + ', '.join(f'value{i}' for i in range(9)) + ')'
                if names != {name.lower() for name in receipt['lifecycle']['resultColumns']} or constructor not in row_body:
                    raise ValueError('Find does not generate the complete nine-column Order')
                entry['rowFactory'] = row[1]
            found.append(entry)
    counts = {name: sum(item['source'] == 'LifecycleCases.cs' and item['constant'] == name for item in found)
              for name in receipt['lifecycle']['constants']}
    verification = [item for item in found if item['source'] == 'LifecycleVerification.cs']
    if (counts != {'FindSql': 3, 'UpdateSql': 2, 'PartialSql': 2, 'CheckedSql': 1, 'StaleSql': 1}
            or len(verification) != 3 or any(item['constant'] != 'FindSql' for item in verification)):
        raise ValueError(f'Actual generated lifecycle call inventory differs: {counts}')
    return {'schemaVersion': 1, 'generatedFactoriesVerified': True,
            'generatedFile': str(paths[0].relative_to(folder)), 'generatedSha256': sha(paths[0]),
            'sources': receipt['sources'], 'calls': found, 'runtimeSqlTraceVerified': False,
            'scope': 'Actual emitted AOT source and typed call bindings; no runtime or timing credit.'}


if __name__=='__main__':
    here=Path(__file__).resolve().parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', type=Path, default=here/'queries-v3.json')
    parser.add_argument('--source', type=Path, default=here.parent/'managed/Dapper/Contracts.generated.cs')
    parser.add_argument('--lifecycle-sql-mode', choices=('observed', 'matched'), default='matched')
    parser.add_argument('--receipt', type=Path)
    args = parser.parse_args()
    result = contract_receipt(args.manifest, args.source, args.lifecycle_sql_mode)
    if args.receipt:
        with args.receipt.open('x') as stream:
            json.dump(result, stream, indent=2); stream.write('\n')
    print(json.dumps({'lifecycleSqlMode': args.lifecycle_sql_mode, 'staticContract': 'passed',
                      'manifestSha256': result['manifestSha256'], 'generatedFactoriesVerified': False}))
