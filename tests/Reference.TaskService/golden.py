#!/usr/bin/env python3
"""Independent D20 v1 byte oracle; no product code is imported."""
import hashlib
import json
import struct
from pathlib import Path


def fields(*values):
    result = bytearray(struct.pack('<I', 1))
    for value in values:
        encoded = value.encode('utf-8', errors='strict')
        result += struct.pack('<I', len(encoded)) + encoded
    return bytes(result)


def create_hash(tenant, principal, title):
    return hashlib.sha256(fields('POST /v1/items', tenant, principal, title)).hexdigest()


def attachment_hash(tenant, principal, item, digest, length, version):
    value = fields('PUT /v1/items/{id}/attachment', tenant, principal, item, digest)
    return hashlib.sha256(value + struct.pack('<qq', length, version)).hexdigest()


def scope_hash(tenant, principal, semantic):
    return hashlib.sha256(fields('scope.v1', tenant, principal, semantic)).hexdigest()


def vectors():
    result = []
    for name, tenant, principal, title in [
        ('ascii', 'tenant-a', 'alice', 'hello'),
        ('unicode', 'tenant-a', 'alice', '\u4e16\u754c \U0001f980'),
        ('composed', 'tenant-a', 'alice', '\u00e9'),
        ('decomposed', 'tenant-a', 'alice', 'e\u0301'),
        ('field-boundary-a', 'ab', 'c', 'title'),
        ('field-boundary-b', 'a', 'bc', 'title'),
    ]:
        encoded = fields('POST /v1/items', tenant, principal, title)
        result.append({'name': name, 'tenant': tenant, 'principal': principal,
                       'title': title, 'bytes': encoded.hex(),
                       'sha256': hashlib.sha256(encoded).hexdigest()})
    return {'version': 1, 'create': result,
            'attachment': {'tenant': 'tenant-a', 'principal': 'alice',
                'itemId': '00112233-4455-6677-8899-aabbccddeeff',
                'contentSha256': hashlib.sha256(b'abc').hexdigest(),
                'contentLength': 3, 'expectedVersion': 7,
                'sha256': attachment_hash('tenant-a', 'alice',
                    '00112233-4455-6677-8899-aabbccddeeff',
                    hashlib.sha256(b'abc').hexdigest(), 3, 7)}}


def verify_fixture(path):
    actual = vectors()
    expected = json.loads(Path(path).read_text())
    if actual != expected:
        raise AssertionError('The immutable v1 golden fixture changed.')
    if actual['create'][2]['sha256'] == actual['create'][3]['sha256']:
        raise AssertionError('The v1 profile does not normalize Unicode.')
    return actual


if __name__ == '__main__':
    print(json.dumps(vectors(), ensure_ascii=False, indent=2))
