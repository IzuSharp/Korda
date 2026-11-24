#!/usr/bin/env python3
"""Freeze SQL, typed consumption and independent deterministic read oracles."""

import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path
import re
import struct

ROOT = Path(__file__).resolve().parent


class Digest:
    def __init__(self):
        self.value = 14695981039346656037

    def raw(self, value):
        for item in value:
            self.value = ((self.value ^ item) * 1099511628211) & ((1 << 64) - 1)

    def integer(self, value):
        self.raw(struct.pack('<q', value))

    def boolean(self, value):
        self.raw(bytes([bool(value)]))

    def text(self, value):
        data = None if value is None else value.encode('utf-8')
        self.integer(-1 if data is None else len(data))
        if data is not None:
            self.raw(data)

    def nullable_integer(self, value):
        self.boolean(value is not None)
        if value is not None:
            self.integer(value)


def customer(order):
    return None if order % 7 == 0 else 1 + order % 8 if order % 10 < 7 else 9 + order % 248


def created(order):
    return 1_700_000_000 + order // 4


def cents(order, tenant=1):
    return 100 + (tenant * 1009 + order * 37) % 100_000


def note(order):
    return None if order % 5 == 0 else f'note-{order % 16}'


def line_count(order):
    return 0 if order % 5 == 0 else 1 + order % 3


def column(name, kind, nullable=False):
    return dict(name=name, type=kind, nullable=nullable)


def clauses(sql):
    # Retain syntactic clauses, including nested clauses, as construction inputs.
    # A dynamic adapter appends these in order and binds every value afresh.
    # A prepared adapter reuses the final SQL; its physical preparation policy is
    # separately reported. No adapter substitutes a whole-SQL copy for this loop.
    chunks = re.split(r'(?=\b(?:SELECT|FROM|WHERE|LEFT JOIN|INNER JOIN|GROUP BY|HAVING|ORDER BY|LIMIT|OFFSET|UNION ALL|UNION|INTERSECT|EXCEPT|WITH)\b)', sql)
    chunks = [x for x in chunks if x]
    assert ''.join(chunks) == sql
    return chunks


def postgres_sql(sql):
    ordinal = 0

    def parameter(_):
        nonlocal ordinal
        ordinal += 1
        return '$' + str(ordinal)

    return re.sub(r'\?', parameter, sql)


def contract(rows=32, orders=8192, tenants=8, batch_rows=128):
    observed = json.loads((ROOT / 'read-sql.sqlite.json').read_text())
    sqls = observed['sql']
    actual_path = ROOT / 'read-sql.actual.json'
    actual = json.loads(actual_path.read_text()) if actual_path.is_file() else {}
    ids = list(range(1, orders + 1))
    lines = []
    for order in ids:
        for index in range(line_count(order)):
            lines.append((len(lines) + 1, 1, order, f'product-{(order * 7 + index) % 32:02d}', 1 + (order + index) % 7))
    by_order = defaultdict(list)
    for line in lines:
        by_order[line[2]].append(line)

    shapes = {
        'scalar-int64': [column('value', 'int64')],
        'text': [column('Id', 'int64'), column('Note', 'string', True)],
        'money': [column('Id', 'int64'), column('Total', 'money')],
        'json': [column('Id', 'int64'), column('Settings', 'settings')],
        'wide': [column('TenantId', 'int64'), column('Id', 'int64'), column('CreatedAt', 'int64'), column('CustomerId', 'nullable-int64', True), column('Total', 'money'), column('Note', 'string', True), column('Version', 'int64'), column('Settings', 'settings'), column('InternalMemo', 'string')],
        'join': [column('Id', 'int64'), column('Name', 'string', True)],
        'customer-total': [column('CustomerId', 'nullable-int64', True), column('Count', 'int64'), column('Total', 'nullable-money', True)],
        'product-total': [column('Product', 'string'), column('Quantity', 'nullable-int64', True)],
        'nullable-money': [column('value', 'nullable-money', True)],
        'rank': [column('Id', 'int64'), column('Rank', 'int64')],
        'running': [column('Id', 'int64'), column('Quantity', 'nullable-int64', True)],
        'detail': [column('OrderId', 'int64'), column('Name', 'string'), column('Product', 'string'), column('Quantity', 'int64')],
        'related': [column('Id', 'int64'), column('TenantId', 'int64'), column('OrderId', 'int64'), column('Product', 'string'), column('Quantity', 'int64'), column('__korda_parent_index', 'int32')],
    }
    cases = []

    def hash_value(digest, kind, value):
        if kind in ('int32', 'int64', 'money'):
            digest.integer(value)
        elif kind in ('nullable-int64', 'nullable-money'):
            digest.nullable_integer(value)
        elif kind == 'string':
            digest.text(value)
        elif kind == 'settings':
            digest.text(value[0]); digest.boolean(value[1])
        else:
            raise ValueError(kind)

    def add(name, shape, params, results, sql=None, entry='fluent', semantic=True):
        sql = actual.get('sqlite', {}).get(name, sql if sql is not None else sqls[name])
        digest = Digest()
        for row in results:
            assert len(row) == len(shapes[shape]), (name, row)
            for col, value in zip(shapes[shape], row):
                hash_value(digest, col['type'], value)
        parameters = [p if isinstance(p,dict) else dict(type='text' if isinstance(p, str) else 'int64', value=p) for p in params]
        assert sql.count('?') == len(parameters), name
        pg = actual.get('postgresql', {}).get(name, postgres_sql(sql))
        cases.append(dict(id=f'R{len(cases)+1:02d}', name=name, entry=entry, semantically_distinct=semantic,
            sql=dict(sqlite=sql, postgresql=pg), fragments=dict(sqlite=clauses(sql), postgresql=clauses(pg)),
            parameters=parameters, columns=shapes[shape], materialization=shape,
            expected=dict(rows=len(results), digest=str(digest.value), digest_hex=f'{digest.value:016x}'),
            status='contract; each provider and route require actual correctness validation'))

    settings = lambda i: ('web' if i % 2 == 0 else 'store', i % 11 == 0)
    add('numeric', 'scalar-int64', [1, rows], [(i,) for i in ids[:rows]])
    add('text', 'text', [1, rows], [(i, note(i)) for i in ids[:rows]])
    add('money', 'money', [1, rows], [(i, cents(i)) for i in ids[:rows]])
    add('json', 'json', [1, rows], [(i, settings(i)) for i in ids[:rows]])
    add('wide', 'wide', [1, rows], [(1, i, created(i), customer(i), cents(i), note(i), 1, settings(i), 'm' * (2048 if i % 17 == 0 else 32)) for i in ids[:rows]])
    add('optional-filter', 'scalar-int64', [1, 2, rows], [(i,) for i in ids if customer(i) == 2][:rows])
    add('range', 'scalar-int64', [1, 1700000100, 1700000200, rows], [(i,) for i in ids if 1700000100 <= created(i) < 1700000200][:rows])
    add('left-join', 'join', [1, rows], [(i, None if customer(i) is None else f'Customer-{customer(i):04d}') for i in ids[:rows]])
    add('inner-join', 'join', [1, rows], [(i, f'Customer-{customer(i):04d}') for i in ids if customer(i) is not None][:rows])
    add('null-notes', 'scalar-int64', [1, rows], [(i,) for i in ids if note(i) is None][:rows])
    add('literal-note', 'scalar-int64', [1, '%note-1%', rows], [(i,) for i in ids if note(i) is not None and 'note-1' in note(i)][:rows])
    groups = defaultdict(list)
    for i in ids:
        groups[customer(i)].append(i)
    totals = [(key, len(group), sum(cents(i) for i in group)) for key, group in sorted(groups.items(), key=lambda item: (item[0] is None, item[0] or 0)) if len(group) > 3]
    add('customer-totals', 'customer-total', [1, 3], totals)
    products = Counter()
    for line in lines:
        products[line[3]] += line[4]
    add('product-totals', 'product-total', [1, 10], [(key, value) for key, value in sorted(products.items()) if value > 10])
    add('empty-sum', 'nullable-money', [-1], [(None,)])
    middle = orders // 2
    add('cursor', 'scalar-int64', [1, created(middle), created(middle), 1, 1, middle, rows], [(i,) for i in ids[middle:middle+rows]])
    add('offset', 'scalar-int64', [1, rows, orders*3//4], [(i,) for i in ids[orders*3//4:orders*3//4+rows]])
    add('exists', 'scalar-int64', [1, 32, 1], [(1,)])
    add('in-subquery', 'scalar-int64', [1, 128, rows], [(i,) for i in sorted({customer(i) for i in ids if i > 128 and customer(i) is not None})[:rows]])
    add('cte', 'scalar-int64', [1, 16, rows], [(i,) for i in ids if i > 16][:rows])
    rank = Counter(); ranks = []
    for i in ids[:rows]:
        rank[customer(i)] += 1
        ranks.append((i, rank[customer(i)]))
    add('window', 'rank', [1, rows], ranks)
    for name, values in [('union-all', sorted(list(range(1,17))+list(range(9,25)))), ('union', range(1,25)), ('intersect', range(9,17)), ('except', range(1,9))]:
        add(name, 'scalar-int64', [1,16,1,9,24], [(i,) for i in values])
    total = 0; running = []
    for line in lines[:rows]:
        total += line[4]; running.append((line[0], total))
    add('running-total', 'running', [1, rows], running)
    details = [(line[2], f'Customer-{customer(line[2]):04d}', line[3], line[4]) for line in lines if customer(line[2]) is not None][:rows]
    add('three-join', 'detail', [1, rows], details)
    add('anti-association', 'scalar-int64', [1,1,5,rows], [(i,) for i in ids if all(line[4] < 5 for line in by_order[i])][:rows])
    add('nullable-order', 'scalar-int64', [1,rows], [(i,) for i in sorted(ids, key=lambda i: (customer(i) is not None, customer(i) or 0, i))[:rows]])
    raw_sql = sqls['raw-sql'].replace('@tenant','?').replace('@take','?')
    add('raw-sql', 'text', [1, rows], [(i,note(i)) for i in ids[:rows]], sql=raw_sql, entry='sql', semantic=False)
    # Relation execution is a bounded result-group operation rather than a
    # prepared Fluent plan. Its exact native SQL is independently compared before
    # timing. Parent groups include empty children and are consumed in input order.
    count = min(rows, batch_rows, orders)
    arms = []
    for index in range(count):
        arms.append('SELECT "Id", "TenantId", "OrderId", "Product", "Quantity", ? AS "__korda_parent_index" FROM "order_lines" WHERE "TenantId" = ? AND "OrderId" = ?')
    sql = ' UNION ALL '.join(arms)
    related_rows = [(*line, i-1) for i in range(1,count+1) for line in by_order[i]]
    add('related', 'related', [value for i in range(1,count+1) for value in (dict(type='int32',value=i-1),1,i)], related_rows, sql=sql, entry='relationship-api')
    relation = cases[-1]
    relation['group_count'] = count
    relation['parent_ids'] = list(range(1,count+1))
    relation['prepared_kind'] = 'unsupported public prepared plan; only relation/key metadata reuse'
    relation['sql_equivalence'] = 'public Fluent reconstruction of exact SelectParent procedure; complete PostgreSQL text matched actual server statement log; see read-sql.actual.json'
    digest = Digest()
    for i in range(1,count+1):
        digest.integer(i); digest.integer(len(by_order[i]))
        for line in by_order[i]:
            for col, value in zip(shapes['related'][:-1], line):
                hash_value(digest,col['type'],value)
    relation['expected'] = dict(rows=len(related_rows), digest=str(digest.value), digest_hex=f'{digest.value:016x}')
    add('stream-large', 'scalar-int64', [1,orders], [(i,) for i in ids], sql=sqls['numeric'], semantic=False)
    add('optional-filter-absent', 'scalar-int64', [1,rows], [(i,) for i in ids[:rows]], sql=sqls['numeric'], semantic=False)

    state_columns = [column('Id','int64'),column('Counter','int64'),column('Version','int64'),column('Note','string',True)]
    worker = dict(type='int64',value=0,runtime='worker_id_plus_one')
    def step(kind, sql, parameters=(), affected=None, state=None):
        pg = postgres_sql(sql)
        value = dict(kind=kind,sql=dict(sqlite=sql,postgresql=pg),
                     fragments=dict(sqlite=clauses(sql),postgresql=clauses(pg)),parameters=list(parameters))
        if affected is not None: value['expected_affected'] = affected
        if state is not None: value['expected_state'] = dict(counter=state[0],version=state[1],note=state[2])
        return value
    def update(counter, version, note):
        return step('execute','UPDATE "perf_state" SET "Counter" = ?, "Version" = ?, "Note" = ? WHERE "Id" = ?',
                    [dict(type='int64',value=counter),dict(type='int64',value=version),dict(type='text',value=note),worker],affected=1)
    def select(counter, version, note):
        return step('select','SELECT "Id", "Counter", "Version", "Note" FROM "perf_state" WHERE "Id" = ?',
                    [worker],state=(counter,version,note))
    def transaction(name, steps, outcome='success'):
        digest = Digest(); reads = 0
        for item in steps:
            if 'expected_state' in item:
                state=item['expected_state'];digest.integer(state['counter']);digest.integer(state['version']);digest.text(state['note']);reads+=1
        marker='/* multi-step business operation; execute steps in order */'
        cases.append(dict(id=f'W{len(cases)+1:02d}',name=name,entry='fluent-mutation-and-transaction-api',semantically_distinct=True,
            sql=dict(sqlite=marker,postgresql=marker),fragments=dict(sqlite=[marker],postgresql=[marker]),parameters=[],
            columns=state_columns,materialization='state',steps=steps,
            expected=dict(rows=reads,digest=str(digest.value),digest_hex=f'{digest.value:016x}',outcome=outcome),
            digest_policy='Each select validates Id == logical worker id + 1, then hashes Counter, Version, Note in order.',
            preparation_policy='Reuse supported read/SQL plans only; no claim of a public prepared Korda mutation API.',
            status='contract; each provider and route require actual correctness validation'))
    begin=step('begin','BEGIN');commit=step('commit','COMMIT');rollback=step('rollback','ROLLBACK')
    transaction('tx-commit',[begin,update(17,2,'committed'),select(17,2,'committed'),commit,select(17,2,'committed')])
    transaction('tx-rollback',[update(0,1,None),begin,update(99,2,'transient'),select(99,2,'transient'),rollback,select(0,1,None)])
    transaction('partial-update',[update(23,7,'base'),step('execute','UPDATE "perf_state" SET "Note" = ? WHERE "Id" = ?',
                [dict(type='text',value=None),worker],affected=1),select(23,7,None)])
    transaction('stale-version',[update(23,2,'base'),step('execute','UPDATE "perf_state" SET "Counter" = ? WHERE "Id" = ? AND "Version" = ?',
                [dict(type='int64',value=99),worker,dict(type='int64',value=1)],affected=0),select(23,2,'base')],outcome='conflict')
    return dict(schema_version=1, task='DB-Perf-01', baseline_commit=observed['baseline_commit'],
        dataset=dict(tenants=tenants, orders_per_tenant=orders, customers=256, rows=rows, batch_rows=batch_rows,
            skew='70% of non-null order references target 8 hot customers; tenant 1 is the measured hot tenant',
            nullable='every seventh customer reference and every fifth note',
            empty_relations='every fifth order has no lines',state_rows=128,
            state_schema='perf_state(Id bigint primary key, Counter bigint not null, Version bigint not null, Note text null)',
            state_ownership='Logical worker i exclusively uses Id=i+1; initial row is (i+1,0,1,NULL). Read dataset is immutable.'),
        digest=dict(algorithm='FNV-1a 64 bit', integer='signed int64 little-endian', nullable='one-byte presence plus value',
            string='int64 UTF-8 byte length then bytes; null length -1', money='exact signed int64 cents',
            settings='UTF-8 Channel then one byte Gift', related='parent id, child count, child fields; groups in input order, children by Id'),
        cases=cases, transaction_cases_status='four deterministic multi-step business contracts; concurrency conflict race is a separate correctness scenario', performance_acceptance_claimed=False)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',type=Path,default=ROOT/'contract-v1.json')
    parser.add_argument('--orders',type=int,default=8192)
    parser.add_argument('--check',action='store_true')
    args=parser.parse_args()
    encoded=(json.dumps(contract(orders=args.orders),indent=2,ensure_ascii=False)+'\n').encode()
    if args.check:
        if args.output.read_bytes()!=encoded:
            raise SystemExit('Contract differs from its deterministic generator')
    else:
        args.output.write_bytes(encoded)
    print(json.dumps(dict(cases=len(json.loads(encoded)['cases']),sha256=hashlib.sha256(encoded).hexdigest(),output=str(args.output),acceptance=False)))


if __name__=='__main__':
    main()
