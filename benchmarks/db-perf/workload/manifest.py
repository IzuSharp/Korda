#!/usr/bin/env python3
"""Emit the shared deterministic DB-Perf-01 v1 read contract. This is not a measurement."""
import argparse
import json
import struct


class Digest:
    def __init__(self): self.value = 14695981039346656037
    def raw(self, data):
        for byte in data:
            self.value = ((self.value ^ byte) * 1099511628211) & ((1 << 64) - 1)
    def integer(self, value): self.raw(struct.pack('<q', value))
    def boolean(self, value): self.raw(bytes([bool(value)]))
    def text(self, value):
        if value is None: self.integer(-1); return
        data = value.encode('utf-8'); self.integer(len(data)); self.raw(data)


def customer(identifier):
    return None if identifier % 7 == 0 else 1 + identifier % 8 if identifier % 10 < 7 else 9 + identifier % 248


def manifest(rows=32, orders=1024, tenants=8):
    result = []
    definitions = [
        ('numeric', ['Id'], ['int64']),
        ('text', ['Id', 'Note'], ['int64', 'string']),
        ('money', ['Id', 'Total'], ['int64', 'cents-from-decimal']),
        ('json', ['Id', 'Settings'], ['int64', 'settings']),
        ('wide', ['TenantId', 'Id', 'CreatedAt', 'CustomerId', 'Total', 'Note', 'Version', 'Settings', 'InternalMemo'],
         ['int64', 'int64', 'int64', 'nullable-int64', 'cents-from-decimal', 'string', 'int64', 'settings', 'string']),
    ]
    for name, fields, kinds in definitions:
        digest = Digest()
        for identifier in range(1, min(rows, orders) + 1):
            if name == 'wide': digest.integer(1)
            digest.integer(identifier)
            if name == 'wide':
                digest.integer(1700000000 + identifier // 4)
                cid = customer(identifier); digest.boolean(cid is not None)
                if cid is not None: digest.integer(cid)
            if name in ('money', 'wide'): digest.integer(100 + (1009 + identifier * 37) % 100000)
            if name in ('text', 'wide'): digest.text(None if identifier % 5 == 0 else 'note-' + str(identifier % 16))
            if name == 'wide': digest.integer(1)
            if name in ('json', 'wide'):
                digest.text('web' if identifier % 2 == 0 else 'store'); digest.boolean(identifier % 11 == 0)
            if name == 'wide': digest.text('m' * (2048 if identifier % 17 == 0 else 32))
        columns = ', '.join('"orders"."' + field + '" AS "' + ('value' if name == 'numeric' else field) + '"' for field in fields)
        sql = 'SELECT ' + columns + ' FROM "orders" WHERE "orders"."TenantId" = {p1} ORDER BY "orders"."Id" ASC NULLS LAST LIMIT {p2}'
        result.append({
            'name': name, 'entry': 'fluent',
            'sql': {'sqlite': sql.format(p1='?', p2='?'), 'postgresql': sql.format(p1='$1', p2='$2')},
            'parameters': [{'name': 'tenant', 'kind': 'int64', 'value': 1}, {'name': 'take', 'kind': 'int64', 'value': rows}],
            'columns': [{'name': field, 'alias': 'value' if name == 'numeric' else field, 'kind': kind, 'nullable': field in ('Note', 'CustomerId'),
                         **({'sqliteStorage': 'integer-cents', 'postgresqlStorage': 'numeric(18,2)'} if field == 'Total' else {})}
                        for field, kind in zip(fields, kinds)],
            'expectedRows': min(rows, orders), 'expectedDigest': str(digest.value),
            'expectedDigestHex': f'{digest.value:016x}', 'materialization': 'stream',
            'businessObjects': 'scalar long' if name == 'numeric' else 'one owned typed DTO per row; strings/JSON objects retained until row consumed',
            'validationStatus': 'contract only; acceptance requires a matching executable hash and actual execution receipt',
        })
    return {'schemaVersion': 1, 'dataset': {'tenants': tenants, 'ordersPerTenant': orders, 'customers': 256, 'schemaMetadata': 'db_perf_metadata'},
            'digest': {'algorithm': 'FNV-1a 64 bit', 'offset': '14695981039346656037', 'prime': '1099511628211',
                       'integer': 'signed Int64 little-endian bytes', 'nullable-int64': 'one-byte presence then integer only if present',
                       'string': 'signed Int64 UTF-8 byte length then UTF-8; NULL length=-1',
                       'cents-from-decimal': 'exact decimal value times 100 to signed Int64; SQLite already stores cents',
                       'settings': 'string Channel then one-byte boolean Gift', 'rowSeparator': 'none'},
            'queries': result}


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--rows', type=int, default=32)
    p.add_argument('--orders-per-tenant', type=int, default=1024)
    p.add_argument('--tenants', type=int, default=8)
    a = p.parse_args()
    print(json.dumps(manifest(a.rows, a.orders_per_tenant, a.tenants), indent=2))
